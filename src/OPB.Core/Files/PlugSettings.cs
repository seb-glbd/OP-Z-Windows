using System.Text.Json;
using System.Text.Json.Nodes;
using OPB.Core.Model;

namespace OPB.Core.Files;

/// <summary>
/// settings/slotConfiguration.json: which plug sits in each of the 10 slots of a track,
/// <c>{"tracks":[{"index":n,"packs":[{"id":plugId,"slot":1..10}]}]}</c>. Edits keep every
/// field the app doesn't understand.
/// </summary>
public sealed class SlotConfiguration
{
    public const int SlotCount = 10;
    readonly JsonObject _root;

    SlotConfiguration(JsonObject root) => _root = root;

    public static SlotConfiguration Parse(ReadOnlySpan<byte> json) =>
        new(JsonNode.Parse(json.ToArray(), documentOptions: new JsonDocumentOptions { AllowTrailingCommas = true }) as JsonObject
            ?? throw new FormatException("slotConfiguration.json is not an object"));

    public SlotConfiguration Clone() => new((JsonObject)_root.DeepClone());

    IEnumerable<JsonObject> TrackObjects() =>
        (_root["tracks"] as JsonArray)?.OfType<JsonObject>() ?? [];

    JsonObject? TrackObject(int track) =>
        TrackObjects().FirstOrDefault(t => Int(t["index"]) == track);

    public IReadOnlyList<int> Tracks => TrackObjects().Select(t => Int(t["index"])).Where(i => i is not null).Select(i => i!.Value).Distinct().Order().ToList();

    public bool HasTrack(int track) => TrackObject(track)?["packs"] is JsonArray;

    /// <summary>The plug in a slot (1..10) of a track, or null when the slot is empty.</summary>
    public uint? Plug(int track, int slot)
    {
        if (TrackObject(track)?["packs"] is not JsonArray packs) return null;
        foreach (var pack in packs.OfType<JsonObject>())
            if (Int(pack["slot"]) == slot && UInt(pack["id"]) is uint id) return id;
        return null;
    }

    /// <summary>Slot (1..10) to plug id for a track.</summary>
    public IReadOnlyDictionary<int, uint> Slots(int track)
    {
        var map = new Dictionary<int, uint>();
        if (TrackObject(track)?["packs"] is JsonArray packs)
            foreach (var pack in packs.OfType<JsonObject>())
                if (Int(pack["slot"]) is int s && UInt(pack["id"]) is uint id) map[s] = id;
        return map;
    }

    /// <summary>Every plug id used by any slot of any track.</summary>
    public IEnumerable<(int Track, int Slot, uint Plug)> All() =>
        Tracks.SelectMany(t => Slots(t).Select(kv => (t, kv.Key, kv.Value)));

    /// <summary>Puts a plug in a slot; returns the plug it replaced. Throws when the track has no slots.</summary>
    public uint? Assign(int track, int slot, uint plugId)
    {
        if (slot is < 1 or > SlotCount) throw new ArgumentOutOfRangeException(nameof(slot));
        if (TrackObject(track)?["packs"] is not JsonArray packs) throw new InvalidOperationException($"Track {track} has no plug slots");
        var prior = packs.OfType<JsonObject>().FirstOrDefault(p => Int(p["slot"]) == slot);
        uint? priorId = prior is null ? null : UInt(prior["id"]);
        if (prior is null)
        {
            packs.Add(new JsonObject { ["id"] = (long)plugId, ["slot"] = (long)slot });
            var sorted = packs.Select(p => p!.DeepClone()).OrderBy(p => Int(p["slot"]) ?? int.MaxValue).ToList();
            packs.Clear();
            foreach (var p in sorted) packs.Add(p);
        }
        else prior["id"] = (long)plugId;
        return priorId;
    }

    /// <summary>True when both documents assign the same plugs to the same slots.</summary>
    public bool SameAssignments(SlotConfiguration other) =>
        All().OrderBy(a => a).SequenceEqual(other.All().OrderBy(a => a));

    public byte[] ToBytes() => JsonSerializer.SerializeToUtf8Bytes(_root, new JsonSerializerOptions { WriteIndented = true });

    static long? Num(JsonNode? n)
    {
        if (n is not JsonValue v) return null;
        if (v.TryGetValue<JsonElement>(out var e)) return e.ValueKind == JsonValueKind.Number && e.TryGetInt64(out var x) ? x : null;
        if (v.TryGetValue<long>(out var l)) return l;
        if (v.TryGetValue<int>(out var i)) return i;
        if (v.TryGetValue<uint>(out var u)) return u;
        return null;
    }

    static int? Int(JsonNode? n) => Num(n) is long l && l is >= int.MinValue and <= int.MaxValue ? (int)l : null;
    static uint? UInt(JsonNode? n) => Num(n) is long l && l is >= 0 and <= uint.MaxValue ? (uint)l : null;
}

