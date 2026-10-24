using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using OPB.App.Rendering;
using OPB.Core.Model;

namespace OPB.App.Screens;

/// <summary>
/// The lights (DMX) track: a 16-fixture preview simulated from the track's color, alt color, speed,
/// intensity and pattern; tap grids for the 10 patterns and the 14 animations (held while touched);
/// main controls on page 1 and the passthrough knobs 5..8 on page 2.
/// </summary>
/// <remarks>
/// The OP-Z doesn't report its fixture colors, so the preview follows the same controls but is an
/// approximation. Sound bytes and note numbers per the native DmxPlug tables: color = byte 0,
/// alt color = 1, speed = 8, intensity = 9, knobs 5..8 = 6, 7, 10, 11.
/// </remarks>
[Screen(ScreenKeys.LightsTrack)]
public sealed class LightsScreen(AppModel app) : ControlTrackScreen(app)
{
    const double RowB = 342, KnobLeft = 736;
    static readonly int[] PatternNotes = [54, 56, 58, 61, 63, 66, 68, 70, 73, 75];
    static readonly int[] AnimationNotes = [53, 55, 57, 59, 60, 62, 64, 65, 67, 69, 71, 72, 74, 76];
    static readonly string[] PatternNames = ["SOLID", "ALTERNATE", "CHASE", "PULSE", "SPLIT", "SPARKLE", "WAVE", "STROBE", "BOUNCE", "RAINBOW"];

    readonly Stopwatch _clock = Stopwatch.StartNew();
    readonly HashSet<int> _heldAnimations = new();
    int _pattern = -1;
    bool _subscribed;

    protected override int Track => (int)TrackId.Lights;
    protected override bool Animating => true;

    protected override void OnShown()
    {
        if (_subscribed) return;
        _subscribed = true;
        Session.NoteEvent += (track, note, velocity) =>
        {
            if (track != Track) return;
            int p = Array.IndexOf(PatternNotes, note), a = Array.IndexOf(AnimationNotes, note);
            if (p >= 0 && velocity > 0) _pattern = p;
            if (a >= 0) { if (velocity > 0) _heldAnimations.Add(a); else _heldAnimations.Remove(a); }
        };
    }

    protected override string? StepText(IReadOnlyList<Note> notes)
    {
        int p = Array.IndexOf(PatternNotes, notes[0].Pitch), a = Array.IndexOf(AnimationNotes, notes[0].Pitch);
        return p >= 0 ? $"P{p + 1}" : a >= 0 ? $"A{a + 1}" : null;
    }

    protected override void RenderBody(DrawingContext dc)
    {
        Draw.VLine(dc, KnobLeft, Row0, BodyBottom);
        Draw.HLine(dc, LeftEdge, RightEnd, RowB);
        Draw.HLine(dc, KnobLeft, RightEnd, Row0 + CellH);
        for (int i = 1; i < 4; i++) Draw.VLine(dc, KnobLeft + i * CellW, Row0, RowB);
        Draw.VLine(dc, KnobLeft + 4 * CellW, Row0, RowB);

        DrawFixtures(dc, new Rect(0, Row0, KnobLeft, RowB - Row0));

        // Page 1: the main controls; page 2: passthrough knobs 5..8.
        Cell(dc, KnobLeft, Row0, "COLOR", Snd(SoundParam.P1, "COLOR", Hue), 0, 0);
        Cell(dc, KnobLeft + CellW, Row0, "ALT COL", Snd(SoundParam.P2, "ALT COLOR", Hue), 0, 1);
        Cell(dc, KnobLeft + 2 * CellW, Row0, "SPEED", Snd(SoundParam.Filter, "SPEED"), 0, 2);
        Cell(dc, KnobLeft + 3 * CellW, Row0, "INTENS", Snd(SoundParam.Resonance, "INTENSITY"), 0, 3);
        SoundParam[] knobs = [SoundParam.Fx1Send, SoundParam.Fx2Send, SoundParam.Pan, SoundParam.Level];
        for (int i = 0; i < 4; i++)
            Cell(dc, KnobLeft + i * CellW, Row0 + CellH, $"KNOB {i + 5}", Snd(knobs[i], $"KNOB {i + 5}"), 1, i);

        // Main and alt color swatches.
        double sx = KnobLeft + 4 * CellW + 20, sw = RightEnd - sx - 20;
        Draw.Label(dc, "MAIN", sx, Row0 + 30, 22, Theme.Mid);
        dc.DrawRectangle(new SolidColorBrush(Hsv(HueOf(SoundParam.P1), 0.9, 0.95)), null, new Rect(sx, Row0 + 48, sw, 72));
        Draw.Label(dc, "ALT", sx, Row0 + CellH + 30, 22, Theme.Mid);
        dc.DrawRectangle(new SolidColorBrush(Hsv(HueOf(SoundParam.P2), 0.9, 0.95)), null, new Rect(sx, Row0 + CellH + 48, sw, 72));

        // Patterns 1..10: a tap selects; animations 1..14: held while touched.
        Section(dc, "PATTERN", 0, RowB);
        for (int i = 0; i < 10; i++)
        {
            var r = new Rect(18 + (i % 5) * 140, RowB + 70 + (i / 5) * 92, 128, 80);
            bool on = i == _pattern;
            dc.DrawRectangle(on ? Theme.Ink : null, new Pen(on ? Theme.Ink : Theme.Mid, 2), r);
            Draw.Label(dc, (i + 1).ToString(), r.X + 14, r.Y + 24, 28, on ? Theme.White : Theme.Ink, Align.Left, Theme.Regular);
            Draw.Label(dc, PatternNames[i], r.X + 14, r.Y + 58, 18, on ? Theme.White : Theme.Mid);
            int index = i;
            Button(r, (_, down) =>
            {
                SendNote(PatternNotes[index], down);
                if (down) _pattern = index;
            });
        }
        Section(dc, "ANIMATIONS (HOLD)", KnobLeft, RowB);
        for (int i = 0; i < 14; i++)
        {
            var r = new Rect(KnobLeft + 18 + (i % 7) * 94, RowB + 70 + (i / 7) * 92, 84, 80);
            bool on = _heldAnimations.Contains(i);
            dc.DrawRectangle(on ? Theme.Yellow : null, new Pen(on ? Theme.Yellow : Theme.Mid, 2), r);
            Draw.Label(dc, i == 13 ? "FOG" : (i + 1).ToString(), r.X + r.Width / 2, r.Y + r.Height / 2, 28, Theme.Ink, Align.Center, Theme.Regular);
            int index = i;
            Button(r, (_, down) =>
            {
                SendNote(AnimationNotes[index], down);
                if (down) _heldAnimations.Add(index); else _heldAnimations.Remove(index);
            });
        }
    }

