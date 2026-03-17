using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Dockview.Core;
using Dockview.Services;

namespace Dockview.ViewModels;

/// <summary>
/// Central view-model that owns the capture pipeline and exposes all bindable
/// state to MainWindow.  The video and audio engines run on their own threads;
/// this VM marshals events back to the UI dispatcher.
/// </summary>
public sealed class MainViewModel : ViewModelBase, IDisposable
{
    // ── Dependencies ──────────────────────────────────────────────────────────

    private readonly SettingsService _settings;
    private readonly VideoCapture    _videoCapture = new();
    private readonly AudioEngine     _audioEngine  = new();

    // ── Device lists ──────────────────────────────────────────────────────────

    public ObservableCollection<VideoCaptureDevice> VideoDevices { get; } = new();
    public ObservableCollection<AudioCaptureDevice> AudioDevices { get; } = new();

    // ── Selected devices ──────────────────────────────────────────────────────

    private VideoCaptureDevice? _selectedVideoDevice;
    public VideoCaptureDevice? SelectedVideoDevice
    {
        get => _selectedVideoDevice;
        set
        {
            if (Set(ref _selectedVideoDevice, value))
                ApplyVideoDevice();
        }
    }

    private AudioCaptureDevice? _selectedAudioDevice;
    public AudioCaptureDevice? SelectedAudioDevice
    {
        get => _selectedAudioDevice;
        set
        {
            if (Set(ref _selectedAudioDevice, value))
                ApplyAudioDevice();
        }
    }

    // ── Video preview ─────────────────────────────────────────────────────────

    /// <summary>The WPF bitmap displayed in the VideoPreview Image control.</summary>
    public WriteableBitmap? PreviewBitmap
    {
        get => _previewBitmap;
        private set => Set(ref _previewBitmap, value);
    }
    private WriteableBitmap? _previewBitmap;

    // Latest frame ready to render — swapped atomically by the capture thread,
    // consumed once per VSync by CompositionTarget.Rendering on the UI thread.
    private VideoFrame? _latestFrame;
    private readonly object _frameLock = new();
    private int _vsyncCount;

    // ── Status ────────────────────────────────────────────────────────────────

    private string _statusText = "Select a capture device to begin";
    public string StatusText
    {
        get => _statusText;
        private set => Set(ref _statusText, value);
    }

    private bool _isCapturing;
    public bool IsCapturing
    {
        get => _isCapturing;
        private set => Set(ref _isCapturing, value);
    }

    // ── FPS / stream info ─────────────────────────────────────────────────────

    private string _fpsText = string.Empty;
    public string FpsText
    {
        get => _fpsText;
        private set => Set(ref _fpsText, value);
    }

    private string _streamInfoText = string.Empty;
    public string StreamInfoText
    {
        get => _streamInfoText;
        private set => Set(ref _streamInfoText, value);
    }

    private int _frameCounter;
    private readonly DispatcherTimer _fpsTimer;

    // ── Render preset ─────────────────────────────────────────────────────────

    private Dockview.Services.RenderPreset _renderPreset = Dockview.Services.RenderPreset.Balanced;
    public Dockview.Services.RenderPreset RenderPreset
    {
        get => _renderPreset;
        set
        {
            if (Set(ref _renderPreset, value))
            {
                Notify(nameof(BitmapScalingMode));
                Notify(nameof(PresetLabel));
            }
        }
    }

    /// <summary>Cycles through Performance → Balanced → Quality → Performance.</summary>
    public void CyclePreset()
    {
        RenderPreset = RenderPreset switch
        {
            Dockview.Services.RenderPreset.Performance => Dockview.Services.RenderPreset.Balanced,
            Dockview.Services.RenderPreset.Balanced    => Dockview.Services.RenderPreset.Quality,
            _                                          => Dockview.Services.RenderPreset.Performance,
        };

        // Restart video capture so the new capture profile takes effect
        if (_isCapturing)
            RestartVideoOnly();
    }

