/*
 * Raw COM vtable dispatch for Media Foundation.
 *
 * Why vtable dispatch instead of [ComImport]?
 * .NET 8's COM RCW wraps every returned pointer in a QueryInterface call.
 * In-process MF objects do NOT support cross-apartment QI, so the cast throws
 * E_NOINTERFACE even on MTA threads.  Calling vtable slots directly bypasses
 * all RCW/QI machinery — exactly what native C++ callers do.
 *
 * Only the interfaces / methods actually used in this project are declared.
 * Vtable slot numbers include the 3 IUnknown slots (QI=0, AddRef=1, Release=2).
 */

using System;
using System.Runtime.InteropServices;

namespace Dockview.Core;

// ── Well-known GUIDs ──────────────────────────────────────────────────────────

internal static class MFGuids
{
    // MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE
    public static readonly Guid SourceType =
        new("C60AC5FE-252A-478F-A0EF-BC8FA5F7CAD3");

    // MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE_VIDCAP_GUID
    public static readonly Guid SourceTypeVideoCapture =
        new("8AC3587A-4AE7-42D8-99E0-0A6013EEF90F");

    // MF_DEVSOURCE_ATTRIBUTE_FRIENDLY_NAME
    public static readonly Guid FriendlyName =
        new("60D0E559-52F8-4FA2-BBCE-ACDB34A8EC01");

    // MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE_VIDCAP_SYMBOLIC_LINK
    public static readonly Guid SymbolicLink =
        new("58F0AAD8-22BF-4F8A-BB3D-D2C4978C6E2F");

    // MF_LOW_LATENCY — disables decoder look-ahead buffering
    public static readonly Guid LowLatency =
        new("9C27891A-ED7A-40E1-88E8-B22727A024EE");

    // MF_SOURCE_READER_ENABLE_VIDEO_PROCESSING — lets the source reader insert
    // a colour-space converter so we can request RGB32 from any native format
    public static readonly Guid EnableVideoProcessing =
        new("FB394F3D-CCF1-42EE-BBB3-F9B845D5681D");

    // MFMediaType_Video
    public static readonly Guid VideoMajorType =
        new("73646976-0000-0010-8000-00AA00389B71");

    // MFVideoFormat_RGB32 (= BGRX 32bpp, matches WPF Bgr32)
    public static readonly Guid VideoFormatRgb32 =
        new("00000016-0000-0010-8000-00AA00389B71");

    // MF_MT_MAJOR_TYPE
    public static readonly Guid MtMajorType =
        new("48EBA18E-F8C9-4687-BF11-0A74C9F96A8F");

    // MF_MT_SUBTYPE
    public static readonly Guid MtSubtype =
        new("F7E34C9A-42E8-4714-B74B-CB29D72C35E5");

    // MF_MT_FRAME_SIZE  (UINT64: hi-DWORD=width, lo-DWORD=height)
    public static readonly Guid MtFrameSize =
        new("1652C33D-D6B2-4012-B834-72030849A37D");

    // First video stream constant for IMFSourceReader
    public const uint FirstVideoStream = 0xFFFFFFFC;
}

// ── Source reader stream flags ────────────────────────────────────────────────

internal enum SourceReaderFlags : uint
{
    None                    = 0,
    EndOfStream             = 0x00000001,
    NewStream               = 0x00000004,
    NativeMediaTypeChanged  = 0x00000010,
    CurrentMediaTypeChanged = 0x00000020,
    StreamTick              = 0x00000100,
    AllEffectsRemoved       = 0x00000200,
}

// ── Direct COM vtable dispatch ────────────────────────────────────────────────

/// <summary>
/// Calls COM vtable methods directly via unmanaged function pointers.
/// No QueryInterface, no RCW — every call reads the vtable and invokes the
/// function pointer, exactly as native C++ does.
/// </summary>
/// <remarks>
/// Slot numbering: IUnknown occupies slots 0-2 (QI, AddRef, Release).
/// Each interface's own methods start at slot 3 and are numbered per their
/// MIDL declaration order in the Windows SDK headers.
/// </remarks>
internal static class MFCom
{
    // ── IUnknown (slots 0-2) ──────────────────────────────────────────────────

