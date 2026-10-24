using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using OPB.App.Motion;
using OPB.App.Rendering;
using OPB.Core.Model;

namespace OPB.App.Screens;

/// <summary>
/// MOTION: real-time visuals from built-in paks (the official videopaks are Unity asset bundles and are not
/// supported), driven by notes from every track: kick pulses, snare flashes, perc/sample sparkle, bass swells,
/// lead/arp/chord notes light individual elements.
/// <para>
/// Track 16 notes, folded every 24 notes from F (note 53, the OP-Z's lowest key at octave 0):
/// the 10 black keys cut between cameras (camera = black key index modulo the pak's camera count);
/// the 14 white keys apply effects while held, in key order F G A B C D E F G A B C D E: invert, strobe, zoom,
/// rotate, color shift, mirror, kaleido, blur, shake, pulse, slow mo, freeze, black out, white out.
/// Dials: P1 hue, P2 speed, filter density, resonance size.
/// </para>
/// </summary>
[Screen(ScreenKeys.Motion)]
public sealed class MotionScreen : StageScreen
{
    const int NoteBase = 53;
    static readonly int[] WhitePositions = [0, 2, 4, 6, 7, 9, 11, 12, 14, 16, 18, 19, 21, 23];
    static readonly int[] BlackPositions = [1, 3, 5, 8, 10, 13, 15, 17, 20, 22];
    static readonly Rect PlayRect = new(286, 92, 1028, 578.25);
    const double KnobH = 120, StripY = 678, StripH = 60;

    readonly MotionStage _stage;
    readonly DispatcherTimer _demoTimer = new(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(10) };
    readonly Stopwatch _demoClock = new();
    bool _pakList;
    int _demoStep = -1;

    public MotionScreen(AppModel app) : this(app, new MotionStage(app.Session, BuiltInPaks.Create())) { }

    MotionScreen(AppModel app, MotionStage stage) : base(app, stage)
    {
        _stage = stage;
        Session.NoteEvent += OnNote;
        _demoTimer.Tick += (_, _) => DemoTick();
        var args = Environment.GetCommandLineArgs();
        int i = Array.IndexOf(args, "--motion-pak");
        if (i >= 0 && i + 1 < args.Length && int.TryParse(args[i + 1], out int pak)) _stage.SetPak(pak);
        if (args.Contains("--motion-demo")) ToggleDemo();
    }

    public override string Title => "MOTION";
    protected override Rect StageRect => PlayRect;
    protected override bool WantsAnimation => true;
    protected override void OnHidden() => _stage.ReleaseAll();

    void OnNote(int track, int note, int velocity)
    {
        if (track != MotionTrack) return;
        int pos = ((note - NoteBase) % 24 + 24) % 24;
        int black = Array.IndexOf(BlackPositions, pos), white = Array.IndexOf(WhitePositions, pos);
        if (black >= 0) { if (velocity > 0) _stage.SetCamera(black); }
        else if (white >= 0) _stage.SetEffect(white, velocity > 0);
    }

    // ------------------------------------------------------------------ demo groove

    void ToggleDemo()
    {
        if (_demoTimer.IsEnabled) { _demoTimer.Stop(); _demoStep = -1; }
        else { _demoClock.Restart(); _demoTimer.Start(); }
        InvalidateVisual();
    }

    /// <summary>A simple four-on-the-floor groove fed straight into the visual (no MIDI is sent).</summary>
    void DemoTick()
    {
        double tempo = Session.Global?.Tempo is int t and >= 40 ? t : 120;
        int step = (int)(_demoClock.Elapsed.TotalSeconds * tempo / 60 * 4);
        while (_demoStep < step)
        {
            int s = ++_demoStep % 16, bar = _demoStep / 16 % 4;
            if (s % 4 == 0 || (bar == 3 && s == 14)) _stage.Note(0, 36, 110);
            if (s is 4 or 12) _stage.Note(1, 38, 100);
            if (s % 2 == 1) _stage.Note(2, 42, 60 + s * 3);
            if (s is 0 or 3 or 6 or 10) _stage.Note(4, 36 + (bar == 2 ? 5 : 0) + (s == 10 ? 7 : 0), 100);
            if (bar % 2 == 1 && s % 2 == 0) _stage.Note(6, 60 + new[] { 0, 3, 7, 10, 12, 7, 3, 0 }[s / 2], 90);
            if (s == 0 && bar == 0) _stage.Note(7, 60, 80);
        }
    }

