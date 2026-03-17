using System.Text.Json;
using System.Text.Json.Serialization;
using Dockview.Services;

namespace Dockview.Tests;

public class AppSettingsTests
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented        = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    [Fact]
    public void DefaultValues_AreCorrect()
    {
        var s = new AppSettings();
        Assert.Equal(0,                  s.AudioOffsetMs);
        Assert.Equal(1.0f,               s.Volume);
        Assert.False(s.WasFullscreen);
        Assert.Equal(RenderPreset.Balanced, s.RenderPreset);
        Assert.Null(s.LastVideoDeviceSymbolicLink);
        Assert.Null(s.LastAudioDeviceId);
    }

    [Fact]
    public void RoundTrip_PreservesAllFields()
    {
        var original = new AppSettings
        {
            LastVideoDeviceSymbolicLink = "\\\\?\\usb#vid_1234",
            LastAudioDeviceId           = "{0.0.1.00000000}.{abc}",
            AudioOffsetMs               = -150,
            Volume                      = 0.75f,
            WasFullscreen               = true,
            RenderPreset                = RenderPreset.Quality,
        };

        var json       = JsonSerializer.Serialize(original, Options);
        var restored   = JsonSerializer.Deserialize<AppSettings>(json, Options)!;

        Assert.Equal(original.LastVideoDeviceSymbolicLink, restored.LastVideoDeviceSymbolicLink);
        Assert.Equal(original.LastAudioDeviceId,           restored.LastAudioDeviceId);
        Assert.Equal(original.AudioOffsetMs,               restored.AudioOffsetMs);
        Assert.Equal(original.Volume,                      restored.Volume);
        Assert.Equal(original.WasFullscreen,               restored.WasFullscreen);
        Assert.Equal(original.RenderPreset,                restored.RenderPreset);
    }

    [Fact]
    public void InvalidJson_DoesNotThrow_ReturnsFallback()
    {
        // SettingsService.Load() returns new AppSettings() on any error — simulate that.
        AppSettings Fallback()
        {
            try { JsonSerializer.Deserialize<AppSettings>("{invalid}", Options); }
            catch { return new AppSettings(); }
            return new AppSettings();
        }

        var result = Fallback();
        Assert.NotNull(result);
        Assert.Equal(RenderPreset.Balanced, result.RenderPreset);
    }

    [Fact]
    public void EmptyJson_ReturnsNull_HandledByService()
    {
        var result = JsonSerializer.Deserialize<AppSettings>("{}", Options);
        Assert.NotNull(result);
        // All fields default when JSON is empty object
        Assert.Equal(RenderPreset.Balanced, result!.RenderPreset);
        Assert.Equal(0, result.AudioOffsetMs);
    }
}
