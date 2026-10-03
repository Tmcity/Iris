using System;

namespace Iris.Platform.Windows.Hotkey;

internal delegate nint NativeKeyboardHookProc(int nCode, nuint wParam, nint lParam);

internal readonly record struct NativeMsg(uint Message, nuint WParam, nint LParam);

/// <summary>
/// 隔离 Win32 低级键盘钩子与消息循环底层调用的窄适配器。
/// 生产环境使用 NativeKeyboardHookAdapter，测试通过 Fake 适配器注入按键、系统键与窗口状态。
/// </summary>
internal interface INativeKeyboardHookAdapter
{
    nint SetWindowsHook(NativeKeyboardHookProc proc);

    bool UnhookWindowsHook(nint hookHandle);

    nint CallNextHook(nint hookHandle, int nCode, nuint wParam, nint lParam);

    nint GetForegroundWindow();

    uint GetWindowProcessId(nint windowHandle);

    uint GetCurrentProcessId();

    bool IsWindow(nint windowHandle);

    bool IsKeyDown(int virtualKey);

    uint GetCurrentThreadId();

    bool PostThreadQuitMessage(uint threadId);

    void EnsureThreadMessageQueueCreated();

    void ReleaseHookResources(nint hookHandle);

    int GetMessage(out NativeMsg msg);

    int GetLastError();
}
