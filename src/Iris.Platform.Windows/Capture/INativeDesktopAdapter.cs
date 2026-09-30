using System;
using System.Collections.Generic;

namespace Iris.Platform.Windows.Capture;

internal readonly record struct NativeDisplayDeviceData(string DeviceName, uint StateFlags);

internal sealed record NativeMonitorEntry(
    nint HMonitor,
    string DeviceName,
    int BoundsLeft,
    int BoundsTop,
    int BoundsRight,
    int BoundsBottom,
    int WorkLeft,
    int WorkTop,
    int WorkRight,
    int WorkBottom,
    bool IsPrimary);

internal interface INativeDesktopAdapter
{
    bool EnumDisplayDevice(uint index, out NativeDisplayDeviceData device);

    bool TryEnumDisplayMonitors(out IReadOnlyList<NativeMonitorEntry> monitors);

    nint SetThreadDpiAwarenessContext(nint dpiContext);

    nint CreateDpiQueryWindow(int x, int y);

    nint MonitorFromWindow(nint hwnd, uint flags);

    uint GetDpiForWindow(nint hwnd);

    bool DestroyWindow(nint hwnd);

    bool GetPhysicalCursorPos(out int x, out int y);

    nint GetForegroundWindow();

    uint GetWindowThreadProcessId(nint hwnd, out uint processId);

    nint OpenProcess(uint desiredAccess, bool inheritHandle, uint processId);

    string? QueryFullProcessImageName(nint hProcess);

    bool CloseHandle(nint handle);

    string? GetWindowText(nint hwnd, int maxChars);

    uint GetCurrentProcessId();

    uint GetCurrentThreadId();

    int GetLastError();
}
