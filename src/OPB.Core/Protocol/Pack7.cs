namespace OPB.Core.Protocol;

/// <summary>
/// Teenage Engineering 7-bit packing: each group of up to 7 source bytes is preceded
/// by one byte holding their high bits (bit N = high bit of byte N in the group).
/// </summary>
public static class Pack7
{
    public static byte[] Encode(ReadOnlySpan<byte> source)
    {
        var output = new byte[source.Length + (source.Length + 6) / 7];
        int o = 0;
        for (int start = 0; start < source.Length; start += 7)
        {
            int count = Math.Min(7, source.Length - start);
            byte mask = 0;
            for (int i = 0; i < count; i++)
                mask |= (byte)(((source[start + i] >> 7) & 1) << i);
            output[o++] = mask;
            for (int i = 0; i < count; i++)
                output[o++] = (byte)(source[start + i] & 0x7F);
        }
        return output;
    }

    public static byte[] Decode(ReadOnlySpan<byte> source)
    {
        var output = new List<byte>(source.Length);
        for (int start = 0; start < source.Length; start += 8)
        {
            int count = Math.Min(8, source.Length - start);
            byte mask = source[start];
            for (int i = 1; i < count; i++)
                output.Add((byte)(source[start + i] | (((mask >> (i - 1)) & 1) << 7)));
        }
        return output.ToArray();
    }
}
