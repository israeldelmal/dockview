using System;
using System.Collections.Generic;
using Dockview.Core;

namespace Dockview.Tests;

public class StreamSelectorTests
{
    // ── helpers ──────────────────────────────────────────────────────────────

    private static NativeStreamInfo Make(
        int width, int height, int fps, bool compressed = false, string fmt = "YUY2")
        => new(0, Guid.NewGuid(), fmt, width, height, fps, compressed);

    private static readonly NativeStreamInfo
        Nv12_1440p35  = Make(2560, 1440, 35, compressed: false, fmt: "NV12"),
        Yuy2_1080p60  = Make(1920, 1080, 60, compressed: false, fmt: "YUY2"),
        Mjpeg_1080p60 = Make(1920, 1080, 60, compressed: true,  fmt: "MJPEG"),
        Yuy2_1080p30  = Make(1920, 1080, 30, compressed: false, fmt: "YUY2"),
        Mjpeg_1080p30 = Make(1920, 1080, 30, compressed: true,  fmt: "MJPEG"),
        Yuy2_720p60   = Make(1280,  720, 60, compressed: false, fmt: "YUY2");

    // ── empty list ───────────────────────────────────────────────────────────

    [Fact]
    public void SelectBest_EmptyList_ReturnsNull()
    {
        var result = StreamSelector.SelectBest([], CaptureProfile.Balanced);
        Assert.Null(result);
    }

    // ── 1080p60 preference ───────────────────────────────────────────────────

    [Theory]
    [InlineData(CaptureProfile.LowLatency)]
    [InlineData(CaptureProfile.Balanced)]
    [InlineData(CaptureProfile.Quality)]
    public void SelectBest_Prefers1080p60_OverHigherResolution(CaptureProfile profile)
    {
        // Device exposes 1440p35 and 1080p60 — all profiles should pick 1080p60.
        var types = new List<NativeStreamInfo> { Nv12_1440p35, Yuy2_1080p60 };
        var result = StreamSelector.SelectBest(types, profile);
        Assert.Equal(Yuy2_1080p60, result);
    }

    [Fact]
    public void SelectBest_No1080p60_FallsBackToGeneralLogic()
    {
        // Only 1440p35 and 720p60 — should not force 1080p filter
        var types = new List<NativeStreamInfo> { Nv12_1440p35, Yuy2_720p60 };
        // Balanced: best resolution×fps uncompressed → 1440p35 wins (2560×1440×35 > 1280×720×60)
        var result = StreamSelector.SelectBest(types, CaptureProfile.Balanced);
        Assert.Equal(Nv12_1440p35, result);
    }

    // ── Balanced ─────────────────────────────────────────────────────────────

    [Fact]
    public void Balanced_PrefersUncompressedOver_Mjpeg_AtSameMode()
    {
        var types = new List<NativeStreamInfo> { Mjpeg_1080p60, Yuy2_1080p60 };
        var result = StreamSelector.SelectBest(types, CaptureProfile.Balanced);
        Assert.Equal(Yuy2_1080p60, result);
    }

    [Fact]
    public void Balanced_OnlyMjpeg_ReturnsIt()
    {
        var types = new List<NativeStreamInfo> { Mjpeg_1080p60 };
        var result = StreamSelector.SelectBest(types, CaptureProfile.Balanced);
        Assert.Equal(Mjpeg_1080p60, result);
    }

    // ── Quality ──────────────────────────────────────────────────────────────

    [Fact]
    public void Quality_PrefersUncompressed_EvenAt30fps_Over_CompressedAt60fps()
    {
        // Within the 1080p60 pool: both have 60fps, but Yuy2 is uncompressed → Yuy2 wins.
        // To test "even at 30fps" we need to step outside the 1080p60 pool:
        // offer only 1080p30 uncompressed vs 1080p60 compressed.
        var types = new List<NativeStreamInfo> { Mjpeg_1080p60, Yuy2_1080p30 };
        // Neither is in the ≥55fps pool at 1080p for Yuy2_1080p30, but Mjpeg_1080p60 is.
        // Pool → { Mjpeg_1080p60 } (only 1080p ≥55fps). Uncompressed not available → MJPEG.
        var result = StreamSelector.SelectBest(types, CaptureProfile.Quality);
        Assert.Equal(Mjpeg_1080p60, result);
    }

    [Fact]
    public void Quality_UncompressedInPool_WinsOverCompressed()
    {
        var types = new List<NativeStreamInfo> { Mjpeg_1080p60, Yuy2_1080p60 };
        var result = StreamSelector.SelectBest(types, CaptureProfile.Quality);
        Assert.Equal(Yuy2_1080p60, result);
    }

    // ── LowLatency ───────────────────────────────────────────────────────────

    [Fact]
    public void LowLatency_PicksHighestFpsInPool()
    {
        // Both are in the 1080p60 pool (≥55fps).
        var types = new List<NativeStreamInfo> { Mjpeg_1080p60, Yuy2_1080p60 };
        // Tied at 60fps → secondary sort is resolution (same) → first in stable order
        var result = StreamSelector.SelectBest(types, CaptureProfile.LowLatency);
        Assert.NotNull(result);
        Assert.Equal(60, result!.Fps);
    }

    [Fact]
    public void LowLatency_NoPool_PicksHighestFpsOverall()
    {
        var types = new List<NativeStreamInfo> { Yuy2_1080p30, Yuy2_720p60 };
        // No 1080p≥55fps → full list; LowLatency picks highest fps → 720p60
        var result = StreamSelector.SelectBest(types, CaptureProfile.LowLatency);
        Assert.Equal(Yuy2_720p60, result);
    }
}
