using System;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Dockview.Core;

/// <summary>
/// Captures audio from a WASAPI endpoint (UAC capture card mic) and routes it
/// to the default system output device with an adjustable sync offset.
///
/// Pipeline:
///   WasapiCapture  →  BufferedWaveProvider  →  AudioSyncBuffer  →  WasapiOut
///
/// The BufferedWaveProvider decouples capture timing from playback timing,
/// allowing the sync buffer to inject or discard samples without starving
/// the output device.
/// </summary>
public sealed class AudioEngine : IDisposable
{
    // ── Events ────────────────────────────────────────────────────────────────

    public event EventHandler? DeviceDisconnected;

    // ── State ─────────────────────────────────────────────────────────────────

    private WasapiCapture?        _capture;
    private WasapiOut?            _output;
    private BufferedWaveProvider? _waveBuffer;
    private AudioSyncBuffer?      _syncBuffer;
    private System.Threading.Timer? _driftTimer;

    private volatile bool _isRunning;
    private volatile bool _disposed;

    private float _volume = 1.0f;
    private int   _offsetMs;

    // ── Public API ────────────────────────────────────────────────────────────

    public bool IsRunning => _isRunning;

    public float Volume
    {
        get => _volume;
        set
        {
            _volume = Math.Clamp(value, 0f, 1f);
            if (_output is not null)
                _output.Volume = _volume;
        }
    }

    /// <summary>
    /// Audio-sync offset in milliseconds.
    /// Positive = delay audio (audio plays later).
    /// Negative = advance audio (audio plays earlier).
    /// </summary>
    public int OffsetMs
    {
        get => _offsetMs;
        set
        {
            _offsetMs = value;
            _syncBuffer?.SetOffsetMs(value);
        }
    }

    /// <summary>Starts the audio pipeline for the given WASAPI device ID.</summary>
    public void Start(string deviceId)
    {
        if (_isRunning)
            throw new InvalidOperationException("Already running.");

        try
        {
            // 1. Open the capture endpoint
            using var enumerator = new MMDeviceEnumerator();
            var captureDevice = enumerator.GetDevice(deviceId);

            _capture = new WasapiCapture(captureDevice)
            {
                // Shared-mode WASAPI; 10 ms buffer = minimal latency
                ShareMode = AudioClientShareMode.Shared,
            };

            var captureFormat = _capture.WaveFormat;

            // 2. Wire up the pipeline
            //    Buffer capacity = 500 ms to absorb jitter without drop-outs
            _waveBuffer = new BufferedWaveProvider(captureFormat)
            {
                BufferDuration      = TimeSpan.FromMilliseconds(500),
                DiscardOnBufferOverflow = true,
            };

            _syncBuffer = new AudioSyncBuffer(_waveBuffer);
            _syncBuffer.SetOffsetMs(_offsetMs);

            // 3. Open the default render endpoint (40 ms shared-mode latency)
            _output = new WasapiOut(AudioClientShareMode.Shared, 40);
            _output.Init(_syncBuffer);
            _output.Volume = _volume;

            // 4. Wire capture → buffer
            _capture.DataAvailable    += OnDataAvailable;
            _capture.RecordingStopped += OnRecordingStopped;

            // 5. Start in order: output first so it's ready before capture delivers data
            _output.Play();
            _capture.StartRecording();

            _isRunning = true;

            // Drift correction: check every 5 s, skip the first 3 s to let
            // the buffer stabilise after startup.
            _driftTimer = new System.Threading.Timer(
                OnDriftCheck, null,
                TimeSpan.FromSeconds(3),
                TimeSpan.FromSeconds(5));
        }
        catch
        {
            StopAndCleanup();
            throw;
        }
    }

    public void Stop()
    {
        _isRunning = false;
        StopAndCleanup();
    }

    // ── Event handlers ────────────────────────────────────────────────────────

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (_waveBuffer is not null && e.BytesRecorded > 0)
            _waveBuffer.AddSamples(e.Buffer, 0, e.BytesRecorded);
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        if (_isRunning && !_disposed)
        {
            _isRunning = false;
            DeviceDisconnected?.Invoke(this, EventArgs.Empty);
        }
    }

    // ── Drift correction ──────────────────────────────────────────────────────

    private void OnDriftCheck(object? state)
    {
        if (_waveBuffer is null || _syncBuffer is null || !_isRunning) return;

        var fmt        = _waveBuffer.WaveFormat;
        int bytesPerMs = fmt.AverageBytesPerSecond / 1000;

        // Steady-state target: keep ~50 ms in the buffer as a cushion
        int target   = bytesPerMs * 50;
        int buffered = _waveBuffer.BufferedBytes;
        int delta    = buffered - target;

        // Dead zone: ±15 ms — ignore small natural fluctuations
        int deadZone = bytesPerMs * 15;
        if (Math.Abs(delta) < deadZone) return;

        // Correct 1 ms per check — completely inaudible
        int correction = (bytesPerMs / fmt.BlockAlign) * fmt.BlockAlign;
        if (correction <= 0) return;

        if (delta > deadZone)
            _syncBuffer.DriftDiscard(correction); // buffer growing → drop 1 ms
        else
            _syncBuffer.DriftInject(correction);  // buffer shrinking → add 1 ms silence
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private void StopAndCleanup()
    {
        _driftTimer?.Dispose();
        _driftTimer = null;

        _capture?.StopRecording();
        _output?.Stop();

        _capture?.Dispose();
        _output?.Dispose();
        _syncBuffer?.Dispose();

        _capture    = null;
        _output     = null;
        _waveBuffer = null;
        _syncBuffer = null;
    }

    // ── IDisposable ───────────────────────────────────────────────────────────

    public void Dispose()
    {
        if (_disposed) return;
        _disposed  = true;
        _isRunning = false;
        StopAndCleanup();
    }
}
