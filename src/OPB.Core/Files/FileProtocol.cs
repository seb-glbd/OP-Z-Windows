using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using OPB.Core.Protocol;

namespace OPB.Core.Files;

/// <summary>
/// Framing of the OP-Z normal-mode file service ($33/$34/$35/$51/$52/$53/$55). The host plays
/// the "file server"; the OP-Z is the client that answers an advertisement and requests files.
/// </summary>
public static class FileMessages
{
    public const byte Status = 0x33, ClientHello = 0x34, Request = 0x35, Advertise = 0x51, ServerHeartbeat = 0x52, Part = 0x53, Close = 0x55;
    public const int PartSize = 200;

    /// <summary>$33 status values: 1..126 = completed part count in the value, 127 = sync complete, 255 = state CRC.</summary>
    public const byte StatusComplete = 127, StatusStateCrc = 255;

    public static byte[] BuildAdvertise(ushort server) => OpzFrame.Build(Advertise, U16(server));
    public static byte[] BuildClose(ushort server) => OpzFrame.Build(Close, U16(server));
    public static byte[] BuildHeartbeat(ushort server, bool syncActive) => OpzFrame.Build(ServerHeartbeat, [.. U16(server), (byte)(syncActive ? 1 : 0)]);

    public static byte[] BuildRequest(ushort requester, uint fileId, ushort part)
    {
        var p = new byte[8];
        BinaryPrimitives.WriteUInt16LittleEndian(p, requester);
        BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(2), fileId);
        BinaryPrimitives.WriteUInt16LittleEndian(p.AsSpan(6), part);
        return OpzFrame.Build(Request, p);
    }

    /// <summary>One $53 part: up to 200 file bytes, compressed as its own zlib stream.</summary>
    public static byte[] BuildPart(ushort sender, uint fileId, ushort part, bool last, ReadOnlySpan<byte> data)
    {
        if (data.Length > PartSize) throw new ArgumentException($"A file part holds at most {PartSize} bytes");
        var z = Zlib.Compress(data);
        var p = new byte[9 + z.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(p, sender);
        BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(2), fileId);
        BinaryPrimitives.WriteUInt16LittleEndian(p.AsSpan(6), part);
        p[8] = (byte)(last ? 1 : 0);
        z.CopyTo(p, 9);
        return OpzFrame.Build(Part, p);
    }

    public static byte[] BuildStatus(ushort client, byte status, uint value)
    {
        var p = new byte[7];
        BinaryPrimitives.WriteUInt16LittleEndian(p, client);
        p[2] = status;
        BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(3), value);
        return OpzFrame.Build(Status, p);
    }

    public static byte[] BuildClientHello(ushort client, uint stateCrc)
    {
        var p = new byte[6];
        BinaryPrimitives.WriteUInt16LittleEndian(p, client);
        BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(2), stateCrc);
        return OpzFrame.Build(ClientHello, p);
    }

    /// <summary>Number of 200-byte parts a file of <paramref name="size"/> bytes is served in (an empty file is one empty part).</summary>
    public static int PartCount(int size) => Math.Max(1, (size + PartSize - 1) / PartSize);

    static byte[] U16(ushort v) => [(byte)v, (byte)(v >> 8)];
}

public readonly record struct FileClientHello(ushort ClientId, uint StateCrc)
{
    public static bool TryDecode(byte[] p, out FileClientHello hello)
    {
        hello = default;
        if (p.Length < 6) return false;
        hello = new(BinaryPrimitives.ReadUInt16LittleEndian(p), BinaryPrimitives.ReadUInt32LittleEndian(p.AsSpan(2)));
        return true;
    }
}

public readonly record struct FileStatus(ushort ClientId, byte Status, uint Value)
{
    public bool Complete => Status == FileMessages.StatusComplete;
    public bool IsProgress => Status is >= 1 and < FileMessages.StatusComplete;

    public static bool TryDecode(byte[] p, out FileStatus status)
    {
        status = default;
        if (p.Length < 7) return false;
        status = new(BinaryPrimitives.ReadUInt16LittleEndian(p), p[2], BinaryPrimitives.ReadUInt32LittleEndian(p.AsSpan(3)));
        return true;
    }
}

