using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using OPB.Core;
using OPB.Core.Model;

namespace OPB.App.Photomatic;

/// <summary>A slot's decoded media. Stills and GIF frames are kept as pixels for color processing.</summary>
public sealed class SlotMedia(string path, MediaKind kind)
{
    public string Path { get; } = path;
    public MediaKind Kind { get; } = kind;
    public Pixels? Image { get; set; }
    public BitmapSource? Bitmap { get; set; }
    public List<(Pixels Pixels, BitmapSource Bitmap, TimeSpan Delay)>? Frames { get; set; }
    public TimeSpan GifLength { get; set; }
    public BitmapSource? Thumb { get; set; }
    public MediaPlayer? Player { get; set; }
    public Size Size { get; set; }
    public bool Failed { get; set; }
    public bool Ready => Kind == MediaKind.Video ? Size.Width > 0 : Bitmap is not null || Frames is not null;
    public bool Playing { get; set; }
    public WriteableBitmap? Grab { get; set; }
    public double GrabTime { get; set; } = -1;
}

/// <summary>
/// The photomatic visual: the current slot's photo, GIF or video with the track 16 dials (hue, saturation,
/// brightness, contrast; shift page crossfade, zoom, double, woozy), the roll editor settings and the held
/// white-key effects. Renders into any size, so it can be moved to a video-out window as is.
/// </summary>
public sealed class PhotomaticStage : FrameworkElement
{
    public static readonly string[] EffectNames =
        ["PREVIOUS", "NEXT", "RANDOM", "FIRST", "INVERT", "FLIP H", "FLIP V", "PUNCH ZOOM", "WHITE OUT", "BLACK OUT", "SHARPEN", "KILL RED", "KILL GREEN", "KILL BLUE"];
    public static readonly string[] DoubleNames = ["OFF", "MIRROR", "KALEIDO", "X4", "X9", "X16"];
    const int Invert = 4, FlipH = 5, FlipV = 6, Punch = 7, WhiteOut = 8, BlackOut = 9, Sharpen = 10, KillRed = 11;
    static readonly Brush Grey = Rendering.Theme.Freeze(new SolidColorBrush(Color.FromRgb(0x80, 0x80, 0x80)));
    static readonly Brush PaperWhite = Rendering.Theme.Freeze(new SolidColorBrush(Color.FromRgb(0xF4, 0xF2, 0xEC)));

    readonly OpzSession _session;
    readonly Stopwatch _clock = Stopwatch.StartNew();
    readonly bool[] _held = new bool[EffectNames.Length];
    readonly double[] _lit = new double[EffectNames.Length];
    readonly Dictionary<(object, ColorParams), BitmapSource> _cache = new();
    readonly Queue<(object, ColorParams)> _cacheOrder = new();
    readonly Random _random = new();
    SlotMedia?[] _media = new SlotMedia?[PhotoLibrary.SlotCount];
    int _previous = -1;
    double _triggerTime = -10, _previousTrigger = -10, _punchTime = -10, _white, _black, _lastFrame;
    bool _animating;
    Pixels? _camera;
    WriteableBitmap? _cameraOut;

    public PhotomaticStage(OpzSession session)
    {
        _session = session;
        ClipToBounds = true;
        IsHitTestVisible = false;
        Loaded += (_, _) => SetAnimating(true);
        Unloaded += (_, _) => SetAnimating(false);
    }

    public RollConfig Config { get; private set; } = new();
    public int Slot { get; private set; }
    public bool CameraOn { get; set; }
    public event Action? SlotChanged;

    double Now => _clock.Elapsed.TotalSeconds;
    byte S(SoundParam p) => _session.CurrentSound[15][(int)p];

    public SlotMedia? Media(int slot) => _media[slot];
    public bool IsLit(int effect) => _held[effect] || Now - _lit[effect] < 0.18;
    public bool HasMedia(int slot) => _media[slot] is { Failed: false };
    public int DoubleMode => Math.Min(DoubleNames.Length - 1, S(SoundParam.Sustain) * DoubleNames.Length / 256);

    public ColorParams Colors => new(S(SoundParam.P1), S(SoundParam.P2), S(SoundParam.Filter), S(SoundParam.Resonance),
        _held[Invert], _held[KillRed], _held[KillRed + 1], _held[KillRed + 2], _held[Sharpen]);

