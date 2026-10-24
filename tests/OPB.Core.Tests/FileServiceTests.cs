using System.Text;
using System.Text.Json;
using OPB.Core.Files;
using OPB.Core.Protocol;

namespace OPB.Core.Tests;

/// <summary>
/// A scripted OP-Z file client (firmware 1.2.45 behavior): answers $51 with $34, serves reserved files
/// on $35 once an idle $52 was seen, and on an active $52 pulls the sync job and every changed file.
/// </summary>
sealed class SimulatedOpz : IFileTransport
{
    public const ushort ClientId = 21899;
    public const uint StateCrc = 0x2E0B847D;

    public bool AnswersAdvert = true;
    public bool StreamsDownloads;
    public int DropUploadPartOnce = -1;
    public bool EarlyCompletion;
    public readonly Dictionary<uint, byte[]> Served = new();
    public readonly Dictionary<string, byte[]> Storage = new(StringComparer.OrdinalIgnoreCase);
    public readonly List<OpzFrame> HostFrames = new();
    public readonly List<(uint File, ushort Part)> ResumeRequests = new();
    public int Begins, Ends, PartsReceived;
    public bool Open, Closed, StateSyncPaused;

    bool _idleBeat, _syncRequested, _dropped;
    uint? _incoming;
    int _expected, _completed;
    MemoryStream _buffer = new();
    readonly Queue<(uint Id, string Path, uint Crc)> _wanted = new();
    readonly Dictionary<uint, (string Path, uint Crc, int Size)> _manifest = new();

    public bool IsReady => true;
    public event Action<byte[]>? Received;
    public void BeginSession() { Begins++; StateSyncPaused = true; }
    public void EndSession() { Ends++; StateSyncPaused = false; }

    void Reply(byte[] frame) => Received?.Invoke(frame);

    public void Send(byte[] message)
    {
        Assert.True(OpzFrame.TryParse(message, out var f));
        HostFrames.Add(f);
        var p = f.Payload;
        switch (f.MessageId)
        {
            case FileMessages.Advertise:
                if (AnswersAdvert && !Open) { Open = true; Reply(FileMessages.BuildClientHello(ClientId, StateCrc)); }
                break;
            case FileMessages.ServerHeartbeat:
                if (!Open) break;
                if (p[2] == 0) _idleBeat = true;
                else if (!_syncRequested) { _syncRequested = true; Request(FileIds.SyncJob, 0); }
                break;
            case FileMessages.Request:
                Assert.True(FileRequest.TryDecode(p, out var r));
                if (!Open || !_idleBeat || !Served.TryGetValue(r.FileId, out var file)) break; // fw sends nothing without the idle $52
                int count = FileMessages.PartCount(file.Length);
                for (int part = r.Part; part < count; part++)
                {
                    int start = part * FileMessages.PartSize;
                    Reply(FileMessages.BuildPart(ClientId, r.FileId, (ushort)part, part == count - 1, file.AsSpan(start, Math.Min(FileMessages.PartSize, file.Length - start))));
                    if (!StreamsDownloads) break;
                }
                break;
            case FileMessages.Part:
                Assert.True(FilePart.TryDecode(p, out var fp));
                ReceivePart(fp);
                break;
            case FileMessages.Close:
                Open = false;
                Closed = true;
                break;
        }
    }

    void Request(uint id, int part)
    {
        _incoming = id;
        _expected = part;
        if (part == 0) _buffer = new MemoryStream();
        Reply(FileMessages.BuildRequest(ClientId, id, (ushort)part));
    }

