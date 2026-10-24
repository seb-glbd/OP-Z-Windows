using System.Windows;
using System.Windows.Media;
using OPB.App.Rendering;
using OPB.Core;
using OPB.Core.Files;
using OPB.Core.Model;

namespace OPB.App.Screens;

/// <summary>
/// Configurator (official app 23.5): assigns plugs to the 10 slots of a track (the 10 black value keys).
/// Pulls settings/slotConfiguration.json and settings/plugs.json over the file service, edits a copy,
/// COMMIT uploads it through a sync job and reads it back (restoring the pulled original bytes if the
/// readback doesn't match), REVERT pulls again. Needs OP-Z firmware 1.2.45 or newer.
/// </summary>
[Screen(ScreenKeys.Configurator)]
public sealed class ConfiguratorScreen : ScreenView
{
    enum Phase { NotConnected, NeedsFirmware, Loading, Unavailable, Failed, Ready, Committing }
    enum Gesture { None, Pending, Scroll, Drag }

    const double TitleY = 40, Row0 = 82, TabsBottom = 152, StripLabelY = 186, StripTop = 220, CardW = 196, CardH = 150, CardGap = 16, StripX = 24;
    const double SlotsTop = 430, SlotY = 556, SlotR = 50, SlotPitch = 150, FooterTop = 712;

    readonly OpzFileClient _client;
    Phase _phase = Phase.NotConnected;
    string _message = "", _status = "";
    byte[]? _originalBytes;
    SlotConfiguration? _original, _edit;
    IReadOnlyList<PlugInfo> _catalog = [];
    bool _demo, _forceTry;
    int _track = -1, _lastActiveTrack = -1;
    double _scroll;
    int? _selectedSlot;
    uint? _selectedPlug;

    readonly List<(Rect Rect, PlugInfo Plug)> _cards = new();
    readonly List<(Rect Rect, int Track)> _tabs = new();
    Rect _commit, _revert, _panelButton;
    List<PlugInfo> _available = new();

    int? _pointer;
    Point _start;
    Gesture _gesture;
    PlugInfo? _dragPlug;
    Point _dragAt;
    int? _hoverSlot;

    public ConfiguratorScreen(AppModel app) : base(app.Session)
    {
        _client = new OpzFileClient(app.Session);
        _lastActiveTrack = Session.ActiveTrack;
    }

    public override string Title => "CONFIGURATOR";

    bool HasChanges => _edit is not null && _original is not null && !_edit.SameAssignments(_original);
    bool Editable => _phase == Phase.Ready && _edit is not null && _track >= 0;

    protected override void OnShown()
    {
        if (_edit is not null && (HasChanges || _phase == Phase.Committing)) return; // keep unsaved edits
        _ = LoadAsync();
    }

    protected override bool ShouldRedraw(Changes changes)
    {
        if ((changes & Changes.Play) != 0 && Session.ActiveTrack != _lastActiveTrack)
        {
            _lastActiveTrack = Session.ActiveTrack;
            if (_edit?.HasTrack(Session.ActiveTrack) == true && _gesture == Gesture.None) SelectTrack(Session.ActiveTrack);
        }
        if ((changes & (Changes.Connection | Changes.Device)) != 0 && _phase is Phase.NotConnected or Phase.NeedsFirmware
            && Session.IsConnected && !string.IsNullOrEmpty(Session.Device.Firmware) && !_demo && IsLoaded)
            _ = LoadAsync();
        return (changes & (Changes.Connection | Changes.Device | Changes.Play)) != 0;
    }

    // ------------------------------------------------------------------ file service

