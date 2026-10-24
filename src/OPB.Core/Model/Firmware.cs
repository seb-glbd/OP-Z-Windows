namespace OPB.Core.Model;

/// <summary>OP-Z firmware versions as reported by $11 ("1.2.17+") and the features they gate.</summary>
public static class Firmware
{
    /// <summary>The first firmware known to answer the file service ($51 then $34): configurator and file transfer.</summary>
    public static readonly Version FileService = new(1, 2, 45);

    public const string DownloadUrl = "teenage.engineering/downloads/op-z";

    public static Version? Parse(string? firmware)
    {
        if (string.IsNullOrWhiteSpace(firmware)) return null;
        var digits = new string(firmware.Trim().TakeWhile(c => char.IsDigit(c) || c == '.').ToArray()).Trim('.');
        var parts = digits.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length is 0 or > 4) return null;
        var n = parts.Select(p => int.TryParse(p, out var v) ? v : -1).ToArray();
        if (n.Any(v => v < 0)) return null;
        return n.Length switch { 1 => new Version(n[0], 0), 2 => new Version(n[0], n[1]), 3 => new Version(n[0], n[1], n[2]), _ => new Version(n[0], n[1], n[2], n[3]) };
    }

    /// <summary>True/false when the version is known, null when it isn't.</summary>
    public static bool? SupportsFileService(string? firmware) => Parse(firmware) is { } v ? v >= FileService : null;
}
