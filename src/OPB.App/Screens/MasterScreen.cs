using System.Windows;
using System.Windows.Media;
using OPB.App.Rendering;
using OPB.Core.Model;

namespace OPB.App.Screens;

/// <summary>
/// The master track: chorus, drive and the combined HP/LP filter (with its response curve), the
/// transposition played by the master notes, which tracks follow it, note style and playback speed.
/// </summary>
[Screen(ScreenKeys.MasterTrack)]
public sealed class MasterScreen(AppModel app) : ControlTrackScreen(app)
{
    const double RowB = 342, GraphLeft = 736, GraphRight = 1426;
    static readonly string[] NoteClasses = ["C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B"];
    static readonly int[] KeyWhite = [5, 7, 9, 11, 0, 2, 4];            // F G A B C D E, like the OP-Z keyboard
    static readonly (int Note, int After)[] KeyBlack = [(6, 0), (8, 1), (10, 2), (1, 4), (3, 5)];
    bool _busy;

    protected override int Track => (int)TrackId.Master;

    protected override string? StepText(IReadOnlyList<Note> notes) => NoteClasses[notes[0].Pitch % 12] + (notes.Count > 1 ? "+" : "");

    protected override void RenderBody(DrawingContext dc)
    {
        // Rules.
        Draw.HLine(dc, LeftEdge, RightEnd, RowB);
        Draw.VLine(dc, 216, Row0, BodyBottom);
        Draw.VLine(dc, GraphLeft, Row0, BodyBottom);
        for (int i = 1; i < 4; i++) Draw.VLine(dc, 216 + i * CellW, Row0, Row0 + CellH);
        Draw.HLine(dc, 216, GraphLeft, Row0 + CellH);
        Draw.VLine(dc, 1212, RowB, BodyBottom);
        Draw.HLine(dc, 1212, RightEnd, RowB + 130);

        // Effects: chorus, drive, filter, resonance on page 1.
        Section(dc, "EFFECTS", 0, Row0);
        Cell(dc, 216, Row0, "CHORUS", Snd(SoundParam.P1, "CHORUS"), 0, 0);
        Cell(dc, 216 + CellW, Row0, "DRIVE", Snd(SoundParam.P2, "DRIVE"), 0, 1);
        Cell(dc, 216 + 2 * CellW, Row0, "FILTER", Snd(SoundParam.Filter, "FILTER", FilterText), 0, 2);
        Cell(dc, 216 + 3 * CellW, Row0, "RES", Snd(SoundParam.Resonance, "RES"), 0, 3);
        Draw.Label(dc, "LP", 216 + 2 * CellW + 14, Row0 + 82, 20, Theme.Mid);
        Draw.Label(dc, "HP", 216 + 3 * CellW - 14, Row0 + 82, 20, Theme.Mid, Align.Right);
        DrawFilterCurve(dc, new Rect(GraphLeft + 40, Row0 + 50, GraphRight - GraphLeft - 80, RowB - Row0 - 100));
        DrawDrive(dc, new Rect(216 + 20, Row0 + CellH + 10, 4 * CellW - 40, RowB - Row0 - CellH - 20));

        // Transposition: the master notes playing now.
        Section(dc, "TRANSPOSE", 0, RowB);
        var active = ActiveNotes();
        string key = active.Count == 0 ? "—" : string.Join(" ", active.Select(n => NoteClasses[n % 12]).Distinct());
        Draw.Label(dc, key, 108, RowB + 150, 64, Theme.Ink, Align.Center, Theme.Regular);
        Draw.Label(dc, active.Count == 0 ? "NO NOTE" : "KEY", 108, RowB + 215, 24, Theme.Mid, Align.Center);
        DrawKeyboard(dc, new Rect(236, RowB + 30, 480, 200), active.Select(n => n % 12).ToHashSet());

        // Which tracks follow the transposition (master routing mask), tap to toggle.
        Section(dc, "TRACKS", GraphLeft, RowB);
        ushort mask = Session.Bank?.MasterRouting(Pattern) ?? 0;
        for (int t = 0; t < 8; t++)
        {
            var r = new Rect(GraphLeft + 18 + (t % 4) * 114, RowB + 80 + (t / 4) * 84, 104, 70);
            bool on = (mask & (1 << t)) != 0;
            dc.DrawRectangle(on ? Theme.Ink : null, new Pen(on ? Theme.Ink : Theme.Mid, 2), r);
            Draw.Label(dc, Tracks.Names[t], r.X + r.Width / 2, r.Y + r.Height / 2, 24, on ? Theme.White : Theme.Mid, Align.Center, Theme.Regular);
            int track = t;
            Button(r, (pointer, down) => { if (down) _ = ToggleRoutingAsync(track); });
        }

        // Note style and playback speed (the master's step length).
        string[] styles = ["LATCH", "FREE"];
        var style = Snd(SoundParam.NoteStyle, "NOTE STYLE", v => Display.Band((byte)v, styles));
        Draw.Label(dc, "NOTE STYLE", 1230, RowB + 42, 28, Theme.Mid);
        Draw.Label(dc, style.Text, 1319, RowB + 92, 40, Theme.Ink, Align.Center, Theme.Regular);
        Hit(new Rect(1212, RowB, RightEnd - 1212, 130), style);
        int length = Session.Bank?.StepLength(Pattern, Track) ?? 1;
        Draw.Label(dc, "SPEED", 1230, RowB + 172, 28, Theme.Mid);
        Draw.Label(dc, $"x{length}", 1319, RowB + 222, 40, Theme.Ink, Align.Center, Theme.Regular);
        Hit(new Rect(1212, RowB + 130, RightEnd - 1212, 130), new ParamRef($"ctl:{Track}:steplength", "SPEED",
            () => length, v => { App.SendTrackCc(Track, 61, v); Session.Bank?.SetStepLength(Pattern, Track, v); InvalidateVisual(); }, v => $"x{v}", 1, 16, 0.05));
    }

