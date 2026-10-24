using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using OPB.App.Rendering;
using OPB.Core;
using OPB.Core.Model;

namespace OPB.App.Screens;

/// <summary>
/// Shared frame for the control tracks (master, module, lights, motion): the track screen's title,
/// touch pad and sequencer, with a track-specific body in between. Subclasses draw the body into
/// <see cref="Body"/> and register tappable parameters with <see cref="Cell"/> or <see cref="Hit"/>.
/// </summary>
public abstract class ControlTrackScreen : ScreenView
{
    protected const double TitleY = 40, Row0 = 82, BodyBottom = 602, RightEnd = 1426;
    const double SeqLabelY = 622, SeqNumTop = 645, SeqDotTop = 688, SeqBottom = 732, StepX0 = 42, StepW = 86.5;
    protected const double CellW = 130, CellH = 130;

    protected readonly AppModel App;
    readonly List<(Rect Rect, ParamRef Param)> _hits = new();
    readonly List<(Rect Rect, Action<int, bool> Press)> _buttons = new();
    readonly Dictionary<int, Action<int, bool>> _held = new();
    readonly Stopwatch _playClock = new();
    readonly Rect _touchPad = new(RightEnd, Row0, ScreenView.DesignWidth - RightEnd, SeqBottom - 2 - Row0);
    int? _touchPadPointer;
    double _touchPadAccumulator;

    protected ControlTrackScreen(AppModel app) : base(app.Session)
    {
        App = app;
        App.SelectionChanged += InvalidateVisual;
    }

    protected abstract int Track { get; }
    protected int Pattern => Session.ActivePattern;
    protected byte S(SoundParam p) => Session.CurrentSound[Track][(int)p];
    protected static readonly Rect Body = new(0, Row0, RightEnd, BodyBottom - Row0);

    public override string Title => Tracks.Names[Track];

    protected override bool WantsAnimation => Session.ClockRunning || Session.Play.Playing || Animating;

    /// <summary>True while the body animates on its own (previews, held triggers).</summary>
    protected virtual bool Animating => false;

    protected override bool ShouldRedraw(Changes c)
    {
        if ((c & (Changes.Play | Changes.Transport)) != 0)
        {
            if (Session.Play.Playing || Session.ClockRunning) { if (!_playClock.IsRunning) _playClock.Restart(); }
            else _playClock.Reset();
        }
        return true;
    }

    protected abstract void RenderBody(DrawingContext dc);

    /// <summary>Text shown in a sequencer step instead of the plain trig dot (e.g. a note name); null keeps the dot.</summary>
    protected virtual string? StepText(IReadOnlyList<Note> notes) => null;

    /// <summary>Parameter to show framed when nothing has been tapped yet.</summary>
    protected virtual SoundParam DefaultParam => SoundParam.P1;

    protected sealed override void Render(DrawingContext dc)
    {
        _hits.Clear();
        _buttons.Clear();
        Draw.Label(dc, Title, RightEnd / 2, TitleY, 56, Theme.Ink, Align.Center, Theme.Regular);
        Draw.Label(dc, "TOUCH PAD", RightEnd + 14, TitleY, 30, Theme.Mid);
        Draw.HLine(dc, LeftEdge, RightEdge, Row0);
        Draw.VLine(dc, RightEnd, TopEdge, BottomEdge);
        Draw.HLine(dc, LeftEdge, RightEdge, BodyBottom);
        dc.DrawRectangle(Theme.Hatch, null, new Rect(RightEnd, Row0, RightEdge - RightEnd, _touchPad.Height));

        RenderBody(dc);
        DrawSequencer(dc);

        var selected = Selected;
        foreach (var (rect, param) in _hits)
            if (param.Equals(selected)) { Draw.Frame(dc, rect, Theme.Red); break; }
        if (_touchPadPointer is not null)
        {
            Draw.Label(dc, selected.Label, RightEnd + 87, Row0 + 60, 28, Theme.Ink, Align.Center);
            Draw.Label(dc, selected.Text, RightEnd + 87, Row0 + 110, 48, Theme.Ink, Align.Center, Theme.Regular);
        }
    }

