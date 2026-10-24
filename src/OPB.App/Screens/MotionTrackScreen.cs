using System.Windows;
using System.Windows.Media;
using OPB.App.Rendering;
using OPB.Core.Model;

namespace OPB.App.Screens;

/// <summary>
/// The motion track (track 16), which drives Photomatic and Motion: the color dials (page 1) and
/// the transition dials (page 2) with a live color preview, the keyboard map of what each note
/// triggers (slots 1..24, effects and rolls above), and shortcuts to the Photomatic and Motion pages.
/// </summary>
[Screen(ScreenKeys.MotionTrack)]
public sealed class MotionTrackScreen(AppModel app) : ControlTrackScreen(app)
{
    const double RowB = 342, PreviewLeft = 736, KeysRight = 1110;
    readonly HashSet<int> _held = new();
    bool _subscribed;

    protected override int Track => (int)TrackId.Motion;

    protected override void OnShown()
    {
        if (_subscribed) return;
        _subscribed = true;
        Session.NoteEvent += (track, note, velocity) =>
        {
            if (track != Track) return;
            if (velocity > 0) _held.Add(note); else _held.Remove(note);
            InvalidateVisual();
        };
    }

    /// <summary>Slot number for slot notes (53..76, folded every 24 below), "FX" for the effect/roll octave above.</summary>
    static string Slot(int note) => note >= PhotomaticScreen.EffectNoteBase
        ? "FX"
        : (((note - PhotomaticScreen.SlotNoteBase) % 24 + 24) % 24 + 1).ToString();

    protected override string? StepText(IReadOnlyList<Note> notes) => Slot(notes[0].Pitch);

    protected override void RenderBody(DrawingContext dc)
    {
        Draw.VLine(dc, 216, Row0, RowB);
        Draw.VLine(dc, PreviewLeft, Row0, RowB);
        Draw.HLine(dc, LeftEdge, RightEnd, RowB);
        Draw.HLine(dc, LeftEdge, PreviewLeft, Row0 + CellH);
        for (int i = 1; i < 4; i++) Draw.VLine(dc, 216 + i * CellW, Row0, RowB);
        Draw.VLine(dc, KeysRight, RowB, BodyBottom);

        Section(dc, "COLOR", 0, Row0);
        Cell(dc, 216, Row0, "HUE", Snd(SoundParam.P1, "HUE", Signed), 0, 0);
        Cell(dc, 216 + CellW, Row0, "SAT", Snd(SoundParam.P2, "SATURATION", Signed), 0, 1);
        Cell(dc, 216 + 2 * CellW, Row0, "BRIGHT", Snd(SoundParam.Filter, "BRIGHTNESS", Signed), 0, 2);
        Cell(dc, 216 + 3 * CellW, Row0, "CONTR", Snd(SoundParam.Resonance, "CONTRAST", Signed), 0, 3);
        Section(dc, "SHIFT", 0, Row0 + CellH);
        Cell(dc, 216, Row0 + CellH, "XFADE", Snd(SoundParam.Attack, "CROSSFADE"), 1, 0);
        Cell(dc, 216 + CellW, Row0 + CellH, "ZOOM", Snd(SoundParam.Decay, "ZOOM"), 1, 1);
        Cell(dc, 216 + 2 * CellW, Row0 + CellH, "DOUBLE", Snd(SoundParam.Sustain, "DOUBLE"), 1, 2);
        Cell(dc, 216 + 3 * CellW, Row0 + CellH, "WOOZY", Snd(SoundParam.Release, "WOOZY"), 1, 3);

        DrawPreview(dc, new Rect(PreviewLeft + 30, Row0 + 54, RightEnd - PreviewLeft - 60, RowB - Row0 - 84));
        DrawKeys(dc, new Rect(18, RowB + 60, KeysRight - 36, BodyBottom - RowB - 80));

        // Shortcuts to the pages this track drives.
        Section(dc, "OPEN", KeysRight, RowB);
        (string Label, AppPage Page)[] pages = [("PHOTOMATIC", AppPage.Photomatic), ("MOTION", AppPage.Motion)];
        for (int i = 0; i < pages.Length; i++)
        {
            var r = new Rect(KeysRight + 18, RowB + 70 + i * 92, RightEnd - KeysRight - 36, 78);
            dc.DrawRectangle(Theme.Ink, null, r);
            Draw.Label(dc, pages[i].Label, r.X + r.Width / 2, r.Y + r.Height / 2, 28, Theme.White, Align.Center, Theme.Regular);
            var page = pages[i].Page;
            Button(r, (_, down) => { if (!down) App.Navigate(page); });
        }
    }

