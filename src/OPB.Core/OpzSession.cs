using System.Buffers.Binary;
using OPB.Core.Midi;
using OPB.Core.Model;
using OPB.Core.Protocol;

namespace OPB.Core;

[Flags]
public enum Changes
{
    None = 0,
    Connection = 1 << 0,
    Device = 1 << 1,
    Play = 1 << 2,
    Level = 1 << 3,
    Ui = 1 << 4,
    Chain = 1 << 5,
    Bank = 1 << 6,
    Global = 1 << 7,
    Sound = 1 << 8,
    Mixer = 1 << 9,
    PatternState = 1 << 10,
    Module = 1 << 11,
    MidiConfig = 1 << 12,
    Sampler = 1 << 13,
    AudioVis = 1 << 14,
    Transport = 1 << 15,
}

/// <summary>
/// The OP-Z companion "StateSync" session: keeps the connection alive with heartbeats,
/// mirrors every state message the OP-Z sends, and exposes the writable state messages.
/// When constructed with a SynchronizationContext (the UI thread), all state changes and
/// events happen on that context.
/// </summary>
public sealed class OpzSession : IDisposable
{
    readonly Func<IMidiConnection?> _connector;
    readonly SynchronizationContext? _context;
    readonly Timer _timer;
    readonly object _connectionLock = new();
    IMidiConnection? _midi;
    long _lastAckTicks;
    ushort _sessionId = 0xCE2E;
    long _clockTicks;
    readonly List<byte> _patternPackets = new();
    TaskCompletionSource<int>? _pendingAck;
    int _pendingAckPacket = -1;
    TaskCompletionSource<bool>? _pendingBank;
    DateTime _lastBankRequest;
    Timer? _bankRefreshTimer;
    volatile bool _stateSyncPaused;
    // Newest unsent state write per message (see SendState), and the pacing timer.
    readonly Dictionary<int, byte[]> _pendingState = new();
    readonly Timer _stateTimer;
    bool _statePacing;
    const int StateInterval = 30;

    public OpzSession(Func<IMidiConnection?> connector, SynchronizationContext? context = null)
    {
        _connector = connector;
        _context = context;
        _timer = new Timer(_ => Tick(), null, Timeout.Infinite, Timeout.Infinite);
        _stateTimer = new Timer(_ => FlushState(), null, Timeout.Infinite, Timeout.Infinite);
        for (int t = 0; t < Tracks.Count; t++) CurrentSound[t] = new byte[18];
    }

    // ---------------------------------------------------------------- state

    public bool IsConnected { get; private set; }
    public string? PortName { get; private set; }
    public DeviceInfo Device { get; private set; }
    public PlayState Play { get; private set; }
    public LevelState Level { get; private set; }
    public UiState Ui { get; private set; } = UiState.Decode([]);
    public ChainState Chain { get; private set; } = new([], 0, 0, 0, 0);
    public PatternBank? Bank { get; private set; }
    public byte BankAddress { get; private set; }
    public ushort BankTransferId { get; private set; }
    public GlobalData? Global { get; private set; }
    public MidiConfig? MidiConfig { get; private set; }
    public MixerState Mixer { get; private set; } = new(new byte[16], 0);
    public bool HasMixer { get; private set; }
    public PatternState PatternState { get; private set; }
    public ModuleInfo Module { get; private set; }
    public SamplerUiState Sampler { get; private set; }
    public byte AudioVis { get; private set; }
    public bool ClockRunning { get; private set; }

    /// <summary>The live 18-byte sound record of each track (from $0E, or the active pattern).</summary>
    public byte[][] CurrentSound { get; } = new byte[Tracks.Count][];

    public int ActivePattern => Chain.Pattern;
    public int ActiveTrack => Play.Track;

    /// <summary>24 PPQN MIDI clock ticks since the last Start (requires clock out enabled on the OP-Z).</summary>
    public long ClockTicks => Interlocked.Read(ref _clockTicks);

    public event Action<Changes>? Changed;
    /// <summary>Track (0..15), MIDI note, velocity (0 = note off).</summary>
    public event Action<int, int, int>? NoteEvent;
    public event Action<int, int, int>? ControlChange;
    public event Action<string>? Log;
    /// <summary>Every frame received (raw), for diagnostics and the file-transfer client.</summary>
    public event Action<byte[]>? RawSysex;

    // ---------------------------------------------------------------- lifecycle

    public void Start() => _timer.Change(0, 900);

    /// <summary>Stops heartbeats so another client (file transfer) can own the device for a while.</summary>
    public void PauseStateSync(bool paused) => _stateSyncPaused = paused;

