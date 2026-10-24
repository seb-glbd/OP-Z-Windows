using System.Windows;
using System.Windows.Media;
using OPB.App.Rendering;
using OPB.App.Video;
using OPB.Core.Model;

namespace OPB.App.Screens;

/// <summary>
/// A screen built around a live visual (the "stage"), hosted as a WPF child at <see cref="StageRect"/> in the
/// 1600 x 812 design space. Video out moves the stage to another display (or full screen) and the screen then
/// shows a live preview. Also holds the chrome shared by Photomatic and Motion: the knob column, buttons,
/// press/drag hit regions and the black name bar whose sides toggle video out.
/// </summary>
public abstract class StageScreen : ScreenView
{
    public const double LeftW = 216, RightX = 1384, HeaderY = 82, BarY = 744;
    public const int MotionTrack = 15;

    sealed record Hit(Rect Rect, Action? Down, Action? Up, Action<Vector>? Drag, Action<double>? Wheel);

    readonly List<Hit> _hits = new();
    readonly Dictionary<int, (Hit Hit, Point Start)> _active = new();
    bool _hosted;

    protected StageScreen(AppModel app, FrameworkElement stage) : base(app.Session)
    {
        App = app;
        Stage = stage;
        VideoOut = new VideoOut(stage, Attach, Detach);
        VideoOut.Changed += () => { InvalidateVisual(); UpdateAnimation(); };
        Attach();
        // Diagnostics: --video-out [window] starts video out when the page opens.
        var args = Environment.GetCommandLineArgs();
        int i = Array.IndexOf(args, "--video-out");
        if (i >= 0)
            Loaded += (_, _) => { if (!VideoOut.IsActive) Dispatcher.BeginInvoke(() => VideoOut.Toggle(this, i + 1 < args.Length && args[i + 1] == "window")); };
    }

    protected AppModel App { get; }
    protected FrameworkElement Stage { get; }
    protected VideoOut VideoOut { get; }

    /// <summary>Where the stage sits, in design units.</summary>
    protected abstract Rect StageRect { get; }

    protected abstract void RenderScreen(DrawingContext dc);

    /// <summary>Call when <see cref="StageRect"/> changes or the stage should be hidden behind an overlay.</summary>
    protected void UpdateStage(bool visible = true)
    {
        Stage.Visibility = visible ? Visibility.Visible : Visibility.Hidden;
        InvalidateArrange();
        InvalidateVisual();
    }

    void Attach()
    {
        if (_hosted) return;
        AddVisualChild(Stage);
        _hosted = true;
        InvalidateMeasure();
        InvalidateArrange();
        InvalidateVisual();
    }

    void Detach()
    {
        if (!_hosted) return;
        RemoveVisualChild(Stage);
        _hosted = false;
        InvalidateVisual();
    }

    protected override int VisualChildrenCount => _hosted ? 1 : 0;
    protected override Visual GetVisualChild(int index) => Stage;

    protected override Size MeasureOverride(Size availableSize)
    {
        if (_hosted) Stage.Measure(Place(availableSize).Size);
        return new Size(0, 0);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (_hosted) Stage.Arrange(Place(finalSize));
        return finalSize;
    }

    Rect Place(Size size)
    {
        if (double.IsInfinity(size.Width) || double.IsInfinity(size.Height)) return new Rect(0, 0, 0, 0);
        double s = Math.Min(size.Width / DesignWidth, size.Height / DesignHeight);
        double ox = (size.Width - DesignWidth * s) / 2, oy = (size.Height - DesignHeight * s) / 2;
        var r = StageRect;
        return new Rect(ox + r.X * s, oy + r.Y * s, Math.Max(0, r.Width * s), Math.Max(0, r.Height * s));
    }

    protected sealed override void Render(DrawingContext dc)
    {
        _hits.Clear();
        var r = StageRect;
        dc.DrawRectangle(Brushes.Black, null, r);
        if (!_hosted && Stage.Visibility == Visibility.Visible)
        {
            dc.DrawRectangle(new VisualBrush(Stage) { Stretch = Stretch.Uniform }, null, r);
            Draw.Label(dc, VideoOut.IsExternal ? "VIDEO OUT" : "FULL SCREEN", r.X + 16, r.Y + 24, 24, Theme.Yellow, Align.Left, Theme.Regular);
        }
        RenderScreen(dc);
    }

    // ------------------------------------------------------------------ chrome

    protected void AddHit(Rect rect, Action? down = null, Action? up = null, Action<Vector>? drag = null, Action<double>? wheel = null) =>
        _hits.Add(new Hit(rect, down, up, drag, wheel));

    protected void Header(DrawingContext dc, string title)
    {
        Draw.Label(dc, title, (LeftW + RightX) / 2, 40, 56, Theme.Ink, Align.Center, Theme.Regular);
        Draw.HLine(dc, LeftEdge, RightEdge, HeaderY);
        Draw.VLine(dc, LeftW, TopEdge, BarY);
        Draw.VLine(dc, RightX, TopEdge, BarY);
    }

    /// <summary>A header cell button (LIBRARY, EDIT): big grey label, ink when active.</summary>
    protected void HeaderButton(DrawingContext dc, double x0, double x1, string label, bool active, Action tap)
    {
        var rect = new Rect(x0, TopEdge, x1 - x0, HeaderY - TopEdge);
        if (active) dc.DrawRectangle(Theme.Ink, null, rect);
        Draw.Label(dc, label, (Math.Max(x0, 0) + Math.Min(x1, DesignWidth)) / 2, 42, 34, active ? Theme.White : Theme.Ink, Align.Center, Theme.Regular);
        AddHit(rect, up: tap);
    }

