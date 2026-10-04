using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Iris.Platform.Windows.Selection;

internal sealed unsafe partial class NativeCopySafetyAdapter : ICopySafetyNative
{
    internal static readonly NativeCopySafetyAdapter Instance = new();
    private static HWND Window(nint window) => new((void*)window);
    public uint CurrentProcessId() => PInvoke.GetCurrentProcessId();
    public bool IsWindow(nint window) => PInvoke.IsWindow(Window(window));
    public bool IsVisible(nint window) => PInvoke.IsWindowVisible(Window(window));
    public bool IsEnabled(nint window) => PInvoke.IsWindowEnabled(Window(window));
    public uint ProcessId(nint window)
    {
        uint pid = 0;
        if (PInvoke.GetWindowThreadProcessId(Window(window), &pid) == 0) return 0;
        return pid;
    }
    public bool IsChild(nint parent, nint child) => PInvoke.IsChild(Window(parent), Window(child));
    public CopyFocusState? GetFocus(nint source)
    {
        uint thread = PInvoke.GetWindowThreadProcessId(Window(source), null);
        if (thread == 0) return null;
        var info = new GUITHREADINFO { cbSize = (uint)sizeof(GUITHREADINFO) };
        if (!PInvoke.GetGUIThreadInfo(thread, &info)) return null;
        return new((nint)info.hwndActive.Value, (nint)info.hwndFocus.Value, (uint)info.flags);
    }
    public string GetClassName(nint window)
    {
        Span<char> name = stackalloc char[256];
        int count = PInvoke.GetClassName(Window(window), name);
        if (count <= 0) throw new InvalidOperationException("FOCUS_CLASS_FAILED");
        return new string(name[..count]);
    }
    public nint GetStyle(nint window)
    {
        PInvoke.SetLastError(0);
        nint style = Environment.Is64BitProcess
            ? GetWindowLongPtr64(window, -16)
            : GetWindowLong32(window, -16);
        if (style == 0 && Marshal.GetLastPInvokeError() != 0) throw new InvalidOperationException("FOCUS_STYLE_FAILED");
        return style;
    }

    // CsWin32's pointer-width export cannot be generated for this AnyCPU assembly.
    // Preserve the documented x86 macro mapping without changing the project target.
    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static partial nint GetWindowLongPtr64(nint window, int index);
    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
    private static partial int GetWindowLong32(nint window, int index);
}
