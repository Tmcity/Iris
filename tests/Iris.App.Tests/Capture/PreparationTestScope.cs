using System.Runtime.ExceptionServices;
using System.Windows;
using Iris.App.Capture;
using Iris.App.Hosting;
using Iris.App.Settings;
using Iris.App.Tests.Selection;
using Iris.App.Tests.Settings;
using Iris.Core.Platform;
using Iris.Core.Presentation;
using Iris.Platform.Windows.Hotkey;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Iris.App.Tests.Capture;

internal sealed class PreparationTestScope(MouseInteractionTestScope mouse, RecordingNative native, PreparationDispatcher ui)
{
    internal MouseInteractionTestScope Mouse { get; } = mouse;
    internal RecordingNative Native { get; } = native;
    internal PreparationDispatcher Ui { get; } = ui;
    internal ScreenshotPreparationCoordinator Preparation => Mouse.Services.GetRequiredService<ScreenshotPreparationCoordinator>();
    internal SettingsWindowPresenter Settings => Mouse.Services.GetRequiredService<SettingsWindowPresenter>();
    internal HotkeyRecordingCoordinator Recording => Mouse.Services.GetRequiredService<HotkeyRecordingCoordinator>();
    internal readonly List<Task> Work = [];
    internal readonly List<Action> Releases = [];
    internal async Task OpenSettings()
    {
        await Settings.ShowAsync("hotkeys");
        Native.Inner.Window = new System.Windows.Interop.WindowInteropHelper(Settings.ActiveSession!.Window).Handle;
    }
    internal static Task Run(Func<PreparationTestScope, Task> body)
    {
        var native = new RecordingNative();
        var ui = new PreparationDispatcher();
        return MouseInteractionTestScope.Run(async mouse =>
        {
            var f = new PreparationTestScope(mouse, native, ui);
            Exception? primary = null; var errors = new List<Exception>();
            try { await body(f); }
            catch (Exception ex) { primary = ex; }
            finally
            {
                foreach (var release in f.Releases) release();
                ui.Release.TrySetResult();
                foreach (var gate in mouse.Gates) gate.Release();
                foreach (var gate in mouse.Platform.Gates) gate.Set();
                native.BeforeUnhook = null; native.FailUnhook = false;
                mouse.Icon.FailHide = mouse.Icon.FailClose = false;
                f.Preparation.Visibility = PreparationVisibility.Instance;
                try { await f.Preparation.StopAsync(); } catch (Exception ex) { errors.Add(ex); }
                try { await f.Settings.CancelRecordingForPreparationAsync(); await f.Recording.CancelForPreparationAsync(); await f.Recording.StopAsync(); }
                catch (Exception ex) { errors.Add(ex); }
                foreach (var task in f.Work) try { await task; } catch (OperationCanceledException) { }
                    catch (Exception ex) { errors.Add(ex); }
                try { await f.Settings.ShutdownAsync(); } catch (Exception ex) { errors.Add(ex); }
                if (native.Threads.Any(thread => thread.IsAlive)) errors.Add(new InvalidOperationException("Recording thread still alive."));
                native.Dispose();
            }
            if (primary != null && errors.Count == 0) ExceptionDispatchInfo.Capture(primary).Throw();
            if (primary != null) errors.Insert(0, primary);
            if (errors.Count != 0) throw new AggregateException(errors);
        }, configure: services =>
        {
            services.AddSingleton<IUiDispatcher>(ui);
            services.RemoveAll<ISettingsWindowPresenter>();
            services.AddSingleton<SettingsWindowPresenter>();
            services.AddSingleton<ISettingsWindowPresenter>(sp => sp.GetRequiredService<SettingsWindowPresenter>());
            services.AddTransient(_ => new SettingsWindow { ShowActivated = false, ShowInTaskbar = false,
                WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = -20000 });
            services.AddSingleton<IHotkeyCaptureService>(new HotkeyCaptureService(native, null, NullLogger<HotkeyCaptureService>.Instance));
            services.AddSingleton<HotkeyRecordingCoordinator>();
            services.AddSingleton<ScreenshotPreparationCoordinator>();
        });
    }
}

internal sealed class PreparationDispatcher : IUiDispatcher
{
    private readonly WpfUiDispatcher _inner = new();
    internal int HoldNext;
    internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public bool CheckAccess() => _inner.CheckAccess();
    public async Task InvokeAsync(Func<Task> callback, CancellationToken token)
    {
        if (Interlocked.Exchange(ref HoldNext, 0) == 1)
        {
            Entered.TrySetResult();
            await Release.Task.ConfigureAwait(false);
        }
        await _inner.InvokeAsync(callback, token).ConfigureAwait(false);
    }
}

internal sealed class RecordingNative : INativeKeyboardHookAdapter, IDisposable
{
    internal readonly SelectionSettingsTestScope.HookNative Inner = new();
    internal Action? BeforeUnhook;
    internal bool FailUnhook;
    internal int Installs;
    internal readonly System.Collections.Concurrent.ConcurrentBag<Thread> Threads = [];
    internal readonly TaskCompletionSource SecondInstalled = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public nint SetWindowsHook(NativeKeyboardHookProc proc)
    {
        Threads.Add(Thread.CurrentThread);
        Installs++; if (Installs == 2) SecondInstalled.TrySetResult(); return Inner.SetWindowsHook(proc);
    }
    public bool UnhookWindowsHook(nint hook) { BeforeUnhook?.Invoke(); return !FailUnhook && Inner.UnhookWindowsHook(hook); }
    public nint CallNextHook(nint hook, int code, nuint message, nint data) => Inner.CallNextHook(hook, code, message, data);
    public nint GetForegroundWindow() => Inner.GetForegroundWindow();
    public uint GetWindowProcessId(nint window) => Inner.GetWindowProcessId(window);
    public uint GetCurrentProcessId() => Inner.GetCurrentProcessId();
    public bool IsWindow(nint window) => Inner.IsWindow(window);
    public bool IsKeyDown(int key) => Inner.IsKeyDown(key);
    public uint GetCurrentThreadId() => Inner.GetCurrentThreadId();
    public bool PostThreadQuitMessage(uint thread) => Inner.PostThreadQuitMessage(thread);
    public void EnsureThreadMessageQueueCreated() => Inner.EnsureThreadMessageQueueCreated();
    public void ReleaseHookResources(nint hook) => Inner.ReleaseHookResources(hook);
    public int GetMessage(out NativeMsg msg) => Inner.GetMessage(out msg);
    public int GetLastError() => 5;
    public void Dispose() => Inner.Dispose();
}
