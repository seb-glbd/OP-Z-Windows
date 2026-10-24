using OPB.Core.Model;
using OPB.Core.Protocol;

namespace OPB.Core.Tests;

public class Pack7Tests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(13)]
    [InlineData(14)]
    [InlineData(200)]
    [InlineData(1000)]
    public void RoundTrips(int length)
    {
        var rng = new Random(length);
        var data = new byte[length];
        rng.NextBytes(data);
        var packed = Pack7.Encode(data);
        Assert.Equal(length + (length + 6) / 7, packed.Length);
        Assert.All(packed, b => Assert.True(b < 0x80));
        Assert.Equal(data, Pack7.Decode(packed));
    }

    [Fact]
    public void KnownVectors()
    {
        // High bits go to the group's mask byte, bit N for byte N.
        Assert.Equal(Fixtures.Hex("00 01 02 03"), Pack7.Encode([1, 2, 3]));
        Assert.Equal(Fixtures.Hex("05 7F 00 01"), Pack7.Encode([0xFF, 0x00, 0x81]));
        Assert.Equal(Fixtures.Hex("7F 00 01 02 03 04 05 06 00 07"), Pack7.Encode([0x80, 0x81, 0x82, 0x83, 0x84, 0x85, 0x86, 0x07]));
        Assert.Equal(new byte[] { 0x80, 0x81, 0x82, 0x83, 0x84, 0x85, 0x86, 0x07 }, Pack7.Decode(Fixtures.Hex("7F 00 01 02 03 04 05 06 00 07")));
        // The heartbeat body 01 4E 2E 06: mask 01 sets the high bit of the first byte.
        Assert.Equal(new byte[] { 0xCE, 0x2E, 0x06 }, Pack7.Decode(Fixtures.Hex("01 4E 2E 06")));
    }

    [Fact]
    public void DecodesDeviceInfoFrame()
    {
        var frame = Fixtures.Hex("F0 00 20 76 01 11 00 58 30 30 30 30 30 30 00 31 00 31 2E 32 2E 31 00 37 2B 00 00 00 00 00 00 00 00 F7");
        Assert.True(OpzFrame.TryParse(frame, out var f));
        Assert.Equal(0x11, f.MessageId);
        var info = DeviceInfo.FromMessage11(f.Payload);
        Assert.Equal("X0000001", info.Serial);
        Assert.Equal("1.2.17+", info.Firmware);
        Assert.Equal(new Version(1, 2, 17), Firmware.Parse(info.Firmware));
        Assert.False(Firmware.SupportsFileService(info.Firmware));
        Assert.True(Firmware.SupportsFileService("1.2.45+"));
        Assert.True(Firmware.SupportsFileService("1.3.0"));
        Assert.Null(Firmware.SupportsFileService(""));
    }
}

public class FrameTests
{
    [Fact]
    public void BuildAndParse()
    {
        byte[] payload = [0x00, 0x7F, 0x80, 0xFF, 0x12, 0x34, 0x56, 0x78, 0x9A];
        var frame = OpzFrame.Build(0x0E, payload);
        Assert.Equal(OpzFrame.Header, frame[..5]);
        Assert.Equal(0x0E, frame[5]);
        Assert.Equal(0xF7, frame[^1]);
        Assert.True(OpzFrame.TryParse(frame, out var parsed));
        Assert.Equal(0x0E, parsed.MessageId);
        Assert.Equal(payload, parsed.Payload);
    }

    [Fact]
    public void EmptyFrames()
    {
        Assert.Equal(Fixtures.Hex("F0 00 20 76 01 0F F7"), OpzFrame.Build(0x0F));
        Assert.True(OpzFrame.TryParse(OpzFrame.Build(0x08), out var f));
        Assert.Empty(f.Payload);
    }

    [Fact]
    public void RejectsForeignOrBrokenFrames()
    {
        Assert.False(OpzFrame.TryParse(Fixtures.Hex("F0 7E 7F 06 01 F7"), out _));
        Assert.False(OpzFrame.TryParse(Fixtures.Hex("F0 00 20 76 01 0F"), out _));
        Assert.False(OpzFrame.TryParse(Fixtures.Hex("F0 00 20 76 01 0F 00 80 F7"), out _));
        Assert.False(OpzFrame.TryParse(Fixtures.Hex("F0 00 20 77 01 0F 00 F7"), out _));
    }