    protected ParamRef Selected
    {
        get
        {
            var s = App.Selected(Track);
            return s.Key.StartsWith($"sound:{Track}:") || s.Key.StartsWith($"ctl:{Track}:") ? s : ParamRef.Sound(Session, Track, DefaultParam);
        }
    }

    // ------------------------------------------------------------------ building blocks

    protected ParamRef Snd(SoundParam p, string label, Func<int, string>? format = null) => ParamRef.Sound(Session, Track, p, label, format);

    /// <summary>Knob color for a dial on a page: the dial color on the device's current page, grey otherwise.</summary>
    protected Brush DialBrush(int page, int dial) => Session.Ui.Page == page ? Theme.Dials[dial] : Theme.Mid;

    /// <summary>A knob cell like the track screen's: label, knob, value. Registers it for the touch pad.</summary>
    protected void Cell(DrawingContext dc, double x, double y, string label, ParamRef param, int page, int dial, string? caption = null)
    {
        var rect = new Rect(x, y, CellW, CellH);
        Draw.Label(dc, label, x + 8, y + 20, 28, Theme.Mid);
        Draw.Knob(dc, new Point(x + 65, y + 65), param.Value / 255.0, DialBrush(page, dial));
        Draw.Label(dc, param.Text, x + 65, y + 107, 30, Theme.Ink, Align.Center, Theme.Regular);
        if (caption is not null) Draw.Label(dc, caption, x + CellW - 6, y + 109, 18, Theme.Mid, Align.Right);
        _hits.Add((rect, param));
    }

    /// <summary>Registers a tappable parameter area without drawing it.</summary>
    protected void Hit(Rect rect, ParamRef param) => _hits.Add((rect, param));

    /// <summary>A press/hold area: called with (pointer, true) on press and (pointer, false) on release.</summary>
    protected void Button(Rect rect, Action<int, bool> press) => _buttons.Add((rect, press));

    protected static void Section(DrawingContext dc, string label, double x, double y) => Draw.Label(dc, label, x + 18, y + 42, 30, Theme.Mid);

    protected static void Frame(DrawingContext dc, Rect r, Brush brush) => Draw.Frame(dc, r, brush);

    protected void SendNote(int note, bool on) => App.SendTrackNote(Track, note, on ? 100 : 0);