    async Task LoadAsync()
    {
        if (_phase is Phase.Loading or Phase.Committing) return;
        _status = "";
        if (OfflinePreview.DemoFile("demo-slotConfiguration.json") is { } demoSlots && OfflinePreview.DemoFile("demo-plugs.json") is { } demoPlugs)
        {
            _demo = true;
            Apply(demoSlots, demoPlugs);
            _status = "OFFLINE DEMO FILES";
            InvalidateVisual();
            return;
        }
        if (!Session.IsConnected) { SetPhase(Phase.NotConnected); return; }
        if (!_forceTry && Firmware.SupportsFileService(Session.Device.Firmware) == false) { SetPhase(Phase.NeedsFirmware); return; }

        SetPhase(Phase.Loading);
        _status = "READING PLUG SETTINGS FROM THE OP-Z";
        var progress = new Progress<FileProgress>(p => { _status = $"READING PLUG SETTINGS ({p.Parts} PARTS)"; InvalidateVisual(); });
        var result = await _client.DownloadAsync([FileIds.SlotConfiguration, FileIds.Plugs], progress);
        if (!result.Ok)
        {
            _message = result.Message;
            _status = "";
            SetPhase(result.Error == FileServiceError.NotAvailable ? Phase.Unavailable : Phase.Failed);
            return;
        }
        try
        {
            Apply(result.Value![0], result.Value[1]);
            PlugNames.Store(result.Value[1]);
            _status = "PLUG SETTINGS READ FROM THE OP-Z";
        }
        catch (Exception ex)
        {
            _message = $"The OP-Z sent settings OP-B can't read: {ex.Message}";
            SetPhase(Phase.Failed);
        }
        InvalidateVisual();
    }

    void Apply(byte[] slots, byte[] plugs)
    {
        var parsed = SlotConfiguration.Parse(slots);
        try { _catalog = PlugCatalog.Parse(plugs); } catch { _catalog = []; }
        _originalBytes = slots;
        _original = parsed;
        _edit = parsed.Clone();
        _selectedPlug = null;
        _selectedSlot = null;
        var tracks = SlotTracks();
        if (!tracks.Contains(_track)) SelectTrack(tracks.Contains(Session.ActiveTrack) ? Session.ActiveTrack : tracks.FirstOrDefault(-1));
        _phase = Phase.Ready;
    }

    void SetPhase(Phase phase)
    {
        _phase = phase;
        InvalidateVisual();
    }

    async Task CommitAsync()
    {
        if (!Editable || !HasChanges || _originalBytes is null) return;
        var expected = _edit!.Clone();
        var edited = expected.ToBytes();
        SetPhase(Phase.Committing);
        _status = "WRITING SLOT CONFIGURATION";
        if (_demo)
        {
            await Task.Delay(600);
            _original = expected.Clone();
            _originalBytes = edited;
            _status = "COMMITTED (DEMO, NOTHING SENT)";
            SetPhase(Phase.Ready);
            return;
        }
        var progress = new Progress<FileProgress>(p => { _status = $"WRITING SLOT CONFIGURATION {p.Parts}/{p.TotalParts}"; InvalidateVisual(); });
        var upload = await _client.UploadAsync([new HostedFile(FileIds.SlotConfigurationPath, edited)], progress: progress);
        string failure;
        if (upload.Ok)
        {
            _status = "CHECKING THE OP-Z'S COPY";
            InvalidateVisual();
            var back = await _client.DownloadAsync(FileIds.SlotConfiguration);
            SlotConfiguration? readback = null;
            if (back.Ok) try { readback = SlotConfiguration.Parse(back.Value![0]); } catch { /* treated as a mismatch */ }
            if (readback is not null && readback.SameAssignments(expected))
            {
                _originalBytes = back.Value![0];
                _original = readback;
                _edit = readback.Clone();
                _status = "COMMITTED TO THE OP-Z";
                SetPhase(Phase.Ready);
                return;
            }
            failure = back.Ok ? "THE OP-Z KEPT A DIFFERENT SLOT MAP" : back.Message.ToUpperInvariant();
            _status = "RESTORING THE ORIGINAL SLOT MAP";
            InvalidateVisual();
            var restore = await _client.UploadAsync([new HostedFile(FileIds.SlotConfigurationPath, _originalBytes)]);
            _status = restore.Ok ? $"COMMIT FAILED ({failure}); ORIGINAL RESTORED" : $"COMMIT FAILED ({failure}); RESTORE FAILED: {restore.Message.ToUpperInvariant()}";
        }
        else
        {
            failure = upload.Message.ToUpperInvariant();
            _status = $"COMMIT FAILED: {failure}";
        }
        SetPhase(Phase.Ready);
    }