    private Core.CaptureProfile GetCaptureProfile() => _renderPreset switch
    {
        Dockview.Services.RenderPreset.Performance => Core.CaptureProfile.LowLatency,
        Dockview.Services.RenderPreset.Quality     => Core.CaptureProfile.Quality,
        _                                          => Core.CaptureProfile.Balanced,
    };

    public System.Windows.Media.BitmapScalingMode BitmapScalingMode => _renderPreset switch
    {
        Dockview.Services.RenderPreset.Performance => System.Windows.Media.BitmapScalingMode.NearestNeighbor,
        Dockview.Services.RenderPreset.Quality     => System.Windows.Media.BitmapScalingMode.HighQuality,
        _                                          => System.Windows.Media.BitmapScalingMode.Linear,
    };

    public string PresetLabel => _renderPreset switch
    {
        Dockview.Services.RenderPreset.Performance => "⚡",
        Dockview.Services.RenderPreset.Quality     => "◈",
        _                                          => "◇",
    };

    // ── Audio controls ────────────────────────────────────────────────────────

    private double _volume = 1.0;
    public double Volume
    {
        get => _volume;
        set
        {
            if (Set(ref _volume, value))
                _audioEngine.Volume = (float)value;
        }
    }

    private int _audioOffsetMs;
    public int AudioOffsetMs
    {
        get => _audioOffsetMs;
        set
        {
            if (Set(ref _audioOffsetMs, value))
            {
                _audioEngine.OffsetMs = value;
                Notify(nameof(AudioOffsetLabel));
            }
        }
    }

    public string AudioOffsetLabel => AudioOffsetMs == 0
        ? "0 ms"
        : (AudioOffsetMs > 0 ? $"+{AudioOffsetMs} ms" : $"{AudioOffsetMs} ms");

    // ── Commands ──────────────────────────────────────────────────────────────

    public RelayCommand RefreshDevicesCommand { get; }

    // ── Constructor ───────────────────────────────────────────────────────────

    public MainViewModel()
    {
        _settings = new SettingsService();

        RefreshDevicesCommand = new RelayCommand(RefreshDevices);

        _fpsTimer = new DispatcherTimer(TimeSpan.FromSeconds(1),
            DispatcherPriority.Background,
            OnFpsTimer,
            Application.Current.Dispatcher);
        _fpsTimer.Start();

        // Subscribe to VSync — renders the latest available frame each display refresh
        System.Windows.Media.CompositionTarget.Rendering += OnVSync;

        // Wire engine events (will fire on background threads)
        _videoCapture.FrameArrived      += OnFrameArrived;
        _videoCapture.DeviceDisconnected += OnVideoDeviceDisconnected;
        _audioEngine.DeviceDisconnected  += OnAudioDeviceDisconnected;

        // Load saved settings, then enumerate devices
        var saved = _settings.Load();
        _audioOffsetMs = saved.AudioOffsetMs;
        _volume        = saved.Volume;
        _renderPreset  = saved.RenderPreset;
        _audioEngine.OffsetMs = _audioOffsetMs;
        _audioEngine.Volume   = (float)_volume;

        RefreshDevices(saved);
    }

    // ── Device management ─────────────────────────────────────────────────────

    private void RefreshDevices() => RefreshDevices(null);