    [Fact]
    public void Heartbeat()
    {
        Assert.True(OpzFrame.TryParse(OpzFrame.Heartbeat, out var f));
        Assert.Equal(0x00, f.MessageId);
    }

    [Fact]
    public void IdentityReply()
    {
        Assert.True(OpzFrame.IsIdentityReply(Fixtures.Hex("F0 7E 7F 06 02 00 20 76 01 58 33 F7")));
        Assert.False(OpzFrame.IsIdentityReply(OpzFrame.IdentityInquiry));
    }

    [Fact]
    public void ZlibRoundTripAndLimit()
    {
        var data = Enumerable.Range(0, 5000).Select(i => (byte)(i * 7)).ToArray();
        Assert.Equal(data, Zlib.Decompress(Zlib.Compress(data)));
        Assert.Throws<InvalidDataException>(() => Zlib.Decompress(Zlib.Compress(data), 100));
    }
}

public class StateTests
{
    [Fact]
    public void ChainRoundTrip()
    {
        int[] chain = [3, 1, 4, 1, 5];
        var payload = ChainState.Encode(chain, 2);
        Assert.Equal(20, payload.Length);
        Assert.Equal(0x13, payload[0]);
        Assert.Equal(0x14, payload[1]);
        Assert.Equal(0x05, payload[2]);
        var state = ChainState.Decode(payload);
        Assert.Equal(chain, state.Chain);
        Assert.Equal(3, state.Pattern);
        Assert.Equal(2, state.Project);
        Assert.Equal(1, state.NextIndex);
        Assert.Equal(1, state.Position);
    }

    [Fact]
    public void SinglePatternChain()
    {
        var state = ChainState.Decode(ChainState.Encode([7], 0));
        Assert.Equal([7], state.Chain);
        Assert.Equal(7, state.Pattern);
        Assert.Equal(0, state.NextIndex);
        Assert.Equal(1, state.Position);
        Assert.Empty(ChainState.Decode([1, 2]).Chain);
    }

    [Fact]
    public void PlayState()
    {
        var s = Model.PlayState.Decode([0xFE, 0x15]);
        Assert.Equal(-2, s.Octave);
        Assert.Equal(5, s.Track);
        Assert.True(s.Playing);
        Assert.Equal(new byte[] { 0xFE, 0x15 }, s.Encode());
        Assert.False(Model.PlayState.Decode([0, 0x0C]).Playing);
    }

    [Fact]
    public void LevelState()
    {
        var s = Model.LevelState.Decode([0x5A, 0xC8, 0x65, 0x13]);
        Assert.Equal(90, s.Battery);
        Assert.Equal(200, s.MasterVolume);
        Assert.Equal(6, s.MicLevel);
        Assert.Equal(2, s.MicFx);
        Assert.True(s.Monitor);
        Assert.Equal(3, s.InputSelector);
        Assert.True(s.MicMode);
        Assert.Equal(default, Model.LevelState.Decode([1]));
    }

    [Fact]
    public void UiState()
    {
        var idle = Model.UiState.Decode([0x80, 0x1F, 0x00, 0x00]);
        Assert.Equal(2, idle.Page);
        Assert.Equal(-1, idle.HeldKey);
        Assert.False(idle.Shift);

        var held = Model.UiState.Decode([0xC0, 0x20 | 0x40 | 0x80 | 0x02, 0x20 | 0x40 | 0x80, 0x03]);
        Assert.Equal(3, held.Page);
        Assert.Equal(2, held.HeldKey);
        Assert.True(held.Shift && held.Tempo && held.Mixer && held.Screen && held.Stop && held.Record && held.TrackHeld && held.Project);

        var empty = Model.UiState.Decode([]);
        Assert.Equal(-1, empty.HeldKey);
    }

