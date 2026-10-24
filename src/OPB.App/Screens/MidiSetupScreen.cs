using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using OPB.App.Rendering;
using OPB.Core;
using OPB.Core.Model;

namespace OPB.App.Screens;

/// <summary>
/// MIDI setup (official app 23.9): per-track MIDI enable and channel, the outgoing CC of each of the
/// selected track's 16 parameters, and the eight global MIDI flags. Edits go to a copy of the OP-Z's
/// 292-byte MIDI configuration that is committed with $10 at most once per second while dirty.
/// </summary>
[Screen(ScreenKeys.MidiSetup)]
public sealed class MidiSetupScreen : ScreenView
{
    const double TitleY = 40, Row0 = 82, ListRight = 520, HeaderBottom = 132, RowH = (DesignHeight - HeaderBottom) / 16;
    const double GridLabel = 520, GridX = 736, RightEnd = 1426, CellW = (RightEnd - GridX) / 4, CellH = 130, GlobalTop = Row0 + 4 * CellH, GlobalH = (DesignHeight - GlobalTop) / 2;
    const double OnX = 316, MinusX = 384, ChannelX = 434, PlusX = 484;

    static readonly string[][] ParamLabels =
    [
        ["P1", "P2", "FILTER", "RES"],
        ["ATTACK", "DECAY", "SUSTAIN", "RELEASE"],
        ["LFO DEPTH", "LFO RATE", "LFO DEST", "LFO SHAPE"],
        ["FX1", "FX2", "PAN", "LEVEL"],
    ];
    static readonly string[] PageNames = ["SOUND", "ENVELOPE", "LFO", "MIX"];
    static readonly (MidiFlag Flag, string Line1, string Line2)[] Flags =
    [
        (MidiFlag.ChannelOneToActive, "CHANNEL 1", "TO ACTIVE"),
        (MidiFlag.IncomingMidi, "INCOMING", "MIDI"),
        (MidiFlag.OutgoingMidi, "OUTGOING", "MIDI"),
        (MidiFlag.ClockIn, "CLOCK IN", ""),
        (MidiFlag.ClockOut, "CLOCK OUT", ""),
        (MidiFlag.AltProgramChange, "ALT PROGRAM", "CHANGE"),
        (MidiFlag.MidiEcho, "MIDI ECHO", ""),
        (MidiFlag.ProgramChange, "PROGRAM", "CHANGE"),
    ];

    readonly DispatcherTimer _commitTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    readonly List<(Rect Rect, Action Tap, Action<int>? Wheel)> _hits = new();
    readonly Rect _touchPad = new(RightEnd, Row0, DesignWidth - RightEnd, DesignHeight - Row0);
    MidiConfig? _edit;
    bool _dirty, _picked;
    int _track;
    int _param;
    int? _touchPadPointer;
    double _touchPadAccumulator;

    public MidiSetupScreen(AppModel app) : base(app.Session)
    {
        _track = Session.ActiveTrack;
        _commitTimer.Tick += (_, _) => Commit();
    }

    public override string Title => "MIDI SETUP";

    protected override void OnShown()
    {
        _edit = Session.MidiConfig?.Clone();
        _dirty = false;
        _picked = false;
        _track = Session.ActiveTrack;
        Session.RequestMidiConfig();
        _commitTimer.Start();
    }

    protected override void OnHidden()
    {
        Commit();
        _commitTimer.Stop();
    }

    protected override bool ShouldRedraw(Changes changes)
    {
        if ((changes & Changes.MidiConfig) != 0 && !_dirty) _edit = Session.MidiConfig?.Clone();
        if ((changes & Changes.Play) != 0 && !_picked) _track = Session.ActiveTrack;
        return (changes & (Changes.MidiConfig | Changes.Connection | Changes.Ui | Changes.Play)) != 0;
    }

    void Edit(Action<MidiConfig> change)
    {
        if (_edit is null) return;
        change(_edit);
        _dirty = true;
        InvalidateVisual();
    }

    /// <summary>Sends the edited configuration (like the official app's one-second CommitMidiConfig timer).</summary>
    void Commit()
    {
        if (!_dirty || _edit is null) return;
        _dirty = false;
        Session.SetMidiConfig(_edit.Clone());
    }

    // ------------------------------------------------------------------ render

