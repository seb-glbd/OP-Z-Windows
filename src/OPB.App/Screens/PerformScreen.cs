using System.Windows;
using System.Windows.Media;
using OPB.App.Rendering;
using OPB.Core;
using OPB.Core.Model;

namespace OPB.App.Screens;

/// <summary>
/// The perform track: the twelve punch-in effects laid out on one octave (F to E). Touching a key
/// plays it live on the perform track (several fingers at once, slide to move between effects);
/// keys also light while a perform note plays or while shift and a key are held on the OP-Z.
/// </summary>
[Screen(ScreenKeys.Perform)]
public sealed class PerformScreen : ScreenView
{
    const double TitleY = 40, Row0 = 82, KeyTop = 128, KeyBottom = 700, BlackBottom = 440, WhiteW = 180, BlackW = 112;
    const double X0 = (ScreenView.DesignWidth - 7 * WhiteW) / 2;
    const double ScopeW = 170, ScopeH = 64, ScopeY = KeyBottom + 24;

    /// <summary>
    /// The OP-Z keyboard's low octave starts at F3 (MIDI 53): punch-ins there affect the current track,
    /// the octave above affects the current group (drums or synths).
    /// </summary>
    const int LowOctaveF = 53;

    /// <summary>Effects from F upward (index = semitones above F).</summary>
    static readonly string[] Effects =
    [
        "DUCK", "FILTER\nSWEEP", "LOOP 1", "STEREO", "LOOP 2", "PITCH",
        "FOLLOW\nECHO", "RAMP UP\nFILL 1", "SHORT", "RAMP DOWN\nFILL 2", "LONG", "RANDOM",
    ];
    static readonly int[] WhiteKeys = [0, 2, 4, 6, 7, 9, 11];
    static readonly (int Key, int After)[] BlackKeys = [(1, 0), (3, 1), (5, 2), (8, 4), (10, 5)];

    readonly AppModel _app;
    readonly HashSet<int> _heldNotes = new();
    /// <summary>Pointer id → the MIDI note it is currently holding.</summary>
    readonly Dictionary<int, int> _touches = new();
    bool _group;

    public PerformScreen(AppModel app) : base(app.Session)
    {
        _app = app;
        Session.NoteEvent += (track, note, velocity) =>
        {
            if (track != (int)TrackId.Perform) return;
            if (velocity > 0) _heldNotes.Add(note); else _heldNotes.Remove(note);
            InvalidateVisual();
        };
    }

    public override string Title => "PERFORM";

    static int EffectIndex(int midiNote) => ((midiNote % 12) - 5 + 12) % 12;

    /// <summary>Effects lit now: touched keys, held perform notes, or shift + a held key on the OP-Z.</summary>
    HashSet<int> Active(out bool group)
    {
        var active = _heldNotes.Concat(_touches.Values).Select(EffectIndex).ToHashSet();
        group = _touches.Count > 0 ? _group : false;
        if (Session.Ui.Shift && Session.Ui.HeldKey >= 0)
        {
            active.Add(Session.Ui.HeldKey % 12);
            group = Session.Ui.HeldKey >= 12;
        }
        return active;
    }

    static Rect WhiteRect(int i) => new(X0 + i * WhiteW, KeyTop, WhiteW, KeyBottom - KeyTop);
    static Rect BlackRect(int after) => new(X0 + (after + 1) * WhiteW - BlackW / 2, KeyTop, BlackW, BlackBottom - KeyTop);
    static Rect ScopeRect(bool group) => new(DesignWidth / 2 + (group ? 0 : -ScopeW), ScopeY, ScopeW, ScopeH);