    void Revert()
    {
        if (_phase is Phase.Loading or Phase.Committing) return;
        _selectedPlug = null;
        _selectedSlot = null;
        if (_demo && _original is not null)
        {
            _edit = _original.Clone();
            _status = "REVERTED";
            InvalidateVisual();
            return;
        }
        _ = LoadAsync();
    }

    // ------------------------------------------------------------------ model helpers

    List<int> SlotTracks() => _edit is null ? [] : _edit.Tracks.Where(_edit.HasTrack).ToList();

    void SelectTrack(int track)
    {
        if (track == _track) return;
        _track = track;
        _scroll = 0;
        _selectedSlot = null;
        _selectedPlug = null;
        InvalidateVisual();
    }

    string PlugName(uint id) => _catalog.FirstOrDefault(p => p.Id == id)?.Name.ToUpperInvariant() ?? PlugNames.Get(id);

    /// <summary>Plugs offered for a track: what the catalog says fits, else what tracks of the same kind use plus unused plugs.</summary>
    List<PlugInfo> Available(int track)
    {
        if (_edit is null) return [];
        var list = _catalog.Where(p => p.Fits(track) == true).ToList();
        if (list.Count == 0)
        {
            var kind = Tracks.Kind(track);
            var all = _edit.All().ToList();
            var sameKind = all.Where(a => Tracks.Kind(a.Track) == kind).Select(a => a.Plug).ToHashSet();
            var used = all.Select(a => a.Plug).ToHashSet();
            list = _catalog.Where(p => p.Fits(track) is null && (sameKind.Contains(p.Id) || !used.Contains(p.Id))).ToList();
        }
        foreach (var id in _edit.Slots(track).Values.Concat(_original?.Slots(track).Values ?? []))
            if (list.All(p => p.Id != id)) list.Add(new PlugInfo(id, PlugName(id), "", [track]));
        return list;
    }

    void Assign(int slot, uint plug)
    {
        if (!Editable) return;
        _edit!.Assign(_track, slot, plug);
        _selectedPlug = null;
        _selectedSlot = null;
        _status = "";
        InvalidateVisual();
    }

    double MaxScroll => Math.Max(0, StripX * 2 + _available.Count * (CardW + CardGap) - CardGap - DesignWidth);

    static Point SlotCenter(int slot) => new(DesignWidth / 2 + (slot - 5.5) * SlotPitch, SlotY);

    static int? SlotAt(Point p)
    {
        for (int s = 1; s <= SlotConfiguration.SlotCount; s++)
            if ((p - SlotCenter(s)).Length <= SlotR + 26) return s;
        return null;
    }

    static string Key(int slot) => slot == 10 ? "0" : slot.ToString();

    // ------------------------------------------------------------------ render

    protected override void Render(DrawingContext dc)
    {
        _cards.Clear();
        _tabs.Clear();
        _commit = _revert = _panelButton = Rect.Empty;
        Draw.Label(dc, Title, DesignWidth / 2, TitleY, 56, Theme.Ink, Align.Center, Theme.Regular);
        Draw.HLine(dc, LeftEdge, RightEdge, Row0);

        if (_edit is null || _phase is Phase.NotConnected or Phase.NeedsFirmware or Phase.Unavailable or Phase.Failed)
        {
            DrawPanel(dc);
            return;
        }
        if (_track < 0)
        {
            DrawMessage(dc, "NO PLUG SLOTS", "THE OP-Z'S SLOT CONFIGURATION LISTS NO TRACKS.", null, null);
            return;
        }
        _available = Available(_track);
        _scroll = Math.Clamp(_scroll, 0, MaxScroll);
        DrawTabs(dc);
        DrawStrip(dc);
        DrawSlots(dc);
        DrawFooter(dc);
        if (_gesture == Gesture.Drag && _dragPlug is not null)
            DrawCard(dc, new Rect(_dragAt.X - CardW / 2, _dragAt.Y - CardH / 2, CardW, CardH), _dragPlug, floating: true);
    }

