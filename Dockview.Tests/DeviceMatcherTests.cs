using System.Collections.Generic;
using Dockview.Core;
using Dockview.ViewModels;

namespace Dockview.Tests;

public class DeviceMatcherTests
{
    private static VideoCaptureDevice Video(string name) =>
        new() { FriendlyName = name, SymbolicLink = "\\\\?\\usb#test" };

    private static AudioCaptureDevice Audio(string name) =>
        new() { FriendlyName = name, DeviceId = "test-id" };

    [Fact]
    public void NullVideo_ReturnsNull()
    {
        var result = MainViewModel.MatchAudioToVideo(null, [Audio("HDMI (UGREEN 25173)")]);
        Assert.Null(result);
    }

    [Fact]
    public void MatchesBySharedWord_LongerThan3Chars()
    {
        var video = Video("UGREEN 25173");
        var audio = new List<AudioCaptureDevice>
        {
            Audio("Speakers (Realtek)"),
            Audio("HDMI (UGREEN 25173)"),
        };
        var result = MainViewModel.MatchAudioToVideo(video, audio);
        Assert.NotNull(result);
        Assert.Equal("HDMI (UGREEN 25173)", result!.FriendlyName);
    }

    [Fact]
    public void DoesNotMatch_ShortWords()
    {
        // "USB" is 3 chars — ignored. No other shared word.
        var video = Video("USB Cap");
        var audio = new List<AudioCaptureDevice> { Audio("USB Audio") };
        var result = MainViewModel.MatchAudioToVideo(video, audio);
        Assert.Null(result);
    }

    [Fact]
    public void MatchIsCaseInsensitive()
    {
        var video = Video("ugreen capture");
        var audio = new List<AudioCaptureDevice> { Audio("HDMI (UGREEN)") };
        var result = MainViewModel.MatchAudioToVideo(video, audio);
        Assert.NotNull(result);
    }

    [Fact]
    public void EmptyAudioList_ReturnsNull()
    {
        var result = MainViewModel.MatchAudioToVideo(Video("UGREEN 25173"), []);
        Assert.Null(result);
    }
}
