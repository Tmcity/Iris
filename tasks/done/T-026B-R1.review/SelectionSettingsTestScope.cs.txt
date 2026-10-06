using System.Collections.Concurrent;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Iris.App.Hosting;
using Iris.App.Services;
using Iris.App.Settings;
using Iris.App.Tests.TestSupport;
using Iris.Core.Actions;
using Iris.Core.DependencyInjection;
using Iris.Core.Platform;
using Iris.Core.Presentation;
using Iris.Core.Security;
using Iris.Core.Settings;
using Iris.Core.Translation;
using Iris.Platform.Windows.Hotkey;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Iris.App.Tests.Settings;

internal sealed class SelectionSettingsTestScope
{
    public MemorySettings Store { get; } = new();
    public ISecretStore Secrets { get; } = Substitute.For<ISecretStore>();
    public IProviderConfigurationObserver Observer { get; } = Substitute.For<IProviderConfigurationObserver>();
    public IAutoStartService AutoStart { get; } = Substitute.For<IAutoStartService>();
    public ISelectionTextReader Reader { get; } = Substitute.For<ISelectionTextReader>();
    public ITranslateWindowPresenter Presenter { get; } = Substitute.For<ITranslateWindowPresenter>();
    public SettingsApplyReceiptManager Receipts { get; } = new();
    public FakeNativeHotkeyAdapter NativeHotkeys { get; } = new();
    public HookNative NativeHook { get; } = new();
    public HotkeyCaptureService Capture { get; }
    public HotkeyCoordinator Hotkeys { get; }
    public HotkeyRecordingCoordinator Recording { get; }
    public ServiceProvider Provider { get; }
    public IServiceScope Scope { get; private set; }
    public SettingsViewModel Vm { get; private set; }
    public SettingsWindow Window { get; private set; }
    public GeneralSettingsSaveCoordinator Save { get; private set; }
    public SettingsWindowSession WindowSession { get; private set; }
    public ISettingsEditCoordinator Core => Provider.GetRequiredService<ISettingsEditCoordinator>();
    public ConcurrentDictionary<string, string> SecretValues { get; } = new();
    public List<Action> Releases { get; } = [];
    public List<Task> Tasks { get; } = [];
    public Action? AfterCleanup { get; set; }

    public SelectionSettingsTestScope(ISettingsService? settings = null)
    {
        Secrets.Get(Arg.Any<string>()).Returns(call => SecretValues.GetValueOrDefault(call.Arg<string>()));
        Secrets.When(s => s.Set(Arg.Any<string>(), Arg.Any<string>())).Do(call => SecretValues[call.ArgAt<string>(0)] = call.ArgAt<string>(1));
        Secrets.Remove(Arg.Any<string>()).Returns(call => SecretValues.TryRemove(call.Arg<string>(), out _));
        var services = new ServiceCollection(); services.AddLogging(); services.AddIrisCore();
        services.AddSingleton<ISettingsService>(settings ?? Store); services.AddSingleton(Secrets); services.AddSingleton(AutoStart);
        services.AddSingleton(Reader); services.AddSingleton(Presenter); services.AddSingleton(Substitute.For<IClipboardService>());
        services.AddSingleton(Substitute.For<IDesktopContext>()); services.AddSingleton(Substitute.For<ISettingsWindowPresenter>());
        services.AddSingleton(Substitute.For<ITranslationProviderRegistry>());
        services.RemoveAll<IProviderConfigurationObserver>(); services.AddSingleton(Observer);
        services.AddSingleton<IUiDispatcher>(new WpfUiDispatcher()); services.AddSingleton<ThemeService>();
        services.AddSingleton<ISettingsApplyReceiptManager>(Receipts);
        services.AddSingleton(Substitute.For<IActionDispatcher>()); services.AddSingleton(Substitute.For<INotificationService>());
        services.AddSingleton<IHotkeyService>(new HotkeyService(NullLogger<HotkeyService>.Instance, NativeHotkeys));
        Capture = new HotkeyCaptureService(NativeHook, null, NullLogger<HotkeyCaptureService>.Instance);
        services.AddSingleton<IHotkeyCaptureService>(Capture);
        services.AddSingleton<HotkeyCoordinator>(); services.AddSingleton<HotkeyRecordingCoordinator>();
        services.AddScoped<GeneralSettingsSaveCoordinator>(); services.AddScoped<SettingsViewModel>();
        services.AddTransient(_ => new SettingsWindow { ShowActivated = false, ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = -20000 });
        Provider = services.BuildServiceProvider();
        Hotkeys = Provider.GetRequiredService<HotkeyCoordinator>(); Recording = Provider.GetRequiredService<HotkeyRecordingCoordinator>();
        Scope = Provider.CreateScope(); Vm = Scope.ServiceProvider.GetRequiredService<SettingsViewModel>();
        Window = Scope.ServiceProvider.GetRequiredService<SettingsWindow>(); Window.DataContext = Vm;
        Save = Scope.ServiceProvider.GetRequiredService<GeneralSettingsSaveCoordinator>();
        WindowSession = new(Scope, Window, Vm, Provider.GetRequiredService<ThemeService>(), Save);
    }
    public void Show()
    {
        Window.Show(); NativeHook.Window = new WindowInteropHelper(Window).Handle;
        Vm.CurrentPageKey = "selection"; Window.UpdateLayout();
    }
    public T Track<T>(T task) where T : Task { Tasks.Add(task); return task; }
    public async Task ReopenAsync()
    {
        await WindowSession.ShutdownAsync();
        Scope = Provider.CreateScope(); Vm = Scope.ServiceProvider.GetRequiredService<SettingsViewModel>();
        Save = Scope.ServiceProvider.GetRequiredService<GeneralSettingsSaveCoordinator>();
        Window = Scope.ServiceProvider.GetRequiredService<SettingsWindow>(); Window.DataContext = Vm;
        WindowSession = new(Scope, Window, Vm, Provider.GetRequiredService<ThemeService>(), Save); Show();
    }
    public static Task Run(Func<SelectionSettingsTestScope, Task> body, ISettingsService? settings = null) => WpfTestRunner.RunAsync(async () =>
    {
        var f = new SelectionSettingsTestScope(settings); Exception? primary = null; var errors = new List<Exception>();
        try { await body(f); } catch (Exception ex) { primary = ex; }
        finally
        {
            foreach (var release in f.Releases) try { release(); } catch (Exception ex) { errors.Add(ex); }
            f.Store.BeforeUpdate = null; f.Store.AfterUpdate = null; f.Store.Failure = null;
            foreach (var task in f.Tasks) try { await task; } catch (Exception ex) { errors.Add(ex); }
            try { await f.WindowSession.ShutdownAsync(); } catch (Exception ex) { errors.Add(ex); }
            try { await f.Recording.StopAsync(); await f.Hotkeys.StopAcceptanceAndNativeAsync(); await f.Capture.StopAsync(); } catch (Exception ex) { errors.Add(ex); }
            try { f.Window.DataContext = null; f.Window.ForceClose = true; f.Window.Close(); f.Scope.Dispose(); await f.Provider.DisposeAsync(); f.NativeHook.Dispose(); }
            catch (Exception ex) { errors.Add(ex); }
            try { f.AfterCleanup?.Invoke(); } catch (Exception ex) { errors.Add(ex); }
        }
        if (primary != null && errors.Count == 0) ExceptionDispatchInfo.Capture(primary).Throw();
        if (primary != null) errors.Insert(0, primary);
        if (errors.Count != 0) throw new AggregateException(errors);
    });

