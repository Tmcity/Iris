using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Iris.Core.Imaging;
using Iris.Core.Platform;
using Iris.Platform.Windows.Hooks;
using Microsoft.Extensions.Logging.Abstractions;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Iris.Platform.Windows.Tests.Hooks;

internal sealed class MouseGate : IDisposable
{
    internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ManualResetEventSlim _release = new();
    internal void Block() { Entered.TrySetResult(); _release.Wait(); }
    internal void Release() => _release.Set();
    public void Dispose() => _release.Dispose();
}

// Fake only the OS calls. Every test below uses the real adapter, message thread,
// bounded queue, recognizer and lifecycle. No global hook or user desktop is touched.
internal sealed class FakeMouseHookApi : IMouseHookApi, IDisposable
{
    private sealed record Work(Action? Callback, TaskCompletionSource? Completion, int Result = 1);
    private readonly ConcurrentDictionary<uint, BlockingCollection<Work>> _queues = new();
    private readonly ConcurrentDictionary<nint, HOOKPROC> _hooks = new();
    private readonly ConcurrentDictionary<uint, Work> _dispatch = new();
    private int _nextHandle;
    internal uint ThreadId;
    internal Thread? MessageThread;
    internal uint Now = 100;
    internal nint Window = 1000;
    internal uint Process = 42;
    internal uint SelfProcess = 7;
    internal bool CursorSuccess = true;
    internal uint CursorFlags = 1;
    internal nint CursorHandle = 900;
    internal nint LoadedCursor = 900;
    internal short Shift { get; set; }
    internal bool PidSuccess = true;
    internal int UnhookFailures;
    internal int PostFailures;
    internal int QuitResult { get; set; }
    internal bool InstallFailure;
    internal string? ThrowAt;
    internal MouseGate? InstallGate;
    internal MouseGate? NextGate { get; set; }
    internal MouseGate? ExitGate { get; set; }
    internal MouseGate? UnhookGate { get; set; }
    internal Action? OnPost { get; set; }
    internal ConcurrentQueue<(string Name, int Thread)> Calls { get; } = new();
    internal ConcurrentQueue<(int Metric, uint Dpi)> MetricCalls { get; } = new();
    internal int Width = 4;
    internal int Height = 4;
    internal uint ClickTime = 500;
    internal int ActiveHandles => _hooks.Count;
    internal HOOKPROC? LastCallback;
    internal nint LastHandle;
    internal int InstalledId;
    internal nint InstalledModule;
    internal uint InstalledThread;
    internal (nint Instance, int Resource) CursorRequest;
    internal uint CursorSize;
    internal int ShiftKey;
    internal uint QuitMessage;
    internal int NextCode;
    internal nint NextPointer;
    internal nuint NextMessage;

