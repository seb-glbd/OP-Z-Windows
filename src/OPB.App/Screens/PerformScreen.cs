using System.Windows;
using System.Windows.Media;
using OPB.App.Rendering;
using OPB.Core;
using OPB.Core.Model;

namespace OPB.App.Screens;

/// <summary>
/// The perform track: the twelve punch-in effects laid out on one octave (F to E), lit while
/// a perform note is held or while shift and a key are held.
/// </summary>
[Screen(ScreenKeys.Perform)]
public sealed class PerformScreen : ScreenView
{
    const double TitleY = 40, Row0 = 82, KeyTop = 128, KeyBottom = 700, BlackBottom = 440, WhiteW = 180, BlackW = 112;
    const double X0 = (ScreenView.DesignWidth - 7 * WhiteW) / 2;

    /// <summary>Effects from F upward (index = semitones above F).</summary>
    static readonly string[] Effects =
    [
        "DUCK", "FILTER\nSWEEP", "LOOP 1", "STEREO", "LOOP 2", "PITCH",
        "FOLLOW\nECHO", "RAMP UP\nFILL 1", "SHORT", "RAMP DOWN\nFILL 2", "LONG", "RANDOM",
    ];
    static readonly string[] KeyNames = ["F", "F#", "G", "G#", "A", "A#", "B", "C", "C#", "D", "D#", "E"];
    static readonly int[] WhiteKeys = [0, 2, 4, 6, 7, 9, 11];
    static readonly (int Key, int After)[] BlackKeys = [(1, 0), (3, 1), (5, 2), (8, 4), (10, 5)];

    readonly HashSet<int> _heldNotes = new();

    public PerformScreen(AppModel app) : base(app.Session)
    {
        Session.NoteEvent += (track, note, velocity) =>
        {
            if (track != (int)TrackId.Perform) return;
            if (velocity > 0) _heldNotes.Add(note); else _heldNotes.Remove(note);
            InvalidateVisual();
        };
    }

    public override string Title => "PERFORM";

    static int EffectIndex(int midiNote) => ((midiNote % 12) - 5 + 12) % 12;

    /// <summary>Effects lit now: held perform notes, or shift + a held key (low octave = track, high = group).</summary>
    HashSet<int> Active(out bool group)
    {
        var active = _heldNotes.Select(EffectIndex).ToHashSet();
        group = false;
        if (Session.Ui.Shift && Session.Ui.HeldKey >= 0)
        {
            active.Add(Session.Ui.HeldKey % 12);
            group = Session.Ui.HeldKey >= 12;
        }
        return active;
    }

    protected override void Render(DrawingContext dc)
    {
        Draw.Label(dc, Title, DesignWidth / 2, TitleY, 56, Theme.Ink, Align.Center, Theme.Regular);
        Draw.HLine(dc, LeftEdge, RightEdge, Row0);
        var active = Active(out bool group);

        for (int i = 0; i < WhiteKeys.Length; i++)
        {
            int key = WhiteKeys[i];
            bool on = active.Contains(key);
            var r = new Rect(X0 + i * WhiteW, KeyTop, WhiteW, KeyBottom - KeyTop);
            dc.DrawRectangle(on ? Theme.Yellow : Theme.Background, new Pen(Theme.Rule, 2), r);
            DrawEffect(dc, key, r.X + WhiteW / 2, KeyBottom - 110, on ? Theme.Ink : Theme.Mid, Theme.Ink);
        }
        foreach (var (key, after) in BlackKeys)
        {
            bool on = active.Contains(key);
            var r = new Rect(X0 + (after + 1) * WhiteW - BlackW / 2, KeyTop, BlackW, BlackBottom - KeyTop);
            dc.DrawRectangle(on ? Theme.Yellow : Theme.Ink, null, r);
            DrawEffect(dc, key, r.X + BlackW / 2, BlackBottom - 100, on ? Theme.Ink : Theme.Mid, on ? Theme.Ink : Theme.Background);
        }

        Draw.Label(dc, "HOLD SHIFT + KEY", 20, KeyBottom + 56, 30, Theme.Mid);
        Draw.Label(dc, active.Count == 0 ? "" : group ? "GROUP" : "TRACK", DesignWidth - 20, KeyBottom + 56, 30, Theme.Ink, Align.Right, Theme.Regular);
    }

    static void DrawEffect(DrawingContext dc, int key, double cx, double y, Brush nameBrush, Brush keyBrush)
    {
        var lines = Effects[key].Split('\n');
        double size = 26;
        for (int i = 0; i < lines.Length; i++)
            Draw.Label(dc, lines[i], cx, y + i * 30, size, nameBrush, Align.Center);
        Draw.Label(dc, KeyNames[key], cx, y + 70, 34, keyBrush, Align.Center, Theme.Regular);
    }
}
