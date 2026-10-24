using OPB.Core.Midi;
using OPB.Core.Protocol;

namespace OPB.Core.Tests;

public class BleMidiTests
{
    sealed class Collector
    {
        public readonly List<byte[]> Sysex = new();
        public readonly List<(byte, byte, byte)> Short = new();
        public readonly BleMidiDecoder Decoder = new();

        public Collector()
        {
            Decoder.SysexReceived += Sysex.Add;
            Decoder.ShortReceived += (s, a, b) => Short.Add((s, a, b));
        }
    }

    [Fact]
    public void EncodesShortMessagesWithTimestamp()
    {
        // Timestamp 0x1234 ms: header 80 | (0x1234 >> 7 & 3F) = A4, stamp 80 | 34 = B4.
        Assert.Equal(Fixtures.Hex("A4 B4 90 3C 64"), BleMidiCodec.EncodeShort(0x90, 60, 100, 0x1234));
        Assert.Equal(Fixtures.Hex("80 80 C1 05"), BleMidiCodec.EncodeShort(0xC1, 5, 0, 0));
        Assert.Equal(Fixtures.Hex("80 80 FA"), BleMidiCodec.EncodeShort(0xFA, 0, 0, 0));
    }

    [Fact]
    public void SmallSysexFitsOnePacket()
    {
        var packets = BleMidiCodec.EncodeSysex(Fixtures.Hex("F0 00 20 76 01 F7"), 20, 0);
        Assert.Single(packets);
        Assert.Equal(Fixtures.Hex("80 80 F0 00 20 76 01 80 F7"), packets[0]);
    }

    [Theory]
    [InlineData(2, 20)]
    [InlineData(15, 20)]
    [InlineData(16, 20)]
    [InlineData(17, 20)]
    [InlineData(18, 20)]
    [InlineData(300, 20)]
    [InlineData(300, 5)]
    [InlineData(2000, 182)]
    [InlineData(2000, 244)]
    public void SysexRoundTripsAcrossPackets(int length, int maxPacket)
    {
        var rng = new Random(length * 1000 + maxPacket);
        var message = new byte[length];
        for (int i = 0; i < length; i++) message[i] = (byte)rng.Next(0x80);
        message[0] = 0xF0;
        message[^1] = 0xF7;

        var packets = BleMidiCodec.EncodeSysex(message, maxPacket, 0x0FFF);
        Assert.All(packets, p => Assert.InRange(p.Length, 2, maxPacket));
        Assert.All(packets, p => Assert.Equal(0x80 | 0x1F, p[0]));

        var c = new Collector();
        foreach (var p in packets) c.Decoder.Feed(p);
        Assert.Single(c.Sysex);
        Assert.Equal(message, c.Sysex[0]);
        Assert.Empty(c.Short);
    }

    [Fact]
    public void RealOpzFrameRoundTrips()
    {
        var hello = OpzFrame.Hello(0x1234);
        var c = new Collector();
        foreach (var p in BleMidiCodec.EncodeSysex(hello, 20, 77)) c.Decoder.Feed(p);
        Assert.Equal(hello, Assert.Single(c.Sysex));
    }

    [Fact]
    public void DecodesRunningStatusAndSeveralMessagesPerPacket()
    {
        var c = new Collector();
        // Note on, then running status with a new timestamp, then running status without one, then a CC.
        c.Decoder.Feed(Fixtures.Hex("80 81 90 3C 64 82 3E 64 40 00 83 B2 07 7F"));
        Assert.Equal(new[] { ((byte)0x90, (byte)0x3C, (byte)0x64), ((byte)0x90, (byte)0x3E, (byte)0x64), ((byte)0x90, (byte)0x40, (byte)0x00), ((byte)0xB2, (byte)0x07, (byte)0x7F) }, c.Short);
        // Running status carries over to the next packet.
        c.Decoder.Feed(Fixtures.Hex("80 85 0A 0B"));
        Assert.Equal(((byte)0xB2, (byte)0x0A, (byte)0x0B), c.Short[^1]);
    }

    [Fact]
    public void RealtimeInsideSysexIsDeliveredSeparately()
    {
        var c = new Collector();
        c.Decoder.Feed(Fixtures.Hex("80 80 F0 00 20 81 F8 76 01"));
        c.Decoder.Feed(Fixtures.Hex("80 02 03 82 F7 83 FC"));
        Assert.Equal(Fixtures.Hex("F0 00 20 76 01 02 03 F7"), Assert.Single(c.Sysex));
        Assert.Equal(new[] { ((byte)0xF8, (byte)0, (byte)0), ((byte)0xFC, (byte)0, (byte)0) }, c.Short);
    }

    [Fact]
    public void NewStatusAbortsUnfinishedSysex()
    {
        var c = new Collector();
        c.Decoder.Feed(Fixtures.Hex("80 80 F0 00 20"));
        c.Decoder.Feed(Fixtures.Hex("80 81 90 3C 64 82 F0 01 83 F7"));
        Assert.Equal(Fixtures.Hex("F0 01 F7"), Assert.Single(c.Sysex));
        Assert.Equal(((byte)0x90, (byte)0x3C, (byte)0x64), Assert.Single(c.Short));
    }

    [Fact]
    public void IgnoresPacketsWithoutHeader()
    {
        var c = new Collector();
        c.Decoder.Feed(Fixtures.Hex("00 90 3C 64"));
        c.Decoder.Feed([]);
        Assert.Empty(c.Short);
        Assert.Empty(c.Sysex);
    }
}