    [Fact]
    public void MixerRoundTrip()
    {
        var levels = Enumerable.Range(0, 16).Select(i => (byte)(i * 10)).ToArray();
        var state = new MixerState(levels, 0b1010);
        var decoded = MixerState.Decode(state.Encode());
        Assert.Equal(levels, decoded.Levels);
        Assert.True(decoded.IsMuted(1));
        Assert.False(decoded.IsMuted(0));
    }

    [Fact]
    public void ModuleInfo()
    {
        var p = new byte[19];
        p[0] = 1; p[4] = 2;
        "ZM-2"u8.CopyTo(p.AsSpan(8));
        p[18] = 1;
        var m = Model.ModuleInfo.Decode(p);
        Assert.Equal("ZM-2", m.Name);
        Assert.True(m.Connected);
        Assert.Equal(1u, m.Caps);
        Assert.Equal(2u, m.Status);
    }
}

public class FixtureTests
{
    [Fact]
    public void PatternBankKickTrigsAndLock()
    {
        var bank = new PatternBank(Fixtures.Read("bank.bin"));
        int[] trigs = Enumerable.Range(0, 16).Where(s => bank.HasTrig(0, 0, s)).ToArray();
        // Steps 1, 5, 9 and 13 at velocity 100, plus a softer trig on step 11 in this capture.
        Assert.Equal([0, 4, 8, 10, 12], trigs);
        Assert.Equal(new Note(6200, 53, 100, 0), bank.Notes(0, 0, 8).Single());
        Assert.Equal(80, bank.Notes(0, 0, 10).Single().Velocity);
        Assert.Equal(200, bank.LockValue(0, 0, 8, SoundParam.Filter));
        Assert.True(bank.IsLocked(0, 0, 8, SoundParam.Filter));
        Assert.True(bank.HasAnyLock(0, 0, 8));
        Assert.False(bank.IsLocked(0, 0, 0, SoundParam.Filter));
        Assert.False(bank.HasAnyLock(0, 0, 4));
        Assert.Equal(16, bank.StepCount(0, 0));
        Assert.Equal(150u, bank.Plug(0, 0));
        Assert.Equal(98u, bank.Plug(0, (int)TrackId.Arp));
    }

    [Fact]
    public void PatternBankOffsets()
    {
        Assert.Equal(342_272, PatternBank.Size);
        var bank = new PatternBank(new byte[PatternBank.Size]);
        // Notes: 8-byte records, 55 slots per step, track slot offsets.
        Assert.Equal(192, bank.NoteOffset(0, 0, 0, 0));
        Assert.Equal(192 + 2 * 8, bank.NoteOffset(0, 1, 0, 0));
        Assert.Equal(192 + 16 * 8, bank.NoteOffset(0, (int)TrackId.Arp, 0, 0));
        Assert.Equal(192 + (55 + 51) * 8, bank.NoteOffset(0, (int)TrackId.Motion, 1, 0));
        Assert.Equal(3 * PatternBank.PatternSize + 192 + (4 * 55 + 16) * 8, bank.NoteOffset(3, 6, 4, 0));
        Assert.Equal(3 * PatternBank.PatternSize + 7_232 + (6 * 16 + 4) * 54, bank.StepOffset(3, 6, 4));
        int last = Tracks.NoteSlotOffset[15] + Tracks.NotesPerStep[15];
        Assert.Equal(55, last);
        for (int t = 1; t < 16; t++) Assert.Equal(Tracks.NoteSlotOffset[t - 1] + Tracks.NotesPerStep[t - 1], Tracks.NoteSlotOffset[t]);
    }

    [Fact]
    public void PatternBankEditsPreserveOtherBytes()
    {
        var original = Fixtures.Read("bank.bin");
        var bank = new PatternBank((byte[])original.Clone());
        bank.SetNotes(3, 6, 4, [new Note(6200, 60, 91, -1), new Note(12400, 67, 92, 2)]);
        int o = bank.NoteOffset(3, 6, 4, 0);
        Assert.Equal(new byte[] { 60, 91, 0xFF }, bank.Data[(o + 4)..(o + 7)]);
        Assert.Equal(2, bank.Notes(3, 6, 4).Count);
        int changed = Enumerable.Range(0, PatternBank.Size).Count(i => bank.Data[i] != original[i]);
        Assert.InRange(changed, 1, 8 * 8);
        bank.SetComponent(0, 0, 0, 3, 42);
        Assert.Equal(1 << 3, bank.ComponentMask(0, 0, 0) & (1 << 3));
        Assert.Equal(42, bank.ComponentValue(0, 0, 0, 3));
        bank.SetComponent(0, 0, 0, 3, null);
        Assert.Equal(0, bank.ComponentMask(0, 0, 0) & (1 << 3));
    }