    static string FilterText(int v)
    {
        int c = (int)Math.Round((v - 128) / 128.0 * 50);
        return c == 0 ? "OFF" : c < 0 ? $"LP {-c}" : $"HP {c}";
    }

    /// <summary>Master notes sounding at the current step: the latest step at or before the playhead with notes.</summary>
    List<int> ActiveNotes()
    {
        var bank = Session.Bank;
        if (bank is null) return [];
        int count = bank.StepCount(Pattern, Track);
        int step = Math.Max(0, CurrentStep(count, bank.StepLength(Pattern, Track)));
        for (int back = 0; back < count; back++)
        {
            var notes = bank.Notes(Pattern, Track, ((step - back) % count + count) % count);
            if (notes.Count > 0) return notes.Select(n => (int)n.Pitch).ToList();
        }
        return [];
    }

    void DrawFilterCurve(DrawingContext dc, Rect r)
    {
        // Log frequency axis 20 Hz..20 kHz; one-pole-ish response with a resonance bump at the cutoff.
        double f = S(SoundParam.Filter), q = S(SoundParam.Resonance) / 255.0;
        bool lowPass = f < 128;
        double amount = Math.Abs(f - 128) / 128.0;
        double cutoff = lowPass ? 4.3 - amount * 2.8 : 1.3 + amount * 2.8; // log10(Hz): 20 Hz = 1.3, 20 kHz = 4.3
        var geo = new StreamGeometry();
        using (var g = geo.Open())
        {
            for (int i = 0; i <= 120; i++)
            {
                double lf = 1.3 + 3.0 * i / 120;
                double d = (lf - cutoff) * 3;
                double gain = amount < 0.02 ? 0 : lowPass ? -Math.Max(0, d) * 12 : -Math.Max(0, -d) * 12;
                gain += amount < 0.02 ? 0 : q * 9 * Math.Exp(-d * d * 4);
                double y = r.Top + r.Height * 0.45 - gain / 36 * r.Height;
                var p = new Point(r.Left + r.Width * i / 120, Math.Clamp(y, r.Top, r.Bottom));
                if (i == 0) g.BeginFigure(p, false, false); else g.LineTo(p, true, true);
            }
        }
        geo.Freeze();
        Draw.HLine(dc, r.Left, r.Right, r.Top + r.Height * 0.45, new Pen(Theme.Rule, 2) { DashStyle = new DashStyle([3, 3], 0) });
        dc.DrawGeometry(null, new Pen(DialBrush(0, 2), 4) { LineJoin = PenLineJoin.Round }, geo);
        string[] ticks = ["20", "200", "2K", "20K"];
        for (int i = 0; i < 4; i++)
            Draw.Label(dc, ticks[i], r.Left + r.Width * i / 3, r.Bottom + 22, 22, Theme.Mid, i == 0 ? Align.Left : i == 3 ? Align.Right : Align.Center);
        Draw.Label(dc, amount < 0.02 ? "FILTER OFF" : lowPass ? "LOW PASS" : "HIGH PASS", r.Right, r.Top - 20, 24, Theme.Mid, Align.Right);
    }

