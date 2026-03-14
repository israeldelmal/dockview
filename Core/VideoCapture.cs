using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace Dockview.Core;

/// <summary>
/// Captures live video from a UVC device using Media Foundation SourceReader.
///
/// Design notes for low latency:
///   • MF_LOW_LATENCY attribute disables decoder lookahead.
///   • Dedicated background thread calls ReadSample() synchronously.
///   • RGB32 output is requested so WPF WriteableBitmap (Bgr32) can accept
///     the raw bytes without a second colour-space conversion.
///   • Frame-drop logic in the view-model prevents render-thread backpressure.
/// </summary>
public sealed class VideoCapture : IDisposable
{
    // ── Public surface ────────────────────────────────────────────────────────

    public event EventHandler<VideoFrame>? FrameArrived;
    public event EventHandler? DeviceDisconnected;

    public bool IsCapturing => _isCapturing;
    public int FrameWidth  { get; private set; }
    public int FrameHeight { get; private set; }

    // ── Internals ─────────────────────────────────────────────────────────────

    private IntPtr         _reader;          // IMFSourceReader* (raw, no RCW)
    private Thread?        _captureThread;
    private volatile bool  _isCapturing;
    private volatile bool  _disposed;

    // ── API ───────────────────────────────────────────────────────────────────

    public void Start(string symbolicLink)
    {
        if (_isCapturing)
            throw new InvalidOperationException("Already capturing.");

        // CreateSourceReader and the capture loop both run on the same MTA thread.
        // New threads in .NET are MTA by default — this avoids the STA/MTA
        // apartment mismatch that causes QI to return E_NOINTERFACE from the UI thread.
        _isCapturing   = true;
        _captureThread = new Thread(() => CaptureEntryPoint(symbolicLink))
        {
            Name         = "VideoCapture",
            IsBackground = true,
            Priority     = ThreadPriority.AboveNormal,
        };
        _captureThread.Start();
    }

