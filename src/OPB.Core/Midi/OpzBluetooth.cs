using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Enumeration;
using Windows.Devices.Radios;

namespace OPB.Core.Midi;

public enum BluetoothState { Checking, Unsupported, Off, Ready }

/// <summary>A BLE-MIDI device seen advertising nearby, or an OP-Z paired in Windows (<see cref="Paired"/>).</summary>
public sealed record BleDevice(ulong Address, string Name, short Rssi, DateTime LastSeen, bool Paired = false)
{
    public bool InRange => LastSeen != DateTime.MinValue;
    public bool IsOpz => Name.Contains("OP-Z", StringComparison.OrdinalIgnoreCase);
    public string AddressText => FormatAddress(Address);

    public static string FormatAddress(ulong address) =>
        string.Join(":", Enumerable.Range(0, 6).Select(i => ((address >> (40 - 8 * i)) & 0xFF).ToString("X2")));
}

/// <summary>
/// Finds OP-Zs (and other BLE-MIDI devices) advertising nearby and keeps a Bluetooth link to the
/// chosen one ready for <see cref="OpzSession"/>. Connecting is slow, so it happens in the
/// background: <see cref="TakeConnection"/> never blocks; it starts an attempt and returns the
/// connection on a later call once it's up. Events are raised on background threads.
/// </summary>
public sealed class OpzBluetooth : IDisposable
{
    static readonly TimeSpan Stale = TimeSpan.FromSeconds(12);
    static readonly TimeSpan WantedFor = TimeSpan.FromSeconds(5);
    static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(4);
    static readonly TimeSpan BlindRetryDelay = TimeSpan.FromSeconds(20);

    /// <summary>
    /// The OP-Z advertises but refuses Windows' encrypted reconnect: it forgot the pairing (it was
    /// paired with another device since), and only removing the device in Windows clears it.
    /// </summary>
    public const string StalePairing = "Windows' pairing with this OP-Z is out of date. Remove the OP-Z in Windows Settings > Bluetooth & devices, then tap it again";

    readonly BluetoothLEAdvertisementWatcher _watcher = new() { ScanningMode = BluetoothLEScanningMode.Active };
    readonly Dictionary<ulong, BleDevice> _seen = new();
    readonly Dictionary<ulong, string> _paired = new();
    readonly HashSet<ulong> _midi = new();
    readonly object _lock = new();
    readonly Timer _housekeeping;
    Radio? _radio;
    bool _scanRequested, _watching, _disposed;
    DateTime _pairedAt = DateTime.MinValue;
    DateTime _wantedAt = DateTime.MinValue, _nextAttempt = DateTime.MinValue, _lastBlindAttempt = DateTime.MinValue;
    Task? _connecting;
    BleMidiConnection? _ready;
    ulong? _target;

    public OpzBluetooth()
    {
        _watcher.Received += OnReceived;
        _watcher.Stopped += OnStopped;
        _housekeeping = new Timer(_ => Housekeeping(), null, 0, 2000);
        _ = CheckAdapterAsync();
    }

    public BluetoothState State { get; private set; } = BluetoothState.Checking;

    /// <summary>What the link is doing right now ("CONNECTING", "PAIRING"), or null.</summary>
    public string? Activity { get; private set; }

    /// <summary>Why the last connection attempt failed, until one succeeds.</summary>
    public string? LastError { get; private set; }

    public bool IsScanning => _scanRequested && State == BluetoothState.Ready;

    /// <summary>Raised when devices, state or activity change (background thread).</summary>
    public event Action? Changed;
    public event Action<string>? Log;

    /// <summary>The OP-Z to connect to over Bluetooth when no USB port is found; null disables Bluetooth.</summary>
    public ulong? Target
    {
        get => _target;
        set
        {
            BleMidiConnection? stale;
            lock (_lock)
            {
                if (_target == value) return;
                _target = value;
                stale = _ready;
                _ready = null;
                _nextAttempt = DateTime.MinValue;
                _lastBlindAttempt = DateTime.MinValue;
                LastError = null;
            }
            stale?.Dispose();
            Changed?.Invoke();
        }
    }

