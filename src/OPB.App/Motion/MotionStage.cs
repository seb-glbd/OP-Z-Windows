using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Media3D;
using OPB.Core;
using OPB.Core.Model;

namespace OPB.App.Motion;

/// <summary>What a pak sees each frame: dials, music envelopes, the camera and the global color treatment.</summary>
public sealed class MotionContext
{
    public double Time, Dt, Beat;
    /// <summary>Track 16 dials, 0..1: P1 hue, P2 speed, filter density, resonance size.</summary>
    public double Hue, Speed, Density, Size;
    public int Camera;
    /// <summary>Decaying envelopes (0..1) from the audio tracks.</summary>
    public double Kick, Snare, Hat, Bass, Synth;
    public int SynthNote = 60;
    public bool Invert;
    public double HueShift;
    public readonly List<(int Track, int Note, int Velocity)> Notes = new();

    /// <summary>A color from the pak's palette: hue in degrees relative to the hue dial.</summary>
    public Color Color(double hue, double sat, double val, double alpha = 1)
    {
        var c = Hsv(hue + Hue * 360 + HueShift, sat, val);
        if (Invert) c = System.Windows.Media.Color.FromRgb((byte)(255 - c.R), (byte)(255 - c.G), (byte)(255 - c.B));
        c.A = (byte)Math.Clamp(alpha * 255, 0, 255);
        return c;
    }

    /// <summary>Light added by emissive materials and lights: never inverted, black when off.</summary>
    public Color Glow(double hue, double sat, double val) => Hsv(hue + Hue * 360 + HueShift, sat, val);

    public Color Background => Invert ? Colors.White : System.Windows.Media.Color.FromRgb(6, 6, 8);

    public static Color Hsv(double h, double s, double v)
    {
        h = ((h % 360) + 360) % 360;
        s = Math.Clamp(s, 0, 1);
        v = Math.Clamp(v, 0, 1);
        double c = v * s, x = c * (1 - Math.Abs(h / 60 % 2 - 1)), m = v - c;
        (double r, double g, double b) = h switch
        {
            < 60 => (c, x, 0.0), < 120 => (x, c, 0.0), < 180 => (0.0, c, x),
            < 240 => (0.0, x, c), < 300 => (x, 0.0, c), _ => (c, 0.0, x),
        };
        return System.Windows.Media.Color.FromRgb((byte)((r + m) * 255), (byte)((g + m) * 255), (byte)((b + m) * 255));
    }
}

/// <summary>A built-in visual pak: 2D drawing, or a WPF element such as a Viewport3D, with named cameras.</summary>
public abstract class MotionPak
{
    public abstract string Name { get; }
    public abstract string Description { get; }
    public abstract string[] Cameras { get; }
    public virtual FrameworkElement? Element => null;
    public abstract void Update(MotionContext c);
    public virtual void Render(DrawingContext dc, Size size, MotionContext c) { }
}

/// <summary>Perspective projection for the 2D paks: a look-at camera with roll.</summary>
public readonly struct Projector
{
    readonly Point3D _eye;
    readonly Vector3D _f, _r, _u;
    readonly double _focal, _cx, _cy;

    public Projector(Point3D eye, Point3D target, double roll, double fovDegrees, Size size)
    {
        _eye = eye;
        _f = target - eye;
        _f.Normalize();
        var up = Math.Abs(_f.Y) > 0.98 ? new Vector3D(0, 0, 1) : new Vector3D(0, 1, 0);
        var r = Vector3D.CrossProduct(_f, up);
        r.Normalize();
        var u = Vector3D.CrossProduct(r, _f);
        double cos = Math.Cos(roll), sin = Math.Sin(roll);
        _r = r * cos + u * sin;
        _u = u * cos - r * sin;
        _focal = size.Height / 2 / Math.Tan(fovDegrees * Math.PI / 360);
        _cx = size.Width / 2;
        _cy = size.Height / 2;
    }

    /// <summary>Projects a point; false when it is behind the camera. <paramref name="depth"/> is the view distance.</summary>
    public bool Project(Point3D p, out Point screen, out double depth)
    {
        var d = p - _eye;
        depth = Vector3D.DotProduct(d, _f);
        if (depth < 0.1) { screen = default; return false; }
        screen = new Point(_cx + Vector3D.DotProduct(d, _r) / depth * _focal, _cy - Vector3D.DotProduct(d, _u) / depth * _focal);
        return true;
    }

    public double Scale(double depth) => _focal / Math.Max(0.1, depth);
}

