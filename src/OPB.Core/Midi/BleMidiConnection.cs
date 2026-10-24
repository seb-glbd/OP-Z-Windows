using System.Collections.Concurrent;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;

namespace OPB.Core.Midi;

/// <summary>
/// MIDI over Bluetooth LE, straight to the device's BLE-MIDI GATT characteristic (no Windows
/// MIDI driver involved). This is how the official mobile apps reach the OP-Z without a cable.
/// Writes go out in order on a sender thread; a lost link makes the next send throw, so
/// <see cref="OpzSession"/> drops the connection and asks for a new one.
/// </summary>
public sealed class BleMidiConnection : IMidiConnection
{
    readonly BluetoothLEDevice _device;
    readonly GattSession _gatt;
    readonly GattDeviceService _service;
    readonly GattCharacteristic _io;
    readonly GattWriteOption _writeOption;
    readonly BleMidiDecoder _decoder = new();
    readonly BlockingCollection<byte[]> _received = new();
    readonly BlockingCollection<byte[]> _outgoing = new();
    readonly Thread _receiver;
    readonly Thread _sender;
    readonly object _sendLock = new();
    volatile bool _closing;
    volatile string? _fault;

    public string Name { get; }
    public ulong Address { get; }
    public event Action<byte[]>? SysexReceived;
    public event Action<byte, byte, byte>? ShortReceived;

    BleMidiConnection(BluetoothLEDevice device, GattSession gatt, GattDeviceService service, GattCharacteristic io)
    {
        _device = device;
        _gatt = gatt;
        _service = service;
        _io = io;
        Address = device.BluetoothAddress;
        Name = $"{(string.IsNullOrWhiteSpace(device.Name) ? "OP-Z" : device.Name)} (Bluetooth)";
        _writeOption = io.CharacteristicProperties.HasFlag(GattCharacteristicProperties.WriteWithoutResponse)
            ? GattWriteOption.WriteWithoutResponse : GattWriteOption.WriteWithResponse;
        _decoder.SysexReceived += m => SysexReceived?.Invoke(m);
        _decoder.ShortReceived += (s, a, b) => ShortReceived?.Invoke(s, a, b);
        _receiver = new Thread(ReceiveLoop) { IsBackground = true, Name = "BLE MIDI receive" };
        _sender = new Thread(SendLoop) { IsBackground = true, Name = "BLE MIDI send" };
        _receiver.Start();
        _sender.Start();
        _io.ValueChanged += OnValueChanged;
        _device.ConnectionStatusChanged += OnConnectionStatusChanged;
    }

    /// <summary>The link was lost or a write failed; the connection must be replaced.</summary>
    public string? Fault => _fault;

    /// <summary>Largest BLE-MIDI packet the link carries (negotiated ATT MTU minus 3).</summary>
    int MaxPacket => Math.Clamp(_gatt.MaxPduSize - 3, 20, 512);

    static int Timestamp => (int)(Environment.TickCount64 & 0x1FFF);