    // ------------------------------------------------------------------ render

    protected override void RenderScreen(DrawingContext dc)
    {
        var pak = _stage.Pak;
        Header(dc, "MOTION");
        HeaderButton(dc, LeftEdge, LeftW, "PAKS", _pakList, () => { _pakList = !_pakList; UpdateStage(!_pakList); });
        HeaderButton(dc, RightX, RightEdge, "DEMO", _demoTimer.IsEnabled, ToggleDemo);
        Draw.Label(dc, $"CAM {_stage.Camera + 1}", RightX - 20, 42, 40, Theme.Ink, Align.Right, Theme.Regular);
        Draw.Label(dc, pak.Cameras[_stage.Camera], RightX - 150, 44, 24, Theme.Mid, Align.Right);

        (string, SoundParam)[] dials = [("HUE", SoundParam.P1), ("SPEED", SoundParam.P2), ("DENSITY", SoundParam.Filter), ("SIZE", SoundParam.Resonance)];
        for (int i = 0; i < 4; i++)
            KnobCell(dc, new Rect(0, HeaderY + i * KnobH, LeftW, KnobH), dials[i].Item1, dials[i].Item2, Theme.Dials[i]);
        DrawMeters(dc);
        DrawEffects(dc);
        NameBar(dc, pak.Name, "BUILT-IN PAK", () => { _pakList = !_pakList; UpdateStage(!_pakList); });

        if (_pakList) { DrawPakList(dc); return; }
        DrawArrows(dc);
        DrawCameras(dc);
    }

    void DrawMeters(DrawingContext dc)
    {
        double y0 = HeaderY + 4 * KnobH;
        Draw.Label(dc, "TRACKS", 18, y0 + 26, 26, Theme.Mid);
        double x0 = 18, w = (LeftW - 36) / 16.0, top = y0 + 52, bottom = BarY - 16;
        for (int t = 0; t < 16; t++)
        {
            var slot = new Rect(x0 + t * w + 1.5, top, w - 3, bottom - top);
            dc.DrawRectangle(Theme.Rule, null, slot);
            double level = t == MotionTrack ? (Enumerable.Range(0, 14).Any(_stage.IsHeld) ? 1 : 0) : _stage.Activity[t];
            double h = slot.Height * Math.Clamp(level, 0, 1);
            if (h > 0.5) dc.DrawRectangle(t == MotionTrack ? Theme.Red : Theme.Ink, null, new Rect(slot.X, slot.Bottom - h, slot.Width, h));
        }
    }

    void DrawEffects(DrawingContext dc)
    {
        double rowH = (BarY - HeaderY) / MotionStage.EffectNames.Length;
        for (int i = 0; i < MotionStage.EffectNames.Length; i++)
        {
            var r = new Rect(RightX, HeaderY + i * rowH, DesignWidth - RightX, rowH);
            bool lit = _stage.IsHeld(i);
            if (lit) dc.DrawRectangle(Theme.Ink, null, new Rect(r.X + 1, r.Y + 1, RightEdge - r.X, r.Height - 1));
            Draw.Label(dc, MotionStage.EffectNames[i], r.X + 18, r.Y + rowH / 2, 25, lit ? Theme.White : Theme.Ink, Align.Left, Theme.Regular);
            Draw.Label(dc, Display.NoteName(NoteBase + WhitePositions[i]), r.Right - 14, r.Y + rowH / 2, 20, Theme.Mid, Align.Right);
            if (i > 0) Draw.HLine(dc, RightX, RightEdge, r.Y);
            int effect = i;
            AddHit(r, down: () => _stage.SetEffect(effect, true), up: () => _stage.SetEffect(effect, false));
        }
    }

