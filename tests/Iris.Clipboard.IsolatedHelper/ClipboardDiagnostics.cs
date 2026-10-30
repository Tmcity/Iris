using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using Iris.Core.Errors;
using Iris.Platform.Windows.Clipboard;

namespace Iris.Clipboard.IsolatedHelper;

// Test-only instrumentation. Every clipboard result is from the frozen product native adapter.
internal static class ClipboardDiagnostics
{
    private static readonly object s_logGate = new();
    private static int s_sequence;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint GetOpenClipboardWindow();
    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint GetClipboardOwner();
    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(nint window, out uint process);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetUserObjectInformationW(nint handle, int index, char[] buffer, uint bytes, out uint needed);

    internal static string ObjectName(nint handle)
    {
        var buffer = new char[256];
        if (handle == 0 || !GetUserObjectInformationW(handle, 2, buffer, (uint)(buffer.Length * sizeof(char)), out _))
            throw new InvalidOperationException("Cannot verify native station/desktop name.");
        return new string(buffer, 0, Array.IndexOf(buffer, '\0'));
    }

    internal static void Log(string role, string action, object? value = null, int error = 0)
    {
        uint tid = Program.GetCurrentThreadId();
        nint station = Program.GetProcessWindowStation(), desktop = Program.GetThreadDesktop(tid);
        nint openWindow = GetOpenClipboardWindow(), dataOwner = GetClipboardOwner();
        uint openPid = 0, dataPid = 0;
        uint openTid = openWindow == 0 ? 0 : GetWindowThreadProcessId(openWindow, out openPid);
        uint dataTid = dataOwner == 0 ? 0 : GetWindowThreadProcessId(dataOwner, out dataPid);
        lock (s_logGate)
        {
            Console.WriteLine(JsonSerializer.Serialize(new { Sequence = ++s_sequence, Utc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                Role = role, Action = action, Pid = Environment.ProcessId, Tid = tid, Station = station.ToInt64(), StationName = ObjectName(station),
                Desktop = desktop.ToInt64(), DesktopName = ObjectName(desktop), OpenWindow = openWindow.ToInt64(), OpenWindowTid = openTid, OpenWindowPid = openPid,
                DataOwner = dataOwner.ToInt64(), DataOwnerTid = dataTid, DataOwnerPid = dataPid, Error = error, Value = value }));
        }
    }

    internal static void Bind(nint desktop)
    {
        bool bound = Program.SetThreadDesktop(desktop);
        int error = Marshal.GetLastWin32Error();
        if (!bound) throw new InvalidOperationException("Diagnostic desktop binding failed: " + error.ToString(CultureInfo.InvariantCulture));
        if (ObjectName(Program.GetThreadDesktop(Program.GetCurrentThreadId())) != ObjectName(desktop))
            throw new InvalidOperationException("Diagnostic desktop identity differs.");
    }

    public static (bool Success, string? ErrorMessage) Run(IIsolatedStationEnvironment environment)
    {
        nint desktop = environment.GetThreadDesktop(environment.GetCurrentThreadId());
        Log("coordinator", "diagnostic-start", new { FrozenProduct = typeof(Win32ClipboardService).Assembly.Location });
        try
        {
            bool nullAllowsOther = NullControl(desktop);
            bool realOccupancyRejected = WindowOccupancy(desktop);
            Log("coordinator", "diagnostic-summary", new { NullAllowsIndependentOpen = nullAllowsOther, WindowOccupancyRejected = realOccupancyRejected,
                OriginalNullConditionProvedExclusive = !nullAllowsOther, OriginalAssertionNotReclassifiedAsPass = true });
            return nullAllowsOther && realOccupancyRejected ? (true, null) : (false, "Control outcomes did not establish the fixture cause.");
        }
        catch (Exception error)
        {
            return (false, error.ToString());
        }
    }