    void DrawPanel(DrawingContext dc)
    {
        string firmware = string.IsNullOrEmpty(Session.Device.Firmware) ? "an unknown firmware" : Session.Device.Firmware;
        switch (_phase)
        {
            case Phase.NotConnected:
                DrawMessage(dc, "OP-Z NOT CONNECTED", "CONNECT THE OP-Z TO ASSIGN PLUGS TO ITS SLOTS. THE CONFIGURATOR READS AND WRITES THE PLUG SETTINGS STORED ON THE DEVICE.", null, null);
                break;
            case Phase.NeedsFirmware:
                DrawMessage(dc, "FIRMWARE UPDATE NEEDED",
                    $"THE CONFIGURATOR READS AND WRITES THE OP-Z'S PLUG SLOTS OVER ITS FILE SERVICE, WHICH NEEDS FIRMWARE {Firmware.FileService} OR NEWER. THIS OP-Z RUNS {firmware.ToUpperInvariant()}.",
                    "TRY ANYWAY", Theme.Yellow, footer: "UPDATE AT " + Firmware.DownloadUrl.ToUpperInvariant());
                break;
            case Phase.Unavailable:
                DrawMessage(dc, "FILE SERVICE NOT AVAILABLE",
                    $"THE OP-Z DID NOT ANSWER THE FILE SERVICE, SO ITS PLUG SLOTS CAN'T BE READ. THIS NEEDS FIRMWARE {Firmware.FileService} OR NEWER (THIS OP-Z RUNS {firmware.ToUpperInvariant()}).",
                    "TRY AGAIN", Theme.Yellow, footer: "UPDATE AT " + Firmware.DownloadUrl.ToUpperInvariant());
                break;
            case Phase.Failed:
                DrawMessage(dc, "COULD NOT READ THE PLUG SETTINGS", _message.ToUpperInvariant(), "TRY AGAIN", Theme.Red);
                break;
            default:
                DrawMessage(dc, "READING PLUG SETTINGS", _status.Length > 0 ? _status : "CONNECTING TO THE OP-Z FILE SERVICE", null, Theme.Ink);
                break;
        }
    }

    void DrawMessage(DrawingContext dc, string title, string body, string? button, Brush? frame, string? footer = null)
    {
        dc.DrawRectangle(Theme.Hatch, null, new Rect(LeftEdge, Row0 + 1, RightEdge - LeftEdge, BottomEdge - Row0 - 1));
        double w = 900, textW = w - 96;
        var lines = Widgets.Wrap(body, textW, 28);
        double h = 130 + lines.Count * 38 + (footer is null ? 0 : 56) + (button is null ? 0 : 110);
        var panel = new Rect((DesignWidth - w) / 2, Row0 + (DesignHeight - Row0 - h) / 2, w, h);
        dc.DrawRectangle(Theme.Background, null, panel);
        if (frame is not null) Draw.Frame(dc, panel, frame, 4);
        double y = panel.Y + 64;
        Draw.Label(dc, title, panel.X + 48, y, 44, Theme.Ink, Align.Left, Theme.Regular);
        y += 66;
        foreach (var line in lines) { Draw.Label(dc, line, panel.X + 48, y, 28, Theme.Mid); y += 38; }
        if (footer is not null) { y += 14; Draw.Label(dc, footer, panel.X + 48, y, 30, Theme.Ink, Align.Left, Theme.Regular); y += 42; }
        if (button is not null)
        {
            _panelButton = new Rect(panel.X + 48, panel.Bottom - 112, 280, 72);
            Widgets.Button(dc, _panelButton, button);
        }
    }

