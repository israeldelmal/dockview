using System;

namespace Dockview.Core;

/// <summary>Video capture device discovered via Media Foundation MFEnumDeviceSources.</summary>
public sealed class VideoCaptureDevice
{
    public string FriendlyName { get; init; } = string.Empty;

    /// <summary>Symbolic link used to re-open this device (e.g. \\?\usb#vid_...).</summary>
    public string SymbolicLink { get; init; } = string.Empty;

    public override string ToString() => FriendlyName;
}

/// <summary>Audio endpoint discovered via Windows MMDevice API (WASAPI).</summary>
public sealed class AudioCaptureDevice
{
    public string FriendlyName { get; init; } = string.Empty;

    /// <summary>WASAPI device ID (e.g. {0.0.1.00000000}.{guid}).</summary>
    public string DeviceId { get; init; } = string.Empty;

    public override string ToString() => FriendlyName;
}

/// <summary>
/// A decoded video frame ready to paint into a WriteableBitmap.
/// The backing array is rented from <see cref="System.Buffers.ArrayPool{T}"/>;
/// the consumer MUST call <see cref="Return"/> exactly once after use to avoid
/// Large Object Heap pressure and Gen2 GC pauses.
/// </summary>
public sealed class VideoFrame
{
    /// <summary>Rented buffer — may be larger than <see cref="DataLength"/>.</summary>
    public byte[] Data { get; }

    /// <summary>Number of valid bytes in <see cref="Data"/> (actual frame size).</summary>
    public int DataLength { get; }

    public int Width { get; }
    public int Height { get; }

    /// <summary>100-nanosecond MF presentation timestamp.</summary>
    public long Timestamp { get; }

    public VideoFrame(byte[] data, int dataLength, int width, int height, long timestamp)
    {
        Data       = data;
        DataLength = dataLength;
        Width      = width;
        Height     = height;
        Timestamp  = timestamp;
    }

    /// <summary>Returns the backing array to the shared pool. Call exactly once.</summary>
    public void Return() => System.Buffers.ArrayPool<byte>.Shared.Return(Data);
}
