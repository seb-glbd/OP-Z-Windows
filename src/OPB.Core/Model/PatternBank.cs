using System.Buffers.Binary;

namespace OPB.Core.Model;

/// <summary>
/// The 16-pattern bank of the active project, as transferred by $09/$0A (342,272 bytes).
/// Edits are made in place; unrelated bytes are always preserved.
/// </summary>
public sealed class PatternBank
{
    public const int PatternSize = 21_392;
    public const int PatternCount = 16;
    public const int Size = PatternSize * PatternCount;

    const int TrackRecordsOffset = 0;
    const int NotesOffset = 192;
    const int StepsOffset = 7_232;
    const int SoundOffset = 21_056;
    const int MuteGroupsOffset = 21_344;
    const int TapeRoutingOffset = 21_384;
    const int MasterRoutingOffset = 21_386;
    const int ActiveMuteGroupOffset = 21_388;
    public const int StepRecordSize = 54;
    public const int DurationPerStep = 6_200;

    public byte[] Data { get; }

    public PatternBank(byte[] data)
    {
        if (data.Length != Size) throw new ArgumentException($"Pattern bank must be {Size} bytes");
        Data = data;
    }

    public PatternBank Clone() => new((byte[])Data.Clone());

    static int Base(int pattern) => pattern * PatternSize;

    // ---- Track records (12 bytes) ----
    int TrackRecord(int pattern, int track) => Base(pattern) + TrackRecordsOffset + track * 12;

    public uint Plug(int pattern, int track) => BinaryPrimitives.ReadUInt32LittleEndian(Data.AsSpan(TrackRecord(pattern, track)));
    public int StepCount(int pattern, int track) => Math.Clamp((int)Data[TrackRecord(pattern, track) + 4], 1, 16);
    public int StepLength(int pattern, int track) => Math.Max(1, (int)Data[TrackRecord(pattern, track) + 6]);
    public byte Quantize(int pattern, int track) => Data[TrackRecord(pattern, track) + 7];
    public byte NoteLength(int pattern, int track) => Data[TrackRecord(pattern, track) + 9];

    public void SetStepCount(int pattern, int track, int count) => Data[TrackRecord(pattern, track) + 4] = (byte)Math.Clamp(count, 1, 16);
    public void SetStepLength(int pattern, int track, int length) => Data[TrackRecord(pattern, track) + 6] = (byte)Math.Clamp(length, 1, 16);
    public void SetQuantize(int pattern, int track, byte value) => Data[TrackRecord(pattern, track) + 7] = value;
    public void SetNoteLength(int pattern, int track, byte value) => Data[TrackRecord(pattern, track) + 9] = value;

    // ---- Sound parameters ----
    public byte Sound(int pattern, int track, SoundParam p) => Data[Base(pattern) + SoundOffset + track * 18 + (int)p];
    public void SetSound(int pattern, int track, SoundParam p, byte value) => Data[Base(pattern) + SoundOffset + track * 18 + (int)p] = value;

    public byte[] SoundRecord(int pattern, int track) => Data.AsSpan(Base(pattern) + SoundOffset + track * 18, 18).ToArray();
    public void SetSoundRecord(int pattern, int track, ReadOnlySpan<byte> record) => record[..18].CopyTo(Data.AsSpan(Base(pattern) + SoundOffset + track * 18));

    // ---- Notes ----
    public int NoteOffset(int pattern, int track, int step, int slot) =>
        Base(pattern) + NotesOffset + (step * 55 + Tracks.NoteSlotOffset[track] + slot) * 8;

    public IReadOnlyList<Note> Notes(int pattern, int track, int step)
    {
        var notes = new List<Note>();
        for (int slot = 0; slot < Tracks.NotesPerStep[track]; slot++)
        {
            int o = NoteOffset(pattern, track, step, slot);
            if (Data[o + 4] == 0xFF) continue;
            notes.Add(new Note(BinaryPrimitives.ReadInt32LittleEndian(Data.AsSpan(o)), Data[o + 4], Data[o + 5], (sbyte)Data[o + 6], Data[o + 7]));
        }
        return notes;
    }