    public IMidiConnection? Connection => _midi;

    /// <summary>Closes the MIDI port; the next heartbeat tick reopens it from scratch.</summary>
    public void Reconnect() => DropConnection();

    /// <summary>Processes a SysEx message as if the OP-Z had sent it (offline previews and tests).</summary>
    public void InjectSysex(byte[] message) => OnSysex(message);

    /// <summary>Processes a short MIDI message as if the OP-Z had sent it (offline previews and tests).</summary>
    public void InjectShort(byte status, byte d1 = 0, byte d2 = 0) => OnShort(status, d1, d2);

    void Tick()
    {
        try
        {
            lock (_connectionLock)
            {
                if (_midi is null)
                {
                    var midi = _connector();
                    if (midi is null) return;
                    _midi = midi;
                    midi.SysexReceived += OnSysex;
                    midi.ShortReceived += OnShort;
                    _lastAckTicks = Environment.TickCount64;
                    _sessionId = (ushort)Random.Shared.Next(1, 0x8000);
                    Post(() =>
                    {
                        PortName = midi.Name;
                        Emit($"Opened MIDI port {midi.Name}");
                    });
                    midi.SendSysex(OpzFrame.IdentityInquiry);
                }

                if (_stateSyncPaused) { _lastAckTicks = Environment.TickCount64; return; }

                _midi.SendSysex(OpzFrame.Hello(_sessionId));

                if (Environment.TickCount64 - _lastAckTicks > 4000)
                {
                    if (IsConnected) Post(() => SetConnected(false));
                    // The port may have vanished (unplugged); reopen it from scratch.
                    if (Environment.TickCount64 - _lastAckTicks > 8000) DropConnection();
                }
            }
        }
        catch (Exception ex)
        {
            Post(() => Emit($"MIDI error: {ex.Message}"));
            DropConnection();
        }
    }

    void DropConnection()
    {
        lock (_connectionLock)
        {
            if (_midi is null) return;
            _midi.SysexReceived -= OnSysex;
            _midi.ShortReceived -= OnShort;
            try { _midi.Dispose(); } catch { /* port already gone */ }
            _midi = null;
        }
        lock (_pendingState) _pendingState.Clear();
        Post(() => SetConnected(false));
    }

    void SetConnected(bool connected)
    {
        if (IsConnected == connected) return;
        IsConnected = connected;
        if (!connected)
        {
            HasMixer = false;
            ClockRunning = false;
        }
        Emit(connected ? "Connected to OP-Z" : "Disconnected");
        Changed?.Invoke(Changes.Connection);
    }

    public void Dispose()
    {
        _timer.Dispose();
        _stateTimer.Dispose();
        _bankRefreshTimer?.Dispose();
        DropConnection();
    }

    void Post(Action action)
    {
        if (_context is null) action();
        else _context.Post(_ => action(), null);
    }

    void Emit(string message) => Log?.Invoke(message);

    // ---------------------------------------------------------------- receive

    void OnShort(byte status, byte d1, byte d2)
    {
        switch (status)
        {
            case 0xF8: Interlocked.Increment(ref _clockTicks); return;
            case 0xFA:
                Interlocked.Exchange(ref _clockTicks, 0);
                Post(() => { ClockRunning = true; Changed?.Invoke(Changes.Transport); });
                return;
            case 0xFB: Post(() => { ClockRunning = true; Changed?.Invoke(Changes.Transport); }); return;
            case 0xFC: Post(() => { ClockRunning = false; Changed?.Invoke(Changes.Transport); }); return;
        }
        int kind = status & 0xF0, channel = status & 0x0F;
        if (kind is 0x90 or 0x80 or 0xB0)
        {
            Post(() =>
            {
                int track = TrackForChannel(channel);
                if (track < 0) return;
                if (kind == 0xB0) ControlChange?.Invoke(track, d1, d2);
                else NoteEvent?.Invoke(track, d1, kind == 0x80 ? 0 : d2);
            });
        }
    }

    int TrackForChannel(int channel)
    {
        if (MidiConfig is null) return channel;
        for (int t = 0; t < Tracks.Count; t++)
            if (MidiConfig.Channel(t) == channel) return t;
        return -1;
    }