    internal sealed class MemorySettings : ISettingsService
    {
        public AppSettings Current { get; private set; } = new();
        public int Updates { get; private set; }
        public Func<CancellationToken, Task>? BeforeUpdate { get; set; }
        public Action? AfterUpdate { get; set; }
        public Exception? Failure { get; set; }
        public event EventHandler<SettingsChangedEventArgs>? Changed;
        public Task LoadAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public async Task UpdateAsync(Func<AppSettings, AppSettings> mutate, CancellationToken cancellationToken = default)
        {
            if (BeforeUpdate != null) await BeforeUpdate(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested(); if (Failure != null) throw Failure;
            Updates++; var old = Current; var next = mutate(old);
            if (!ReferenceEquals(old, next)) { Current = next; Changed?.Invoke(this, new(old, next)); }
            AfterUpdate?.Invoke();
        }
        public void External(Func<AppSettings, AppSettings> mutate) { var old = Current; Current = mutate(old); Changed?.Invoke(this, new(old, Current)); }
    }

    internal sealed class HookNative : INativeKeyboardHookAdapter, IDisposable
    {
        private readonly BlockingCollection<Action> _queue = new();
        private static readonly Action Quit = () => { };
        private NativeKeyboardHookProc? _hook;
        public nint Window { get; set; } = 0x1234;
        public TaskCompletionSource PumpEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Unhooks { get; private set; }
        public nint SetWindowsHook(NativeKeyboardHookProc proc) { _hook = proc; return 44; }
        public bool UnhookWindowsHook(nint hookHandle) { Unhooks++; return true; }
        public nint CallNextHook(nint hookHandle, int code, nuint wParam, nint lParam) => 0;
        public nint GetForegroundWindow() => Window;
        public uint GetWindowProcessId(nint window) => window == Window ? 9001u : 0;
        public uint GetCurrentProcessId() => 9001;
        public bool IsWindow(nint window) => window == Window;
        public bool IsKeyDown(int virtualKey) => false;
        public uint GetCurrentThreadId() => (uint)Environment.CurrentManagedThreadId;
        public bool PostThreadQuitMessage(uint threadId) { _queue.Add(Quit); return true; }
        public void EnsureThreadMessageQueueCreated() { }
        public void ReleaseHookResources(nint hookHandle) { }
        public int GetMessage(out NativeMsg msg)
        {
            PumpEntered.TrySetResult(); var action = _queue.Take();
            if (ReferenceEquals(action, Quit)) { msg = new(0x12, 0, 0); return 0; }
            action(); msg = default; return 1;
        }
        public int GetLastError() => 0;
        public Task KeyAsync(uint key, bool up = false)
        {
            var complete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _queue.Add(() =>
            {
                var pointer = Marshal.AllocHGlobal(32);
                try
                {
                    Marshal.Copy(new byte[32], 0, pointer, 32); Marshal.WriteInt32(pointer, unchecked((int)key));
                    _hook!(0, up ? 0x101u : 0x100u, pointer); complete.TrySetResult();
                }
                catch (Exception ex) { complete.TrySetException(ex); }
                finally { Marshal.FreeHGlobal(pointer); }
            });
            return complete.Task;
        }
        public void Dispose() => _queue.Dispose();
    }
}