    void ReceivePart(FilePart fp)
    {
        if (fp.FileId != _incoming) return;
        if (fp.Part != _expected)
        {
            if (fp.Part > _expected && !ResumeRequests.Contains((fp.FileId, (ushort)_expected)))
            {
                ResumeRequests.Add((fp.FileId, (ushort)_expected));
                Reply(FileMessages.BuildStatus(ClientId, 1, (uint)_completed));
                Reply(FileMessages.BuildRequest(ClientId, fp.FileId, (ushort)_expected));
            }
            return;
        }
        if (fp.Part == DropUploadPartOnce && fp.FileId != FileIds.SyncJob && !_dropped) { _dropped = true; return; }
        PartsReceived++;
        _buffer.Write(fp.Data);
        _expected++;
        if (!fp.Last)
        {
            _completed++;
            Reply(FileMessages.BuildStatus(ClientId, 1, (uint)_completed));
            if (EarlyCompletion && _incoming != FileIds.SyncJob && _wanted.Count == 0 && _expected == FileMessages.PartCount(_manifest[fp.FileId].Size) - 1)
                Reply(FileMessages.BuildStatus(ClientId, FileMessages.StatusComplete, 0));
            return;
        }
        var data = _buffer.ToArray();
        if (fp.FileId == FileIds.SyncJob)
        {
            using var doc = JsonDocument.Parse(data);
            foreach (var e in doc.RootElement.GetProperty("files").EnumerateArray())
            {
                if (!e.TryGetProperty("path", out var path)) continue;
                uint id = e.GetProperty("fileId").GetUInt32(), crc = e.GetProperty("crc").GetUInt32();
                Assert.Equal(FileIds.ForPath(path.GetString()!), id);
                _manifest[id] = (path.GetString()!, crc, e.GetProperty("size").GetInt32());
                if (!Storage.TryGetValue(path.GetString()!, out var current) || Crc32.Compute(current) != crc)
                    _wanted.Enqueue((id, path.GetString()!, crc));
            }
        }
        else
        {
            var (path, crc, _) = _manifest[fp.FileId];
            Assert.Equal(crc, Crc32.Compute(data));
            Storage[path] = data;
        }
        if (_wanted.Count > 0) Request(_wanted.Dequeue().Id, 0);
        else
        {
            _incoming = null;
            Reply(FileMessages.BuildStatus(ClientId, FileMessages.StatusComplete, 0));
        }
    }
}

public class FileProtocolTests
{
    [Fact]
    public void FileIds()
    {
        Assert.Equal(2_873_598_156u, Files.FileIds.ForPath("settings/slotConfiguration.json"));
        Assert.Equal(Files.FileIds.ForPath("SETTINGS/SLOTCONFIGURATION.JSON"), Files.FileIds.ForPath("settings/slotconfiguration.json"));
        Assert.Equal(0u, Files.FileIds.ForDownload("settings/slotConfiguration.json"));
        Assert.Equal(1u, Files.FileIds.ForDownload("settings/plugs.json"));
        Assert.Equal(2u, Files.FileIds.ForDownload("syncjob.json"));
        Assert.Equal(Files.FileIds.ForPath("projects/project01.opz"), Files.FileIds.ForDownload("projects/project01.opz"));
        Assert.Equal(0xCBF43926u, Crc32.Compute("123456789"u8));
    }

    [Fact]
    public void MessageFraming()
    {
        Assert.Equal(Fixtures.Hex("F0 00 20 76 01 51 00 42 4F F7"), FileMessages.BuildAdvertise(0x4F42));
        Assert.Equal(Fixtures.Hex("F0 00 20 76 01 55 00 42 42 F7"), FileMessages.BuildClose(0x4242));
        Assert.Equal(Fixtures.Hex("F0 00 20 76 01 52 00 42 42 01 F7"), FileMessages.BuildHeartbeat(0x4242, true));

        Assert.True(OpzFrame.TryParse(FileMessages.BuildRequest(0x4242, 2_873_598_156, 3), out var req));
        Assert.Equal(FileMessages.Request, req.MessageId);
        Assert.Equal(Fixtures.Hex("42 42 CC A0 47 AB 03 00"), req.Payload);
        Assert.True(FileRequest.TryDecode(req.Payload, out var r));
        Assert.Equal(new FileRequest(0x4242, 2_873_598_156, 3), r);

        var data = Enumerable.Range(0, 200).Select(i => (byte)i).ToArray();
        Assert.True(OpzFrame.TryParse(FileMessages.BuildPart(21899, 1, 49, true, data), out var part));
        Assert.Equal(FileMessages.Part, part.MessageId);
        Assert.True(FilePart.TryDecode(part.Payload, out var fp));
        Assert.Equal((ushort)21899, fp.Sender);
        Assert.Equal(1u, fp.FileId);
        Assert.Equal((ushort)49, fp.Part);
        Assert.True(fp.Last);
        Assert.Equal(data, fp.Data);
        Assert.Throws<ArgumentException>(() => FileMessages.BuildPart(1, 1, 0, true, new byte[201]));

        Assert.True(OpzFrame.TryParse(FileMessages.BuildStatus(21899, 127, 0), out var st));
        Assert.True(FileStatus.TryDecode(st.Payload, out var status));
        Assert.True(status.Complete);
        Assert.True(OpzFrame.TryParse(FileMessages.BuildClientHello(21899, 0x2E0B847D), out var hello));
        Assert.True(FileClientHello.TryDecode(hello.Payload, out var h));
        Assert.Equal(new FileClientHello(21899, 0x2E0B847D), h);

        Assert.Equal(1, FileMessages.PartCount(0));
        Assert.Equal(1, FileMessages.PartCount(200));
        Assert.Equal(2, FileMessages.PartCount(201));
        Assert.Equal(30, FileMessages.PartCount(5891));
    }