    private void RefreshDevices(AppSettings? saved)
    {
        // Stop active capture before changing devices
        StopCapture();

        VideoDevices.Clear();
        AudioDevices.Clear();

        foreach (var v in DeviceEnumerator.GetVideoDevices())
            VideoDevices.Add(v);

        foreach (var a in DeviceEnumerator.GetAudioDevices())
            AudioDevices.Add(a);

        if (saved != null)
        {
            // Restore last selections
            var lastVideo = VideoDevices.FirstOrDefault(
                d => d.SymbolicLink == saved.LastVideoDeviceSymbolicLink);
            var lastAudio = AudioDevices.FirstOrDefault(
                d => d.DeviceId == saved.LastAudioDeviceId);

            // Set backing fields directly to avoid double-start during init
            _selectedVideoDevice = lastVideo ?? VideoDevices.FirstOrDefault();
            _selectedAudioDevice = lastAudio
                ?? MatchAudioToVideo(_selectedVideoDevice)
                ?? AudioDevices.FirstOrDefault();

            Notify(nameof(SelectedVideoDevice));
            Notify(nameof(SelectedAudioDevice));
        }
        else
        {
            _selectedVideoDevice = VideoDevices.FirstOrDefault();
            _selectedAudioDevice = MatchAudioToVideo(_selectedVideoDevice)
                ?? AudioDevices.FirstOrDefault();

            Notify(nameof(SelectedVideoDevice));
            Notify(nameof(SelectedAudioDevice));
        }

        // Show diagnostic info when no video devices found
        if (VideoDevices.Count == 0)
        {
            var error = DeviceEnumerator.LastVideoEnumerationError;
            StatusText = error != null
                ? $"MF error: {error}"
                : "No video capture devices found — check Device Manager";
        }

        // Auto-start if devices are available
        if (_selectedVideoDevice != null)
            StartCapture();
    }

    /// <summary>
    /// Tries to find an audio capture device whose name shares a significant word
    /// with the given video device (e.g. both contain "UGREEN").
    /// Returns null if no match is found.
    /// </summary>
    private AudioCaptureDevice? MatchAudioToVideo(VideoCaptureDevice? video)
    {
        if (video is null || AudioDevices.Count == 0) return null;

        var words = video.FriendlyName
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length > 3)
            .ToArray();

