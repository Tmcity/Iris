using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Iris.Platform.Windows.Hooks;

internal sealed unsafe class Win32MouseHookApi : IMouseHookApi
{
    public void PeekMessage() => PInvoke.PeekMessage(out _, default, 0, 0, PEEK_MESSAGE_REMOVE_TYPE.PM_NOREMOVE);
    public uint CurrentThreadId() => PInvoke.GetCurrentThreadId();
    public uint CurrentProcessId() => PInvoke.GetCurrentProcessId();
    public nint ModuleHandle()
    {
        using var module = PInvoke.GetModuleHandle((string?)null);
        return module.DangerousGetHandle(); // GetModuleHandle returns a borrowed module.
    }
    public nint LoadCursor(nint instance, int resource) => (nint)PInvoke.LoadCursor(new HINSTANCE((void*)instance), (char*)resource).Value;
    public nint SetHook(int hookId, HOOKPROC callback, nint module, uint threadId) =>
        (nint)PInvoke.SetWindowsHookEx((WINDOWS_HOOK_ID)hookId, callback, new HINSTANCE((void*)module), threadId).Value;
    public bool Unhook(nint hook) => PInvoke.UnhookWindowsHookEx(new HHOOK((void*)hook));
    public nint CallNext(int code, nuint message, nint data) => PInvoke.CallNextHookEx(default, code, new WPARAM(message), new LPARAM(data)).Value;
    public int GetMessage(out MSG message) => PInvoke.GetMessage(out message, default, 0, 0).Value;
    public void TranslateMessage(in MSG message) => PInvoke.TranslateMessage(in message);
    public void DispatchMessage(in MSG message) => PInvoke.DispatchMessage(in message);
    public bool PostThreadMessage(uint thread, uint message) => PInvoke.PostThreadMessage(thread, message, default, default);
    public bool GetCursorInfo(ref CURSORINFO cursor)
    {
        fixed (CURSORINFO* pointer = &cursor) return PInvoke.GetCursorInfo(pointer);
    }
    private static readonly int[] NonTextCursorIds =
    [
        32512, 32514, 32515, 32642, 32643, 32644, 32645, 32646, 32648, 32649, 32650, 32651
    ];

    public bool IsIBeamCursorShape(nint cursor)
    {
        if (cursor == 0) return false;
        foreach (int id in NonTextCursorIds)
        {
            if (cursor == LoadCursor(0, id)) return false;
        }
        if (!GetIconInfo(cursor, out var info)) return false;
        try
        {
            if (info.fIcon != 0) return false;
            int w = 0, h = 0;
            nint hbm = info.hbmColor != 0 ? info.hbmColor : info.hbmMask;
            if (hbm != 0 && GetObjectW(hbm, sizeof(BITMAP_RAW), out var bm) > 0)
            {
                w = bm.bmWidth;
                h = info.hbmColor != 0 ? bm.bmHeight : bm.bmHeight / 2;
            }
            if (w < 16 || h < 16) return false;
            bool centeredBeam = info.xHotspot >= w / 4 && info.xHotspot <= (w * 3) / 4 &&
                                info.yHotspot >= h / 6 && info.yHotspot <= (h * 5) / 6 &&
                                !(info.xHotspot == w / 2 && info.yHotspot == h / 2 && info.hbmColor == 0);
            bool marginLineArrow = info.hbmColor == 0 &&
                                   info.xHotspot >= (w * 3) / 4 && info.yHotspot <= h / 6;
            return centeredBeam || marginLineArrow;
        }
        finally
        {
            if (info.hbmMask != 0) DeleteObject(info.hbmMask);
            if (info.hbmColor != 0) DeleteObject(info.hbmColor);
        }
    }

    public bool IsTextSelectionSurface(nint rootWindow, int x, int y)
    {
        if (rootWindow == 0) return false;
        string rootClass = GetWindowClass(rootWindow);
        if (!string.Equals(rootClass, "CASCADIA_HOSTING_WINDOW_CLASS", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(rootClass, "ConsoleWindowClass", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        if (GetWindowRect(rootWindow, out var rect))
        {
            if (x < rect.left + 8 || x > rect.right - 8 || y < rect.top + 8 || y > rect.bottom - 8)
            {
                return false;
            }
        }
        nint hit = WindowFromPoint(new POINT { x = x, y = y });
        if (hit != 0 && string.Equals(GetWindowClass(hit), "DRAG_BAR_WINDOW_CLASS", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        return true;
    }

    private static string GetWindowClass(nint hwnd)
    {
        Span<char> buffer = stackalloc char[128];
        fixed (char* ptr = buffer)
        {
            int len = GetClassNameW(hwnd, ptr, buffer.Length);
            return len > 0 ? new string(buffer[..len]) : string.Empty;
        }
    }

    public short GetAsyncKeyState(int key) => PInvoke.GetAsyncKeyState(key);
    public nint ForegroundWindow() => (nint)PInvoke.GetForegroundWindow().Value;
    public nint RootWindowAtPoint(int x, int y)
    {
        nint hwnd = WindowFromPoint(new POINT { x = x, y = y });
        if (hwnd == 0) return 0;
        nint root = GetAncestor(hwnd, 2); // GA_ROOT
        return root != 0 ? root : hwnd;
    }

    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int x, y; }
    [DllImport("user32.dll")] private static extern nint WindowFromPoint(POINT point);
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint hwnd, uint gaFlags);
    [StructLayout(LayoutKind.Sequential)] private struct RECT_RAW { public int left, top, right, bottom; }
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint hWnd, out RECT_RAW lpRect);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassNameW(nint hWnd, char* lpClassName, int nMaxCount);
    [StructLayout(LayoutKind.Sequential)] private struct ICONINFO_RAW { public int fIcon; public uint xHotspot, yHotspot; public nint hbmMask, hbmColor; }
    [StructLayout(LayoutKind.Sequential)] private struct BITMAP_RAW { public int bmType, bmWidth, bmHeight, bmWidthBytes; public ushort bmPlanes, bmBitsPixel; public nint bmBits; }
    [DllImport("user32.dll")] private static extern bool GetIconInfo(nint hIcon, out ICONINFO_RAW piconinfo);
    [DllImport("gdi32.dll")] private static extern int GetObjectW(nint h, int c, out BITMAP_RAW pv);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint ho);
    public uint WindowThreadProcessId(nint window, out uint processId)
    {
        processId = 0;
        fixed (uint* pid = &processId) return PInvoke.GetWindowThreadProcessId(new HWND((void*)window), pid);
    }
    public uint TickCount() => PInvoke.GetTickCount();
    public uint DoubleClickTime() => PInvoke.GetDoubleClickTime();
    public int SystemMetricsForDpi(int index, uint dpi) => PInvoke.GetSystemMetricsForDpi((SYSTEM_METRICS_INDEX)index, dpi);
    public int LastError() => Marshal.GetLastPInvokeError();
}