    protected override void Render(DrawingContext dc)
    {
        _hits.Clear();
        Draw.Label(dc, Title, RightEnd / 2, TitleY, 56, Theme.Ink, Align.Center, Theme.Regular);
        Draw.Label(dc, "TOUCH PAD", RightEnd + 14, TitleY, 30, Theme.Mid);
        Draw.HLine(dc, LeftEdge, RightEdge, Row0);
        Draw.VLine(dc, RightEnd, TopEdge, BottomEdge);
        Draw.VLine(dc, ListRight, Row0, BottomEdge);
        dc.DrawRectangle(Theme.Hatch, null, new Rect(RightEnd + 1, Row0 + 1, RightEdge - RightEnd - 1, BottomEdge - Row0 - 1));

        if (_edit is null)
        {
            dc.DrawRectangle(Theme.Hatch, null, new Rect(LeftEdge, Row0 + 1, RightEnd - LeftEdge - 1, BottomEdge - Row0 - 1));
            var panel = new Rect(RightEnd / 2 - 360, 330, 720, 150);
            dc.DrawRectangle(Theme.Background, null, panel);
            Draw.Label(dc, Session.IsConnected ? "WAITING FOR THE MIDI SETUP" : "OP-Z NOT CONNECTED", panel.X + panel.Width / 2, panel.Y + 52, 40, Theme.Ink, Align.Center, Theme.Regular);
            Draw.Label(dc, Session.IsConnected ? "REQUESTED FROM THE OP-Z" : "CONNECT THE OP-Z TO EDIT ITS MIDI SETTINGS", panel.X + panel.Width / 2, panel.Y + 104, 26, Theme.Mid, Align.Center);
            return;
        }

        DrawTracks(dc, _edit);
        DrawParameters(dc, _edit);
        DrawGlobals(dc, _edit);

        if (_touchPadPointer is not null)
        {
            Draw.Label(dc, ParamLabels[_param / 4][_param % 4], RightEnd + 87, Row0 + 60, 28, Theme.Ink, Align.Center);
            Draw.Label(dc, $"CC {_edit.ParameterCc(_track, _param)}", RightEnd + 87, Row0 + 110, 44, Theme.Ink, Align.Center, Theme.Regular);
        }
    }

    void DrawTracks(DrawingContext dc, MidiConfig m)
    {
        Draw.Label(dc, "TRACK", 20, Row0 + 25, 26, Theme.Mid);
        Draw.Label(dc, "ON", OnX, Row0 + 25, 26, Theme.Mid, Align.Center);
        Draw.Label(dc, "CHANNEL", ChannelX, Row0 + 25, 26, Theme.Mid, Align.Center);
        Draw.HLine(dc, LeftEdge, ListRight, HeaderBottom);
        for (int t = 0; t < 16; t++)
        {
            int track = t;
            double y = HeaderBottom + t * RowH, cy = y + RowH / 2;
            bool selected = t == _track, on = m.TrackEnabled(t);
            if (selected) dc.DrawRectangle(Theme.Rule, null, new Rect(LeftEdge, y + 1, ListRight - LeftEdge, RowH - 1));
            if (t == Session.ActiveTrack) dc.DrawRectangle(Theme.Yellow, null, new Rect(6, cy - 9, 5, 18));
            Draw.Label(dc, $"{t + 1}", 22, cy, 24, Theme.Mid);
            Draw.Label(dc, Tracks.Names[t], 64, cy, 28, on ? Theme.Ink : Theme.Mid, Align.Left, selected ? Theme.Regular : Theme.Light);
            Widgets.Toggle(dc, new Point(OnX, cy), on, size: 24);
            Widgets.Stepper(dc, new Point(MinusX, cy), false, m.Channel(t) > 0, 14);
            Draw.Label(dc, (m.Channel(t) + 1).ToString(), ChannelX, cy, 28, on ? Theme.Ink : Theme.Mid, Align.Center, Theme.Regular);
            Widgets.Stepper(dc, new Point(PlusX, cy), true, m.Channel(t) < 15, 14);
            if (t % 4 == 3 && t < 15) Draw.HLine(dc, LeftEdge, ListRight, y + RowH);
            _hits.Add((new Rect(OnX - 34, y, 68, RowH), () => Edit(c => c.SetTrackEnabled(track, !c.TrackEnabled(track))), null));
            _hits.Add((new Rect(MinusX - 26, y, 50, RowH), () => Edit(c => c.SetChannel(track, c.Channel(track) - 1)), null));
            _hits.Add((new Rect(PlusX - 24, y, ListRight - PlusX + 24, RowH), () => Edit(c => c.SetChannel(track, c.Channel(track) + 1)), null));
            _hits.Add((new Rect(ChannelX - 26, y, 52, RowH), () => SelectTrack(track), d => Edit(c => c.SetChannel(track, c.Channel(track) + d))));
            _hits.Add((new Rect(LeftEdge, y, OnX - 34 - LeftEdge, RowH), () => SelectTrack(track), null));
        }
    }

    void SelectTrack(int track)
    {
        _track = track;
        _picked = true;
        InvalidateVisual();
    }