    void DrawTabs(DrawingContext dc)
    {
        var tracks = SlotTracks();
        double w = DesignWidth / Math.Max(1, tracks.Count);
        for (int i = 0; i < tracks.Count; i++)
        {
            int t = tracks[i];
            var r = new Rect(i * w, Row0, w, TabsBottom - Row0);
            bool selected = t == _track;
            if (i > 0) Draw.VLine(dc, r.X, Row0, TabsBottom);
            Draw.Label(dc, Tracks.Names[t], r.X + w / 2, Row0 + 35, 28, selected ? Theme.Ink : Theme.Mid, Align.Center, selected ? Theme.Regular : Theme.Light);
            if (selected) dc.DrawRectangle(Theme.Yellow, null, new Rect(r.X + 18, TabsBottom - 8, w - 36, 6));
            if (t == Session.ActiveTrack && Session.IsConnected) dc.DrawEllipse(Theme.Mid, null, new Point(r.X + w / 2, Row0 + 58), 4, 4);
            _tabs.Add((r, t));
        }
        Draw.HLine(dc, LeftEdge, RightEdge, TabsBottom);
    }

    void DrawStrip(DrawingContext dc)
    {
        var size = Draw.Label(dc, "PLUGS", StripX, StripLabelY, 28, Theme.Mid);
        Draw.Label(dc, _available.Count.ToString(), StripX + size.Width + 12, StripLabelY, 28, Theme.Ink, Align.Left, Theme.Regular);
        string hint = _selectedSlot is int s ? $"TAP A PLUG FOR SLOT {Key(s)}" : "DRAG A PLUG ONTO A SLOT, OR TAP A PLUG THEN A SLOT";
        Draw.Label(dc, hint, DesignWidth - StripX, StripLabelY, 24, _selectedSlot is null ? Theme.Mid : Theme.Red, Align.Right);
        var used = _edit!.Slots(_track);
        for (int i = 0; i < _available.Count; i++)
        {
            var plug = _available[i];
            var r = new Rect(StripX + i * (CardW + CardGap) - _scroll, StripTop, CardW, CardH);
            if (r.Right < LeftEdge || r.Left > RightEdge) { _cards.Add((r, plug)); continue; }
            bool dragged = _gesture == Gesture.Drag && _dragPlug?.Id == plug.Id;
            if (dragged) dc.DrawRectangle(null, new Pen(Theme.Rule, 3) { DashStyle = new DashStyle([3, 3], 0) }, new Rect(r.X + 1.5, r.Y + 1.5, r.Width - 3, r.Height - 3));
            else DrawCard(dc, r, plug, floating: false, selected: _selectedPlug == plug.Id, slots: used.Where(kv => kv.Value == plug.Id).Select(kv => kv.Key).ToList());
            _cards.Add((r, plug));
        }
        // Scroll position.
        double trackW = DesignWidth - 2 * StripX, content = trackW + MaxScroll;
        if (MaxScroll > 0)
        {
            double y = StripTop + CardH + 26;
            dc.DrawRectangle(Theme.Rule, null, new Rect(StripX, y - 2, trackW, 4));
            double thumbW = Math.Max(60, trackW * trackW / content), thumbX = StripX + (trackW - thumbW) * _scroll / MaxScroll;
            dc.DrawRectangle(Theme.Mid, null, new Rect(thumbX, y - 2, thumbW, 4));
        }
    }

    void DrawCard(DrawingContext dc, Rect r, PlugInfo plug, bool floating, bool selected = false, IReadOnlyList<int>? slots = null)
    {
        bool dark = floating || selected;
        if (dark) dc.DrawRectangle(floating ? Theme.Ink : Theme.Ink, null, r);
        else Draw.Frame(dc, r, Theme.Mid, 3);
        var ink = dark ? Theme.White : Theme.Ink;
        var lines = Widgets.Wrap(plug.Name.ToUpperInvariant(), CardW - 32, 30, Theme.Regular);
        if (lines.Count > 2) lines = [lines[0], Widgets.Fit(string.Join(' ', lines.Skip(1)), CardW - 32, 30, Theme.Regular)];
        for (int i = 0; i < lines.Count; i++)
            Draw.Label(dc, lines[i], r.X + 16, r.Y + 34 + i * 34, 30, ink, Align.Left, Theme.Regular);
        Draw.Label(dc, $"#{plug.Id}", r.X + 16, r.Bottom - 26, 22, dark ? Theme.Rule : Theme.Mid);
        if (slots is { Count: > 0 })
        {
            double x = r.Right - 24;
            foreach (var s in slots.OrderDescending())
            {
                dc.DrawEllipse(dark ? Theme.White : Theme.Ink, null, new Point(x, r.Bottom - 26), 15, 15);
                Draw.Label(dc, Key(s), x, r.Bottom - 26, 22, dark ? Theme.Ink : Theme.White, Align.Center, Theme.Regular);
                x -= 36;
            }
        }
    }

