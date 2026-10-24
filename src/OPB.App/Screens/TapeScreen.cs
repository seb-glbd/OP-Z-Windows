using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using OPB.App.Rendering;
using OPB.Core;
using OPB.Core.Model;

namespace OPB.App.Screens;

/// <summary>
/// The tape track: a scrolling waveform strip and two reels that turn while playing, at the
/// speed set by the tape's speed (P1) and fine (P2) parameters.
/// </summary>
[Screen(ScreenKeys.Tape)]
public sealed class TapeScreen : ScreenView
{
    const double TitleY = 40, Row0 = 82, WaveY = 171, WaveX0 = 525, BarStep = 17.5, BarW = 8;
    const double ReelY = 492, ReelR = 216, BandHalf = 47, BandReach = 190, TapeOuter = 128, HubR = 64;
    const double RevsPerSecond = 0.35, BarsPerSecond = 8;

    /// <summary>Bar heights of the official app's idle waveform.</summary>
    static readonly double[] Wave =
    [
        83, 39, 19, 12, 76, 39, 23, 12, 85, 39, 37, 39, 58, 29, 16, 10,
        37, 23, 12, 43, 56, 29, 16, 12, 85, 39, 25, 47, 23, 16, 43, 23,
    ];

    static readonly Brush TapeBlue = Theme.Freeze(new SolidColorBrush(Theme.TapeBlueColor));
    static readonly Brush RedBand = Theme.Freeze(new SolidColorBrush(Color.FromRgb(0x9B, 0x1A, 0x27)));
    static readonly Brush BlueBand = Theme.Freeze(new SolidColorBrush(Color.FromRgb(0x0F, 0x35, 0x56)));
    static readonly Brush Tape = Theme.Freeze(new SolidColorBrush(Color.FromRgb(0x6B, 0x3A, 0x36)));
    static readonly Brush Hub = Theme.Freeze(new SolidColorBrush(Color.FromRgb(0x50, 0x50, 0x50)));
    static readonly Brush Spindle = Theme.Freeze(new SolidColorBrush(Color.FromRgb(0x1A, 0x1A, 0x1A)));
    static readonly Brush Cap = Theme.Freeze(new SolidColorBrush(Color.FromRgb(0x4D, 0x4D, 0x4D)));

    readonly Stopwatch _clock = Stopwatch.StartNew();
    double _rotation, _scroll, _noteLevel, _visLevel, _lastFrame = -1;

    public TapeScreen(AppModel app) : base(app.Session)
    {
        Session.NoteEvent += (_, _, velocity) =>
        {
            if (velocity == 0) return;
            _noteLevel = Math.Max(_noteLevel, velocity / 127.0);
            UpdateAnimation();
        };
    }

    public override string Title => "TAPE";

    bool Playing => Session.ClockRunning || Session.Play.Playing;
    double Activity => Math.Max(_noteLevel, _visLevel);

    protected override bool WantsAnimation => Playing || Activity > 0.02 || Session.AudioVis != 0;

    /// <summary>Coarse speed: fully left is normal, turning right speeds up then slows to a stop. Fine: center normal, left half, right double.</summary>
    double SpeedFactor
    {
        get
        {
            var s = Session.CurrentSound[(int)TrackId.Tape];
            double p1 = s[(int)SoundParam.P1] / 255.0;
            double coarse = p1 < 0.6 ? 1 + p1 / 0.6 : 2 * (1 - (p1 - 0.6) / 0.4);
            return coarse * Math.Pow(2, (s[(int)SoundParam.P2] - 128) / 128.0);
        }
    }

    protected override void OnFrame(TimeSpan time)
    {
        double now = _clock.Elapsed.TotalSeconds;
        double dt = _lastFrame < 0 ? 0 : Math.Clamp(now - _lastFrame, 0, 0.1);
        _lastFrame = now;
        if (Playing)
        {
            double speed = SpeedFactor;
            _rotation = (_rotation + dt * 360 * RevsPerSecond * speed) % 360;
            _scroll = (_scroll + dt * BarsPerSecond * speed) % Wave.Length;
        }
        // AudioVis carries two 4-bit visualization levels.
        int vis = Session.AudioVis;
        double target = ((vis & 0x0F) + (vis >> 4)) / 30.0;
        _visLevel += (target - _visLevel) * Math.Min(1, dt * 12);
        _noteLevel *= Math.Exp(-dt * 4);
    }

    protected override void Render(DrawingContext dc)
    {
        Draw.Label(dc, "TAPE", DesignWidth / 2, TitleY, 56, Theme.Ink, Align.Center, Theme.Regular);
        Draw.HLine(dc, LeftEdge, RightEdge, Row0);

        bool live = Playing || Activity > 0.02;
        double gain = 0.4 + 0.6 * Math.Clamp(Activity, 0, 1);
        int offset = (int)_scroll;
        for (int i = 0; i < Wave.Length; i++)
        {
            double h = live ? Math.Max(8, Wave[(i + offset) % Wave.Length] * gain) : Wave[i];
            dc.DrawRectangle(Theme.Ink, null, new Rect(WaveX0 + i * BarStep - BarW / 2, WaveY - h / 2, BarW, h));
        }

        DrawReel(dc, new Point(562, ReelY), Theme.Red, RedBand, 35 + _rotation);
        DrawReel(dc, new Point(1039, ReelY), TapeBlue, BlueBand, -55 + _rotation);
    }

    static void DrawReel(DrawingContext dc, Point c, Brush color, Brush band, double angle)
    {
        dc.DrawEllipse(color, null, c, ReelR, ReelR);
        dc.PushTransform(new RotateTransform(angle, c.X, c.Y));
        var bandRect = new Rect(c.X - BandHalf, c.Y - BandReach, BandHalf * 2, BandReach * 2);
        dc.DrawRectangle(band, null, bandRect);
        // The wound tape shows through the window in the flange.
        dc.PushClip(new RectangleGeometry(bandRect));
        dc.DrawEllipse(Tape, null, c, TapeOuter, TapeOuter);
        dc.Pop();
        dc.DrawEllipse(color, null, c, HubR + 5, HubR + 5);
        dc.DrawEllipse(Hub, null, c, HubR, HubR);
        dc.DrawEllipse(Spindle, null, c, 50, 50);
        for (int i = 0; i < 3; i++)
        {
            dc.PushTransform(new RotateTransform(i * 120 + 20, c.X, c.Y));
            dc.DrawRectangle(Spindle, null, new Rect(c.X - 12, c.Y - 63, 24, 22));
            dc.Pop();
        }
        dc.DrawEllipse(Cap, null, c, 19, 19);
        dc.Pop();
    }
}
