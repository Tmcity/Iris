using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Iris.Platform.Windows.Hooks;

// Only external calls are substitutable. NativeMouseAdapter retains marshaling, flags,
// message filtering, call order and callback/delegate ownership in tests as in production.
internal interface IMouseHookApi
{
    void PeekMessage();
    uint CurrentThreadId();
    uint CurrentProcessId();
    nint ModuleHandle();
    nint LoadCursor(nint instance, int resource);
    nint SetHook(int hookId, HOOKPROC callback, nint module, uint threadId);
    bool Unhook(nint hook);
    nint CallNext(int code, nuint message, nint data);
    int GetMessage(out MSG message);
    void TranslateMessage(in MSG message);
    void DispatchMessage(in MSG message);
    bool PostThreadMessage(uint thread, uint message);
    bool GetCursorInfo(ref CURSORINFO cursor);
    short GetAsyncKeyState(int key);
    nint ForegroundWindow();
    uint WindowThreadProcessId(nint window, out uint processId);
    uint TickCount();
    uint DoubleClickTime();
    int SystemMetricsForDpi(int index, uint dpi);
    int LastError();
}

internal sealed class MouseHookRegistration
{
    internal required HOOKPROC Callback { get; init; }
    internal nint Handle { get; set; }
    internal bool Unhooked { get; set; }
}

internal interface IMouseThreadFactory
{
    Thread Create(ThreadStart action);
}

internal sealed class MouseThreadFactory : IMouseThreadFactory
{
    public Thread Create(ThreadStart action) => new(action) { IsBackground = true, Name = "Iris.MouseHook" };
}