    [Fact]
    public void RejectsOversizedParts()
    {
        var p = new byte[9 + 0];
        var z = Zlib.Compress(new byte[201]);
        Assert.False(FilePart.TryDecode([.. p, .. z], out _));
        Assert.False(FilePart.TryDecode([1, 2, 3], out _));
    }

    [Fact]
    public void SyncJobManifest()
    {
        var file = new HostedFile("samplepacks/test.aif", "synthetic"u8.ToArray());
        var json = SyncJob.Build([file], [1234]);
        using var doc = JsonDocument.Parse(json);
        var files = doc.RootElement.GetProperty("files");
        Assert.Equal("samplepacks/test.aif", files[0].GetProperty("path").GetString());
        Assert.Equal(file.FileId, files[0].GetProperty("fileId").GetUInt32());
        Assert.Equal(Crc32.Compute("synthetic"u8), files[0].GetProperty("crc").GetUInt32());
        Assert.Equal(9, files[0].GetProperty("size").GetInt32());
        Assert.Equal("remove", files[1].GetProperty("operation").GetString());
        Assert.Equal(1234, files[1].GetProperty("plugId").GetInt32());
        Assert.StartsWith("{\"files\":[{\"crc\":", Encoding.UTF8.GetString(json));
    }

    const string SlotJson = """
        {"version":1,"tracks":[{"index":0,"packs":[{"id":150,"slot":1},{"id":151,"slot":3}]},{"index":4,"packs":[{"id":10,"slot":1,"extra":"x"}]}]}
        """;

    [Fact]
    public void SlotConfigurationEdits()
    {
        var config = SlotConfiguration.Parse(Encoding.UTF8.GetBytes(SlotJson));
        Assert.Equal([0, 4], config.Tracks);
        Assert.Equal(150u, config.Plug(0, 1));
        Assert.Null(config.Plug(0, 2));
        Assert.True(config.HasTrack(4));
        Assert.False(config.HasTrack(7));

        var edited = config.Clone();
        Assert.Null(edited.Assign(0, 2, 60001));
        Assert.Equal(150u, edited.Assign(0, 1, 152));
        Assert.Throws<InvalidOperationException>(() => edited.Assign(7, 1, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => edited.Assign(0, 11, 1));
        Assert.Equal(new Dictionary<int, uint> { [1] = 152, [2] = 60001, [3] = 151 }, edited.Slots(0));
        Assert.Equal(150u, config.Plug(0, 1)); // clone is independent
        Assert.False(edited.SameAssignments(config));

        var round = SlotConfiguration.Parse(edited.ToBytes());
        Assert.True(round.SameAssignments(edited));
        using var doc = JsonDocument.Parse(edited.ToBytes());
        Assert.Equal(1, doc.RootElement.GetProperty("version").GetInt32());
        Assert.Equal("x", doc.RootElement.GetProperty("tracks")[1].GetProperty("packs")[0].GetProperty("extra").GetString());
        var slots = doc.RootElement.GetProperty("tracks")[0].GetProperty("packs").EnumerateArray().Select(p => p.GetProperty("slot").GetInt32());
        Assert.Equal([1, 2, 3], slots);
    }

    [Fact]
    public void PlugCatalogShapes()
    {
        var flat = PlugCatalog.Parse("""{"plugs":[{"id":1,"name":"bow","type":"synth"},{"id":150,"name":"z-kicks","type":"kick"},{"id":300,"name":"delay","category":"fx"},{"id":7,"name":"mystery"}]}"""u8);
        var byId = flat.ToDictionary(p => p.Id);
        Assert.Equal(4, byId.Count);
        Assert.True(byId[1].Fits(5));
        Assert.False(byId[1].Fits(0));
        Assert.True(byId[150].Fits(0));
        Assert.False(byId[150].Fits(1));
        Assert.True(byId[300].Fits(9));
        Assert.Null(byId[7].Fits(0));

        var nested = PlugCatalog.Parse("""{"tracks":[{"index":1,"plugs":[{"id":20,"name":"snare pack"}]}],"drum":[{"id":21,"name":"kit"}]}"""u8);
        var n = nested.ToDictionary(p => p.Id);
        Assert.Equal([1], n[20].Tracks);
        Assert.True(n[21].Fits(3));
        Assert.False(n[21].Fits(4));
    }
}

public class FileClientTests
{
    static readonly FileClientOptions Fast = new()
    {
        HelloTimeout = TimeSpan.FromMilliseconds(400),
        HeartbeatInterval = TimeSpan.FromMilliseconds(100),
        PartTimeout = TimeSpan.FromMilliseconds(250),
        IdleTimeout = TimeSpan.FromMilliseconds(800),
        CompletionGrace = TimeSpan.FromMilliseconds(200),
        PartInterval = TimeSpan.FromMilliseconds(1),
        CloseDelay = TimeSpan.Zero,
    };