    public static unsafe uint Release(IntPtr p)
    {
        if (p == IntPtr.Zero) return 0;
        return ((delegate* unmanaged[Stdcall]<IntPtr, uint>)Slot(p, 2))(p);
    }

    // ── IMFAttributes own methods (slots 3-32) ────────────────────────────────
    //
    //  3 GetItem          12 GetString          21 SetUINT32
    //  4 GetItemType      13 GetAllocatedString  22 SetUINT64
    //  5 CompareItem      14 GetBlobSize         23 SetDouble
    //  6 Compare          15 GetBlob             24 SetGUID
    //  7 GetUINT32        16 GetAllocatedBlob    25 SetString
    //  8 GetUINT64        17 GetUnknown          26 SetBlob
    //  9 GetDouble        18 SetItem             27 SetUnknown
    // 10 GetGUID          19 DeleteItem          28 LockStore
    // 11 GetStringLength  20 DeleteAllItems      29 UnlockStore
    //                                            30 GetCount
    //                                            31 GetItemByIndex
    //                                            32 CopyAllItems

    /// <summary>IMFAttributes::GetUINT64 — slot 8.</summary>
    public static unsafe int GetUINT64(IntPtr p, ref Guid key, out ulong value) =>
        ((delegate* unmanaged[Stdcall]<IntPtr, ref Guid, out ulong, int>)Slot(p, 8))
            (p, ref key, out value);

    /// <summary>IMFAttributes::GetAllocatedString — slot 13. Frees the native buffer internally.</summary>
    public static unsafe int GetAllocatedString(IntPtr p, ref Guid key,
        out string? value, out uint cch)
    {
        var hr = ((delegate* unmanaged[Stdcall]<IntPtr, ref Guid, out IntPtr, out uint, int>)Slot(p, 13))
                     (p, ref key, out IntPtr pStr, out cch);
        if (hr >= 0 && pStr != IntPtr.Zero)
        {
            value = Marshal.PtrToStringUni(pStr);
            Marshal.FreeCoTaskMem(pStr);
        }
        else
        {
            value = null;
        }
        return hr;
    }

    /// <summary>IMFAttributes::SetUINT32 — slot 21.</summary>
    public static unsafe int SetUINT32(IntPtr p, ref Guid key, uint value) =>
        ((delegate* unmanaged[Stdcall]<IntPtr, ref Guid, uint, int>)Slot(p, 21))
            (p, ref key, value);

    /// <summary>IMFAttributes::SetUINT64 — slot 22.</summary>
    public static unsafe int SetUINT64(IntPtr p, ref Guid key, ulong value) =>
        ((delegate* unmanaged[Stdcall]<IntPtr, ref Guid, ulong, int>)Slot(p, 22))
            (p, ref key, value);

    /// <summary>IMFAttributes::SetGUID — slot 24.</summary>
    public static unsafe int SetGUID(IntPtr p, ref Guid key, ref Guid value) =>
        ((delegate* unmanaged[Stdcall]<IntPtr, ref Guid, ref Guid, int>)Slot(p, 24))
            (p, ref key, ref value);

    // ── IMFActivate own methods (slots 33-35, after 33 IMFAttributes slots) ───

    /// <summary>IMFActivate::ActivateObject — slot 33.</summary>
    public static unsafe int ActivateObject(IntPtr p, ref Guid riid, out IntPtr ppv) =>
        ((delegate* unmanaged[Stdcall]<IntPtr, ref Guid, out IntPtr, int>)Slot(p, 33))
            (p, ref riid, out ppv);

    // ── IMFSourceReader own methods (IUnknown = 0-2, own = 3-12) ─────────────
    //
    //  3 GetStreamSelection    7 SetCurrentMediaType   10 GetServiceForStream
    //  4 SetStreamSelection    8 SetCurrentPosition    11 GetPresentationAttribute
    //  5 GetNativeMediaType    9 ReadSample
    //  6 GetCurrentMediaType  10 Flush
    //
    // NOTE: SetCurrentPosition (slot 8) sits between SetCurrentMediaType and
    // ReadSample in the Windows SDK MIDL — easy to miss, fatal to skip.