    void DrawDrive(DrawingContext dc, Rect r)
    {
        // A sine shaped by the drive amount, with a chorus copy offset by the chorus amount.
        double drive = S(SoundParam.P2) / 255.0, chorus = S(SoundParam.P1) / 255.0;
        StreamGeometry Wave(double phase, double clip)
        {
            var geo = new StreamGeometry();
            using (var g = geo.Open())
                for (int i = 0; i <= 80; i++)
                {
                    double t = i / 80.0 * Math.PI * 4 + phase;
                    double v = Math.Tanh(Math.Sin(t) * (1 + clip * 6)) / Math.Tanh(1 + clip * 6);
                    var p = new Point(r.Left + r.Width * i / 80, r.Top + r.Height / 2 - v * r.Height * 0.4);
                    if (i == 0) g.BeginFigure(p, false, false); else g.LineTo(p, true, true);
                }
            geo.Freeze();
            return geo;
        }
        if (chorus > 0.02) dc.DrawGeometry(null, new Pen(DialBrush(0, 0), 3) { LineJoin = PenLineJoin.Round }, Wave(chorus * 1.2, drive));
        dc.DrawGeometry(null, new Pen(DialBrush(0, 1), 3) { LineJoin = PenLineJoin.Round }, Wave(0, drive));
    }

    void DrawKeyboard(DrawingContext dc, Rect r, HashSet<int> lit)
    {
        double w = r.Width / 7, bw = w * 0.62, bh = r.Height * 0.58;
        for (int i = 0; i < 7; i++)
        {
            var k = new Rect(r.X + i * w, r.Y, w, r.Height);
            dc.DrawRectangle(lit.Contains(KeyWhite[i]) ? Theme.Yellow : Theme.Background, new Pen(Theme.Rule, 2), k);
            Draw.Label(dc, NoteClasses[KeyWhite[i]], k.X + w / 2, k.Bottom - 22, 22, Theme.Mid, Align.Center);
        }
        foreach (var (note, after) in KeyBlack)
            dc.DrawRectangle(lit.Contains(note) ? Theme.Yellow : Theme.Ink, null, new Rect(r.X + (after + 1) * w - bw / 2, r.Y, bw, bh));
    }

    async Task ToggleRoutingAsync(int track)
    {
        if (_busy || Session.Bank is null) return;
        _busy = true;
        int pattern = Pattern;
        bool on = (Session.Bank.MasterRouting(pattern) & (1 << track)) == 0;
        try { await Session.EditBankAsync(b => b.SetRouted(pattern, master: true, track, on)); }
        finally { _busy = false; InvalidateVisual(); }
    }
}
