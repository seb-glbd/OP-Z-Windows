using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace OPB.Core.Midi;

/// <summary>
/// USB MIDI through the Windows multimedia (WinMM) API. Works with class-compliant
/// devices such as the OP-Z without any driver or third-party package.
/// </summary>
public sealed class WinMidiConnection : IMidiConnection
{
    const int BufferCount = 32;
    const int BufferSize = 64 * 1024;

    readonly IntPtr _in;
    readonly IntPtr _out;
    readonly NativeMethods.MidiInProc _callback; // kept alive for the lifetime of the handle
    readonly List<IntPtr> _headers = new();
    readonly BlockingCollection<Action> _work = new();
    readonly Thread _worker;
    readonly object _sendLock = new();
    readonly List<byte> _partialSysex = new();
    volatile bool _closing;

    public string Name { get; }
    public event Action<byte[]>? SysexReceived;
    public event Action<byte, byte, byte>? ShortReceived;

    WinMidiConnection(string name, int inputId, int outputId)
    {
        Name = name;
        _callback = OnMidiIn;
        Check(NativeMethods.midiInOpen(out _in, inputId, _callback, IntPtr.Zero, NativeMethods.CALLBACK_FUNCTION), "midiInOpen");
        int r = NativeMethods.midiOutOpen(out _out, outputId, IntPtr.Zero, IntPtr.Zero, 0);
        if (r != 0)
        {
            NativeMethods.midiInClose(_in);
            Check(r, "midiOutOpen");
        }
        _worker = new Thread(WorkLoop) { IsBackground = true, Name = "MIDI receive" };
        _worker.Start();

        int headerSize = Marshal.SizeOf<NativeMethods.MIDIHDR>();
        for (int i = 0; i < BufferCount; i++)
        {
            var header = Marshal.AllocHGlobal(headerSize);
            var hdr = new NativeMethods.MIDIHDR { lpData = Marshal.AllocHGlobal(BufferSize), dwBufferLength = BufferSize };
            Marshal.StructureToPtr(hdr, header, false);
            NativeMethods.midiInPrepareHeader(_in, header, headerSize);
            NativeMethods.midiInAddBuffer(_in, header, headerSize);
            _headers.Add(header);
        }
        Check(NativeMethods.midiInStart(_in), "midiInStart");
    }

    public static IReadOnlyList<string> InputNames() =>
        Enumerable.Range(0, NativeMethods.midiInGetNumDevs()).Select(i =>
        {
            NativeMethods.midiInGetDevCaps(i, out var caps, Marshal.SizeOf<NativeMethods.MIDIINCAPS>());
            return caps.szPname;
        }).ToList();

    public static IReadOnlyList<string> OutputNames() =>
        Enumerable.Range(0, NativeMethods.midiOutGetNumDevs()).Select(i =>
        {
            NativeMethods.midiOutGetDevCaps(i, out var caps, Marshal.SizeOf<NativeMethods.MIDIOUTCAPS>());
            return caps.szPname;
        }).ToList();

    /// <summary>Opens the first input and output whose names contain <paramref name="nameFragment"/>.</summary>
    public static WinMidiConnection? TryOpen(string nameFragment)
    {
        int inId = IndexOf(InputNames(), nameFragment);
        int outId = IndexOf(OutputNames(), nameFragment);
        if (inId < 0 || outId < 0) return null;
        return new WinMidiConnection(InputNames()[inId], inId, outId);
    }

