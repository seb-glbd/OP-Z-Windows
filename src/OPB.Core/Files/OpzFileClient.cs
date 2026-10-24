using System.Collections.Concurrent;
using System.Diagnostics;
using OPB.Core.Protocol;

namespace OPB.Core.Files;

/// <summary>The link a file session runs over: sending frames, receiving frames, and borrowing the device.</summary>
public interface IFileTransport
{
    /// <summary>True when frames can be sent (a device is connected).</summary>
    bool IsReady { get; }
    void Send(byte[] message);
    /// <summary>Every OP-Z manufacturer frame received (may be raised on any thread).</summary>
    event Action<byte[]>? Received;
    /// <summary>Called before a file session (pauses StateSync heartbeats).</summary>
    void BeginSession();
    /// <summary>Always called after a file session, even on failure (resumes StateSync).</summary>
    void EndSession();
}

/// <summary>Runs file sessions over an <see cref="OpzSession"/>'s MIDI connection, pausing its StateSync while they last.</summary>
public sealed class SessionFileTransport(OpzSession session) : IFileTransport
{
    public bool IsReady => session.IsConnected && session.Connection is not null;
    public void Send(byte[] message) => (session.Connection ?? throw new InvalidOperationException("OP-Z is not connected")).SendSysex(message);

    public event Action<byte[]>? Received
    {
        add => session.RawSysex += value;
        remove => session.RawSysex -= value;
    }

    public void BeginSession() => session.PauseStateSync(true);
    public void EndSession() => session.PauseStateSync(false);
}

public enum FileServiceError { None, NotConnected, NotAvailable, NotServed, Timeout, Protocol, Busy, Cancelled }

public sealed record FileServiceResult<T>(T? Value, FileServiceError Error, string Message)
{
    public bool Ok => Error == FileServiceError.None;
    public static FileServiceResult<T> Success(T value, string message = "") => new(value, FileServiceError.None, message);
    public static FileServiceResult<T> Fail(FileServiceError error, string message) => new(default, error, message);
}

/// <summary>Outcome of an upload: which hosted files the OP-Z skipped (its copy already matched) and how many parts were served.</summary>
public sealed record UploadReport(IReadOnlyList<uint> SkippedFileIds, int PartsSent, uint StateCrc);

public readonly record struct FileProgress(string Phase, int Parts, int TotalParts);

public sealed class FileClientOptions
{
    public ushort ServerId { get; init; } = 0x4F42;
    /// <summary>
    /// No $34 within this time means the OP-Z doesn't offer the file service (firmware older than 1.2.45).
    /// After a previous session the firmware takes about 6.3 s to answer again, even after $55, so this
    /// must comfortably exceed that.
    /// </summary>
    public TimeSpan HelloTimeout { get; init; } = TimeSpan.FromSeconds(12);
    public TimeSpan HeartbeatInterval { get; init; } = TimeSpan.FromMilliseconds(900);
    public TimeSpan PartTimeout { get; init; } = TimeSpan.FromSeconds(2);
    public int PartRetries { get; init; } = 2;
    /// <summary>Upload: give up when the OP-Z neither requests nor reports anything for this long.</summary>
    public TimeSpan IdleTimeout { get; init; } = TimeSpan.FromSeconds(6);
    /// <summary>Upload: after completion status 127 with files still unrequested, wait this long before treating them as skipped.</summary>
    public TimeSpan CompletionGrace { get; init; } = TimeSpan.FromMilliseconds(1500);
    public TimeSpan PartInterval { get; init; } = TimeSpan.FromMilliseconds(10);
    public int MaxInFlightParts { get; init; } = 64;
    /// <summary>Pause after closing so the firmware can tear the session down before StateSync resumes.</summary>
    public TimeSpan CloseDelay { get; init; } = TimeSpan.FromMilliseconds(150);
}

/// <summary>
/// Host side ("file server") of the OP-Z normal-mode file service: advertises with $51, waits
/// for the OP-Z's $34, keeps the session alive with $52, downloads files part by part with $35/$53,
/// and uploads through the syncjob.json manifest flow. Every session pauses StateSync and always
/// resumes it, sends $55 on exit, and fails cleanly when the firmware doesn't answer (1.2.17 doesn't;
/// 1.2.45 and later do).
/// </summary>
public sealed class OpzFileClient
{
    static readonly SemaphoreSlim Gate = new(1, 1);
    readonly IFileTransport _transport;
    readonly FileClientOptions _options;