    double HueOf(SoundParam p) => S(p) / 255.0 * 360;
    static string Hue(int v) => $"{(int)Math.Round(v / 255.0 * 360)}°";

    void DrawFixtures(DrawingContext dc, Rect r)
    {
        Section(dc, "FIXTURES", r.X, r.Y);
        Draw.Label(dc, "PREVIEW", r.Right - 18, r.Y + 42, 22, Theme.Mid, Align.Right);
        double t = _clock.Elapsed.TotalSeconds, speed = 0.3 + S(SoundParam.Filter) / 255.0 * 4, intensity = S(SoundParam.Resonance) / 255.0;
        double main = HueOf(SoundParam.P1), alt = HueOf(SoundParam.P2);
        double phase = t * speed;
        int pattern = Math.Max(0, _pattern);
        bool animating = _heldAnimations.Count > 0;
        for (int i = 0; i < 16; i++)
        {
            double hue = main, level = 1;
            int step = (int)Math.Floor(phase * 4);
            switch (pattern)
            {
                case 1: hue = ((i + step) % 2 == 0) ? main : alt; break;
                case 2: hue = i == step % 16 ? alt : main; break;
                case 3: level = 0.25 + 0.75 * (0.5 + 0.5 * Math.Sin(phase * Math.PI * 2)); break;
                case 4: hue = (i < 8) ^ (step % 2 == 1) ? main : alt; break;
                case 5: hue = Hash(i, step) > 0.7 ? alt : main; level = Hash(i + 50, step) > 0.3 ? 1 : 0.2; break;
                case 6: hue = main + (alt - main) * (0.5 + 0.5 * Math.Sin(phase * 2 + i * 0.4)); break;
                case 7: level = step % 2 == 0 ? 1 : 0.05; break;
                case 8: { int pos = step % 30; pos = pos < 16 ? pos : 30 - pos; hue = i == pos ? alt : main; level = i == pos ? 1 : 0.5; break; }
                case 9: hue = main + i * 22.5 + phase * 40; break;
            }
            if (animating && Hash(i, (int)(t * 12)) > 0.5) level = 1;
            var color = Hsv(hue, animating ? 0.35 : 0.9, Math.Clamp(level * (0.15 + 0.85 * intensity), 0, 1));
            var center = new Point(r.X + 70 + (i % 8) * 84, r.Y + 110 + (i / 8) * 86);
            dc.DrawEllipse(new SolidColorBrush(color), new Pen(Theme.Ink, 3), center, 32, 32);
            Draw.Label(dc, (i + 1).ToString(), center.X, center.Y, 20, color.R * 0.3 + color.G * 0.59 + color.B * 0.11 > 140 ? Theme.Ink : Theme.White, Align.Center, Theme.Regular);
        }
        if (_heldAnimations.Contains(13))
            dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(0x70, 0xF2, 0xF2, 0xF2)), null, new Rect(r.X + 20, r.Y + 70, r.Width - 40, r.Height - 80));
    }

    static double Hash(int a, int b)
    {
        uint h = (uint)(a * 374761393 + b * 668265263);
        h = (h ^ (h >> 13)) * 1274126177;
        return (h & 0xFFFF) / 65535.0;
    }
}