    static int IndexOf(IReadOnlyList<string> names, string fragment)
    {
        for (int i = 0; i < names.Count; i++)
            if (names[i].Contains(fragment, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }

    void OnMidiIn(IntPtr handle, uint msg, IntPtr instance, IntPtr param1, IntPtr param2)
    {
        // Runs on a driver thread: copy the data out and hand off; never call midiIn* here.
        switch (msg)
        {
            case NativeMethods.MIM_DATA:
                int m = param1.ToInt32();
                _work.TryAdd(() => ShortReceived?.Invoke((byte)m, (byte)(m >> 8), (byte)(m >> 16)));
                break;
            case NativeMethods.MIM_LONGDATA:
            case NativeMethods.MIM_LONGERROR:
                var header = param1;
                var hdr = Marshal.PtrToStructure<NativeMethods.MIDIHDR>(header);
                var bytes = new byte[hdr.dwBytesRecorded];
                if (bytes.Length > 0) Marshal.Copy(hdr.lpData, bytes, 0, bytes.Length);
                _work.TryAdd(() => { Requeue(header); HandleSysexChunk(bytes); });
                break;
        }
    }

    void Requeue(IntPtr header)
    {
        if (_closing) return;
        var hdr = Marshal.PtrToStructure<NativeMethods.MIDIHDR>(header);
        hdr.dwBytesRecorded = 0;
        hdr.dwFlags &= ~NativeMethods.MHDR_DONE;
        Marshal.StructureToPtr(hdr, header, false);
        NativeMethods.midiInAddBuffer(_in, header, Marshal.SizeOf<NativeMethods.MIDIHDR>());
    }

    void HandleSysexChunk(byte[] chunk)
    {
        // A long message can be split across buffers; reassemble until F7.
        foreach (var b in chunk)
        {
            if (b == 0xF0) _partialSysex.Clear();
            if (b >= 0xF8) continue; // realtime bytes are delivered separately
            _partialSysex.Add(b);
            if (b == 0xF7)
            {
                if (_partialSysex.Count > 1 && _partialSysex[0] == 0xF0)
                    SysexReceived?.Invoke(_partialSysex.ToArray());
                _partialSysex.Clear();
            }
        }
    }

    void WorkLoop()
    {
        foreach (var action in _work.GetConsumingEnumerable())
        {
            try { action(); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"MIDI handler error: {ex}"); }
        }
    }

    public void SendShort(byte status, byte data1 = 0, byte data2 = 0)
    {
        lock (_sendLock)
            NativeMethods.midiOutShortMsg(_out, status | (data1 << 8) | (data2 << 16));
    }

    public void SendSysex(byte[] message)
    {
        int headerSize = Marshal.SizeOf<NativeMethods.MIDIHDR>();
        lock (_sendLock)
        {
            var data = Marshal.AllocHGlobal(message.Length);
            var header = Marshal.AllocHGlobal(headerSize);
            try
            {
                Marshal.Copy(message, 0, data, message.Length);
                Marshal.StructureToPtr(new NativeMethods.MIDIHDR { lpData = data, dwBufferLength = (uint)message.Length, dwBytesRecorded = (uint)message.Length }, header, false);
                Check(NativeMethods.midiOutPrepareHeader(_out, header, headerSize), "midiOutPrepareHeader");
                Check(NativeMethods.midiOutLongMsg(_out, header, headerSize), "midiOutLongMsg");
                var deadline = Environment.TickCount64 + 2000;
                while ((Marshal.PtrToStructure<NativeMethods.MIDIHDR>(header).dwFlags & NativeMethods.MHDR_DONE) == 0
                       && Environment.TickCount64 < deadline)
                    Thread.Sleep(0);
                NativeMethods.midiOutUnprepareHeader(_out, header, headerSize);
            }
            finally
            {
                Marshal.FreeHGlobal(header);
                Marshal.FreeHGlobal(data);
            }
        }
    }

    public void Dispose()
    {
        if (_closing) return;
        _closing = true;
        NativeMethods.midiInStop(_in);
        NativeMethods.midiInReset(_in);
        int headerSize = Marshal.SizeOf<NativeMethods.MIDIHDR>();
        foreach (var header in _headers)
            NativeMethods.midiInUnprepareHeader(_in, header, headerSize);
        NativeMethods.midiInClose(_in);
        lock (_sendLock)
        {
            NativeMethods.midiOutReset(_out);
            NativeMethods.midiOutClose(_out);
        }
        _work.CompleteAdding();
        _worker.Join(1000);
        foreach (var header in _headers)
        {
            var hdr = Marshal.PtrToStructure<NativeMethods.MIDIHDR>(header);
            Marshal.FreeHGlobal(hdr.lpData);
            Marshal.FreeHGlobal(header);
        }
        _headers.Clear();
    }

    static void Check(int result, string call)
    {
        if (result != 0) throw new IOException($"{call} failed with MMRESULT {result}");
    }

    static class NativeMethods
    {
        public const int CALLBACK_FUNCTION = 0x30000;
        public const uint MIM_DATA = 0x3C3, MIM_LONGDATA = 0x3C4, MIM_LONGERROR = 0x3C6;
        public const uint MHDR_DONE = 1;

        public delegate void MidiInProc(IntPtr handle, uint msg, IntPtr instance, IntPtr param1, IntPtr param2);

        [StructLayout(LayoutKind.Sequential)]
        public struct MIDIHDR
        {
            public IntPtr lpData;
            public uint dwBufferLength;
            public uint dwBytesRecorded;
            public IntPtr dwUser;
            public uint dwFlags;
            public IntPtr lpNext;
            public IntPtr reserved;
            public uint dwOffset;
            public IntPtr r0, r1, r2, r3, r4, r5, r6, r7; // dwReserved[8]
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct MIDIINCAPS
        {
            public ushort wMid, wPid;
            public uint vDriverVersion;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szPname;
            public uint dwSupport;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct MIDIOUTCAPS
        {
            public ushort wMid, wPid;
            public uint vDriverVersion;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szPname;
            public ushort wTechnology, wVoices, wNotes, wChannelMask;
            public uint dwSupport;
        }

        [DllImport("winmm.dll")] public static extern int midiInGetNumDevs();
        [DllImport("winmm.dll")] public static extern int midiOutGetNumDevs();
        [DllImport("winmm.dll", CharSet = CharSet.Unicode, EntryPoint = "midiInGetDevCapsW")] public static extern int midiInGetDevCaps(int id, out MIDIINCAPS caps, int size);
        [DllImport("winmm.dll", CharSet = CharSet.Unicode, EntryPoint = "midiOutGetDevCapsW")] public static extern int midiOutGetDevCaps(int id, out MIDIOUTCAPS caps, int size);
        [DllImport("winmm.dll")] public static extern int midiInOpen(out IntPtr handle, int id, MidiInProc callback, IntPtr instance, int flags);
        [DllImport("winmm.dll")] public static extern int midiInStart(IntPtr handle);
        [DllImport("winmm.dll")] public static extern int midiInStop(IntPtr handle);
        [DllImport("winmm.dll")] public static extern int midiInReset(IntPtr handle);
        [DllImport("winmm.dll")] public static extern int midiInClose(IntPtr handle);
        [DllImport("winmm.dll")] public static extern int midiInPrepareHeader(IntPtr handle, IntPtr header, int size);
        [DllImport("winmm.dll")] public static extern int midiInUnprepareHeader(IntPtr handle, IntPtr header, int size);
        [DllImport("winmm.dll")] public static extern int midiInAddBuffer(IntPtr handle, IntPtr header, int size);
        [DllImport("winmm.dll")] public static extern int midiOutOpen(out IntPtr handle, int id, IntPtr callback, IntPtr instance, int flags);
        [DllImport("winmm.dll")] public static extern int midiOutClose(IntPtr handle);
        [DllImport("winmm.dll")] public static extern int midiOutReset(IntPtr handle);
        [DllImport("winmm.dll")] public static extern int midiOutShortMsg(IntPtr handle, int message);
        [DllImport("winmm.dll")] public static extern int midiOutPrepareHeader(IntPtr handle, IntPtr header, int size);
        [DllImport("winmm.dll")] public static extern int midiOutUnprepareHeader(IntPtr handle, IntPtr header, int size);
        [DllImport("winmm.dll")] public static extern int midiOutLongMsg(IntPtr handle, IntPtr header, int size);
    }
}
