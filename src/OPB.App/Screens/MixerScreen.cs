using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using OPB.App.Rendering;
using OPB.Core;
using OPB.Core.Model;

namespace OPB.App.Screens;

/// <summary>
/// The mixer (hold MIXER): group levels in the header, faders for tracks 1-8 (several can be
/// dragged at once) and activity meters that pulse with each note and toggle mute when tapped.
/// </summary>
[Screen(ScreenKeys.Mixer)]
public sealed class MixerScreen : ScreenView
{
    const double TitleY = 40, Row0 = 82, IconRow = 150, ValueY = 184, FaderTop = 214, FaderBottom = 641, MeterY = 726;
    const double X0 = 107, ColW = 173.5, BarW = 130, BarH = 21, MeterR = 64;
    const double PulseSeconds = 0.45, HoldSeconds = 0.8, GroupShowSeconds = 1.2;

    static readonly string[] GroupNames = ["DRUM GROUP", "SYNTH GROUP", "PUNCH", "MASTER"];
    static readonly double[] GroupX = [129, 389, 1083, 1343];
    static readonly Brush[] GroupBrushes = [Theme.Green, Theme.Blue, Theme.Yellow, Theme.Red];

    readonly AppModel _app;
    readonly Stopwatch _clock = Stopwatch.StartNew();
    readonly double[] _noteAt = Enumerable.Repeat(double.NegativeInfinity, 8).ToArray();
    readonly double[] _velocity = new double[8];
    readonly Dictionary<int, (int Track, double Accum)> _faders = new();
    readonly Dictionary<int, (int Group, double Accum)> _groups = new();
    readonly int?[] _heldLevel = new int?[8];
    readonly double[] _heldUntil = new double[8];
    readonly (bool Muted, bool Reported)?[] _mute = new (bool, bool)?[8];
    readonly double[] _groupEditAt = Enumerable.Repeat(double.NegativeInfinity, 4).ToArray();

    public MixerScreen(AppModel app) : base(app.Session)
    {
        _app = app;
        Session.NoteEvent += (track, _, velocity) =>
        {
            if (track >= 8 || velocity == 0) return;
            _noteAt[track] = Now;
            _velocity[track] = velocity / 127.0;
            UpdateAnimation();
            InvalidateVisual();
        };
    }

    public override string Title => "MIXER";

    double Now => _clock.Elapsed.TotalSeconds;

    protected override bool WantsAnimation
    {
        get
        {
            double now = Now;
            for (int t = 0; t < 8; t++)
                if (now - _noteAt[t] < PulseSeconds || (_heldLevel[t] is not null && now < _heldUntil[t])) return true;
            return _groupEditAt.Any(at => now - at < GroupShowSeconds);
        }
    }

    // ------------------------------------------------------------------ state

    int ReportedLevel(int t) => Session.HasMixer ? Session.Mixer.Levels[t] : Session.CurrentSound[t][(int)SoundParam.Level];

    int Level(int t)
    {
        if (_heldLevel[t] is { } held)
        {
            if (_faders.Values.Any(f => f.Track == t) || Now < _heldUntil[t]) return held;
            _heldLevel[t] = null;
        }
        return ReportedLevel(t);
    }

    bool ReportedMute(int t) => Session.HasMixer ? Session.Mixer.IsMuted(t) : Session.Bank?.IsMuted(Session.ActivePattern, t) == true;

    /// <summary>The optimistic mute state holds until the device reports a different state than when it was toggled.</summary>
    bool Muted(int t)
    {
        bool reported = ReportedMute(t);
        if (_mute[t] is { } m)
        {
            if (reported != m.Reported) _mute[t] = null;
            else return m.Muted;
        }
        return reported;
    }

    byte GroupLevel(int g) => Session.Global is not { } gl ? (byte)0 : g switch
    {
        0 => gl.DrumLevel, 1 => gl.SynthLevel, 2 => gl.PunchLevel, _ => gl.MasterLevel,
    };

    void SetGroupLevel(int g, byte v)
    {
        Session.EditGlobal(gl =>
        {
            switch (g)
            {
                case 0: gl.DrumLevel = v; break;
                case 1: gl.SynthLevel = v; break;
                case 2: gl.PunchLevel = v; break;
                default: gl.MasterLevel = v; break;
            }
        });
        _groupEditAt[g] = Now;
        UpdateAnimation();
    }

    void SetLevel(int t, int percent)
    {
        byte raw = Display.FromPercent(Math.Clamp(percent, 0, 99));
        _heldLevel[t] = raw;
        _heldUntil[t] = Now + HoldSeconds;
        Session.SetSound(t, SoundParam.Level, raw);
        InvalidateVisual();
    }

