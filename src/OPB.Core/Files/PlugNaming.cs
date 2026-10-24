using System.Text;
using System.Text.Json;

namespace OPB.Core.Files;

/// <summary>
/// Firmware plugs.json entries carry no display name, only an engine path and an optional sample
/// path: <c>{"engine":"engines/sampleplayergain.engine","id":140,"samples":"samplepacks/SebaKicks.aif"}</c>.
/// Sample plugs are named after the sample pack ("SEBA KICKS", as the official app shows them);
/// engine plugs after the engine; plugs without an engine are the control tracks' built-ins.
/// </summary>
public static class PlugNaming
{
    // Declared before Engines: static initializers run in order.
    static readonly int[] Drums = [0, 1, 2, 3];
    static readonly int[] Synth = [4, 5, 6, 7];
    static readonly int[] Fx = [8, 9];

    static readonly Dictionary<string, (string Name, int[] Tracks)> Engines = new(StringComparer.OrdinalIgnoreCase)
    {
        ["analoga"] = ("ANALOG", Synth), ["cluster"] = ("CLUSTER", Synth), ["organ"] = ("ORGAN", Synth),
        ["ep"] = ("EP", Synth), ["digital"] = ("DIGITAL", Synth), ["saw013a"] = ("SAW", Synth),
        ["chord"] = ("CHORD", Synth), ["volt"] = ("VOLT", Synth), ["fmedit"] = ("FM", Synth),
        ["fm003b"] = ("FM", Synth), ["electric"] = ("ELECTRIC", Synth), ["ministringshort"] = ("STRING", Synth),
        ["synthsampler"] = ("SAMPLER", Synth),
        ["delay"] = ("DELAY", Fx), ["reverb"] = ("REVERB", Fx), ["fdnreverb"] = ("RYMD", Fx),
        ["chorus2"] = ("CHORUS-80", Fx), ["dist"] = ("DIST", Fx), ["crush"] = ("CRUSH", Fx),
        ["tapetwo"] = ("TAPE", [10]), ["chorus"] = ("MASTER", [11]),
        ["sampleplayergain"] = ("SAMPLE", Drums),
    };

    public static (string Name, string Hint, int[] Tracks)? Derive(JsonElement plug, uint id)
    {
        string engine = Str(plug, "engine"), samples = Str(plug, "samples");
        if (engine.Length == 0 && samples.Length == 0)
        {
            if (!plug.TryGetProperty("engine", out _) && !plug.TryGetProperty("samples", out _)) return null;
            return ("BUILT-IN", "control", []);
        }
        string stem = Stem(engine);
        Engines.TryGetValue(stem, out var known);
        if (samples.Length > 0)
        {
            // A sample on the synth sampler is a melodic sample; on the sample player it's a drum kit.
            int[] tracks = stem.Equals("synthsampler", StringComparison.OrdinalIgnoreCase) ? Synth : Drums;
            return (Words(Stem(samples)), tracks == Synth ? "synth sample" : "drum kit", tracks);
        }
        return (known.Name ?? Words(stem), known.Tracks == Fx ? "effect" : known.Tracks == Synth ? "synth engine" : "engine", known.Tracks ?? []);
    }

    static string Str(JsonElement e, string key) =>
        e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    static string Stem(string path)
    {
        var file = path.Replace('\\', '/');
        file = file[(file.LastIndexOf('/') + 1)..];
        int dot = file.LastIndexOf('.');
        return dot > 0 ? file[..dot] : file;
    }

    /// <summary>"SebaKicks" → "SEBA KICKS", "CuckooC_keypawn" → "CUCKOO C KEYPAWN".</summary>
    public static string Words(string stem)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < stem.Length; i++)
        {
            char c = stem[i];
            if (c is '_' or '-' or ' ') { if (sb.Length > 0 && sb[^1] != ' ') sb.Append(' '); continue; }
            if (i > 0 && char.IsUpper(c) && (char.IsLower(stem[i - 1]) || char.IsDigit(stem[i - 1])) && sb.Length > 0 && sb[^1] != ' ')
                sb.Append(' ');
            sb.Append(char.ToUpperInvariant(c));
        }
        return sb.ToString().Trim();
    }
}