    void OnSysex(byte[] data)
    {
        if (OpzFrame.IsIdentityReply(data))
        {
            Post(() =>
            {
                // F0 7E 7F 06 02 00 20 76 01 <ascii serial(8)> <ascii version> 00 ...
                var text = System.Text.Encoding.ASCII.GetString(data, 9, data.Length - 10).TrimEnd('\0', (char)0x06);
                int end = text.IndexOf('\0');
                if (end >= 0) text = text[..end];
                if (text.Length > 8 && string.IsNullOrEmpty(Device.Serial))
                    Device = new DeviceInfo(text[..8], text[8..]);
                Changed?.Invoke(Changes.Device);
            });
            return;
        }
        if (!OpzFrame.TryParse(data, out var frame)) return;
        Post(() =>
        {
            RawSysex?.Invoke(data);
            try { Handle(frame); }
            catch (Exception ex) { Emit($"Error handling ${frame.MessageId:X2}: {ex.Message}"); }
        });
    }

    void Handle(OpzFrame frame)
    {
        var p = frame.Payload;
        switch (frame.MessageId)
        {
            case 0x01:
                _lastAckTicks = Environment.TickCount64;
                if (!IsConnected)
                {
                    SetConnected(true);
                    // Ask for the full pattern bank and MIDI setup if they don't arrive by themselves.
                    _ = Task.Delay(1500).ContinueWith(_ => Post(() =>
                    {
                        if (Bank is null) RequestBank();
                        if (MidiConfig is null) RequestMidiConfig();
                    }));
                }
                break;
            case 0x02: ApplyPatternDelta(p); break;
            case 0x03: Play = PlayState.Decode(p); Changed?.Invoke(Changes.Play); break;
            case 0x04: Level = LevelState.Decode(p); Changed?.Invoke(Changes.Level); break;
            case 0x06: Ui = UiState.Decode(p); Changed?.Invoke(Changes.Ui); break;
            case 0x07:
                int previous = Chain.Pattern;
                Chain = ChainState.Decode(p);
                if (Bank is not null && previous != Chain.Pattern) LoadSoundFromBank();
                Changed?.Invoke(Changes.Chain | Changes.Sound);
                break;
            case 0x08:
                if (p.Length >= 4 && p[0] == 0xFF && p[1] == 0xFF)
                {
                    Emit("OP-Z rejected a pattern upload");
                    _pendingAck?.TrySetResult(-1);
                }
                break;
            case 0x09:
            case 0x0A:
                ReceivePatternPacket(frame.MessageId, p);
                break;
            case 0x0B:
                if (p.Length >= 8 && _pendingAck is not null)
                {
                    int packet = BinaryPrimitives.ReadUInt16LittleEndian(p.AsSpan(6));
                    if (packet == _pendingAckPacket) _pendingAck.TrySetResult(packet);
                }
                break;
            case 0x0C:
                Global = new GlobalData(Zlib.Decompress(p, GlobalData.Size));
                Changed?.Invoke(Changes.Global);
                break;
            case 0x0E:
                if (p.Length >= 19)
                {
                    int track = p[0] & 0x0F;
                    p.AsSpan(1, 18).CopyTo(CurrentSound[track]);
                    Bank?.SetSoundRecord(ActivePattern, track, p.AsSpan(1, 18));
                    Changed?.Invoke(Changes.Sound);
                }
                break;
            case 0x10:
                MidiConfig = new MidiConfig(Zlib.Decompress(p, MidiConfig.Size));
                Changed?.Invoke(Changes.MidiConfig);
                break;
            case 0x11:
                Device = DeviceInfo.FromMessage11(p);
                Changed?.Invoke(Changes.Device);
                break;
            case 0x12:
                Mixer = MixerState.Decode(p);
                HasMixer = true;
                Changed?.Invoke(Changes.Mixer);
                break;
            case 0x13: PatternState = PatternState.Decode(p); Changed?.Invoke(Changes.PatternState); break;
            case 0x14: Module = ModuleInfo.Decode(p); Changed?.Invoke(Changes.Module); break;
            case 0x1A: Sampler = SamplerUiState.Decode(p); Changed?.Invoke(Changes.Sampler); break;
            case 0x1B: if (p.Length > 0) { AudioVis = p[0]; Changed?.Invoke(Changes.AudioVis); } break;
        }
    }

    void ReceivePatternPacket(byte id, byte[] p)
    {
        if (p.Length < 6) return;
        int packet = BinaryPrimitives.ReadUInt16LittleEndian(p.AsSpan(4));
        if (packet == 0) _patternPackets.Clear();
        _patternPackets.AddRange(p.AsSpan(6).ToArray());
        BankAddress = p[0];
        BankTransferId = BinaryPrimitives.ReadUInt16LittleEndian(p.AsSpan(2));
        if (id == 0x09)
        {
            // Acknowledge every non-final packet or the OP-Z stops sending.
            var ack = new byte[] { 0x09, 0, 0, 0, p[2], p[3], p[4], p[5] };
            Send(OpzFrame.Build(0x0B, ack));
            return;
        }
        try
        {
            var data = Zlib.Decompress(_patternPackets.ToArray(), PatternBank.Size);
            if (data.Length == PatternBank.Size)
            {
                Bank = new PatternBank(data);
                LoadSoundFromBank();
                _pendingBank?.TrySetResult(true);
                Changed?.Invoke(Changes.Bank | Changes.Sound);
            }
        }
        catch (Exception ex)
        {
            Emit($"Pattern bank decode failed: {ex.Message}");
        }
        _patternPackets.Clear();
    }