    void SetAnimating(bool on)
    {
        if (on == _animating) return;
        _animating = on;
        if (on) CompositionTarget.Rendering += OnRendering;
        else CompositionTarget.Rendering -= OnRendering;
    }

    void OnRendering(object? sender, EventArgs e)
    {
        double now = Now;
        double dt = Math.Clamp(now - _lastFrame, 0, 0.1);
        _lastFrame = now;
        _white = Ease(_white, _held[WhiteOut] ? 1 : 0, dt);
        _black = Ease(_black, _held[BlackOut] ? 1 : 0, dt);
        UpdatePlayers(now);
        InvalidateVisual();
    }

    static double Ease(double v, double target, double dt) => v + (target - v) * Math.Min(1, dt / (target > v ? 0.03 : 0.12));

    // ------------------------------------------------------------------ roll and slots

    /// <summary>Replaces the roll; <paramref name="media"/> entries are filled in later by the loader.</summary>
    public void SetRoll(RollConfig config, SlotMedia?[] media)
    {
        foreach (var m in _media) m?.Player?.Close();
        _media = media;
        Config = config;
        _cache.Clear();
        _cacheOrder.Clear();
        _previous = -1;
        if (!HasMedia(Slot)) Slot = Math.Max(0, FirstFilled());
        _triggerTime = Now;
        foreach (var m in media) if (m?.Kind == MediaKind.Video) OpenVideo(m);
        Retrigger();
    }

    public void SetConfig(RollConfig config)
    {
        Config = config;
        foreach (var m in _media) if (m?.Player is { } p && m.Playing) p.SpeedRatio = RollConfig.Speeds[Config.VideoSpeed];
    }

    /// <summary>Called by the loader when a slot's media has been decoded.</summary>
    public void MediaReady(int slot)
    {
        if (slot == Slot) _triggerTime = Math.Min(_triggerTime, Now - 10);
        SlotChanged?.Invoke();
    }

    int FirstFilled()
    {
        for (int i = 0; i < _media.Length; i++) if (HasMedia(i)) return i;
        return -1;
    }

    /// <summary>Shows a slot (note on, arrow, tap). Re-triggering the shown slot restarts it.</summary>
    public void Trigger(int slot)
    {
        slot = (slot % _media.Length + _media.Length) % _media.Length;
        if (slot != Slot)
        {
            _previous = Slot;
            _previousTrigger = _triggerTime;
            Slot = slot;
        }
        _triggerTime = Now;
        Retrigger();
        SlotChanged?.Invoke();
    }

    void Retrigger()
    {
        if (_media[Slot] is { Kind: MediaKind.Video, Player: { } player } m)
        {
            if (Config.VideoRestart || !m.Playing) player.Position = TimeSpan.Zero;
            player.SpeedRatio = RollConfig.Speeds[Config.VideoSpeed];
            player.Play();
            m.Playing = true;
        }
    }

    /// <summary>The next/previous slot holding media, in <paramref name="direction"/>.</summary>
    public int Step(int direction)
    {
        for (int i = 1; i <= _media.Length; i++)
        {
            int s = ((Slot + direction * i) % _media.Length + _media.Length) % _media.Length;
            if (HasMedia(s)) return s;
        }
        return (Slot + direction + _media.Length) % _media.Length;
    }

    /// <summary>White-key effect on (note on / press) or off (note off / release).</summary>
    public void ApplyEffect(int effect, bool on)
    {
        if (on) _lit[effect] = Now;
        switch (effect)
        {
            case 0: if (on) Trigger(Step(-1)); break;
            case 1: if (on) Trigger(Step(1)); break;
            case 2:
                if (!on) break;
                var filled = Enumerable.Range(0, _media.Length).Where(i => HasMedia(i) && i != Slot).ToList();
                if (filled.Count > 0) Trigger(filled[_random.Next(filled.Count)]);
                break;
            case 3: if (on) Trigger(Math.Max(0, FirstFilled())); break;
            case Punch: if (on) _punchTime = Now; break;
            default: _held[effect] = on; break;
        }
    }

    public void ReleaseAll() => Array.Clear(_held);

    public void SetCameraFrame(Pixels frame) => _camera = frame;

