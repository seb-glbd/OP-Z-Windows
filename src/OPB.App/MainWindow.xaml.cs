using System.Windows;
using System.Windows.Input;
using OPB.App.Controls;
using OPB.App.Screens;
using OPB.Core;
using OPB.Core.Midi;

namespace OPB.App;

public partial class MainWindow : Window
{
    readonly OpzSession _session;
    readonly AppModel _app;
    readonly MenuOverlay _menu;
    readonly Dictionary<string, ScreenView> _screens = new();
    string? _route;
    WindowState _restoreState;

    public MainWindow()
    {
        InitializeComponent();
        var args = Environment.GetCommandLineArgs();
        bool offline = OfflinePreview.IsRequested(args);
        _session = new OpzSession(() => offline ? null : WinMidiConnection.TryOpen("OP-Z"), System.Threading.SynchronizationContext.Current);
        if (offline) Loaded += (_, _) => OfflinePreview.Load(_session, args);
        _app = new AppModel(_session);

        var status = new StatusBar(_app);
        status.MenuRequested += ToggleMenu;
        StatusHost.Content = status;

        _menu = new MenuOverlay(_app);
        _menu.Closed += () => OverlayHost.Content = null;

        _session.Changed += _ => UpdateRoute();
        _app.PageChanged += UpdateRoute;
        SizeChanged += (_, _) => StatusRow.Height = new GridLength(Math.Max(40, ActualHeight * 88 / 900));
        Loaded += (_, _) => { UpdateRoute(); _session.Start(); _ = SnapshotIfRequestedAsync(); };
        Closed += (_, _) => _session.Dispose();
    }

    /// <summary>
    /// Diagnostics: <c>OPB.App --snapshot out.png [page] [WxH]</c> connects, waits for state,
    /// renders the window to a PNG and exits.
    /// </summary>
    async Task SnapshotIfRequestedAsync()
    {
        var args = Environment.GetCommandLineArgs();
        int i = Array.IndexOf(args, "--snapshot");
        if (i < 0 || i + 1 >= args.Length) return;
        string path = args[i + 1];
        if (i + 2 < args.Length && Enum.TryParse<AppPage>(args[i + 2], true, out var page)) _app.Navigate(page);
        if (i + 3 < args.Length && args[i + 3].Split('x') is [var w, var h])
        {
            Width = double.Parse(w);
            Height = double.Parse(h);
        }
        await Task.Delay(3500);
        UpdateLayout();
        var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(this);
        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(
            (int)(Root.ActualWidth * dpi.DpiScaleX), (int)(Root.ActualHeight * dpi.DpiScaleY),
            dpi.PixelsPerInchX, dpi.PixelsPerInchY, System.Windows.Media.PixelFormats.Pbgra32);
        bitmap.Render(Root);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path))!);
        using (var file = System.IO.File.Create(path)) encoder.Save(file);
        Close();
    }

    public OpzSession Session => _session;
    public AppModel App => _app;

    void UpdateRoute()
    {
        var route = ScreenRegistry.RouteFor(_app);
        if (route == _route) return;
        _route = route;
        if (!_screens.TryGetValue(route, out var screen))
            _screens[route] = screen = ScreenRegistry.Create(route, _app);
        ScreenHost.Content = screen;
    }

    void ToggleMenu()
    {
        OverlayHost.Content = OverlayHost.Content is null ? _menu : null;
        _menu.InvalidateVisual();
        _menu.Focus();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.F11:
                ToggleFullScreen();
                break;
            case Key.Escape when OverlayHost.Content is not null:
                OverlayHost.Content = null;
                break;
            case Key.Escape when _app.Page != AppPage.Screen:
                _app.Navigate(AppPage.Screen);
                break;
            case Key.Escape when WindowStyle == WindowStyle.None:
                ToggleFullScreen();
                break;
            case Key.M when Keyboard.Modifiers == ModifierKeys.None:
                ToggleMenu();
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    bool _stageFullScreen;

    /// <summary>Shows a visual over the whole window in full screen (video out without a second display); null restores.</summary>
    public void ShowStage(UIElement? stage)
    {
        StageHost.Content = stage;
        bool full = WindowStyle == WindowStyle.None;
        if (stage is not null && !full) { _stageFullScreen = true; ToggleFullScreen(); }
        else if (stage is null && _stageFullScreen) { _stageFullScreen = false; if (full) ToggleFullScreen(); }
    }

    void ToggleFullScreen()
    {
        if (WindowStyle == WindowStyle.None)
        {
            WindowStyle = WindowStyle.SingleBorderWindow;
            ResizeMode = ResizeMode.CanResize;
            WindowState = _restoreState;
        }
        else
        {
            _restoreState = WindowState;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            WindowState = WindowState.Normal;
            WindowState = WindowState.Maximized;
        }
    }
}
