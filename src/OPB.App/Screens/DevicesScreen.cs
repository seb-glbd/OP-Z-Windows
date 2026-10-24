using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using OPB.App.Rendering;
using OPB.Core;
using OPB.Core.Midi;
using OPB.Core.Model;

namespace OPB.App.Screens;

/// <summary>
/// Devices (official app 23.8): the MIDI ports on this computer, the connected OP-Z with its
/// serial, firmware, battery and expansion module, a reconnect button, and a warning when the
/// firmware is too old for the configurator and file transfer.
/// </summary>
[Screen(ScreenKeys.Devices)]
public sealed class DevicesScreen : ScreenView
{
    const double TitleY = 40, Row0 = 82, ListRight = 560, InfoX = 600, InfoW = 960, TableTop = 222, TableRow = 74;

    readonly DispatcherTimer _portTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    IReadOnlyList<string> _inputs = [];
    IReadOnlyList<string> _outputs = [];
    Rect _reconnect;
    DateTime _reconnectedAt;

    public DevicesScreen(AppModel app) : base(app.Session)
    {
        _portTimer.Tick += (_, _) => RefreshPorts();
    }

    public override string Title => "DEVICES";

    protected override void OnShown()
    {
        RefreshPorts();
        _portTimer.Start();
    }

    protected override void OnHidden() => _portTimer.Stop();

    void RefreshPorts()
    {
        IReadOnlyList<string> inputs, outputs;
        try { inputs = WinMidiConnection.InputNames(); outputs = WinMidiConnection.OutputNames(); }
        catch { inputs = []; outputs = []; }
        if (inputs.SequenceEqual(_inputs) && outputs.SequenceEqual(_outputs)) return;
        _inputs = inputs;
        _outputs = outputs;
        InvalidateVisual();
    }

    static bool IsOpz(string name) => name.Contains("OP-Z", StringComparison.OrdinalIgnoreCase);

    // ------------------------------------------------------------------ render

    protected override void Render(DrawingContext dc)
    {
        Draw.Label(dc, Title, DesignWidth / 2, TitleY, 56, Theme.Ink, Align.Center, Theme.Regular);
        Draw.HLine(dc, LeftEdge, RightEdge, Row0);
        Draw.VLine(dc, ListRight, Row0, BottomEdge);
        DrawPorts(dc);
        DrawDevice(dc);
    }

    void DrawPorts(DrawingContext dc)
    {
        Draw.Label(dc, "MIDI PORTS", 24, Row0 + 42, 30, Theme.Mid);
        Draw.HLine(dc, LeftEdge, ListRight, Row0 + 84);
        double y = Row0 + 84;
        if (_inputs.Count == 0)
        {
            Draw.Label(dc, "NO MIDI PORTS FOUND", 24, y + 40, 30, Theme.Mid);
            return;
        }
        foreach (var name in _inputs.Take(9))
        {
            bool opz = IsOpz(name);
            bool active = Session.PortName is not null && string.Equals(name, Session.PortName, StringComparison.OrdinalIgnoreCase) && Session.Connection is not null;
            var dot = active ? (Session.IsConnected ? Theme.Green : Theme.Yellow) : opz ? Theme.Ink : Theme.Rule;
            dc.DrawEllipse(dot, null, new Point(40, y + 37), 9, 9);
            Draw.Label(dc, Widgets.Fit(name.ToUpperInvariant(), ListRight - 90, 30, Theme.Regular), 66, y + 30, 30, opz ? Theme.Ink : Theme.Mid, Align.Left, opz ? Theme.Regular : Theme.Light);
            string detail = active ? (Session.IsConnected ? "IN USE BY OP-B" : "OPENING") : _outputs.Contains(name) ? "IN + OUT" : "IN";
            Draw.Label(dc, detail, 66, y + 58, 20, active ? Theme.Green : Theme.Mid);
            y += TableRow;
            Draw.HLine(dc, LeftEdge, ListRight, y);
        }
        if (_inputs.Count > 9) Draw.Label(dc, $"+ {_inputs.Count - 9} MORE", 66, y + 30, 24, Theme.Mid);
    }