    // ------------------------------------------------------------------ video

    void OpenVideo(SlotMedia m)
    {
        var player = new MediaPlayer { IsMuted = true, Volume = 0, ScrubbingEnabled = true };
        player.MediaOpened += (_, _) =>
        {
            m.Size = new Size(Math.Max(1, player.NaturalVideoWidth), Math.Max(1, player.NaturalVideoHeight));
            if (!m.Playing) player.Pause();
            SlotChanged?.Invoke();
        };
        player.MediaFailed += (_, _) => { m.Failed = true; SlotChanged?.Invoke(); };
        player.MediaEnded += (_, _) => Loop(m);
        player.Open(new Uri(m.Path));
        player.Pause();
        m.Player = player;
    }

    void Loop(SlotMedia m)
    {
        if (m.Player is not { } p) return;
        if (Config.VideoLoop) { p.Position = TimeSpan.Zero; p.Play(); }
        else { p.Pause(); m.Playing = false; }
    }

    void UpdatePlayers(double now)
    {
        bool transitioning = now - _triggerTime < TransitionSeconds;
        for (int i = 0; i < _media.Length; i++)
        {
            if (_media[i] is not { Player: { } p } m || !m.Playing) continue;
            if (i != Slot && !(i == _previous && transitioning)) { p.Pause(); m.Playing = false; continue; }
            // Photomatic plays at most 10 s of a video.
            if (p.Position > TimeSpan.FromSeconds(10)) Loop(m);
        }
    }

    // ------------------------------------------------------------------ render

    double TransitionSeconds => Config.Transition == 0 ? 0 : S(SoundParam.Attack) / 255.0 * 2.0;

    protected override void OnRender(DrawingContext dc)
    {
        var size = RenderSize;
        if (size.Width < 1 || size.Height < 1) return;
        double now = Now;
        var full = new Rect(size);
        dc.DrawRectangle(Config.Background switch { 1 => PaperWhite, 2 => Grey, _ => Brushes.Black }, null, full);
        var colors = Colors;

        var current = Layer(Slot, colors, now, _triggerTime);
        if (Config.Background == 3 && current is { } echo)
        {
            dc.PushOpacity(0.35);
            dc.DrawRectangle(echo.Brush, null, Fit(echo.Size, size, 1));
            dc.Pop();
        }

        double duration = TransitionSeconds, t = duration <= 0 ? 1 : Math.Clamp((now - _triggerTime) / duration, 0, 1);
        double e = t * t * (3 - 2 * t);
        if (t < 1 && _previous >= 0 && _previous != Slot && Layer(_previous, colors, now, _previousTrigger) is { } before)
        {
            switch (Config.Transition)
            {
                case 2: // slide: the new image pushes the old one out to the left
                    DrawLayer(dc, before, size, 1, 1, new Vector(-e * size.Width, 0), now, _previousTrigger);
                    if (current is { } c2) DrawLayer(dc, c2, size, 1, 1, new Vector((1 - e) * size.Width, 0), now, _triggerTime);
                    break;
                case 3: // zoom: the old image blows up and fades as the new one grows in
                    DrawLayer(dc, before, size, 1 - e, 1 + e * 0.6, default, now, _previousTrigger);
                    if (current is { } c3) DrawLayer(dc, c3, size, e, 0.7 + 0.3 * e, default, now, _triggerTime);
                    break;
                default:
                    DrawLayer(dc, before, size, 1, 1, default, now, _previousTrigger);
                    if (current is { } c1) DrawLayer(dc, c1, size, e, 1, default, now, _triggerTime);
                    break;
            }
        }
        else if (current is { } c) DrawLayer(dc, c, size, 1, 1, default, now, _triggerTime);

        if (_white > 0.002) { dc.PushOpacity(_white); dc.DrawRectangle(Brushes.White, null, full); dc.Pop(); }
        if (_black > 0.002) { dc.PushOpacity(_black); dc.DrawRectangle(Brushes.Black, null, full); dc.Pop(); }
    }

    readonly record struct LayerContent(Brush Brush, Size Size);

