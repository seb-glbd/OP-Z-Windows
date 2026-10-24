namespace OPB.Core.Midi;

/// <summary>
/// MIDI over Bluetooth LE (the MMA/Apple BLE-MIDI 1.0 packet format). Every packet starts with a
/// header byte (bit 7 set, timestamp bits 12..7), and every message with a timestamp byte
/// (bit 7 set, timestamp bits 6..0). A SysEx longer than one packet continues in the next packets
/// straight after their header byte; its closing F7 gets its own timestamp byte.
/// </summary>
public static class BleMidiCodec
{
    /// <summary>The BLE-MIDI service and its single MIDI I/O characteristic.</summary>
    public static readonly Guid ServiceUuid = new("03B80E5A-EDE8-4B33-A751-6CE34EC4C700");
    public static readonly Guid CharacteristicUuid = new("7772E5DB-3868-4112-A1A9-F2669D106BF3");

    /// <summary>Bytes in a MIDI message with this status byte (status included).</summary>
    public static int MessageLength(byte status) => status switch
    {
        < 0x80 => 0,
        < 0xC0 => 3,
        < 0xE0 => 2,
        < 0xF0 => 3,
        0xF1 or 0xF3 => 2,
        0xF2 => 3,
        _ => 1,
    };

    static byte Header(int timestamp) => (byte)(0x80 | ((timestamp >> 7) & 0x3F));
    static byte Stamp(int timestamp) => (byte)(0x80 | (timestamp & 0x7F));

    /// <summary>One short message (note, CC, realtime...) as a single packet.</summary>
    public static byte[] EncodeShort(byte status, byte data1, byte data2, int timestamp)
    {
        int length = MessageLength(status);
        var packet = new byte[2 + length];
        packet[0] = Header(timestamp);
        packet[1] = Stamp(timestamp);
        packet[2] = status;
        if (length > 1) packet[3] = (byte)(data1 & 0x7F);
        if (length > 2) packet[4] = (byte)(data2 & 0x7F);
        return packet;
    }

    /// <summary>
    /// A complete SysEx (F0 ... F7) split into packets of at most <paramref name="maxPacket"/> bytes
    /// (the ATT MTU minus 3).
    /// </summary>
    public static List<byte[]> EncodeSysex(byte[] message, int maxPacket, int timestamp)
    {
        if (message.Length < 2 || message[0] != 0xF0 || message[^1] != 0xF7)
            throw new ArgumentException("Not a complete SysEx message", nameof(message));
        maxPacket = Math.Max(maxPacket, 5);
        var packets = new List<byte[]>();
        var packet = new List<byte>(maxPacket) { Header(timestamp), Stamp(timestamp), 0xF0 };
        int last = message.Length - 1;
        for (int i = 1; i < last; i++)
        {
            if (packet.Count == maxPacket)
            {
                packets.Add(packet.ToArray());
                packet.Clear();
                packet.Add(Header(timestamp));
            }
            packet.Add(message[i]);
        }
        if (packet.Count + 2 > maxPacket)
        {
            packets.Add(packet.ToArray());
            packet.Clear();
            packet.Add(Header(timestamp));
        }
        packet.Add(Stamp(timestamp));
        packet.Add(0xF7);
        packets.Add(packet.ToArray());
        return packets;
    }
}

/// <summary>
/// Turns received BLE-MIDI packets back into complete MIDI messages. Keeps the running status
/// and a SysEx in progress across packets. Timestamps are dropped.
/// </summary>
public sealed class BleMidiDecoder
{
    readonly List<byte> _sysex = new();
    bool _inSysex;
    byte _running;

    public event Action<byte[]>? SysexReceived;
    public event Action<byte, byte, byte>? ShortReceived;

    public void Feed(ReadOnlySpan<byte> packet)
    {
        if (packet.Length < 2 || (packet[0] & 0x80) == 0) return; // no header: not BLE-MIDI
        int i = 1;
        while (i < packet.Length)
        {
            byte b = packet[i];
            if (_inSysex)
            {
                if (b < 0x80) { _sysex.Add(b); i++; continue; }
                // A timestamp inside a SysEx: the end, an interleaved realtime byte, or an abort.
                if (i + 1 >= packet.Length)
                {
                    if (b == 0xF7) EndSysex(); // lenient: an F7 without its timestamp
                    return;
                }
                byte next = packet[i + 1];
                if (next == 0xF7) { EndSysex(); i += 2; continue; }
                if (next >= 0xF8) { ShortReceived?.Invoke(next, 0, 0); i += 2; continue; }
                _inSysex = false;
                _sysex.Clear();
            }

            if (b >= 0x80)
            {
                // Timestamp, then a status byte or running-status data.
                if (++i >= packet.Length) return;
                b = packet[i];
            }
            if (b < 0x80)
            {
                if (_running == 0) { i++; continue; }
                i = ReadMessage(packet, i, _running);
                continue;
            }
            if (b == 0xF0)
            {
                _inSysex = true;
                _sysex.Clear();
                _sysex.Add(0xF0);
                i++;
                continue;
            }
            if (b >= 0xF8) { ShortReceived?.Invoke(b, 0, 0); i++; continue; }
            _running = b < 0xF0 ? b : (byte)0;
            i = ReadMessage(packet, i + 1, b);
        }
    }

    /// <summary>Reads the data bytes of a message starting at <paramref name="i"/>; returns the next index.</summary>
    int ReadMessage(ReadOnlySpan<byte> packet, int i, byte status)
    {
        int dataBytes = BleMidiCodec.MessageLength(status) - 1;
        byte d1 = 0, d2 = 0;
        if (dataBytes >= 1) { if (i >= packet.Length || packet[i] >= 0x80) return i; d1 = packet[i++]; }
        if (dataBytes >= 2) { if (i >= packet.Length || packet[i] >= 0x80) return i; d2 = packet[i++]; }
        ShortReceived?.Invoke(status, d1, d2);
        return i;
    }

    void EndSysex()
    {
        _sysex.Add(0xF7);
        _inSysex = false;
        var message = _sysex.ToArray();
        _sysex.Clear();
        SysexReceived?.Invoke(message);
    }
}