    void DrawArrows(DrawingContext dc)
    {
        var pen = new Pen(Theme.Ink, 5) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
        double cy = PlayRect.Y + PlayRect.Height / 2;
        foreach (int dir in new[] { -1, 1 })
        {
            double cx = dir < 0 ? (LeftW + PlayRect.X) / 2 : (PlayRect.Right + RightX) / 2;
            var g = new StreamGeometry();
            using (var c = g.Open())
            {
                c.BeginFigure(new Point(cx - dir * 10, cy - 28), false, false);
                c.LineTo(new Point(cx + dir * 12, cy), true, true);
                c.LineTo(new Point(cx - dir * 10, cy + 28), true, true);
            }
            g.Freeze();
            dc.DrawGeometry(null, pen, g);
            double x0 = dir < 0 ? LeftW : PlayRect.Right;
            AddHit(new Rect(x0, PlayRect.Y, PlayRect.X - LeftW, PlayRect.Height), up: () => _stage.SetPak(_stage.Paks.ToList().IndexOf(_stage.Pak) + dir));
        }
    }

    void DrawCameras(DrawingContext dc)
    {
        var cams = _stage.Pak.Cameras;
        double x0 = LeftW + 10, w = (RightX - LeftW - 20) / cams.Length;
        for (int i = 0; i < cams.Length; i++)
        {
            var r = new Rect(x0 + i * w + 3, StripY, w - 6, StripH);
            bool on = i == _stage.Camera;
            if (on) dc.DrawRectangle(Theme.Ink, null, r);
            else dc.DrawRectangle(null, new Pen(Theme.Ink, 2), new Rect(r.X + 1, r.Y + 1, r.Width - 2, r.Height - 2));
            Draw.Label(dc, (i + 1).ToString(), r.X + 16, r.Y + r.Height / 2, 30, on ? Theme.Yellow : Theme.Ink, Align.Left, Theme.Regular);
            Draw.Label(dc, cams[i], r.X + 52, r.Y + r.Height / 2, 26, on ? Theme.White : Theme.Ink, Align.Left, Theme.Regular);
            string keys = string.Join(" ", BlackPositions.Where((_, k) => k % cams.Length == i).Select(p => Display.NoteName(NoteBase + p)));
            Draw.Label(dc, keys, r.Right - 12, r.Y + r.Height / 2, 18, Theme.Mid, Align.Right);
            int cam = i;
            AddHit(r, up: () => _stage.SetCamera(cam));
        }
    }

    void DrawPakList(DrawingContext dc)
    {
        var area = new Rect(LeftW, HeaderY, RightX - LeftW, BarY - HeaderY);
        dc.DrawRectangle(Theme.Background, null, area);
        Draw.Label(dc, "INSTALLED PAKS", area.X + 30, area.Y + 44, 34, Theme.Mid);
        double rowH = 112;
        for (int i = 0; i < _stage.Paks.Count; i++)
        {
            var pak = _stage.Paks[i];
            var r = new Rect(area.X + 30, area.Y + 84 + i * rowH, area.Width - 60, rowH - 12);
            dc.DrawRectangle(null, Theme.RulePen, r);
            Draw.Label(dc, (i + 1).ToString(), r.X + 26, r.Y + r.Height / 2, 44, Theme.Ink, Align.Left, Theme.Regular);
            Draw.Label(dc, pak.Name, r.X + 90, r.Y + 32, 34, Theme.Ink, Align.Left, Theme.Regular);
            Draw.Label(dc, pak.Description, r.X + 90, r.Y + 68, 22, Theme.Mid);
            Draw.Label(dc, $"{pak.Cameras.Length} CAMERAS", r.Right - 24, r.Y + r.Height / 2, 24, Theme.Mid, Align.Right);
            if (pak == _stage.Pak) Draw.Frame(dc, r, Theme.Red);
            int index = i;
            AddHit(r, up: () => { _stage.SetPak(index); _pakList = false; UpdateStage(); });
        }
        Draw.Label(dc, "OFFICIAL VIDEOPAKS ARE UNITY ASSET BUNDLES AND CAN'T RUN HERE; THESE PAKS ARE BUILT INTO OP-B", area.X + 30, area.Bottom - 34, 21, Theme.Mid);
    }
}