/// <summary>
/// The MOTION visual: runs the active pak, feeds it note envelopes from every track, and applies the held
/// track 16 effects to the whole picture (zoom/rotate/shake transforms, blur, mirror/kaleido via VisualBrush,
/// strobe and outs as overlays; invert and color shift through the pak palette).
/// </summary>
public sealed class MotionStage : FrameworkElement
{
    public static readonly string[] EffectNames =
        ["INVERT", "STROBE", "ZOOM", "ROTATE", "COLOR SHIFT", "MIRROR", "KALEIDO", "BLUR", "SHAKE", "PULSE", "SLOW MO", "FREEZE", "BLACK OUT", "WHITE OUT"];
    const int Invert = 0, Strobe = 1, Zoom = 2, Rotate = 3, ColorShift = 4, Mirror = 5, Kaleido = 6, Blur = 7, Shake = 8, Pulse = 9, Slow = 10, Freeze = 11, BlackOut = 12, WhiteOut = 13;

    readonly OpzSession _session;
    readonly Grid _root = new() { ClipToBounds = true };
    readonly Grid _content = new();
    readonly Layer _paint, _overlay;
    readonly ScaleTransform _scale = new();
    readonly RotateTransform _rotate = new();
    readonly TranslateTransform _shift = new();
    readonly BlurEffect _blur = new() { Radius = 0, KernelType = KernelType.Gaussian };
    readonly Stopwatch _clock = Stopwatch.StartNew();
    readonly bool[] _held = new bool[EffectNames.Length];
    readonly Random _random = new();
    readonly MotionContext _c = new();
    double _last, _zoom = 1, _angle, _shake, _blurAmount, _black, _white, _pulsePhase;
    bool _animating;
    MotionPak _pak;

    public MotionStage(OpzSession session, IReadOnlyList<MotionPak> paks)
    {
        _session = session;
        Paks = paks;
        _pak = paks[0];
        IsHitTestVisible = false;
        _paint = new Layer(dc => _pak.Render(dc, _paint!.RenderSize, _c));
        _overlay = new Layer(DrawOverlay);
        _content.RenderTransform = new TransformGroup { Children = { _scale, _rotate, _shift } };
        _content.RenderTransformOrigin = new Point(0.5, 0.5);
        _root.Children.Add(_content);
        _content.Children.Add(_paint);
        AddVisualChild(_root);
        AddVisualChild(_overlay);
        SetPak(0);
        Loaded += (_, _) => SetAnimating(true);
        Unloaded += (_, _) => SetAnimating(false);
        session.NoteEvent += Note;
    }

    public IReadOnlyList<MotionPak> Paks { get; }
    public MotionPak Pak => _pak;
    public int Camera => _c.Camera;
    public bool IsHeld(int effect) => _held[effect];

    /// <summary>Per-track activity (0..1) for the meters on the screen.</summary>
    public double[] Activity { get; } = new double[16];

    public void SetPak(int index)
    {
        _content.Children.Clear();
        _pak = Paks[(index % Paks.Count + Paks.Count) % Paks.Count];
        if (_pak.Element is { } element) _content.Children.Add(element);
        _content.Children.Add(_paint);
        _c.Camera %= _pak.Cameras.Length;
    }

    public void SetCamera(int camera) => _c.Camera = (camera % _pak.Cameras.Length + _pak.Cameras.Length) % _pak.Cameras.Length;

    public void SetEffect(int effect, bool on) => _held[effect] = on;

    public void ReleaseAll() => Array.Clear(_held);

    /// <summary>A note from an audio track (track 16 is handled by the screen).</summary>
    public void Note(int track, int note, int velocity)
    {
        if (track == 15 || velocity == 0) return;
        double v = 0.45 + velocity / 127.0 * 0.55;
        Activity[track] = Math.Max(Activity[track], v);
        switch ((TrackId)track)
        {
            case TrackId.Kick: _c.Kick = Math.Max(_c.Kick, v); break;
            case TrackId.Snare: _c.Snare = Math.Max(_c.Snare, v); break;
            case TrackId.Perc or TrackId.Sample: _c.Hat = Math.Max(_c.Hat, v); break;
            case TrackId.Bass: _c.Bass = Math.Max(_c.Bass, v); break;
            case TrackId.Lead or TrackId.Arp or TrackId.Chord: _c.Synth = Math.Max(_c.Synth, v); _c.SynthNote = note; break;
            default: _c.Hat = Math.Max(_c.Hat, v * 0.5); break;
        }
        if (_c.Notes.Count < 64) _c.Notes.Add((track, note, velocity));
    }

    void SetAnimating(bool on)
    {
        if (on == _animating) return;
        _animating = on;
        if (on) { _last = _clock.Elapsed.TotalSeconds; CompositionTarget.Rendering += OnRendering; }
        else CompositionTarget.Rendering -= OnRendering;
    }