    LayerContent? Layer(int slot, ColorParams colors, double now, double triggered)
    {
        if (CameraOn && slot == Slot && _camera is { } cam) return new(new ImageBrush(CameraBitmap(cam, colors)), new Size(cam.Width, cam.Height));
        if (_media[slot] is not { Failed: false } m || !m.Ready) return null;
        switch (m.Kind)
        {
            case MediaKind.Video when m.Player is { } player:
                if (colors.IsIdentity)
                    return new(new DrawingBrush(new VideoDrawing { Player = player, Rect = new Rect(m.Size) }), m.Size);
                return new(new ImageBrush(GrabVideo(m, player, colors, now)), m.Size);
            case MediaKind.Gif when m.Frames is { Count: > 0 } frames:
                double t = (now - triggered) % Math.Max(0.02, m.GifLength.TotalSeconds);
                int index = 0;
                for (double acc = 0; index < frames.Count - 1; index++)
                {
                    acc += frames[index].Delay.TotalSeconds;
                    if (acc > t) break;
                }
                var f = frames[index];
                return new(new ImageBrush(Processed(f.Pixels, f.Bitmap, colors)), m.Size);
            case MediaKind.Image when m.Image is { } px && m.Bitmap is { } bmp:
                return new(new ImageBrush(Processed(px, bmp, colors)), m.Size);
        }
        return null;
    }

    BitmapSource Processed(Pixels px, BitmapSource original, ColorParams colors)
    {
        if (colors.IsIdentity) return original;
        var key = ((object)px, colors);
        if (_cache.TryGetValue(key, out var hit)) return hit;
        var result = ImageFx.Apply(px, colors).ToBitmap();
        _cache[key] = result;
        _cacheOrder.Enqueue(key);
        while (_cacheOrder.Count > 24) _cache.Remove(_cacheOrder.Dequeue());
        return result;
    }

    BitmapSource CameraBitmap(Pixels cam, ColorParams colors)
    {
        if (_cameraOut is null || _cameraOut.PixelWidth != cam.Width || _cameraOut.PixelHeight != cam.Height)
            _cameraOut = new WriteableBitmap(cam.Width, cam.Height, 96, 96, PixelFormats.Bgra32, null);
        var data = cam.Data;
        if (!colors.IsIdentity)
        {
            data = new byte[cam.Data.Length];
            ImageFx.Apply(cam.Data, data, cam.Width, cam.Height, colors);
        }
        _cameraOut.WritePixels(new Int32Rect(0, 0, cam.Width, cam.Height), data, cam.Stride, 0);
        return _cameraOut;
    }

