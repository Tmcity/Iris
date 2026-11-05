using System;
using System.Collections.Generic;
using Iris.Platform.Windows.Hotkey;

namespace Iris.App.Tests.TestSupport;

internal sealed class FakeNativeHotkeyAdapter : INativeHotkeyAdapter
{
    public uint CurrentThreadId { get; set; } = 1000;
    public int LastError { get; set; }
    public NativeWndProc? CapturedWndProc { get; private set; }

    public bool FailRegisterHotKey { get; set; }
    public bool FailUnregisterHotKey { get; set; }
    public bool FailDestroyWindow { get; set; }

    public int RegisterHotKeyCallCount { get; private set; }
    public int UnregisterHotKeyCallCount { get; private set; }

    public List<(nint Hwnd, int Id, uint Modifiers, uint Vk)> RegisteredHotkeys { get; } = [];
    public List<(nint Hwnd, int Id)> UnregisteredHotkeys { get; } = [];

    public uint GetCurrentThreadId() => CurrentThreadId;

    public ushort RegisterClass(string className, NativeWndProc wndProc)
    {
        CapturedWndProc = wndProc;
        return 42;
    }

    public bool UnregisterClass(string className) => true;

    public nint CreateMessageWindow(string className) => 0xCAFE;

    public bool DestroyWindow(nint hwnd)
    {
        if (FailDestroyWindow)
        {
            LastError = 1004;
            return false;
        }
        return true;
    }

    public nint DefWindowProc(nint hwnd, uint msg, nuint wParam, nint lParam) => 0;

    public bool RegisterHotKey(nint hwnd, int id, uint fsModifiers, uint vk)
    {
        RegisterHotKeyCallCount++;
        if (FailRegisterHotKey)
        {
            if (LastError == 0) LastError = 1409;
            return false;
        }

        RegisteredHotkeys.Add((hwnd, id, fsModifiers, vk));
        return true;
    }

    public bool UnregisterHotKey(nint hwnd, int id)
    {
        UnregisterHotKeyCallCount++;
        if (FailUnregisterHotKey)
        {
            if (LastError == 0) LastError = 1400;
            return false;
        }

        UnregisteredHotkeys.Add((hwnd, id));
        return true;
    }

    public int GetLastError() => LastError;

    public void SendWmHotkey(int id, uint modifiers, uint vk)
    {
        if (CapturedWndProc != null)
        {
            CapturedWndProc(0xCAFE, 0x0312, (nuint)id, (nint)((vk << 16) | (modifiers & 0xFFFF)));
        }
    }
}
