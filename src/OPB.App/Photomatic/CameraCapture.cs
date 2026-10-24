using System.Runtime.InteropServices;

namespace OPB.App.Photomatic;

/// <summary>
/// Live camera frames through the Media Foundation source reader (mfplat/mfreadwrite COM interop), so the
/// project can stay on plain net8.0-windows without the WinRT MediaCapture projection. Frames arrive as
/// BGRA pixels on a background thread.
/// </summary>
public sealed class CameraCapture : IDisposable
{
    const int FirstVideoStream = unchecked((int)0xFFFFFFFC);
    static readonly Guid SourceType = new("c60ac5fe-252a-478f-a0ef-bc8fa5f7cad3");
    static readonly Guid SourceTypeVidcap = new("8ac3587a-4ae7-42d8-99e0-0a6013eef90f");
    static readonly Guid FriendlyName = new("60d0e559-52f8-4fa2-bbce-acdb34a8ec01");
    static readonly Guid AdvancedVideoProcessing = new("0f81da2c-b537-4672-a8b2-a681b17307a3");
    static readonly Guid MajorType = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
    static readonly Guid Subtype = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
    static readonly Guid FrameSize = new("1652c33d-d6b2-4012-b834-72030849a37d");
    static readonly Guid DefaultStride = new("644b4e48-1e02-4516-b0eb-c01ca9d49ac6");
    static readonly Guid Video = new("73646976-0000-0010-8000-00AA00389B71");
    static readonly Guid Rgb32 = new("00000016-0000-0010-8000-00AA00389B71");
    static readonly Guid MediaSourceIid = new("279a808d-aec7-40c8-9c6b-a6b492c78a66");
    static bool _started;

    readonly Thread _thread;
    volatile bool _stop;

    public CameraCapture(int device)
    {
        Device = device;
        _thread = new Thread(Run) { IsBackground = true, Name = "camera" };
        _thread.SetApartmentState(ApartmentState.MTA);
        _thread.Start();
    }

    public int Device { get; }
    public string? Error { get; private set; }
    public event Action<Pixels>? Frame;
    public event Action<string>? Failed;

    /// <summary>Friendly names of the video capture devices.</summary>
    public static List<string> Devices()
    {
        var names = new List<string>();
        try
        {
            foreach (var a in Enumerate())
            {
                var key = FriendlyName;
                names.Add(a.GetAllocatedString(ref key, out var name, out _) >= 0 ? name : "CAMERA");
                Marshal.ReleaseComObject(a);
            }
        }
        catch (Exception) { /* Media Foundation unavailable (N editions) */ }
        return names;
    }

    static List<IMFActivate> Enumerate()
    {
        Startup();
        Check(MFCreateAttributes(out var attributes, 1));
        var key = SourceType;
        var value = SourceTypeVidcap;
        Check(attributes.SetGUID(ref key, ref value));
        Check(MFEnumDeviceSources(attributes, out var array, out int count));
        var list = new List<IMFActivate>();
        for (int i = 0; i < count; i++)
        {
            var ptr = Marshal.ReadIntPtr(array, i * IntPtr.Size);
            list.Add((IMFActivate)Marshal.GetObjectForIUnknown(ptr));
            Marshal.Release(ptr);
        }
        Marshal.FreeCoTaskMem(array);
        Marshal.ReleaseComObject(attributes);
        return list;
    }

    static void Startup()
    {
        if (_started) return;
        Check(MFStartup(0x00020070, 0));
        _started = true;
    }

