using System;

namespace Iris.Platform.Windows.Hotkey;

internal delegate nint NativeWndProc(nint hwnd, uint msg, nuint wParam, nint lParam);

/// <summary>
/// 隔离 Win32 热键与消息窗口底层调用的窄适配器。
/// 生产环境使用 NativeHotkeyAdapter，测试通过 Fake 适配器注入故障与模拟消息。
/// </summary>
internal interface INativeHotkeyAdapter
{
    uint GetCurrentThreadId();

    ushort RegisterClass(string className, NativeWndProc wndProc);

    bool UnregisterClass(string className);

    nint CreateMessageWindow(string className);

    bool DestroyWindow(nint hwnd);

    nint DefWindowProc(nint hwnd, uint msg, nuint wParam, nint lParam);

    bool RegisterHotKey(nint hwnd, int id, uint fsModifiers, uint vk);

    bool UnregisterHotKey(nint hwnd, int id);

    int GetLastError();
}
