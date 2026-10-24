using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using OPB.App.Rendering;
using OPB.Core;
using OPB.Core.Model;

namespace OPB.App.Screens;

/// <summary>
/// Tempo (hold TEMPO): a metronome whose pendulum swings with the beat and whose weight
/// slides with the BPM. Dragging up/down anywhere (or the wheel) sets 40-200 BPM; swing and
/// the metronome click have small readouts on the sides.
/// </summary>
[Screen(ScreenKeys.Tempo)]
public sealed class TempoScreen : ScreenView
{
    const double TitleY = 40, Row0 = 82, TopY = 139, BaseY = 564, BottomY = 758, TopHalf = 41, BottomHalf = 195;
    const double PivotX = 800, RodLength = 400, MaxSwing = 30, BpmY = 661;

    enum Target { Tempo, Swing, MetronomeLevel, MetronomeSound }

    static readonly Rect SwingRect = new(0, 220, 420, 220);
    static readonly Rect LevelRect = new(1180, 220, 420, 150);
    static readonly Rect SoundRect = new(1180, 370, 420, 110);
    static readonly Brush Weight = Theme.Freeze(new SolidColorBrush(Color.FromRgb(0x7A, 0x7A, 0x7A)));

    readonly Stopwatch _clock = Stopwatch.StartNew();
    readonly Dictionary<int, (Target Target, double Accum, double Travel)> _drags = new();
    double _beats, _lastFrame = -1, _tickTime;
    long _lastTicks = -1;

    public TempoScreen(AppModel app) : base(app.Session) { }

    public override string Title => "TEMPO";

    int Tempo => Session.Global?.Tempo ?? 120;
    bool Playing => Session.ClockRunning || Session.Play.Playing;

    protected override bool WantsAnimation => Playing;

    protected override bool ShouldRedraw(Changes c)
    {
        if ((c & (Changes.Play | Changes.Transport)) != 0 && !Playing) { _beats = 0; _lastFrame = -1; }
        return true;
    }

    protected override void OnFrame(TimeSpan time)
    {
        double now = _clock.Elapsed.TotalSeconds;
        double dt = _lastFrame < 0 ? 0 : Math.Clamp(now - _lastFrame, 0, 0.1);
        _lastFrame = now;
        long ticks = Session.ClockTicks;
        if (Session.ClockRunning && ticks > 0)
        {
            // 24 PPQN clock, interpolated between ticks so the pendulum moves smoothly.
            if (ticks != _lastTicks) { _lastTicks = ticks; _tickTime = now; }
            double frac = Math.Min(1, (now - _tickTime) * Tempo * 24 / 60);
            _beats = (ticks + frac) / 24.0;
        }
        else _beats += dt * Tempo / 60;
    }

    // ------------------------------------------------------------------ render

    protected override void Render(DrawingContext dc)
    {
        Draw.Label(dc, "TEMPO", DesignWidth / 2, TitleY, 56, Theme.Ink, Align.Center, Theme.Regular);
        Draw.HLine(dc, LeftEdge, RightEdge, Row0);

        var body = new StreamGeometry();
        using (var g = body.Open())
        {
            g.BeginFigure(new Point(PivotX - TopHalf, TopY), true, true);
            g.LineTo(new Point(PivotX + TopHalf, TopY), true, false);
            g.LineTo(new Point(PivotX + BottomHalf, BottomY), true, false);
            g.LineTo(new Point(PivotX - BottomHalf, BottomY), true, false);
        }
        body.Freeze();
        dc.DrawGeometry(Theme.Ink, null, body);
        var light = new Pen(Theme.Background, 3);
        double half = TopHalf + (BaseY - TopY) / (BottomY - TopY) * (BottomHalf - TopHalf);
        dc.DrawLine(light, new Point(PivotX, TopY), new Point(PivotX, BaseY));
        dc.DrawLine(light, new Point(PivotX - half, BaseY), new Point(PivotX + half, BaseY));
        Draw.Label(dc, Tempo.ToString(), PivotX, BpmY, 64, Theme.Background, Align.Center, Theme.Regular);

        // Pendulum: one beat per side, at the extremes on the beat.
        double angle = Playing ? -MaxSwing * Math.Cos(Math.PI * _beats) : -MaxSwing;
        var pivot = new Point(PivotX, BaseY);
        dc.PushTransform(new RotateTransform(angle, pivot.X, pivot.Y));
        dc.DrawRectangle(Theme.Red, null, new Rect(PivotX - 5, BaseY - RodLength, 10, RodLength));
        double weightY = BaseY - WeightDistance(Tempo);
        var w = new StreamGeometry();
        using (var g = w.Open())
        {
            g.BeginFigure(new Point(PivotX - 33, weightY - 22), true, true);
            g.LineTo(new Point(PivotX + 33, weightY - 22), true, false);
            g.LineTo(new Point(PivotX + 24, weightY + 22), true, false);
            g.LineTo(new Point(PivotX - 24, weightY + 22), true, false);
        }
        w.Freeze();
        dc.DrawGeometry(Weight, null, w);
        dc.DrawEllipse(Theme.Ink, null, new Point(PivotX - 18, weightY - 4), 3, 3);
        dc.DrawEllipse(Theme.Ink, null, new Point(PivotX + 18, weightY - 4), 3, 3);
        dc.DrawLine(new Pen(Theme.Ink, 3), new Point(PivotX - 4, weightY - 10), new Point(PivotX - 4, weightY + 6));
        dc.DrawLine(new Pen(Theme.Background, 3), new Point(PivotX + 4, weightY - 14), new Point(PivotX + 4, weightY + 2));
        dc.Pop();

        DrawReadouts(dc);
    }

