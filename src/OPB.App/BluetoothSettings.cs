using System.Globalization;
using System.IO;

namespace OPB.App;

/// <summary>The OP-Z chosen for Bluetooth, remembered in %APPDATA%\OP-B so OP-B reconnects to it next time.</summary>
public static class BluetoothSettings
{
    static string FilePath => Path.Combine(PlugNames.CacheDirectory, "bluetooth.txt");

    /// <summary>Address and name of the saved OP-Z, or null.</summary>
    public static (ulong Address, string Name)? Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return null;
            var lines = File.ReadAllLines(FilePath);
            if (lines.Length == 0 || !ulong.TryParse(lines[0].Trim(), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var address)) return null;
            return (address, lines.Length > 1 ? lines[1].Trim() : "OP-Z");
        }
        catch { return null; }
    }

    public static void Save(ulong? address, string? name)
    {
        try
        {
            if (address is null) File.Delete(FilePath);
            else
            {
                Directory.CreateDirectory(PlugNames.CacheDirectory);
                File.WriteAllLines(FilePath, [address.Value.ToString("X12", CultureInfo.InvariantCulture), name ?? "OP-Z"]);
            }
        }
        catch { /* not remembering only costs a tap next time */ }
    }
}