    private void Call(string name)
    {
        Calls.Enqueue((name, Environment.CurrentManagedThreadId));
        if (ThrowAt == name) throw new InvalidOperationException("PRIVATE_TITLE_AND_SOURCE");
    }
    public void PeekMessage()
    {
        Call(nameof(PeekMessage));
        ThreadId = (uint)Environment.CurrentManagedThreadId;
        MessageThread = Thread.CurrentThread;
        _queues.TryAdd(ThreadId, new());
    }
    public uint CurrentThreadId() { Call(nameof(CurrentThreadId)); return (uint)Environment.CurrentManagedThreadId; }
    public uint CurrentProcessId() { Call(nameof(CurrentProcessId)); return SelfProcess; }
    public nint ModuleHandle() { Call(nameof(ModuleHandle)); return 77; }
    public nint LoadCursor(nint instance, int resource) { Call(nameof(LoadCursor)); CursorRequest = (instance, resource); return LoadedCursor; }
    public nint SetHook(int hookId, HOOKPROC callback, nint module, uint threadId)
    {
        Call(nameof(SetHook));
        InstalledId = hookId; InstalledModule = module; InstalledThread = threadId;
        LastCallback = callback;
        InstallGate?.Block();
        if (InstallFailure) return 0;
        LastHandle = Interlocked.Increment(ref _nextHandle);
        _hooks[LastHandle] = callback;
        return LastHandle;
    }
    public bool Unhook(nint hook)
    {
        Call(nameof(Unhook));
        UnhookGate?.Block();
        if (UnhookFailures > 0) { UnhookFailures--; return false; }
        if (!_hooks.TryRemove(hook, out _)) throw new InvalidOperationException("DOUBLE_UNHOOK");
        return true;
    }
    public nint CallNext(int code, nuint message, nint data)
    {
        Call(nameof(CallNext));
        NextCode = code; NextMessage = message; NextPointer = data;
        NextGate?.Block();
        return 123;
    }
    public int GetMessage(out MSG message)
    {
        Call(nameof(GetMessage));
        uint thread = (uint)Environment.CurrentManagedThreadId;
        var work = _queues[thread].Take();
        message = default;
        if (work.Result <= 0) { ExitGate?.Block(); work.Completion?.TrySetResult(); return work.Result; }
        _dispatch[thread] = work;
        return 1;
    }
    public void TranslateMessage(in MSG message) => Call(nameof(TranslateMessage));
    public void DispatchMessage(in MSG message)
    {
        Call(nameof(DispatchMessage));
        if (_dispatch.TryRemove((uint)Environment.CurrentManagedThreadId, out var work))
        {
            try { work.Callback!(); work.Completion!.TrySetResult(); }
            catch (Exception ex) { work.Completion!.TrySetException(ex); }
        }
    }
    public bool PostThreadMessage(uint thread, uint message)
    {
        Call(nameof(PostThreadMessage));
        QuitMessage = message;
        OnPost?.Invoke();
        if (PostFailures > 0) { PostFailures--; return false; }
        _queues[thread].Add(new(null, null, QuitResult));
        return true;
    }
    public bool GetCursorInfo(ref CURSORINFO cursor)
    {
        Call(nameof(GetCursorInfo));
        CursorSize = cursor.cbSize;
        cursor.flags = (CURSORINFO_FLAGS)CursorFlags;
        cursor.hCursor = (HCURSOR)CursorHandle;
        return CursorSuccess;
    }
    public short GetAsyncKeyState(int key) { Call(nameof(GetAsyncKeyState)); ShiftKey = key; return Shift; }
    public nint ForegroundWindow() { Call(nameof(ForegroundWindow)); return Window; }
    public uint WindowThreadProcessId(nint window, out uint processId)
    {
        Call(nameof(WindowThreadProcessId)); processId = Process; return PidSuccess ? 11u : 0;
    }
    public uint TickCount() { Call(nameof(TickCount)); return Now; }
    public uint DoubleClickTime() { Call(nameof(DoubleClickTime)); return ClickTime; }
    public int SystemMetricsForDpi(int index, uint dpi)
    {
        Call(nameof(SystemMetricsForDpi)); MetricCalls.Enqueue((index, dpi)); return index == 36 ? Width : Height;
    }
    public int LastError() => 5;

    internal Task EnqueueAsync(MouseMessage message, int x = 0, int y = 0, uint? time = null, uint flags = 0, int code = 0)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var raw = new MSLLHOOKSTRUCT { pt = new(x, y), time = time ?? Now, flags = flags };
        _queues[ThreadId].Add(new(() => Invoke(LastCallback!, message, raw, code), done));
        return done.Task;
    }
    internal static nint Invoke(HOOKPROC callback, MouseMessage message, MSLLHOOKSTRUCT raw, int code = 0)
    {
        nint pointer = Marshal.AllocHGlobal(Marshal.SizeOf<MSLLHOOKSTRUCT>());
        try { Marshal.StructureToPtr(raw, pointer, false); return callback(code, new WPARAM((nuint)message), new LPARAM(pointer)).Value; }
        finally { Marshal.FreeHGlobal(pointer); }
    }
    internal void FailPump() => _queues[ThreadId].Add(new(null, null, -1));
    public void Dispose() { foreach (var queue in _queues.Values) queue.Dispose(); }
}