    protected override void Render(DrawingContext dc)
    {
        Draw.Label(dc, Title, DesignWidth / 2, TitleY, 56, Theme.Ink, Align.Center, Theme.Regular);
        Draw.HLine(dc, LeftEdge, RightEdge, Row0);
        var active = Active(out bool group);

        for (int i = 0; i < WhiteKeys.Length; i++)
        {
            int key = WhiteKeys[i];
            bool on = active.Contains(key);
            var r = WhiteRect(i);
            dc.DrawRectangle(on ? Theme.Yellow : Theme.Background, new Pen(Theme.Rule, 2), r);
            DrawEffect(dc, key, r.X + WhiteW / 2, KeyBottom - 70, on ? Theme.Ink : Theme.Mid);
        }
        foreach (var (key, after) in BlackKeys)
        {
            bool on = active.Contains(key);
            var r = BlackRect(after);
            dc.DrawRectangle(on ? Theme.Yellow : Theme.Ink, null, r);
            DrawEffect(dc, key, r.X + BlackW / 2, BlackBottom - 60, on ? Theme.Ink : Theme.Mid);
        }

        // Scope switch: which octave touches play (low = current track, high = its group).
        foreach (bool g in new[] { false, true })
        {
            var r = ScopeRect(g);
            bool selected = _group == g;
            dc.DrawRectangle(selected ? Theme.Ink : null, new Pen(Theme.Ink, 2), r);
            Draw.Label(dc, g ? "GROUP" : "TRACK", r.X + r.Width / 2, r.Y + r.Height / 2, 30, selected ? Theme.White : Theme.Ink, Align.Center, Theme.Regular);
        }
        Draw.Label(dc, "TOUCH A KEY OR HOLD SHIFT + KEY", 20, ScopeY + ScopeH / 2, 30, Theme.Mid);
        Draw.Label(dc, active.Count == 0 ? "" : group ? "GROUP" : "TRACK", DesignWidth - 20, ScopeY + ScopeH / 2, 30, Theme.Ink, Align.Right, Theme.Regular);
    }

    static void DrawEffect(DrawingContext dc, int key, double cx, double y, Brush brush)
    {
        var lines = Effects[key].Split('\n');
        double top = y - (lines.Length - 1) * 15;
        for (int i = 0; i < lines.Length; i++)
            Draw.Label(dc, lines[i], cx, top + i * 30, 26, brush, Align.Center);
    }

    /// <summary>The effect under a point; black keys sit on top of white keys.</summary>
    static int? KeyAt(Point p)
    {
        foreach (var (key, after) in BlackKeys)
            if (BlackRect(after).Contains(p)) return key;
        for (int i = 0; i < WhiteKeys.Length; i++)
            if (WhiteRect(i).Contains(p)) return WhiteKeys[i];
        return null;
    }

    int NoteFor(int effect) => LowOctaveF + effect + (_group ? 12 : 0);

    void Press(int id, int effect)
    {
        int note = NoteFor(effect);
        _touches[id] = note;
        _app.SendTrackNote((int)TrackId.Perform, note, 100);
    }

    void Release(int id)
    {
        if (!_touches.Remove(id, out int note)) return;
        // Keep the effect on while another finger still holds the same note.
        if (!_touches.ContainsValue(note)) _app.SendTrackNote((int)TrackId.Perform, note, 0);
    }

    protected override void OnPointerDown(int id, Point p)
    {
        foreach (bool g in new[] { false, true })
            if (ScopeRect(g).Contains(p)) { ReleaseAll(); _group = g; return; }
        if (KeyAt(p) is int effect) Press(id, effect);
    }

    protected override void OnPointerMove(int id, Point p, Vector delta)
    {
        if (!_touches.TryGetValue(id, out int note)) return;
        int? effect = KeyAt(p);
        if (effect is int e && NoteFor(e) == note) return;
        Release(id);
        if (effect is int next) Press(id, next);
        InvalidateVisual();
    }

    protected override void OnPointerUp(int id, Point p) => Release(id);

    void ReleaseAll()
    {
        foreach (var id in _touches.Keys.ToList()) Release(id);
    }

    protected override void OnHidden() => ReleaseAll();
}