    /// <summary>IMFSourceReader::GetNativeMediaType — slot 5.</summary>
    public static unsafe int SourceReader_GetNativeMediaType(
        IntPtr p, uint streamIndex, uint typeIndex, out IntPtr ppType) =>
        ((delegate* unmanaged[Stdcall]<IntPtr, uint, uint, out IntPtr, int>)Slot(p, 5))
            (p, streamIndex, typeIndex, out ppType);

    /// <summary>IMFSourceReader::GetCurrentMediaType — slot 6.</summary>
    public static unsafe int SourceReader_GetCurrentMediaType(
        IntPtr p, uint streamIndex, out IntPtr ppType) =>
        ((delegate* unmanaged[Stdcall]<IntPtr, uint, out IntPtr, int>)Slot(p, 6))
            (p, streamIndex, out ppType);

    /// <summary>IMFSourceReader::SetCurrentMediaType — slot 7.</summary>
    public static unsafe int SourceReader_SetCurrentMediaType(
        IntPtr p, uint streamIndex, IntPtr reserved, IntPtr pType) =>
        ((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr, IntPtr, int>)Slot(p, 7))
            (p, streamIndex, reserved, pType);

    /// <summary>IMFSourceReader::ReadSample — slot 9 (slot 8 = SetCurrentPosition).</summary>
    public static unsafe int SourceReader_ReadSample(IntPtr p,
        uint streamIndex, uint flags,
        out uint actualStreamIndex, out uint streamFlags,
        out long timestamp, out IntPtr ppSample) =>
        ((delegate* unmanaged[Stdcall]<IntPtr, uint, uint,
            out uint, out uint, out long, out IntPtr, int>)Slot(p, 9))
            (p, streamIndex, flags,
             out actualStreamIndex, out streamFlags, out timestamp, out ppSample);

    // ── IMFSample own methods (IMFAttributes = 0-32, own = 33-46) ────────────
    //
    // 33 GetSampleFlags   37 GetSampleDuration   41 ConvertToContiguousBuffer
    // 34 SetSampleFlags   38 SetSampleDuration   42 AddBuffer
    // 35 GetSampleTime    39 GetBufferCount       43 RemoveBufferByIndex
    // 36 SetSampleTime    40 GetBufferByIndex     44 RemoveAllBuffers

    /// <summary>IMFSample::ConvertToContiguousBuffer — slot 41.</summary>
    public static unsafe int Sample_ConvertToContiguousBuffer(IntPtr p, out IntPtr ppBuffer) =>
        ((delegate* unmanaged[Stdcall]<IntPtr, out IntPtr, int>)Slot(p, 41))
            (p, out ppBuffer);

    // ── IMFMediaBuffer own methods (IUnknown = 0-2, own = 3-7) ───────────────
    //
    //  3 Lock   4 Unlock   5 GetCurrentLength   6 SetCurrentLength   7 GetMaxLength

    /// <summary>IMFMediaBuffer::Lock — slot 3.</summary>
    public static unsafe int MediaBuffer_Lock(IntPtr p,
        out IntPtr data, out uint maxLen, out uint currentLen) =>
        ((delegate* unmanaged[Stdcall]<IntPtr, out IntPtr, out uint, out uint, int>)Slot(p, 3))
            (p, out data, out maxLen, out currentLen);

    /// <summary>IMFMediaBuffer::Unlock — slot 4.</summary>
    public static unsafe int MediaBuffer_Unlock(IntPtr p) =>
        ((delegate* unmanaged[Stdcall]<IntPtr, int>)Slot(p, 4))(p);

    // ── Private ───────────────────────────────────────────────────────────────