    static string Signed(int v)
    {
        int c = (int)Math.Round((v - 128) / 128.0 * 50);
        return c > 0 ? $"+{c}" : c.ToString();
    }

    /// <summary>Hue bars and a grey ramp run through the current hue/saturation/brightness/contrast settings.</summary>
    void DrawPreview(DrawingContext dc, Rect r)
    {
        Draw.Label(dc, "PREVIEW", r.X - 12, Row0 + 30, 22, Theme.Mid);
        double hueShift = (S(SoundParam.P1) - 128) / 128.0 * 180, sat = S(SoundParam.P2) / 128.0;
        double bright = (S(SoundParam.Filter) - 128) / 128.0, contrast = S(SoundParam.Resonance) / 128.0;
        Color Apply(double hue, double s, double v)
        {
            var c = Hsv(hue + hueShift, Math.Clamp(s * sat, 0, 1), 1);
            double Ch(byte x) => Math.Clamp(((x / 255.0 * v) - 0.5) * contrast + 0.5 + bright * 0.5, 0, 1);
            return Color.FromRgb((byte)(Ch(c.R) * 255), (byte)(Ch(c.G) * 255), (byte)(Ch(c.B) * 255));
        }
        int bars = 12;
        double bw = r.Width / bars, top = r.Height * 0.62;
        for (int i = 0; i < bars; i++)
        {
            dc.DrawRectangle(new SolidColorBrush(Apply(i * 30, 0.85, 0.95)), null, new Rect(r.X + i * bw, r.Y, bw + 0.5, top));
            dc.DrawRectangle(new SolidColorBrush(Apply(0, 0, i / (double)(bars - 1))), null, new Rect(r.X + i * bw, r.Y + top, bw + 0.5, r.Height - top));
        }
        dc.DrawRectangle(null, new Pen(Theme.Ink, 2), r);
    }

    /// <summary>The OP-Z keyboard's two octaves: 24 slot keys, lit while the motion track plays them.</summary>
    void DrawKeys(DrawingContext dc, Rect r)
    {
        Section(dc, "KEYS → PHOTOMATIC SLOTS", 0, RowB);
        int[] white = [0, 2, 4, 6, 7, 9, 11, 12, 14, 16, 18, 19, 21, 23];
        int[] black = [1, 3, 5, 8, 10, 13, 15, 17, 20, 22];
        var lit = _held.Where(n => n < PhotomaticScreen.EffectNoteBase).Select(n => ((n - PhotomaticScreen.SlotNoteBase) % 24 + 24) % 24).ToHashSet();
        double w = r.Width / 14, bw = w * 0.62, bh = r.Height * 0.58;
        for (int i = 0; i < white.Length; i++)
        {
            var k = new Rect(r.X + i * w, r.Y, w, r.Height);
            bool on = lit.Contains(white[i]);
            dc.DrawRectangle(on ? Theme.Yellow : Theme.Background, new Pen(Theme.Rule, 2), k);
            Draw.Label(dc, (white[i] + 1).ToString(), k.X + w / 2, k.Bottom - 22, 22, on ? Theme.Ink : Theme.Mid, Align.Center, Theme.Regular);
        }
        foreach (int key in black)
        {
            int after = Array.FindLastIndex(white, x => x < key);
            var k = new Rect(r.X + (after + 1) * w - bw / 2, r.Y, bw, bh);
            bool on = lit.Contains(key);
            dc.DrawRectangle(on ? Theme.Yellow : Theme.Ink, null, k);
            Draw.Label(dc, (key + 1).ToString(), k.X + bw / 2, k.Bottom - 20, 18, on ? Theme.Ink : Theme.Mid, Align.Center);
        }
    }
}