    public bool HasTrig(int pattern, int track, int step)
    {
        for (int slot = 0; slot < Tracks.NotesPerStep[track]; slot++)
            if (Data[NoteOffset(pattern, track, step, slot) + 4] != 0xFF) return true;
        return false;
    }

    public void SetNotes(int pattern, int track, int step, IReadOnlyList<Note> notes)
    {
        int slots = Tracks.NotesPerStep[track];
        if (notes.Count > slots) throw new ArgumentException($"{Tracks.Names[track]} holds {slots} notes per step");
        for (int slot = 0; slot < slots; slot++)
        {
            int o = NoteOffset(pattern, track, step, slot);
            if (slot < notes.Count)
            {
                var n = notes[slot];
                BinaryPrimitives.WriteInt32LittleEndian(Data.AsSpan(o), n.Duration);
                Data[o + 4] = n.Pitch;
                Data[o + 5] = n.Velocity;
                Data[o + 6] = (byte)n.MicroTiming;
                Data[o + 7] = n.Age;
            }
            else
            {
                Data[o + 4] = 0xFF;
            }
        }
    }

    // ---- Step records: components and parameter locks ----
    public int StepOffset(int pattern, int track, int step) => Base(pattern) + StepsOffset + (track * 16 + step) * StepRecordSize;

    public ushort ComponentMask(int pattern, int track, int step) => BinaryPrimitives.ReadUInt16LittleEndian(Data.AsSpan(StepOffset(pattern, track, step)));
    public byte ComponentValue(int pattern, int track, int step, int component) => Data[StepOffset(pattern, track, step) + 2 + component];

    public void SetComponent(int pattern, int track, int step, int component, byte? value)
    {
        int o = StepOffset(pattern, track, step);
        ushort mask = BinaryPrimitives.ReadUInt16LittleEndian(Data.AsSpan(o));
        if (value is null) mask &= (ushort)~(1 << component);
        else
        {
            mask |= (ushort)(1 << component);
            Data[o + 2 + component] = value.Value;
        }
        BinaryPrimitives.WriteUInt16LittleEndian(Data.AsSpan(o), mask);
    }

    public bool IsLocked(int pattern, int track, int step, SoundParam p) => Data[StepOffset(pattern, track, step) + 36 + (int)p] != 0;
    public byte LockValue(int pattern, int track, int step, SoundParam p) => Data[StepOffset(pattern, track, step) + 18 + (int)p];

    public bool HasAnyLock(int pattern, int track, int step)
    {
        int o = StepOffset(pattern, track, step) + 36;
        for (int i = 0; i < 18; i++) if (Data[o + i] != 0) return true;
        return false;
    }

    // ---- Mutes and routing ----
    public int ActiveMuteGroup(int pattern) => Math.Min((int)Data[Base(pattern) + ActiveMuteGroupOffset], 9);

    public bool IsMuted(int pattern, int track)
    {
        int o = Base(pattern) + MuteGroupsOffset + ActiveMuteGroup(pattern) * 4 + track / 4;
        return (Data[o] & (1 << ((track % 4) * 2 + 1))) != 0;
    }

    public ushort TapeRouting(int pattern) => BinaryPrimitives.ReadUInt16LittleEndian(Data.AsSpan(Base(pattern) + TapeRoutingOffset));
    public ushort MasterRouting(int pattern) => BinaryPrimitives.ReadUInt16LittleEndian(Data.AsSpan(Base(pattern) + MasterRoutingOffset));

    public bool IsEmpty(int pattern)
    {
        for (int track = 0; track < Tracks.Count; track++)
            for (int step = 0; step < 16; step++)
                if (HasTrig(pattern, track, step)) return false;
        return true;
    }
}

public readonly record struct Note(int Duration, byte Pitch, byte Velocity, sbyte MicroTiming, byte Age = 0)
{
    public double Steps => Duration / (double)PatternBank.DurationPerStep;
}
