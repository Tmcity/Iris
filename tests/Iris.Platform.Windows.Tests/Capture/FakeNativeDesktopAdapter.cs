using System;
using System.Collections.Generic;
using Iris.Core.Errors;

namespace Iris.Platform.Windows.Capture;

internal sealed class FakeNativeDesktopAdapter : INativeDesktopAdapter
{
    public List<NativeDisplayDeviceData> Devices { get; set; } = [];
    public List<NativeMonitorEntry> Monitors { get; set; } = [];

    public Dictionary<nint, uint> WindowDpis { get; } = new();
    public Dictionary<nint, nint> WindowMonitors { get; } = new();

    public List<string> CallLog { get; } = [];
    public List<(string Action, int ErrorCode)> CleanupFailuresLogged { get; } = [];

    public List<nint> RecordedEnumContexts { get; } = [];
    public HashSet<nint> LiveWindows { get; } = [];
    public List<nint> SetThreadDpiContextTargets { get; } = [];

    public nint CurrentThreadDpiContext { get; set; } = 1001;
    public nint ExpectedAwarenessContext { get; set; } = -4; // DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2
    public bool RestoreThreadDpiContextFails { get; set; }
    public bool FailRestoreOnce { get; set; }
    public int RestoreThreadDpiContextError { get; set; } = 1359;

    public bool CreateDpiQueryWindowFails { get; set; }
    public int CreateDpiQueryWindowError { get; set; } = 1400;

    public bool DestroyWindowFails { get; set; }
    public bool FailDestroyOnce { get; set; }
    public int DestroyWindowError { get; set; } = 1400;

    public bool FailDpiOnce { get; set; }
    public int DpiQueryCallCount { get; private set; }

    public bool GetPhysicalCursorPosResult { get; set; } = true;
    public int CursorX { get; set; } = 100;
    public int CursorY { get; set; } = 200;

    public nint ForegroundWindowHandle { get; set; } = 5001;
    public uint ForegroundProcessId { get; set; } = 6001;
    public uint ForegroundThreadId { get; set; } = 7001;

    public bool ReSampleForegroundSwitches { get; set; }
    public nint SwitchedForegroundWindowHandle { get; set; } = 5002;
    public uint SwitchedForegroundProcessId { get; set; } = 6002;

    public bool OpenProcessFails { get; set; }
    public int OpenProcessError { get; set; } = 5; // Access denied

    public bool ThrowOnProcessImageQuery { get; set; }
    public string? ProcessImagePath { get; set; } = @"C:\Windows\System32\notepad.exe";
    public string? WindowTextResult { get; set; } = "Untitled - Notepad";

    public bool CloseHandleFails { get; set; }
    public int CloseHandleError { get; set; } = 6; // Invalid handle

    public uint CurrentProcessIdValue { get; set; } = 9999;
    public uint CurrentThreadIdValue { get; set; } = 8888;

    public int LastErrorCode { get; set; }

    public int GetForegroundWindowCallCount { get; private set; }
    public int DestroyWindowCallCount { get; private set; }
    public int CloseHandleCallCount { get; private set; }
    public int SetThreadDpiContextCallCount { get; private set; }

    public bool EnumDisplayDevice(uint index, out NativeDisplayDeviceData device)
    {
        RecordedEnumContexts.Add(CurrentThreadDpiContext);
        CallLog.Add($"EnumDisplayDevice({index})");
        if (index < Devices.Count)
        {
            device = Devices[(int)index];
            return true;
        }

        device = default;
        return false;
    }

    public bool TryEnumDisplayMonitors(out IReadOnlyList<NativeMonitorEntry> monitors)
    {
        RecordedEnumContexts.Add(CurrentThreadDpiContext);
        CallLog.Add($"TryEnumDisplayMonitors() -> count={Monitors.Count}");
        monitors = Monitors.ToArray();
        return true;
    }

    public nint SetThreadDpiAwarenessContext(nint dpiContext)
    {
        SetThreadDpiContextCallCount++;
        SetThreadDpiContextTargets.Add(dpiContext);
        CallLog.Add($"SetThreadDpiAwarenessContext({dpiContext})");

        if (FailRestoreOnce && SetThreadDpiContextCallCount == 2)
        {
            LastErrorCode = RestoreThreadDpiContextError;
            return 0;
        }

        if (SetThreadDpiContextCallCount > 1 && RestoreThreadDpiContextFails)
        {
            LastErrorCode = RestoreThreadDpiContextError;
            return 0;
        }

        nint old = CurrentThreadDpiContext;
        CurrentThreadDpiContext = dpiContext;
        return old;
    }

