using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Dockview.Services;

/// <summary>Persists user preferences to %APPDATA%\Dockview\settings.json.</summary>
public sealed class SettingsService
{
    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Dockview",
        "settings.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented        = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public AppSettings Load()
    {
        try
        {
            if (!File.Exists(SettingsPath))
                return new AppSettings();

            var json = File.ReadAllText(SettingsPath);
            return JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
        }
        catch
        {
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            var json = JsonSerializer.Serialize(settings, JsonOptions);
            File.WriteAllText(SettingsPath, json);
        }
        catch
        {
            // Non-fatal: settings will reset next launch
        }
    }
}

public enum RenderPreset
{
    /// <summary>NearestNeighbor scaling, 30 fps render cap. Best for low-end PCs.</summary>
    Performance,
    /// <summary>Linear (bilinear GPU) scaling, 60 fps. Good balance for most PCs.</summary>
    Balanced,
    /// <summary>HighQuality (bicubic CPU) scaling, 60 fps. For high-end PCs only.</summary>
    Quality,
}

public sealed class AppSettings
{
    /// <summary>Symbolic link of the last-used video capture device.</summary>
    public string? LastVideoDeviceSymbolicLink { get; set; }

    /// <summary>WASAPI device ID of the last-used audio capture device.</summary>
    public string? LastAudioDeviceId { get; set; }

    /// <summary>Audio sync offset in milliseconds.  Range: -300 to +300.</summary>
    public int AudioOffsetMs { get; set; } = 0;

    /// <summary>Master volume (0.0 – 1.0).</summary>
    public float Volume { get; set; } = 1.0f;

    /// <summary>Whether the window was last in fullscreen mode.</summary>
    public bool WasFullscreen { get; set; } = false;

    /// <summary>Render quality preset.</summary>
    public RenderPreset RenderPreset { get; set; } = RenderPreset.Balanced;
}