    /// <summary>A bordered button; filled ink when active.</summary>
    protected void Button(DrawingContext dc, Rect rect, string label, bool active, bool enabled = true, double size = 24)
    {
        var inset = new Rect(rect.X + 1, rect.Y + 1, rect.Width - 2, rect.Height - 2);
        if (active) dc.DrawRectangle(Theme.Ink, null, inset);
        else dc.DrawRectangle(null, new Pen(enabled ? Theme.Ink : Theme.Rule, 2), inset);
        Draw.Label(dc, label, rect.X + rect.Width / 2, rect.Y + rect.Height / 2, size, active ? Theme.White : enabled ? Theme.Ink : Theme.Mid, Align.Center, Theme.Regular);
    }

    /// <summary>A knob cell editing a motion-track sound byte: drag vertically or use the wheel.</summary>
    protected void KnobCell(DrawingContext dc, Rect rect, string label, SoundParam param, Brush color, Func<byte, string>? format = null)
    {
        byte v = Session.CurrentSound[MotionTrack][(int)param];
        Draw.Label(dc, label, rect.X + 18, rect.Y + 26, 28, Theme.Mid);
        string text = format?.Invoke(v) ?? Display.Percent(v).ToString();
        Draw.Label(dc, text, rect.X + 18, rect.Y + rect.Height - 34, text.Length > 4 ? 30 : 40, Theme.Ink, Align.Left, Theme.Regular);
        Draw.Knob(dc, new Point(rect.Right - 52, rect.Y + rect.Height / 2 + 8), v / 255.0, color, 24);
        Draw.HLine(dc, rect.X, rect.Right, rect.Bottom);
        int start = v;
        void Set(int value) => Session.SetSound(MotionTrack, param, (byte)Math.Clamp(value, 0, 255));
        AddHit(rect, down: () => start = Session.CurrentSound[MotionTrack][(int)param],
            drag: d => Set(start - (int)Math.Round(d.Y * 0.9)),
            wheel: n => Set(Session.CurrentSound[MotionTrack][(int)param] + (int)Math.Round(n * 4)));
    }

    /// <summary>
    /// The black bar under the visual: name in the middle (tap: <paramref name="nameTap"/>), and video-out
    /// zones on either side of it.
    /// </summary>
    protected void NameBar(DrawingContext dc, string name, string sub, Action nameTap)
    {
        var bar = new Rect(LeftEdge, BarY, RightEdge - LeftEdge, BottomEdge - BarY);
        dc.DrawRectangle(Theme.StatusBar, null, bar);
        double cy = BarY + (DesignHeight - BarY) / 2;
        var nameSize = Draw.Text(name, 36, Theme.White, Theme.Regular);
        var subSize = Draw.Text(sub, 22, Theme.Mid);
        double w = nameSize.Width + 18 + subSize.Width, x = DesignWidth / 2 - w / 2;
        Draw.Label(dc, name, x, cy, 36, Theme.White, Align.Left, Theme.Regular);
        Draw.Label(dc, sub, x + nameSize.Width + 18, cy + 3, 22, Theme.Mid);
        double half = Math.Max(220, w / 2 + 40);
        AddHit(new Rect(DesignWidth / 2 - half, BarY, half * 2, bar.Height), up: nameTap);

        Brush c = VideoOut.IsActive ? Theme.Yellow : Theme.Mid;
        string label = VideoOut.IsActive ? (VideoOut.IsExternal ? "VIDEO OUT ON" : "FULL SCREEN") : "VIDEO OUT";
        foreach (var side in new[] { 0, 1 })
        {
            double cx = side == 0 ? 190 : DesignWidth - 190;
            var icon = new Rect(cx - 104, cy - 13, 36, 26);
            dc.DrawRectangle(VideoOut.IsActive ? Theme.Yellow : null, new Pen(c, 2.5), icon);
            Draw.Label(dc, label, cx - 54, cy, 24, c);
        }
        AddHit(new Rect(LeftEdge, BarY, DesignWidth / 2 - half - LeftEdge, bar.Height), up: () => VideoOut.Toggle(this));
        AddHit(new Rect(DesignWidth / 2 + half, BarY, RightEdge - DesignWidth / 2 - half, bar.Height), up: () => VideoOut.Toggle(this));
    }

    // ------------------------------------------------------------------ input

    Hit? Find(Point p)
    {
        for (int i = _hits.Count - 1; i >= 0; i--)
            if (_hits[i].Rect.Contains(p)) return _hits[i];
        return null;
    }

    protected override void OnPointerDown(int id, Point p)
    {
        if (Find(p) is not { } hit) return;
        _active[id] = (hit, p);
        hit.Down?.Invoke();
    }

    protected override void OnPointerMove(int id, Point p, Vector delta)
    {
        if (_active.TryGetValue(id, out var a) && a.Hit.Drag is { } drag) drag(p - a.Start);
    }

    protected override void OnPointerUp(int id, Point p)
    {
        if (!_active.Remove(id, out var a)) return;
        if (a.Hit.Drag is null || (p - a.Start).Length < 12) a.Hit.Up?.Invoke();
        InvalidateVisual();
    }

    protected override void OnWheel(Point p, double notches) => Find(p)?.Wheel?.Invoke(notches);
}