    public OpzFileClient(IFileTransport transport, FileClientOptions? options = null)
    {
        _transport = transport;
        _options = options ?? new FileClientOptions();
    }

    public OpzFileClient(OpzSession session) : this(new SessionFileTransport(session)) { }

    /// <summary>The client-state CRC the OP-Z reported in its last $34.</summary>
    public uint? LastStateCrc { get; private set; }

    /// <summary>Diagnostics (raised on a background thread).</summary>
    public event Action<string>? Log;

    /// <summary>Downloads files by id, in order, within one session.</summary>
    public Task<FileServiceResult<IReadOnlyList<byte[]>>> DownloadAsync(params uint[] fileIds) => DownloadAsync(fileIds, null, default);

    public Task<FileServiceResult<IReadOnlyList<byte[]>>> DownloadAsync(IReadOnlyList<uint> fileIds, IProgress<FileProgress>? progress, CancellationToken cancel = default) =>
        RunAsync(session => DownloadFilesAsync(session, fileIds, progress, cancel), cancel);

    /// <summary>Uploads files through a sync job. The OP-Z requests the manifest, then every file whose CRC differs from its copy.</summary>
    public Task<FileServiceResult<UploadReport>> UploadAsync(IReadOnlyList<HostedFile> files, IReadOnlyList<uint>? removePlugIds = null, IProgress<FileProgress>? progress = null, CancellationToken cancel = default)
    {
        foreach (var f in files)
        {
            if (f.Data.Length == 0) return Task.FromResult(FileServiceResult<UploadReport>.Fail(FileServiceError.Protocol, $"Empty files can't be uploaded ({f.Path})"));
            if (FileMessages.PartCount(f.Data.Length) > ushort.MaxValue) return Task.FromResult(FileServiceResult<UploadReport>.Fail(FileServiceError.Protocol, $"{f.Path} is too large"));
        }
        if (files.Select(f => f.FileId).Distinct().Count() != files.Count || files.Any(f => f.FileId == FileIds.SyncJob))
            return Task.FromResult(FileServiceResult<UploadReport>.Fail(FileServiceError.Protocol, "File id collision"));
        return RunAsync(session => UploadFilesAsync(session, files, removePlugIds ?? [], progress, cancel), cancel);
    }

    /// <summary>
    /// Uploads files, then reads <paramref name="readBackIds"/> in the same session. Each new session
    /// costs ~6 s of waiting for the OP-Z, so a write plus its verification share one.
    /// </summary>
    public async Task<(FileServiceResult<UploadReport> Upload, FileServiceResult<IReadOnlyList<byte[]>>? ReadBack)> UploadAndReadBackAsync(
        IReadOnlyList<HostedFile> files, IReadOnlyList<uint> readBackIds, IProgress<FileProgress>? progress = null, CancellationToken cancel = default)
    {
        FileServiceResult<IReadOnlyList<byte[]>>? readBack = null;
        var upload = await RunAsync(async session =>
        {
            var result = await UploadFilesAsync(session, files, [], progress, cancel).ConfigureAwait(false);
            if (!result.Ok) return result;
            session.BeatIfDue(false, force: true); // back to idle before requesting files
            readBack = await DownloadFilesAsync(session, readBackIds, progress, cancel).ConfigureAwait(false);
            return result;
        }, cancel).ConfigureAwait(false);
        return (upload, readBack);
    }