internal sealed class FakeMouseDesktop : IDesktopContext
{
    internal ForegroundAppInfo? App = new("editor.exe", 42, "PRIVATE_TITLE_AND_SOURCE", 1000);
    internal double Dpi = 1.5;
    internal MouseGate? SourceGate { get; set; }
    internal Action? AfterSource { get; set; }
    internal ConcurrentQueue<int> Threads { get; } = new();
    internal bool ThrowSource { get; set; }
    public ForegroundAppInfo? GetForegroundApp()
    {
        Threads.Enqueue(Environment.CurrentManagedThreadId);
        SourceGate?.Block();
        AfterSource?.Invoke();
        if (ThrowSource) throw new InvalidOperationException("PRIVATE_TITLE_AND_SOURCE");
        return App;
    }
    public MonitorInfo GetMonitorAt(PixelPoint point) => new("fake", new(0, 0, 100, 100), new(0, 0, 100, 100), Dpi, true);
    public PixelPoint GetCursorPosition() => throw new InvalidOperationException("FORBIDDEN_CURSOR_SCAN");
    public IReadOnlyList<MonitorInfo> GetMonitors() => throw new InvalidOperationException("FORBIDDEN_MONITOR_SCAN");
}

internal sealed class MouseTestRig
{
    internal FakeMouseHookApi Api { get; } = new();
    internal FakeMouseDesktop Desktop { get; } = new();
    internal MouseSelectionMonitor Monitor { get; }
    internal List<MouseGate> Gates { get; } = [];
    internal List<(Task Task, bool ExpectedFailure)> Pending { get; } = [];
    internal ConcurrentQueue<SelectionGestureEventArgs> Gestures { get; } = new();
    internal ConcurrentQueue<MouseSelectionInvalidatedEventArgs> Invalidations { get; } = new();
    internal MouseTestRig(IMouseThreadFactory? factory = null)
    {
        Monitor = new(new NativeMouseAdapter(Api), Desktop, factory ?? new MouseThreadFactory(), NullLogger<MouseSelectionMonitor>.Instance);
        Monitor.SelectionGestureDetected += (_, args) => Gestures.Enqueue(args);
        Monitor.Invalidated += (_, args) => Invalidations.Enqueue(args);
    }
    internal static MouseSelectionMonitorOptions Options(bool iBeam = true) => new(4, iBeam, []);
    internal MouseGate Gate() { var gate = new MouseGate(); Gates.Add(gate); return gate; }
    internal Task Track(Task task, bool expectedFailure = false) { Pending.Add((task, expectedFailure)); return task; }
    internal Task Start(MouseSelectionMonitorOptions? options = null, bool expectedFailure = false, CancellationToken token = default) =>
        Track(Monitor.StartAsync(options ?? Options(), token), expectedFailure);
    internal Task Send(MouseMessage message, int x = 0, int y = 0, uint? time = null, uint flags = 0) =>
        Track(Api.EnqueueAsync(message, x, y, time, flags));
    internal async Task DrainAsync()
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Marker(object? sender, MouseSelectionInputEventArgs args) { if (args.CursorPosition.X == -888) done.TrySetResult(); }
        Monitor.MouseDown += Marker;
        try { await Send(MouseMessage.XDown, -888, flags: 1); await done.Task.WaitAsync(TimeSpan.FromSeconds(10)); }
        finally { Monitor.MouseDown -= Marker; }
    }
    internal async Task<List<Exception>> CloseAsync()
    {
        var errors = new List<Exception>();
        foreach (var gate in Gates) gate.Release();
        // Initiate physical stop before waiting any still-pending start/callback.
        Task stop = Monitor.StopAsync();
        foreach (var pending in Pending)
        {
            try { await pending.Task; }
            catch (Exception ex) { if (!pending.ExpectedFailure) errors.Add(ex); }
        }
        try { await stop; } catch (Exception ex) { errors.Add(ex); }
        Api.ThrowAt = null; Api.UnhookFailures = Api.PostFailures = 0;
        try { await Monitor.DisposeAsync(); } catch (Exception ex) { errors.Add(ex); }
        if (Api.ActiveHandles != 0 || Api.MessageThread is { IsAlive: true }) errors.Add(new InvalidOperationException("RESOURCES_NOT_DRAINED"));
        foreach (var gate in Gates) gate.Dispose();
        Api.Dispose();
        return errors;
    }
    internal static async Task Use(Func<MouseTestRig, Task> body, IMouseThreadFactory? factory = null)
    {
        var rig = new MouseTestRig(factory);
        var errors = new List<Exception>();
        try { await body(rig); } catch (Exception ex) { errors.Add(ex); }
        finally { errors.AddRange(await rig.CloseAsync()); }
        if (errors.Count != 0) throw new AggregateException(errors);
    }
}
