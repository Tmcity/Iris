using Windows.Win32.UI.Input.KeyboardAndMouse;

namespace Iris.Platform.Windows.Selection;

/// <summary>
/// 窄 Win32 键盘与窗口原生调用适配器，供 KeyboardSimulator 与测试替换。
/// 构造与 DI 解析零桌面 I/O。
/// </summary>
internal interface INativeKeyboardInputAdapter
{
    short GetAsyncKeyState(int vKey);
    nint GetForegroundWindow();
    uint GetWindowProcessId(nint windowHandle);
    uint GetCurrentProcessId();
    bool IsWindow(nint windowHandle);
    uint SendInput(uint cInputs, INPUT[] inputs, int cbSize);
    int GetLastError();
}