    // ------------------------------------------------------------------ sequencer

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
            if (step > 0) Draw.VLine(dc, x, SeqNumTop, SeqDotTop, step % 4 == 0 ? new Pen(Theme.Ink, 2) : Theme.RulePen);
            Draw.Label(dc, (step + 1).ToString(), x + 4, SeqNumTop + 22, 40, beyond ? Theme.Rule : Theme.Mid);
            var center = new Point(x + StepW / 2 - 21, SeqDotTop + 22);
            var notes = bank is null ? [] : bank.Notes(Pattern, Track, step);
            string? text = notes.Count > 0 ? StepText(notes) : null;
            if (beyond) dc.DrawEllipse(Theme.Background, null, center, 11, 11);
            else if (text is not null) Draw.Label(dc, text, x + StepW / 2, center.Y, 26, Theme.Ink, Align.Center, Theme.Regular);
            else dc.DrawEllipse(notes.Count > 0 ? Theme.Ink : Theme.Mid, null, center, 11, 11);
        }
        double fx = StepX0 + Math.Max(0, playStep) * StepW;
        Draw.Frame(dc, new Rect(fx - 2, SeqNumTop - 2, StepW + 2, SeqBottom - SeqNumTop + 4), Theme.Yellow);

        Draw.Label(dc, "STEP COUNT", 52, SeqBottom + 40, 32, Theme.Mid);
        Draw.Label(dc, count.ToString(), 247, SeqBottom + 40, 42, Theme.Ink, Align.Left, Theme.Regular);
        Draw.Label(dc, "STEP LENGTH", 398, SeqBottom + 40, 32, Theme.Mid);
        Draw.Label(dc, length.ToString(), 592, SeqBottom + 40, 42, Theme.Ink, Align.Left, Theme.Regular);
        Draw.Label(dc, "SUM", 1315, SeqBottom + 40, 32, Theme.Mid, Align.Right);
        Draw.Label(dc, (count * length).ToString(), 1350, SeqBottom + 40, 42, Theme.Green, Align.Left, Theme.Regular);
        _hits.Add((new Rect(0, SeqBottom, 360, DesignHeight - SeqBottom), new ParamRef($"ctl:{Track}:stepcount", "STEP COUNT",
            () => count, v => { App.SendTrackCc(Track, 60, v); bank?.SetStepCount(Pattern, Track, v); InvalidateVisual(); }, v => v.ToString(), 1, 16, 0.05)));
        _hits.Add((new Rect(360, SeqBottom, 360, DesignHeight - SeqBottom), new ParamRef($"ctl:{Track}:steplength", "STEP LENGTH",
            () => length, v => { App.SendTrackCc(Track, 61, v); bank?.SetStepLength(Pattern, Track, v); InvalidateVisual(); }, v => v.ToString(), 1, 16, 0.05)));
    }

    /// <summary>The playing step of this track, from MIDI clock or (without clock) the tempo.</summary>
    protected int CurrentStep(int count, int length)
    {
        if (!Session.ClockRunning && !Session.Play.Playing) return -1;
        long sixteenths = Session.ClockTicks > 0
            ? Session.ClockTicks / 6
            : (long)(_playClock.Elapsed.TotalMinutes * (Session.Global?.Tempo ?? 120) * 4);
        return (int)(sixteenths / Math.Max(1, length) % Math.Max(1, count));
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
        foreach (var (rect, press) in _buttons)
            if (rect.Contains(p)) { _held[id] = press; press(id, true); return; }
        foreach (var (rect, param) in _hits)
            if (rect.Contains(p)) { App.Select(Track, param); return; }
    }

    protected override void OnPointerMove(int id, Point p, Vector delta)
    {
        if (id != _touchPadPointer) return;
        var param = Selected;
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
        if (_held.Remove(id, out var press)) press(id, false);
    }

    protected override void OnHidden()
    {
        foreach (var (id, press) in _held.ToList()) press(id, false);
        _held.Clear();
    }

    protected override void OnWheel(Point p, double notches)
    {
        foreach (var (rect, param) in _hits)
        {
            if (!rect.Contains(p)) continue;
            App.Select(Track, param);
            param.Set(param.Value + (int)Math.Round(notches * Math.Max(1, 4 * param.Sensitivity)));
            InvalidateVisual();
            return;
        }
        if (_touchPad.Contains(p))
        {
            var param = Selected;
            param.Set(param.Value + (int)Math.Round(notches * Math.Max(1, 4 * param.Sensitivity)));
            InvalidateVisual();
        }
    }

    // ------------------------------------------------------------------ helpers

    protected static Color Hsv(double h, double s, double v)
    {
        h = ((h % 360) + 360) % 360;
        double c = v * s, x = c * (1 - Math.Abs(h / 60 % 2 - 1)), m = v - c;
        (double r, double g, double b) = h switch
        {
            < 60 => (c, x, 0.0), < 120 => (x, c, 0.0), < 180 => (0.0, c, x),
            < 240 => (0.0, x, c), < 300 => (x, 0.0, c), _ => (c, 0.0, x),
        };
        return Color.FromRgb((byte)((r + m) * 255), (byte)((g + m) * 255), (byte)((b + m) * 255));
    }
}
