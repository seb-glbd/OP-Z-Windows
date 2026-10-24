using System.IO.Compression;

namespace OPB.Core.Protocol;

/// <summary>One decoded OP-Z manufacturer SysEx frame: F0 00 20 76 01 &lt;id&gt; &lt;packed payload&gt; F7.</summary>
public readonly record struct OpzFrame(byte MessageId, byte[] Payload)
{
    public static readonly byte[] Header = [0xF0, 0x00, 0x20, 0x76, 0x01];
    public static readonly byte[] IdentityInquiry = [0xF0, 0x7E, 0x7F, 0x06, 0x01, 0xF7];

    /// <summary>The heartbeat used by the official app: app session id 01 4E 2E (packed), protocol 6.</summary>
    public static readonly byte[] Heartbeat = [0xF0, 0x00, 0x20, 0x76, 0x01, 0x00, 0x01, 0x4E, 0x2E, 0x06, 0xF7];

    /// <summary>
    /// StateSync hello: the app's session id (big-endian) and protocol version 6. A new id makes
    /// the OP-Z treat the client as a new session and resend its full state burst.
    /// </summary>
    public static byte[] Hello(ushort sessionId) => Build(0x00, [(byte)(sessionId >> 8), (byte)sessionId, 0x06]);

    public static byte[] Build(byte messageId, ReadOnlySpan<byte> payload)
    {
        var packed = Pack7.Encode(payload);
        var frame = new byte[Header.Length + 1 + packed.Length + 1];
        Header.CopyTo(frame, 0);
        frame[Header.Length] = messageId;
        packed.CopyTo(frame, Header.Length + 1);
        frame[^1] = 0xF7;
        return frame;
    }

    public static byte[] Build(byte messageId) => Build(messageId, ReadOnlySpan<byte>.Empty);

    public static bool TryParse(ReadOnlySpan<byte> data, out OpzFrame frame)
    {
        frame = default;
        if (data.Length < 7 || !data[..5].SequenceEqual(Header) || data[^1] != 0xF7)
            return false;
        var body = data[6..^1];
        foreach (var b in body)
            if (b > 0x7F) return false;
        frame = new OpzFrame(data[5], Pack7.Decode(body));
        return true;
    }

    public static bool IsIdentityReply(ReadOnlySpan<byte> data) =>
        data.Length > 9 && data[0] == 0xF0 && data[1] == 0x7E && data[3] == 0x06 && data[4] == 0x02
        && data[5] == 0x00 && data[6] == 0x20 && data[7] == 0x76;
}

public static class Zlib
{
    public static byte[] Compress(ReadOnlySpan<byte> data)
    {
        using var output = new MemoryStream();
        using (var z = new ZLibStream(output, CompressionLevel.Optimal, leaveOpen: true))
            z.Write(data);
        return output.ToArray();
    }

    public static byte[] Decompress(ReadOnlySpan<byte> data, int maxSize = 1 << 20)
    {
        using var input = new MemoryStream(data.ToArray());
        using var z = new ZLibStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        var buffer = new byte[16384];
        int read;
        while ((read = z.Read(buffer, 0, buffer.Length)) > 0)
        {
            output.Write(buffer, 0, read);
            if (output.Length > maxSize)
                throw new InvalidDataException($"Inflated payload exceeds {maxSize} bytes");
        }
        return output.ToArray();
    }
}