    void DrawSlots(DrawingContext dc)
    {
        Draw.HLine(dc, LeftEdge, RightEdge, SlotsTop);
        Draw.Label(dc, "SLOTS", StripX, SlotsTop + 36, 28, Theme.Mid);
        Draw.Label(dc, $"{Tracks.Names[_track]}  ·  TRACK + VALUE KEY PICKS A SLOT ON THE OP-Z", DesignWidth - StripX, SlotsTop + 36, 24, Theme.Mid, Align.Right);
        var slots = _edit!.Slots(_track);
        var original = _original?.Slots(_track) ?? new Dictionary<int, uint>();
        var dashed = new Pen(Theme.Mid, 3) { DashStyle = new DashStyle([2.5, 2.5], 0) };
        for (int s = 1; s <= SlotConfiguration.SlotCount; s++)
        {
            var c = SlotCenter(s);
            bool assigned = slots.TryGetValue(s, out var plug);
            bool changed = original.TryGetValue(s, out var was) ? !assigned || was != plug : assigned;
            bool target = _hoverSlot == s || _selectedSlot == s;
            if (target) dc.DrawEllipse(null, new Pen(Theme.Red, 5), c, SlotR + 10, SlotR + 10);
            else if (changed) dc.DrawEllipse(null, new Pen(Theme.Yellow, 5), c, SlotR + 10, SlotR + 10);
            if (assigned)
            {
                dc.DrawEllipse(Theme.Ink, null, c, SlotR, SlotR);
                Draw.Label(dc, Key(s), c.X, c.Y, 44, Theme.White, Align.Center, Theme.Regular);
                Draw.Label(dc, Widgets.Fit(PlugName(plug), SlotPitch - 12, 26, Theme.Regular), c.X, SlotY + SlotR + 40, 26, Theme.Ink, Align.Center, Theme.Regular);
            }
            else
            {
                dc.DrawEllipse(null, dashed, c, SlotR - 1.5, SlotR - 1.5);
                Draw.Label(dc, Key(s), c.X, c.Y, 44, Theme.Mid, Align.Center);
                Draw.Label(dc, "EMPTY", c.X, SlotY + SlotR + 40, 24, Theme.Mid, Align.Center);
            }
            if (changed && original.TryGetValue(s, out var previous))
                Draw.Label(dc, Widgets.Fit("WAS " + PlugName(previous), SlotPitch - 12, 20), c.X, SlotY + SlotR + 72, 20, Theme.Mid, Align.Center);
        }
    }

    void DrawFooter(DrawingContext dc)
    {
        Draw.HLine(dc, LeftEdge, RightEdge, FooterTop);
        bool busy = _phase is Phase.Loading or Phase.Committing;
        int changes = 0;
        if (_edit is not null && _original is not null)
            foreach (var t in SlotTracks())
                for (int s = 1; s <= SlotConfiguration.SlotCount; s++)
                    if (_edit.Plug(t, s) != _original.Plug(t, s)) changes++;
        double cy = FooterTop + (DesignHeight - FooterTop) / 2;
        if (busy) dc.DrawEllipse(Theme.Yellow, null, new Point(StripX + 8, cy), 8, 8);
        string status = _status.Length > 0 ? _status : changes > 0 ? $"{changes} SLOT{(changes == 1 ? "" : "S")} CHANGED, NOT YET ON THE OP-Z" : "IN SYNC WITH THE OP-Z";
        if (_status.Length > 0 && changes > 0 && !busy && !status.StartsWith("COMMIT")) status += $"  ·  {changes} CHANGED";
        Draw.Label(dc, Widgets.Fit(status, 1000 - StripX - (busy ? 26 : 0), 26), StripX + (busy ? 26 : 0), cy, 26, changes > 0 || busy ? Theme.Ink : Theme.Mid);
        _revert = new Rect(1060, FooterTop + 18, 240, 70);
        _commit = new Rect(1316, FooterTop + 18, 260, 70);
        Widgets.Button(dc, _revert, "REVERT", enabled: !busy);
        Widgets.Button(dc, _commit, _phase == Phase.Committing ? "COMMITTING" : "COMMIT", enabled: !busy && changes > 0, primary: true);
    }

