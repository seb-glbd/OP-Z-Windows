using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace OPB.App.Video;

/// <summary>
/// Video out for a live visual ("stage"): moves the stage element to a borderless full-screen window on a
/// second display, or full screen over the main window when there is only one display. The owning screen
/// shows a live VisualBrush preview while the stage is away.
/// </summary>
public sealed class VideoOut(FrameworkElement stage, Action attach, Action detach)
{
    Window? _window;
    Grid? _frame;
    MainWindow? _main;

    public bool IsActive => _window is not null || _frame is not null;
    public bool IsExternal => _window is not null;
    public event Action? Changed;

    /// <summary>Starts or stops video out; <paramref name="inWindow"/> skips looking for a second display.</summary>
    public void Toggle(Visual from, bool inWindow = false)
    {
        if (IsActive) Stop();
        else if (Window.GetWindow(from) is { } owner) Start(owner, inWindow);
    }

    void Start(Window owner, bool inWindow)
    {
        var target = inWindow ? null : Monitors.Other(owner);
        detach();
        if (target is { } bounds)
        {
            var dpi = VisualTreeHelper.GetDpi(owner);
            var window = new Window
            {
                Title = "OP-B video out", WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize,
                Background = Brushes.Black, ShowInTaskbar = false, ShowActivated = false, Cursor = Cursors.None,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = bounds.Left / dpi.DpiScaleX + 40, Top = bounds.Top / dpi.DpiScaleY + 40, Width = 640, Height = 360,
                Content = stage,
            };
            window.KeyDown += (_, e) => { if (e.Key == Key.Escape) Stop(); };
            window.MouseDoubleClick += (_, _) => Stop();
            window.Closed += (_, _) => { if (_window == window) Stop(); };
            owner.Closed += (_, _) => window.Close();
            _window = window;
            window.Show();
            window.WindowState = WindowState.Maximized;
        }
        else if (owner is MainWindow main)
        {
            var hint = new TextBlock
            {
                Text = "TAP OR ESC TO EXIT", Foreground = Brushes.White, FontFamily = Rendering.Theme.Font, FontSize = 20,
                FontWeight = FontWeights.Light, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(24), IsHitTestVisible = false,
            };
            hint.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0.8, 0, TimeSpan.FromSeconds(1)) { BeginTime = TimeSpan.FromSeconds(2) });
            var frame = new Grid { Background = Brushes.Black, Children = { stage, hint } };
            frame.MouseLeftButtonUp += (_, _) => Stop();
            frame.TouchUp += (_, e) => { e.Handled = true; Stop(); };
            main.PreviewKeyDown += OnMainKey;
            _main = main;
            _frame = frame;
            main.ShowStage(frame);
        }
        else
        {
            attach();
            return;
        }
        Changed?.Invoke();
    }

    void OnMainKey(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        e.Handled = true;
        Stop();
    }

    public void Stop()
    {
        if (_window is { } window)
        {
            _window = null;
            window.Content = null;
            window.Close();
        }
        if (_frame is { } frame)
        {
            _frame = null;
            frame.Children.Remove(stage);
            if (_main is not null)
            {
                _main.PreviewKeyDown -= OnMainKey;
                _main.ShowStage(null);
            }
        }
        attach();
        Changed?.Invoke();
    }
}

/// <summary>Display enumeration (physical pixels) via EnumDisplayMonitors.</summary>
public static class Monitors
{
    delegate bool MonitorEnumProc(IntPtr monitor, IntPtr hdc, ref Rect32 rect, IntPtr data);

    [StructLayout(LayoutKind.Sequential)]
    struct Rect32 { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")] static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc proc, IntPtr data);
    [DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    public static List<(IntPtr Handle, Rect Bounds)> All()
    {
        var list = new List<(IntPtr, Rect)>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr m, IntPtr _, ref Rect32 r, IntPtr _) =>
        {
            list.Add((m, new Rect(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top)));
            return true;
        }, IntPtr.Zero);
        return list;
    }

    /// <summary>Bounds of a display other than the one showing <paramref name="window"/>, if any.</summary>
    public static Rect? Other(Window window)
    {
        var own = MonitorFromWindow(new WindowInteropHelper(window).Handle, 2);
        foreach (var (handle, bounds) in All())
            if (handle != own) return bounds;
        return null;
    }
}
