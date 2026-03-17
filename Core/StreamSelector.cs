using System;
using System.Collections.Generic;
using System.Linq;

namespace Dockview.Core;

/// <summary>
/// Pure selection logic for choosing the best native stream type from a
/// capture device.  Extracted so it can be unit-tested without hardware.
/// </summary>
internal static class StreamSelector
{
    /// <summary>
    /// Picks the best <see cref="NativeStreamInfo"/> from <paramref name="types"/>
    /// according to the given <paramref name="profile"/>.
    /// Returns <c>null</c> if the list is empty.
    /// </summary>
    internal static NativeStreamInfo? SelectBest(
        IReadOnlyList<NativeStreamInfo> types, CaptureProfile profile)
    {
        if (types.Count == 0) return null;

        // Always prefer 1920×1080 @ ≥55 fps when available — the standard
        // target for 1080p60 console capture.  Fall back to the full list
        // only when the device doesn't expose that mode at all.
        var target1080p60 = types
            .Where(t => t.Width == 1920 && t.Height == 1080 && t.Fps >= 55)
            .ToList();
        IReadOnlyList<NativeStreamInfo> pool = target1080p60.Count > 0
            ? target1080p60
            : types;

        return profile switch
        {
            CaptureProfile.LowLatency =>
                pool.OrderByDescending(t => t.Fps)
                    .ThenByDescending(t => (long)t.Width * t.Height)
                    .First(),

            CaptureProfile.Quality =>
                PreferUncompressed(pool, t => ((long)t.Width * t.Height, (long)t.Fps)),

            _ => // Balanced
                PreferUncompressed(pool, t => ((long)t.Width * t.Height * t.Fps, 0L)),
        };
    }

    internal static NativeStreamInfo PreferUncompressed(
        IReadOnlyList<NativeStreamInfo> types,
        Func<NativeStreamInfo, (long primary, long secondary)> order)
    {
        var uncompressed = types.Where(t => !t.IsCompressed).ToList();
        var pool = uncompressed.Count > 0 ? uncompressed : types.ToList();
        return pool.OrderByDescending(t => order(t).primary)
                   .ThenByDescending(t => order(t).secondary)
                   .First();
    }
}
