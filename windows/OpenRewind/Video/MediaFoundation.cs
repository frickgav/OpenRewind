using System.Runtime.InteropServices;

namespace OpenRewind;

/// The few Media Foundation calls the video storage needs (built into Windows 10 and 11).
/// Each interface lists every method in vtable order; ones derived from IMFAttributes repeat its 30 methods first.
static class MF
{
    // Attribute keys and values, from mfapi.h, mfreadwrite.h and codecapi.h.
    public static Guid MT_MAJOR_TYPE = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
    public static Guid MT_SUBTYPE = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
    public static Guid MT_AVG_BITRATE = new("20332624-fb0d-4d9e-bd0d-cbf6786c102e");
    public static Guid MT_INTERLACE_MODE = new("e2724bb8-e676-4806-b4b2-a8d6efb44ccd");
    public static Guid MT_FRAME_SIZE = new("1652c33d-d6b2-4012-b834-72030849a37d");
    public static Guid MT_FRAME_RATE = new("c459a2e8-3d2c-4e44-b132-fee5156c7bb0");
    public static Guid MT_PIXEL_ASPECT_RATIO = new("c6376a1e-8d0a-4027-be45-6d9a0ad39bb6");
    public static Guid MT_DEFAULT_STRIDE = new("644b4e48-1e02-4516-b0eb-c01ca9d49ac6");
    public static Guid MT_MINIMUM_DISPLAY_APERTURE = new("d7388766-18fe-48c6-a177-ee894867c8c4");
    public static Guid MT_GEOMETRIC_APERTURE = new("66758743-7e5f-400d-980a-aa8596c85696");
    public static Guid MediaType_Video = new("73646976-0000-0010-8000-00aa00389b71");
    public static Guid VideoFormat_H264 = new("34363248-0000-0010-8000-00aa00389b71");
    public static Guid VideoFormat_HEVC = new("43564548-0000-0010-8000-00aa00389b71");
    public static Guid VideoFormat_NV12 = new("3231564e-0000-0010-8000-00aa00389b71");
    public static Guid READWRITE_ENABLE_HARDWARE_TRANSFORMS = new("a634a91c-822b-41b9-a494-4de4643612b0");
    public static Guid SINK_WRITER_DISABLE_THROTTLING = new("08b845d8-2b74-4afe-9d53-be16d2d5ae4f");
    public static Guid TRANSCODE_CONTAINERTYPE = new("150ff23f-4abc-478b-ac4f-e1916fba1cca");
    public static Guid TranscodeContainerType_MPEG4 = new("dc6cd05d-b9d0-40ef-bd35-fa622c1ab28a");
    public static Guid PD_DURATION = new("6c990d33-bb8e-477a-8598-0d5d96fcd88a");
    public static Guid AVEncMPVGOPSize = new("95f31b26-95a4-41aa-9303-246a7fc6eef1");
    public static Guid AVEncMPVDefaultBPictureCount = new("8d390aac-dc5c-4200-b57f-814d04babab2");
    public static Guid AVEncCommonRateControlMode = new("1c0608e9-370c-4710-8a58-cb6181c42423");
    public static Guid AVEncCommonQuality = new("fcbf57a3-7ea5-4b0c-9644-69b40c39c391");
    public const uint RateControl_UnconstrainedVBR = 2, RateControl_Quality = 3;
    public const uint Interlace_Progressive = 2;

    public const uint SOURCE_READER_FIRST_VIDEO_STREAM = 0xFFFFFFFC, SOURCE_READER_ALL_STREAMS = 0xFFFFFFFE, SOURCE_READER_MEDIASOURCE = 0xFFFFFFFF;
    public const uint SOURCE_READERF_ERROR = 0x1, SOURCE_READERF_ENDOFSTREAM = 0x2, SOURCE_READERF_CURRENTMEDIATYPECHANGED = 0x20;
    const uint MF_VERSION = 0x00020070, MFSTARTUP_LITE = 0x1;

    /// 100-nanosecond units per second (Media Foundation times).
    public const long Second = 10_000_000;