    private static unsafe void* Slot(IntPtr obj, int index)
    {
        // COM object layout: *obj = vtable pointer; vtable[index] = method pointer
        var vtable = *(void**)obj.ToPointer();
        return ((void**)vtable)[index];
    }
}

// ── P/Invoke entry points ─────────────────────────────────────────────────────

internal static class MFNativeMethods
{
    // PreserveSig=false → throws on HRESULT failure; return type becomes void.

    [DllImport("mfplat.dll", PreserveSig = false)]
    public static extern void MFStartup(uint version, uint dwFlags);

    [DllImport("mfplat.dll", PreserveSig = false)]
    public static extern void MFShutdown();

    // Returns raw pointer — no RCW wrapping, no QI.
    [DllImport("mfplat.dll", PreserveSig = false)]
    public static extern void MFCreateAttributes(out IntPtr ppMFAttributes, uint cInitialSize);

    [DllImport("mfplat.dll", PreserveSig = false)]
    public static extern void MFCreateMediaType(out IntPtr ppMFType);

    // MFEnumDeviceSources lives in mf.dll (capture-specific)
    [DllImport("mf.dll", PreserveSig = false)]
    public static extern void MFEnumDeviceSources(
        IntPtr pAttributes,             // IMFAttributes*
        out IntPtr pppSourceActivate,   // IMFActivate**
        out uint pcSourceActivate);

    [DllImport("mfreadwrite.dll", PreserveSig = false)]
    public static extern void MFCreateSourceReaderFromMediaSource(
        IntPtr pMediaSource,            // IMFMediaSource*
        IntPtr pAttributes,             // IMFAttributes* (may be IntPtr.Zero)
        out IntPtr ppSourceReader);     // IMFSourceReader**

    public const uint MF_VERSION       = 0x00020070;
    public const uint MFSTARTUP_NOSOCKET = 1;
}

// ── Managed helpers ───────────────────────────────────────────────────────────

/// <summary>Thin RAII wrapper around an IMFAttributes raw pointer.</summary>
internal sealed class MFAttributesBag : IDisposable
{
    public IntPtr Native { get; }

    public MFAttributesBag(int initialCapacity = 4)
    {
        MFNativeMethods.MFCreateAttributes(out var ptr, (uint)initialCapacity);
        Native = ptr;
    }

    public void SetGuid(Guid key, Guid value) =>
        MFCom.SetGUID(Native, ref key, ref value).ThrowIfFailed();

    public void SetUInt32(Guid key, uint value) =>
        MFCom.SetUINT32(Native, ref key, value).ThrowIfFailed();

    public void SetUInt32Bool(Guid key, bool value) =>
        MFCom.SetUINT32(Native, ref key, value ? 1u : 0u).ThrowIfFailed();

    public void Dispose() => MFCom.Release(Native);
}

/// <summary>Thin RAII wrapper around an IMFMediaType raw pointer.</summary>
internal sealed class MFMediaTypeBag : IDisposable
{
    public IntPtr Native { get; }

    public MFMediaTypeBag()
    {
        MFNativeMethods.MFCreateMediaType(out var ptr);
        Native = ptr;
    }

    public void SetMajorType(Guid type)
    {
        var key = MFGuids.MtMajorType;
        MFCom.SetGUID(Native, ref key, ref type).ThrowIfFailed();
    }

    public void SetSubtype(Guid subtype)
    {
        var key = MFGuids.MtSubtype;
        MFCom.SetGUID(Native, ref key, ref subtype).ThrowIfFailed();
    }

    /// <summary>Packs width+height into the MF_MT_FRAME_SIZE UINT64 attribute.</summary>
    public void SetFrameSize(int width, int height)
    {
        var key = MFGuids.MtFrameSize;
        MFCom.SetUINT64(Native, ref key, ((ulong)width << 32) | (uint)height).ThrowIfFailed();
    }

    public void Dispose() => MFCom.Release(Native);
}

internal static class HResultExtensions
{
    public static void ThrowIfFailed(this int hr)
    {
        if (hr < 0) Marshal.ThrowExceptionForHR(hr);
    }
}
