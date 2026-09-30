using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using global::Windows.Win32;
using global::Windows.Win32.Foundation;
using global::Windows.Win32.Graphics.Gdi;
using global::Windows.Win32.System.Threading;
using global::Windows.Win32.UI.HiDpi;
using global::Windows.Win32.UI.WindowsAndMessaging;

namespace Iris.Platform.Windows.Capture;

internal unsafe sealed class NativeDesktopAdapter : INativeDesktopAdapter
{
    public static readonly NativeDesktopAdapter Instance = new();

    public bool EnumDisplayDevice(uint index, out NativeDisplayDeviceData device)
    {
        DISPLAY_DEVICEW dev = default;
        dev.cb = (uint)sizeof(DISPLAY_DEVICEW);

        bool result = PInvoke.EnumDisplayDevices(default, index, &dev, 0);
        if (result)
        {
            device = new NativeDisplayDeviceData(dev.DeviceName.ToString(), (uint)dev.StateFlags);
            return true;
        }

        device = default;
        return false;
    }

    public bool TryEnumDisplayMonitors(out IReadOnlyList<NativeMonitorEntry> monitors)
    {
        var list = new List<NativeMonitorEntry>();
        bool callbackFailed = false;

        BOOL Callback(HMONITOR hMonitor, HDC hdcMonitor, RECT* lprcMonitor, LPARAM dwData)
        {
            try
            {
                MONITORINFOEXW minfo = default;
                minfo.monitorInfo.cbSize = (uint)sizeof(MONITORINFOEXW);
                if (!PInvoke.GetMonitorInfo(hMonitor, (MONITORINFO*)&minfo))
                {
                    callbackFailed = true;
                    return false;
                }

                string devName = minfo.szDevice.ToString();
                bool isPrimary = (minfo.monitorInfo.dwFlags & 1) != 0; // MONITORINFOF_PRIMARY = 1
                list.Add(new NativeMonitorEntry(
                    (nint)hMonitor.Value,
                    devName,
                    minfo.monitorInfo.rcMonitor.left,
                    minfo.monitorInfo.rcMonitor.top,
                    minfo.monitorInfo.rcMonitor.right,
                    minfo.monitorInfo.rcMonitor.bottom,
                    minfo.monitorInfo.rcWork.left,
                    minfo.monitorInfo.rcWork.top,
                    minfo.monitorInfo.rcWork.right,
                    minfo.monitorInfo.rcWork.bottom,
                    isPrimary));
                return true;
            }
            catch
            {
                callbackFailed = true;
                return false;
            }
        }

        bool enumSuccess = PInvoke.EnumDisplayMonitors(default, null, Callback, 0);
        if (!enumSuccess || callbackFailed)
        {
            monitors = Array.Empty<NativeMonitorEntry>();
            return false;
        }

        monitors = list;
        return true;
    }

    public nint SetThreadDpiAwarenessContext(nint dpiContext)
    {
        var result = PInvoke.SetThreadDpiAwarenessContext(new DPI_AWARENESS_CONTEXT((void*)dpiContext));
        return (nint)result.Value;
    }

    public nint CreateDpiQueryWindow(int x, int y)
    {
        fixed (char* pClassName = "STATIC")
        fixed (char* pWindowName = "")
        {
            var hwnd = PInvoke.CreateWindowEx(
                WINDOW_EX_STYLE.WS_EX_NOACTIVATE | WINDOW_EX_STYLE.WS_EX_TOOLWINDOW,
                pClassName,
                pWindowName,
                WINDOW_STYLE.WS_POPUP,
                x,
                y,
                1,
                1,
                default,
                default,
                default,
                null);

            return (nint)hwnd.Value;
        }
    }

    public nint MonitorFromWindow(nint hwnd, uint flags)
    {
        var hMonitor = PInvoke.MonitorFromWindow(new HWND((void*)hwnd), (MONITOR_FROM_FLAGS)flags);
        return (nint)hMonitor.Value;
    }

    public uint GetDpiForWindow(nint hwnd)
    {
        return PInvoke.GetDpiForWindow(new HWND((void*)hwnd));
    }

    public bool DestroyWindow(nint hwnd)
    {
        if (hwnd != 0)
        {
            return PInvoke.DestroyWindow(new HWND((void*)hwnd));
        }
        return true;
    }

    public bool GetPhysicalCursorPos(out int x, out int y)
    {
        System.Drawing.Point pt = default;
        bool success = PInvoke.GetPhysicalCursorPos(&pt);
        x = pt.X;
        y = pt.Y;
        return success;
    }

    public nint GetForegroundWindow()
    {
        var hwnd = PInvoke.GetForegroundWindow();
        return (nint)hwnd.Value;
    }

    public uint GetWindowThreadProcessId(nint hwnd, out uint processId)
    {
        uint pid = 0;
        uint tid = PInvoke.GetWindowThreadProcessId(new HWND((void*)hwnd), &pid);
        processId = pid;
        return tid;
    }

    public nint OpenProcess(uint desiredAccess, bool inheritHandle, uint processId)
    {
        var hProcess = PInvoke.OpenProcess((PROCESS_ACCESS_RIGHTS)desiredAccess, inheritHandle, processId);
        return (nint)hProcess.Value;
    }

    public string? QueryFullProcessImageName(nint hProcess)
    {
        char[] buffer = new char[32768];
        using var safeHandle = new Microsoft.Win32.SafeHandles.SafeProcessHandle(hProcess, ownsHandle: false);
        uint size = (uint)buffer.Length;
        if (PInvoke.QueryFullProcessImageName(safeHandle, (PROCESS_NAME_FORMAT)0, buffer.AsSpan(), ref size))
        {
            return new string(buffer, 0, (int)size);
        }
        return null;
    }

    public bool CloseHandle(nint handle)
    {
        if (handle != 0)
        {
            return PInvoke.CloseHandle(new HANDLE((void*)handle));
        }
        return true;
    }

    public string? GetWindowText(nint hwnd, int maxChars)
    {
        char[] buffer = new char[1025];
        fixed (char* pBuf = buffer)
        {
            int len = PInvoke.GetWindowText(new HWND((void*)hwnd), new PWSTR(pBuf), 1025);
            if (len > 0)
            {
                return new string(pBuf, 0, Math.Min(len, maxChars));
            }
            return null;
        }
    }

    public uint GetCurrentProcessId()
    {
        return PInvoke.GetCurrentProcessId();
    }

    public uint GetCurrentThreadId()
    {
        return PInvoke.GetCurrentThreadId();
    }

    public int GetLastError()
    {
        return Marshal.GetLastWin32Error();
    }
}
