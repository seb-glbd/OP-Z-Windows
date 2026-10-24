using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using OPB.App.Rendering;
using OPB.Core;

namespace OPB.App.Controls;

/// <summary>
/// The black bar at the bottom of the app: battery, transport state with the current pattern,
/// and the screen-menu button.
/// </summary>
public sealed class StatusBar : FrameworkElement
{
    const double DesignHeight = 88;
    readonly AppModel _app;

    public StatusBar(AppModel app)
    {
        _app = app;
        _app.Session.Changed += _ => InvalidateVisual();
        Stylus.SetIsPressAndHoldEnabled(this, false);
    }

    public event Action? MenuRequested;

    OpzSession Session => _app.Session;
    double Scale => ActualHeight / DesignHeight;

    protected override void OnRender(DrawingContext dc)
    {
        Draw.PixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        dc.DrawRectangle(Theme.StatusBar, null, new Rect(RenderSize));
        double s = Scale;
        if (s <= 0) return;
        var greyPen = new Pen(Theme.Mid, 3 * s);

        // Battery.
        double by = ActualHeight / 2;
        int battery = Session.IsConnected ? Session.Level.Battery : 0;
        dc.DrawRectangle(null, greyPen, new Rect(48 * s, by - 10 * s, 32 * s, 20 * s));
        dc.DrawRectangle(Theme.Mid, null, new Rect(80 * s, by - 5 * s, 5 * s, 10 * s));
        dc.DrawRectangle(Theme.Mid, null, new Rect(52 * s, by - 6 * s, 24 * s * battery / 100.0, 12 * s));
        string label = Session.IsConnected ? $"{battery} %" : "NOT CONNECTED";
        Draw.Label(dc, label, 108 * s, by, 42 * s, Theme.Mid);

        // Transport icon and current pattern.
        double cx = ActualWidth / 2;
        var whitePen = new Pen(Theme.White, 5 * s) { LineJoin = PenLineJoin.Miter };
        bool playing = Session.ClockRunning || Session.Play.Playing;
        if (playing)
        {
            var tri = new StreamGeometry();
            using (var g = tri.Open())
            {
                g.BeginFigure(new Point(cx - 40 * s, by - 24 * s), false, true);
                g.LineTo(new Point(cx + 2 * s, by), true, true);
                g.LineTo(new Point(cx - 40 * s, by + 24 * s), true, true);
            }
            tri.Freeze();
            dc.DrawGeometry(null, whitePen, tri);
        }
        else
        {
            dc.DrawRectangle(null, whitePen, new Rect(cx - 44 * s, by - 22 * s, 44 * s, 44 * s));
        }
        Draw.Label(dc, (Session.ActivePattern + 1).ToString(), cx + 14 * s, by, 58 * s, Theme.Yellow);

        // Screen menu button.
        dc.DrawRectangle(null, new Pen(Theme.White, 3 * s), new Rect(ActualWidth - 116 * s, by - 16 * s, 56 * s, 32 * s));
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e) => Tap(e.GetPosition(this));

    protected override void OnTouchUp(TouchEventArgs e)
    {
        Tap(e.GetTouchPoint(this).Position);
        e.Handled = true;
    }

    void Tap(Point p)
    {
        double s = Scale;
        if (p.X > ActualWidth - 180 * s) MenuRequested?.Invoke();
        else if (Math.Abs(p.X - ActualWidth / 2) < 120 * s)
        {
            if (Session.ClockRunning || Session.Play.Playing) Session.TransportStop();
            else Session.TransportStart();
        }
        else if (p.X < 400 * s) _app.Navigate(AppPage.Devices);
    }
}
