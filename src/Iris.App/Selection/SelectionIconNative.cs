using System.Runtime.InteropServices;
using System.Windows;
using Iris.App.Translation;
using Iris.Core.Imaging;
using Iris.Core.Platform;

namespace Iris.App.Selection;

internal interface ISelectionIconSystem
{
    nint GetStyle(nint handle);
    void SetStyle(nint handle, nint value);
    nint ForegroundWindow();
    uint ProcessId(nint handle);
    void Show(Window window);
    void Hide(Window window);
    void Close(Window window);
}

internal sealed class SelectionIconNative(ISelectionIconSystem system, INativeWindowPlacementAdapter placement)
{
    internal void Configure(nint handle) => system.SetStyle(handle, system.GetStyle(handle) | 0x08000080); // NOACTIVATE | TOOLWINDOW
    internal bool IsForeground(ForegroundAppInfo source) => source.WindowHandle != 0 && source.ProcessId > 0 &&
        system.ForegroundWindow() == source.WindowHandle && system.ProcessId(source.WindowHandle) == (uint)source.ProcessId;
    internal void Show(Window window, nint handle, PixelRect bounds)
    {
        if (!placement.SetWindowBounds(handle, bounds, show: false, activate: false)) throw new InvalidOperationException("Icon placement failed.");
        system.Show(window);
        if (!placement.SetWindowBounds(handle, bounds, show: true, activate: false)) throw new InvalidOperationException("Icon placement failed.");
    }
    internal void Hide(Window window) => system.Hide(window);
    internal void Close(Window window) => system.Close(window);
}

internal sealed class SelectionIconSystem : ISelectionIconSystem
{
    public nint GetStyle(nint handle)
    {
        Marshal.SetLastPInvokeError(0);
        nint style = Environment.Is64BitProcess ? GetWindowLongPtr(handle, -20) : GetWindowLong(handle, -20);
        if (style == 0 && Marshal.GetLastPInvokeError() != 0) throw new InvalidOperationException("Icon style query failed.");
        return style;
    }
    public void SetStyle(nint handle, nint value)
    {
        Marshal.SetLastPInvokeError(0);
        nint previous = Environment.Is64BitProcess ? SetWindowLongPtr(handle, -20, value) : SetWindowLong(handle, -20, (int)value);
        if (previous == 0 && Marshal.GetLastPInvokeError() != 0) throw new InvalidOperationException("Icon style write failed.");
    }
    public nint ForegroundWindow() => GetForegroundWindow();
    public uint ProcessId(nint handle) => GetWindowThreadProcessId(handle, out uint pid) == 0 ? 0 : pid;
    public void Show(Window window) => window.Show();
    public void Hide(Window window) => window.Hide();
    public void Close(Window window) => window.Close();

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)] private static extern nint GetWindowLongPtr(nint hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)] private static extern int GetWindowLong(nint hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)] private static extern nint SetWindowLongPtr(nint hwnd, int index, nint value);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)] private static extern int SetWindowLong(nint hwnd, int index, int value);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hwnd, out uint pid);
}