    void DrawParameters(DrawingContext dc, MidiConfig m)
    {
        bool on = m.TrackEnabled(_track);
        for (int page = 0; page < 4; page++)
        {
            double y = Row0 + page * CellH;
            if (page == 0)
            {
                Draw.Label(dc, Tracks.Names[_track], GridLabel + 20, y + 40, 34, on ? Theme.Ink : Theme.Mid, Align.Left, Theme.Regular);
                Draw.Label(dc, on ? $"CHANNEL {m.Channel(_track) + 1}" : "MIDI OFF", GridLabel + 20, y + 80, 24, on ? Theme.Mid : Theme.Red);
                Draw.Label(dc, "OUTGOING CC", GridLabel + 20, y + 110, 22, Theme.Mid);
            }
            else
            {
                Draw.Label(dc, PageNames[page], GridLabel + 20, y + 42, 30, Theme.Mid);
            }
            dc.DrawRectangle(Theme.Pages[page], null, new Rect(GridX - 14, y + 26, 6, CellH - 52));
            for (int dial = 0; dial < 4; dial++)
            {
                int param = page * 4 + dial;
                double x = GridX + dial * CellW;
                int cc = m.ParameterCc(_track, param);
                Draw.Label(dc, ParamLabels[page][dial], x + 10, y + 20, 26, Theme.Mid);
                Draw.Knob(dc, new Point(x + CellW / 2, y + 64), cc / 127.0, on ? Theme.Dials[dial] : Theme.Mid, 20);
                Draw.Label(dc, "CC", x + CellW / 2 - 6, y + 107, 22, Theme.Mid, Align.Right);
                Draw.Label(dc, cc.ToString(), x + CellW / 2, y + 107, 32, Theme.Ink, Align.Left, Theme.Regular);
                _hits.Add((new Rect(x, y, CellW, CellH), () => { _param = param; InvalidateVisual(); },
                    d => { _param = param; Edit(c => c.SetParameterCc(_track, param, c.ParameterCc(_track, param) + d)); }));
                if (dial > 0) Draw.VLine(dc, x, y, y + CellH);
            }
            Draw.HLine(dc, GridLabel, RightEnd, y + CellH);
        }
        Draw.VLine(dc, GridX, Row0, GlobalTop);
        Draw.Frame(dc, new Rect(GridX + _param % 4 * CellW, Row0 + _param / 4 * CellH, CellW, CellH), Theme.Red);
    }

    void DrawGlobals(DrawingContext dc, MidiConfig m)
    {
        Draw.Label(dc, "GLOBAL", GridLabel + 20, GlobalTop + 42, 30, Theme.Mid);
        Draw.Label(dc, _dirty ? "SENDING" : "IN SYNC", GridLabel + 20, GlobalTop + 82, 22, _dirty ? Theme.Yellow : Theme.Mid);
        Draw.VLine(dc, GridX, GlobalTop, BottomEdge);
        for (int i = 0; i < Flags.Length; i++)
        {
            var (flag, line1, line2) = Flags[i];
            double x = GridX + i % 4 * CellW, y = GlobalTop + i / 4 * GlobalH, cy = y + GlobalH / 2;
            bool on = m.Flag(flag);
            Widgets.Toggle(dc, new Point(x + 24, cy), on, size: 24);
            var brush = on ? Theme.Ink : Theme.Mid;
            if (line2.Length == 0) Draw.Label(dc, line1, x + 48, cy, 22, brush, Align.Left, on ? Theme.Regular : Theme.Light);
            else
            {
                Draw.Label(dc, line1, x + 48, cy - 13, 22, brush, Align.Left, on ? Theme.Regular : Theme.Light);
                Draw.Label(dc, line2, x + 48, cy + 14, 22, brush, Align.Left, on ? Theme.Regular : Theme.Light);
            }
            if (i % 4 > 0) Draw.VLine(dc, x, y, i / 4 == 1 ? BottomEdge : y + GlobalH);
            _hits.Add((new Rect(x, y, CellW, GlobalH), () => Edit(c => c.SetFlag(flag, !c.Flag(flag))), null));
        }
        Draw.HLine(dc, GridLabel, RightEnd, GlobalTop + GlobalH);
    }

    // ------------------------------------------------------------------ input

    protected override void OnPointerDown(int id, Point p)
    {
        if (_touchPad.Contains(p))
        {
            _touchPadPointer = id;
            _touchPadAccumulator = 0;
            return;
        }
        foreach (var (rect, tap, _) in _hits)
            if (rect.Contains(p)) { tap(); return; }
    }

    protected override void OnPointerMove(int id, Point p, Vector delta)
    {
        if (id != _touchPadPointer || _edit is null) return;
        _touchPadAccumulator += (-delta.Y + delta.X * 0.5) * 0.2;
        int steps = (int)_touchPadAccumulator;
        if (steps == 0) return;
        _touchPadAccumulator -= steps;
        Edit(c => c.SetParameterCc(_track, _param, c.ParameterCc(_track, _param) + steps));
    }

    protected override void OnPointerUp(int id, Point p)
    {
        if (id == _touchPadPointer) { _touchPadPointer = null; InvalidateVisual(); }
    }

    protected override void OnWheel(Point p, double notches)
    {
        int d = Math.Sign(notches) * Math.Max(1, (int)Math.Round(Math.Abs(notches)));
        if (_touchPad.Contains(p)) { Edit(c => c.SetParameterCc(_track, _param, c.ParameterCc(_track, _param) + d)); return; }
        foreach (var (rect, _, wheel) in _hits)
            if (rect.Contains(p) && wheel is not null) { wheel(d); return; }
    }
}