    private static bool IndependentOpenProbe(nint desktop, string role)
    {
        bool opened = false;
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            nint original = Program.GetThreadDesktop(Program.GetCurrentThreadId());
            try
            {
                Bind(desktop);
                opened = NativeClipboardAdapter.Instance.OpenClipboard(0);
                int error = NativeClipboardAdapter.Instance.GetLastError();
                Log(role, "OpenClipboard-null", opened, error);
                if (opened)
                {
                    bool closed = NativeClipboardAdapter.Instance.CloseClipboard();
                    error = NativeClipboardAdapter.Instance.GetLastError();
                    Log(role, "CloseClipboard", closed, error);
                    if (!closed) throw new InvalidOperationException("Independent probe failed to close its actual Open.");
                }
            }
            catch (Exception error) { failure = error; }
            finally
            {
                try { Bind(original); Log(role, "thread-restored"); }
                catch (Exception error) { failure = failure == null ? error : new AggregateException(failure, error); }
            }
        });
        thread.SetApartmentState(ApartmentState.MTA);
        thread.Start(); thread.Join();
        Log("coordinator", role + "-joined", new { Joined = !thread.IsAlive });
        if (failure != null) throw failure;
        return opened;
    }

    private static bool NullControl(nint desktop)
    {
        bool opened = NativeClipboardAdapter.Instance.OpenClipboard(0);
        int error = NativeClipboardAdapter.Instance.GetLastError();
        Log("original-null-holder", "OpenClipboard-null", opened, error);
        if (!opened) throw new InvalidOperationException("Original NULL control did not open.");
        bool competitorOpened;
        try { competitorOpened = IndependentOpenProbe(desktop, "null-competitor"); }
        finally
        {
            // The independent NULL Open/Close can invalidate this original NULL condition.
            bool closed = NativeClipboardAdapter.Instance.CloseClipboard();
            error = NativeClipboardAdapter.Instance.GetLastError();
            Log("original-null-holder", "CloseClipboard-after-competitor", closed, error);
        }
        return competitorOpened;
    }

    internal static bool WindowOccupancy(nint desktop, Win32ClipboardService? service = null)
    {
        bool rejected = false;
        var native = new RealClipboardOccupancyNative(desktop);
        ClipboardOccupancyCoordinator.Run(native, () =>
        {
            if (IndependentOpenProbe(desktop, "window-competitor")) throw new InvalidOperationException("HWND occupancy did not exclude independent Open.");
            service ??= new Win32ClipboardService(null, null, new DiagnosticClipboardAdapter(desktop));
            using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
            using var registration = cancellation.Token.Register(() => Log("cancellation", "accepted"));
            try { service.GetTextAsync(cancellation.Token).GetAwaiter().GetResult(); }
            catch (OperationCanceledException) { rejected = true; Log("coordinator", "service-canceled-and-work-ended"); }
            catch (IrisException error) when (error.Code == ErrorCode.ClipboardBusy) { rejected = true; Log("coordinator", "service-busy-and-work-ended"); }
            Log("coordinator", "service-task-completed-before-holder-release", new { Rejected = rejected });
            if (!rejected) throw new InvalidOperationException("Expected cancellation/busy when occupied.");
        }, TimeSpan.FromSeconds(5));
        return rejected;
    }
}

internal sealed class RealClipboardOccupancyNative(nint desktop) : IClipboardOccupancyNative
{
    private nint _original;
    public void Bind() { _original = Program.GetThreadDesktop(Program.GetCurrentThreadId()); ClipboardDiagnostics.Bind(desktop); }
    public nint CreateWindow() { nint value = NativeClipboardAdapter.Instance.CreateOwnerWindow(); int error = NativeClipboardAdapter.Instance.GetLastError(); ClipboardDiagnostics.Log("window-holder", "CreateOwnerWindow", value.ToInt64(), error); return value; }
    public bool Open(nint window) { bool value = NativeClipboardAdapter.Instance.OpenClipboard(window); int error = NativeClipboardAdapter.Instance.GetLastError(); ClipboardDiagnostics.Log("window-holder", "OpenClipboard-window", value, error); return value; }
    public bool Close() { bool value = NativeClipboardAdapter.Instance.CloseClipboard(); int error = NativeClipboardAdapter.Instance.GetLastError(); ClipboardDiagnostics.Log("window-holder", "CloseClipboard", value, error); return value; }
    public bool Destroy(nint window) { bool value = NativeClipboardAdapter.Instance.DestroyWindow(window); int error = NativeClipboardAdapter.Instance.GetLastError(); ClipboardDiagnostics.Log("window-holder", "DestroyWindow", value, error); return value; }
    public void Restore() { if (_original != 0) ClipboardDiagnostics.Bind(_original); ClipboardDiagnostics.Log("window-holder", "thread-restored"); }
    public void Record(string action) => ClipboardDiagnostics.Log("occupancy-protocol", action);
}