    public nint CreateDpiQueryWindow(int x, int y)
    {
        CallLog.Add($"CreateDpiQueryWindow({x}, {y})");
        if (CreateDpiQueryWindowFails)
        {
            LastErrorCode = CreateDpiQueryWindowError;
            return 0;
        }

        // Return pseudo window handle based on coordinate hash
        nint hwnd = (nint)(0x10000 + Math.Abs(x) * 31 + Math.Abs(y));
        LiveWindows.Add(hwnd);
        return hwnd;
    }

    public nint MonitorFromWindow(nint hwnd, uint flags)
    {
        CallLog.Add($"MonitorFromWindow({hwnd}, {flags})");
        if (WindowMonitors.TryGetValue(hwnd, out nint mon))
        {
            return mon;
        }
        // Default to first monitor if configured
        return Monitors.Count > 0 ? Monitors[0].HMonitor : 0;
    }

    public uint GetDpiForWindow(nint hwnd)
    {
        DpiQueryCallCount++;
        CallLog.Add($"GetDpiForWindow({hwnd})");

        if (FailDpiOnce && DpiQueryCallCount == 1)
        {
            return 0;
        }

        if (WindowDpis.TryGetValue(hwnd, out uint dpi))
        {
            return dpi;
        }
        return 96;
    }

    public bool DestroyWindow(nint hwnd)
    {
        DestroyWindowCallCount++;
        CallLog.Add($"DestroyWindow({hwnd})");

        if (FailDestroyOnce && DestroyWindowCallCount == 1)
        {
            LastErrorCode = DestroyWindowError;
            return false;
        }

        if (DestroyWindowFails)
        {
            LastErrorCode = DestroyWindowError;
            return false;
        }

        LiveWindows.Remove(hwnd);
        return true;
    }

    public bool GetPhysicalCursorPos(out int x, out int y)
    {
        CallLog.Add($"GetPhysicalCursorPos()");
        x = CursorX;
        y = CursorY;
        if (!GetPhysicalCursorPosResult)
        {
            LastErrorCode = 1460;
            return false;
        }
        return true;
    }

    public nint GetForegroundWindow()
    {
        GetForegroundWindowCallCount++;
        CallLog.Add($"GetForegroundWindow() #{GetForegroundWindowCallCount}");

        if (ReSampleForegroundSwitches && (GetForegroundWindowCallCount % 2 == 0))
        {
            return SwitchedForegroundWindowHandle;
        }

        return ForegroundWindowHandle;
    }

    public uint GetWindowThreadProcessId(nint hwnd, out uint processId)
    {
        CallLog.Add($"GetWindowThreadProcessId({hwnd})");
        if (hwnd == 0)
        {
            processId = 0;
            return 0;
        }

        if (hwnd == SwitchedForegroundWindowHandle)
        {
            processId = SwitchedForegroundProcessId;
            return 7002;
        }

        processId = ForegroundProcessId;
        return ForegroundThreadId;
    }

    public nint OpenProcess(uint desiredAccess, bool inheritHandle, uint processId)
    {
        CallLog.Add($"OpenProcess({desiredAccess:X}, {inheritHandle}, {processId})");
        if (OpenProcessFails)
        {
            LastErrorCode = OpenProcessError;
            return 0;
        }

        return (nint)(0x8000 + processId);
    }

    public string? QueryFullProcessImageName(nint hProcess)
    {
        CallLog.Add($"QueryFullProcessImageName({hProcess})");
        if (ThrowOnProcessImageQuery)
        {
            throw new IrisException(ErrorCode.Unknown, "primary query failure");
        }
        return ProcessImagePath;
    }

    public bool CloseHandle(nint handle)
    {
        CloseHandleCallCount++;
        CallLog.Add($"CloseHandle({handle})");
        if (CloseHandleFails)
        {
            LastErrorCode = CloseHandleError;
            return false;
        }
        return true;
    }

    public string? GetWindowText(nint hwnd, int maxChars)
    {
        CallLog.Add($"GetWindowText({hwnd}, {maxChars})");
        if (WindowTextResult != null && WindowTextResult.Length > maxChars)
        {
            return WindowTextResult[..maxChars];
        }
        return WindowTextResult;
    }

    public uint GetCurrentProcessId() => CurrentProcessIdValue;

    public uint GetCurrentThreadId() => CurrentThreadIdValue;

    public int GetLastError() => LastErrorCode;
}