    void Run()
    {
        IMFActivate? activate = null;
        try
        {
            var devices = Enumerate();
            if (Device >= devices.Count) throw new InvalidOperationException("Camera not found");
            activate = devices[Device];
            foreach (var other in devices) if (other != activate) Marshal.ReleaseComObject(other);
            var iid = MediaSourceIid;
            Check(activate.ActivateObject(ref iid, out var source));
            Check(MFCreateAttributes(out var readerAttributes, 1));
            var key = AdvancedVideoProcessing;
            Check(readerAttributes.SetUINT32(ref key, 1));
            Check(MFCreateSourceReaderFromMediaSource(source, readerAttributes, out var reader));

            // Pick the largest native format up to 1280 wide, then ask for RGB32 at that size.
            long bestSize = 0;
            int bestWidth = 0;
            for (int i = 0; ; i++)
            {
                if (reader.GetNativeMediaType(FirstVideoStream, i, out var native) < 0 || native is null) break;
                var fs = FrameSize;
                if (native.GetUINT64(ref fs, out long size) >= 0)
                {
                    int w = (int)(size >> 32);
                    if (w <= 1280 && w > bestWidth) { bestWidth = w; bestSize = size; }
                }
                Marshal.ReleaseComObject(native);
            }
            Check(MFCreateMediaType(out var output));
            var mk = MajorType; var mv = Video; var sk = Subtype; var sv = Rgb32;
            Check(output.SetGUID(ref mk, ref mv));
            Check(output.SetGUID(ref sk, ref sv));
            if (bestSize != 0) { var fk = FrameSize; output.SetUINT64(ref fk, bestSize); }
            Check(reader.SetCurrentMediaType(FirstVideoStream, IntPtr.Zero, output));
            Check(reader.GetCurrentMediaType(FirstVideoStream, out var current));
            var fsk = FrameSize;
            Check(current.GetUINT64(ref fsk, out long frame));
            int width = (int)(frame >> 32), height = (int)(frame & 0xFFFFFFFF);
            var strideKey = DefaultStride;
            int stride = current.GetUINT32(ref strideKey, out int s) >= 0 ? s : width * 4;

            while (!_stop)
            {
                int hr = reader.ReadSample(FirstVideoStream, 0, out _, out int flags, out _, out var sample);
                if (hr < 0 || (flags & 0x2) != 0) throw new COMException("Camera stream ended", hr);
                if (sample is null) continue;
                try
                {
                    Check(sample.ConvertToContiguousBuffer(out var buffer));
                    Check(buffer.Lock(out var ptr, out _, out int length));
                    var data = new byte[width * height * 4];
                    int rowBytes = width * 4, abs = Math.Abs(stride);
                    for (int y = 0; y < height && (long)y * abs + rowBytes <= length; y++)
                    {
                        // Negative stride = bottom-up rows.
                        int srcRow = stride < 0 ? height - 1 - y : y;
                        Marshal.Copy(ptr + srcRow * abs, data, y * rowBytes, rowBytes);
                    }
                    buffer.Unlock();
                    Marshal.ReleaseComObject(buffer);
                    for (int i = 3; i < data.Length; i += 4) data[i] = 255;
                    Frame?.Invoke(new Pixels(data, width, height));
                }
                finally { Marshal.ReleaseComObject(sample); }
            }
            Marshal.ReleaseComObject(reader);
        }
        catch (Exception ex)
        {
            Error = ex.Message;
            Failed?.Invoke(ex.Message);
        }
        finally
        {
            try { activate?.ShutdownObject(); } catch (Exception) { }
            if (activate is not null) Marshal.ReleaseComObject(activate);
        }
    }

    public void Dispose() => _stop = true;

    static void Check(int hr) { if (hr < 0) Marshal.ThrowExceptionForHR(hr); }

    // ------------------------------------------------------------------ interop

    [DllImport("mfplat.dll")] static extern int MFStartup(int version, int flags);
    [DllImport("mfplat.dll")] static extern int MFCreateAttributes(out IMFAttributes attributes, int size);
    [DllImport("mfplat.dll")] static extern int MFCreateMediaType(out IMFAttributes type);
    [DllImport("mf.dll")] static extern int MFEnumDeviceSources(IMFAttributes attributes, out IntPtr activates, out int count);
    [DllImport("mfreadwrite.dll")]
    static extern int MFCreateSourceReaderFromMediaSource([MarshalAs(UnmanagedType.IUnknown)] object source, IMFAttributes attributes, out IMFSourceReader reader);