public readonly record struct FileRequest(ushort Requester, uint FileId, ushort Part)
{
    public static bool TryDecode(byte[] p, out FileRequest request)
    {
        request = default;
        if (p.Length < 8) return false;
        request = new(BinaryPrimitives.ReadUInt16LittleEndian(p), BinaryPrimitives.ReadUInt32LittleEndian(p.AsSpan(2)), BinaryPrimitives.ReadUInt16LittleEndian(p.AsSpan(6)));
        return true;
    }
}

public readonly record struct FilePart(ushort Sender, uint FileId, ushort Part, bool Last, byte[] Data)
{
    /// <summary>Decodes a $53 payload and inflates its part (at most 200 bytes).</summary>
    public static bool TryDecode(byte[] p, out FilePart part)
    {
        part = default;
        if (p.Length < 9) return false;
        byte[] data;
        try { data = Zlib.Decompress(p.AsSpan(9), FileMessages.PartSize); }
        catch (Exception) { return false; }
        part = new(BinaryPrimitives.ReadUInt16LittleEndian(p), BinaryPrimitives.ReadUInt32LittleEndian(p.AsSpan(2)),
            BinaryPrimitives.ReadUInt16LittleEndian(p.AsSpan(6)), p[8] != 0, data);
        return true;
    }
}

/// <summary>File ids of the OP-Z file service.</summary>
public static class FileIds
{
    public const uint SlotConfiguration = 0, Plugs = 1, SyncJob = 2;
    public const string SlotConfigurationPath = "settings/slotConfiguration.json";
    public const string PlugsPath = "settings/plugs.json";
    public const string SyncJobPath = "syncjob.json";

    /// <summary>The id a hosted (uploaded) file is announced and served under: crc32 of the lowercase path.</summary>
    public static uint ForPath(string path) => Crc32.Compute(Encoding.UTF8.GetBytes(path.ToLowerInvariant()));

    /// <summary>The id used to request a file from the OP-Z: reserved ids for the settings files, otherwise the path id.</summary>
    public static uint ForDownload(string path) => path.ToLowerInvariant() switch
    {
        "settings/slotconfiguration.json" => SlotConfiguration,
        "settings/plugs.json" => Plugs,
        "syncjob.json" => SyncJob,
        _ => ForPath(path),
    };

    public static string Describe(uint id) => id switch
    {
        SlotConfiguration => SlotConfigurationPath,
        Plugs => PlugsPath,
        SyncJob => SyncJobPath,
        _ => $"file {id}",
    };
}

/// <summary>A file the host serves to the OP-Z during an upload (sync job).</summary>
public sealed record HostedFile(string Path, byte[] Data)
{
    public uint FileId { get; } = FileIds.ForPath(Path);
    public uint Crc { get; } = Crc32.Compute(Data);
}

/// <summary>The syncjob.json manifest (reserved file 2) the OP-Z downloads first during an upload.</summary>
public static class SyncJob
{
    public static byte[] Build(IReadOnlyList<HostedFile> files, IReadOnlyList<uint>? removePlugIds = null)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteStartArray("files");
            foreach (var f in files)
            {
                w.WriteStartObject();
                w.WriteNumber("crc", f.Crc);
                w.WriteNumber("fileId", f.FileId);
                w.WriteString("path", f.Path);
                w.WriteNumber("size", f.Data.Length);
                w.WriteEndObject();
            }
            foreach (var id in removePlugIds ?? [])
            {
                w.WriteStartObject();
                w.WriteString("operation", "remove");
                w.WriteNumber("plugId", id);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        return ms.ToArray();
    }
}

/// <summary>Standard CRC-32 (IEEE 802.3, as zlib.crc32).</summary>
public static class Crc32
{
    static readonly uint[] Table = Enumerable.Range(0, 256).Select(n =>
    {
        uint c = (uint)n;
        for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
        return c;
    }).ToArray();

    public static uint Compute(ReadOnlySpan<byte> data)
    {
        uint c = 0xFFFFFFFFu;
        foreach (var b in data) c = Table[(c ^ b) & 0xFF] ^ (c >> 8);
        return c ^ 0xFFFFFFFFu;
    }
}
