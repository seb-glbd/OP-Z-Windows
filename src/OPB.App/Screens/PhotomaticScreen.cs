using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using OPB.App.Photomatic;
using OPB.App.Rendering;
using OPB.Core.Model;

namespace OPB.App.Screens;

/// <summary>
/// PHOTOMATIC: sequences photos, GIFs and videos from 10 rolls of 24 slots, driven by track 16 (motion).
/// <para>
/// Track 16 notes (MIDI note numbers as the OP-Z sends them; its lowest key F at octave 0 is note 53):
/// <list type="bullet">
/// <item>53..76 (octave 0, both keyboard octaves F..E) and any lower note, folded every 24 notes:
/// the 24 keys chromatically select slots 1..24 (F = slot 1, F# = slot 2 ... E = slot 24).</item>
/// <item>77 and up (octave +2): the 14 white keys trigger effects in key order F G A B C D E F G A B C D E:
/// previous, next, random, first image, invert, flip horizontal, flip vertical, punch zoom, white out,
/// black out, sharpen, kill red, kill green, kill blue (held effects last until note off);
/// the 10 black keys select rolls 1..10.</item>
/// <item>Shift (on the OP-Z) + any black key: rolls 1..10 in black key order.</item>
/// </list>
/// Dials (track 16 sound bytes): P1 hue, P2 saturation, filter brightness, resonance contrast; second page
/// (shown while shift is held) attack crossfade, decay zoom, sustain double, release woozy.
/// </para>
/// </summary>
[Screen(ScreenKeys.Photomatic)]
public sealed class PhotomaticScreen : StageScreen
{
    public const int SlotNoteBase = 53, EffectNoteBase = 77;
    static readonly int[] WhitePositions = [0, 2, 4, 6, 7, 9, 11, 12, 14, 16, 18, 19, 21, 23];
    static readonly int[] BlackPositions = [1, 3, 5, 8, 10, 13, 15, 17, 20, 22];
    static readonly Rect PlayRect = new(286, 92, 1028, 578.25);
    static readonly Rect EditPreview = new(796, 170, 568, 319.5);
    const double KnobH = 120, StripY = 678, StripH = 60;
    static readonly string[] Pages = ["APPEARANCE", "MOVEMENT", "VIDEO", "ROLL"];

    readonly PhotomaticStage _stage;
    readonly PhotoLibrary _library;
    RollConfig _config = new();
    int _roll, _loadGeneration, _editPage;
    bool _editing, _rollList, _shiftPage;
    string? _message;
    readonly Stopwatch _messageAge = new();
    CameraCapture? _camera;
    List<string>? _cameraNames;
    int _cameraIndex, _framePending;
    Pixels? _lastFrame;

    public PhotomaticScreen(AppModel app) : this(app, new PhotomaticStage(app.Session)) { }

    PhotomaticScreen(AppModel app, PhotomaticStage stage) : base(app, stage)
    {
        _stage = stage;
        _stage.SlotChanged += InvalidateVisual;
        var args = Environment.GetCommandLineArgs();
        int i = Array.IndexOf(args, "--photomatic-dir");
        _library = new PhotoLibrary(i >= 0 && i + 1 < args.Length ? args[i + 1] : null);
        AllowDrop = true;
        Session.NoteEvent += OnNote;
        LoadRoll(0);
        int view = Array.IndexOf(args, "--photomatic-view");
        if (view >= 0 && view + 1 < args.Length)
        {
            // Diagnostics for snapshots: open the roll list or an editor page (edit0..edit3).
            if (args[view + 1] == "rolls") { _rollList = true; Stage.Visibility = Visibility.Hidden; }
            else if (args[view + 1].StartsWith("edit")) { _editing = true; int.TryParse(args[view + 1][4..], out _editPage); }
        }
    }

    public override string Title => "PHOTOMATIC";

    protected override Rect StageRect => _editing ? EditPreview : PlayRect;