    public void Stop()
    {
        _isCapturing = false;
        _captureThread?.Join(2000);
        _captureThread = null;

        if (_reader != IntPtr.Zero)
        {
            MFCom.Release(_reader);
            _reader = IntPtr.Zero;
        }

        FrameWidth  = 0;
        FrameHeight = 0;
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    /// <summary>Entry point for the capture thread: initialises MF then starts the loop.</summary>
    private void CaptureEntryPoint(string symbolicLink)
    {
        try
        {
            DeviceEnumerator.EnsureMFStarted(); // called from MTA thread — safe
            _reader = CreateSourceReader(symbolicLink);
            CaptureLoop();
        }
        catch (Exception ex)
        {
            _isCapturing = false;
            // Surface as disconnect so the UI shows an error state
            System.Windows.Application.Current?.Dispatcher.InvokeAsync(() =>
                System.Windows.MessageBox.Show(
                    $"Failed to open capture device:\n{ex.Message}",
                    "Dockview", System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Warning));
        }
    }

    private IntPtr CreateSourceReader(string symbolicLink)
    {
        // 1. Find the IMFActivate* matching the symbolic link
        using var enumAttribs = new MFAttributesBag(1);
        enumAttribs.SetGuid(MFGuids.SourceType, MFGuids.SourceTypeVideoCapture);

        MFNativeMethods.MFEnumDeviceSources(enumAttribs.Native, out var ppActivate, out uint count);

        IntPtr target = IntPtr.Zero;
        for (uint i = 0; i < count; i++)
        {
            var ptr = Marshal.ReadIntPtr(ppActivate, (int)(i * IntPtr.Size));
            if (ptr == IntPtr.Zero) continue;

            var linkKey = MFGuids.SymbolicLink;
            MFCom.GetAllocatedString(ptr, ref linkKey, out string? link, out _);

            if (link == symbolicLink)
            {
                target = ptr;
                // Keep this ref — we release it after ActivateObject below
            }
            else
            {
                MFCom.Release(ptr);
            }
        }
        Marshal.FreeCoTaskMem(ppActivate);

        if (target == IntPtr.Zero)
            throw new InvalidOperationException($"Video device not found: {symbolicLink}");

        // 2. Activate the IMFMediaSource
        var mfMediaSourceIid = new Guid("279A808D-AEC7-40C8-9C6B-A6B492C78A66");
        MFCom.ActivateObject(target, ref mfMediaSourceIid, out IntPtr pSource).ThrowIfFailed();
        MFCom.Release(target); // done with the activate object

        // 3. Create SourceReader with low-latency + video processing attributes.
        // EnableVideoProcessing lets MF insert a colour-space converter so we can
        // request RGB32 output even when the device natively sends YUY2 / MJPEG.
        using var readerAttribs = new MFAttributesBag(3);
        readerAttribs.SetUInt32Bool(MFGuids.LowLatency, true);
        readerAttribs.SetUInt32Bool(MFGuids.EnableVideoProcessing, true);

        MFNativeMethods.MFCreateSourceReaderFromMediaSource(pSource, readerAttribs.Native,
            out IntPtr reader);
        MFCom.Release(pSource); // SourceReader holds its own ref to the source

        // 4a. Find and select the native 1920×1080 media type so the device
        //     captures at full HD instead of its default lower resolution.
        IntPtr native1080 = FindNativeType(reader, 1920, 1080);
        if (native1080 != IntPtr.Zero)
        {
            // Ignore failure — device may not support 1080p natively but the
            // video processor can still upscale; we try anyway.
            MFCom.SourceReader_SetCurrentMediaType(reader, MFGuids.FirstVideoStream,
                IntPtr.Zero, native1080);
            MFCom.Release(native1080);
        }

        // 4b. Request RGB32 output at 1920×1080.
        //     The frame size must be set here too — without it the source reader
        //     is free to pick any native resolution and will often default lower.
        //     If 1080p RGB32 fails (device truly doesn't support it), fall back
        //     to RGB32 at whatever resolution the device negotiated.
        using var mt = new MFMediaTypeBag();
        mt.SetMajorType(MFGuids.VideoMajorType);
        mt.SetSubtype(MFGuids.VideoFormatRgb32);
        mt.SetFrameSize(1920, 1080);
        var hrSet = MFCom.SourceReader_SetCurrentMediaType(reader,
            MFGuids.FirstVideoStream, IntPtr.Zero, mt.Native);
        if (hrSet < 0)
        {
            // Fallback: RGB32 at device default resolution
            using var mtFallback = new MFMediaTypeBag();
            mtFallback.SetMajorType(MFGuids.VideoMajorType);
            mtFallback.SetSubtype(MFGuids.VideoFormatRgb32);
            MFCom.SourceReader_SetCurrentMediaType(reader,
                MFGuids.FirstVideoStream, IntPtr.Zero, mtFallback.Native).ThrowIfFailed();
        }

        // 5. Read back negotiated frame dimensions
        MFCom.SourceReader_GetCurrentMediaType(reader, MFGuids.FirstVideoStream,
            out IntPtr negotiated).ThrowIfFailed();
        try
        {
            var sizeKey = MFGuids.MtFrameSize;
            MFCom.GetUINT64(negotiated, ref sizeKey, out ulong packed).ThrowIfFailed();
            FrameWidth  = (int)(packed >> 32);
            FrameHeight = (int)(packed & 0xFFFF_FFFF);
        }
        finally
        {
            MFCom.Release(negotiated);
        }

        return reader;
    }

    // ── Capture loop ──────────────────────────────────────────────────────────

    private void CaptureLoop()
    {
        while (_isCapturing && !_disposed)
        {
            try
            {
                var hr = MFCom.SourceReader_ReadSample(
                    _reader,
                    MFGuids.FirstVideoStream,
                    0, // MF_SOURCE_READER_CONTROL_FLAG_NONE
                    out _,
                    out uint flags,
                    out long timestamp,
                    out IntPtr samplePtr);

                if (hr < 0)
                {
                    // Device removed (0xC00D36B1 = MF_E_HARDWARE_MFT_FAILED or similar)
                    HandleDisconnect();
                    return;
                }

                if ((flags & (uint)SourceReaderFlags.EndOfStream) != 0)
                    break;

                if ((flags & (uint)SourceReaderFlags.StreamTick) != 0)
                {
                    // No signal / gap — continue polling
                    samplePtr = IntPtr.Zero;
                    Thread.Sleep(30);
                }

                if (samplePtr == IntPtr.Zero) continue;

                try
                {
                    ProcessSample(samplePtr, timestamp);
                }
                finally
                {
                    MFCom.Release(samplePtr);
                }
            }
            catch
            {
                if (_isCapturing)
                    HandleDisconnect();
                return;
            }
        }
    }

    private void ProcessSample(IntPtr samplePtr, long timestamp)
    {
        MFCom.Sample_ConvertToContiguousBuffer(samplePtr, out IntPtr buffer).ThrowIfFailed();
        try
        {
            MFCom.MediaBuffer_Lock(buffer, out IntPtr ptr, out _, out uint currentLen)
                 .ThrowIfFailed();
            try
            {
                // Rent from the shared pool to avoid allocating ~8 MB on the LOH
                // every frame (which causes Gen2 GC pauses and visible stutter).
                // The consumer (ViewModel) must call frame.Return() after use.
                var frameData = System.Buffers.ArrayPool<byte>.Shared.Rent((int)currentLen);
                Marshal.Copy(ptr, frameData, 0, (int)currentLen);
                FrameArrived?.Invoke(this,
                    new VideoFrame(frameData, (int)currentLen, FrameWidth, FrameHeight, timestamp));
            }
            finally
            {
                MFCom.MediaBuffer_Unlock(buffer);
            }
        }
        finally
        {
            MFCom.Release(buffer);
        }
    }

    /// <summary>
    /// Iterates the device's native media types and returns the first one whose
    /// frame size matches <paramref name="width"/>×<paramref name="height"/>.
    /// The caller is responsible for calling <see cref="MFCom.Release"/> on the
    /// returned pointer (if non-zero).
    /// </summary>
    private static IntPtr FindNativeType(IntPtr reader, int width, int height)
    {
        const int MF_E_NO_MORE_TYPES = unchecked((int)0xC00D36B9);

        for (uint i = 0; ; i++)
        {
            var hr = MFCom.SourceReader_GetNativeMediaType(
                reader, MFGuids.FirstVideoStream, i, out IntPtr pType);

            if (hr == MF_E_NO_MORE_TYPES || hr < 0)
                break;

            var sizeKey = MFGuids.MtFrameSize;
            if (MFCom.GetUINT64(pType, ref sizeKey, out ulong packed) >= 0)
            {
                int w = (int)(packed >> 32);
                int h = (int)(packed & 0xFFFF_FFFF);
                if (w == width && h == height)
                    return pType; // caller releases
            }

            MFCom.Release(pType);
        }

        return IntPtr.Zero;
    }

    private void HandleDisconnect()
    {
        _isCapturing = false;
        DeviceDisconnected?.Invoke(this, EventArgs.Empty);
    }

    // ── IDisposable ───────────────────────────────────────────────────────────

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }
}