    /// <summary>Color effects on video: renders the current frame on the CPU (at most 640 px wide, 30 fps).</summary>
    BitmapSource GrabVideo(SlotMedia m, MediaPlayer player, ColorParams colors, double now)
    {
        double k = Math.Min(1, 640 / m.Size.Width);
        int w = Math.Max(1, (int)(m.Size.Width * k)), h = Math.Max(1, (int)(m.Size.Height * k));
        if (m.Grab is null || m.Grab.PixelWidth != w || m.Grab.PixelHeight != h)
            m.Grab = new WriteableBitmap(w, h, 96, 96, PixelFormats.Bgra32, null);
        if (now - m.GrabTime < 1 / 30.0) return m.Grab;
        m.GrabTime = now;
        var visual = new DrawingVisual();
        using (var vdc = visual.RenderOpen()) vdc.DrawVideo(player, new Rect(0, 0, w, h));
        var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);
        var px = Pixels.From(rtb);
        var data = new byte[px.Data.Length];
        ImageFx.Apply(px.Data, data, w, h, colors);
        m.Grab.WritePixels(new Int32Rect(0, 0, w, h), data, w * 4, 0);
        return m.Grab;
    }

    /// <summary>The content rect for media of <paramref name="natural"/> size: 0 fit, 1 fill, 2 stretch.</summary>
    static Rect Fit(Size natural, Size size, int mode)
    {
        if (mode == 2 || natural.Width <= 0 || natural.Height <= 0) return new Rect(size);
        double k = mode == 1 ? Math.Max(size.Width / natural.Width, size.Height / natural.Height) : Math.Min(size.Width / natural.Width, size.Height / natural.Height);
        double w = natural.Width * k, h = natural.Height * k;
        return new Rect((size.Width - w) / 2, (size.Height - h) / 2, w, h);
    }

    void DrawLayer(DrawingContext dc, LayerContent layer, Size size, double opacity, double scale, Vector offset, double now, double triggered)
    {
        if (opacity <= 0.002) return;
        var brush = layer.Brush;
        var content = Fit(layer.Size, size, Config.Fit);
        if (Config.Border > 0 && Config.Fit != 2)
        {
            // Leave room for a white print border inside the stage.
            double pad = Config.Border == 1 ? 0.04 : 0.09, k = 1 - pad * 2;
            content = new Rect(size.Width / 2 - content.Width * k / 2, size.Height / 2 - content.Height * k / 2 - (Config.Border == 2 ? size.Height * 0.03 : 0), content.Width * k, content.Height * k);
        }

        if (brush is TileBrush tile)
        {
            switch (DoubleMode)
            {
                case 1: tile.Viewbox = new Rect(0, 0, 0.5, 1); tile.Viewport = new Rect(0, 0, 0.5, 1); tile.TileMode = TileMode.FlipX; break;
                case 2: tile.Viewbox = new Rect(0, 0, 0.5, 0.5); tile.Viewport = new Rect(0, 0, 0.5, 0.5); tile.TileMode = TileMode.FlipXY; break;
                case 3: tile.Viewport = new Rect(0, 0, 0.5, 0.5); tile.TileMode = TileMode.Tile; break;
                case 4: tile.Viewport = new Rect(0, 0, 1 / 3.0, 1 / 3.0); tile.TileMode = TileMode.Tile; break;
                case 5: tile.Viewport = new Rect(0, 0, 0.25, 0.25); tile.TileMode = TileMode.Tile; break;
            }
        }

        double zoom = 1 + S(SoundParam.Decay) / 255.0 * 2;
        double punch = 1 + 0.4 * Math.Exp(-(now - _punchTime) * 7);
        double age = now - triggered;
        double drift = Config.Drift == 0 ? 0 : Math.Min(1, age / (Config.Drift == 1 ? 12 : 4));
        drift = drift * drift * (3 - 2 * drift);
        double total = scale * zoom * punch * (1 + drift * 0.18);
        var center = new Point(size.Width / 2, size.Height / 2);
        var pan = new Vector(drift * size.Width * 0.03, -drift * size.Height * 0.02);

        dc.PushOpacity(opacity);
        dc.PushTransform(new TranslateTransform(offset.X + pan.X, offset.Y + pan.Y));
        dc.PushTransform(new ScaleTransform(total * (_held[FlipH] ? -1 : 1), total * (_held[FlipV] ? -1 : 1), center.X, center.Y));
        if (Config.Border > 0 && Config.Fit != 2)
        {
            double b = Math.Min(size.Width, size.Height) * (Config.Border == 1 ? 0.012 : 0.03);
            var paper = new Rect(content.X - b, content.Y - b, content.Width + 2 * b, content.Height + 2 * b + (Config.Border == 2 ? b * 3 : 0));
            dc.DrawRectangle(PaperWhite, null, paper);
        }

        double woozy = S(SoundParam.Release) / 255.0;
        if (woozy > 0.01)
        {
            int slices = (int)Math.Clamp(content.Height / 4, 24, 200);
            double speed = Config.WoozySpeed switch { 0 => 1.2, 2 => 6, _ => 3 };
            double amp = woozy * content.Width * 0.06, sliceH = content.Height / slices;
            for (int i = 0; i < slices; i++)
            {
                double y = content.Y + i * sliceH;
                double dx = Math.Sin(now * speed + i * 0.28 * 48 / slices) * amp + Math.Sin(now * speed * 0.37 + i * 0.07 * 48 / slices) * amp * 0.5;
                double dy = Math.Sin(now * speed * 0.8 + i * 0.5 * 48 / slices) * woozy * content.Height / 48 * 0.4;
                dc.PushClip(new RectangleGeometry(new Rect(content.X - amp * 2, y, content.Width + amp * 4, sliceH + 0.6)));
                dc.DrawRectangle(brush, null, new Rect(content.X + dx, content.Y + dy, content.Width, content.Height));
                dc.Pop();
            }
        }
        else dc.DrawRectangle(brush, null, content);
        dc.Pop();
        dc.Pop();
        dc.Pop();
    }
}