    static byte[] Text(int length) => Encoding.ASCII.GetBytes(string.Concat(Enumerable.Range(0, length).Select(i => (char)('a' + i % 26))));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DownloadsBothSettingsFiles(bool streaming)
    {
        var device = new SimulatedOpz { StreamsDownloads = streaming };
        var slots = Text(5891);
        var plugs = Text(9950);
        device.Served[Files.FileIds.SlotConfiguration] = slots;
        device.Served[Files.FileIds.Plugs] = plugs;
        var client = new OpzFileClient(device, Fast);

        var result = await client.DownloadAsync(Files.FileIds.SlotConfiguration, Files.FileIds.Plugs);

        Assert.True(result.Ok, result.Message);
        Assert.Equal(slots, result.Value![0]);
        Assert.Equal(plugs, result.Value[1]);
        Assert.Equal(SimulatedOpz.StateCrc, client.LastStateCrc);
        Assert.True(device.Closed);
        Assert.Equal(1, device.Begins);
        Assert.Equal(1, device.Ends);
        Assert.False(device.StateSyncPaused);

        // $51, then $34 is answered with an idle $52 before the first $35.
        var ids = device.HostFrames.Select(f => f.MessageId).ToList();
        Assert.Equal(FileMessages.Advertise, ids[0]);
        int firstRequest = ids.IndexOf(FileMessages.Request);
        int idleBeat = device.HostFrames.FindIndex(f => f.MessageId == FileMessages.ServerHeartbeat && f.Payload[2] == 0);
        Assert.InRange(idleBeat, 1, firstRequest - 1);
        Assert.Equal(FileMessages.Close, ids[^1]);
        Assert.Equal(30 + 50, device.HostFrames.Count(f => f.MessageId == FileMessages.Request));
    }

    [Fact]
    public async Task OldFirmwareIsReportedAsUnavailable()
    {
        var device = new SimulatedOpz { AnswersAdvert = false };
        var client = new OpzFileClient(device, Fast);
        var result = await client.DownloadAsync(Files.FileIds.SlotConfiguration);
        Assert.False(result.Ok);
        Assert.Equal(FileServiceError.NotAvailable, result.Error);
        Assert.Contains("1.2.45", result.Message);
        Assert.Equal(1, device.Begins);
        Assert.Equal(1, device.Ends);
        Assert.False(device.StateSyncPaused);
        Assert.All(device.HostFrames, f => Assert.Equal(FileMessages.Advertise, f.MessageId));
        Assert.InRange(device.HostFrames.Count, 2, 6);
    }

    [Fact]
    public async Task UnservedFileFails()
    {
        var device = new SimulatedOpz();
        var client = new OpzFileClient(device, Fast);
        var result = await client.DownloadAsync(Files.FileIds.ForPath("engines/saw013a.engine"));
        Assert.Equal(FileServiceError.NotServed, result.Error);
        Assert.True(device.Closed);
        Assert.Equal(1, device.Ends);
    }

    [Fact]
    public async Task NotConnectedFailsWithoutTouchingTheDevice()
    {
        var transport = new OfflineTransport();
        var result = await new OpzFileClient(transport, Fast).DownloadAsync(0);
        Assert.Equal(FileServiceError.NotConnected, result.Error);
        Assert.Equal(0, transport.Begins);
    }

