namespace OPB.Core.Midi;

/// <summary>A bidirectional MIDI connection. Receive events are raised on a background thread.</summary>
public interface IMidiConnection : IDisposable
{
    string Name { get; }

    /// <summary>A complete SysEx message, F0 ... F7.</summary>
    event Action<byte[]>? SysexReceived;

    /// <summary>A short message (status, data1, data2), including realtime bytes such as clock.</summary>
    event Action<byte, byte, byte>? ShortReceived;

    void SendSysex(byte[] message);

    void SendShort(byte status, byte data1 = 0, byte data2 = 0);
}
