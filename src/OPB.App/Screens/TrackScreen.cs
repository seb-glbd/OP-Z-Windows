using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using OPB.App.Rendering;
using OPB.Core;
using OPB.Core.Model;

namespace OPB.App.Screens;

/// <summary>
/// The main "screen" for a track: sound, envelope, LFO, sends, pan, level, track settings,
/// the 16-step sequencer and the touch pad, laid out exactly like the official app.
/// </summary>
public sealed class TrackScreen : ScreenView
{
    // Layout (design units, from the Android app at 1600 x 900).
    const double TitleY = 40, Row0 = 82, Row1 = 212, Row2 = 342, EnvLabels = 428, Row3 = 472, Row4 = 602;
    const double ColLabel = 216, ColW = 130, FxLeft = 736, FxRight = 952, FxEnd = 1212, RightEnd = 1426;
    const double SeqLabelY = 622, SeqNumTop = 645, SeqDotTop = 688, SeqBottom = 732, StepX0 = 42, StepW = 86.5;

    readonly AppModel _app;
    readonly List<(Rect Rect, ParamRef Param)> _hits = new();
    readonly Dictionary<int, (Rect Rect, int Step)> _stepHits = new();
    readonly Stopwatch _playClock = new();
    readonly Rect _touchPad = new(RightEnd, Row0, ScreenView.DesignWidth - RightEnd, SeqBottom - 2 - Row0);
    int? _touchPadPointer;
    double _touchPadAccumulator;
    bool _busy;

    public TrackScreen(AppModel app) : base(app.Session)
    {
        _app = app;
        _app.SelectionChanged += InvalidateVisual;
        Session.NoteEvent += (_, _, _) => { if (Session.ClockRunning || Session.Play.Playing) UpdateAnimation(); };
    }

    int Track => Session.ActiveTrack;
    int Pattern => Session.ActivePattern;
    TrackKind Kind => Tracks.Kind(Track);
    byte S(SoundParam p) => Session.CurrentSound[Track][(int)p];

    public override string Title => Tracks.Names[Track];

    protected override bool WantsAnimation => Session.ClockRunning || Session.Play.Playing;

    protected override bool ShouldRedraw(Changes c)
    {
        if ((c & (Changes.Play | Changes.Transport)) != 0)
        {
            if (Session.Play.Playing || Session.ClockRunning) { if (!_playClock.IsRunning) _playClock.Restart(); }
            else _playClock.Reset();
        }
        return true;
    }

    // ------------------------------------------------------------------ render

    protected override void Render(DrawingContext dc)
    {
        _hits.Clear();
        _stepHits.Clear();
        var selected = _app.Selected(Track);

        Draw.Label(dc, Title, (RightEnd) / 2, TitleY, 56, Theme.Ink, Align.Center, Theme.Regular);
        Draw.Label(dc, "TOUCH PAD", RightEnd + 14, TitleY, 30, Theme.Mid);

        // Grid rules.
        Draw.HLine(dc, LeftEdge, RightEdge, Row0);
        Draw.VLine(dc, RightEnd, TopEdge, BottomEdge);
        Draw.HLine(dc, LeftEdge, RightEnd, Row1);
        Draw.HLine(dc, LeftEdge, ColLabel, Row3);
        Draw.HLine(dc, ColLabel, FxLeft, EnvLabels);
        Draw.HLine(dc, ColLabel, FxLeft, Row3);
        Draw.HLine(dc, LeftEdge, RightEdge, Row4);
        Draw.HLine(dc, FxEnd, RightEnd, Row2);
        Draw.HLine(dc, FxEnd, RightEnd, Row3);
        Draw.VLine(dc, ColLabel, Row0, Row4);
        for (int i = 1; i < 4; i++)
        {
            Draw.VLine(dc, ColLabel + i * ColW, Row0, Row1);
            Draw.VLine(dc, ColLabel + i * ColW, EnvLabels, Row4);
        }
        Draw.VLine(dc, FxLeft, Row0, Row4);
        Draw.VLine(dc, FxRight, Row0, Row4);
        Draw.VLine(dc, FxRight + ColW, Row0, Row1);
        Draw.VLine(dc, FxEnd, Row0, Row4);

        dc.DrawRectangle(Theme.Hatch, null, new Rect(RightEnd, Row0, RightEdge - RightEnd, _touchPad.Height));

        DrawSoundRow(dc);
        DrawEnvelope(dc);
        DrawLfoRow(dc);
        DrawSends(dc);
        DrawTrackSettings(dc);
        DrawSequencer(dc);

        // Active page frame, then the selected parameter on top.
        int page = Session.Ui.Page;
        var pageRect = page switch
        {
            0 => new Rect(ColLabel, Row0, 4 * ColW, Row1 - Row0),
            1 => new Rect(ColLabel, Row1, 4 * ColW, Row3 - Row1),
            2 => new Rect(ColLabel, Row3, 4 * ColW, Row4 - Row3),
            _ => new Rect(FxLeft, Row0, FxEnd - FxLeft, Row4 - Row0),
        };
        Draw.Frame(dc, pageRect, Theme.Pages[page]);
        foreach (var (rect, param) in _hits)
            if (param.Equals(selected)) { Draw.Frame(dc, rect, Theme.Red); break; }

        if (_touchPadPointer is not null)
        {
            Draw.Label(dc, selected.Label, RightEnd + 87, Row0 + 60, 28, Theme.Ink, Align.Center);
            Draw.Label(dc, selected.Text, RightEnd + 87, Row0 + 110, 48, Theme.Ink, Align.Center, Theme.Regular);
        }
    }

