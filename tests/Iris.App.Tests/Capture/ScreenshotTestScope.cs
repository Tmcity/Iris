using System.Runtime.ExceptionServices;
using System.Windows;
using Iris.App.Capture;
using Iris.App.Services;
using Iris.Core.Imaging;
using Iris.Core.Platform;
using Iris.Core.Presentation;
using Iris.Core.Settings;
using Iris.Platform.Windows.Tests.Capture;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;

namespace Iris.App.Tests.Capture;

internal sealed class ScreenshotTestScope : IAsyncDisposable
{
    internal readonly PreparationTestScope A;
    internal readonly ScreenCaptureTestScope Codec;
    internal readonly SystemBoundary Native = new();
    internal readonly IClipboardService Clipboard = Substitute.For<IClipboardService>();
    internal readonly CapturerBoundary Capturer;
    internal readonly FactoryBoundary Factory;
    internal readonly ScreenshotService Service;
    internal readonly List<(Task Task, Type? Expected)> Tasks = [];
    internal readonly List<Action> Releases = [];
    internal IReadOnlyList<MonitorInfo> Monitors = [new("synthetic", new(-40, -20, 320, 240), new(-40, -20, 320, 240), 1, true)];
    internal ScreenshotTestScope(PreparationTestScope a, bool gdi, bool realEncoder)
    {
        A = a; Codec = new(realEncoder);
        Capturer = new(this);
        Factory = new(Native, a.Mouse.Position);
        Codec.Desktop.Query = () => Monitors;
        a.Mouse.Platform.Desktop.GetMonitors().Returns(_ => Monitors);
        Service = new(a.Mouse.Settings, a.Preparation, gdi ? Codec.Service : Capturer, Clipboard, a.Mouse.Platform.Desktop,
            a.Ui, Native, new(a.Ui, Native), Factory,
            a.Mouse.Presenter, a.Settings, a.Mouse.Services.GetRequiredService<ThemeService>(), NullLogger<ScreenshotService>.Instance);
    }
    public async ValueTask DisposeAsync() { await Service.StopAsync(); await Codec.DisposeAsync(); }
    internal Task Settings(ScreenshotSettings settings, AppTheme theme = AppTheme.Light) => A.Mouse.Settings.UpdateAsync(s => s with
    { Screenshot = settings, General = s.General with { Theme = theme } });
    internal Task<ScreenshotResult?> Start(ScreenshotPurpose purpose = ScreenshotPurpose.Translate, CancellationToken token = default) =>
        Track(Service.CaptureRegionAsync(purpose, token));
    internal T Track<T>(T task, Type? expected = null) where T : Task { Tasks.Add((task, expected)); return task; }
    internal async Task Ready() => await Native.Activated.Task.WaitAsync(TimeSpan.FromSeconds(10));
    internal void Input(ScreenshotInput input, int x = 10, int y = 20, int screen = 0) =>
        Service.Windows[screen].RaiseEvent(new ScreenshotInputEventArgs(input, new(x, y)));
    internal void Select(int x0 = 10, int y0 = 20, int x1 = 13, int y1 = 23, int screen = 0)
    { Input(ScreenshotInput.Down, x0, y0, screen); Input(ScreenshotInput.Move, x1, y1, screen); Input(ScreenshotInput.Up, x1, y1, screen); }
    internal static Task Run(Func<ScreenshotTestScope, Task> body, bool gdi = false, bool realEncoder = true) => PreparationTestScope.Run(async a =>
    {
        var f = new ScreenshotTestScope(a, gdi, realEncoder); Exception? primary = null; var errors = new List<Exception>();
        try { await body(f); } catch (Exception ex) { primary = ex; }
        finally
        {
            foreach (var release in f.Releases) try { release(); } catch (Exception ex) { errors.Add(ex); }
            foreach (var gate in f.Codec.Gates) gate.Open();
            f.Native.FailClose = f.Native.FailRelease = false; f.Native.BeforeClose = null; f.Native.BeforeShow = null;
            a.Ui.Release.TrySetResult(); a.Mouse.Position.Result = null;
            f.Codec.Native.Failures.Clear(); f.Codec.Native.Before = null; f.Codec.Native.After = null;
            try { await f.Service.StopAsync(); } catch (Exception ex) { errors.Add(ex); }
            foreach (var work in f.Tasks)
                try { await work.Task; } catch (Exception ex) { if (work.Expected == null || !work.Expected.IsInstanceOfType(ex)) errors.Add(ex); }
            try { await f.Codec.DisposeAsync(); } catch (Exception ex) { errors.Add(ex); }
            try { f.Native.Windows.ShouldAllBe(w => w.IsClosed); f.Native.Captured.ShouldBeNull(); } catch (Exception ex) { errors.Add(ex); }
        }
        if (primary != null && errors.Count == 0) ExceptionDispatchInfo.Capture(primary).Throw();
        if (primary != null) errors.Insert(0, primary);
        if (errors.Count != 0) throw new AggregateException(errors);
    });
    internal CapturedFrame Frame()
    {
        long x = Monitors.Min(m => (long)m.Bounds.X), y = Monitors.Min(m => (long)m.Bounds.Y);
        int width = checked((int)(Monitors.Max(m => (long)m.Bounds.X + m.Bounds.Width) - x));
        int height = checked((int)(Monitors.Max(m => (long)m.Bounds.Y + m.Bounds.Height) - y));
        int stride = width * 4 + 16; var pixels = new byte[stride * height];
        for (int row = 0; row < height; row++) for (int col = 0; col < width; col++)
        {
            int at = row * stride + col * 4;
            pixels[at] = (byte)(col * 7); pixels[at + 1] = (byte)(row * 5); pixels[at + 2] = (byte)(col + row); pixels[at + 3] = 255;
        }
        return new(pixels, width, height, stride, new((int)x, (int)y, width, height), Monitors.ToArray(), DateTimeOffset.UnixEpoch);
    }
    internal sealed class CapturerBoundary(ScreenshotTestScope owner) : IScreenCapturer
    {
        internal int Captures, Crops;
        internal Func<Task>? BeforeCapture { get; set; }
        internal Func<Task>? BeforeCrop { get; set; }
        internal readonly List<WeakReference> PixelReferences = [];
        internal readonly List<WeakReference> ImageReferences = [];
        public async Task<CapturedFrame> CaptureVirtualScreenAsync(CancellationToken cancellationToken)
        {
            Captures++; if (BeforeCapture != null) await BeforeCapture();
            var frame = owner.Frame(); PixelReferences.Add(new(frame.Bgra32Pixels)); return frame;
        }
        public async Task<ImageData> CropAsync(CapturedFrame frame, PixelRect region, ImageFormat format = ImageFormat.Png, CancellationToken cancellationToken = default)
        {
            Crops++; if (BeforeCrop != null) await BeforeCrop();
            var image = await owner.Codec.Service.CropAsync(frame, region, format, cancellationToken);
            ImageReferences.Add(new(image.Bytes)); return image;
        }
    }
    internal sealed class FactoryBoundary(IScreenshotSystem system, Iris.App.Translation.INativeWindowPlacementAdapter placement) : IScreenshotOverlayFactory
    {
        internal bool FailCreate { get; set; }
        internal Action<ScreenshotOverlayWindow>? Created { get; set; }
        public ScreenshotOverlayWindow Create(ScreenshotFrameView frame, int monitor, ScreenshotSettings settings, bool dark)
        {
            if (FailCreate) throw new IOException("synthetic create failure");
            var window = new ScreenshotOverlayWindow(system, placement, frame, monitor, settings, dark);
            Created?.Invoke(window); return window;
        }
    }
    internal sealed class SystemBoundary : IScreenshotSystem
    {
        internal readonly TaskCompletionSource Activated = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly List<ScreenshotOverlayWindow> Windows = [];
        internal ScreenshotFocus Current = new(100, 200);
        internal Window? Captured;
        internal int Flushes, Restores, Warnings;
        internal bool FailClose { get; set; }
        internal bool FailShow { get; set; }
        internal bool FailRelease { get; set; }
        internal bool FailCapture { get; set; }
        internal bool FailWarning { get; set; }
        internal Action? BeforeFlush { get; set; }
        internal Action? BeforeShow { get; set; }
        internal Action? BeforeClose { get; set; }
        internal Action? BeforeRestore { get; set; }
        internal Action? BeforeWarning { get; set; }
        internal int ReviewFlushResult;
        public int FlushComposition() { Flushes++; BeforeFlush?.Invoke(); return ReviewFlushResult; }
        internal Action? ReviewForeground;
        public ScreenshotFocus Foreground() { ReviewForeground?.Invoke(); return Current; }
        public bool IsTarget(ScreenshotFocus target) => target == new ScreenshotFocus(100, 200);
        public bool RestoreForeground(nint handle, Func<bool> permitted) { BeforeRestore?.Invoke(); if (!permitted()) return false; Restores++; Current = new(handle, 200); return true; }
        internal Func<Window, Point, PixelPoint>? ReviewPhysicalPoint;
        public PixelPoint PhysicalPoint(Window window, Point local) => ReviewPhysicalPoint?.Invoke(window, local) ?? throw new InvalidOperationException("Only synthetic physical input is authorized.");
        public void Show(Window window)
        {
            Windows.Add((ScreenshotOverlayWindow)window); BeforeShow?.Invoke();
            window.Left = window.Top = -20000; window.Show();
            if (FailShow) throw new IOException("private failed show");
        }
        public void Activate(Window window) { Current = new(((ScreenshotOverlayWindow)window).Handle, (uint)Environment.ProcessId); Activated.TrySetResult(); }
        public void Close(Window window)
        {
            BeforeClose?.Invoke(); if (FailClose) throw new IOException("private close");
            nint handle = ((ScreenshotOverlayWindow)window).Handle;
            window.Close(); if (ReferenceEquals(Captured, window)) Captured = null;
            if (Current.Handle == handle && Current.Process == (uint)Environment.ProcessId) Current = default;
        }
        public bool Capture(Window window) { Captured = window; return !FailCapture; }
        public void ReleaseCapture(Window window) { if (FailRelease) throw new IOException("private capture"); Captured = null; }
        public void WarnCopyFailure(Func<bool> permitted) { BeforeWarning?.Invoke(); if (!permitted()) return; Warnings++; if (FailWarning) throw new IOException("private warning"); }
    }
}
