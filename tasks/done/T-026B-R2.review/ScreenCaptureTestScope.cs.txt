using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using Iris.Core.Imaging;
using Iris.Core.Platform;
using Iris.Platform.Windows.Capture;
using Shouldly;
using Windows.Win32.Graphics.Gdi;

namespace Iris.Platform.Windows.Tests.Capture;

internal sealed class CaptureGate : IDisposable
{
    internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ManualResetEventSlim _release = new();
    internal void Block() { Entered.TrySetResult(); _release.Wait(); }
    internal async Task WaitAsync() { Entered.TrySetResult(); await Release.Task; }
    internal void Open() { _release.Set(); Release.TrySetResult(); }
    public void Dispose() => _release.Dispose();
}

internal sealed class FakeGdiCaptureApi : IGdiCaptureApi, IDisposable
{
    internal readonly ConcurrentQueue<(string Stage, int Thread)> Calls = new();
    internal readonly Dictionary<string, int> Failures = [];
    internal string? ThrowAt { get; set; }
    internal Action<string>? Before { get; set; }
    internal Action<string>? After { get; set; }
    internal bool NullBits { get; set; }
    internal bool HgdiError { get; set; }
    internal int Violations;
    internal int Owner;
    internal nint Screen, Memory, Bitmap, Bits, Dpi = -2;
    internal bool Selected;
    internal BITMAPINFOHEADER Header;
    internal (uint Usage, nint Section, uint Offset) DibArguments;
    internal readonly List<(int X, int Y, int Width, int Height, int SourceX, int SourceY, uint Raster)> Blits = [];
    internal int Count(string stage) => Calls.Count(c => c.Stage == stage);
    internal int Handles => (Screen != 0 ? 1 : 0) + (Memory != 0 ? 1 : 0) + (Bitmap != 0 ? 1 : 0);
    private bool Call(string stage)
    {
        int thread = Environment.CurrentManagedThreadId;
        Calls.Enqueue((stage, thread)); Before?.Invoke(stage);
        if (Owner != 0 && thread != Owner) { Violations++; throw new InvalidOperationException("wrong owner thread"); }
        if (ThrowAt == stage) throw new IOException("PRIVATE_DEVICE_OR_PIXELS");
        if (Failures.TryGetValue(stage, out int count) && count != 0) { if (count > 0) Failures[stage] = count - 1; return false; }
        return true;
    }
    public nint SetThreadDpiAwarenessContext(nint context)
    {
        string stage = context == -4 ? "DpiEnter" : "DpiRestore";
        if (!Call(stage)) return 0;
        Owner = Environment.CurrentManagedThreadId; nint previous = Dpi; Dpi = context; After?.Invoke(stage); return previous;
    }
    public nint GetDC(nint window)
    {
        window.ShouldBe(0); if (!Call("GetDC")) return 0;
        Screen.ShouldBe(0); Screen = 101; After?.Invoke("GetDC"); return Screen;
    }
    public int ReleaseDC(nint window, nint dc)
    {
        window.ShouldBe(0); dc.ShouldBe(Screen); if (!Call("ReleaseDC")) return 0;
        Screen.ShouldNotBe(0); Screen = 0; After?.Invoke("ReleaseDC"); return 1;
    }
    public nint CreateCompatibleDC(nint screen)
    {
        screen.ShouldBe(Screen); if (!Call("CreateDC")) return 0;
        Memory.ShouldBe(0); return Memory = 102;
    }
    public nint CreateDIBSection(nint dc, in BITMAPINFO info, uint usage, out nint bits, nint section, uint offset)
    {
        dc.ShouldBe(Screen); Header = info.bmiHeader; DibArguments = (usage, section, offset); bits = 0;
        if (!Call("CreateDIB")) return 0;
        Bitmap.ShouldBe(0); Bitmap = 103;
        if (!NullBits)
        {
            Bits = Marshal.AllocHGlobal(checked((int)Header.biSizeImage));
            Marshal.Copy(Enumerable.Repeat((byte)0xCD, checked((int)Header.biSizeImage)).ToArray(), 0, Bits, checked((int)Header.biSizeImage));
            bits = Bits;
        }
        return Bitmap;
    }
    public nint SelectObject(nint dc, nint value)
    {
        dc.ShouldBe(Memory);
        string stage = value == Bitmap ? "Select" : "Restore";
        if (!Call(stage)) return HgdiError ? -1 : 0;
        if (stage == "Select") { Selected.ShouldBeFalse(); Selected = true; return 77; }
        value.ShouldBe((nint)77); Selected.ShouldBeTrue(); Selected = false; return Bitmap;
    }
    public bool DeleteObject(nint bitmap)
    {
        bitmap.ShouldBe(Bitmap);
        if (Selected) { Violations++; throw new InvalidOperationException("deleting selected bitmap"); }
        if (!Call("DeleteBitmap")) return false;
        Bitmap.ShouldNotBe(0); if (Bits != 0) Marshal.FreeHGlobal(Bits); Bits = Bitmap = 0; return true;
    }
    public bool DeleteDC(nint dc)
    {
        dc.ShouldBe(Memory); if (!Call("DeleteDC")) return false;
        Memory.ShouldNotBe(0); Memory = 0; Selected = false; return true;
    }
    public bool BitBlt(nint target, int x, int y, int width, int height, nint source, int sourceX, int sourceY, uint rasterOperation)
    {
        target.ShouldBe(Memory); source.ShouldBe(Screen); Selected.ShouldBeTrue();
        Blits.Add((x, y, width, height, sourceX, sourceY, rasterOperation));
        if (!Call("BitBlt")) return false;
        for (int row = 0; row < height; row++)
        for (int column = 0; column < width; column++)
        {
            int offset = ((y + row) * Header.biWidth + x + column) * 4;
            Marshal.WriteByte(Bits, offset, unchecked((byte)(sourceX + column)));
            Marshal.WriteByte(Bits, offset + 1, unchecked((byte)(sourceY + row)));
            Marshal.WriteByte(Bits, offset + 2, 37); Marshal.WriteByte(Bits, offset + 3, 17);
        }
        After?.Invoke("BitBlt"); return true;
    }
    public bool GdiFlush() { bool success = Call("Flush"); After?.Invoke("Flush"); return success; }
    public int LastError() { Calls.Enqueue(("LastError", Environment.CurrentManagedThreadId)); return 12345; }
    public void Dispose() { if (Bits != 0) { Marshal.FreeHGlobal(Bits); Bits = 0; } }
}