    void LoadSoundFromBank()
    {
        if (Bank is null) return;
        for (int t = 0; t < Tracks.Count; t++)
            CurrentSound[t] = Bank.SoundRecord(ActivePattern, t);
    }

    void ApplyPatternDelta(byte[] p)
    {
        // Change records: counter, flag, offset LE16, address, length, type, payload...
        bool needsRefresh = false;
        int i = 0;
        while (Bank is not null && i + 7 <= p.Length)
        {
            int offset = p[i + 2] | (p[i + 3] << 8);
            int pattern = p[i + 4] & 0x0F;
            int length = p[i + 5];
            int type = p[i + 6];
            if (i + 7 + length > p.Length) break;
            if (type is 0x04 or 0x05) needsRefresh = true;
            // Offset 0 is valid: it's the kick's plug id, the first field of the pattern.
            else if (offset + length <= PatternBank.PatternSize)
                p.AsSpan(i + 7, length).CopyTo(Bank.Data.AsSpan(pattern * PatternBank.PatternSize + offset));
            i += 7 + length;
        }
        if (Bank is not null)
        {
            LoadSoundFromBank();
            Changed?.Invoke(Changes.Bank | Changes.Sound);
        }
        if (needsRefresh) ScheduleBankRefresh();
    }

    void ScheduleBankRefresh()
    {
        _bankRefreshTimer?.Dispose();
        _bankRefreshTimer = new Timer(_ => Post(RequestBank), null, 350, Timeout.Infinite);
    }

    // ---------------------------------------------------------------- send

    void Send(byte[] message)
    {
        try { _midi?.SendSysex(message); }
        catch (Exception ex) { Emit($"Send failed: {ex.Message}"); }
    }

    public void SendShort(byte status, byte d1 = 0, byte d2 = 0)
    {
        try { _midi?.SendShort(status, d1, d2); }
        catch (Exception ex) { Emit($"Send failed: {ex.Message}"); }
    }

    public void SendFrame(byte id, byte[] payload) => Send(OpzFrame.Build(id, payload));

    /// <summary>
    /// Sends a state message that replaces the previous one with the same <paramref name="key"/>.
    /// A drag edits far faster than Bluetooth carries, and a backlog delays the heartbeat until
    /// the link times out, so only the newest version waits while the link is busy.
    /// </summary>
    void SendState(int key, byte[] message)
    {
        lock (_pendingState)
        {
            _pendingState[key] = message;
            if (!_statePacing) FlushState();
        }
    }

    void FlushState()
    {
        lock (_pendingState)
        {
            if (_pendingState.Count == 0) { _statePacing = false; return; }
            if (_midi is not { QueuedPackets: > 0 })
            {
                foreach (var message in _pendingState.Values) Send(message);
                _pendingState.Clear();
            }
            _statePacing = true;
            try { _stateTimer.Change(StateInterval, Timeout.Infinite); }
            catch (ObjectDisposedException) { /* session closed */ }
        }
    }

    public void RequestBank()
    {
        if (!IsConnected || (DateTime.UtcNow - _lastBankRequest).TotalMilliseconds < 200) return;
        _lastBankRequest = DateTime.UtcNow;
        Send(OpzFrame.Build(0x08));
    }

    public void RequestMidiConfig() => Send(OpzFrame.Build(0x0F));

    public async Task<bool> RefreshBankAsync(TimeSpan timeout)
    {
        _pendingBank = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _lastBankRequest = DateTime.MinValue;
        RequestBank();
        var done = await Task.WhenAny(_pendingBank.Task, Task.Delay(timeout));
        return done == _pendingBank.Task;
    }

    /// <summary>Writes one sound parameter of a track immediately ($0E, 8-bit, active pattern).</summary>
    public void SetSound(int track, SoundParam param, byte value)
    {
        CurrentSound[track][(int)param] = value;
        Bank?.SetSound(ActivePattern, track, param, value);
        var payload = new byte[19];
        payload[0] = (byte)track;
        CurrentSound[track].CopyTo(payload, 1);
        SendState(0x0E00 | track, OpzFrame.Build(0x0E, payload));
        Changed?.Invoke(Changes.Sound);
    }

