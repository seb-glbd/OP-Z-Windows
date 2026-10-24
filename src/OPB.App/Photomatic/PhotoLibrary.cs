using System.IO;
using System.Text.Json;

namespace OPB.App.Photomatic;

public enum MediaKind { None, Image, Gif, Video }

/// <summary>Roll editor settings and slot assignments, stored as camerarollroll.json in each roll folder.</summary>
public sealed class RollConfig
{
    public string Name { get; set; } = "";
    public string?[] Slots { get; set; } = new string?[PhotoLibrary.SlotCount];
    // appearance
    public int Fit { get; set; }
    public int Background { get; set; }
    public int Border { get; set; }
    // movement
    public int Transition { get; set; } = 1;
    public int Drift { get; set; }
    public int WoozySpeed { get; set; } = 1;
    // video
    public int VideoSpeed { get; set; } = 1;
    public bool VideoLoop { get; set; } = true;
    public bool VideoRestart { get; set; } = true;

    public static readonly string[] FitNames = ["FIT", "FILL", "STRETCH"];
    public static readonly string[] BackgroundNames = ["BLACK", "WHITE", "GREY", "ECHO"];
    public static readonly string[] BorderNames = ["NONE", "THIN", "PRINT"];
    public static readonly string[] TransitionNames = ["CUT", "FADE", "SLIDE", "ZOOM"];
    public static readonly string[] DriftNames = ["OFF", "SLOW", "FAST"];
    public static readonly string[] WoozyNames = ["SLOW", "MEDIUM", "FAST"];
    public static readonly string[] SpeedNames = ["0.5X", "1X", "1.5X", "2X"];
    public static readonly double[] Speeds = [0.5, 1, 1.5, 2];
}

/// <summary>
/// The photomatic folder: 10 roll subfolders (roll01..roll10), each with up to 24 media files and a
/// camerarollroll.json. Imported files are copied into the roll folder.
/// </summary>
public sealed class PhotoLibrary
{
    public const int RollCount = 10, SlotCount = 24;
    const string ConfigName = "camerarollroll.json";
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    public static readonly string[] Extensions = [".png", ".jpg", ".jpeg", ".bmp", ".gif", ".mp4", ".mov", ".wmv"];

    public PhotoLibrary(string? root = null)
    {
        Root = root ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "OP-B", "photomatic");
    }

    public string Root { get; }

    public string RollDir(int roll)
    {
        var dir = Path.Combine(Root, $"roll{roll + 1:00}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    public static MediaKind KindOf(string? path) => Path.GetExtension(path ?? "").ToLowerInvariant() switch
    {
        ".gif" => MediaKind.Gif,
        ".mp4" or ".mov" or ".wmv" => MediaKind.Video,
        ".png" or ".jpg" or ".jpeg" or ".bmp" => MediaKind.Image,
        _ => MediaKind.None,
    };

    public RollConfig Load(int roll)
    {
        var dir = RollDir(roll);
        var path = Path.Combine(dir, ConfigName);
        RollConfig? config = null;
        try { if (File.Exists(path)) config = JsonSerializer.Deserialize<RollConfig>(File.ReadAllText(path), Json); }
        catch (Exception) { /* unreadable config: start fresh but keep the media */ }
        if (config is null)
        {
            // A roll folder filled by hand: assign its media files to slots in name order.
            config = new RollConfig();
            var files = Directory.EnumerateFiles(dir).Where(f => KindOf(f) != MediaKind.None).Order(StringComparer.OrdinalIgnoreCase).Take(SlotCount).ToList();
            for (int i = 0; i < files.Count; i++) config.Slots[i] = Path.GetFileName(files[i]);
        }
        if (config.Slots.Length != SlotCount) config.Slots = [.. config.Slots.Take(SlotCount), .. new string?[Math.Max(0, SlotCount - config.Slots.Length)]];
        for (int i = 0; i < SlotCount; i++)
            if (config.Slots[i] is { } f && !File.Exists(Path.Combine(dir, f))) config.Slots[i] = null;
        if (string.IsNullOrWhiteSpace(config.Name)) config.Name = $"ROLL {roll + 1}";
        return config;
    }

    public void Save(int roll, RollConfig config)
    {
        try { File.WriteAllText(Path.Combine(RollDir(roll), ConfigName), JsonSerializer.Serialize(config, Json)); }
        catch (IOException) { /* read-only or locked: settings stay in memory */ }
    }

    public string? SlotPath(int roll, RollConfig config, int slot) =>
        config.Slots[slot] is { } f ? Path.Combine(RollDir(roll), f) : null;

    /// <summary>Copies <paramref name="source"/> into the roll folder and assigns it to the slot.</summary>
    public void Import(int roll, RollConfig config, int slot, string source)
    {
        var dir = RollDir(roll);
        string name = $"{slot + 1:00}-{Path.GetFileName(source)}";
        var target = Path.Combine(dir, name);
        if (!string.Equals(Path.GetFullPath(source), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase))
        {
            RemoveFile(roll, config, slot);
            File.Copy(source, target, true);
        }
        config.Slots[slot] = name;
        Save(roll, config);
    }

    /// <summary>Unassigns a slot and deletes the copy this app made of its file.</summary>
    public void Clear(int roll, RollConfig config, int slot)
    {
        RemoveFile(roll, config, slot);
        config.Slots[slot] = null;
        Save(roll, config);
    }

    void RemoveFile(int roll, RollConfig config, int slot)
    {
        if (config.Slots[slot] is not { } f) return;
        if (config.Slots.Count(s => s == f) > 1) return;
        try { File.Delete(Path.Combine(RollDir(roll), f)); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    public string NewSnapshotPath(int roll, int slot) => Path.Combine(RollDir(roll), $"{slot + 1:00}-snap-{DateTime.Now:yyyyMMdd-HHmmss}.png");
}
