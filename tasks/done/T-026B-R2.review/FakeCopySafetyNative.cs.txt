using Iris.Platform.Windows.Selection;

namespace Iris.Platform.Windows.Tests.Selection;

internal sealed class FakeCopySafetyNative : ICopySafetyNative
{
    public nint Source { get; set; } = 0x1234;
    public nint Focus { get; set; } = 0x5678;
    public uint Pid { get; set; } = 5000;
    public string ClassName { get; set; } = "Edit";
    public string SourceClassName { get; set; } = "AppWindow";
    public nint Style { get; set; }
    public uint Flags { get; set; }
    public bool Visible { get; set; } = true;
    public bool Enabled { get; set; } = true;
    public bool Child { get; set; } = true;
    public bool Valid { get; set; } = true;
    public bool HasFocus { get; set; } = true;
    public uint OwnPid { get; set; } = 1000;
    public int Calls { get; private set; }
    public Action<string>? Before { get; set; }
    private void Call(string name) { Calls++; Before?.Invoke(name); }
    public uint CurrentProcessId() { Call("CurrentPid"); return OwnPid; }
    public bool IsWindow(nint window) { Call("IsWindow"); return Valid; }
    public bool IsVisible(nint window) { Call("Visible"); return Visible; }
    public bool IsEnabled(nint window) { Call("Enabled"); return Enabled; }
    public uint ProcessId(nint window) { Call("Pid"); return Pid; }
    public bool IsChild(nint parent, nint child) { Call("Child"); return Child; }
    public CopyFocusState? GetFocus(nint source) { Call("Focus"); return HasFocus ? new(Source, Focus, Flags) : null; }
    public string GetClassName(nint window) { Call("Class"); return window == Source ? SourceClassName : ClassName; }
    public nint GetStyle(nint window) { Call("Style"); return Style; }
}
