using OPB.Core;
using OPB.Core.Model;

/// <summary>
/// Reversible write checks against a connected OP-Z. Every change is undone and verified by readback.
/// Only run on a pattern you don't mind being briefly edited.
/// </summary>
static class WriteTest
{
    public static async Task RunAsync(OpzSession session)
    {
        var log = new List<string>();
        void Say(string s) { Console.WriteLine($"  >> {s}"); }

        // 1. $0E: rewrite the kick's current record unchanged (no-op), watch for an echo.
        var echo = 0;
        session.Changed += c => { if ((c & Changes.Sound) != 0) echo++; };
        var kick = (byte[])session.CurrentSound[0].Clone();
        session.SetSound(0, SoundParam.P1, kick[(int)SoundParam.P1]);
        await Task.Delay(500);
        Say($"$0E no-op write sent; sound changes observed: {echo}");

        // 2. $0E real change and restore on the kick's filter.
        byte filter = kick[(int)SoundParam.Filter];
        session.SetSound(0, SoundParam.Filter, (byte)(filter ^ 0x40));
        await Task.Delay(300);
        await session.RefreshBankAsync(TimeSpan.FromSeconds(3));
        Say($"filter {filter} -> {(byte)(filter ^ 0x40)}; bank readback {session.Bank!.Sound(session.ActivePattern, 0, SoundParam.Filter)}");
        session.SetSound(0, SoundParam.Filter, filter);
        await Task.Delay(300);
        await session.RefreshBankAsync(TimeSpan.FromSeconds(3));
        Say($"restored; bank readback {session.Bank!.Sound(session.ActivePattern, 0, SoundParam.Filter)}");

        // 3. $03 track select round trip.
        int track = session.Play.Track;
        session.SelectTrack((track + 1) % 8);
        await Task.Delay(400);
        Say($"selected track {(track + 1) % 8}; device reports {session.Play.Track}");
        session.SelectTrack(track);
        await Task.Delay(400);
        Say($"restored track {track}; device reports {session.Play.Track}");

        // 3b. $0C tempo change and restore; a fresh heartbeat session must report it.
        if (session.Global is { } global)
        {
            var before = global.Clone();
            session.EditGlobal(g => g.Tempo = before.Tempo + 1);
            await Task.Delay(600);
            session.SendFrame(0x00, []); // not a heartbeat; harmless, ignored
            Say($"tempo {before.Tempo} -> {session.Global!.Tempo} (local)");
            session.SetGlobal(before);
            await Task.Delay(600);
            Say($"tempo restored to {session.Global!.Tempo} (local)");
        }

        // 3c. CC 60 step count on the kick channel, read back through the bank, then restore.
        int channel = session.MidiConfig?.Channel(0) ?? 0;
        int steps = session.Bank!.StepCount(session.ActivePattern, 0);
        session.SendShort((byte)(0xB0 | channel), 60, (byte)(steps == 16 ? 15 : 16));
        await Task.Delay(400);
        await session.RefreshBankAsync(TimeSpan.FromSeconds(3));
        Say($"step count CC: {steps} -> {session.Bank!.StepCount(session.ActivePattern, 0)}");
        session.SendShort((byte)(0xB0 | channel), 60, (byte)steps);
        await Task.Delay(400);
        await session.RefreshBankAsync(TimeSpan.FromSeconds(3));
        Say($"step count restored: {session.Bank!.StepCount(session.ActivePattern, 0)}");

        // 4. Pattern bank upload: add a kick trig on step 1, read back, remove it, verify exact restore.
        var original = session.Bank!.Clone();
        int pattern = session.ActivePattern;
        bool hadTrig = original.HasTrig(pattern, 0, 0);
        bool ok = await session.EditBankAsync(b =>
        {
            if (hadTrig) b.SetNotes(pattern, 0, 0, []);
            else b.SetNotes(pattern, 0, 0, [new Note(PatternBank.DurationPerStep, 53, 100, 0)]);
        });
        Say($"bank upload ok={ok}; kick step 1 trig now {session.Bank!.HasTrig(pattern, 0, 0)} (was {hadTrig})");
        ok = await session.UploadBankAsync(original.Clone());
        bool exact = session.Bank!.Data.AsSpan().SequenceEqual(original.Data);
        Say($"restore upload ok={ok}; bank identical to original: {exact}");
        if (!exact)
        {
            int diffs = 0, first = -1;
            for (int i = 0; i < original.Data.Length; i++) if (original.Data[i] != session.Bank.Data[i]) { diffs++; if (first < 0) first = i; }
            Say($"differences: {diffs} bytes, first at {first}");
        }
    }
}