    /// <summary>Keeps the live preview fresh while the visual is on video out.</summary>
    protected override bool WantsAnimation => VideoOut.IsActive;

    protected override void OnHidden() => _stage.ReleaseAll();

    // ------------------------------------------------------------------ notes

    void OnNote(int track, int note, int velocity)
    {
        if (track != MotionTrack) return;
        bool on = velocity > 0;
        int k = note - SlotNoteBase, pos = ((k % 24) + 24) % 24;
        int black = Array.IndexOf(BlackPositions, pos), white = Array.IndexOf(WhitePositions, pos);
        if (on && black >= 0 && (Session.Ui.Shift || note >= EffectNoteBase)) LoadRoll(black);
        else if (note >= EffectNoteBase) { if (white >= 0) _stage.ApplyEffect(white, on); }
        else if (on) _stage.Trigger(pos);
        InvalidateVisual();
    }

    // ------------------------------------------------------------------ rolls and media

    void LoadRoll(int roll)
    {
        _roll = roll;
        _config = _library.Load(roll);
        var media = new SlotMedia?[PhotoLibrary.SlotCount];
        for (int i = 0; i < media.Length; i++)
            if (_library.SlotPath(roll, _config, i) is { } path && PhotoLibrary.KindOf(path) is var kind and not MediaKind.None)
                media[i] = new SlotMedia(path, kind);
        _stage.SetRoll(_config, media);
        int generation = ++_loadGeneration;
        var order = Enumerable.Range(0, media.Length).OrderBy(s => (s - _stage.Slot + media.Length) % media.Length).Where(s => media[s] is { Kind: MediaKind.Image or MediaKind.Gif }).ToList();
        var dispatcher = Dispatcher;
        // Decode on an STA worker (GIF compositing uses visuals); results are frozen and handed to the UI thread.
        var thread = new Thread(() =>
        {
            foreach (int slot in order)
            {
                if (generation != _loadGeneration) return;
                var m = media[slot]!;
                try
                {
                    if (m.Kind == MediaKind.Gif)
                    {
                        var frames = ImageFx.LoadGif(m.Path, 960).Select(f => (Pixels.From(f.Frame), f.Frame, f.Delay)).ToList();
                        var thumb = Thumb(frames[0].Frame);
                        dispatcher.BeginInvoke(() =>
                        {
                            m.Frames = frames;
                            m.GifLength = TimeSpan.FromTicks(frames.Sum(f => f.Delay.Ticks));
                            m.Size = new Size(frames[0].Frame.PixelWidth, frames[0].Frame.PixelHeight);
                            m.Thumb = thumb;
                            _stage.MediaReady(slot);
                        });
                    }
                    else
                    {
                        var bmp = ImageFx.Load(m.Path, 1600);
                        var px = Pixels.From(bmp);
                        var thumb = Thumb(bmp);
                        dispatcher.BeginInvoke(() =>
                        {
                            m.Image = px;
                            m.Bitmap = bmp;
                            m.Size = new Size(bmp.PixelWidth, bmp.PixelHeight);
                            m.Thumb = thumb;
                            _stage.MediaReady(slot);
                        });
                    }
                }
                catch (Exception)
                {
                    dispatcher.BeginInvoke(() => { m.Failed = true; InvalidateVisual(); });
                }
            }
        }) { IsBackground = true, Name = "photomatic loader" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        InvalidateVisual();
    }

    static BitmapSource Thumb(BitmapSource source)
    {
        double k = 96.0 / Math.Max(1, source.PixelHeight);
        var thumb = new WriteableBitmap(new TransformedBitmap(source, new ScaleTransform(k, k)));
        thumb.Freeze();
        return thumb;
    }

    void Import(IEnumerable<string> files, int firstSlot)
    {
        int slot = firstSlot, count = 0;
        foreach (var file in files.Where(f => PhotoLibrary.KindOf(f) != MediaKind.None).Take(PhotoLibrary.SlotCount))
        {
            try { _library.Import(_roll, _config, slot, file); count++; }
            catch (Exception ex) { Say($"IMPORT FAILED: {ex.Message}"); }
            slot = (slot + 1) % PhotoLibrary.SlotCount;
        }
        if (count == 0) return;
        LoadRoll(_roll);
        _stage.Trigger(firstSlot);
        Say(count == 1 ? $"IMPORTED TO SLOT {firstSlot + 1}" : $"IMPORTED {count} FILES");
    }

    void OpenLibrary()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = $"Import into {_config.Name}, slot {_stage.Slot + 1}",
            Filter = "Photos and videos|*.png;*.jpg;*.jpeg;*.gif;*.bmp;*.mp4;*.mov;*.wmv|All files|*.*",
            Multiselect = true,
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true) Import(dialog.FileNames, _stage.Slot);
    }

    protected override void OnDrop(DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files) return;
        var p = ToDesign(e.GetPosition(this));
        int slot = p.Y >= StripY && p.Y < StripY + StripH ? SlotAt(p.X) : _stage.Slot;
        Import(files, slot < 0 ? _stage.Slot : slot);
        e.Handled = true;
    }

    void Say(string message)
    {
        _message = message;
        _messageAge.Restart();
        InvalidateVisual();
    }

    void SaveConfig()
    {
        _library.Save(_roll, _config);
        _stage.SetConfig(_config);
        InvalidateVisual();
    }

    // ------------------------------------------------------------------ camera

    void ToggleCamera()
    {
        if (_camera is not null) { StopCamera(); return; }
        _cameraNames ??= CameraCapture.Devices();
        if (_cameraNames.Count == 0) { Say("NO CAMERA FOUND"); return; }
        StartCamera(_cameraIndex % _cameraNames.Count);
    }

    void StartCamera(int index)
    {
        StopCamera();
        _cameraIndex = index;
        var camera = new CameraCapture(index);
        camera.Frame += px =>
        {
            _lastFrame = px;
            if (Interlocked.Exchange(ref _framePending, 1) == 1) return;
            Dispatcher.BeginInvoke(() =>
            {
                _framePending = 0;
                if (_camera == camera && _lastFrame is { } f) _stage.SetCameraFrame(f);
            });
        };
        camera.Failed += error => Dispatcher.BeginInvoke(() => { if (_camera == camera) { StopCamera(); Say($"CAMERA: {error}"); } });
        _camera = camera;
        _stage.CameraOn = true;
        Say(_cameraNames is { } names && index < names.Count ? names[index].ToUpperInvariant() : "CAMERA ON");
    }

    void StopCamera()
    {
        _camera?.Dispose();
        _camera = null;
        _lastFrame = null;
        _stage.CameraOn = false;
        InvalidateVisual();
    }

    void FlipCamera()
    {
        _cameraNames ??= CameraCapture.Devices();
        if (_cameraNames.Count < 2) { Say(_cameraNames.Count == 0 ? "NO CAMERA FOUND" : "ONLY ONE CAMERA"); return; }
        StartCamera((_cameraIndex + 1) % _cameraNames.Count);
    }

    void Snap()
    {
        if (_camera is null || _lastFrame is not { } frame) { Say("TURN THE CAMERA ON TO SNAP"); return; }
        int slot = _stage.Slot;
        var path = _library.NewSnapshotPath(_roll, slot);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(frame.ToBitmap()));
        using (var file = File.Create(path)) encoder.Save(file);
        _library.Import(_roll, _config, slot, path);
        StopCamera();
        LoadRoll(_roll);
        _stage.Trigger(slot);
        Say($"SNAPPED TO SLOT {slot + 1}");
    }

    // ------------------------------------------------------------------ render

    protected override void RenderScreen(DrawingContext dc)
    {
        Header(dc, "PHOTOMATIC");
        HeaderButton(dc, LeftEdge, LeftW, "LIBRARY", false, OpenLibrary);
        HeaderButton(dc, RightX, RightEdge, _editing ? "DONE" : "EDIT", _editing, () => { _editing = !_editing; _rollList = false; UpdateStage(); });
        if (_message is not null && _messageAge.Elapsed.TotalSeconds < 4)
            Draw.Label(dc, _message, RightX - 20, 42, 24, Theme.Mid, Align.Right);
        else
        {
            var m = _stage.Media(_stage.Slot);
            string kind = _stage.CameraOn ? "LIVE CAMERA" : m is null ? "EMPTY" : m.Failed ? "UNREADABLE" : m.Kind.ToString().ToUpperInvariant();
            Draw.Label(dc, $"{_stage.Slot + 1:00}", RightX - 20, 42, 40, Theme.Ink, Align.Right, Theme.Regular);
            Draw.Label(dc, kind, RightX - 74, 44, 24, Theme.Mid, Align.Right);
        }

        DrawKnobs(dc);
        DrawCamera(dc);
        DrawEffects(dc);
        NameBar(dc, _config.Name.ToUpperInvariant(), $"{_config.Slots.Count(s => s is not null)} / 24", () => { _rollList = !_rollList; _editing = false; UpdateStage(!_rollList); });

        if (_rollList) DrawRollList(dc);
        else if (_editing) DrawEditor(dc);
        else
        {
            DrawArrows(dc);
            DrawStrip(dc);
        }
    }

    bool ShiftPage => _shiftPage || Session.Ui.Shift || Session.Ui.Page == 1;

    void DrawKnobs(DrawingContext dc)
    {
        (string, SoundParam, Func<byte, string>?)[] page = ShiftPage
            ? [("XFADE", SoundParam.Attack, null), ("ZOOM", SoundParam.Decay, null),
               ("DOUBLE", SoundParam.Sustain, v => PhotomaticStage.DoubleNames[Math.Min(5, v * 6 / 256)]), ("WOOZY", SoundParam.Release, null)]
            : [("HUE", SoundParam.P1, null), ("SAT", SoundParam.P2, null), ("BRIGHT", SoundParam.Filter, null), ("CONTR", SoundParam.Resonance, null)];
        for (int i = 0; i < 4; i++)
            KnobCell(dc, new Rect(0, HeaderY + i * KnobH, LeftW, KnobH), page[i].Item1, page[i].Item2, Theme.Dials[i], page[i].Item3);
        var shift = new Rect(12, HeaderY + 4 * KnobH + 10, LeftW - 24, 44);
        Button(dc, shift, "SHIFT", ShiftPage, size: 24);
        AddHit(shift, up: () => { _shiftPage = !_shiftPage; InvalidateVisual(); });
    }

    void DrawCamera(DrawingContext dc)
    {
        double y = 622;
        Draw.HLine(dc, 0, LeftW, y);
        Draw.Label(dc, "CAMERA", 18, y + 24, 26, Theme.Mid);
        string[] labels = ["CAM", "FLIP", "SNAP"];
        Action[] actions = [ToggleCamera, FlipCamera, Snap];
        for (int i = 0; i < 3; i++)
        {
            var r = new Rect(12 + i * 66, y + 50, 60, 60);
            Button(dc, r, labels[i], i == 0 && _camera is not null, i == 0 || _camera is not null, 20);
            AddHit(r, up: actions[i]);
        }
    }

    void DrawEffects(DrawingContext dc)
    {
        double rowH = (BarY - HeaderY) / PhotomaticStage.EffectNames.Length;
        for (int i = 0; i < PhotomaticStage.EffectNames.Length; i++)
        {
            var r = new Rect(RightX, HeaderY + i * rowH, DesignWidth - RightX, rowH);
            bool lit = _stage.IsLit(i);
            if (lit) dc.DrawRectangle(Theme.Ink, null, new Rect(r.X + 1, r.Y + 1, RightEdge - r.X, r.Height - 1));
            Draw.Label(dc, PhotomaticStage.EffectNames[i], r.X + 18, r.Y + rowH / 2, 25, lit ? Theme.White : Theme.Ink, Align.Left, Theme.Regular);
            Draw.Label(dc, Display.NoteName(EffectNoteBase + WhitePositions[i]), r.Right - 14, r.Y + rowH / 2, 20, Theme.Mid, Align.Right);
            if (i > 0) Draw.HLine(dc, RightX, RightEdge, r.Y);
            int effect = i;
            AddHit(r, down: () => _stage.ApplyEffect(effect, true), up: () => _stage.ApplyEffect(effect, false));
        }
        if (_stage.IsLit(0) || _stage.IsLit(1) || _stage.IsLit(2) || _stage.IsLit(3) || _stage.IsLit(7))
            Dispatcher.BeginInvoke(InvalidateVisual, System.Windows.Threading.DispatcherPriority.Background);
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
            AddHit(new Rect(x0, PlayRect.Y, PlayRect.X - LeftW, PlayRect.Height), up: () => _stage.Trigger(_stage.Step(dir)));
        }
        AddHit(PlayRect, up: () => _stage.Trigger(_stage.Slot));
        if (!_stage.HasMedia(_stage.Slot) && !_stage.CameraOn)
        {
            double cx = PlayRect.X + PlayRect.Width / 2;
            Draw.Label(dc, $"SLOT {_stage.Slot + 1} IS EMPTY", cx, cy - 22, 36, Theme.Mid, Align.Center, Theme.Regular);
            Draw.Label(dc, "TAP LIBRARY OR DROP A PHOTO / VIDEO HERE", cx, cy + 26, 24, Theme.Mid, Align.Center);
        }
    }

    double CellW => (RightX - LeftW - 20) / PhotoLibrary.SlotCount;

    int SlotAt(double x)
    {
        int i = (int)Math.Floor((x - LeftW - 10) / CellW);
        return i >= 0 && i < PhotoLibrary.SlotCount ? i : -1;
    }

    void DrawStrip(DrawingContext dc)
    {
        double w = CellW;
        for (int i = 0; i < PhotoLibrary.SlotCount; i++)
        {
            var cell = new Rect(LeftW + 10 + i * w + 2, StripY, w - 4, StripH);
            var m = _stage.Media(i);
            if (m?.Thumb is { } thumb)
            {
                dc.PushClip(new RectangleGeometry(cell));
                double tw = cell.Height * thumb.PixelWidth / Math.Max(1, thumb.PixelHeight);
                double th = cell.Height;
                if (tw < cell.Width) { th = cell.Width * thumb.PixelHeight / Math.Max(1, thumb.PixelWidth); tw = cell.Width; }
                dc.DrawImage(thumb, new Rect(cell.X + (cell.Width - tw) / 2, cell.Y + (cell.Height - th) / 2, tw, th));
                dc.Pop();
            }
            else if (m is not null)
            {
                dc.DrawRectangle(Theme.Ink, null, cell);
                if (m.Kind == MediaKind.Video)
                {
                    var tri = new StreamGeometry();
                    using (var c = tri.Open())
                    {
                        c.BeginFigure(new Point(cell.X + cell.Width / 2 - 7, cell.Y + cell.Height / 2 - 10), true, true);
                        c.LineTo(new Point(cell.X + cell.Width / 2 + 9, cell.Y + cell.Height / 2), true, false);
                        c.LineTo(new Point(cell.X + cell.Width / 2 - 7, cell.Y + cell.Height / 2 + 10), true, false);
                    }
                    tri.Freeze();
                    dc.DrawGeometry(m.Failed ? Theme.Red : Theme.White, null, tri);
                }
            }
            else dc.DrawRectangle(Theme.Hatch, null, cell);
            bool current = i == _stage.Slot;
            Draw.Label(dc, (i + 1).ToString(), cell.X + 4, cell.Y + 11, 18, m is null ? Theme.Mid : Theme.White, Align.Left, Theme.Regular);
            if (current) Draw.Frame(dc, new Rect(cell.X - 2, cell.Y - 2, cell.Width + 4, cell.Height + 4), Theme.Red);
            int slot = i;
            AddHit(new Rect(LeftW + 10 + i * w, StripY - 4, w, StripH + 8), up: () => _stage.Trigger(slot));
        }
    }

    void DrawRollList(DrawingContext dc)
    {
        var area = new Rect(LeftW, HeaderY, RightX - LeftW, BarY - HeaderY);
        dc.DrawRectangle(Theme.Background, null, area);
        Draw.Label(dc, "ROLLS", area.X + 30, area.Y + 44, 34, Theme.Mid);
        Draw.Label(dc, "OR SHIFT + BLACK KEYS 1-10 ON TRACK 16", area.Right - 30, area.Y + 44, 24, Theme.Mid, Align.Right);
        double cw = (area.Width - 60) / 2, ch = 104;
        for (int i = 0; i < PhotoLibrary.RollCount; i++)
        {
            var r = new Rect(area.X + 30 + (i / 5) * cw, area.Y + 84 + (i % 5) * ch, cw - 10, ch - 10);
            var config = i == _roll ? _config : _library.Load(i);
            int count = config.Slots.Count(s => s is not null);
            dc.DrawRectangle(null, Theme.RulePen, r);
            Draw.Label(dc, (i + 1).ToString(), r.X + 24, r.Y + r.Height / 2, 44, Theme.Ink, Align.Left, Theme.Regular);
            Draw.Label(dc, config.Name.ToUpperInvariant(), r.X + 90, r.Y + 32, 32, Theme.Ink, Align.Left, Theme.Regular);
            Draw.Label(dc, count == 0 ? "EMPTY" : $"{count} / 24", r.X + 90, r.Y + 66, 24, Theme.Mid);
            if (i == _roll) Draw.Frame(dc, r, Theme.Red);
            int roll = i;
            AddHit(r, up: () => { _rollList = false; LoadRoll(roll); UpdateStage(); });
        }
    }

    void DrawEditor(DrawingContext dc)
    {
        var area = new Rect(LeftW, HeaderY, RightX - LeftW, BarY - HeaderY);
        dc.DrawRectangle(Theme.Background, null, area);
        double tabW = area.Width / Pages.Length;
        for (int i = 0; i < Pages.Length; i++)
        {
            var r = new Rect(area.X + i * tabW, area.Y, tabW, 68);
            bool on = i == _editPage;
            if (on) dc.DrawRectangle(Theme.Ink, null, r);
            Draw.Label(dc, Pages[i], r.X + tabW / 2, r.Y + 35, 28, on ? Theme.White : Theme.Ink, Align.Center, Theme.Regular);
            if (i > 0) Draw.VLine(dc, r.X, r.Y, r.Bottom);
            int page = i;
            AddHit(r, up: () => { _editPage = page; InvalidateVisual(); });
        }
        Draw.HLine(dc, area.X, area.Right, area.Y + 68);

        var rows = _editPage switch
        {
            0 => new (string, string, Action)[]
            {
                ("FIT", RollConfig.FitNames[_config.Fit], () => _config.Fit = (_config.Fit + 1) % RollConfig.FitNames.Length),
                ("BACKGROUND", RollConfig.BackgroundNames[_config.Background], () => _config.Background = (_config.Background + 1) % RollConfig.BackgroundNames.Length),
                ("BORDER", RollConfig.BorderNames[_config.Border], () => _config.Border = (_config.Border + 1) % RollConfig.BorderNames.Length),
            },
            1 =>
            [
                ("TRANSITION", RollConfig.TransitionNames[_config.Transition], () => _config.Transition = (_config.Transition + 1) % RollConfig.TransitionNames.Length),
                ("DRIFT", RollConfig.DriftNames[_config.Drift], () => _config.Drift = (_config.Drift + 1) % RollConfig.DriftNames.Length),
                ("WOOZY SPEED", RollConfig.WoozyNames[_config.WoozySpeed], () => _config.WoozySpeed = (_config.WoozySpeed + 1) % RollConfig.WoozyNames.Length),
            ],
            2 =>
            [
                ("PLAYBACK SPEED", RollConfig.SpeedNames[_config.VideoSpeed], () => _config.VideoSpeed = (_config.VideoSpeed + 1) % RollConfig.SpeedNames.Length),
                ("LOOP", _config.VideoLoop ? "ON" : "OFF", () => _config.VideoLoop = !_config.VideoLoop),
                ("ON NOTE", _config.VideoRestart ? "RESTART" : "CONTINUE", () => _config.VideoRestart = !_config.VideoRestart),
            ],
            _ =>
            [
                ("NAME", _config.Name.ToUpperInvariant(), Rename),
                ($"CLEAR SLOT {_stage.Slot + 1}", _stage.Media(_stage.Slot) is null ? "EMPTY" : "CLEAR", ClearSlot),
                ("ROLL FOLDER", "OPEN", () => Process.Start(new ProcessStartInfo(_library.RollDir(_roll)) { UseShellExecute = true })),
            ],
        };
        double y = area.Y + 68;
        for (int i = 0; i < rows.Length; i++)
        {
            var r = new Rect(area.X, y + i * 100, EditPreview.X - 30 - area.X, 100);
            Draw.Label(dc, rows[i].Item1, r.X + 24, r.Y + 50, 30, Theme.Mid);
            Draw.Label(dc, rows[i].Item2, r.Right - 20, r.Y + 50, 36, Theme.Ink, Align.Right, Theme.Regular);
            Draw.HLine(dc, area.X, r.Right, r.Bottom);
            var action = rows[i].Item3;
            AddHit(r, up: () => { action(); SaveConfig(); });
        }
        string note = _editPage switch
        {
            1 => "TRANSITION LENGTH FOLLOWS THE XFADE DIAL (SHIFT PAGE)",
            2 => "VIDEOS PLAY MUTED, AT MOST 10 SECONDS",
            3 => "ROLL SETTINGS ARE NOT SEQUENCED",
            _ => "SETTINGS APPLY TO THE WHOLE ROLL",
        };
        Draw.Label(dc, note, area.X + 24, area.Bottom - 34, 22, Theme.Mid);
        Draw.Label(dc, "PREVIEW", EditPreview.X, EditPreview.Bottom + 28, 24, Theme.Mid);
        Draw.Label(dc, $"SLOT {_stage.Slot + 1}", EditPreview.Right, EditPreview.Bottom + 28, 24, Theme.Ink, Align.Right, Theme.Regular);
    }

    void ClearSlot()
    {
        if (_stage.Media(_stage.Slot) is null) return;
        int slot = _stage.Slot;
        _library.Clear(_roll, _config, slot);
        LoadRoll(_roll);
        _stage.Trigger(slot);
    }

    void Rename()
    {
        var box = new TextBox { Text = _config.Name, FontFamily = Theme.Font, FontSize = 22, Margin = new Thickness(16), MaxLength = 24 };
        var ok = new System.Windows.Controls.Button { Content = "OK", IsDefault = true, Width = 90, Margin = new Thickness(16, 0, 16, 16), HorizontalAlignment = HorizontalAlignment.Right };
        var window = new Window
        {
            Title = "Roll name", SizeToContent = SizeToContent.WidthAndHeight, ResizeMode = ResizeMode.NoResize, WindowStyle = WindowStyle.ToolWindow,
            Owner = Window.GetWindow(this), WindowStartupLocation = WindowStartupLocation.CenterOwner, MinWidth = 360,
            Background = Theme.Background, Content = new StackPanel { Children = { box, ok } },
        };
        ok.Click += (_, _) => window.DialogResult = true;
        window.Loaded += (_, _) => { box.Focus(); box.SelectAll(); };
        if (window.ShowDialog() == true && !string.IsNullOrWhiteSpace(box.Text)) _config.Name = box.Text.Trim();
    }
}
