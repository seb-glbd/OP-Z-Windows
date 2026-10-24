using System.IO;
using OPB.Core;
using OPB.Core.Model;
using OPB.Core.Protocol;

namespace OPB.App;

/// <summary>
/// Feeds captured OP-Z state into a session without hardware, for screenshots and development:
/// <c>OPB.App --offline tests\fixtures [--ui mixer|tempo|project|shift] [--track N] [--page N] [--playing]</c>.
/// </summary>
public static class OfflinePreview
{
    public static bool IsRequested(string[] args) => args.Contains("--offline");

    public static void Load(OpzSession session, string[] args)
    {
        string dir = Arg(args, "--offline") ?? "tests/fixtures";
        session.InjectSysex(OpzFrame.Build(0x01, [0x0C, 0x15, 0x55, 0x2D]));
        session.InjectSysex(OpzFrame.Build(0x11, "X0000001\01.2.17+\0"u8.ToArray()));
        session.InjectSysex(OpzFrame.Build(0x04, [0x5A, 0xC8, 0x05, 0x00]));

        int track = int.TryParse(Arg(args, "--track"), out var t) ? Math.Clamp(t, 0, 15) : 0;
        session.InjectSysex(OpzFrame.Build(0x03, new PlayState(0, track, args.Contains("--playing")).Encode()));
        session.InjectSysex(OpzFrame.Build(0x07, ChainState.Encode([0], 4)));

        var bankPath = Path.Combine(dir, "bank.bin");
        if (File.Exists(bankPath))
            session.InjectSysex(OpzFrame.Build(0x0A, [0x40, 0, 0, 0, 0, 0, .. Zlib.Compress(File.ReadAllBytes(bankPath))]));
        var globalPath = Path.Combine(dir, "global.bin");
        if (File.Exists(globalPath))
            session.InjectSysex(OpzFrame.Build(0x0C, Zlib.Compress(File.ReadAllBytes(globalPath))));
        var midiPath = Path.Combine(dir, "midiconfig.bin");
        if (File.Exists(midiPath))
            session.InjectSysex(OpzFrame.Build(0x10, Zlib.Compress(File.ReadAllBytes(midiPath))));

        // Mixer: track levels from the sound records, a couple of mutes for illustration.
        var levels = new byte[18];
        for (int i = 0; i < 16; i++) levels[i] = (byte)(150 + i * 37 % 100);
        levels[16] = 0b1100;
        session.InjectSysex(OpzFrame.Build(0x12, levels));

        int page = int.TryParse(Arg(args, "--page"), out var pg) ? Math.Clamp(pg, 0, 3) : 0;
        var ui = new byte[4];
        ui[0] = (byte)(page << 6);
        ui[1] = 0x1F; // no key held
        switch (Arg(args, "--ui"))
        {
            case "mixer": ui[1] |= 0x80; break;
            case "tempo": ui[1] |= 0x40; break;
            case "project": ui[3] |= 0x02; break;
            case "shift": ui[1] = 0x20 | 0x02; break; // shift + step 3 held
        }
        session.InjectSysex(OpzFrame.Build(0x06, ui));
        if (args.Contains("--playing"))
        {
            session.InjectShort(0xFA);
            for (int i = 0; i < 6 * 5; i++) session.InjectShort(0xF8);
        }
    }

    static string? Arg(string[] args, string name)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length && !args[i + 1].StartsWith("--") ? args[i + 1] : null;
    }
}
