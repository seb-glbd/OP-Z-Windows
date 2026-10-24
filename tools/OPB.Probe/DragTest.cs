using OPB.Core;
using OPB.Core.Model;

/// <summary>
/// Replays a touch pad drag: the filter of the active track swept up and down at mouse-move
/// rate, logging the link's send queue, then the original value is put back.
/// </summary>
static class DragTest
{
    public static void Run(OpzSession session, int seconds, int rate, Func<string> stamp)
    {
        int track = session.ActiveTrack;
        byte original = session.CurrentSound[track][(int)SoundParam.Filter];
        Console.WriteLine($"{stamp()} DRAG track {Tracks.Names[track]} filter from {original}, {rate} edits/s for {seconds} s");
        var until = DateTime.Now.AddSeconds(seconds);
        var lastReport = DateTime.Now;
        int value = original, direction = 2, edits = 0, maxQueued = 0;
        while (DateTime.Now < until)
        {
            value += direction;
            if (value is >= 255 or <= 0) { direction = -direction; value = Math.Clamp(value, 0, 255); }
            session.SetSound(track, SoundParam.Filter, (byte)value);
            edits++;
            int queued = session.Connection?.QueuedPackets ?? -1;
            maxQueued = Math.Max(maxQueued, queued);
            if ((DateTime.Now - lastReport).TotalMilliseconds >= 500)
            {
                Console.WriteLine($"{stamp()} DRAG edits {edits} queued {queued} (max {maxQueued}) connected {session.IsConnected}");
                lastReport = DateTime.Now;
                maxQueued = 0;
            }
            Thread.Sleep(1000 / rate);
        }
        Thread.Sleep(500);
        session.SetSound(track, SoundParam.Filter, original);
        Console.WriteLine($"{stamp()} DRAG done, filter back to {original}");
        Thread.Sleep(1500);
    }
}