    /// <summary>Selects the active track (and octave) on the device ($03).</summary>
    public void SelectTrack(int track, sbyte? octave = null)
    {
        var state = Play with { Track = track, Octave = octave ?? Play.Octave };
        Play = state;
        Send(OpzFrame.Build(0x03, state.Encode()));
        Changed?.Invoke(Changes.Play);
    }

    public void SetOctave(sbyte octave) => SelectTrack(Play.Track, (sbyte)Math.Clamp((int)octave, -3, 3));

    /// <summary>Recalls a pattern (or a chain of patterns) in the current project ($07).</summary>
    public void SelectPatterns(IReadOnlyList<int> patterns)
    {
        if (patterns.Count == 0) return;
        Send(OpzFrame.Build(0x07, ChainState.Encode(patterns, Chain.Project)));
    }

    public void SetMixer(MixerState state)
    {
        Mixer = state;
        SendState(0x12, OpzFrame.Build(0x12, state.Encode()));
        Changed?.Invoke(Changes.Mixer);
    }

    public void SetTrackMute(int track, bool muted)
    {
        ushort mask = muted ? (ushort)(Mixer.MuteMask | (1 << track)) : (ushort)(Mixer.MuteMask & ~(1 << track));
        SetMixer(Mixer with { MuteMask = mask });
    }

    /// <summary>Replaces the project-global block ($0C): tempo, swing, group levels, metronome.</summary>
    public void SetGlobal(GlobalData global)
    {
        Global = global;
        SendState(0x0C, OpzFrame.Build(0x0C, Zlib.Compress(global.Data)));
        Changed?.Invoke(Changes.Global);
    }

    public void EditGlobal(Action<GlobalData> edit)
    {
        if (Global is null) return;
        var copy = Global.Clone();
        edit(copy);
        SetGlobal(copy);
    }

    public void SetMidiConfig(MidiConfig config)
    {
        MidiConfig = config;
        SendState(0x10, OpzFrame.Build(0x10, Zlib.Compress(config.Data)));
        Changed?.Invoke(Changes.MidiConfig);
    }

    public void TransportStart() => SendShort(0xFA);
    public void TransportStop() => SendShort(0xFC);

    /// <summary>
    /// Uploads an edited bank with per-packet acknowledgement, then reads it back.
    /// Returns false (after attempting to restore <paramref name="rollback"/>) if anything fails.
    /// </summary>
    public async Task<bool> UploadBankAsync(PatternBank bank, PatternBank? rollback = null)
    {
        if (!IsConnected) return false;
        bool ok = await SendBankPacketsAsync(bank);
        if (ok)
        {
            Bank = bank;
            LoadSoundFromBank();
            Changed?.Invoke(Changes.Bank | Changes.Sound);
            await Task.Delay(150);
            await RefreshBankAsync(TimeSpan.FromSeconds(3));
            return true;
        }
        Emit("Pattern upload failed");
        if (rollback is not null && await SendBankPacketsAsync(rollback))
            await RefreshBankAsync(TimeSpan.FromSeconds(3));
        return false;
    }

    async Task<bool> SendBankPacketsAsync(PatternBank bank)
    {
        var compressed = Zlib.Compress(bank.Data);
        int chunks = (compressed.Length + 177) / 178;
        byte address = (byte)(BankAddress & 0xF0);
        for (int index = 0; index < chunks; index++)
        {
            var chunk = compressed[(index * 178)..Math.Min(compressed.Length, index * 178 + 178)];
            var payload = new byte[6 + chunk.Length];
            payload[0] = address;
            BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(2), BankTransferId);
            BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(4), (ushort)index);
            chunk.CopyTo(payload.AsSpan(6));
            bool last = index == chunks - 1;
            if (last)
            {
                Send(OpzFrame.Build(0x0A, payload));
                return true;
            }
            _pendingAck = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pendingAckPacket = index;
            Send(OpzFrame.Build(0x09, payload));
            var done = await Task.WhenAny(_pendingAck.Task, Task.Delay(1500));
            if (done != _pendingAck.Task || _pendingAck.Task.Result != index) return false;
        }
        return true;
    }

    /// <summary>Applies an edit to a copy of the bank and uploads it with readback and rollback.</summary>
    public async Task<bool> EditBankAsync(Action<PatternBank> edit)
    {
        if (Bank is null) return false;
        var original = Bank.Clone();
        var edited = Bank.Clone();
        edit(edited);
        return await UploadBankAsync(edited, original);
    }
}
