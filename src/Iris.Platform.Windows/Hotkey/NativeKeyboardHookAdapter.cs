using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using global::Windows.Win32;
using global::Windows.Win32.Foundation;
using global::Windows.Win32.UI.WindowsAndMessaging;

namespace Iris.Platform.Windows.Hotkey;

/// <summary>
/// 真实 Win32 低级键盘钩子适配器实现，使用 CsWin32 封装。
/// 仅用于实际运行时；测试环境替换为 Fake 适配器。
/// </summary>
internal sealed unsafe class NativeKeyboardHookAdapter : INativeKeyboardHookAdapter
{
    private const uint WM_QUIT = 0x0012;

    public static readonly NativeKeyboardHookAdapter Instance = new();

    private readonly ConcurrentDictionary<nint, HOOKPROC> _activeDelegates = new();

    public nint SetWindowsHook(NativeKeyboardHookProc proc)
    {
        ArgumentNullException.ThrowIfNull(proc);

        HOOKPROC nativeDelegate = (nCode, wParam, lParam) =>
        {
            return new LRESULT(proc(nCode, (nuint)wParam.Value, (nint)lParam.Value));
        };

        using var hMod = PInvoke.GetModuleHandle((string?)null);
        var hInstance = (HINSTANCE)hMod.DangerousGetHandle();

        var hHook = PInvoke.SetWindowsHookEx(WINDOWS_HOOK_ID.WH_KEYBOARD_LL, nativeDelegate, hInstance, 0);
        nint handle = (nint)hHook.Value;
        if (handle != 0)
        {
            _activeDelegates[handle] = nativeDelegate;
        }

        return handle;
    }

    public bool UnhookWindowsHook(nint hookHandle)
    {
        if (hookHandle == 0)
        {
            return true;
        }

        bool success = PInvoke.UnhookWindowsHookEx(new HHOOK((void*)hookHandle));
        if (success)
        {
            _activeDelegates.TryRemove(hookHandle, out _);
        }

        return success;
    }

    public nint CallNextHook(nint hookHandle, int nCode, nuint wParam, nint lParam)
    {
        var res = PInvoke.CallNextHookEx(new HHOOK((void*)hookHandle), nCode, new WPARAM(wParam), new LPARAM(lParam));
        return (nint)res.Value;
    }

    public nint GetForegroundWindow()
    {
        var hwnd = PInvoke.GetForegroundWindow();
        return (nint)hwnd.Value;
    }

    public uint GetWindowProcessId(nint windowHandle)
    {
        if (windowHandle == 0)
        {
            return 0;
        }

        uint pid = 0;
        uint threadId = PInvoke.GetWindowThreadProcessId(new HWND((void*)windowHandle), &pid);
        return threadId != 0 ? pid : 0;
    }

    public uint GetCurrentProcessId()
    {
        return PInvoke.GetCurrentProcessId();
    }

    public bool IsWindow(nint windowHandle)
    {
        if (windowHandle == 0)
        {
            return false;
        }

        return PInvoke.IsWindow(new HWND((void*)windowHandle));
    }

    public bool IsKeyDown(int virtualKey)
    {
        return (PInvoke.GetAsyncKeyState(virtualKey) & 0x8000) != 0;
    }

    public uint GetCurrentThreadId()
    {
        return PInvoke.GetCurrentThreadId();
    }

    public bool PostThreadQuitMessage(uint threadId)
    {
        return PInvoke.PostThreadMessage(threadId, WM_QUIT, default, default);
    }

    public bool GetMessage(out NativeMsg msg)
    {
        BOOL ret = PInvoke.GetMessage(out var m, default, 0, 0);
        msg = new NativeMsg(m.message, (nuint)m.wParam.Value, (nint)m.lParam.Value);
        if (ret)
        {
            PInvoke.TranslateMessage(in m);
            PInvoke.DispatchMessage(in m);
            return true;
        }

        return false;
    }

    public int GetLastError()
    {
        return Marshal.GetLastPInvokeError();
    }
}