internal sealed class CaptureDesktop : IDesktopContext
{
    internal IReadOnlyList<MonitorInfo> Monitors { get; set; } = [new("synthetic", new(0, 0, 4, 3), new(0, 0, 4, 3), 1, true)];
    internal Func<IReadOnlyList<MonitorInfo>>? Query { get; set; }
    internal readonly List<int> Threads = [];
    public IReadOnlyList<MonitorInfo> GetMonitors() { Threads.Add(Environment.CurrentManagedThreadId); return Query?.Invoke() ?? Monitors; }
    public ForegroundAppInfo? GetForegroundApp() => throw new InvalidOperationException("forbidden foreground query");
    public PixelPoint GetCursorPosition() => throw new InvalidOperationException("forbidden cursor query");
    public MonitorInfo GetMonitorAt(PixelPoint point) => throw new InvalidOperationException("forbidden monitor-at query");
}

internal sealed class CaptureThreads : ICaptureThreadFactory
{
    internal readonly List<Thread> All = [];
    internal bool FailCreate { get; set; }
    public Thread Create(ThreadStart start)
    {
        if (FailCreate) throw new IOException("private thread error");
        var thread = new Thread(start) { IsBackground = true }; All.Add(thread); return thread;
    }
}

internal sealed class ScreenCaptureTestScope : IAsyncDisposable
{
    internal readonly FakeGdiCaptureApi Native = new();
    internal readonly CaptureDesktop Desktop = new();
    internal readonly CaptureThreads Threads = new();
    internal readonly FakeScreenEncoderFactory Encoding = new();
    internal readonly List<CaptureGate> Gates = [];
    internal readonly List<(Task Task, Type? Allowed)> Tasks = [];
    internal GdiScreenCapturer Service { get; }
    internal ScreenCaptureTestScope(bool realEncoder = false) => Service = new(new(Native), Desktop,
        new(realEncoder ? new WinRtScreenEncoderFactory() : Encoding), Threads, TimeProvider.System);
    internal CaptureGate Gate() { var gate = new CaptureGate(); Gates.Add(gate); return gate; }
    internal T Track<T>(T task, Type? allowed = null) where T : Task { Tasks.Add((task, allowed)); return task; }
    internal static CapturedFrame Frame(int width = 2, int height = 2) => new(new byte[width * height * 4], width, height, width * 4, new(-3, -2, width, height), [], DateTimeOffset.UnixEpoch);
    internal static async Task Run(Func<ScreenCaptureTestScope, Task> body, bool realEncoder = false)
    {
        var f = new ScreenCaptureTestScope(realEncoder); Exception? primary = null; var errors = new List<Exception>();
        try { await body(f); } catch (Exception ex) { primary = ex; }
        finally { try { await f.DisposeAsync(); } catch (AggregateException ex) { errors.AddRange(ex.InnerExceptions); } }
        if (primary != null && errors.Count == 0) ExceptionDispatchInfo.Capture(primary).Throw();
        if (primary != null) errors.Insert(0, primary);
        if (errors.Count != 0) throw new AggregateException(errors);
    }
    public async ValueTask DisposeAsync()
    {
        var f = this; var errors = new List<Exception>();
        {
            foreach (var gate in f.Gates) gate.Open();
            f.Native.Before = f.Native.After = null; f.Native.ThrowAt = null; f.Native.Failures.Clear();
            foreach (var io in f.Encoding.Sessions) { io.Failures.Clear(); io.Before = null; }
            try { await f.Service.StopAsync(); } catch (Exception ex) { errors.Add(ex); }
            foreach (var item in f.Tasks) try { await item.Task; } catch (Exception ex) { if (item.Allowed == null || !item.Allowed.IsInstanceOfType(ex)) errors.Add(ex); }
            try { await f.Service.DisposeAsync(); } catch (Exception ex) { errors.Add(ex); }
            try
            {
                f.Native.Handles.ShouldBe(0); f.Native.Violations.ShouldBe(0); f.Native.Dpi.ShouldBe((nint)(-2));
                f.Threads.All.ShouldAllBe(t => !t.IsAlive);
                f.Encoding.Sessions.ShouldAllBe(io => !io.StreamOwned && !io.ReaderOwned && io.InFlight == 0);
            }
            catch (Exception ex) { errors.Add(ex); }
            foreach (var gate in f.Gates) gate.Dispose(); f.Native.Dispose();
        }
        if (errors.Count != 0) throw new AggregateException(errors);
    }
}