    /// <summary>
    /// Devices seen in the last few seconds, plus OP-Zs paired in Windows (a paired OP-Z may not
    /// advertise); OP-Zs first, then by signal strength.
    /// </summary>
    public IReadOnlyList<BleDevice> Devices
    {
        get
        {
            lock (_lock)
            {
                var seen = _seen.Values.Where(d => _midi.Contains(d.Address) || d.IsOpz)
                    .Select(d => _paired.ContainsKey(d.Address) ? d with { Paired = true } : d);
                var paired = _paired.Where(p => !_seen.ContainsKey(p.Key))
                    .Select(p => new BleDevice(p.Key, p.Value, 0, DateTime.MinValue, Paired: true));
                return seen.Concat(paired).OrderByDescending(d => d.IsOpz).ThenByDescending(d => d.InRange).ThenByDescending(d => d.Rssi).ToList();
            }
        }
    }

    /// <summary>Scans actively for devices to show (the Devices screen); the link scans by itself.</summary>
    public void SetScanning(bool on)
    {
        lock (_lock) _scanRequested = on;
        UpdateWatcher();
        if (on) _ = RefreshPairedAsync();
        Changed?.Invoke();
    }

    /// <summary>Reads the OP-Zs Windows has paired (Settings or an earlier connection).</summary>
    async Task RefreshPairedAsync()
    {
        lock (_lock)
        {
            if (DateTime.UtcNow - _pairedAt < TimeSpan.FromSeconds(5)) return;
            _pairedAt = DateTime.UtcNow;
        }
        try
        {
            var found = new Dictionary<ulong, string>();
            foreach (var info in await DeviceInformation.FindAllAsync(BluetoothLEDevice.GetDeviceSelectorFromPairingState(true)))
            {
                if (!info.Name.Contains("OP-Z", StringComparison.OrdinalIgnoreCase)) continue;
                using var device = await BluetoothLEDevice.FromIdAsync(info.Id);
                if (device is not null) found[device.BluetoothAddress] = info.Name;
            }
            bool changed;
            lock (_lock)
            {
                changed = !found.OrderBy(p => p.Key).SequenceEqual(_paired.OrderBy(p => p.Key));
                _paired.Clear();
                foreach (var p in found) _paired[p.Key] = p.Value;
            }
            if (changed) Changed?.Invoke();
        }
        catch (Exception ex)
        {
            Log?.Invoke($"Could not list paired Bluetooth devices: {ex.Message}");
        }
    }

    /// <summary>
    /// For the session's connector: returns a ready connection to <see cref="Target"/>, or null
    /// and starts connecting in the background.
    /// </summary>
    public IMidiConnection? TakeConnection()
    {
        lock (_lock)
        {
            _wantedAt = DateTime.UtcNow;
            if (_ready is { } ready)
            {
                _ready = null;
                if (ready.Fault is null) return ready;
                ready.Dispose();
            }
        }
        UpdateWatcher();
        TryStartConnecting();
        return null;
    }

    bool Wanted => DateTime.UtcNow - _wantedAt < WantedFor;

    void TryStartConnecting()
    {
        ulong target;
        lock (_lock)
        {
            if (_disposed || _target is null || _connecting is not null || _ready is not null) return;
            if (State != BluetoothState.Ready || DateTime.UtcNow < _nextAttempt) return;
            target = _target.Value;
            // Connect once it advertises; now and then try anyway (a bonded OP-Z may not show up in scans).
            bool seen = _seen.TryGetValue(target, out var d) && DateTime.UtcNow - d.LastSeen < Stale;
            if (!seen && DateTime.UtcNow - _lastBlindAttempt < BlindRetryDelay) return;
            if (!seen) _lastBlindAttempt = DateTime.UtcNow;
            _connecting = Task.Run(() => ConnectAsync(target));
        }
    }

    async Task ConnectAsync(ulong target)
    {
        try
        {
            SetActivity("CONNECTING");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
            var connection = await BleMidiConnection.ConnectAsync(target, SetActivity, timeout.Token).ConfigureAwait(false);
            bool keep;
            lock (_lock)
            {
                keep = !_disposed && _target == target;
                if (keep) { _ready = connection; LastError = null; }
            }
            if (!keep) connection.Dispose();
            else Log?.Invoke($"Bluetooth link to {connection.Name} is up");
        }
        catch (Exception ex)
        {
            string message;
            lock (_lock)
            {
                bool advertising = _seen.TryGetValue(target, out var d) && DateTime.UtcNow - d.LastSeen < Stale;
                message = ex switch
                {
                    OperationCanceledException => "The OP-Z did not answer over Bluetooth",
                    BleUnreachableException { Paired: true } when advertising => StalePairing,
                    _ => ex.Message,
                };
                LastError = message;
                _nextAttempt = DateTime.UtcNow + RetryDelay;
            }
            Log?.Invoke($"Bluetooth: {message}");
        }
        finally
        {
            lock (_lock) _connecting = null;
            SetActivity(null);
        }
    }

