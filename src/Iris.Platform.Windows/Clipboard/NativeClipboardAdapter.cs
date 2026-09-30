using System;
using System.Runtime.InteropServices;
using global::Windows.Win32;
using global::Windows.Win32.Foundation;
using global::Windows.Win32.System.Memory;
using global::Windows.Win32.UI.WindowsAndMessaging;

namespace Iris.Platform.Windows.Clipboard;

internal unsafe sealed class NativeClipboardAdapter : INativeClipboardAdapter
{
    public static readonly NativeClipboardAdapter Instance = new();

    public nint CreateOwnerWindow()
    {
        fixed (char* pClassName = "STATIC")
        fixed (char* pWindowName = "")
        {
            var hwnd = PInvoke.CreateWindowEx(
                WINDOW_EX_STYLE.WS_EX_LEFT,
                pClassName,
                pWindowName,
                WINDOW_STYLE.WS_OVERLAPPED,
                0, 0, 0, 0,
                new HWND((void*)(-3)), // HWND_MESSAGE = ((HWND)-3)
                default,
                default,
                null);

            return (nint)hwnd.Value;
        }
    }

    public bool DestroyWindow(nint hwnd)
    {
        if (hwnd != 0)
        {
            return PInvoke.DestroyWindow(new HWND((void*)hwnd));
        }
        return true;
    }

    public bool OpenClipboard(nint hwnd)
    {
        return PInvoke.OpenClipboard(new HWND((void*)hwnd));
    }

    public bool CloseClipboard()
    {
        return PInvoke.CloseClipboard();
    }

    public bool EmptyClipboard()
    {
        return PInvoke.EmptyClipboard();
    }

    public nint SetClipboardData(uint format, nint hMem)
    {
        var result = PInvoke.SetClipboardData(format, new HANDLE((void*)hMem));
        return (nint)result.Value;
    }

    public nint GetClipboardData(uint format)
    {
        var result = PInvoke.GetClipboardData(format);
        return (nint)result.Value;
    }

    public bool IsClipboardFormatAvailable(uint format)
    {
        return PInvoke.IsClipboardFormatAvailable(format);
    }

    public uint GetClipboardSequenceNumber()
    {
        return PInvoke.GetClipboardSequenceNumber();
    }

    public nint GlobalAlloc(uint uFlags, nuint dwBytes)
    {
        var result = PInvoke.GlobalAlloc((GLOBAL_ALLOC_FLAGS)uFlags, dwBytes);
        return (nint)result.Value;
    }

    public nint GlobalFree(nint hMem)
    {
        var result = PInvoke.GlobalFree(new HGLOBAL((void*)hMem));
        return (nint)result.Value;
    }

    public nint GlobalLock(nint hMem)
    {
        void* ptr = PInvoke.GlobalLock(new HGLOBAL((void*)hMem));
        return (nint)ptr;
    }

    public bool GlobalUnlock(nint hMem)
    {
        return PInvoke.GlobalUnlock(new HGLOBAL((void*)hMem));
    }

    public nuint GlobalSize(nint hMem)
    {
        return PInvoke.GlobalSize(new HGLOBAL((void*)hMem));
    }

    public int GetLastError()
    {
        return Marshal.GetLastPInvokeError();
    }
}