    // Unused vtable slots are declared as parameterless placeholders; only their order matters.
    [ComImport, Guid("2cd2d921-c447-44a7-a13c-4adabfc247e3"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMFAttributes
    {
        void GetItem(); void GetItemType(); void CompareItem(); void Compare();
        [PreserveSig] int GetUINT32(ref Guid key, out int value);
        [PreserveSig] int GetUINT64(ref Guid key, out long value);
        void GetDouble(); void GetGUID(); void GetStringLength(); void GetString();
        [PreserveSig] int GetAllocatedString(ref Guid key, [MarshalAs(UnmanagedType.LPWStr)] out string value, out int length);
        void GetBlobSize(); void GetBlob(); void GetAllocatedBlob(); void GetUnknown(); void SetItem(); void DeleteItem(); void DeleteAllItems();
        [PreserveSig] int SetUINT32(ref Guid key, int value);
        [PreserveSig] int SetUINT64(ref Guid key, long value);
        void SetDouble();
        [PreserveSig] int SetGUID(ref Guid key, ref Guid value);
        void SetString(); void SetBlob(); void SetUnknown(); void LockStore(); void UnlockStore(); void GetCount(); void GetItemByIndex(); void CopyAllItems();
    }

    [ComImport, Guid("7FEE9E9A-4A89-47a6-899C-B6A53A70FB67"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMFActivate
    {
        void GetItem(); void GetItemType(); void CompareItem(); void Compare(); void GetUINT32(); void GetUINT64();
        void GetDouble(); void GetGUID(); void GetStringLength(); void GetString();
        [PreserveSig] int GetAllocatedString(ref Guid key, [MarshalAs(UnmanagedType.LPWStr)] out string value, out int length);
        void GetBlobSize(); void GetBlob(); void GetAllocatedBlob(); void GetUnknown(); void SetItem(); void DeleteItem(); void DeleteAllItems();
        void SetUINT32(); void SetUINT64(); void SetDouble(); void SetGUID(); void SetString(); void SetBlob(); void SetUnknown();
        void LockStore(); void UnlockStore(); void GetCount(); void GetItemByIndex(); void CopyAllItems();
        [PreserveSig] int ActivateObject(ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object instance);
        [PreserveSig] int ShutdownObject();
        [PreserveSig] int DetachObject();
    }

    [ComImport, Guid("70ae66f2-c809-4e4f-8915-bdcb406b7993"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMFSourceReader
    {
        void GetStreamSelection(); void SetStreamSelection();
        [PreserveSig] int GetNativeMediaType(int stream, int index, out IMFAttributes type);
        [PreserveSig] int GetCurrentMediaType(int stream, out IMFAttributes type);
        [PreserveSig] int SetCurrentMediaType(int stream, IntPtr reserved, IMFAttributes type);
        void SetCurrentPosition();
        [PreserveSig] int ReadSample(int stream, int control, out int actualStream, out int flags, out long timestamp, out IMFSample? sample);
    }

    [ComImport, Guid("c40a00f2-b93a-4d80-ae8c-5a1c634f58e4"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMFSample
    {
        void GetItem(); void GetItemType(); void CompareItem(); void Compare(); void GetUINT32(); void GetUINT64();
        void GetDouble(); void GetGUID(); void GetStringLength(); void GetString(); void GetAllocatedString();
        void GetBlobSize(); void GetBlob(); void GetAllocatedBlob(); void GetUnknown(); void SetItem(); void DeleteItem(); void DeleteAllItems();
        void SetUINT32(); void SetUINT64(); void SetDouble(); void SetGUID(); void SetString(); void SetBlob(); void SetUnknown();
        void LockStore(); void UnlockStore(); void GetCount(); void GetItemByIndex(); void CopyAllItems();
        void GetSampleFlags(); void SetSampleFlags(); void GetSampleTime(); void SetSampleTime(); void GetSampleDuration(); void SetSampleDuration();
        void GetBufferCount(); void GetBufferByIndex();
        [PreserveSig] int ConvertToContiguousBuffer(out IMFMediaBuffer buffer);
    }

    [ComImport, Guid("045FA593-8799-42b8-BC8D-8968C6453507"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMFMediaBuffer
    {
        [PreserveSig] int Lock(out IntPtr buffer, out int maxLength, out int currentLength);
        [PreserveSig] int Unlock();
    }
}
