using System.Windows;
using System.Windows.Media;
using OPB.App.Rendering;
using OPB.Core;
using OPB.Core.Model;

namespace OPB.App.Screens;

/// <summary>
/// Shown while shift + a step is held: the step as a node wired to a box for every step
/// component on it (with the value-key meaning) and to its parameter locks.
/// </summary>
[Screen(ScreenKeys.StepComponents)]
public sealed class StepComponentsScreen : ScreenView
{
    const double TitleY = 40, Row0 = 82, NodeX = 190, NodeY = 440, NodeR = 46;
    const double CompBus = 400, CompX = 470, CompW = 400, LockBus = 990, LockX = 1060, LockW = 470;
    const double AreaTop = 160, AreaBottom = 740, BottomWire = 776;

    static readonly string[] ComponentNames =
    [
        "PULSE", "PARAM SPARK", "JUMP", "VELOCITY", "RAMP UP", "RAMP DOWN", "RANDOM", "GLIDE",
        "MULTIPLY", "SWEEP", "PULSE HOLD", "TONALITY", "TRIGGER SPARK", "COMP SPARK", "BIT 14", "BIT 15",
    ];
    static readonly string[] Jumps = ["TO START", "TO 2/4", "TO 3/4", "TO 4/4", "FORWARD", "BACK", "RANDOM", "STAY", "ALIGN GLOBAL", "GATE STEP"];
    static readonly string[] Sweeps = ["FILTER UP", "FILTER DOWN", "SYNTH UP", "SYNTH DOWN", "PAN", "FILTER UP L", "FILTER DN L", "SYNTH UP L", "SYNTH DN L", "PAN L"];
    static readonly string[] Tonalities = ["IGNORE CHORDS", "TRANSPOSE", "OCTAVE", "FIFTH", "THIRD", "CHROM UP", "CHROM DOWN", "QUANTIZE 1", "QUANTIZE 2", "QUANTIZE 3"];
    static readonly int[] Velocities = [4, 8, 16, 32, 64, 100, 112, 127];

    int _step;

    public StepComponentsScreen(AppModel app) : base(app.Session) { }

    int Track => Session.ActiveTrack;
    int Pattern => Session.ActivePattern;
    int Step => Session.Ui.HeldKey >= 0 ? _step = Session.Ui.HeldKey : _step;

    public override string Title => $"STEP {Step + 1}";

    /// <summary>The meaning of a component value; raw 1..10 are value keys 1..9, 0 (sparks keep a mode in the high nibble).</summary>
    static string ComponentText(int component, byte raw)
    {
        int k = raw & 0x0F;
        if (k is < 1 or > 10) return raw.ToString();
        return component switch
        {
            0 or 10 => k == 10 ? "RANDOM" : $"COUNT {k}",
            1 or 12 or 13 => k <= 8 ? $"PASS {k}" : k == 9 ? "RANDOM" : "RESET",
            2 => Jumps[k - 1],
            3 => k <= 8 ? $"VEL {Velocities[k - 1]}" : k == 9 ? "MUTE" : "RANDOM",
            4 or 5 or 6 => $"{(k - 1) % 5 + 2} / {(k <= 5 ? 1 : 3)} OCT",
            7 => k <= 8 ? $"GLIDE {k}" : k == 9 ? "DIRECT" : "RANDOM",
            8 => k <= 8 ? $"X{k}" : raw.ToString(),
            9 => Sweeps[k - 1],
            11 => Tonalities[k - 1],
            _ => raw.ToString(),
        };
    }

    string LockName(SoundParam p)
    {
        bool drum = Tracks.Kind(Track) == TrackKind.Drum;
        return p switch
        {
            SoundParam.Attack => drum ? "START" : "ATTACK",
            SoundParam.Decay => drum ? "ATTACK" : "DECAY",
            SoundParam.Sustain => drum ? "HOLD" : "SUSTAIN",
            SoundParam.Release => drum ? "DECAY" : "RELEASE",
            SoundParam.Fx1Send => "FX 1",
            SoundParam.Fx2Send => "FX 2",
            SoundParam.Resonance => "RES",
            SoundParam.LfoDepth => "LFO DEPTH",
            SoundParam.LfoSpeed => "LFO RATE",
            SoundParam.LfoValue => "LFO DEST",
            SoundParam.LfoShape => "LFO SHAPE",
            SoundParam.NoteStyle => "NOTE STYLE",
            _ => p.ToString().ToUpperInvariant(),
        };
    }

    string LockText(SoundParam p, byte v) => p switch
    {
        SoundParam.Pan => v == 128 ? "C" : v < 128 ? $"L{Display.Percent((byte)((128 - v) * 2 - 1))}" : $"R{Display.Percent((byte)((v - 128) * 2 + 1))}",
        SoundParam.LfoValue => Display.Band(v, Display.LfoDestinations),
        SoundParam.LfoShape => Display.Band(v, Display.LfoShapes),
        SoundParam.NoteStyle => Display.Band(v, Tracks.Kind(Track) == TrackKind.Drum ? Display.DrumNoteStyles : Display.SynthNoteStyles),
        _ => Display.Percent(v).ToString(),
    };

    // ------------------------------------------------------------------ render