        return AudioDevices.FirstOrDefault(a =>
            words.Any(w => a.FriendlyName.Contains(w, StringComparison.OrdinalIgnoreCase)));
    }

    private void ApplyVideoDevice()
    {
        StopCapture();
        if (_selectedVideoDevice != null)
            StartCapture();
    }

    private void ApplyAudioDevice()
    {
        _audioEngine.Stop();
        if (_selectedAudioDevice != null)
        {
            try   { _audioEngine.Start(_selectedAudioDevice.DeviceId); }
            catch { /* audio failure is non-fatal */ }
        }
    }

    // ── Capture lifecycle ─────────────────────────────────────────────────────

    private void StartCapture()
    {
        if (_selectedVideoDevice is null) return;

        try
        {
            _videoCapture.Start(_selectedVideoDevice.SymbolicLink, GetCaptureProfile());
            IsCapturing = true;
            StatusText  = "Live";
        }
        catch (Exception ex)
        {
            StatusText = $"Failed to open device: {ex.Message}";
        }

        // Start audio independently — failure should not block video
        if (_selectedAudioDevice != null)
        {
            try   { _audioEngine.Start(_selectedAudioDevice.DeviceId); }
            catch { /* audio failure is non-fatal */ }
        }
    }

    /// <summary>Restarts only the video capture (e.g. after a preset change). Audio is unaffected.</summary>
    private void RestartVideoOnly()
    {
        if (_selectedVideoDevice is null) return;
        _videoCapture.Stop();
        StreamInfoText = string.Empty;
        try
        {
            _videoCapture.Start(_selectedVideoDevice.SymbolicLink, GetCaptureProfile());
        }
        catch (Exception ex)
        {
            StatusText = $"Failed to reopen device: {ex.Message}";
        }
    }

    private void StopCapture()
    {
        if (!IsCapturing) return;

        _videoCapture.Stop();
        _audioEngine.Stop();

        IsCapturing = false;
        StatusText  = "Stopped";
        PreviewBitmap = null;
    }

    // ── Frame rendering ───────────────────────────────────────────────────────

    // ── VSync render ──────────────────────────────────────────────────────────

    /// <summary>
    /// Called by WPF on the UI thread once per display refresh (VSync).
    /// Takes the latest frame deposited by the capture thread and renders it.
    /// This replaces per-frame InvokeAsync dispatches and syncs naturally to
    /// the monitor refresh rate — no artificial frame dropping needed.
    /// </summary>
    private void OnVSync(object? sender, EventArgs e)
    {
        // Performance preset: render every 2nd VSync → effective 30 fps on 60 Hz.
        // This halves GPU/CPU render cost on lower-end machines.
        _vsyncCount++;
        if (_renderPreset == Dockview.Services.RenderPreset.Performance && (_vsyncCount & 1) != 0)
            return;

        VideoFrame? frame;
        lock (_frameLock)
        {
            frame = _latestFrame;
            _latestFrame = null;
        }
        if (frame is null) return;

        try   { RenderFrame(frame); }
        finally { frame.Return(); }
    }

    private void OnFpsTimer(object? sender, EventArgs e)
    {
        int fps = System.Threading.Interlocked.Exchange(ref _frameCounter, 0);
        if (_isCapturing && _previewBitmap is not null)
        {
            FpsText = $"{fps} fps · {_previewBitmap.PixelWidth}×{_previewBitmap.PixelHeight}";

            var si = _videoCapture.ActiveStreamInfo;
            StreamInfoText = si is not null
                ? $"{si.FormatName}{(si.IsCompressed ? " · compressed" : " · raw")}"
                : string.Empty;
        }
        else
        {
            FpsText        = string.Empty;
            StreamInfoText = string.Empty;
        }
    }

    private void OnFrameArrived(object? sender, VideoFrame frame)
    {
        System.Threading.Interlocked.Increment(ref _frameCounter);

        // Swap in the new frame; if a previous unrendered frame was waiting,
        // return its buffer immediately — VSync will pick up the newer one.
        VideoFrame? displaced;
        lock (_frameLock)
        {
            displaced   = _latestFrame;
            _latestFrame = frame;
        }
        displaced?.Return();
    }

    private void RenderFrame(VideoFrame frame)
    {
        // (Re)allocate WriteableBitmap when dimensions change (e.g. first frame, resolution switch)
        if (PreviewBitmap is null
            || PreviewBitmap.PixelWidth  != frame.Width
            || PreviewBitmap.PixelHeight != frame.Height)
        {
            PreviewBitmap = new WriteableBitmap(
                frame.Width, frame.Height,
                96, 96,
                PixelFormats.Bgr32,  // matches MFVideoFormat_RGB32 byte order (BGRX)
                null);
        }

        PreviewBitmap.Lock();
        try
        {
            unsafe
            {
                fixed (byte* src = frame.Data)
                {
                    Buffer.MemoryCopy(
                        src,
                        (void*)PreviewBitmap.BackBuffer,
                        frame.DataLength,
                        frame.DataLength);
                }
            }
            PreviewBitmap.AddDirtyRect(new Int32Rect(0, 0, frame.Width, frame.Height));
        }
        finally
        {
            PreviewBitmap.Unlock();
        }
    }

    // ── Disconnect handlers ───────────────────────────────────────────────────

    private void OnVideoDeviceDisconnected(object? sender, EventArgs e)
    {
        Application.Current?.Dispatcher.InvokeAsync(() =>
        {
            IsCapturing   = false;
            PreviewBitmap = null;
            StatusText    = "No HDMI Signal — reconnect the capture device";
        });
    }

    private void OnAudioDeviceDisconnected(object? sender, EventArgs e)
    {
        // Audio can silently fail; optionally show in status
    }

    // ── Settings persistence ──────────────────────────────────────────────────

    public void SaveSettings()
    {
        _settings.Save(new AppSettings
        {
            LastVideoDeviceSymbolicLink = _selectedVideoDevice?.SymbolicLink,
            LastAudioDeviceId           = _selectedAudioDevice?.DeviceId,
            AudioOffsetMs               = _audioOffsetMs,
            Volume                      = (float)_volume,
            RenderPreset                = _renderPreset,
        });
    }

    // ── IDisposable ───────────────────────────────────────────────────────────

    public void Dispose()
    {
        System.Windows.Media.CompositionTarget.Rendering -= OnVSync;
        _fpsTimer.Stop();
        SaveSettings();
        _videoCapture.Dispose();
        _audioEngine.Dispose();
    }
}