    /// <summary>Advances the visual by one frame (also used for still snapshots).</summary>
    public void Step(double dt)
    {
        var sound = _session.CurrentSound[15];
        _c.Hue = sound[(int)SoundParam.P1] / 255.0;
        _c.Speed = sound[(int)SoundParam.P2] / 255.0;
        _c.Density = sound[(int)SoundParam.Filter] / 255.0;
        _c.Size = sound[(int)SoundParam.Resonance] / 255.0;

        double tempo = _session.Global?.Tempo is int t and >= 40 ? t : 120;
        if (_session.ClockRunning) _c.Beat = _session.ClockTicks / 24.0;
        else _c.Beat += dt * tempo / 60;

        if (_held[Pulse])
        {
            double phase = Math.Floor(_c.Beat * 2);
            if (phase != _pulsePhase) { _pulsePhase = phase; _c.Kick = 1; _c.Notes.Add((0, 36, 110)); }
        }

        double timeScale = _held[Freeze] ? 0 : _held[Slow] ? 0.2 : 1;
        _c.Dt = dt * timeScale;
        _c.Time += _c.Dt * (0.25 + _c.Speed * 1.75);
        _c.Invert = _held[Invert];
        if (_held[ColorShift]) _c.HueShift += dt * 220;

        _pak.Update(_c);
        _c.Notes.Clear();

        double decay = _c.Dt;
        _c.Kick *= Math.Exp(-decay * 7);
        _c.Snare *= Math.Exp(-decay * 6);
        _c.Hat *= Math.Exp(-decay * 12);
        _c.Bass *= Math.Exp(-decay * 3);
        _c.Synth *= Math.Exp(-decay * 2.5);
        for (int i = 0; i < Activity.Length; i++) Activity[i] *= Math.Exp(-dt * 5);

        _zoom += ((_held[Zoom] ? 1.7 + _c.Kick * 0.2 : 1) - _zoom) * Math.Min(1, dt * 10);
        if (_held[Rotate]) _angle += dt * (60 + _c.Speed * 240);
        else _angle -= _angle * Math.Min(1, dt * 6);
        _shake += ((_held[Shake] ? 1 : 0) - _shake) * Math.Min(1, dt * 12);
        _blurAmount += ((_held[Blur] ? 1 : 0) - _blurAmount) * Math.Min(1, dt * 10);
        _black += ((_held[BlackOut] ? 1 : 0) - _black) * Math.Min(1, dt * (_held[BlackOut] ? 30 : 8));
        _white += ((_held[WhiteOut] ? 1 : 0) - _white) * Math.Min(1, dt * (_held[WhiteOut] ? 30 : 8));

        _scale.ScaleX = _scale.ScaleY = _zoom * (1 + _c.Kick * 0.03);
        _rotate.Angle = _angle;
        double amp = _shake * (14 + _c.Kick * 40);
        _shift.X = (_random.NextDouble() - 0.5) * amp;
        _shift.Y = (_random.NextDouble() - 0.5) * amp;
        _blur.Radius = _blurAmount * 18;
        _content.Effect = _blurAmount > 0.02 ? _blur : null;
        _root.Background = new SolidColorBrush(_c.Background);
        _paint.InvalidateVisual();
        _overlay.InvalidateVisual();
    }

    void OnRendering(object? sender, EventArgs e)
    {
        double now = _clock.Elapsed.TotalSeconds;
        Step(Math.Clamp(now - _last, 0, 0.1));
        _last = now;
    }

    void DrawOverlay(DrawingContext dc)
    {
        var rect = new Rect(_overlay.RenderSize);
        if (_held[Mirror] || _held[Kaleido])
        {
            var brush = new VisualBrush(_root)
            {
                Viewbox = _held[Kaleido] ? new Rect(0, 0, 0.5, 0.5) : new Rect(0, 0, 0.5, 1),
                Viewport = _held[Kaleido] ? new Rect(0, 0, 0.5, 0.5) : new Rect(0, 0, 0.5, 1),
                TileMode = _held[Kaleido] ? TileMode.FlipXY : TileMode.FlipX,
                Stretch = Stretch.Fill,
            };
            dc.DrawRectangle(brush, null, rect);
        }
        if (_held[Strobe] && (_c.Beat * 4 % 1) < 0.45)
            dc.DrawRectangle(_c.Invert ? Brushes.Black : Brushes.White, null, rect);
        if (_black > 0.003) { dc.PushOpacity(_black); dc.DrawRectangle(Brushes.Black, null, rect); dc.Pop(); }
        if (_white > 0.003) { dc.PushOpacity(_white); dc.DrawRectangle(Brushes.White, null, rect); dc.Pop(); }
    }

    protected override int VisualChildrenCount => 2;
    protected override Visual GetVisualChild(int index) => index == 0 ? _root : _overlay;

    protected override Size MeasureOverride(Size availableSize)
    {
        _root.Measure(availableSize);
        _overlay.Measure(availableSize);
        return new Size(0, 0);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        _root.Arrange(new Rect(finalSize));
        _overlay.Arrange(new Rect(finalSize));
        return finalSize;
    }

    /// <summary>A plain element that draws through a callback.</summary>
    sealed class Layer(Action<DrawingContext> render) : FrameworkElement
    {
        protected override void OnRender(DrawingContext dc)
        {
            if (RenderSize.Width >= 1 && RenderSize.Height >= 1) render(dc);
        }
    }
}
