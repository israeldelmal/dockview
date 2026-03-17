using System;
using NAudio.Wave;

namespace Dockview.Core;

/// <summary>
/// A WaveProvider that wraps a source and applies a configurable millisecond delay.
///
/// Sync strategy:
///   • Positive offset (+N ms)  → audio plays N ms LATER than video.
///     Implementation: pre-fill the read buffer with N ms of silence before
///     passing through real samples.  This compensates for audio arriving
///     earlier than video (common with hardware capture cards).
///
///   • Negative offset (−N ms)  → audio plays N ms EARLIER than video.
///     Implementation: discard the first N ms of audio at startup.
///     Use this when video is processing faster than audio.
///
///   • Zero                     → passthrough; no extra latency introduced.
///
/// The offset can be changed at runtime; the buffer will adapt over the next
/// ~100 ms cycle without an audible glitch.
/// </summary>
public sealed class AudioSyncBuffer : IWaveProvider, IDisposable
{
    private readonly IWaveProvider _source;
    private readonly object        _lock = new();

    private int _pendingDelaySamples; // silence still to inject at start of stream
    private int _driftDiscard;        // bytes to silently drop from source (clock running fast)
    private int _driftInject;         // bytes of silence to inject (clock running slow)

    public WaveFormat WaveFormat => _source.WaveFormat;

    public AudioSyncBuffer(IWaveProvider source, int bufferCapacityMs = 600)
    {
        _source = source;
    }

    /// <summary>
    /// Sets the audio-sync offset in milliseconds.  Thread-safe; takes effect
    /// within one read cycle.
    /// </summary>
    public void SetOffsetMs(int ms)
    {
        int bytesPerMs  = WaveFormat.AverageBytesPerSecond / 1000;
        int targetBytes = Math.Abs(ms) * bytesPerMs;
        // Round to block align
        int align       = WaveFormat.BlockAlign;
        targetBytes     = (targetBytes / align) * align;

        lock (_lock)
        {
            if (ms >= 0)
            {
                // Positive: delay audio — pre-fill with silence before passthrough
                _pendingDelaySamples = targetBytes;
            }
            else
            {
                // Negative: advance audio — discard initial bytes from source so it
                // plays earlier relative to video
                _pendingDelaySamples = 0;

                var skip = new byte[Math.Min(targetBytes, 4096)];
                int remaining = targetBytes;
                while (remaining > 0)
                {
                    int toSkip = Math.Min(remaining, skip.Length);
                    int read   = _source.Read(skip, 0, toSkip);
                    if (read == 0) break;
                    remaining -= read;
                }
            }
        }
    }

    /// <summary>Schedules a small discard to correct forward drift (buffer growing).</summary>
    public void DriftDiscard(int bytes) { lock (_lock) _driftDiscard += bytes; }

    /// <summary>Schedules a small silence injection to correct backward drift (buffer shrinking).</summary>
    public void DriftInject(int bytes) { lock (_lock) _driftInject += bytes; }

    public int Read(byte[] buffer, int offset, int count)
    {
        lock (_lock)
        {
            int written = 0;

            // 1. Inject silence for positive-delay pre-roll
            if (_pendingDelaySamples > 0)
            {
                int silenceBytes = Math.Min(_pendingDelaySamples, count);
                Array.Clear(buffer, offset, silenceBytes);
                _pendingDelaySamples -= silenceBytes;
                written              += silenceBytes;
                offset               += silenceBytes;
                count                -= silenceBytes;
            }

            // 2. Drift correction — discard a few source bytes (forward drift)
            if (_driftDiscard > 0)
            {
                int toDiscard = (Math.Min(_driftDiscard, 4096) / WaveFormat.BlockAlign) * WaveFormat.BlockAlign;
                if (toDiscard > 0)
                {
                    var trash = new byte[toDiscard];
                    int discarded = _source.Read(trash, 0, toDiscard);
                    _driftDiscard -= discarded;
                }
            }

            // 3. Drift correction — inject silence (backward drift)
            if (_driftInject > 0 && count > 0)
            {
                int toInject = (Math.Min(_driftInject, count) / WaveFormat.BlockAlign) * WaveFormat.BlockAlign;
                if (toInject > 0)
                {
                    Array.Clear(buffer, offset, toInject);
                    _driftInject -= toInject;
                    written      += toInject;
                    offset       += toInject;
                    count        -= toInject;
                }
            }

            // 4. Pass through from source
            if (count > 0)
            {
                int read = _source.Read(buffer, offset, count);
                written += read;

                // If source returned less than requested, zero-fill the gap
                // to avoid WaveOut glitches
                if (read < count)
                    Array.Clear(buffer, offset + read, count - read);
            }

            return written;
        }
    }

    public void Dispose()
    {
        (_source as IDisposable)?.Dispose();
    }
}
