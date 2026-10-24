using System.Windows;
using System.Windows.Media;
using OPB.App.Rendering;
using OPB.Core.Model;

namespace OPB.App.Screens;

/// <summary>
/// The module track: its 16 values (4 pages of 4 dials) with their outgoing MIDI CC numbers, the
/// connected expansion module (oplab, line, rumble) decoded from $14, and the audio input routing
/// and microphone state from $04.
/// </summary>
[Screen(ScreenKeys.ModuleTrack)]
public sealed class ModuleScreen(AppModel app) : ControlTrackScreen(app)
{
    const double PanelLeft = 736, RowB = 342;
    static readonly string[] PageNames = ["SOUND", "ENVELOPE", "LFO", "MIX"];
    static readonly (SoundParam Param, string Label)[][] Pages =
    [
        [(SoundParam.P1, "P1"), (SoundParam.P2, "P2"), (SoundParam.Filter, "FILTER"), (SoundParam.Resonance, "RES")],
        [(SoundParam.Attack, "ATTACK"), (SoundParam.Decay, "DECAY"), (SoundParam.Sustain, "SUSTAIN"), (SoundParam.Release, "RELEASE")],
        [(SoundParam.LfoDepth, "DEPTH"), (SoundParam.LfoSpeed, "RATE"), (SoundParam.LfoValue, "DEST"), (SoundParam.LfoShape, "SHAPE")],
        [(SoundParam.Fx1Send, "FX 1"), (SoundParam.Fx2Send, "FX 2"), (SoundParam.Pan, "PAN"), (SoundParam.Level, "LEVEL")],
    ];

    protected override int Track => (int)TrackId.Module;

    protected override string? StepText(IReadOnlyList<Note> notes) => Display.NoteName(notes[0].Pitch);

    protected override void RenderBody(DrawingContext dc)
    {
        Draw.VLine(dc, 216, Row0, BodyBottom);
        Draw.VLine(dc, PanelLeft, Row0, BodyBottom);
        Draw.HLine(dc, PanelLeft, RightEnd, RowB);

        // The 16 values: the device's four dial pages, each value going out as a MIDI CC.
        var midi = Session.MidiConfig;
        for (int page = 0; page < 4; page++)
        {
            double y = Row0 + page * CellH;
            if (page > 0) Draw.HLine(dc, LeftEdge, PanelLeft, y);
            Draw.Label(dc, PageNames[page], 18, y + 42, 30, Session.Ui.Page == page ? Theme.Ink : Theme.Mid);
            Draw.Label(dc, $"PAGE {page + 1}", 18, y + 80, 22, Theme.Mid);
            for (int dial = 0; dial < 4; dial++)
            {
                double x = 216 + dial * CellW;
                if (dial > 0) Draw.VLine(dc, x, y, y + CellH);
                var (param, label) = Pages[page][dial];
                int index = page * 4 + dial;
                string cc = midi is null ? "" : $"CC {midi.ParameterCc(Track, index)}";
                Cell(dc, x, y, label, Snd(param, label, v => (v / 2).ToString()), page, dial, cc);
            }
        }

        DrawModule(dc, new Rect(PanelLeft, Row0, RightEnd - PanelLeft, RowB - Row0));
        DrawInput(dc, new Rect(PanelLeft, RowB, RightEnd - PanelLeft, BodyBottom - RowB));
    }