    // ------------------------------------------------------------------ render

    protected override void Render(DrawingContext dc)
    {
        double now = Now;
        Draw.Label(dc, "MIXER", DesignWidth / 2, TitleY, 56, Theme.Ink, Align.Center, Theme.Regular);
        for (int g = 0; g < 4; g++)
        {
            Draw.Handle(dc, new Point(GroupX[g], TitleY + 2), GroupBrushes[g], 22);
            bool editing = _groups.Values.Any(e => e.Group == g) || now - _groupEditAt[g] < GroupShowSeconds;
            if (editing) Draw.Label(dc, Display.Percent(GroupLevel(g)).ToString(), GroupX[g] + 41, TitleY, 36, Theme.Ink, Align.Left, Theme.Regular);
            else Draw.Label(dc, GroupNames[g], GroupX[g] + 41, TitleY, 30, Theme.Mid);
        }

        for (int t = 0; t < 8; t++)
        {
            double x = X0 + t * ColW, cx = x + ColW / 2;
            bool muted = Muted(t);
            if (muted) dc.DrawRectangle(Theme.Hatch, null, new Rect(x, FaderTop, ColW, BottomEdge - FaderTop));
            DrawGlyph(dc, t, x + 17, (Row0 + IconRow) / 2);

            int level = Level(t);
            Draw.Label(dc, Display.Percent((byte)level).ToString(), cx, ValueY, 42, Theme.Ink, Align.Center, Theme.Regular);
            double y = FaderBottom - level / 255.0 * (FaderBottom - FaderTop);
            var color = muted ? Theme.Rule : t < 4 ? Theme.Green : Theme.Blue;
            dc.DrawLine(new Pen(Theme.Rule, 4), new Point(cx, FaderTop), new Point(cx, FaderBottom));
            if (!muted) dc.DrawLine(new Pen(color, 4), new Point(cx, y), new Point(cx, FaderBottom));
            dc.DrawRectangle(color, null, new Rect(cx - BarW / 2, y - BarH / 2, BarW, BarH));

            var center = new Point(cx, MeterY);
            dc.DrawEllipse(Theme.Hatch, null, center, MeterR, MeterR);
            // A note sends a ring outward from the dot while the dot swells and settles.
            double k = Math.Clamp(1 - (now - _noteAt[t]) / PulseSeconds, 0, 1), amp = 0.4 + 0.6 * _velocity[t];
            if (k > 0)
            {
                double ring = 12 + (MeterR - 12) * amp * (1 - k * k);
                dc.PushOpacity(k);
                dc.DrawEllipse(null, new Pen(Theme.White, 3), center, ring, ring);
                dc.Pop();
            }
            double dot = 10 + 14 * k * k * amp;
            dc.DrawEllipse(Theme.White, null, center, dot, dot);
        }

        Draw.HLine(dc, LeftEdge, RightEdge, Row0);
        Draw.HLine(dc, LeftEdge, RightEdge, IconRow);
        Draw.HLine(dc, LeftEdge, RightEdge, FaderBottom);
        for (int i = 0; i <= 8; i++) Draw.VLine(dc, X0 + i * ColW, Row0, BottomEdge);
    }

