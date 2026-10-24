using System.IO;
using OPB.Core;
using OPB.Core.Files;
using OPB.Core.Model;

namespace OPB.App;

/// <summary>
/// Names of plugs (sample packs, synth engines, effects). The OP-Z only stores plug ids in
/// patterns; names come from the device's settings/plugs.json, which the configurator pulls
/// and caches under %APPDATA%\OP-B.
/// </summary>
public static class PlugNames
{
    static readonly Dictionary<uint, string> Names = new();
    static bool _loaded;

    public static string CacheDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "OP-B");
    static string CatalogPath => Path.Combine(CacheDirectory, "plugs.json");

    public static event Action? Updated;

    public static string Get(OpzSession session, int track)
    {
        if (session.Bank is null) return "";
        uint plug = session.Bank.Plug(session.ActivePattern, track);
        var name = Get(plug);
        // Control tracks (perform, module, lights, motion) use built-in plugs without a file.
        return name == "BUILT-IN" ? Tracks.Names[track] : name;
    }

    public static string Get(uint plug)
    {
        EnsureLoaded();
        return Names.TryGetValue(plug, out var name) ? name.ToUpperInvariant() : $"PLUG {plug}";
    }

    public static IReadOnlyDictionary<uint, string> All
    {
        get { EnsureLoaded(); return Names; }
    }

    static void EnsureLoaded()
    {
        if (_loaded) return;
        _loaded = true;
        try
        {
            if (File.Exists(CatalogPath)) Parse(File.ReadAllBytes(CatalogPath));
        }
        catch { /* a corrupt cache only costs us the names */ }
    }

    /// <summary>Stores a freshly pulled plugs.json and reloads names from it.</summary>
    public static void Store(byte[] plugsJson)
    {
        Directory.CreateDirectory(CacheDirectory);
        File.WriteAllBytes(CatalogPath, plugsJson);
        Names.Clear();
        Parse(plugsJson);
        _loaded = true;
        Updated?.Invoke();
    }

    static void Parse(byte[] json)
    {
        foreach (var plug in PlugCatalog.Parse(json)) Names[plug.Id] = plug.Name;
    }
}