    void DrawDevice(DrawingContext dc)
    {
        bool connected = Session.IsConnected;
        bool opening = !connected && Session.Connection is not null;
        var device = Session.Device;
        bool known = !string.IsNullOrEmpty(device.Serial);

        var title = Draw.Label(dc, "OP-Z", InfoX, Row0 + 66, 72, connected ? Theme.Ink : Theme.Mid, Align.Left, Theme.Regular);
        string state = connected ? "CONNECTED" : opening ? "WAITING FOR OP-Z" : "NOT CONNECTED";
        var stateBrush = connected ? Theme.Green : opening ? Theme.Yellow : Theme.Red;
        dc.DrawEllipse(stateBrush, null, new Point(InfoX + title.Width + 36, Row0 + 70), 10, 10);
        Draw.Label(dc, state, InfoX + title.Width + 58, Row0 + 70, 32, stateBrush, Align.Left, Theme.Regular);

        string battery = connected ? $"{Session.Level.Battery} %" : "-";
        string port = Session.PortName?.ToUpperInvariant() ?? (connected ? "OFFLINE PREVIEW" : "-");
        string module = Session.Module.Connected && !string.IsNullOrWhiteSpace(Session.Module.Name) ? Session.Module.Name.ToUpperInvariant() : "NONE";
        string sampler = !connected ? "-" : Session.Sampler.Active ? $"SAMPLING ({Session.Sampler.PackProgress} %)" : "IDLE";
        (string, string)[] left = [("SERIAL", known ? device.Serial : "-"), ("FIRMWARE", known ? device.Firmware : "-"), ("MODULE", module)];
        (string, string)[] right = [("BATTERY", battery), ("PORT", port), ("SAMPLER", sampler)];
        double colW = InfoW / 2;
        Draw.HLine(dc, ListRight, RightEdge, TableTop);
        for (int i = 0; i < 3; i++)
        {
            double y = TableTop + i * TableRow;
            Draw.Label(dc, left[i].Item1, InfoX, y + TableRow / 2, 30, Theme.Mid);
            Draw.Label(dc, Widgets.Fit(left[i].Item2, colW - 220, 34, Theme.Regular), InfoX + 190, y + TableRow / 2, 34, Theme.Ink, Align.Left, Theme.Regular);
            Draw.Label(dc, right[i].Item1, InfoX + colW, y + TableRow / 2, 30, Theme.Mid);
            Draw.Label(dc, Widgets.Fit(right[i].Item2, colW - 170, 34, Theme.Regular), InfoX + colW + 150, y + TableRow / 2, 34, Theme.Ink, Align.Left, Theme.Regular);
            Draw.HLine(dc, ListRight, RightEdge, y + TableRow);
        }
        Draw.VLine(dc, InfoX + colW - 24, TableTop, TableTop + 3 * TableRow);
        if (connected && Session.Level.Battery > 0)
        {
            double bx = InfoX + colW + 150 + Draw.Text(battery, 34, Theme.Ink, Theme.Regular).Width + 22, by = TableTop + TableRow / 2;
            Draw.Frame(dc, new Rect(bx, by - 11, 38, 22), Theme.Mid, 3);
            dc.DrawRectangle(Theme.Mid, null, new Rect(bx + 38, by - 5, 5, 10));
            dc.DrawRectangle(Session.Level.Battery < 15 ? Theme.Red : Theme.Ink, null, new Rect(bx + 5, by - 6, 28 * Math.Clamp(Session.Level.Battery, 0, 100) / 100.0, 12));
        }

        // Firmware / app compatibility.
        double boxTop = TableTop + 3 * TableRow + 30;
        var box = new Rect(InfoX, boxTop, InfoW, 200);
        bool? supported = known ? Firmware.SupportsFileService(device.Firmware) : null;
        if (supported == false)
        {
            Draw.Frame(dc, box, Theme.Yellow, 4);
            Draw.Label(dc, "FIRMWARE UPDATE NEEDED", box.X + 28, box.Y + 40, 34, Theme.Ink, Align.Left, Theme.Regular);
            Widgets.Paragraph(dc, $"THIS OP-Z RUNS {device.Firmware}. THE CONFIGURATOR AND FILE TRANSFER NEED FIRMWARE {Firmware.FileService} OR NEWER. EVERYTHING ELSE WORKS AS IT IS.",
                box.X + 28, box.Y + 86, box.Width - 56, 26, Theme.Mid);
            Draw.Label(dc, "UPDATE AT " + Firmware.DownloadUrl.ToUpperInvariant(), box.X + 28, box.Y + 160, 28, Theme.Ink, Align.Left, Theme.Regular);
        }
        else if (supported == true)
        {
            Draw.Frame(dc, box with { Height = 96 }, Theme.Green, 4);
            Draw.Label(dc, "FIRMWARE UP TO DATE", box.X + 28, box.Y + 32, 30, Theme.Green, Align.Left, Theme.Regular);
            Draw.Label(dc, "ALL FEATURES, INCLUDING THE CONFIGURATOR, ARE AVAILABLE", box.X + 28, box.Y + 66, 24, Theme.Mid);
        }
        else if (!connected)
        {
            Widgets.Paragraph(dc, "CONNECT THE OP-Z WITH A USB CABLE AND TURN IT ON. OP-B OPENS THE FIRST MIDI PORT NAMED OP-Z AND CONNECTS BY ITSELF.",
                InfoX, boxTop + 24, InfoW, 28, Theme.Mid);
        }

        _reconnect = new Rect(InfoX, DesignHeight - 112, 300, 80);
        bool recent = (DateTime.UtcNow - _reconnectedAt).TotalSeconds < 2;
        Widgets.Button(dc, _reconnect, recent ? "RECONNECTING" : "RECONNECT", enabled: !recent);
        Draw.Label(dc, connected ? "CLOSES AND REOPENS THE MIDI PORT" : "LOOKS FOR THE OP-Z AGAIN", _reconnect.Right + 28, _reconnect.Y + _reconnect.Height / 2, 24, Theme.Mid);
    }

    // ------------------------------------------------------------------ input

    protected override void OnPointerUp(int id, Point p)
    {
        if (!_reconnect.Contains(p) || (DateTime.UtcNow - _reconnectedAt).TotalSeconds < 2) return;
        _reconnectedAt = DateTime.UtcNow;
        Session.Reconnect();
        RefreshPorts();
        InvalidateVisual();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.1) };
        timer.Tick += (_, _) => { timer.Stop(); RefreshPorts(); InvalidateVisual(); };
        timer.Start();
    }
}