internal sealed class DiagnosticClipboardAdapter(nint desktop) : INativeClipboardAdapter
{
    [ThreadStatic] private static int s_savedError;
    [ThreadStatic] private static nint s_previousDesktop;
    [ThreadStatic] private static nint s_window;

    private void BindFrame()
    {
        if (s_previousDesktop != 0) return;
        nint previous = Program.GetThreadDesktop(Program.GetCurrentThreadId());
        ClipboardDiagnostics.Bind(desktop);
        s_previousDesktop = previous;
        ClipboardDiagnostics.Log("service-native", "frame-bound-before-native");
    }
    private static void Restore()
    {
        if (s_previousDesktop == 0) return;
        ClipboardDiagnostics.Bind(s_previousDesktop); s_previousDesktop = 0;
        ClipboardDiagnostics.Log("service-native", "frame-restored-after-native");
    }
    private static T Call<T>(string name, Func<T> actual)
    {
        T value = actual();
        int error = NativeClipboardAdapter.Instance.GetLastError(); // Capture before any diagnostic P/Invoke/logging.
        s_savedError = error;
        object? logged = value is nint pointer ? pointer.ToInt64() : value is nuint size ? size.ToUInt64() : value;
        ClipboardDiagnostics.Log("service-native", name, logged, error);
        return value;
    }
    public nint CreateOwnerWindow() { BindFrame(); s_window = Call("CreateOwnerWindow", NativeClipboardAdapter.Instance.CreateOwnerWindow); if (s_window == 0) Restore(); return s_window; }
    public bool DestroyWindow(nint window) { bool result = Call("DestroyWindow", () => NativeClipboardAdapter.Instance.DestroyWindow(window)); if (result) { s_window = 0; Restore(); } return result; }
    public bool OpenClipboard(nint window) { BindFrame(); bool result = Call("OpenClipboard", () => NativeClipboardAdapter.Instance.OpenClipboard(window)); if (!result && s_window == 0) Restore(); return result; }
    public bool CloseClipboard() { bool result = Call("CloseClipboard", NativeClipboardAdapter.Instance.CloseClipboard); if (result && s_window == 0) Restore(); return result; }
    public bool EmptyClipboard() => Call("EmptyClipboard", NativeClipboardAdapter.Instance.EmptyClipboard);
    public nint SetClipboardData(uint format, nint memory) => Call("SetClipboardData", () => NativeClipboardAdapter.Instance.SetClipboardData(format, memory));
    public nint GetClipboardData(uint format) => Call("GetClipboardData", () => NativeClipboardAdapter.Instance.GetClipboardData(format));
    public bool IsClipboardFormatAvailable(uint format) => Call("IsClipboardFormatAvailable", () => NativeClipboardAdapter.Instance.IsClipboardFormatAvailable(format));
    public uint GetClipboardSequenceNumber() => Call("GetClipboardSequenceNumber", NativeClipboardAdapter.Instance.GetClipboardSequenceNumber);
    public nint GlobalAlloc(uint flags, nuint bytes) => Call("GlobalAlloc", () => NativeClipboardAdapter.Instance.GlobalAlloc(flags, bytes));
    public nint GlobalFree(nint memory) => Call("GlobalFree", () => NativeClipboardAdapter.Instance.GlobalFree(memory));
    public nint GlobalLock(nint memory) => Call("GlobalLock", () => NativeClipboardAdapter.Instance.GlobalLock(memory));
    public bool GlobalUnlock(nint memory) => Call("GlobalUnlock", () => NativeClipboardAdapter.Instance.GlobalUnlock(memory));
    public nuint GlobalSize(nint memory) => Call("GlobalSize", () => NativeClipboardAdapter.Instance.GlobalSize(memory));
    public int GetLastError() => s_savedError;
}