    [Fact]
    public async Task UploadsThroughSyncJob()
    {
        var device = new SimulatedOpz();
        device.Storage[Files.FileIds.SlotConfigurationPath] = Text(5891);
        var edited = Text(5827);
        edited[100] = (byte)'#';
        var client = new OpzFileClient(device, Fast);
        var progress = new List<FileProgress>();

        var result = await client.UploadAsync([new HostedFile(Files.FileIds.SlotConfigurationPath, edited)], progress: new SyncProgress(progress));

        Assert.True(result.Ok, result.Message);
        Assert.Equal(edited, device.Storage[Files.FileIds.SlotConfigurationPath]);
        Assert.Empty(result.Value!.SkippedFileIds);
        Assert.Equal(1 + 30, result.Value.PartsSent);
        Assert.Equal(31, progress[^1].Parts);
        Assert.Equal(31, progress[^1].TotalParts);
        Assert.True(device.Closed);
        Assert.False(device.StateSyncPaused);
        Assert.Contains(device.HostFrames, f => f.MessageId == FileMessages.ServerHeartbeat && f.Payload[2] == 1);
    }

    [Fact]
    public async Task UploadResumesFromRequestedPart()
    {
        var device = new SimulatedOpz { DropUploadPartOnce = 3 };
        var edited = Text(1999);
        var client = new OpzFileClient(device, Fast);

        var result = await client.UploadAsync([new HostedFile(Files.FileIds.SlotConfigurationPath, edited)]);

        Assert.True(result.Ok, result.Message);
        Assert.Equal(edited, device.Storage[Files.FileIds.SlotConfigurationPath]);
        Assert.Single(device.ResumeRequests);
        Assert.Equal((ushort)3, device.ResumeRequests[0].Part);
        Assert.True(result.Value!.PartsSent > 1 + 10);
    }

    [Fact]
    public async Task UploadKeepsServingAfterEarlyCompletion()
    {
        var device = new SimulatedOpz { EarlyCompletion = true };
        var edited = Text(1000);
        var result = await new OpzFileClient(device, Fast).UploadAsync([new HostedFile(Files.FileIds.SlotConfigurationPath, edited)]);
        Assert.True(result.Ok, result.Message);
        Assert.Equal(edited, device.Storage[Files.FileIds.SlotConfigurationPath]);
        Assert.Empty(result.Value!.SkippedFileIds);
    }

    [Fact]
    public async Task UnchangedFileIsSkipped()
    {
        var device = new SimulatedOpz();
        var same = Text(700);
        device.Storage[Files.FileIds.SlotConfigurationPath] = same;
        var result = await new OpzFileClient(device, Fast).UploadAsync([new HostedFile(Files.FileIds.SlotConfigurationPath, same)]);
        Assert.True(result.Ok, result.Message);
        Assert.Equal([Files.FileIds.ForPath(Files.FileIds.SlotConfigurationPath)], result.Value!.SkippedFileIds);
        Assert.Equal(1, result.Value.PartsSent);
    }

    [Fact]
    public async Task UploadRejectsEmptyFiles()
    {
        var result = await new OpzFileClient(new SimulatedOpz(), Fast).UploadAsync([new HostedFile("x.json", [])]);
        Assert.Equal(FileServiceError.Protocol, result.Error);
    }

    [Fact]
    public async Task UploadTimesOutWhenSyncJobIsNeverRequested()
    {
        var device = new SilentAfterHello();
        var result = await new OpzFileClient(device, Fast).UploadAsync([new HostedFile("a.json", [1, 2, 3])]);
        Assert.Equal(FileServiceError.Timeout, result.Error);
        Assert.Equal(1, device.Ends);
    }

    sealed class SyncProgress(List<FileProgress> list) : IProgress<FileProgress>
    {
        public void Report(FileProgress value) { lock (list) list.Add(value); }
    }

    sealed class OfflineTransport : IFileTransport
    {
        public int Begins;
        public bool IsReady => false;
        public void Send(byte[] message) => throw new InvalidOperationException();
        public event Action<byte[]>? Received { add { } remove { } }
        public void BeginSession() => Begins++;
        public void EndSession() { }
    }

    sealed class SilentAfterHello : IFileTransport
    {
        public int Ends;
        public bool IsReady => true;
        public event Action<byte[]>? Received;
        public void Send(byte[] message)
        {
            if (OpzFrame.TryParse(message, out var f) && f.MessageId == FileMessages.Advertise)
                Received?.Invoke(FileMessages.BuildClientHello(1, 2));
        }
        public void BeginSession() { }
        public void EndSession() => Ends++;
    }
}
