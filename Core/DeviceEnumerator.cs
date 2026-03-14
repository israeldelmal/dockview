using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using NAudio.CoreAudioApi;

namespace Dockview.Core;

/// <summary>
/// Enumerates video (UVC/MF) and audio (WASAPI) capture devices.
///
/// IMPORTANT: All Media Foundation calls MUST run on an MTA thread.
/// WPF's UI thread is STA; calling MF from STA causes COM to create a
/// cross-apartment proxy whose QueryInterface always returns E_NOINTERFACE.
/// We spin up a short-lived background thread (MTA by default in .NET) for
/// every MF operation that originates from the UI thread.
/// </summary>
public static class DeviceEnumerator
{
    /// <summary>Populated if GetVideoDevices() threw — useful for diagnostics.</summary>
    public static string? LastVideoEnumerationError { get; private set; }

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>Returns all active video capture devices via Media Foundation.</summary>
    public static IReadOnlyList<VideoCaptureDevice> GetVideoDevices()
    {
        IReadOnlyList<VideoCaptureDevice>? result = null;

        // Run on a new MTA thread to avoid STA/MTA apartment mismatch
        RunOnMta(() => result = GetVideoDevicesCore());

        return result ?? Array.Empty<VideoCaptureDevice>();
    }

    /// <summary>Returns all active audio capture endpoints via WASAPI.</summary>
    public static IReadOnlyList<AudioCaptureDevice> GetAudioDevices()
    {
        var result = new List<AudioCaptureDevice>();
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            var endpoints = enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active);
            foreach (var ep in endpoints)
            {
                result.Add(new AudioCaptureDevice
                {
                    FriendlyName = ep.FriendlyName,
                    DeviceId     = ep.ID,
                });
                ep.Dispose();
            }
        }
        catch { /* WASAPI unavailable */ }
        return result;
    }

    // ── Internal helpers ──────────────────────────────────────────────────────

    /// <summary>
    /// Ensures MFStartup has been called on the CURRENT thread (must be MTA).
    /// Each MTA thread that uses MF needs its own MFStartup call when using NOSOCKET.
    /// </summary>
    internal static void EnsureMFStarted()
    {
        // MFStartup is idempotent for the same process; safe to call multiple times.
        MFNativeMethods.MFStartup(MFNativeMethods.MF_VERSION, MFNativeMethods.MFSTARTUP_NOSOCKET);
    }

    private static IReadOnlyList<VideoCaptureDevice> GetVideoDevicesCore()
    {
        var result = new List<VideoCaptureDevice>();

        EnsureMFStarted();

        // Build attribute bag with SourceType = VideoCapture
        using var attribs = new MFAttributesBag(1);
        attribs.SetGuid(MFGuids.SourceType, MFGuids.SourceTypeVideoCapture);

        // Enumerate devices — ppActivate is a CoTaskMem-allocated array of IMFActivate*
        MFNativeMethods.MFEnumDeviceSources(attribs.Native, out var ppActivate, out uint count);

        for (uint i = 0; i < count; i++)
        {
            var ptr = Marshal.ReadIntPtr(ppActivate, (int)(i * IntPtr.Size));
            if (ptr == IntPtr.Zero) continue;

            try
            {
                var nameKey = MFGuids.FriendlyName;
                var linkKey = MFGuids.SymbolicLink;

                // Direct vtable calls — no QI, no RCW
                MFCom.GetAllocatedString(ptr, ref nameKey, out string? name, out _);
                MFCom.GetAllocatedString(ptr, ref linkKey, out string? link, out _);

                if (!string.IsNullOrWhiteSpace(name))
                {
                    result.Add(new VideoCaptureDevice
                    {
                        FriendlyName = name ?? "(unknown)",
                        SymbolicLink = link ?? string.Empty,
                    });
                }
            }
            finally
            {
                MFCom.Release(ptr); // release the ref held by the enumeration array
            }
        }

        Marshal.FreeCoTaskMem(ppActivate);
        return result;
    }

    /// <summary>
    /// Runs <paramref name="action"/> on a new MTA thread and waits for it.
    /// New threads in .NET are MTA by default — no SetApartmentState needed.
    /// </summary>
    private static void RunOnMta(Action action)
    {
        Exception? caught = null;

        var thread = new Thread(() =>
        {
            try   { action(); }
            catch (Exception ex) { caught = ex; LastVideoEnumerationError = ex.Message; }
        })
        {
            Name         = "MF-MTA-Worker",
            IsBackground = true,
        };

        thread.Start();
        thread.Join();

        // Don't rethrow — let callers check LastVideoEnumerationError
    }
}
