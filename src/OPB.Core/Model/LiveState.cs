using System.Buffers.Binary;
using System.Text;

namespace OPB.Core.Model;

/// <summary>$03 PlayState: octave, selected track, and playing flag.</summary>
public readonly record struct PlayState(sbyte Octave, int Track, bool Playing)
{
    public static PlayState Decode(byte[] p) =>
        p.Length < 2 ? default : new((sbyte)p[0], p[1] & 0x0F, (p[1] >> 4) != 0);

    public byte[] Encode() => [(byte)Octave, (byte)((Track & 0x0F) | (Playing ? 0x10 : 0))];
}

/// <summary>$04 input/level state: battery, master volume, microphone settings and input routing.</summary>
public readonly record struct LevelState(int Battery, byte MasterVolume, int MicLevel, int MicFx, bool Monitor, int InputSelector, bool MicMode)
{
    public static LevelState Decode(byte[] p)
    {
        if (p.Length < 4) return default;
        return new(p[0], p[1], (p[2] & 0x0F) + 1, (p[2] >> 4) & 0x03, (p[2] & 0x40) != 0, p[3] & 0x0F, (p[3] & 0x10) != 0);
    }
}

/// <summary>$06 UI state: parameter page, held buttons. Held key index is 0..15 or -1.</summary>
public readonly record struct UiState(int Page, int HeldKey, bool Shift, bool Tempo, bool Mixer, bool Screen, bool Stop, bool Record, bool TrackHeld, bool Project, byte[] Raw)
{
    public static UiState Decode(byte[] p)
    {
        if (p.Length < 4) return new(0, -1, false, false, false, false, false, false, false, false, p);
        int step = p[1] & 0x1F;
        return new(
            Page: (p[0] >> 6) & 0x03,
            HeldKey: step < 16 ? step : -1,
            Shift: (p[1] & 0x20) != 0,
            Tempo: (p[1] & 0x40) != 0,
            Mixer: (p[1] & 0x80) != 0,
            Screen: (p[2] & 0x20) != 0,
            Stop: (p[2] & 0x40) != 0,
            Record: (p[2] & 0x80) != 0,
            TrackHeld: (p[3] & 0x01) != 0,
            Project: (p[3] & 0x02) != 0,
            Raw: p);
    }
}

/// <summary>$07 chain state: active chain, active pattern, next chain index, project.</summary>
public sealed record ChainState(IReadOnlyList<int> Chain, int Pattern, int NextIndex, int Project, byte Address)
{
    public static ChainState Decode(byte[] p)
    {
        if (p.Length < 20) return new([], 0, 0, 0, 0);
        int length = Math.Min((int)p[16], 31);
        var chain = new List<int>();
        for (int i = 0; i < length; i++)
            chain.Add((p[i / 2] >> (i % 2 == 0 ? 0 : 4)) & 0x0F);
        return new(chain, p[17] & 0x0F, p[18], p[19] & 0x0F, p[17]);
    }

    public int Position => Chain.Count == 0 ? 0 : (NextIndex == 0 ? Chain.Count : NextIndex);

    public static byte[] Encode(IReadOnlyList<int> chain, int project)
    {
        var payload = new byte[20];
        for (int i = 0; i < chain.Count; i++)
            payload[i / 2] |= (byte)((chain[i] & 0x0F) << (i % 2 == 0 ? 0 : 4));
        payload[16] = (byte)chain.Count;
        payload[17] = (byte)(((project & 0x0F) << 4) | (chain[0] & 0x0F));
        payload[18] = (byte)(chain.Count > 1 ? 1 : 0);
        payload[19] = (byte)project;
        return payload;
    }
}

/// <summary>$12 mixer state: 16 track levels plus a mute mask.</summary>
public sealed record MixerState(byte[] Levels, ushort MuteMask)
{
    public static MixerState Decode(byte[] p) =>
        p.Length < 18 ? new(new byte[16], 0) : new(p[..16], BinaryPrimitives.ReadUInt16LittleEndian(p.AsSpan(16)));

    public bool IsMuted(int track) => (MuteMask & (1 << track)) != 0;

    public byte[] Encode()
    {
        var payload = new byte[18];
        Levels.CopyTo(payload, 0);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(16), MuteMask);
        return payload;
    }
}

/// <summary>$13 pattern state: copy flags, pattern lift, chain mode, autosave, bounce.</summary>
public readonly record struct PatternState(bool PatternCopied, bool ProjectCopied, int LiftMode, bool ChainMode, bool Autosave, bool Bouncing)
{
    public static PatternState Decode(byte[] p) => p.Length < 3 ? default :
        new(p[0] != 0, p[1] != 0, p[2] & 0x0F, (p[2] & 0x10) != 0, (p[2] & 0x20) != 0, (p[2] & 0x40) != 0);
}

/// <summary>$14 expansion module info.</summary>
public readonly record struct ModuleInfo(uint Caps, uint Status, string Name, bool Connected)
{
    public static ModuleInfo Decode(byte[] p)
    {
        if (p.Length < 19) return default;
        int end = Array.IndexOf(p, (byte)0, 8, 10);
        string name = Encoding.ASCII.GetString(p, 8, (end < 0 ? 18 : end) - 8);
        return new(BinaryPrimitives.ReadUInt32LittleEndian(p), BinaryPrimitives.ReadUInt32LittleEndian(p.AsSpan(4)), name, p[18] != 0);
    }
}

/// <summary>$1A sampler UI state.</summary>
public readonly record struct SamplerUiState(int Context, int PackProgress, int Position, int Page, bool Active)
{
    public static SamplerUiState Decode(byte[] p) => p.Length < 5 ? default : new(p[0], p[1], p[2], p[3], (p[4] & 1) != 0);
}

/// <summary>Serial number and firmware version from $11 or the identity reply.</summary>
public readonly record struct DeviceInfo(string Serial, string Firmware)
{
    public static DeviceInfo FromMessage11(byte[] p)
    {
        // Two NUL-terminated ASCII strings: serial, then firmware version.
        var parts = Encoding.ASCII.GetString(p).Split('\0', StringSplitOptions.RemoveEmptyEntries);
        return new(parts.Length > 0 ? parts[0] : "", parts.Length > 1 ? parts[1] : "");
    }
}
