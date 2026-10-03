using System;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Input.KeyboardAndMouse;

namespace Iris.Platform.Windows.Selection;

/// <summary>
/// 真实 Win32 键盘原生输入适配器。
/// 仅用于实际运行时；构造及注册时不产生任何原生调用或桌面副作用。
/// </summary>
internal sealed unsafe class Win32KeyboardInputAdapter : INativeKeyboardInputAdapter
{
    public static readonly Win32KeyboardInputAdapter Instance = new();

    internal Win32KeyboardInputAdapter()
    {
    }

    public short GetAsyncKeyState(int vKey)
    {
        return PInvoke.GetAsyncKeyState(vKey);
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

    public uint SendInput(uint cInputs, INPUT[] inputs, int cbSize)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        if (cInputs == 0)
        {
            return 0;
        }

        return PInvoke.SendInput(inputs.AsSpan(0, (int)cInputs), cbSize);
    }

    public int GetLastError()
    {
        return Marshal.GetLastPInvokeError();
    }
}
