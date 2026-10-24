using System.Windows;
using System.Windows.Media;
using OPB.App.Rendering;
using OPB.Core;
using OPB.Core.Model;

namespace OPB.App.Screens;

/// <summary>
/// The perform track: the twelve punch-in effects on two octaves (F to E twice), like the OP-Z
/// keyboard: the low octave acts on the drum tracks, the high octave on the synth tracks.
/// Touching a key plays it live (several fingers at once, slide to move between effects); keys
/// also light while a perform note plays or while shift and a key are held on the OP-Z.
/// </summary>
[Screen(ScreenKeys.Perform)]
public sealed class PerformScreen : ScreenView
{
    const double TitleY = 40, Row0 = 82, LabelY = 116, KeyTop = 148, KeyBottom = 720, BlackBottom = 460;
    const double WhiteW = 106, BlackW = 66;
    const double X0 = (ScreenView.DesignWidth - 14 * WhiteW) / 2;

    /// <summary>The OP-Z keyboard's low octave starts at F3 (MIDI 53); the high octave follows at F4.</summary>
    const int LowOctaveF = 53;

    /// <summary>Effects from F upward (index = semitones above F).</summary>
    static readonly string[] Effects =
    [
        "DUCK", "FILTER\nSWEEP", "LOOP\n1", "STEREO", "LOOP\n2", "PITCH",
        "FOLLOW\nECHO", "RAMP UP\nFILL 1", "SHORT", "RAMP\nDOWN\nFILL 2", "LONG", "RANDOM",
    ];
    static readonly string[] Octaves = ["DRUMS", "SYNTHS"];
    static readonly int[] WhiteKeys = [0, 2, 4, 6, 7, 9, 11];
    static readonly (int Key, int After)[] BlackKeys = [(1, 0), (3, 1), (5, 2), (8, 4), (10, 5)];

    readonly AppModel _app;
    readonly HashSet<int> _heldNotes = new();
    /// <summary>Pointer id → the MIDI note it is currently holding.</summary>
    readonly Dictionary<int, int> _touches = new();

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

    /// <summary>Key index on the two-octave keyboard (0..23) for a MIDI note; notes above fold onto the high octave.</summary>
    static int KeyIndex(int midiNote)
    {
        int effect = ((midiNote % 12) - 5 + 12) % 12;
        return midiNote >= LowOctaveF + 12 ? 12 + effect : effect;
    }

    /// <summary>Keys lit now: touched keys, held perform notes, or shift + a held key on the OP-Z.</summary>
    HashSet<int> Active()
    {
        var active = _heldNotes.Concat(_touches.Values).Select(KeyIndex).ToHashSet();
        if (Session.Ui.Shift && Session.Ui.HeldKey >= 0) active.Add(Session.Ui.HeldKey % 24);
        return active;
    }

    static Rect WhiteRect(int octave, int i) => new(X0 + (octave * 7 + i) * WhiteW, KeyTop, WhiteW, KeyBottom - KeyTop);
    static Rect BlackRect(int octave, int after) => new(X0 + (octave * 7 + after + 1) * WhiteW - BlackW / 2, KeyTop, BlackW, BlackBottom - KeyTop);

    protected override void Render(DrawingContext dc)
    {
        Draw.Label(dc, Title, DesignWidth / 2, TitleY, 56, Theme.Ink, Align.Center, Theme.Regular);
        Draw.HLine(dc, LeftEdge, RightEdge, Row0);
        var active = Active();

        for (int octave = 0; octave < 2; octave++)
        {
            double left = X0 + octave * 7 * WhiteW;
            bool any = active.Any(k => k / 12 == octave);
            Draw.Label(dc, Octaves[octave], left + 7 * WhiteW / 2, LabelY, 30, any ? Theme.Ink : Theme.Mid, Align.Center, any ? Theme.Regular : Theme.Light);
            for (int i = 0; i < WhiteKeys.Length; i++)
            {
                int key = octave * 12 + WhiteKeys[i];
                bool on = active.Contains(key);
                var r = WhiteRect(octave, i);
                dc.DrawRectangle(on ? Theme.Yellow : Theme.Background, new Pen(Theme.Rule, 2), r);
                DrawEffect(dc, WhiteKeys[i], r.X + WhiteW / 2, KeyBottom - 80, on ? Theme.Ink : Theme.Mid);
            }
        }
        // The octave boundary, a little heavier, like the split on the device.
        Draw.VLine(dc, X0 + 7 * WhiteW, KeyTop, KeyBottom, new Pen(Theme.Ink, 3));
        for (int octave = 0; octave < 2; octave++)
            foreach (var (key, after) in BlackKeys)
            {
                bool on = active.Contains(octave * 12 + key);
                var r = BlackRect(octave, after);
                dc.DrawRectangle(on ? Theme.Yellow : Theme.Ink, null, r);
                DrawEffect(dc, key, r.X + BlackW / 2, BlackBottom - 60, on ? Theme.Ink : Theme.Mid, 19);
            }

        Draw.Label(dc, "TOUCH A KEY OR HOLD SHIFT + KEY", DesignWidth / 2, KeyBottom + 48, 30, Theme.Mid, Align.Center);
    }

    static void DrawEffect(DrawingContext dc, int effect, double cx, double y, Brush brush, double size = 22)
    {
        var lines = Effects[effect].Split('\n');
        double top = y - (lines.Length - 1) * size * 0.6;
        for (int i = 0; i < lines.Length; i++)
            Draw.Label(dc, lines[i], cx, top + i * size * 1.2, size, brush, Align.Center);
    }

    /// <summary>The key (0..23) under a point; black keys sit on top of white keys.</summary>
    static int? KeyAt(Point p)
    {
        for (int octave = 0; octave < 2; octave++)
            foreach (var (key, after) in BlackKeys)
                if (BlackRect(octave, after).Contains(p)) return octave * 12 + key;
        for (int octave = 0; octave < 2; octave++)
            for (int i = 0; i < WhiteKeys.Length; i++)
                if (WhiteRect(octave, i).Contains(p)) return octave * 12 + WhiteKeys[i];
        return null;
    }

    static int NoteFor(int key) => LowOctaveF + key;

    void Press(int id, int key)
    {
        int note = NoteFor(key);
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
        if (KeyAt(p) is int key) Press(id, key);
    }

    protected override void OnPointerMove(int id, Point p, Vector delta)
    {
        if (!_touches.TryGetValue(id, out int note)) return;
        int? key = KeyAt(p);
        if (key is int k && NoteFor(k) == note) return;
        Release(id);
        if (key is int next) Press(id, next);
        InvalidateVisual();
    }

    protected override void OnPointerUp(int id, Point p) => Release(id);

    protected override void OnHidden()
    {
        foreach (var id in _touches.Keys.ToList()) Release(id);
    }
}
