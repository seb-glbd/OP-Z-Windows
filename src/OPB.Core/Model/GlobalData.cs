using System.Buffers.Binary;

namespace OPB.Core.Model;

/// <summary>The 568-byte project-global block carried by $0C: chains, group levels, tempo, swing, metronome.</summary>
public sealed class GlobalData
{
    public const int Size = 568;
    public byte[] Data { get; }

    public GlobalData(byte[] data)
    {
        if (data.Length != Size) throw new ArgumentException($"Global data must be {Size} bytes");
        Data = data;
    }

    public GlobalData Clone() => new((byte[])Data.Clone());

    public byte DrumLevel { get => Data[512]; set => Data[512] = value; }
    public byte SynthLevel { get => Data[513]; set => Data[513] = value; }
    public byte PunchLevel { get => Data[514]; set => Data[514] = value; }
    public byte MasterLevel { get => Data[515]; set => Data[515] = value; }

    public int Tempo
    {
        get => BinaryPrimitives.ReadUInt16LittleEndian(Data.AsSpan(516));
        set => BinaryPrimitives.WriteUInt16LittleEndian(Data.AsSpan(516), (ushort)Math.Clamp(value, 40, 200));
    }

    public byte Swing { get => Data[561]; set => Data[561] = value; }
    public byte MetronomeLevel { get => Data[562]; set => Data[562] = value; }
    public byte MetronomeSound { get => Data[563]; set => Data[563] = value; }
    public int SelectedChain => (sbyte)Data[564];

    /// <summary>Slots 0..14 are saved chains; slot 15 is the live active-chain buffer.</summary>
    public IReadOnlyList<int> Chain(int slot)
    {
        var chain = new List<int>();
        for (int i = 0; i < 32; i++)
        {
            byte b = Data[slot * 32 + i];
            if (b == 0xFF) break;
            chain.Add(b);
        }
        return chain;
    }
}

/// <summary>The 292-byte MIDI configuration carried by $0F/$10.</summary>
public sealed class MidiConfig
{
    public const int Size = 292;
    public byte[] Data { get; }

    public MidiConfig(byte[] data)
    {
        if (data.Length != Size) throw new ArgumentException($"MIDI config must be {Size} bytes");
        Data = data;
    }

    public MidiConfig Clone() => new((byte[])Data.Clone());

    public bool TrackEnabled(int track) => Data[track] != 0;
    public void SetTrackEnabled(int track, bool on) => Data[track] = (byte)(on ? 1 : 0);
    public int Channel(int track) => Data[16 + track] & 0x0F;
    public void SetChannel(int track, int channel) => Data[16 + track] = (byte)Math.Clamp(channel, 0, 15);
    public int ParameterCc(int track, int parameter) => Data[32 + track * 16 + parameter];
    public void SetParameterCc(int track, int parameter, int cc) => Data[32 + track * 16 + parameter] = (byte)Math.Clamp(cc, 0, 127);

    public bool Flag(MidiFlag flag) => (Data[288] & (1 << (int)flag)) != 0;
    public void SetFlag(MidiFlag flag, bool on) => Data[288] = (byte)(on ? Data[288] | (1 << (int)flag) : Data[288] & ~(1 << (int)flag));
}

public enum MidiFlag
{
    ChannelOneToActive, IncomingMidi, OutgoingMidi, ClockIn, ClockOut, AltProgramChange, MidiEcho, ProgramChange,
}
