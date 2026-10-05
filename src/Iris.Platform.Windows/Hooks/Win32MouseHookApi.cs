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
    public short GetAsyncKeyState(int key) => PInvoke.GetAsyncKeyState(key);
    public nint ForegroundWindow() => (nint)PInvoke.GetForegroundWindow().Value;
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