    [DllImport("mfplat.dll")] static extern int MFStartup(uint version, uint flags);
    [DllImport("mfplat.dll")] static extern int MFCreateMediaType(out IMFMediaType type);
    [DllImport("mfplat.dll")] static extern int MFCreateAttributes(out IMFAttributes attributes, uint initialSize);
    [DllImport("mfplat.dll")] static extern int MFCreateSample(out IMFSample sample);
    [DllImport("mfplat.dll")] static extern int MFCreateMemoryBuffer(uint maxLength, out IMFMediaBuffer buffer);
    [DllImport("mfreadwrite.dll", CharSet = CharSet.Unicode)]
    public static extern int MFCreateSinkWriterFromURL(string url, IntPtr byteStream, IMFAttributes? attributes, out IMFSinkWriter writer);
    [DllImport("mfreadwrite.dll", CharSet = CharSet.Unicode)]
    public static extern int MFCreateSourceReaderFromURL(string url, IMFAttributes? attributes, out IMFSourceReader reader);

    static readonly Lazy<int> started = new(() =>
    {
        try { return MFStartup(MF_VERSION, MFSTARTUP_LITE); }
        catch (DllNotFoundException) { return unchecked((int)0x80004005); }   // N editions without the Media Feature Pack
    });

    /// Start Media Foundation once per process. Throws when this Windows has none.
    public static void Start() => Check(started.Value, "starting Media Foundation");

    public static void Check(int hr, string what)
    {
        if (hr < 0) throw new COMException($"{what} failed (0x{hr:X8})", hr);
    }

    public static IMFMediaType MediaType()
    {
        Check(MFCreateMediaType(out var t), "MFCreateMediaType");
        return t;
    }

    public static IMFAttributes Attributes()
    {
        Check(MFCreateAttributes(out var a, 4), "MFCreateAttributes");
        return a;
    }

    public static IMFSample Sample()
    {
        Check(MFCreateSample(out var s), "MFCreateSample");
        return s;
    }

    public static IMFMediaBuffer Buffer(int length)
    {
        Check(MFCreateMemoryBuffer((uint)length, out var b), "MFCreateMemoryBuffer");
        return b;
    }

    /// Two 32-bit values in one UINT64 attribute (frame size, frame rate).
    public static ulong Pack(int hi, int lo) => ((ulong)(uint)hi << 32) | (uint)lo;

    public static void Release(object? o)
    {
        if (o != null && Marshal.IsComObject(o)) Marshal.ReleaseComObject(o);
    }
}

/// A PROPVARIANT holding a 64-bit integer (VT_I8 / VT_UI8). 24 bytes, its size on 64-bit Windows.
[StructLayout(LayoutKind.Explicit, Size = 24)]
struct PropVariant
{
    [FieldOffset(0)] public ushort Type;
    [FieldOffset(8)] public long Value;

    public static PropVariant Int64(long v) => new() { Type = 20, Value = v };  // VT_I8
}