    // ------------------------------------------------------------------ input

    protected override void OnPointerDown(int id, Point p)
    {
        if (_pointer is not null) return;
        _pointer = id;
        _start = p;
        _gesture = Gesture.Pending;
        _dragPlug = Editable ? _cards.FirstOrDefault(c => c.Rect.Contains(p)).Plug : null;
    }

    protected override void OnPointerMove(int id, Point p, Vector delta)
    {
        if (id != _pointer) return;
        var d = p - _start;
        if (_gesture == Gesture.Pending && Editable && p.Y >= TabsBottom && p.Y < SlotsTop)
        {
            if (Math.Abs(d.X) > 14 && Math.Abs(d.X) >= Math.Abs(d.Y)) _gesture = Gesture.Scroll;
            else if (Math.Abs(d.Y) > 14 && _dragPlug is not null) _gesture = Gesture.Drag;
        }
        else if (_gesture == Gesture.Pending && _dragPlug is not null && d.Length > 14) _gesture = Gesture.Drag;
        switch (_gesture)
        {
            case Gesture.Scroll:
                _scroll = Math.Clamp(_scroll - delta.X, 0, MaxScroll);
                InvalidateVisual();
                break;
            case Gesture.Drag:
                _dragAt = p;
                _hoverSlot = SlotAt(p);
                InvalidateVisual();
                break;
        }
    }

    protected override void OnPointerUp(int id, Point p)
    {
        if (id != _pointer) return;
        var gesture = _gesture;
        var plug = _dragPlug;
        _pointer = null;
        _gesture = Gesture.None;
        _dragPlug = null;
        _hoverSlot = null;
        if (gesture == Gesture.Drag)
        {
            if (plug is not null && SlotAt(p) is int slot) Assign(slot, plug.Id);
            InvalidateVisual();
            return;
        }
        if (gesture == Gesture.Pending) Tap(_start);
    }

    void Tap(Point p)
    {
        if (_panelButton.Contains(p))
        {
            _forceTry = true;
            _ = LoadAsync();
            return;
        }
        if (_revert.Contains(p)) { Revert(); return; }
        if (_commit.Contains(p)) { _ = CommitAsync(); return; }
        foreach (var (rect, track) in _tabs)
            if (rect.Contains(p)) { SelectTrack(track); return; }
        if (!Editable) return;
        if (SlotAt(p) is int slot)
        {
            if (_selectedPlug is uint chosen) Assign(slot, chosen);
            else _selectedSlot = _selectedSlot == slot ? null : slot;
            InvalidateVisual();
            return;
        }
        foreach (var (rect, plug) in _cards)
        {
            if (!rect.Contains(p)) continue;
            if (_selectedSlot is int target) Assign(target, plug.Id);
            else _selectedPlug = _selectedPlug == plug.Id ? null : plug.Id;
            InvalidateVisual();
            return;
        }
        _selectedPlug = null;
        _selectedSlot = null;
        InvalidateVisual();
    }

    protected override void OnWheel(Point p, double notches)
    {
        if (!Editable || p.Y < TabsBottom || p.Y > SlotsTop) return;
        _scroll = Math.Clamp(_scroll - notches * (CardW + CardGap), 0, MaxScroll);
        InvalidateVisual();
    }
}