    /// <summary>The small track icons of the official mixer header.</summary>
    static void DrawGlyph(DrawingContext dc, int track, double x, double y)
    {
        var pen = new Pen(Theme.Mid, 3.5) { StartLineCap = PenLineCap.Flat, EndLineCap = PenLineCap.Flat };
        switch (track)
        {
            case 0: // kick: drum shell with a beater
                dc.DrawEllipse(null, pen, new Point(x + 14, y - 3), 13, 13);
                dc.DrawEllipse(Theme.Mid, null, new Point(x + 14, y - 5), 5, 5);
                dc.DrawLine(pen, new Point(x + 14, y - 2), new Point(x + 14, y + 16));
                break;
            case 1: // snare: drum on a stand
                dc.DrawRectangle(null, pen, new Rect(x + 1, y - 11, 26, 10));
                dc.DrawLine(pen, new Point(x + 14, y - 1), new Point(x + 14, y + 15));
                break;
            case 2: // perc: cymbal
            {
                dc.DrawLine(pen, new Point(x, y - 1), new Point(x + 28, y - 1));
                var g = new StreamGeometry();
                using (var c = g.Open())
                {
                    c.BeginFigure(new Point(x + 5, y - 5), true, true);
                    c.LineTo(new Point(x + 14, y - 12), true, false);
                    c.LineTo(new Point(x + 23, y - 5), true, false);
                }
                g.Freeze();
                dc.DrawGeometry(Theme.Mid, null, g);
                dc.DrawLine(pen, new Point(x + 14, y - 14), new Point(x + 14, y + 15));
                break;
            }
            case 3: // sample: waveform
            {
                var thin = new Pen(Theme.Mid, 3);
                dc.DrawLine(thin, new Point(x, y), new Point(x + 8, y));
                double[] h = [16, 26, 12, 20];
                for (int i = 0; i < 4; i++) dc.DrawLine(thin, new Point(x + 11 + i * 5, y - h[i] / 2), new Point(x + 11 + i * 5, y + h[i] / 2));
                dc.DrawLine(thin, new Point(x + 29, y), new Point(x + 37, y));
                break;
            }
            case 4: Draw.Label(dc, "B", x + 2, y, 36, Theme.Mid); break;
            case 5: // lead: a note
                dc.DrawEllipse(Theme.Mid, null, new Point(x + 7, y + 10), 6.5, 5.5);
                dc.DrawLine(pen, new Point(x + 12, y + 9), new Point(x + 12, y - 14));
                break;
            case 6: // arp: three dots
                dc.DrawEllipse(Theme.Mid, null, new Point(x + 14, y - 7), 5.5, 5.5);
                dc.DrawEllipse(Theme.Mid, null, new Point(x + 5, y + 6), 5.5, 5.5);
                dc.DrawEllipse(Theme.Mid, null, new Point(x + 23, y + 6), 5.5, 5.5);
                break;
            default: Draw.Label(dc, "CH", x + 2, y, 36, Theme.Mid); break;
        }
    }

    // ------------------------------------------------------------------ input

    static int ColumnAt(Point p) => p.X < X0 || p.X >= X0 + 8 * ColW ? -1 : (int)((p.X - X0) / ColW);

    static int GroupAt(Point p)
    {
        if (p.Y > Row0) return -1;
        for (int g = 0; g < 4; g++)
            if (p.X >= GroupX[g] - 30 && p.X < GroupX[g] + 210) return g;
        return -1;
    }

    protected override void OnPointerDown(int id, Point p)
    {
        int g = GroupAt(p);
        if (g >= 0) { _groups[id] = (g, 0); _groupEditAt[g] = Now; UpdateAnimation(); return; }
        int t = ColumnAt(p);
        if (t < 0) return;
        if (p.Y >= IconRow && p.Y < FaderBottom)
        {
            _faders[id] = (t, 0);
            _heldLevel[t] = Level(t);
            _heldUntil[t] = Now + HoldSeconds;
        }
        else if (p.Y >= FaderBottom)
        {
            bool next = !Muted(t);
            _mute[t] = (next, ReportedMute(t));
            _app.SendTrackCc(t, 53, next ? 1 : 0);
        }
    }

    protected override void OnPointerMove(int id, Point p, Vector delta)
    {
        if (_faders.TryGetValue(id, out var f))
        {
            // Full fader travel spans 0..99.
            double accum = f.Accum - delta.Y * 99 / (FaderBottom - FaderTop);
            int steps = (int)accum;
            _faders[id] = (f.Track, accum - steps);
            if (steps != 0) SetLevel(f.Track, Display.Percent((byte)Level(f.Track)) + steps);
        }
        else if (_groups.TryGetValue(id, out var g))
        {
            double accum = g.Accum + (delta.X - delta.Y) * 0.25;
            int steps = (int)accum;
            _groups[id] = (g.Group, accum - steps);
            if (steps != 0) { SetGroupLevel(g.Group, Display.FromPercent(Math.Clamp(Display.Percent(GroupLevel(g.Group)) + steps, 0, 99))); InvalidateVisual(); }
        }
    }

    protected override void OnPointerUp(int id, Point p)
    {
        if (_faders.Remove(id, out var f)) _heldUntil[f.Track] = Now + HoldSeconds;
        if (_groups.Remove(id, out var g)) _groupEditAt[g.Group] = Now;
        UpdateAnimation();
    }

    protected override void OnWheel(Point p, double notches)
    {
        int step = (int)Math.Round(notches);
        if (step == 0) return;
        int g = GroupAt(p);
        if (g >= 0)
        {
            SetGroupLevel(g, Display.FromPercent(Math.Clamp(Display.Percent(GroupLevel(g)) + step, 0, 99)));
            InvalidateVisual();
            return;
        }
        int t = ColumnAt(p);
        if (t < 0 || p.Y < IconRow || p.Y >= FaderBottom) return;
        SetLevel(t, Display.Percent((byte)Level(t)) + step);
        UpdateAnimation();
    }
}
