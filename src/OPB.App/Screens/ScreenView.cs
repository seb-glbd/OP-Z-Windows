using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using OPB.App.Rendering;
using OPB.Core;

namespace OPB.App.Screens;

/// <summary>
/// Base class for every app screen. Screens draw into a fixed 1600 x 812 design space
/// (the Android app's layout above its status bar) which is scaled uniformly to the window.
/// Touch and mouse input are unified into pointer callbacks in design coordinates, so
/// several fingers can drag different controls at once.
/// </summary>
public abstract class ScreenView : FrameworkElement
{
    public const double DesignWidth = 1600;
    public const double DesignHeight = 812;

    readonly Dictionary<int, Point> _pointers = new();
    bool _animating;
    double _scale = 1;
    Vector _offset;

    protected ScreenView(OpzSession session)
    {
        Session = session;
        ClipToBounds = true;
        Focusable = true;
        Stylus.SetIsPressAndHoldEnabled(this, false);
        Stylus.SetIsFlicksEnabled(this, false);
        IsManipulationEnabled = false;
        Loaded += (_, _) => { Session.Changed += OnSessionChanged; OnShown(); UpdateAnimation(); };
        Unloaded += (_, _) => { Session.Changed -= OnSessionChanged; SetAnimating(false); OnHidden(); };
    }

    public OpzSession Session { get; }

    /// <summary>Title shown by the router (also used for the window title).</summary>
    public virtual string Title => "";

    /// <summary>Which session changes require a redraw. Return true to invalidate.</summary>
    protected virtual bool ShouldRedraw(Changes changes) => true;

    /// <summary>Return true while the screen needs per-frame redraws (playhead, spinning reels).</summary>
    protected virtual bool WantsAnimation => false;

    protected virtual void OnShown() { }
    protected virtual void OnHidden() { }

    protected abstract void Render(DrawingContext dc);

    protected virtual void OnPointerDown(int id, Point p) { }
    protected virtual void OnPointerMove(int id, Point p, Vector delta) { }
    protected virtual void OnPointerUp(int id, Point p) { }

    /// <summary>Called for each animation frame while <see cref="WantsAnimation"/> is true.</summary>
    protected virtual void OnFrame(TimeSpan time) { }

    void OnSessionChanged(Changes changes)
    {
        if (ShouldRedraw(changes)) InvalidateVisual();
        UpdateAnimation();
    }

    protected void UpdateAnimation() => SetAnimating(WantsAnimation && IsLoaded);

    void SetAnimating(bool on)
    {
        if (on == _animating) return;
        _animating = on;
        if (on) CompositionTarget.Rendering += OnRendering;
        else CompositionTarget.Rendering -= OnRendering;
    }

    void OnRendering(object? sender, EventArgs e)
    {
        OnFrame(e is RenderingEventArgs r ? r.RenderingTime : TimeSpan.Zero);
        InvalidateVisual();
        if (!WantsAnimation) SetAnimating(false);
    }

    protected sealed override void OnRender(DrawingContext dc)
    {
        Draw.PixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        dc.DrawRectangle(Theme.Background, null, new Rect(RenderSize));
        _scale = Math.Min(ActualWidth / DesignWidth, ActualHeight / DesignHeight);
        if (_scale <= 0) return;
        _offset = new Vector((ActualWidth - DesignWidth * _scale) / 2, (ActualHeight - DesignHeight * _scale) / 2);
        LeftEdge = -_offset.X / _scale;
        RightEdge = DesignWidth + _offset.X / _scale;
        TopEdge = -_offset.Y / _scale;
        BottomEdge = DesignHeight + _offset.Y / _scale;
        RenderBackdrop(dc, _offset, _scale);
        dc.PushTransform(new TranslateTransform(_offset.X, _offset.Y));
        dc.PushTransform(new ScaleTransform(_scale, _scale));
        Render(dc);
        dc.Pop();
        dc.Pop();
    }

    /// <summary>Draws anything that should extend past the design area (in window pixels).</summary>
    protected virtual void RenderBackdrop(DrawingContext dc, Vector offset, double scale) { }

    /// <summary>Window edges in design coordinates (beyond 0..DesignWidth when the window is wider than 16:9).</summary>
    protected double LeftEdge { get; private set; }
    protected double RightEdge { get; private set; } = DesignWidth;
    protected double TopEdge { get; private set; }
    protected double BottomEdge { get; private set; } = DesignHeight;

    protected Point ToDesign(Point windowPoint) => new((windowPoint.X - _offset.X) / _scale, (windowPoint.Y - _offset.Y) / _scale);

    // ---- input plumbing ----

    protected override void OnTouchDown(TouchEventArgs e)
    {
        CaptureTouch(e.TouchDevice);
        PointerDown(e.TouchDevice.Id + 1, e.GetTouchPoint(this).Position);
        e.Handled = true;
    }

    protected override void OnTouchMove(TouchEventArgs e)
    {
        PointerMove(e.TouchDevice.Id + 1, e.GetTouchPoint(this).Position);
        e.Handled = true;
    }

    protected override void OnTouchUp(TouchEventArgs e)
    {
        PointerUp(e.TouchDevice.Id + 1, e.GetTouchPoint(this).Position);
        ReleaseTouchCapture(e.TouchDevice);
        e.Handled = true;
    }

    protected override void OnLostTouchCapture(TouchEventArgs e)
    {
        if (_pointers.ContainsKey(e.TouchDevice.Id + 1)) PointerUp(e.TouchDevice.Id + 1, e.GetTouchPoint(this).Position);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        if (e.StylusDevice is not null) return; // promoted from touch; already handled
        CaptureMouse();
        PointerDown(0, e.GetPosition(this));
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (e.StylusDevice is not null || !_pointers.ContainsKey(0)) return;
        PointerMove(0, e.GetPosition(this));
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (e.StylusDevice is not null || !_pointers.ContainsKey(0)) return;
        PointerUp(0, e.GetPosition(this));
        ReleaseMouseCapture();
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        OnWheel(ToDesign(e.GetPosition(this)), e.Delta / 120.0);
        e.Handled = true;
    }

    /// <summary>Mouse wheel over a point; +1 per notch away from the user.</summary>
    protected virtual void OnWheel(Point p, double notches) { }

    void PointerDown(int id, Point window)
    {
        var p = ToDesign(window);
        _pointers[id] = p;
        OnPointerDown(id, p);
        InvalidateVisual();
    }

    void PointerMove(int id, Point window)
    {
        if (!_pointers.TryGetValue(id, out var last)) return;
        var p = ToDesign(window);
        _pointers[id] = p;
        OnPointerMove(id, p, p - last);
    }

    void PointerUp(int id, Point window)
    {
        if (!_pointers.Remove(id)) return;
        OnPointerUp(id, ToDesign(window));
        InvalidateVisual();
    }

    protected bool IsPointerDown => _pointers.Count > 0;
}
