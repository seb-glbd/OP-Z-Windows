using System.Globalization;
using OPB.Core;
using OPB.Core.Midi;
using OPB.Core.Model;

// Read-only diagnostics: connects, mirrors state for a few seconds, prints a summary.
// Usage: OPB.Probe [seconds] [--raw] [--ble [address]]
int seconds = args.Length > 0 && int.TryParse(args[0], out var s) ? s : 6;
bool raw = args.Contains("--raw");

Console.WriteLine("MIDI inputs:  " + string.Join(", ", WinMidiConnection.InputNames()));
Console.WriteLine("MIDI outputs: " + string.Join(", ", WinMidiConnection.OutputNames()));

// --ble-scan: list paired LE devices and nearby advertisements, then exit.
if (args.Contains("--ble-scan"))
{
    await BleScan.RunAsync(seconds);
    return;
}

// --ble-connect <address>: one diagnostic GATT connection attempt, then exit.
int bleConnect = Array.IndexOf(args, "--ble-connect");
if (bleConnect >= 0 && bleConnect + 1 < args.Length)
{
    await BleScan.ConnectTestAsync(ulong.Parse(args[bleConnect + 1].Replace(":", ""), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
    return;
}

// --ble: connect over Bluetooth instead of USB, to the given address or the first OP-Z advertising.
int ble = Array.IndexOf(args, "--ble");
using var bluetooth = ble >= 0 ? new OpzBluetooth() : null;
if (bluetooth is not null)
{
    bluetooth.Log += m => Console.WriteLine($"BLE {m}");
    if (ble + 1 < args.Length && ulong.TryParse(args[ble + 1].Replace(":", ""), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var address))
        bluetooth.Target = address;
    else
    {
        Console.WriteLine("Scanning for an OP-Z over Bluetooth: press the button on its back...");
        bluetooth.SetScanning(true);
        var until = DateTime.Now.AddSeconds(20);
        while (bluetooth.Target is null && DateTime.Now < until)
        {
            Thread.Sleep(250);
            if (bluetooth.Devices.FirstOrDefault(d => d.IsOpz) is { } opz)
            {
                Console.WriteLine($"Found {opz.Name} {opz.AddressText} ({opz.Rssi} dBm)");
                bluetooth.Target = opz.Address;
            }
        }
        bluetooth.SetScanning(false);
        if (bluetooth.Target is null)
        {
            Console.WriteLine($"No OP-Z found (Bluetooth {bluetooth.State}).");
            return;
        }
    }
}

using var session = new OpzSession(bluetooth is not null ? bluetooth.TakeConnection : () => WinMidiConnection.TryOpen("OP-Z"));
var start = DateTime.Now;
string Stamp() => $"{(DateTime.Now - start).TotalSeconds,7:F3}";
session.Log += m => Console.WriteLine($"{Stamp()} LOG {m}");
session.Changed += c => Console.WriteLine($"{Stamp()} CHANGED {c}");
session.NoteEvent += (t, n, v) => Console.WriteLine($"{Stamp()} NOTE {Tracks.Names[t]} {n} {v}");
if (raw) session.RawSysex += d => Console.WriteLine($"{Stamp()} RX {Convert.ToHexString(d)}");
// --deltas: decode pattern deltas ($02) and time pattern bank transfers ($09/$0A).
if (args.Contains("--deltas"))
{
    session.RawSysex += d =>
    {
        if (!OPB.Core.Protocol.OpzFrame.TryParse(d, out var f)) return;
        if (f.MessageId == 0x02) Console.WriteLine($"{Stamp()} DELTA {Convert.ToHexString(f.Payload)}");
        else if (f.MessageId is 0x09 or 0x0A && f.Payload.Length >= 6)
            Console.WriteLine($"{Stamp()} BANK ${f.MessageId:X2} packet {f.Payload[4] | (f.Payload[5] << 8)} ({f.Payload.Length} bytes)");
    };
}
// --short: log every non-realtime MIDI message (the connection opens later, so hook on the first change).
bool shortHooked = false;
session.Changed += _ =>
{
    if (shortHooked || !args.Contains("--short") || session.Connection is null) return;
    shortHooked = true;
    session.Connection.ShortReceived += (st, d1, d2) =>
    {
        if (st >= 0xF8) return; // clock and other realtime bytes
        Console.WriteLine($"{Stamp()} MIDI {st:X2} {d1:X2} {d2:X2}  ch{(st & 0x0F) + 1} {(st & 0xF0) switch { 0x90 => "ON ", 0x80 => "OFF", 0xB0 => "CC ", _ => "   " }} {d1} ({Display.NoteName(d1)}) {d2}");
    };
};
session.Changed += c => { if ((c & Changes.Ui) != 0) Console.WriteLine($"{Stamp()} UI raw {Convert.ToHexString(session.Ui.Raw)} shift={session.Ui.Shift} held={session.Ui.HeldKey}"); };
session.Changed += c => { if ((c & Changes.Play) != 0) Console.WriteLine($"{Stamp()} PLAY track={Tracks.Names[session.Play.Track]} octave={session.Play.Octave} playing={session.Play.Playing}"); };
session.Start();
Thread.Sleep(TimeSpan.FromSeconds(seconds));
int setTempo = Array.IndexOf(args, "--set-tempo");
if (setTempo >= 0 && session.Global is not null)
{
    session.EditGlobal(g => g.Tempo = int.Parse(args[setTempo + 1]));
    Thread.Sleep(800);
}
// --drag [seconds] [edits per second]: replay a touch pad drag on the active track's filter.
int drag = Array.IndexOf(args, "--drag");
if (drag >= 0 && session.IsConnected)
{
    int dragSeconds = drag + 1 < args.Length && int.TryParse(args[drag + 1], out var ds) ? ds : 10;
    int dragRate = drag + 2 < args.Length && int.TryParse(args[drag + 2], out var dr) ? dr : 120;
    DragTest.Run(session, dragSeconds, dragRate, Stamp);
}
// --filter-to <value>: set the active track's filter (puts it back after an interrupted drag).
int filterTo = Array.IndexOf(args, "--filter-to");
if (filterTo >= 0 && filterTo + 1 < args.Length && session.IsConnected)
{
    session.SetSound(session.ActiveTrack, SoundParam.Filter, byte.Parse(args[filterTo + 1]));
    Thread.Sleep(1500);
}
if (args.Contains("--file-test") && session.IsConnected)
    await FileTest.RunAsync(session);
if (args.Contains("--write-test") && session.IsConnected && session.Bank is not null)
    await WriteTest.RunAsync(session);

int dump = Array.IndexOf(args, "--dump");
if (dump >= 0 && dump + 1 < args.Length)
{
    Directory.CreateDirectory(args[dump + 1]);
    if (session.Bank is not null) File.WriteAllBytes(Path.Combine(args[dump + 1], "bank.bin"), session.Bank.Data);
    if (session.Global is not null) File.WriteAllBytes(Path.Combine(args[dump + 1], "global.bin"), session.Global.Data);
    if (session.MidiConfig is not null) File.WriteAllBytes(Path.Combine(args[dump + 1], "midiconfig.bin"), session.MidiConfig.Data);
}

Console.WriteLine();
Console.WriteLine($"Connected: {session.IsConnected}  Device: {session.Device.Serial} fw {session.Device.Firmware}");
Console.WriteLine($"Play: {session.Play}  Level: {session.Level}");
Console.WriteLine($"Ui: page {session.Ui.Page} held {session.Ui.HeldKey}  Chain: project {session.Chain.Project + 1} pattern {session.Chain.Pattern + 1}");
Console.WriteLine($"Clock ticks: {session.ClockTicks}  running {session.ClockRunning}");
if (session.Global is { } g)
    Console.WriteLine($"Global: tempo {g.Tempo} swing {g.Swing} drum {g.DrumLevel} synth {g.SynthLevel} punch {g.PunchLevel} master {g.MasterLevel} metronome {g.MetronomeLevel}/{g.MetronomeSound}");
if (session.MidiConfig is { } mc)
    Console.WriteLine($"MIDI config: flags {mc.Data[288]:X2} channels {string.Join(",", Enumerable.Range(0, 16).Select(mc.Channel))}");
if (session.HasMixer)
    Console.WriteLine($"Mixer: {string.Join(",", session.Mixer.Levels)} mutes {session.Mixer.MuteMask:X4}");
if (session.Bank is { } bank)
{
    int pattern = session.ActivePattern;
    for (int t = 0; t < Tracks.Count; t++)
    {
        var trigs = string.Concat(Enumerable.Range(0, 16).Select(st => bank.HasTrig(pattern, t, st) ? 'x' : '.'));
        Console.WriteLine($"{Tracks.Names[t],-8} plug {bank.Plug(pattern, t),6} steps {bank.StepCount(pattern, t),2} x{bank.StepLength(pattern, t)} {trigs} sound {Convert.ToHexString(session.CurrentSound[t])}");
    }
}
