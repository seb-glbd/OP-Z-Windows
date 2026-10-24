using OPB.Core;
using OPB.Core.Midi;
using OPB.Core.Model;

// Read-only diagnostics: connects, mirrors state for a few seconds, prints a summary.
// Usage: OPB.Probe [seconds] [--raw]
int seconds = args.Length > 0 && int.TryParse(args[0], out var s) ? s : 6;
bool raw = args.Contains("--raw");

Console.WriteLine("MIDI inputs:  " + string.Join(", ", WinMidiConnection.InputNames()));
Console.WriteLine("MIDI outputs: " + string.Join(", ", WinMidiConnection.OutputNames()));

using var session = new OpzSession(() => WinMidiConnection.TryOpen("OP-Z"));
var start = DateTime.Now;
string Stamp() => $"{(DateTime.Now - start).TotalSeconds,7:F3}";
session.Log += m => Console.WriteLine($"{Stamp()} LOG {m}");
session.Changed += c => Console.WriteLine($"{Stamp()} CHANGED {c}");
session.NoteEvent += (t, n, v) => Console.WriteLine($"{Stamp()} NOTE {Tracks.Names[t]} {n} {v}");
if (raw) session.RawSysex += d => Console.WriteLine($"{Stamp()} RX {Convert.ToHexString(d)}");
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