    void DrawModule(DrawingContext dc, Rect r)
    {
        Section(dc, "MODULE", r.X, r.Y);
        var m = Session.Module;
        string name = m.Connected && !string.IsNullOrWhiteSpace(m.Name) ? m.Name.Trim().ToLowerInvariant() : "";
        string kind = name switch { "oplab" => "OPLAB", "line" or "in-out" => "LINE", "rumble" => "RUMBLE", "" => "NONE", _ => name.ToUpperInvariant() };
        Draw.Label(dc, kind, r.X + 18, r.Y + 110, 56, m.Connected ? Theme.Ink : Theme.Mid, Align.Left, Theme.Regular);
        Draw.Label(dc, m.Connected ? "CONNECTED" : "NO EXPANSION MODULE", r.X + 18, r.Y + 160, 24, m.Connected ? Theme.Green : Theme.Mid);

        double cx = r.X + 330, y = r.Y + 70;
        uint status = m.Status;
        if (kind is "OPLAB" or "LINE")
        {
            string output = ((status >> 24) & 3) switch { 1 => "MIDI", 2 => "TRIGGER", 3 => "PO SYNC", _ => "—" };
            string input = ((status >> 27) & 1) == 0 ? "MIDI" : "TRIGGER";
            Row(dc, cx, y, "OUT SWITCH", output);
            Row(dc, cx, y + 44, "IN SWITCH", input);
            if (kind == "LINE")
            {
                Row(dc, cx, y + 88, "LINE OUT", ((status >> 20) & 1) == 0 ? "—" : ((status >> 21) & 1) != 0 ? "MONO" : "STEREO");
                Row(dc, cx, y + 132, "LINE IN", ((status >> 22) & 1) == 0 ? "—" : ((status >> 23) & 1) != 0 ? "MONO" : "STEREO");
            }
        }
        else if (kind == "RUMBLE")
        {
            int level = (int)Math.Min(4, status & 0x0F);
            Draw.Label(dc, "LEVEL", cx, y, 24, Theme.Mid);
            for (int i = 0; i < 4; i++)
                dc.DrawRectangle(i < level ? Theme.Ink : Theme.Rule, null, new Rect(cx + 120 + i * 40, y + 20 - (i + 1) * 14, 28, (i + 1) * 14));
        }
        else
        {
            Draw.Label(dc, "WITHOUT A MODULE THE TRACK", cx, y + 10, 24, Theme.Mid);
            Draw.Label(dc, "SENDS ITS 16 VALUES AS MIDI CC", cx, y + 44, 24, Theme.Mid);
            Draw.Label(dc, "AND ITS NOTES ON CHANNEL " + ((Session.MidiConfig?.Channel(Track) ?? Track) + 1), cx, y + 78, 24, Theme.Mid);
        }
    }

    void DrawInput(DrawingContext dc, Rect r)
    {
        Section(dc, "INPUT", r.X, r.Y);
        var level = Session.Level;
        (string Name, int Bit)[] sources = [("MIC", 1), ("HEADSET", 2), ("MODULE", 4), ("USB", 8)];
        for (int i = 0; i < sources.Length; i++)
        {
            var chip = new Rect(r.X + 18 + i * 162, r.Y + 70, 150, 56);
            bool on = (level.InputSelector & sources[i].Bit) != 0;
            dc.DrawRectangle(on ? Theme.Ink : null, new Pen(on ? Theme.Ink : Theme.Mid, 2), chip);
            Draw.Label(dc, sources[i].Name, chip.X + chip.Width / 2, chip.Y + chip.Height / 2, 24, on ? Theme.White : Theme.Mid, Align.Center, Theme.Regular);
        }

        double y = r.Y + 160;
        Draw.Label(dc, "MIC LEVEL", r.X + 18, y, 24, Theme.Mid);
        for (int i = 0; i < 16; i++)
            dc.DrawRectangle(i < level.MicLevel ? Theme.Ink : Theme.Rule, null, new Rect(r.X + 160 + i * 14, y - 12, 10, 24));
        Row(dc, r.X + 420, y, "MIC FX", Display.MicFx[Math.Clamp(level.MicFx, 0, 3)]);
        Row(dc, r.X + 18, y + 54, "MONITOR", level.Monitor ? "ON" : "OFF", 142);
        Row(dc, r.X + 420, y + 54, "MIC MODE", level.MicMode ? "ON" : "OFF");
    }

    static void Row(DrawingContext dc, double x, double y, string label, string value, double valueOffset = 150)
    {
        Draw.Label(dc, label, x, y, 24, Theme.Mid);
        Draw.Label(dc, value, x + valueOffset, y, 26, Theme.Ink, Align.Left, Theme.Regular);
    }
}