    /// <summary>The knob color of a parameter on page <paramref name="page"/> at dial index <paramref name="dial"/>.</summary>
    Brush DialBrush(int page, int dial) => Session.Ui.Page == page ? Theme.Dials[dial] : Theme.Mid;

    void ParamCell(DrawingContext dc, double x, double y, string label, ParamRef param, int page, int dial, double? value01 = null)
    {
        var rect = new Rect(x, y, ColW, Row1 - Row0);
        Draw.Label(dc, label, x + 8, y + 20, 30, Theme.Mid);
        Draw.Knob(dc, new Point(x + 65, y + 65), value01 ?? param.Value / 255.0, DialBrush(page, dial));
        Draw.Label(dc, param.Text, x + 65, y + 107, 32, Theme.Ink, Align.Center, Theme.Regular);
        _hits.Add((rect, param));
    }

    ParamRef Snd(SoundParam p, string label, Func<int, string>? format = null) => ParamRef.Sound(Session, Track, p, label, format);

    string[] PageOneLabels() => Kind switch
    {
        TrackKind.Master => ["CHORUS", "DRIVE", "FILTER", "RES"],
        TrackKind.Lights => ["COLOR", "ALT COL", "SPEED", "INTENS"],
        TrackKind.Motion => ["HUE", "SAT", "BRIGHT", "CONTR"],
        TrackKind.Tape => ["SPEED", "FINE", "FILTER", "RES"],
        _ => ["P1", "P2", "FILTER", "RES"],
    };

    void DrawSoundRow(DrawingContext dc)
    {
        Draw.Label(dc, Kind == TrackKind.Fx ? "EFFECT" : "SOUND", 20, Row0 + 42, 30, Theme.Mid);
        string name = PlugNames.Get(Session, Track);
        Draw.Label(dc, Fit(name, 190, 32), 20, Row0 + 85, 32, Theme.Ink, Align.Left, Theme.Regular);
        var labels = PageOneLabels();
        SoundParam[] ps = [SoundParam.P1, SoundParam.P2, SoundParam.Filter, SoundParam.Resonance];
        for (int i = 0; i < 4; i++)
        {
            var p = ps[i];
            Func<int, string>? fmt = (Kind == TrackKind.Master && p == SoundParam.Filter) ? FormatBipolar : null;
            ParamCell(dc, ColLabel + i * ColW, Row0, labels[i], Snd(p, labels[i], fmt), 0, i);
        }
        if (Kind == TrackKind.Lights)
        {
            // Color swatches for main and alt color.
            for (int i = 0; i < 2; i++)
            {
                var hue = S(i == 0 ? SoundParam.P1 : SoundParam.P2) / 255.0 * 360;
                dc.DrawRectangle(new SolidColorBrush(Hsv(hue, 0.85, 0.9)), null, new Rect(ColLabel + i * ColW + 92, Row0 + 10, 28, 20));
            }
        }
    }