    /// <summary>
    /// Connects to a BLE-MIDI device, pairing first only if it refuses an unencrypted link,
    /// and subscribes to its MIDI characteristic.
    /// </summary>
    public static async Task<BleMidiConnection> ConnectAsync(ulong address, Action<string>? progress = null, CancellationToken cancel = default)
    {
        var device = await BluetoothLEDevice.FromBluetoothAddressAsync(address).AsTask(cancel).ConfigureAwait(false)
                     ?? throw new IOException("Bluetooth device not found");
        GattSession? gatt = null;
        GattDeviceService? service = null;
        try
        {
            gatt = await GattSession.FromDeviceIdAsync(device.BluetoothDeviceId).AsTask(cancel).ConfigureAwait(false);
            gatt.MaintainConnection = true;
            progress?.Invoke("CONNECTING");
            (service, var io) = await OpenMidiAsync(device, cancel).ConfigureAwait(false);
            var result = await Subscribe(io, cancel).ConfigureAwait(false);
            if (NeedsPairing(result))
            {
                progress?.Invoke("PAIRING");
                if (!await PairAsync(device).ConfigureAwait(false))
                    throw new IOException("The OP-Z refused to pair. Press the button on its back and try again");
                service.Dispose();
                (service, io) = await OpenMidiAsync(device, cancel).ConfigureAwait(false);
                result = await Subscribe(io, cancel).ConfigureAwait(false);
            }
            if (result.Status != GattCommunicationStatus.Success)
                throw new IOException($"Could not subscribe to Bluetooth MIDI ({result.Status})");
            // BLE-MIDI centrals read the characteristic once after connecting; the answer is empty.
            try { await io.ReadValueAsync(BluetoothCacheMode.Uncached).AsTask(cancel).ConfigureAwait(false); }
            catch (Exception) when (!cancel.IsCancellationRequested) { /* optional */ }
            return new BleMidiConnection(device, gatt, service, io);
        }
        catch
        {
            service?.Dispose();
            gatt?.Dispose();
            device.Dispose();
            throw;
        }
    }

    static async Task<(GattDeviceService, GattCharacteristic)> OpenMidiAsync(BluetoothLEDevice device, CancellationToken cancel)
    {
        var services = await device.GetGattServicesForUuidAsync(BleMidiCodec.ServiceUuid, BluetoothCacheMode.Uncached).AsTask(cancel).ConfigureAwait(false);
        if (services.Status == GattCommunicationStatus.Unreachable)
            throw new BleUnreachableException(device.DeviceInformation.Pairing.IsPaired);
        if (services.Status != GattCommunicationStatus.Success)
            throw new IOException($"Bluetooth MIDI service unavailable ({services.Status})");
        var service = services.Services.FirstOrDefault() ?? throw new IOException("This device has no Bluetooth MIDI service");
        foreach (var extra in services.Services.Skip(1)) extra.Dispose();
        try
        {
            var characteristics = await service.GetCharacteristicsForUuidAsync(BleMidiCodec.CharacteristicUuid, BluetoothCacheMode.Uncached).AsTask(cancel).ConfigureAwait(false);
            if (characteristics.Status != GattCommunicationStatus.Success || characteristics.Characteristics.Count == 0)
                throw new IOException($"Bluetooth MIDI characteristic unavailable ({characteristics.Status})");
            return (service, characteristics.Characteristics[0]);
        }
        catch
        {
            service.Dispose();
            throw;
        }
    }

    static Task<GattWriteResult> Subscribe(GattCharacteristic io, CancellationToken cancel) =>
        io.WriteClientCharacteristicConfigurationDescriptorWithResultAsync(GattClientCharacteristicConfigurationDescriptorValue.Notify).AsTask(cancel);

    /// <summary>The device wants an encrypted (bonded) link before it accepts the subscription.</summary>
    static bool NeedsPairing(GattWriteResult result) =>
        result.Status == GattCommunicationStatus.AccessDenied
        || (result.Status == GattCommunicationStatus.ProtocolError
            && result.ProtocolError is { } error
            && (error == GattProtocolError.InsufficientAuthentication || error == GattProtocolError.InsufficientEncryption
                || error == GattProtocolError.InsufficientEncryptionKeySize));

    static async Task<bool> PairAsync(BluetoothLEDevice device)
    {
        var pairing = device.DeviceInformation.Pairing;
        if (pairing.IsPaired) return true;
        if (!pairing.CanPair) return false;
        // The OP-Z has no display or keypad: "just works" pairing, confirmed on our side.
        void Accept(DeviceInformationCustomPairing sender, DevicePairingRequestedEventArgs args) => args.Accept();
        pairing.Custom.PairingRequested += Accept;
        try
        {
            var result = await pairing.Custom.PairAsync(DevicePairingKinds.ConfirmOnly | DevicePairingKinds.DisplayPin, DevicePairingProtectionLevel.Encryption).AsTask().ConfigureAwait(false);
            return result.Status is DevicePairingResultStatus.Paired or DevicePairingResultStatus.AlreadyPaired;
        }
        finally
        {
            pairing.Custom.PairingRequested -= Accept;
        }
    }