    async Task<FileServiceResult<T>> RunAsync<T>(Func<Session, Task<FileServiceResult<T>>> body, CancellationToken cancel)
    {
        if (!_transport.IsReady) return FileServiceResult<T>.Fail(FileServiceError.NotConnected, "OP-Z is not connected");
        if (!await Gate.WaitAsync(0, CancellationToken.None).ConfigureAwait(false))
            return FileServiceResult<T>.Fail(FileServiceError.Busy, "Another file transfer is running");
        var session = new Session(_transport, _options, s => Log?.Invoke(s));
        try
        {
            _transport.BeginSession();
            session.Attach();
            var hello = await session.OpenAsync(cancel).ConfigureAwait(false);
            if (hello is null)
                return FileServiceResult<T>.Fail(FileServiceError.NotAvailable,
                    $"The OP-Z did not answer the file request within {_options.HelloTimeout.TotalSeconds:0} seconds");
            LastStateCrc = hello.Value.StateCrc;
            return await body(session).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return FileServiceResult<T>.Fail(FileServiceError.Cancelled, "Transfer cancelled");
        }
        catch (Exception ex)
        {
            return FileServiceResult<T>.Fail(FileServiceError.Protocol, $"File transfer failed: {ex.Message}");
        }
        finally
        {
            try
            {
                if (session.IsOpen)
                {
                    session.TrySend(FileMessages.BuildClose(_options.ServerId));
                    await Task.Delay(_options.CloseDelay, CancellationToken.None).ConfigureAwait(false);
                }
            }
            finally
            {
                session.Detach();
                _transport.EndSession();
                Gate.Release();
            }
        }
    }

    // ------------------------------------------------------------------ download

    async Task<FileServiceResult<IReadOnlyList<byte[]>>> DownloadFilesAsync(Session s, IReadOnlyList<uint> ids, IProgress<FileProgress>? progress, CancellationToken cancel)
    {
        var files = new List<byte[]>();
        int totalParts = 0;
        foreach (var id in ids)
        {
            string name = FileIds.Describe(id);
            using var content = new MemoryStream();
            ushort expected = 0;
            int retries = 0;
            s.TrySend(FileMessages.BuildRequest(_options.ServerId, id, 0));
            var deadline = s.Now + _options.PartTimeout;
            while (true)
            {
                cancel.ThrowIfCancellationRequested();
                s.BeatIfDue(false);
                var frame = await s.NextAsync(Min(deadline - s.Now, s.UntilBeat), cancel).ConfigureAwait(false);
                if (frame is null)
                {
                    if (s.Now < deadline) continue;
                    if (retries++ >= _options.PartRetries)
                        return expected == 0
                            ? FileServiceResult<IReadOnlyList<byte[]>>.Fail(FileServiceError.NotServed, $"The OP-Z did not serve {name}")
                            : FileServiceResult<IReadOnlyList<byte[]>>.Fail(FileServiceError.Timeout, $"Timed out reading {name} at part {expected}");
                    s.Emit($"re-requesting {name} part {expected}");
                    s.TrySend(FileMessages.BuildRequest(_options.ServerId, id, expected));
                    deadline = s.Now + _options.PartTimeout;
                    continue;
                }
                if (frame.Value.MessageId != FileMessages.Part) continue;
                if (!FilePart.TryDecode(frame.Value.Payload, out var part))
                    return FileServiceResult<IReadOnlyList<byte[]>>.Fail(FileServiceError.Protocol, $"Corrupt part while reading {name}");
                if (part.Sender != s.ClientId || part.FileId != id || part.Part < expected) continue; // stray or duplicate
                if (part.Part > expected)
                {
                    s.TrySend(FileMessages.BuildRequest(_options.ServerId, id, expected));
                    deadline = s.Now + _options.PartTimeout;
                    continue;
                }
                content.Write(part.Data);
                totalParts++;
                progress?.Report(new FileProgress($"Reading {name}", totalParts, 0));
                if (part.Last) break;
                expected++;
                retries = 0;
                s.TrySend(FileMessages.BuildRequest(_options.ServerId, id, expected));
                deadline = s.Now + _options.PartTimeout;
            }
            s.Emit($"read {name}: {content.Length} bytes in {expected + 1} parts");
            files.Add(content.ToArray());
        }
        return FileServiceResult<IReadOnlyList<byte[]>>.Success(files);
    }

    // ------------------------------------------------------------------ upload