    static string FormatBipolar(int v)
    {
        int c = (int)Math.Round((v - 128) / 128.0 * 50);
        return c == 0 ? "0" : c < 0 ? $"LP {-c}" : $"HP {c}";
    }

    void DrawEnvelope(DrawingContext dc)
    {
        bool drum = Kind == TrackKind.Drum;
        bool hasEnvelope = Kind is TrackKind.Drum or TrackKind.Synth or TrackKind.Module;
        Draw.Label(dc, "ENVELOPE", 20, Row1 + 42, 30, Theme.Mid);
        var area = new Rect(ColLabel, Row1, 4 * ColW, EnvLabels - Row1);
        if (!hasEnvelope)
        {
            dc.DrawRectangle(Theme.Hatch, null, new Rect(area.X + 1, area.Y + 1, area.Width - 2, Row3 - Row1 - 2));
            return;
        }

        string[] letters = drum ? ["S", "A", "H", "D"] : ["A", "D", "S", "R"];
        SoundParam[] ps = [SoundParam.Attack, SoundParam.Decay, SoundParam.Sustain, SoundParam.Release];
        for (int i = 0; i < 4; i++)
        {
            double cx = ColLabel + i * ColW + ColW / 2;
            var param = Snd(ps[i], letters[i] switch { "S" => drum ? "START" : "SUSTAIN", "A" => "ATTACK", "H" => "HOLD", "D" => "DECAY", _ => "RELEASE" });
            string value = param.Text;
            var lw = Draw.Text(letters[i], 30, Theme.Mid).Width;
            var vw = Draw.Text(value, 30, Theme.Ink, Theme.Regular).Width;
            double left = cx - (lw + 10 + vw) / 2;
            Draw.Label(dc, letters[i], left, EnvLabels + 22, 30, DialBrush(1, i));
            Draw.Label(dc, value, left + lw + 10, EnvLabels + 22, 30, Theme.Ink, Align.Left, Theme.Regular);
            _hits.Add((new Rect(ColLabel + i * ColW, EnvLabels, ColW, Row3 - EnvLabels), param));
        }

        // Graph: x 260..692, y 255 (top) .. 385 (bottom).
        double x0 = 260, x1 = 692, top = Row1 + 43, bottom = Row1 + 173, w = x1 - x0;
        var linePen = new Pen(Theme.Mid, 4) { LineJoin = PenLineJoin.Round };
        Brush handle = Session.Ui.Page == 1 ? Theme.Ink : Theme.Mid;
        double a = S(SoundParam.Attack) / 255.0, d = S(SoundParam.Decay) / 255.0, s = S(SoundParam.Sustain) / 255.0, r = S(SoundParam.Release) / 255.0;
        var geo = new StreamGeometry();
        Point[] handles;
        using (var g = geo.Open())
        {
            if (drum)
            {
                // Start delay, attack ramp, hold, decay curve.
                double xs = x0 + a * w * 0.22, xa = xs + d * w * 0.22, xd = x1 - r * w * 0.27;
                double xh = Math.Max(xa, xa + (xd - xa) * s);
                xd = Math.Max(xh, xd);
                g.BeginFigure(new Point(x0, bottom), false, false);
                g.LineTo(new Point(xs, bottom), true, true);
                g.LineTo(new Point(xa, top), true, true);
                g.LineTo(new Point(xh, top), true, true);
                g.BezierTo(new Point(xh + (x1 - xh) * 0.55, top), new Point(x1, top + (bottom - top) * 0.45), new Point(x1, bottom), true, true);
                g.LineTo(new Point(x0, bottom), true, true);
                handles = [new(xs, bottom), new(xa, top), new(xh, top), new(x1, bottom)];
                dc.DrawLine(new Pen(Theme.Mid, 3) { DashStyle = new DashStyle([3, 3], 0) }, new Point(xh, top + 10), new Point(xh, bottom));
            }
            else
            {
                double xa = x0 + a * w * 0.3, xd = xa + d * w * 0.3, ys = bottom - s * (bottom - top);
                double xr = x1 - r * w * 0.3;
                xr = Math.Max(xr, xd);
                g.BeginFigure(new Point(x0, bottom), false, false);
                g.LineTo(new Point(xa, top), true, true);
                g.BezierTo(new Point(xa + (xd - xa) * 0.3, ys - (ys - top) * 0.1), new Point(xd - (xd - xa) * 0.2, ys), new Point(xd, ys), true, true);
                g.LineTo(new Point(xr, ys), true, true);
                g.BezierTo(new Point(xr + (x1 - xr) * 0.4, ys), new Point(x1, bottom - (bottom - ys) * 0.3), new Point(x1, bottom), true, true);
                handles = [new(xa, top), new(xd, ys), new(xr, ys), new(x1, bottom)];
                dc.DrawLine(new Pen(Theme.Mid, 3) { DashStyle = new DashStyle([3, 3], 0) }, new Point(xr, ys + 10), new Point(xr, bottom));
                dc.DrawLine(linePen, new Point(x0, bottom), new Point(x1, bottom));
            }
        }
        geo.Freeze();
        dc.DrawGeometry(null, linePen, geo);
        // One handle per envelope parameter, colored like the encoder that turns it.
        for (int i = 0; i < handles.Length; i++) Draw.Handle(dc, handles[i], DialBrush(1, i));
        if (drum) Draw.Handle(dc, new Point(x0, bottom), handle);
    }