    // ---------------------------------------------------------------- receive

    void OnValueChanged(GattCharacteristic sender, GattValueChangedEventArgs args)
    {
        // Runs on a WinRT thread: copy the packet out and hand off.
        var packet = args.CharacteristicValue.ToArray();
        if (!_closing) _received.TryAdd(packet);
    }

    void OnConnectionStatusChanged(BluetoothLEDevice sender, object args)
    {
        if (sender.ConnectionStatus == BluetoothConnectionStatus.Disconnected && !_closing)
            _fault ??= "Bluetooth link lost";
    }

    void ReceiveLoop()
    {
        foreach (var packet in _received.GetConsumingEnumerable())
        {
            try { _decoder.Feed(packet); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"BLE MIDI handler error: {ex}"); }
        }
    }

    // ---------------------------------------------------------------- send

    void ThrowIfFaulted()
    {
        if (_closing) throw new ObjectDisposedException(Name);
        if (_fault is { } fault) throw new IOException(fault);
    }

    public void SendShort(byte status, byte data1 = 0, byte data2 = 0)
    {
        ThrowIfFaulted();
        lock (_sendLock) _outgoing.TryAdd(BleMidiCodec.EncodeShort(status, data1, data2, Timestamp));
    }

    public void SendSysex(byte[] message)
    {
        ThrowIfFaulted();
        var packets = BleMidiCodec.EncodeSysex(message, MaxPacket, Timestamp);
        // One message's packets must not interleave with another's.
        lock (_sendLock)
            foreach (var packet in packets) _outgoing.TryAdd(packet);
    }

    void SendLoop()
    {
        foreach (var packet in _outgoing.GetConsumingEnumerable())
        {
            if (_fault is not null) continue;
            try
            {
                var result = _io.WriteValueWithResultAsync(packet.AsBuffer(), _writeOption).AsTask().GetAwaiter().GetResult();
                if (result.Status == GattCommunicationStatus.Unreachable) _fault ??= "The OP-Z is out of reach";
                else if (result.Status != GattCommunicationStatus.Success)
                    System.Diagnostics.Debug.WriteLine($"BLE MIDI write failed: {result.Status} {result.ProtocolError}");
            }
            catch (Exception ex) when (!_closing)
            {
                _fault ??= $"Bluetooth write failed: {ex.Message}";
            }
            catch (Exception) { /* closing */ }
        }
    }

    public void Dispose()
    {
        if (_closing) return;
        _closing = true;
        _io.ValueChanged -= OnValueChanged;
        _device.ConnectionStatusChanged -= OnConnectionStatusChanged;
        _outgoing.CompleteAdding();
        _received.CompleteAdding();
        _sender.Join(1000);
        _receiver.Join(1000);
        try
        {
            if (_device.ConnectionStatus == BluetoothConnectionStatus.Connected)
                _io.WriteClientCharacteristicConfigurationDescriptorAsync(GattClientCharacteristicConfigurationDescriptorValue.None)
                    .AsTask().Wait(500);
        }
        catch { /* link already gone */ }
        // Releasing every handle lets Windows close the link, so the OP-Z can advertise again.
        _service.Dispose();
        _gatt.MaintainConnection = false;
        _gatt.Dispose();
        _device.Dispose();
    }
}

/// <summary>
/// Windows could not open a link to the device. With <see cref="Paired"/> set and the device
/// advertising, that usually means the device forgot the bond Windows still holds.
/// </summary>
public sealed class BleUnreachableException(bool paired) : IOException("The OP-Z is out of reach or turned off")
{
    public bool Paired { get; } = paired;
}