    async Task<FileServiceResult<UploadReport>> UploadFilesAsync(Session s, IReadOnlyList<HostedFile> files, IReadOnlyList<uint> removePlugIds, IProgress<FileProgress>? progress, CancellationToken cancel)
    {
        var manifest = SyncJob.Build(files, removePlugIds);
        var hosted = files.ToDictionary(f => f.FileId, f => f.Data);
        int totalParts = FileMessages.PartCount(manifest.Length) + files.Sum(f => FileMessages.PartCount(f.Data.Length));
        var sentLast = new HashSet<uint>();
        uint? active = null;
        int nextPart = 0, sentParts = 0, completedParts = 0;
        bool manifestRequested = false, complete = false;
        TimeSpan lastSent = Never, lastActivity = s.Now, completeAt = TimeSpan.Zero, lastFeedback = s.Now;
        s.BeatIfDue(true, force: true);

        while (true)
        {
            cancel.ThrowIfCancellationRequested();
            s.BeatIfDue(true);

            // Handle everything already received before deciding to send.
            while (s.TryNext(out var frame)) HandleFrame(frame);

            if (complete && (sentLast.Count == hosted.Count || (active is null && s.Now - completeAt >= _options.CompletionGrace)))
            {
                var skipped = hosted.Keys.Where(k => !sentLast.Contains(k)).ToList();
                s.Emit($"upload complete: {sentParts} parts, {skipped.Count} file(s) skipped");
                return FileServiceResult<UploadReport>.Success(new UploadReport(skipped, sentParts, LastStateCrc ?? 0));
            }

            TimeSpan wait;
            if (active is not null)
            {
                int inFlight = Math.Max(0, sentParts - completedParts);
                var interval = inFlight >= 33 ? TimeSpan.FromMilliseconds(50) : inFlight >= 17 ? TimeSpan.FromMilliseconds(20) : _options.PartInterval;
                if (interval < _options.PartInterval) interval = _options.PartInterval;
                bool stalled = inFlight >= _options.MaxInFlightParts && s.Now - lastFeedback < TimeSpan.FromSeconds(1);
                if (!stalled && s.Now - lastSent >= interval)
                {
                    SendPart();
                    lastSent = s.Now;
                    continue;
                }
                wait = stalled ? TimeSpan.FromMilliseconds(50) : interval - (s.Now - lastSent);
            }
            else
            {
                var idle = _options.IdleTimeout - (s.Now - lastActivity);
                if (idle <= TimeSpan.Zero)
                    return FileServiceResult<UploadReport>.Fail(FileServiceError.Timeout, manifestRequested
                        ? "The OP-Z stopped requesting file parts"
                        : "The OP-Z did not request the sync job");
                wait = Min(idle, s.UntilBeat);
                if (complete) wait = Min(wait, _options.CompletionGrace - (s.Now - completeAt));
            }
            var next = await s.NextAsync(Max(wait, TimeSpan.FromMilliseconds(1)), cancel).ConfigureAwait(false);
            if (next is not null) HandleFrame(next.Value);
        }

        void HandleFrame(OpzFrame frame)
        {
            switch (frame.MessageId)
            {
                case FileMessages.Request when FileRequest.TryDecode(frame.Payload, out var r) && r.Requester == s.ClientId:
                    byte[]? data = r.FileId == FileIds.SyncJob ? manifest : hosted.GetValueOrDefault(r.FileId);
                    if (data is null) throw new InvalidDataException($"The OP-Z requested unknown file {r.FileId}");
                    if (r.Part >= FileMessages.PartCount(data.Length)) throw new InvalidDataException($"The OP-Z requested part {r.Part} beyond {FileIds.Describe(r.FileId)}");
                    if (r.FileId == FileIds.SyncJob) manifestRequested = true;
                    if (active == r.FileId && r.Part != nextPart) s.Emit($"resume {FileIds.Describe(r.FileId)} at part {r.Part}");
                    active = r.FileId;
                    nextPart = r.Part;
                    lastSent = Never;
                    lastActivity = s.Now;
                    break;
                case FileMessages.Status when FileStatus.TryDecode(frame.Payload, out var st) && st.ClientId == s.ClientId:
                    lastActivity = lastFeedback = s.Now;
                    if (st.Complete) { if (!complete) completeAt = s.Now; complete = true; }
                    else if (st.IsProgress) completedParts = Math.Max(completedParts, (int)st.Value);
                    else if (st.Status == FileMessages.StatusStateCrc) LastStateCrc = st.Value;
                    break;
            }
        }

        void SendPart()
        {
            uint id = active!.Value;
            var data = id == FileIds.SyncJob ? manifest : hosted[id];
            int start = nextPart * FileMessages.PartSize, end = Math.Min(start + FileMessages.PartSize, data.Length);
            bool last = end == data.Length;
            s.TrySend(FileMessages.BuildPart(_options.ServerId, id, (ushort)nextPart, last, data.AsSpan(start, end - start)));
            sentParts++;
            lastActivity = s.Now;
            progress?.Report(new FileProgress($"Writing {FileIds.Describe(id)}", Math.Min(sentParts, totalParts), totalParts));
            if (last)
            {
                if (id != FileIds.SyncJob) sentLast.Add(id);
                active = null;
            }
            else nextPart++;
        }
    }