    void SetActivity(string? activity)
    {
        Activity = activity;
        Changed?.Invoke();
    }

    // ---------------------------------------------------------------- scanning

    async Task CheckAdapterAsync()
    {
        try
        {
            var adapter = await BluetoothAdapter.GetDefaultAsync();
            if (adapter is null || !adapter.IsLowEnergySupported) { SetState(BluetoothState.Unsupported); return; }
            _radio = await adapter.GetRadioAsync();
            if (_radio is null) { SetState(BluetoothState.Unsupported); return; }
            _radio.StateChanged += (r, _) => SetState(r.State == RadioState.On ? BluetoothState.Ready : BluetoothState.Off);
            SetState(_radio.State == RadioState.On ? BluetoothState.Ready : BluetoothState.Off);
        }
        catch (Exception ex)
        {
            Log?.Invoke($"Bluetooth unavailable: {ex.Message}");
            SetState(BluetoothState.Unsupported);
        }
    }

    void SetState(BluetoothState state)
    {
        if (State == state) return;
        State = state;
        if (state != BluetoothState.Ready) lock (_lock) _seen.Clear();
        UpdateWatcher();
        Changed?.Invoke();
    }

    void UpdateWatcher()
    {
        bool run;
        lock (_lock)
        {
            run = !_disposed && State == BluetoothState.Ready && (_scanRequested || (_target is not null && Wanted));
            if (run == _watching) return;
            _watching = run;
        }
        try
        {
            if (run) _watcher.Start();
            else _watcher.Stop();
        }
        catch (Exception ex)
        {
            lock (_lock) _watching = false;
            Log?.Invoke($"Bluetooth scan failed: {ex.Message}");
        }
    }

    void OnReceived(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementReceivedEventArgs args)
    {
        bool midi = args.Advertisement.ServiceUuids.Contains(BleMidiCodec.ServiceUuid);
        string name = args.Advertisement.LocalName ?? "";
        bool added;
        lock (_lock)
        {
            bool known = _seen.TryGetValue(args.BluetoothAddress, out var previous);
            // The name often comes in a separate scan response without the service UUID.
            if (!known && !midi && !name.Contains("OP-Z", StringComparison.OrdinalIgnoreCase)) return;
            if (midi) _midi.Add(args.BluetoothAddress);
            if (name.Length == 0) name = previous?.Name ?? "";
            _seen[args.BluetoothAddress] = new BleDevice(args.BluetoothAddress, name, args.RawSignalStrengthInDBm, DateTime.UtcNow);
            added = !known || previous!.Name != name;
        }
        if (added) Changed?.Invoke();
        if (args.BluetoothAddress == _target) TryStartConnecting();
    }

    void OnStopped(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementWatcherStoppedEventArgs args)
    {
        lock (_lock) _watching = false;
        if (args.Error is BluetoothError.RadioNotAvailable or BluetoothError.DisabledByUser) SetState(BluetoothState.Off);
        else if (args.Error == BluetoothError.NotSupported) SetState(BluetoothState.Unsupported);
        else if (args.Error != BluetoothError.Success) Log?.Invoke($"Bluetooth scan stopped: {args.Error}");
    }

    void Housekeeping()
    {
        BleMidiConnection? unused = null;
        bool pruned;
        lock (_lock)
        {
            var now = DateTime.UtcNow;
            var old = _seen.Values.Where(d => now - d.LastSeen > Stale).Select(d => d.Address).ToList();
            foreach (var address in old) _seen.Remove(address);
            pruned = old.Count > 0;
            // Nobody picked the link up (a USB cable came in): let the OP-Z go.
            if (_ready is not null && !Wanted) { unused = _ready; _ready = null; }
        }
        unused?.Dispose();
        UpdateWatcher();
        if (Wanted) TryStartConnecting();
        if (IsScanning) _ = RefreshPairedAsync();
        if (pruned || IsScanning) Changed?.Invoke();
    }

    public void Dispose()
    {
        BleMidiConnection? ready;
        lock (_lock)
        {
            _disposed = true;
            ready = _ready;
            _ready = null;
        }
        _housekeeping.Dispose();
        try { _watcher.Stop(); } catch { /* radio gone */ }
        ready?.Dispose();
    }
}