/// <summary>One plug from settings/plugs.json: a sample pack, synth engine or effect.</summary>
public sealed record PlugInfo(uint Id, string Name, string Hint, IReadOnlyList<int> Tracks)
{
    /// <summary>True/false when the catalog says whether the plug fits a track; null when it doesn't say.</summary>
    public bool? Fits(int track)
    {
        if (Tracks.Count > 0) return Tracks.Contains(track);
        var kinds = KindsIn(Hint);
        if (kinds.Count == 0) return null;
        return kinds.Contains(track);
    }

    static readonly (string Word, int[] Tracks)[] Words =
    [
        ("kick", [0]), ("snare", [1]), ("perc", [2]), ("bass", [4]), ("lead", [5]), ("arp", [6]), ("chord", [7]),
        ("drum", [0, 1, 2, 3]), ("synth", [4, 5, 6, 7]), ("engine", [4, 5, 6, 7]), ("effect", [8, 9]), ("fx", [8, 9]),
    ];

    static HashSet<int> KindsIn(string hint)
    {
        var set = new HashSet<int>();
        foreach (var token in hint.ToLowerInvariant().Split([' ', '/', '.', '_', '-', ':', '[', ']'], StringSplitOptions.RemoveEmptyEntries))
            foreach (var (word, tracks) in Words)
                if (token == word || token == word + "s") set.UnionWith(tracks);
        return set;
    }
}

/// <summary>Reads settings/plugs.json. Its exact shape varies by firmware, so any object with a numeric id and a name counts as a plug.</summary>
public static class PlugCatalog
{
    static readonly string[] TypeKeys = ["type", "category", "kind", "group", "class", "track", "trackType"];
    static readonly string[] TrackKeys = ["track", "trackIndex", "tracks"];

    public static IReadOnlyList<PlugInfo> Parse(ReadOnlySpan<byte> json)
    {
        using var doc = JsonDocument.Parse(json.ToArray(), new JsonDocumentOptions { AllowTrailingCommas = true });
        var plugs = new Dictionary<uint, PlugInfo>();
        Walk(doc.RootElement, "", null, plugs);
        return plugs.Values.ToList();
    }

    static void Walk(JsonElement e, string context, int? trackIndex, Dictionary<uint, PlugInfo> plugs)
    {
        if (e.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in e.EnumerateArray()) Walk(item, context, trackIndex, plugs);
            return;
        }
        if (e.ValueKind != JsonValueKind.Object) return;
        uint? id = null;
        string? name = null;
        var hints = new List<string> { context };
        var tracks = new List<int>();
        int? index = null;
        foreach (var p in e.EnumerateObject())
        {
            var v = p.Value;
            if ((p.NameEquals("id") || p.NameEquals("plugId")) && v.ValueKind == JsonValueKind.Number && v.TryGetUInt32(out var i)) id = i;
            else if (p.NameEquals("name") && v.ValueKind == JsonValueKind.String) name = v.GetString();
            else if (p.NameEquals("index") && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var ix)) index = ix;
            if (TypeKeys.Contains(p.Name) && v.ValueKind == JsonValueKind.String) hints.Add(v.GetString()!);
            if (TrackKeys.Contains(p.Name))
            {
                if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var t)) tracks.Add(t);
                else if (v.ValueKind == JsonValueKind.Array)
                    tracks.AddRange(v.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Number && x.TryGetInt32(out _)).Select(x => x.GetInt32()));
                else if (v.ValueKind == JsonValueKind.String && TrackIndex(v.GetString()!) is int named) tracks.Add(named);
            }
        }
        // A container with an "index" but no name (like {"index":4,"plugs":[...]}) scopes its children to that track.
        int? scope = name is null && index is >= 0 and < Model.Tracks.Count ? index : trackIndex;
        foreach (var p in e.EnumerateObject())
            if (p.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                Walk(p.Value, $"{context} {p.Name}".Trim(), scope, plugs);
        if (id is not null && string.IsNullOrWhiteSpace(name) && PlugNaming.Derive(e, id.Value) is { } derived)
        {
            name = derived.Name;
            hints.Add(derived.Hint);
            tracks.AddRange(derived.Tracks);
        }
        if (id is null || string.IsNullOrWhiteSpace(name)) return;
        if (tracks.Count == 0 && trackIndex is int scoped) tracks.Add(scoped);
        tracks = tracks.Where(t => t is >= 0 and < Model.Tracks.Count).Distinct().ToList();
        if (plugs.TryGetValue(id.Value, out var existing))
            tracks = existing.Tracks.Union(tracks).ToList();
        plugs[id.Value] = new PlugInfo(id.Value, name!, string.Join(' ', hints.Where(h => h.Length > 0)), tracks);
    }

    static int? TrackIndex(string name)
    {
        int i = Array.FindIndex(Model.Tracks.Names, n => string.Equals(n.Replace(" ", ""), name.Replace(" ", ""), StringComparison.OrdinalIgnoreCase));
        return i >= 0 ? i : null;
    }
}
