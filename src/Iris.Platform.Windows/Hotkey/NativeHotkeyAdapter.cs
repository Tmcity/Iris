using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using global::Windows.Win32;
using global::Windows.Win32.Foundation;
using global::Windows.Win32.UI.Input.KeyboardAndMouse;
using global::Windows.Win32.UI.WindowsAndMessaging;

namespace Iris.Platform.Windows.Hotkey;

/// <summary>
/// 真实 Win32 热键与消息窗口适配器实现，使用 CsWin32 封装。
/// </summary>
internal sealed unsafe class NativeHotkeyAdapter : INativeHotkeyAdapter
{
    public static readonly NativeHotkeyAdapter Instance = new();

    private readonly ConcurrentDictionary<string, WNDPROC> _activeDelegates = new(StringComparer.Ordinal);

    public uint GetCurrentThreadId()
    {
        return PInvoke.GetCurrentThreadId();
    }

    public ushort RegisterClass(string className, NativeWndProc wndProc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(className);
        ArgumentNullException.ThrowIfNull(wndProc);

        using var hMod = PInvoke.GetModuleHandle((string?)null);
        var hInstance = (HINSTANCE)hMod.DangerousGetHandle();

        WNDPROC nativeDelegate = (hwnd, msg, wParam, lParam) =>
        {
            return new LRESULT(wndProc((nint)hwnd.Value, msg, (nuint)wParam.Value, (nint)lParam.Value));
        };

        _activeDelegates[className] = nativeDelegate;

        WNDCLASSEXW wc = default;
        wc.cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>();
        wc.lpfnWndProc = nativeDelegate;
        wc.hInstance = hInstance;

        fixed (char* pClassName = className)
        {
            wc.lpszClassName = pClassName;
            ushort atom = PInvoke.RegisterClassEx(wc);
            if (atom == 0)
            {
                _activeDelegates.TryRemove(className, out _);
            }
            return atom;
        }
    }

    public bool UnregisterClass(string className)
    {
        if (string.IsNullOrWhiteSpace(className))
        {
            return true;
        }

        using var hMod = PInvoke.GetModuleHandle((string?)null);
        var hInstance = (HINSTANCE)hMod.DangerousGetHandle();

        bool success;
        fixed (char* pClassName = className)
        {
            success = PInvoke.UnregisterClass(pClassName, hInstance);
        }

        if (success)
        {
            _activeDelegates.TryRemove(className, out _);
        }

        return success;
    }

    internal bool HasActiveDelegate(string className)
    {
        return _activeDelegates.ContainsKey(className);
    }

    public nint CreateMessageWindow(string className)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(className);

        using var hMod = PInvoke.GetModuleHandle((string?)null);
        var hInstance = (HINSTANCE)hMod.DangerousGetHandle();

        fixed (char* pClassName = className)
        fixed (char* pWindowName = "")
        {
            var hwnd = PInvoke.CreateWindowEx(
                WINDOW_EX_STYLE.WS_EX_LEFT,
                pClassName,
                pWindowName,
                WINDOW_STYLE.WS_OVERLAPPED,
                0, 0, 0, 0,
                new HWND((void*)(-3)), // HWND_MESSAGE
                default,
                hInstance,
                null);

            return (nint)hwnd.Value;
        }
    }

    public bool DestroyWindow(nint hwnd)
    {
        if (hwnd == 0)
        {
            return true;
        }

        return PInvoke.DestroyWindow(new HWND((void*)hwnd));
    }

    public nint DefWindowProc(nint hwnd, uint msg, nuint wParam, nint lParam)
    {
        var res = PInvoke.DefWindowProc(new HWND((void*)hwnd), msg, new WPARAM(wParam), new LPARAM(lParam));
        return (nint)res.Value;
    }

    public bool RegisterHotKey(nint hwnd, int id, uint fsModifiers, uint vk)
    {
        return PInvoke.RegisterHotKey(new HWND((void*)hwnd), id, (HOT_KEY_MODIFIERS)fsModifiers, vk);
    }

    public bool UnregisterHotKey(nint hwnd, int id)
    {
        return PInvoke.UnregisterHotKey(new HWND((void*)hwnd), id);
    }

    public int GetLastError()
    {
        return Marshal.GetLastPInvokeError();
    }
}
