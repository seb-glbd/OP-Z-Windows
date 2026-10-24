using OPB.Core.Midi;
using OPB.Core.Model;
using OPB.Core.Protocol;

namespace OPB.Core.Tests;

public class SessionSendTests
{
    sealed class FakeLink : IMidiConnection
    {
        public readonly List<byte[]> Sent = new();
        public volatile int Queued;
        public string Name => "fake";
        public event Action<byte[]>? SysexReceived { add { } remove { } }
        public event Action<byte, byte, byte>? ShortReceived { add { } remove { } }
        public void SendSysex(byte[] message) { lock (Sent) Sent.Add(message); }
        public void SendShort(byte status, byte data1 = 0, byte data2 = 0) { }
        public int QueuedPackets => Queued;
        public void Dispose() { }

        public List<byte[]> SoundPayloads()
        {
            lock (Sent)
                return Sent.Select(m => OpzFrame.TryParse(m, out var f) && f.MessageId == 0x0E ? f.Payload : null)
                    .OfType<byte[]>().ToList();
        }
    }

    static void WaitFor(Func<bool> condition)
    {
        var until = DateTime.UtcNow.AddSeconds(2);
        while (!condition() && DateTime.UtcNow < until) Thread.Sleep(5);
        Assert.True(condition());
    }

    [Fact]
    public void SoundEditsWaitForABusyLinkAndKeepOnlyTheNewest()
    {
        var link = new FakeLink();
        using var session = new OpzSession(() => link);
        session.Start();
        WaitFor(() => { lock (link.Sent) return link.Sent.Count > 0; });

        session.SetSound(0, SoundParam.Filter, 1);
        Assert.Single(link.SoundPayloads());

        link.Queued = 3;
        for (byte v = 2; v <= 40; v++) session.SetSound(0, SoundParam.Filter, v);
        Thread.Sleep(150);
        Assert.Single(link.SoundPayloads());

        link.Queued = 0;
        WaitFor(() => link.SoundPayloads().Count == 2);
        Thread.Sleep(100);
        var sent = link.SoundPayloads();
        Assert.Equal(2, sent.Count);
        Assert.Equal(40, sent[1][1 + (int)SoundParam.Filter]);
    }
}
