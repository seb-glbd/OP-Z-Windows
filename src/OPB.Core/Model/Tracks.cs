namespace OPB.Core.Model;

public enum TrackId
{
    Kick, Snare, Perc, Sample, Bass, Lead, Arp, Chord,
    Fx1, Fx2, Tape, Master, Perform, Module, Lights, Motion,
}

public enum TrackKind { Drum, Synth, Fx, Tape, Master, Perform, Module, Lights, Motion }

/// <summary>The 18 per-track sound bytes, in the order used by $0E, pattern storage and parameter locks.</summary>
public enum SoundParam
{
    P1, P2, Attack, Decay, Sustain, Release, Fx1Send, Fx2Send, Filter, Resonance,
    Pan, Level, Portamento, LfoDepth, LfoSpeed, LfoValue, LfoShape, NoteStyle,
}

public static class Tracks
{
    public const int Count = 16;

    public static readonly string[] Names =
    [
        "KICK", "SNARE", "PERC", "SAMPLE", "BASS", "LEAD", "ARP", "CHORD",
        "FX 1", "FX 2", "TAPE", "MASTER", "PERFORM", "MODULE", "LIGHTS", "MOTION",
    ];

    /// <summary>Note slots per step, and their offset inside each step's 55-slot run.</summary>
    public static readonly int[] NotesPerStep = [2, 2, 2, 2, 4, 4, 8, 4, 1, 1, 1, 4, 6, 6, 4, 4];
    public static readonly int[] NoteSlotOffset = [0, 2, 4, 6, 8, 12, 16, 24, 28, 29, 30, 31, 35, 41, 47, 51];

    public static TrackKind Kind(int track) => (TrackId)track switch
    {
        <= TrackId.Sample => TrackKind.Drum,
        <= TrackId.Chord => TrackKind.Synth,
        TrackId.Fx1 or TrackId.Fx2 => TrackKind.Fx,
        TrackId.Tape => TrackKind.Tape,
        TrackId.Master => TrackKind.Master,
        TrackId.Perform => TrackKind.Perform,
        TrackId.Module => TrackKind.Module,
        TrackId.Lights => TrackKind.Lights,
        _ => TrackKind.Motion,
    };

    public static bool HasSound(int track) => Kind(track) is TrackKind.Drum or TrackKind.Synth;
}

/// <summary>Conversions between raw 0..255 bytes and the values the OP-Z shows.</summary>
public static class Display
{
    public static int Percent(byte raw) => (int)Math.Round(raw * 99 / 255.0);

    public static byte FromPercent(int value) => (byte)Math.Clamp((int)Math.Round(value * 255 / 99.0), 0, 255);

    /// <summary>Picks one of <paramref name="options"/> by splitting 0..255 into equal bands.</summary>
    public static string Band(byte raw, IReadOnlyList<string> options) =>
        options[Math.Min(options.Count - 1, raw * options.Count / 256)];

    public static int BandIndex(byte raw, int count) => Math.Min(count - 1, raw * count / 256);

    public static byte BandCenter(int index, int count) => (byte)Math.Clamp((index * 256 + 128) / count, 0, 255);

    public static readonly string[] NoteLengths = ["DRONE", "1", "1/2", "1/4", "1/8", "1/16", "1/32", "1/64"];
    public static readonly string[] DrumNoteStyles = ["RETRIG", "MONO", "GATE", "LOOP"];
    public static readonly string[] SynthNoteStyles = ["POLY", "MONO", "LEGATO"];
    public static readonly string[] LfoDestinations = ["P1", "P2", "FILTER", "RES", "ATTACK", "PITCH", "PAN", "VOLUME"];
    public static readonly string[] LfoShapes = ["SINE", "TRIANGLE", "SQUARE", "SAW", "RANDOM", "GYRO", "SINE", "TRIANGLE", "SQUARE", "SAW", "RANDOM", "ONCE"];
    public static readonly string[] MetronomeSounds = ["CLICK", "SWEDISH", "ENGLISH", "GERMAN", "JAPANESE", "ITALIAN"];
    public static readonly string[] MicFx = ["NO FX", "FX 1", "FX 2", "FX 1+2"];

    public static readonly string[] StepComponents =
    [
        "PULSE", "PARAMETER SPARK", "JUMP", "VELOCITY", "RAMP UP", "RAMP DOWN", "RANDOM", "GLIDE",
        "MULTIPLY", "SWEEP", "PULSE HOLD", "TONALITY", "TRIGGER SPARK", "COMPONENT SPARK", "", "",
    ];

    public static string NoteName(int midiNote)
    {
        string[] names = ["C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B"];
        return $"{names[midiNote % 12]}{midiNote / 12 - 1}";
    }
}