    [Fact]
    public void GlobalTempo()
    {
        var g = new GlobalData(Fixtures.Read("global.bin"));
        Assert.Equal(174, g.Tempo);
        var copy = g.Clone();
        copy.Tempo = 250;
        Assert.Equal(200, copy.Tempo);
        Assert.Equal(174, g.Tempo);
        Assert.Throws<ArgumentException>(() => new GlobalData(new byte[10]));
    }

    [Fact]
    public void MidiConfigFixture()
    {
        var m = new MidiConfig(Fixtures.Read("midiconfig.bin"));
        for (int t = 0; t < 16; t++)
        {
            Assert.True(m.TrackEnabled(t));
            Assert.Equal(t, m.Channel(t));
            for (int p = 0; p < 16; p++) Assert.Equal(p + 1, m.ParameterCc(t, p));
        }
        Assert.True(m.Flag(MidiFlag.ChannelOneToActive));
        Assert.True(m.Flag(MidiFlag.ProgramChange));
        Assert.False(m.Flag(MidiFlag.AltProgramChange));
    }

    [Fact]
    public void MidiConfigEditsPreserveReservedBytes()
    {
        var data = Fixtures.Read("midiconfig.bin");
        data[289] = 0xAB; data[290] = 0xCD; data[291] = 0xEF;
        var m = new MidiConfig(data).Clone();
        m.SetTrackEnabled(3, false);
        m.SetChannel(3, 20);
        m.SetParameterCc(15, 15, 17);
        m.SetParameterCc(0, 0, 300);
        m.SetFlag(MidiFlag.AltProgramChange, true);
        m.SetFlag(MidiFlag.ChannelOneToActive, false);
        Assert.False(m.TrackEnabled(3));
        Assert.Equal(15, m.Channel(3));
        Assert.Equal(17, m.Data[287]);
        Assert.Equal(127, m.ParameterCc(0, 0));
        Assert.True(m.Flag(MidiFlag.AltProgramChange));
        Assert.False(m.Flag(MidiFlag.ChannelOneToActive));
        Assert.Equal(new byte[] { 0xAB, 0xCD, 0xEF }, m.Data[289..]);
        // The $10 frame is the zlib-compressed 292 bytes.
        Assert.True(OpzFrame.TryParse(OpzFrame.Build(0x10, Zlib.Compress(m.Data)), out var f));
        Assert.Equal(m.Data, Zlib.Decompress(f.Payload, MidiConfig.Size));
        Assert.Throws<ArgumentException>(() => new MidiConfig(new byte[291]));
    }

    [Fact]
    public void SessionAppliesInjectedState()
    {
        var session = new OpzSession(() => null);
        var changes = Changes.None;
        session.Changed += c => changes |= c;
        session.InjectSysex(OpzFrame.Build(0x11, "X0000001\01.2.17+\0"u8.ToArray()));
        session.InjectSysex(OpzFrame.Build(0x10, Zlib.Compress(Fixtures.Read("midiconfig.bin"))));
        session.InjectSysex(OpzFrame.Build(0x0C, Zlib.Compress(Fixtures.Read("global.bin"))));
        session.InjectSysex(OpzFrame.Build(0x03, new PlayState(1, 5, false).Encode()));
        Assert.Equal("X0000001", session.Device.Serial);
        Assert.Equal(174, session.Global!.Tempo);
        Assert.NotNull(session.MidiConfig);
        Assert.Equal(5, session.ActiveTrack);
        Assert.True(changes.HasFlag(Changes.MidiConfig) && changes.HasFlag(Changes.Device) && changes.HasFlag(Changes.Global));
    }
}
