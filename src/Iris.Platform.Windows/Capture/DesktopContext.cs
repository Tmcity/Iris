using System;
using System.Collections.Generic;
using System.IO;
using Iris.Core.Errors;
using Iris.Core.Imaging;
using Iris.Core.Platform;
using Microsoft.Extensions.Logging;

namespace Iris.Platform.Windows.Capture;

internal sealed class DesktopContext : IDesktopContext
{
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    private const int MaxTitleCharacters = 1024;

    private static readonly Action<ILogger, string, int, Exception?> s_logCleanupFailure =
        LoggerMessage.Define<string, int>(
            LogLevel.Warning,
            new EventId(1, nameof(DesktopContext)),
            "Failed to {Action} during desktop context cleanup: error {ErrorCode}");

    private readonly INativeDesktopAdapter _adapter;
    private readonly ILogger<DesktopContext>? _logger;

    public DesktopContext()
        : this(null, null)
    {
    }

    public DesktopContext(ILogger<DesktopContext>? logger)
        : this(null, logger)
    {
    }

    internal DesktopContext(INativeDesktopAdapter? adapter, ILogger<DesktopContext>? logger)
    {
        _adapter = adapter ?? NativeDesktopAdapter.Instance;
        _logger = logger;
    }

    public ForegroundAppInfo? GetForegroundApp()
    {
        for (int attempt = 0; attempt < 2; attempt++)
        {
            nint hwnd1 = _adapter.GetForegroundWindow();
            if (hwnd1 == 0)
            {
                return null;
            }

            uint pid1 = 0;
            uint threadId1 = _adapter.GetWindowThreadProcessId(hwnd1, out pid1);
            if (threadId1 == 0 || pid1 == 0)
            {
                return null;
            }

            nint hProcess = _adapter.OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid1);
            if (hProcess == 0)
            {
                return null;
            }

            bool closeFailed = false;
            int closeError = 0;
            string? fullPath = null;
            try
            {
                fullPath = _adapter.QueryFullProcessImageName(hProcess);
            }
            finally
            {
                if (!_adapter.CloseHandle(hProcess))
                {
                    closeFailed = true;
                    closeError = _adapter.GetLastError();
                    if (_logger != null)
                    {
                        s_logCleanupFailure(_logger, "CloseHandle", closeError, null);
                    }
                }
            }

            if (closeFailed)
            {
                throw new IrisException(ErrorCode.Unknown, "桌面查询资源清理失败");
            }

            if (string.IsNullOrEmpty(fullPath))
            {
                return null;
            }

            string processName = Path.GetFileName(fullPath);
            if (string.IsNullOrEmpty(processName))
            {
                return null;
            }

            string? windowTitle = null;
            uint currentPid = _adapter.GetCurrentProcessId();
            if (pid1 == currentPid)
            {
                uint currentTid = _adapter.GetCurrentThreadId();
                if (threadId1 == currentTid)
                {
                    windowTitle = _adapter.GetWindowText(hwnd1, MaxTitleCharacters);
                }
                else
                {
                    // Same process, different thread: avoid cross-thread WM_GETTEXT hang
                    windowTitle = null;
                }
            }
            else
            {
                windowTitle = _adapter.GetWindowText(hwnd1, MaxTitleCharacters);
            }

            if (string.IsNullOrEmpty(windowTitle))
            {
                windowTitle = null;
            }
            else if (windowTitle.Length > MaxTitleCharacters)
            {
                windowTitle = windowTitle[..MaxTitleCharacters];
            }

            // Re-sample foreground window & PID to ensure stable source
            nint hwnd2 = _adapter.GetForegroundWindow();
            uint pid2 = 0;
            _adapter.GetWindowThreadProcessId(hwnd2, out pid2);

            if (hwnd1 != hwnd2 || pid1 != pid2)
            {
                continue;
            }

            return new ForegroundAppInfo(processName, (int)pid1, windowTitle, hwnd1);
        }

        return null;
    }

    public PixelPoint GetCursorPosition()
    {
        if (!_adapter.GetPhysicalCursorPos(out int x, out int y))
        {
            throw new IrisException(ErrorCode.Unknown, "无法获取鼠标位置");
        }

        return new PixelPoint(x, y);
    }

    public IReadOnlyList<MonitorInfo> GetMonitors()
    {
        return MonitorEnumerator.EnumerateMonitors(_adapter, _logger, s_logCleanupFailure);
    }

    public MonitorInfo GetMonitorAt(PixelPoint point)
    {
        var monitors = GetMonitors();
        return DesktopGeometryHelper.GetNearestMonitor(monitors, point);
    }
}