    void DrawLfoRow(DrawingContext dc)
    {
        bool arp = (TrackId)Track == TrackId.Arp;
        Draw.Label(dc, arp ? "ARP" : "LFO", 20, Row3 + 42, 30, Theme.Mid);
        double y = Row3;
        if (arp)
        {
            string[] patterns = ["MANUAL", "UP", "DOWN", "UP/DN", "DN/UP", "RANDOM"];
            ParamCell(dc, ColLabel, y, "SPEED", Snd(SoundParam.LfoDepth, "SPEED", v => v < 16 ? "OFF" : Display.Band((byte)v, ["1/1", "1/2", "1/4", "1/8", "1/16", "1/32", "1/64", "1/128"])), 2, 0);
            TextCell(dc, ColLabel + ColW, y, "PATTERN", Snd(SoundParam.LfoSpeed, "PATTERN", v => Display.Band((byte)v, patterns)), 2, 1);
            TextCell(dc, ColLabel + 2 * ColW, y, "STYLE", Snd(SoundParam.LfoValue, "STYLE", v => (Display.BandIndex((byte)v, 6) + 1).ToString()), 2, 2);
            ParamCell(dc, ColLabel + 3 * ColW, y, "RANGE", Snd(SoundParam.LfoShape, "RANGE", v => (Display.BandIndex((byte)v, 4) + 1).ToString()), 2, 3);
            return;
        }
        ParamCell(dc, ColLabel, y, "DEPTH", Snd(SoundParam.LfoDepth, "DEPTH", v => Display.Percent((byte)v).ToString()), 2, 0);
        ParamCell(dc, ColLabel + ColW, y, "RATE", Snd(SoundParam.LfoSpeed, "RATE"), 2, 1);

        // Destination: a dot and the target name.
        var dest = Snd(SoundParam.LfoValue, "DEST", v => Display.Band((byte)v, Display.LfoDestinations));
        double dx = ColLabel + 2 * ColW;
        Draw.Label(dc, "DEST", dx + 8, y + 20, 30, Theme.Mid);
        dc.DrawEllipse(DialBrush(2, 2), null, new Point(dx + 65, y + 65), 11, 11);
        Draw.Label(dc, dest.Text, dx + 65, y + 107, 32, Theme.Ink, Align.Center, Theme.Regular);
        _hits.Add((new Rect(dx, y, ColW, Row4 - Row3), dest));

        // Shape: a small drawing of the waveform and its index.
        var shape = Snd(SoundParam.LfoShape, "SHAPE", v => Display.Band((byte)v, Display.LfoShapes));
        double sx = ColLabel + 3 * ColW;
        Draw.Label(dc, "SHAPE", sx + 8, y + 20, 30, Theme.Mid);
        DrawLfoShape(dc, Display.BandIndex(S(SoundParam.LfoShape), 12), new Rect(sx + 20, y + 42, 90, 42), DialBrush(2, 3));
        Draw.Label(dc, Display.BandIndex(S(SoundParam.LfoShape), 12).ToString(), sx + 65, y + 107, 32, Theme.Ink, Align.Center, Theme.Regular);
        _hits.Add((new Rect(sx, y, ColW, Row4 - Row3), shape));
    }