    protected override void Render(DrawingContext dc)
    {
        int step = Step;
        Draw.Label(dc, Title, DesignWidth / 2, TitleY, 56, Theme.Ink, Align.Center, Theme.Regular);
        Draw.Label(dc, Tracks.Names[Track], 20, TitleY, 30, Theme.Mid);
        Draw.HLine(dc, LeftEdge, RightEdge, Row0);
        Draw.Label(dc, "COMPONENTS", CompX, Row0 + 42, 30, Theme.Mid);
        Draw.Label(dc, "PARAMETER LOCKS", LockX, Row0 + 42, 30, Theme.Mid);

        var bank = Session.Bank;
        var components = new List<(string Name, string Value)>();
        var locks = new List<(string Name, string Value)>();
        IReadOnlyList<Note> notes = [];
        if (bank is not null)
        {
            ushort mask = bank.ComponentMask(Pattern, Track, step);
            for (int c = 0; c < 16; c++)
                if ((mask & (1 << c)) != 0) components.Add((ComponentNames[c], ComponentText(c, bank.ComponentValue(Pattern, Track, step, c))));
            foreach (var p in Enum.GetValues<SoundParam>())
                if (bank.IsLocked(Pattern, Track, step, p)) locks.Add((LockName(p), LockText(p, bank.LockValue(Pattern, Track, step, p))));
            notes = bank.Notes(Pattern, Track, step);
        }

        var node = new Point(NodeX, NodeY);
        var compPen = new Pen(Theme.Green, 3) { LineJoin = PenLineJoin.Round };
        var lockPen = new Pen(Theme.Red, 3) { LineJoin = PenLineJoin.Round };

        var compYs = Rows(components.Count);
        if (components.Count > 0)
        {
            dc.DrawLine(compPen, new Point(NodeX + NodeR, NodeY), new Point(CompBus, NodeY));
            dc.DrawLine(compPen, new Point(CompBus, Math.Min(NodeY, compYs[0])), new Point(CompBus, Math.Max(NodeY, compYs[^1])));
        }
        else Draw.Label(dc, "NONE", CompX, NodeY, 30, Theme.Rule);
        for (int i = 0; i < components.Count; i++)
            DrawBox(dc, compPen, Theme.Green, CompBus, CompX, CompW, compYs[i], components[i].Name, components[i].Value, BoxHeight(components.Count));

        var lockYs = Rows(locks.Count);
        if (locks.Count > 0)
        {
            // Route under the component column: down from the node, across, up the lock bus.
            var route = new StreamGeometry();
            using (var g = route.Open())
            {
                g.BeginFigure(new Point(NodeX, NodeY + NodeR), false, false);
                g.PolyLineTo([new Point(NodeX, BottomWire), new Point(LockBus, BottomWire), new Point(LockBus, lockYs[0])], true, true);
            }
            route.Freeze();
            dc.DrawGeometry(null, lockPen, route);
        }
        else Draw.Label(dc, "NONE", LockX, NodeY, 30, Theme.Rule);
        for (int i = 0; i < locks.Count; i++)
            DrawBox(dc, lockPen, Theme.Red, LockBus, LockX, LockW, lockYs[i], locks[i].Name, locks[i].Value, BoxHeight(locks.Count));

        bool trig = notes.Count > 0;
        dc.DrawEllipse(trig ? Theme.Ink : Theme.Mid, null, node, NodeR, NodeR);
        Draw.Label(dc, (step + 1).ToString(), NodeX, NodeY, 44, Theme.Background, Align.Center, Theme.Regular);
        string noteText = trig ? string.Join(" ", notes.Select(n => Tracks.Kind(Track) == TrackKind.Drum ? $"NOTE {n.Pitch}" : Display.NoteName(n.Pitch))) : "NO TRIG";
        Draw.Label(dc, noteText, NodeX, NodeY - NodeR - 70, 28, Theme.Mid, Align.Center);
        if (trig) Draw.Label(dc, $"VEL {notes[0].Velocity}", NodeX, NodeY - NodeR - 34, 28, Theme.Mid, Align.Center);
    }

    static double BoxHeight(int count) => Math.Min(56, (AreaBottom - AreaTop) / Math.Max(1, count) - 8);

    static double[] Rows(int count)
    {
        if (count == 0) return [];
        double pitch = Math.Min(72, (AreaBottom - AreaTop) / count);
        double top = Math.Clamp(NodeY - pitch * (count - 1) / 2, AreaTop + pitch / 2, AreaBottom - pitch * (count - 0.5));
        return Enumerable.Range(0, count).Select(i => top + i * pitch).ToArray();
    }

    static void DrawBox(DrawingContext dc, Pen wire, Brush color, double bus, double x, double w, double y, string name, string value, double h)
    {
        dc.DrawLine(wire, new Point(bus, y), new Point(x, y));
        dc.DrawEllipse(color, null, new Point(bus, y), 5, 5);
        dc.DrawRectangle(Theme.Background, wire, new Rect(x, y - h / 2, w, h));
        double size = Math.Min(30, h * 0.6);
        Draw.Label(dc, name, x + 14, y, size, Theme.Mid);
        Draw.Label(dc, value, x + w - 14, y, size, Theme.Ink, Align.Right, Theme.Regular);
    }
}
