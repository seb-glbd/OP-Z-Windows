using OPB.Core.Midi;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;

/// <summary>
/// Bluetooth diagnostics that connect to nothing: the LE devices Windows has paired (with their
/// link state), then every advertisement heard for a few seconds.
/// </summary>
static class BleScan
{
    /// <summary>One GATT connection attempt with timings and pairing state, without subscribing or writing.</summary>
    public static async Task ConnectTestAsync(ulong address)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        using var device = await BluetoothLEDevice.FromBluetoothAddressAsync(address);
        if (device is null) { Console.WriteLine("Device not found"); return; }
        var pairing = device.DeviceInformation.Pairing;
        Console.WriteLine($"{device.Name} paired={pairing.IsPaired} canPair={pairing.CanPair} protection={pairing.ProtectionLevel} status={device.ConnectionStatus}");
        device.ConnectionStatusChanged += (d, _) => Console.WriteLine($"  {sw.ElapsedMilliseconds,6} ms link {d.ConnectionStatus}");
        using var session = await GattSession.FromDeviceIdAsync(device.BluetoothDeviceId);
        session.MaintainConnection = true;
        foreach (var mode in new[] { BluetoothCacheMode.Uncached, BluetoothCacheMode.Uncached })
        {
            sw.Restart();
            var services = await device.GetGattServicesAsync(mode);
            Console.WriteLine($"  {sw.ElapsedMilliseconds,6} ms services {services.Status} count={services.Services.Count} link={device.ConnectionStatus} pdu={session.MaxPduSize}");
            foreach (var s in services.Services) { Console.WriteLine($"           {s.Uuid}"); s.Dispose(); }
            if (services.Status == GattCommunicationStatus.Success) break;
        }
    }

    public static async Task RunAsync(int seconds)
    {
        Console.WriteLine("Paired Bluetooth LE devices:");
        var paired = await DeviceInformation.FindAllAsync(BluetoothLEDevice.GetDeviceSelectorFromPairingState(true));
        foreach (var info in paired)
        {
            using var device = await BluetoothLEDevice.FromIdAsync(info.Id);
            if (device is null) { Console.WriteLine($"  {info.Name}: not available"); continue; }
            var services = await device.GetGattServicesForUuidAsync(BleMidiCodec.ServiceUuid, BluetoothCacheMode.Cached);
            bool midi = services.Status == GattCommunicationStatus.Success && services.Services.Count > 0;
            foreach (var s in services.Services) s.Dispose();
            Console.WriteLine($"  {info.Name,-24} {BleDevice.FormatAddress(device.BluetoothAddress)}  {device.ConnectionStatus,-12} midi(cached)={midi}");
        }

        Console.WriteLine($"Advertisements for {seconds} s:");
        var watcher = new BluetoothLEAdvertisementWatcher { ScanningMode = BluetoothLEScanningMode.Active };
        var seen = new HashSet<(ulong, string)>();
        watcher.Received += (_, a) =>
        {
            string name = a.Advertisement.LocalName ?? "";
            bool midi = a.Advertisement.ServiceUuids.Contains(BleMidiCodec.ServiceUuid);
            lock (seen)
            {
                if (!seen.Add((a.BluetoothAddress, name + midi))) return;
                Console.WriteLine($"  {BleDevice.FormatAddress(a.BluetoothAddress)} {a.RawSignalStrengthInDBm,4} dBm {a.AdvertisementType,-22} midi={midi,-5} {name}");
            }
        };
        watcher.Stopped += (_, a) => { if (a.Error != BluetoothError.Success) Console.WriteLine($"  scan stopped: {a.Error}"); };
        watcher.Start();
        await Task.Delay(TimeSpan.FromSeconds(seconds));
        watcher.Stop();
        Console.WriteLine($"  {seen.Count} distinct advertisements");
    }
}
