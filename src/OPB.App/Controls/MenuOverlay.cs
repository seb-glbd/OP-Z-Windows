using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using OPB.App.Rendering;

namespace OPB.App.Controls;

/// <summary>The app's main menu, opened from the screen icon in the status bar.</summary>
public sealed class MenuOverlay : FrameworkElement
{
    static readonly (AppPage Page, string Label)[] Items =
    [
        (AppPage.Screen, "SCREEN"),
        (AppPage.Configurator, "CONFIGURATOR"),
        (AppPage.Photomatic, "PHOTOMATIC"),
        (AppPage.Motion, "MOTION"),
        (AppPage.MidiSetup, "MIDI SETUP"),
        (AppPage.Devices, "DEVICES"),
    ];

    readonly AppModel _app;
    double _scrollStartY;
    bool _dragged;

    public MenuOverlay(AppModel app)
    {
        _app = app;
        Stylus.SetIsPressAndHoldEnabled(this, false);
    }

    public event Action? Closed;

    double Scale => Math.Min(ActualWidth / 1600, ActualHeight / 812);

    protected override void OnRender(DrawingContext dc)
    {
        Draw.PixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(0xF2, 0x13, 0x13, 0x13)), null, new Rect(RenderSize));
        double s = Scale, rowH = 104 * s, top = (ActualHeight - rowH * Items.Length) / 2;
        for (int i = 0; i < Items.Length; i++)
        {
            bool current = Items[i].Page == _app.Page;
            double y = top + i * rowH + rowH / 2;
            Draw.Label(dc, Items[i].Label, ActualWidth / 2, y, 64 * s, current ? Theme.Yellow : Theme.White, Align.Center, Theme.Regular);
        }
    }

    // Touch taps also arrive as promoted mouse events (StylusDevice set); handle each tap once.
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e) { if (e.StylusDevice is null) { _scrollStartY = e.GetPosition(this).Y; _dragged = false; } }
    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e) { if (e.StylusDevice is null) Tap(e.GetPosition(this)); }
    protected override void OnTouchDown(TouchEventArgs e) { _scrollStartY = e.GetTouchPoint(this).Position.Y; _dragged = false; e.Handled = true; }
    protected override void OnTouchUp(TouchEventArgs e) { Tap(e.GetTouchPoint(this).Position); e.Handled = true; }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape) Closed?.Invoke();
    }

    void Tap(Point p)
    {
        if (Math.Abs(p.Y - _scrollStartY) > 40) _dragged = true;
        if (_dragged) return;
        double s = Scale, rowH = 104 * s, top = (ActualHeight - rowH * Items.Length) / 2;
        int index = (int)Math.Floor((p.Y - top) / rowH);
        if (index >= 0 && index < Items.Length) _app.Navigate(Items[index].Page);
        Closed?.Invoke();
    }
}