    static readonly TimeSpan Never = TimeSpan.FromDays(-1);
    static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;
    static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;

    // ------------------------------------------------------------------ session plumbing

    /// <summary>One $51 ... $55 session: queues received file frames and keeps the $52 heartbeat going.</summary>
    sealed class Session(IFileTransport transport, FileClientOptions options, Action<string> log)
    {
        readonly ConcurrentQueue<OpzFrame> _queue = new();
        readonly SemaphoreSlim _signal = new(0);
        readonly Stopwatch _clock = Stopwatch.StartNew();
        TimeSpan _lastBeat = TimeSpan.MinValue;

        public ushort ClientId { get; private set; }
        public bool IsOpen { get; private set; }
        public TimeSpan Now => _clock.Elapsed;
        public TimeSpan UntilBeat => _lastBeat == TimeSpan.MinValue ? TimeSpan.Zero : options.HeartbeatInterval - (Now - _lastBeat);

        public void Attach() => transport.Received += OnReceived;
        public void Detach() => transport.Received -= OnReceived;
        public void Emit(string message) => log(message);

        void OnReceived(byte[] data)
        {
            if (!OpzFrame.TryParse(data, out var frame)) return;
            if (frame.MessageId is not (FileMessages.Status or FileMessages.ClientHello or FileMessages.Request or FileMessages.Part)) return;
            _queue.Enqueue(frame);
            _signal.Release();
        }

        public bool TryNext(out OpzFrame frame)
        {
            if (_signal.Wait(0) && _queue.TryDequeue(out frame)) return true;
            frame = default;
            return false;
        }

        public async Task<OpzFrame?> NextAsync(TimeSpan timeout, CancellationToken cancel)
        {
            if (timeout < TimeSpan.Zero) timeout = TimeSpan.Zero;
            if (!await _signal.WaitAsync(timeout, cancel).ConfigureAwait(false)) return null;
            return _queue.TryDequeue(out var frame) ? frame : null;
        }

        public void TrySend(byte[] message)
        {
            try { transport.Send(message); }
            catch (Exception ex) { log($"send failed: {ex.Message}"); }
        }

        public void BeatIfDue(bool syncActive, bool force = false)
        {
            if (!force && UntilBeat > TimeSpan.Zero) return;
            TrySend(FileMessages.BuildHeartbeat(options.ServerId, syncActive));
            _lastBeat = Now;
        }

        /// <summary>Advertises the server until the OP-Z answers with $34, then sends an idle $52 (required before the first $35).</summary>
        public async Task<FileClientHello?> OpenAsync(CancellationToken cancel)
        {
            var deadline = Now + options.HelloTimeout;
            var nextAdvert = TimeSpan.Zero;
            while (Now < deadline)
            {
                if (Now >= nextAdvert)
                {
                    TrySend(FileMessages.BuildAdvertise(options.ServerId));
                    nextAdvert = Now + options.HeartbeatInterval;
                }
                var wait = (nextAdvert < deadline ? nextAdvert : deadline) - Now;
                var frame = await NextAsync(wait, cancel).ConfigureAwait(false);
                if (frame is { MessageId: FileMessages.ClientHello } f && FileClientHello.TryDecode(f.Payload, out var hello))
                {
                    ClientId = hello.ClientId;
                    IsOpen = true;
                    log($"file client {hello.ClientId} (state crc {hello.StateCrc:X8})");
                    BeatIfDue(false, force: true);
                    return hello;
                }
            }
            return null;
        }
    }
}