    void TextCell(DrawingContext dc, double x, double y, string label, ParamRef param, int page, int dial)
    {
        Draw.Label(dc, label, x + 8, y + 20, 30, Theme.Mid);
        dc.DrawEllipse(DialBrush(page, dial), null, new Point(x + 65, y + 65), 11, 11);
        Draw.Label(dc, param.Text, x + 65, y + 107, 30, Theme.Ink, Align.Center, Theme.Regular);
        _hits.Add((new Rect(x, y, ColW, Row4 - Row3), param));
    }

    static void DrawLfoShape(DrawingContext dc, int index, Rect r, Brush brush)
    {
        var pen = new Pen(brush, 4) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            double l = r.Left, rt = r.Right, t = r.Top, b = r.Bottom, m = (t + b) / 2, cx = (l + rt) / 2;
            switch (index % 6)
            {
                case 0: // sine
                    c.BeginFigure(new Point(l, m), false, false);
                    c.BezierTo(new Point(l + r.Width * 0.18, t - 6), new Point(cx - r.Width * 0.12, t - 6), new Point(cx, m), true, true);
                    c.BezierTo(new Point(cx + r.Width * 0.12, b + 6), new Point(rt - r.Width * 0.18, b + 6), new Point(rt, m), true, true);
                    break;
                case 1: // triangle
                    c.BeginFigure(new Point(l, b), false, false);
                    c.LineTo(new Point(cx, t), true, true);
                    c.LineTo(new Point(rt, b), true, true);
                    break;
                case 2: // square
                    c.BeginFigure(new Point(l, b), false, false);
                    c.LineTo(new Point(l, t), true, true);
                    c.LineTo(new Point(cx, t), true, true);
                    c.LineTo(new Point(cx, b), true, true);
                    c.LineTo(new Point(rt, b), true, true);
                    break;
                case 3: // saw
                    c.BeginFigure(new Point(l, b), false, false);
                    c.LineTo(new Point(rt, t), true, true);
                    c.LineTo(new Point(rt, b), true, true);
                    break;
                case 4: // random
                    c.BeginFigure(new Point(l, m), false, false);
                    double[] ys = [m, t, t, b, b, m + 6, m + 6, t + 8, t + 8, m];
                    for (int i = 1; i < ys.Length; i++)
                        c.LineTo(new Point(l + r.Width * ((i + 1) / 2) / 5.0, ys[i]), true, true);
                    break;
                default: // gyro
                    c.BeginFigure(new Point(cx + r.Height / 2, m), false, false);
                    c.ArcTo(new Point(cx - r.Height / 2, m), new Size(r.Height / 2, r.Height / 2), 0, false, SweepDirection.Clockwise, true, true);
                    c.ArcTo(new Point(cx + r.Height / 2, m), new Size(r.Height / 2, r.Height / 2), 0, false, SweepDirection.Clockwise, true, true);
                    break;
            }
        }
        g.Freeze();
        dc.DrawGeometry(null, pen, g);
        if (index >= 6) dc.DrawEllipse(brush, null, new Point(r.Left - 6, r.Bottom), 4, 4); // triggered variant
    }

    void DrawSends(DrawingContext dc)
    {
        if (Kind is TrackKind.Fx or TrackKind.Lights)
        {
            dc.DrawRectangle(Theme.Hatch, null, new Rect(FxLeft + 1, Row0 + 1, FxEnd - FxLeft - 2, Row4 - Row0 - 2));
            return;
        }
        Draw.Label(dc, "FX SEND", FxLeft + 18, Row0 + 42, 30, Theme.Mid);
        ParamCell(dc, FxRight, Row0, "1", Snd(SoundParam.Fx1Send, "FX SEND 1"), 3, 0);
        ParamCell(dc, FxRight + ColW, Row0, "2", Snd(SoundParam.Fx2Send, "FX SEND 2"), 3, 1);

        // Pan: L (knob) R, the knob slides with the value.
        var pan = Snd(SoundParam.Pan, "PAN", v => v == 128 ? "C" : v < 128 ? $"L{Display.Percent((byte)((128 - v) * 2 - 1))}" : $"R{Display.Percent((byte)((v - 128) * 2 + 1))}");
        Draw.Label(dc, "PAN", FxLeft + 18, Row1 + 42, 30, Theme.Mid);
        double pcx = FxRight + ColW, pcy = Row1 + 64;
        Draw.Label(dc, "L", pcx - 65, pcy, 30, Theme.Ink, Align.Center);
        Draw.Label(dc, "R", pcx + 65, pcy, 30, Theme.Ink, Align.Center);
        double px = pcx + (S(SoundParam.Pan) - 128) / 128.0 * 42;
        var panBrush = DialBrush(3, 2);
        dc.DrawLine(new Pen(panBrush, 3.5), new Point(pcx, pcy - 42), new Point(pcx, pcy - 22));
        dc.DrawEllipse(panBrush, null, new Point(px, pcy), 22, 22);
        Draw.Cross(dc, new Point(px, pcy), 11);
        _hits.Add((new Rect(FxRight, Row1, FxEnd - FxRight, Row2 - Row1), pan));

        // Level: vertical slider.
        var level = Snd(SoundParam.Level, "LEVEL");
        Draw.Label(dc, "LEVEL", FxLeft + 18, Row2 + 42, 30, Theme.Mid);
        double lx = FxRight + ColW, ltop = Row2 + 30, lbottom = Row2 + 193;
        double ly = lbottom - S(SoundParam.Level) / 255.0 * (lbottom - ltop);
        var levelBrush = DialBrush(3, 3);
        dc.DrawLine(new Pen(Theme.Rule, 3.5), new Point(lx, ltop - 8), new Point(lx, lbottom));
        dc.DrawLine(new Pen(levelBrush, 3.5), new Point(lx, ly), new Point(lx, lbottom));
        dc.DrawEllipse(levelBrush, null, new Point(lx, ly), 16, 16);
        Draw.Cross(dc, new Point(lx, ly), 8);
        Draw.Label(dc, level.Text, lx, lbottom + 32, 32, Theme.Ink, Align.Center, Theme.Regular);
        _hits.Add((new Rect(FxRight, Row2, FxEnd - FxRight, Row4 - Row2), level));
    }

    void DrawTrackSettings(DrawingContext dc)
    {
        bool drum = Kind == TrackKind.Drum;
        var bank = Session.Bank;
        string[] styles = Kind switch
        {
            TrackKind.Drum => Display.DrumNoteStyles,
            TrackKind.Master => ["LATCH", "FREE"],
            _ => Display.SynthNoteStyles,
        };
        var noteLength = new ParamRef($"notelength:{Track}", "NOTE LENGTH",
            () => bank?.NoteLength(Pattern, Track) ?? 0,
            v => { _app.SendTrackCc(Track, 63, v / 2); bank?.SetNoteLength(Pattern, Track, (byte)v); InvalidateVisual(); },
            v => Display.Band((byte)v, ["1/64", "1/32", "1/16", "1/8", "1/4", "1/2", "1", "DRONE"]));
        var noteStyle = Snd(SoundParam.NoteStyle, "NOTE STYLE", v => Display.Band((byte)v, styles));
        var quantize = new ParamRef($"quantize:{Track}", "QUANTIZE",
            () => bank?.Quantize(Pattern, Track) ?? 0,
            v => { _app.SendTrackCc(Track, 62, v / 2); bank?.SetQuantize(Pattern, Track, (byte)v); InvalidateVisual(); },
            v => Display.Percent((byte)v).ToString());
        string fourth = Kind is TrackKind.Tape or TrackKind.Module ? "DRY LEVEL" : "PORTAMENTO";
        var portamento = Snd(SoundParam.Portamento, fourth);

        ParamRef[] items = [noteLength, noteStyle, quantize, portamento];
        double[] rows = [Row0, Row1, Row2, Row3];
        for (int i = 0; i < 4; i++)
        {
            Draw.Label(dc, items[i].Label, FxEnd + 18, rows[i] + 42, 30, Theme.Mid);
            Draw.Label(dc, items[i].Text, FxEnd + 106, rows[i] + 90, 42, Theme.Ink, Align.Center, Theme.Regular);
            _hits.Add((new Rect(FxEnd, rows[i], RightEnd - FxEnd, 130), items[i]));
        }
        _ = drum;
    }

    void DrawSequencer(DrawingContext dc)
    {
        Draw.Label(dc, "SEQUENCER", 30, SeqLabelY, 30, Theme.Mid);
        Draw.HLine(dc, LeftEdge, RightEnd, SeqNumTop);
        dc.DrawRectangle(Theme.Rule, null, new Rect(LeftEdge, SeqDotTop, RightEnd - LeftEdge, SeqBottom - SeqDotTop));
        Draw.HLine(dc, LeftEdge, RightEdge, SeqBottom);

        var bank = Session.Bank;
        int count = bank?.StepCount(Pattern, Track) ?? 16;
        int length = bank?.StepLength(Pattern, Track) ?? 1;
        int playStep = CurrentStep(count, length);

        for (int step = 0; step < 16; step++)
        {
            double x = StepX0 + step * StepW;
            bool beyond = step >= count;
            var sepPen = step % 4 == 0 ? new Pen(Theme.Ink, 2) : Theme.RulePen;
            if (step > 0) Draw.VLine(dc, x, SeqNumTop, SeqDotTop, sepPen);
            Draw.Label(dc, (step + 1).ToString(), x + 4, SeqNumTop + 22, 40, beyond ? Theme.Rule : Theme.Mid);
            var center = new Point(x + StepW / 2 - 21, SeqDotTop + 22);
            bool trig = bank?.HasTrig(Pattern, Track, step) == true;
            if (beyond)
                dc.DrawEllipse(Theme.Background, null, center, 11, 11);
            else
                dc.DrawEllipse(trig ? Theme.Ink : Theme.Mid, null, center, 11, 11);
            if (bank is not null && !beyond)
            {
                if (bank.HasAnyLock(Pattern, Track, step))
                    dc.DrawEllipse(null, new Pen(Theme.Red, 3), center, 16, 16);
                if (bank.ComponentMask(Pattern, Track, step) != 0)
                    dc.DrawEllipse(Theme.Green, null, new Point(center.X + 26, center.Y - 10), 5, 5);
            }
            _stepHits[step] = (new Rect(x, SeqNumTop, StepW, SeqBottom - SeqNumTop), step);
        }

        if (playStep >= 0)
            Draw.Frame(dc, new Rect(StepX0 + playStep * StepW - 2, SeqNumTop - 2, StepW + 2, SeqBottom - SeqNumTop + 4), Theme.Yellow);
        else
            Draw.Frame(dc, new Rect(StepX0 - 2, SeqNumTop - 2, StepW + 2, SeqBottom - SeqNumTop + 4), Theme.Yellow);

        Draw.Label(dc, "STEP COUNT", 52, SeqBottom + 40, 32, Theme.Mid);
        Draw.Label(dc, count.ToString(), 247, SeqBottom + 40, 42, Theme.Ink, Align.Left, Theme.Regular);
        Draw.Label(dc, "STEP LENGTH", 398, SeqBottom + 40, 32, Theme.Mid);
        Draw.Label(dc, length.ToString(), 592, SeqBottom + 40, 42, Theme.Ink, Align.Left, Theme.Regular);
        Draw.Label(dc, "SUM", 1315, SeqBottom + 40, 32, Theme.Mid, Align.Right);
        Draw.Label(dc, (count * length).ToString(), 1350, SeqBottom + 40, 42, Theme.Green, Align.Left, Theme.Regular);
        _hits.Add((new Rect(0, SeqBottom, 360, DesignHeight - SeqBottom), new ParamRef($"stepcount:{Track}", "STEP COUNT",
            () => count, v => { _app.SendTrackCc(Track, 60, v); bank?.SetStepCount(Pattern, Track, v); InvalidateVisual(); }, v => v.ToString(), 1, 16, 0.05)));
        _hits.Add((new Rect(360, SeqBottom, 360, DesignHeight - SeqBottom), new ParamRef($"steplength:{Track}", "STEP LENGTH",
            () => length, v => { _app.SendTrackCc(Track, 61, v); bank?.SetStepLength(Pattern, Track, v); InvalidateVisual(); }, v => v.ToString(), 1, 16, 0.05)));
    }

    /// <summary>The playing step of the active track, from MIDI clock or (without clock) the tempo.</summary>
    int CurrentStep(int count, int length)
    {
        if (!Session.ClockRunning && !Session.Play.Playing) return -1;
        long sixteenths;
        if (Session.ClockTicks > 0)
            sixteenths = Session.ClockTicks / 6;
        else
        {
            double bpm = Session.Global?.Tempo ?? 120;
            sixteenths = (long)(_playClock.Elapsed.TotalMinutes * bpm * 4);
        }
        return (int)(sixteenths / Math.Max(1, length) % Math.Max(1, count));
    }

    static string Fit(string text, double width, double size)
    {
        while (text.Length > 1 && Draw.Text(text, size, Theme.Ink, Theme.Regular).Width > width) text = text[..^1];
        return text;
    }

    static Color Hsv(double h, double s, double v)
    {
        double c = v * s, x = c * (1 - Math.Abs(h / 60 % 2 - 1)), m = v - c;
        (double r, double g, double b) = (h % 360) switch
        {
            < 60 => (c, x, 0.0), < 120 => (x, c, 0.0), < 180 => (0.0, c, x),
            < 240 => (0.0, x, c), < 300 => (x, 0.0, c), _ => (c, 0.0, x),
        };
        return Color.FromRgb((byte)((r + m) * 255), (byte)((g + m) * 255), (byte)((b + m) * 255));
    }

    // ------------------------------------------------------------------ input

    protected override void OnPointerDown(int id, Point p)
    {
        if (_touchPad.Contains(p))
        {
            _touchPadPointer = id;
            _touchPadAccumulator = 0;
            return;
        }
        foreach (var (rect, param) in _hits)
            if (rect.Contains(p)) { _app.Select(Track, param); return; }
        foreach (var (rect, step) in _stepHits.Values)
            if (rect.Contains(p)) { _ = ToggleStepAsync(step); return; }
    }

    protected override void OnPointerMove(int id, Point p, Vector delta)
    {
        if (id != _touchPadPointer) return;
        var param = _app.Selected(Track);
        // Up and right both increase; the pad spans ~650 units for the full 0..255 range.
        _touchPadAccumulator += (-delta.Y + delta.X * 0.5) * 0.4 * param.Sensitivity;
        int steps = (int)_touchPadAccumulator;
        if (steps == 0) return;
        _touchPadAccumulator -= steps;
        param.Set(param.Value + steps);
        InvalidateVisual();
    }

    protected override void OnPointerUp(int id, Point p)
    {
        if (id == _touchPadPointer) _touchPadPointer = null;
    }

    protected override void OnWheel(Point p, double notches)
    {
        foreach (var (rect, param) in _hits)
        {
            if (!rect.Contains(p)) continue;
            _app.Select(Track, param);
            param.Set(param.Value + (int)Math.Round(notches * Math.Max(1, 4 * param.Sensitivity)));
            InvalidateVisual();
            return;
        }
        if (_touchPad.Contains(p))
        {
            var param = _app.Selected(Track);
            param.Set(param.Value + (int)Math.Round(notches * Math.Max(1, 4 * param.Sensitivity)));
            InvalidateVisual();
        }
    }

    /// <summary>Adds or removes a trig by editing the pattern bank (uploaded with readback and rollback).</summary>
    async Task ToggleStepAsync(int step)
    {
        if (_busy || Session.Bank is null || !Session.IsConnected) return;
        _busy = true;
        int track = Track, pattern = Pattern;
        try
        {
            await Session.EditBankAsync(bank =>
            {
                if (bank.HasTrig(pattern, track, step))
                    bank.SetNotes(pattern, track, step, []);
                else
                    bank.SetNotes(pattern, track, step, [new Note(PatternBank.DurationPerStep, DefaultPitch(bank, pattern, track), 100, 0)]);
            });
        }
        finally
        {
            _busy = false;
            InvalidateVisual();
        }
    }

    /// <summary>Uses the most recent pitch already on the track, otherwise middle C (the first drum sample).</summary>
    static byte DefaultPitch(PatternBank bank, int pattern, int track)
    {
        for (int step = 0; step < 16; step++)
        {
            var notes = bank.Notes(pattern, track, step);
            if (notes.Count > 0) return notes[0].Pitch;
        }
        return Tracks.Kind(track) == TrackKind.Drum ? (byte)53 : (byte)60;
    }
}
