using System;
using System.Collections.Generic;
using Iris.Core.Errors;
using Iris.Core.Imaging;
using Iris.Core.Platform;
using Microsoft.Extensions.Logging;

namespace Iris.Platform.Windows.Capture;

internal static class MonitorEnumerator
{
    private const uint DISPLAY_DEVICE_ATTACHED_TO_DESKTOP = 0x00000001;
    private const uint DISPLAY_DEVICE_MIRRORING_DRIVER = 0x00000008;
    private const nint DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = -4;

    public static IReadOnlyList<MonitorInfo> EnumerateMonitors(
        INativeDesktopAdapter adapter,
        ILogger? logger,
        Action<ILogger, string, int, Exception?> logCleanupFailure)
    {
        ArgumentNullException.ThrowIfNull(adapter);

        for (int attempt = 0; attempt < 2; attempt++)
        {
            var (success, monitors) = TryEnumerateOnce(adapter, logger, logCleanupFailure);
            if (success && monitors != null && monitors.Count > 0)
            {
                return monitors;
            }
        }

        throw new IrisException(ErrorCode.Unknown, global::Iris.Core.Localization.CoreText.Get("Text_18140661bf9b"));
    }

    private static (bool Success, IReadOnlyList<MonitorInfo>? Monitors) TryEnumerateOnce(
        INativeDesktopAdapter adapter,
        ILogger? logger,
        Action<ILogger, string, int, Exception?> logCleanupFailure)
    {
        nint oldContext = adapter.SetThreadDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
        if (oldContext == 0)
        {
            return (false, null);
        }

        bool primaryExceptionOccurred = false;
        var cleanupFailures = new List<(string Action, int ErrorCode)>();
        bool queryFailed = false;
        var resultList = new List<MonitorInfo>();

        try
        {
            var allDevices = new Dictionary<string, NativeDisplayDeviceData>(StringComparer.OrdinalIgnoreCase);
            bool deviceConflict = false;

            for (uint index = 0; ; index++)
            {
                if (!adapter.EnumDisplayDevice(index, out var devData))
                {
                    break;
                }

                if (string.IsNullOrEmpty(devData.DeviceName))
                {
                    continue;
                }

                if (allDevices.TryGetValue(devData.DeviceName, out var existing))
                {
                    if (existing.StateFlags != devData.StateFlags)
                    {
                        deviceConflict = true;
                    }
                }
                else
                {
                    allDevices[devData.DeviceName] = devData;
                }
            }

            if (deviceConflict || allDevices.Count == 0)
            {
                queryFailed = true;
            }

            IReadOnlyList<NativeMonitorEntry>? rawMonitors = null;
            if (!queryFailed && (!adapter.TryEnumDisplayMonitors(out rawMonitors) || rawMonitors == null || rawMonitors.Count == 0))
            {
                queryFailed = true;
            }

            var validMonitors = new List<NativeMonitorEntry>();
            var seenMonitors = new Dictionary<string, NativeMonitorEntry>(StringComparer.OrdinalIgnoreCase);

            if (!queryFailed && rawMonitors != null)
            {
                foreach (var m in rawMonitors)
                {
                    if (string.IsNullOrEmpty(m.DeviceName))
                    {
                        queryFailed = true;
                        break;
                    }

                    if (!allDevices.TryGetValue(m.DeviceName, out var devData))
                    {
                        // Unmapped monitor in adapter device list -> inconsistent snapshot
                        queryFailed = true;
                        break;
                    }

                    bool attached = (devData.StateFlags & DISPLAY_DEVICE_ATTACHED_TO_DESKTOP) != 0;
                    bool mirror = (devData.StateFlags & DISPLAY_DEVICE_MIRRORING_DRIVER) != 0;

                    if (attached && !mirror)
                    {
                        if (seenMonitors.TryGetValue(m.DeviceName, out var existing))
                        {
                            if (existing.HMonitor != m.HMonitor ||
                                existing.BoundsLeft != m.BoundsLeft ||
                                existing.BoundsTop != m.BoundsTop ||
                                existing.BoundsRight != m.BoundsRight ||
                                existing.BoundsBottom != m.BoundsBottom ||
                                existing.WorkLeft != m.WorkLeft ||
                                existing.WorkTop != m.WorkTop ||
                                existing.WorkRight != m.WorkRight ||
                                existing.WorkBottom != m.WorkBottom ||
                                existing.IsPrimary != m.IsPrimary)
                            {
                                // Conflicting monitor information for the same device name -> inconsistent snapshot
                                queryFailed = true;
                                break;
                            }

                            // Exact identical duplicate: de-duplicate
                            continue;
                        }

                        seenMonitors[m.DeviceName] = m;
                        validMonitors.Add(m);
                    }
                }
            }

            if (validMonitors.Count == 0)
            {
                queryFailed = true;
            }

            if (!queryFailed)
            {
                foreach (var m in validMonitors)
                {
                    long boundsW = (long)m.BoundsRight - m.BoundsLeft;
                    long boundsH = (long)m.BoundsBottom - m.BoundsTop;
                    if (boundsW <= 0 || boundsH <= 0 || boundsW > int.MaxValue || boundsH > int.MaxValue)
                    {
                        queryFailed = true;
                        break;
                    }

                    int centerX = (int)((long)m.BoundsLeft + boundsW / 2);
                    int centerY = (int)((long)m.BoundsTop + boundsH / 2);

                    nint hwnd = adapter.CreateDpiQueryWindow(centerX, centerY);
                    if (hwnd == 0)
                    {
                        queryFailed = true;
                        break;
                    }

                    try
                    {
                        nint windowMonitor = adapter.MonitorFromWindow(hwnd, 0);
                        if (windowMonitor != m.HMonitor)
                        {
                            queryFailed = true;
                            break;
                        }

                        uint dpi = adapter.GetDpiForWindow(hwnd);
                        if (dpi == 0)
                        {
                            queryFailed = true;
                            break;
                        }

                        double dpiScale = dpi / 96.0;
                        if (!DesktopGeometryHelper.ValidateGeometry(
                                m.BoundsLeft, m.BoundsTop, m.BoundsRight, m.BoundsBottom,
                                m.WorkLeft, m.WorkTop, m.WorkRight, m.WorkBottom,
                                dpiScale))
                        {
                            queryFailed = true;
                            break;
                        }

                        var bounds = new PixelRect(m.BoundsLeft, m.BoundsTop, (int)boundsW, (int)boundsH);
                        var workArea = new PixelRect(m.WorkLeft, m.WorkTop, m.WorkRight - m.WorkLeft, m.WorkBottom - m.WorkTop);
                        resultList.Add(new MonitorInfo(m.DeviceName, bounds, workArea, dpiScale, m.IsPrimary));
                    }
                    finally
                    {
                        if (!adapter.DestroyWindow(hwnd))
                        {
                            cleanupFailures.Add(("DestroyWindow", adapter.GetLastError()));
                        }
                    }
                }
            }
        }
        catch
        {
            primaryExceptionOccurred = true;
            throw;
        }
        finally
        {
            nint restored = adapter.SetThreadDpiAwarenessContext(oldContext);
            if (restored == 0)
            {
                cleanupFailures.Add(("SetThreadDpiAwarenessContext", adapter.GetLastError()));
            }

            if (primaryExceptionOccurred && logger != null)
            {
                foreach (var (action, err) in cleanupFailures)
                {
                    logCleanupFailure(logger, action, err, null);
                }
            }
        }

        if (queryFailed || resultList.Count == 0)
        {
            if (cleanupFailures.Count > 0)
            {
                // Primary query failure present AND cleanup failure occurred.
                // Log cleanup failures safely and terminate immediately - do NOT mask via retry!
                if (logger != null)
                {
                    foreach (var (action, err) in cleanupFailures)
                    {
                        logCleanupFailure(logger, action, err, null);
                    }
                }
                throw new IrisException(ErrorCode.Unknown, global::Iris.Core.Localization.CoreText.Get("Text_18140661bf9b"));
            }

            // Clean failure without cleanup errors -> allow retry
            return (false, null);
        }

        if (cleanupFailures.Count > 0)
        {
            // Query succeeded, but cleanup failed.
            // First cleanup failure becomes primary error; remaining are logged.
            if (logger != null)
            {
                for (int i = 1; i < cleanupFailures.Count; i++)
                {
                    logCleanupFailure(logger, cleanupFailures[i].Action, cleanupFailures[i].ErrorCode, null);
                }
            }
            throw new IrisException(ErrorCode.Unknown, global::Iris.Core.Localization.CoreText.Get("Text_bda16101de59"));
        }

        // Sort by IsPrimary DESC, then DeviceName Ordinal ASC
        resultList.Sort((a, b) =>
        {
            if (a.IsPrimary != b.IsPrimary)
            {
                return a.IsPrimary ? -1 : 1;
            }
            return string.Compare(a.DeviceName, b.DeviceName, StringComparison.Ordinal);
        });

        return (true, resultList.AsReadOnly());
    }
}