[ComImport, Guid("2cd2d921-c447-44a7-a13c-4adabfc247e3"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IMFAttributes
{
    // IMFAttributes
    int GetItem([In] ref Guid key, IntPtr value);
    int GetItemType([In] ref Guid key, out int type);
    int CompareItem([In] ref Guid key, IntPtr value, out bool result);
    int Compare(IntPtr theirs, int matchType, out bool result);
    int GetUINT32([In] ref Guid key, out uint value);
    int GetUINT64([In] ref Guid key, out ulong value);
    int GetDouble([In] ref Guid key, out double value);
    int GetGUID([In] ref Guid key, out Guid value);
    int GetStringLength([In] ref Guid key, out uint length);
    int GetString([In] ref Guid key, IntPtr buffer, uint size, out uint length);
    int GetAllocatedString([In] ref Guid key, out IntPtr value, out uint length);
    int GetBlobSize([In] ref Guid key, out uint size);
    int GetBlob([In] ref Guid key, [Out] byte[] buffer, uint size, out uint written);
    int GetAllocatedBlob([In] ref Guid key, out IntPtr buffer, out uint size);
    int GetUnknown([In] ref Guid key, [In] ref Guid iid, out IntPtr value);
    int SetItem([In] ref Guid key, IntPtr value);
    int DeleteItem([In] ref Guid key);
    int DeleteAllItems();
    int SetUINT32([In] ref Guid key, uint value);
    int SetUINT64([In] ref Guid key, ulong value);
    int SetDouble([In] ref Guid key, double value);
    int SetGUID([In] ref Guid key, [In] ref Guid value);
    int SetString([In] ref Guid key, [MarshalAs(UnmanagedType.LPWStr)] string value);
    int SetBlob([In] ref Guid key, [In] byte[] buffer, uint size);
    int SetUnknown([In] ref Guid key, IntPtr value);
    int LockStore();
    int UnlockStore();
    int GetCount(out uint count);
    int GetItemByIndex(uint index, out Guid key, IntPtr value);
    int CopyAllItems(IntPtr destination);
}

[ComImport, Guid("44ae0fa8-ea31-4109-8d2e-4cae4997c555"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IMFMediaType
{
    // IMFAttributes
    int GetItem([In] ref Guid key, IntPtr value);
    int GetItemType([In] ref Guid key, out int type);
    int CompareItem([In] ref Guid key, IntPtr value, out bool result);
    int Compare(IntPtr theirs, int matchType, out bool result);
    int GetUINT32([In] ref Guid key, out uint value);
    int GetUINT64([In] ref Guid key, out ulong value);
    int GetDouble([In] ref Guid key, out double value);
    int GetGUID([In] ref Guid key, out Guid value);
    int GetStringLength([In] ref Guid key, out uint length);
    int GetString([In] ref Guid key, IntPtr buffer, uint size, out uint length);
    int GetAllocatedString([In] ref Guid key, out IntPtr value, out uint length);
    int GetBlobSize([In] ref Guid key, out uint size);
    int GetBlob([In] ref Guid key, [Out] byte[] buffer, uint size, out uint written);
    int GetAllocatedBlob([In] ref Guid key, out IntPtr buffer, out uint size);
    int GetUnknown([In] ref Guid key, [In] ref Guid iid, out IntPtr value);
    int SetItem([In] ref Guid key, IntPtr value);
    int DeleteItem([In] ref Guid key);
    int DeleteAllItems();
    int SetUINT32([In] ref Guid key, uint value);
    int SetUINT64([In] ref Guid key, ulong value);
    int SetDouble([In] ref Guid key, double value);
    int SetGUID([In] ref Guid key, [In] ref Guid value);
    int SetString([In] ref Guid key, [MarshalAs(UnmanagedType.LPWStr)] string value);
    int SetBlob([In] ref Guid key, [In] byte[] buffer, uint size);
    int SetUnknown([In] ref Guid key, IntPtr value);
    int LockStore();
    int UnlockStore();
    int GetCount(out uint count);
    int GetItemByIndex(uint index, out Guid key, IntPtr value);
    int CopyAllItems(IntPtr destination);
    // IMFMediaType
    int GetMajorType(out Guid type);
    int IsCompressedFormat(out bool compressed);
    int IsEqual(IntPtr other, out uint flags);
    int GetRepresentation(Guid representation, out IntPtr value);
    int FreeRepresentation(Guid representation, IntPtr value);
}

[ComImport, Guid("c40a00f2-b93a-4d80-ae8c-5a1c634f58e4"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IMFSample
{
    // IMFAttributes
    int GetItem([In] ref Guid key, IntPtr value);
    int GetItemType([In] ref Guid key, out int type);
    int CompareItem([In] ref Guid key, IntPtr value, out bool result);
    int Compare(IntPtr theirs, int matchType, out bool result);
    int GetUINT32([In] ref Guid key, out uint value);
    int GetUINT64([In] ref Guid key, out ulong value);
    int GetDouble([In] ref Guid key, out double value);
    int GetGUID([In] ref Guid key, out Guid value);
    int GetStringLength([In] ref Guid key, out uint length);
    int GetString([In] ref Guid key, IntPtr buffer, uint size, out uint length);
    int GetAllocatedString([In] ref Guid key, out IntPtr value, out uint length);
    int GetBlobSize([In] ref Guid key, out uint size);
    int GetBlob([In] ref Guid key, [Out] byte[] buffer, uint size, out uint written);
    int GetAllocatedBlob([In] ref Guid key, out IntPtr buffer, out uint size);
    int GetUnknown([In] ref Guid key, [In] ref Guid iid, out IntPtr value);
    int SetItem([In] ref Guid key, IntPtr value);
    int DeleteItem([In] ref Guid key);
    int DeleteAllItems();
    int SetUINT32([In] ref Guid key, uint value);
    int SetUINT64([In] ref Guid key, ulong value);
    int SetDouble([In] ref Guid key, double value);
    int SetGUID([In] ref Guid key, [In] ref Guid value);
    int SetString([In] ref Guid key, [MarshalAs(UnmanagedType.LPWStr)] string value);
    int SetBlob([In] ref Guid key, [In] byte[] buffer, uint size);
    int SetUnknown([In] ref Guid key, IntPtr value);
    int LockStore();
    int UnlockStore();
    int GetCount(out uint count);
    int GetItemByIndex(uint index, out Guid key, IntPtr value);
    int CopyAllItems(IntPtr destination);
    // IMFSample
    int GetSampleFlags(out uint flags);
    int SetSampleFlags(uint flags);
    int GetSampleTime(out long time);
    int SetSampleTime(long time);
    int GetSampleDuration(out long duration);
    int SetSampleDuration(long duration);
    int GetBufferCount(out uint count);
    int GetBufferByIndex(uint index, out IMFMediaBuffer buffer);
    int ConvertToContiguousBuffer(out IMFMediaBuffer buffer);
    int AddBuffer(IMFMediaBuffer buffer);
    int RemoveBufferByIndex(uint index);
    int RemoveAllBuffers();
    int GetTotalLength(out uint length);
    int CopyToBuffer(IMFMediaBuffer buffer);
}

[ComImport, Guid("045fa593-8799-42b8-bc8d-8968c6453507"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IMFMediaBuffer
{
    int Lock(out IntPtr buffer, out uint maxLength, out uint currentLength);
    int Unlock();
    int GetCurrentLength(out uint length);
    int SetCurrentLength(uint length);
    int GetMaxLength(out uint length);
}

[ComImport, Guid("3137f1cd-fe5e-4805-a5d8-fb477448cb3d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IMFSinkWriter
{
    int AddStream(IMFMediaType type, out uint index);
    int SetInputMediaType(uint index, IMFMediaType type, IMFAttributes? encodingParameters);
    int BeginWriting();
    int WriteSample(uint index, IMFSample sample);
    int SendStreamTick(uint index, long timestamp);
    int PlaceMarker(uint index, IntPtr context);
    int NotifyEndOfSegment(uint index);
    int Flush(uint index);
    int FinalizeWriting();  // IMFSinkWriter::Finalize
    int GetServiceForStream(uint index, [In] ref Guid service, [In] ref Guid iid, out IntPtr value);
    int GetStatistics(uint index, IntPtr statistics);
}

[ComImport, Guid("70ae66f2-c809-4e4f-8915-bdcb406b7993"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IMFSourceReader
{
    int GetStreamSelection(uint index, out bool selected);
    int SetStreamSelection(uint index, bool selected);
    int GetNativeMediaType(uint index, uint typeIndex, out IMFMediaType type);
    int GetCurrentMediaType(uint index, out IMFMediaType type);
    int SetCurrentMediaType(uint index, IntPtr reserved, IMFMediaType type);
    int SetCurrentPosition([In] ref Guid timeFormat, [In] ref PropVariant position);
    int ReadSample(uint index, uint controlFlags, out uint actualIndex, out uint flags, out long timestamp, out IMFSample? sample);
    int Flush(uint index);
    int GetServiceForStream(uint index, [In] ref Guid service, [In] ref Guid iid, out IntPtr value);
    int GetPresentationAttribute(uint index, [In] ref Guid attribute, out PropVariant value);
}