    /// <summary>Distance of the sliding weight from the pivot: higher tempo, lower weight.</summary>
    static double WeightDistance(int bpm) => 340 - (Math.Clamp(bpm, 40, 200) - 40) / 160.0 * 250;

    void DrawReadouts(DrawingContext dc)
    {
        var g = Session.Global;
        string swing = g is null ? "-" : $"{(int)Math.Round(g.Swing * 100 / 255.0)}%";
        Draw.Label(dc, "SWING", 60, 300, 30, Theme.Mid);
        Draw.Label(dc, swing, 60, 350, 40, Theme.Ink, Align.Left, Theme.Regular);

        double right = DesignWidth - 60;
        string level = g is null ? "-" : g.MetronomeLevel == 0 ? "OFF" : Display.Percent(g.MetronomeLevel).ToString();
        string sound = g is null ? "-" : Display.MetronomeSounds[Math.Min(g.MetronomeSound, Display.MetronomeSounds.Length - 1)];
        Draw.Label(dc, "METRONOME", right, 300, 30, Theme.Mid, Align.Right);
        Draw.Label(dc, level, right, 350, 40, Theme.Ink, Align.Right, Theme.Regular);
        Draw.Label(dc, sound, right, 410, 30, Theme.Mid, Align.Right);
    }

    // ------------------------------------------------------------------ input

    static Target TargetAt(Point p) =>
        SwingRect.Contains(p) ? Target.Swing : LevelRect.Contains(p) ? Target.MetronomeLevel : SoundRect.Contains(p) ? Target.MetronomeSound : Target.Tempo;

    void Change(Target target, int steps)
    {
        var g = Session.Global;
        if (g is null || steps == 0) return;
        switch (target)
        {
            case Target.Tempo:
                Session.EditGlobal(x => x.Tempo = Math.Clamp(g.Tempo + steps, 40, 200));
                break;
            case Target.Swing:
                int swing = Math.Clamp((int)Math.Round(g.Swing * 100 / 255.0) + steps, 0, 100);
                Session.EditGlobal(x => x.Swing = (byte)Math.Round(swing * 255 / 100.0));
                break;
            case Target.MetronomeLevel:
                Session.EditGlobal(x => x.MetronomeLevel = Display.FromPercent(Math.Clamp(Display.Percent(g.MetronomeLevel) + steps, 0, 99)));
                break;
            case Target.MetronomeSound:
                int n = Display.MetronomeSounds.Length;
                Session.EditGlobal(x => x.MetronomeSound = (byte)(((g.MetronomeSound + steps) % n + n) % n));
                break;
        }
        InvalidateVisual();
    }

    protected override void OnPointerDown(int id, Point p) => _drags[id] = (TargetAt(p), 0, 0);

    protected override void OnPointerMove(int id, Point p, Vector delta)
    {
        if (!_drags.TryGetValue(id, out var d)) return;
        double perPixel = d.Target == Target.MetronomeSound ? 1 / 40.0 : 0.2;
        double accum = d.Accum - delta.Y * perPixel;
        int steps = (int)accum;
        _drags[id] = (d.Target, accum - steps, d.Travel + delta.Length);
        Change(d.Target, steps);
    }

    protected override void OnPointerUp(int id, Point p)
    {
        // A tap on the metronome sound steps to the next one.
        if (_drags.Remove(id, out var d) && d.Target == Target.MetronomeSound && d.Travel < 12) Change(Target.MetronomeSound, 1);
    }

    protected override void OnWheel(Point p, double notches) => Change(TargetAt(p), (int)Math.Round(notches));
}
