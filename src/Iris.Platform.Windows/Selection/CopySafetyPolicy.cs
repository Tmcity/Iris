using Iris.Core.Platform;

namespace Iris.Platform.Windows.Selection;

internal readonly record struct CopyFocusState(nint ActiveWindow, nint FocusedWindow, uint Flags);
internal readonly record struct CopyFocusPermit(nint SourceWindow, uint ProcessId, nint FocusedWindow, bool AllowCustomFocus = false)
{
    public override string ToString() => "CopyFocusPermit";
}

internal interface ICopySafetyNative
{
    uint CurrentProcessId();
    bool IsWindow(nint window);
    bool IsVisible(nint window);
    bool IsEnabled(nint window);
    uint ProcessId(nint window);
    bool IsChild(nint parent, nint child);
    CopyFocusState? GetFocus(nint source);
    string GetClassName(nint window);
    nint GetStyle(nint window);
}

internal sealed class CopySafetyPolicy(ICopySafetyNative native)
{
    public bool IsOwnProcess(int pid) => pid <= 0 || checked((uint)pid) == native.CurrentProcessId();

    public CopyFocusPermit? Capture(ForegroundAppInfo source, bool allowCustomWhenUiaUnsupported = false)
    {
        uint pid = checked((uint)source.ProcessId);
        nint window = source.WindowHandle;
        if (IsOwnProcess(source.ProcessId) || !native.IsWindow(window) || native.ProcessId(window) != pid
            || !native.IsVisible(window) || !native.IsEnabled(window)) return null;
        string hostClass = native.GetClassName(window);
        if (IsTerminalWindowClass(hostClass)) return null;
        var focus = native.GetFocus(window);
        if (focus == null || focus.Value.ActiveWindow != window || (focus.Value.Flags & 0x1E) != 0) return null;
        nint target = focus.Value.FocusedWindow;
        if (target == 0 && allowCustomWhenUiaUnsupported) target = window;
        if (target == 0 || !native.IsWindow(target) || native.ProcessId(target) != pid
            || (target != window && !native.IsChild(window, target)) || !native.IsVisible(target) || !native.IsEnabled(target)) return null;
        string name = native.GetClassName(target);
        if (IsTerminalWindowClass(name)) return null;
        if (!allowCustomWhenUiaUnsupported && !new[] { "Edit", "RichEdit", "RichEdit20A", "RichEdit20W", "RICHEDIT50W", "RichEditD2DPT" }
            .Contains(name, StringComparer.OrdinalIgnoreCase)) return null;
        if ((native.GetStyle(target) & 0x20) != 0) return null; // ES_PASSWORD, including Rich Edit.
        return new(window, pid, target, allowCustomWhenUiaUnsupported);
    }

    public bool IsValid(ForegroundAppInfo source, CopyFocusPermit permit) => Capture(source, permit.AllowCustomFocus) == permit;

    private static bool IsTerminalWindowClass(string className) =>
        string.Equals(className, "ConsoleWindowClass", StringComparison.OrdinalIgnoreCase)
        || string.Equals(className, "CASCADIA_HOSTING_WINDOW_CLASS", StringComparison.OrdinalIgnoreCase);
}
