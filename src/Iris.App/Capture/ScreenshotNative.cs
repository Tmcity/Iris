using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Iris.App.Hosting;
using Iris.App.Services;
using Iris.Core.Imaging;

namespace Iris.App.Capture;

internal readonly record struct ScreenshotFocus(nint Handle, uint Process);
internal interface IScreenshotSystem
{
    int FlushComposition();
    ScreenshotFocus Foreground();
    bool IsTarget(ScreenshotFocus target);
    bool RestoreForeground(nint handle, Func<bool> permitted);
    PixelPoint PhysicalPoint(Window window, Point local);
    void Show(Window window);
    void Activate(Window window);
    void Close(Window window);
    bool Capture(Window window);
    void ReleaseCapture(Window window);
    void WarnCopyFailure(Func<bool> permitted);
}

internal sealed class ScreenshotSystem(TrayNotificationService notifications) : IScreenshotSystem
{
    public int FlushComposition() => DwmFlush();
    public ScreenshotFocus Foreground()
    {
        nint handle = GetForegroundWindow();
        uint pid = GetWindowThreadProcessId(handle, out uint process) == 0 ? 0 : process;
        return new(handle, pid);
    }
    public bool IsTarget(ScreenshotFocus target) => target.Handle != 0 && target.Process != 0 && IsWindow(target.Handle) &&
        GetWindowThreadProcessId(target.Handle, out uint pid) != 0 && pid == target.Process;
    public bool RestoreForeground(nint handle, Func<bool> permitted) => permitted() && SetForegroundWindow(handle);
    public PixelPoint PhysicalPoint(Window window, Point local)
    {
        var p = window.PointToScreen(local);
        return new(checked((int)Math.Floor(p.X)), checked((int)Math.Floor(p.Y)));
    }
    public void Show(Window window) => window.Show();
    public void Activate(Window window) => window.Activate();
    public void Close(Window window) => window.Close();
    public bool Capture(Window window) => Mouse.Capture(window, CaptureMode.Element);
    public void ReleaseCapture(Window window) { if (ReferenceEquals(Mouse.Captured, window)) Mouse.Capture(null); }
    public void WarnCopyFailure(Func<bool> permitted)
    {
        if (permitted() && !notifications.TryNotifyScreenshotCopyFailure()) throw ScreenshotService.Failure("COPY_WARNING_UNAVAILABLE");
    }
    [DllImport("dwmapi.dll")] private static extern int DwmFlush();
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hwnd, out uint process);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindow(nint hwnd);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetForegroundWindow(nint hwnd);
}

internal sealed class ScreenshotRenderSynchronizer(IUiDispatcher ui, IScreenshotSystem system)
{
    internal async Task SynchronizeAsync()
    {
        await ui.InvokeAsync(async () =>
            await Dispatcher.CurrentDispatcher.InvokeAsync(static () => { }, DispatcherPriority.Render), CancellationToken.None).ConfigureAwait(false);
        await Task.Run(() =>
        {
            int hr = system.FlushComposition();
            if (hr < 0) throw ScreenshotService.Failure("DWM", hr);
        }).ConfigureAwait(false);
    }
}
