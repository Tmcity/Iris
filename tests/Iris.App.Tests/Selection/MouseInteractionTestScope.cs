using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Windows;
using Iris.App.Hosting;
using Iris.App.Selection;
using Iris.App.Services;
using Iris.App.Settings;
using Iris.App.Tests.TestSupport;
using Iris.App.Translation;
using Iris.App.Tray;
using Iris.Core.Actions;
using Iris.Core.Common;
using Iris.Core.DependencyInjection;
using Iris.Core.Errors;
using Iris.Core.Imaging;
using Iris.Core.Platform;
using Iris.Core.Presentation;
using Iris.Core.Security;
using Iris.Core.Settings;
using Iris.Core.Storage;
using Iris.Core.Translation;
using Iris.Platform.Windows.Hooks;
using Iris.Platform.Windows.Tests.Hooks;
using Iris.Platform.Windows.Tests.Selection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;

namespace Iris.App.Tests.Selection;

internal sealed class MouseInteractionTestScope
{
    internal SelectionTestScope Platform { get; } = new();
    internal FakeMouseHookApi Mouse { get; } = new();
    internal MouseSelectionMonitor Monitor { get; }
    internal SelectionReadCoordinator Reader { get; }
    internal SelectionIconController Controller { get; }
    internal ServiceProvider Services { get; }
    internal Store Settings { get; } = new();
    internal IconSystem Icon { get; } = new();
    internal Placement Position { get; } = new();
    internal IconFactory Factory { get; }
    internal TranslateWindowPresenter Presenter { get; }
    internal ActionDispatcher Dispatcher { get; }
    internal FakeProvider Provider { get; } = new();
    internal List<Action> Release { get; } = [];
    internal List<MouseGate> Gates { get; } = [];
    internal List<(Task Task, Type? Allowed)> Tasks { get; } = [];

    internal MouseInteractionTestScope(Func<TimeProvider, TimeProvider>? wrapTime = null, Action<ServiceCollection>? configure = null)
    {
        Mouse.Window = 0x1234; Mouse.Process = 5000;
        Platform.Desktop.GetMonitorAt(Arg.Any<PixelPoint>()).Returns(new MonitorInfo("fake", new(-1000, -500, 2000, 1000), new(-1000, -500, 2000, 1000), 1.5, true));
        Platform.Desktop.GetMonitors().Returns([new MonitorInfo("fake", new(-1000, -500, 2000, 1000), new(-1000, -500, 2000, 1000), 1.5, true)]);
        Monitor = new(new NativeMouseAdapter(Mouse), Platform.Desktop, new MouseThreadFactory(), NullLogger<MouseSelectionMonitor>.Instance);
        Reader = new(Platform.Reader);
        var native = new SelectionIconNative(Icon, Position);
        Factory = new(native);
        var services = new ServiceCollection(); services.AddLogging(); services.AddIrisCore();
        services.AddSingleton(Substitute.For<IScreenshotService>());
        services.AddSingleton(Substitute.For<IScreenshotOcrFeedback>());
        services.AddSingleton(Substitute.For<Iris.Core.Ocr.IOcrService>());
        // This legacy UI fixture supplies no OCR factories; keep OCR as an external boundary.
        services.AddSingleton(Substitute.For<Iris.Core.Ocr.IOcrProviderRegistry>());
        services.AddSingleton<ISettingsService>(Settings); services.AddSingleton(Platform.Desktop);
        services.AddSingleton<ISelectionTextReader>(Reader); services.AddSingleton(Reader);
        services.AddSingleton<IMouseSelectionMonitor>(Monitor); services.AddSingleton(Factory);
        services.AddSingleton<ISelectionIconViewFactory>(Factory); services.AddSingleton(native);
        services.AddSingleton(wrapTime?.Invoke(Platform.Time) ?? (TimeProvider)Platform.Time);
        services.AddSingleton<IUiDispatcher>(new WpfUiDispatcher());
        services.AddSingleton<IClipboardService>(Substitute.For<IClipboardService>());
        services.AddSingleton(Substitute.For<ISecretStore>()); services.AddSingleton(Substitute.For<IAutoStartService>());
        services.AddSingleton(Substitute.For<ISettingsWindowPresenter>()); services.AddSingleton(Substitute.For<IAppLifetime>());
        services.AddSingleton(Substitute.For<IHistoryWindowPresenter>());
        services.AddSingleton(Substitute.For<ITranslationHistoryRepository>());
        var registry = Substitute.For<ITranslationProviderRegistry>();
        var profile = new TranslationProviderProfile { Id = "fake", Kind = TranslationProviderKind.OpenAICompatible, DisplayName = "Fake", Model = "fake", Enabled = true };
        var instance = Guid.NewGuid();
        registry.AcquireSnapshotAsync(Arg.Any<CancellationToken>()).Returns(_ =>
        {
            var snapshot = Substitute.For<ITranslationProviderSnapshot>(); snapshot.Settings.Returns(Settings.Current);
            snapshot.Providers.Returns([new TranslationProviderRegistration(profile, Provider, instance)]);
            return Task.FromResult(snapshot);
        });
        services.AddSingleton(registry);
        services.AddSingleton<INativeWindowPlacementAdapter>(Position);
        services.AddSingleton<TranslateWindowPresenter>();
        services.AddSingleton<ITranslateWindowPresenter>(sp => sp.GetRequiredService<TranslateWindowPresenter>());
        services.AddScoped<TranslateWindowViewModel>();
        services.AddTransient(_ => new TranslateWindow { ShowActivated = false, ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = -20000 });
        services.AddSingleton<TrayNotificationService>(); services.AddSingleton<INotificationService>(sp => sp.GetRequiredService<TrayNotificationService>());
        services.AddSingleton<ActionDispatcher>(); services.AddSingleton<IActionDispatcher>(sp => sp.GetRequiredService<ActionDispatcher>());
        services.AddSingleton<SelectionIconController>(); services.AddSingleton<ThemeService>();
        services.AddSingleton<ISettingsApplyReceiptManager, SettingsApplyReceiptManager>();
        services.AddScoped<GeneralSettingsSaveCoordinator>(); services.AddScoped<SettingsViewModel>();
        configure?.Invoke(services);
        Services = services.BuildServiceProvider();
        Presenter = Services.GetRequiredService<TranslateWindowPresenter>(); Dispatcher = Services.GetRequiredService<ActionDispatcher>();
        Controller = Services.GetRequiredService<SelectionIconController>();
    }
    internal MouseGate Gate() { var gate = new MouseGate(); Gates.Add(gate); return gate; }
    internal T Track<T>(T task, Type? allowed = null) where T : Task { Tasks.Add((task, allowed)); return task; }
    internal Task Mode(MouseSelectionMode mode) => Settings.UpdateAsync(s => s with { Selection = s.Selection with { MouseMode = mode } });
    internal async Task Drag(int x = 0, int y = 0)
    {
        await Track(Mouse.EnqueueAsync(MouseMessage.LeftDown, x, y));
        await Track(Mouse.EnqueueAsync(MouseMessage.LeftUp, x + 20, y));
        await Platform.Time.WaitForDelayAsync(120);
    }
    internal async Task ShowCandidate(bool clipboard = false)
    {
        Platform.UiaStatus = Iris.Platform.Windows.Selection.UiaSelectionStatus.Success;
        if (clipboard)
        {
            Platform.Seed(); Platform.CopyOnSend(" cached original\r\ntext ");
            await Settings.UpdateAsync(s => s with { Selection = s.Selection with { ReadOrder = [SelectionReadMethod.Clipboard] } });
            await Controller.WaitForApplyAsync();
        }
        Icon.Shown = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await Drag(); Platform.Time.Advance(TimeSpan.FromMilliseconds(120));
        if (clipboard) await Platform.Tick(20);
        await Icon.Shown.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await FlushUi();
    }
    internal static async Task FlushUi() => await System.Windows.Threading.Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);

    internal static Task Run(Func<MouseInteractionTestScope, Task> body, Func<TimeProvider, TimeProvider>? wrapTime = null, Action<ServiceCollection>? configure = null) => WpfTestRunner.RunAsync(async () =>
    {
        var f = new MouseInteractionTestScope(wrapTime, configure); Exception? primary = null; var errors = new List<Exception>();
        try { await body(f); } catch (Exception ex) { primary = ex; }
        finally
        {
            foreach (var gate in f.Gates) gate.Release();
            foreach (var gate in f.Platform.Gates) gate.Set();
            foreach (var release in f.Release) try { release(); } catch (Exception ex) { errors.Add(ex); }
            f.Provider.Release.TrySetResult(); f.Mouse.UnhookFailures = f.Mouse.PostFailures = 0; f.Mouse.ThrowAt = null;
            f.Icon.FailClose = false; f.Icon.FailShow = false; f.Icon.FailHide = false;
            f.Icon.AfterShow = null; f.Icon.BeforeHide = null; f.Position.Result = null;
            f.Platform.Native.Failures.Clear(); f.Platform.Native.Before = null; f.Platform.Native.After = null;
            f.Dispatcher.StopAcceptance();
            try { await f.Controller.DisposeAsync(); } catch (Exception ex) { errors.Add(ex); }
            try { await f.Reader.DisposeAsync(); } catch (Exception ex) { errors.Add(ex); }
            try { await SelectionTestScope.Run(_ => Task.CompletedTask, f.Platform); } catch (Exception ex) { errors.Add(ex); }
            foreach (var item in f.Tasks) try { await item.Task; } catch (Exception ex) { if (item.Allowed == null || !item.Allowed.IsInstanceOfType(ex)) errors.Add(ex); }
            try { await f.Dispatcher.WaitForInFlightAsync(); await f.Presenter.ShutdownAsync(); } catch (Exception ex) { errors.Add(ex); }
            try { await f.Services.DisposeAsync(); } catch (Exception ex) { errors.Add(ex); }
            f.Mouse.ActiveHandles.ShouldBe(0); f.Mouse.MessageThread?.IsAlive.ShouldBeFalse();
            foreach (var gate in f.Gates) gate.Dispose(); f.Mouse.Dispose();
        }
        if (primary != null && errors.Count == 0) ExceptionDispatchInfo.Capture(primary).Throw();
        if (primary != null) errors.Insert(0, primary);
        if (errors.Count != 0) throw new AggregateException(errors);
    });

    internal sealed class Store : ISettingsService
    {
        public bool IsReadOnly => false;
        public AppSettings Current { get; private set; } = new()
        {
            Selection = new() { ReadOrder = [SelectionReadMethod.UIAutomation] },
            Translation = new() { AutoCopy = AutoCopyMode.None },
            TranslateWindow = new() { HideOnDeactivate = false },
        };
        internal int Updates;
        public event EventHandler<SettingsChangedEventArgs>? Changed;
        public Task LoadAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task UpdateAsync(Func<AppSettings, AppSettings> mutate, CancellationToken cancellationToken = default)
        { cancellationToken.ThrowIfCancellationRequested(); var old = Current; Current = mutate(old); Updates++; Changed?.Invoke(this, new(old, Current)); return Task.CompletedTask; }
    }
    internal sealed class IconFactory(SelectionIconNative native) : ISelectionIconViewFactory
    {
        internal List<SelectionIconWindow> Windows { get; } = [];
        public ISelectionIconView Create() { var window = new SelectionIconWindow(native); Windows.Add(window); return window; }
    }
    internal sealed class IconSystem : ISelectionIconSystem
    {
        internal TaskCompletionSource Shown { get; set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal long Style;
        internal int Shows, Hides, Closes;
        internal bool FailClose, FailShow;
        internal bool FailHide { get; set; }
        internal Action<Window>? AfterShow { get; set; }
        internal Action<Window>? BeforeHide { get; set; }
        internal int HideAttempts { get; private set; }
        internal nint Foreground = 0x1234;
        public nint GetStyle(nint handle) => 0;
        public void SetStyle(nint handle, nint value) => Style = value;
        public nint ForegroundWindow() => Foreground;
        public uint ProcessId(nint handle) => 5000;
        public void Show(Window window) { if (FailShow) throw new IOException("private show"); window.Show(); Shows++; Shown.TrySetResult(); AfterShow?.Invoke(window); }
        public void Hide(Window window) { HideAttempts++; BeforeHide?.Invoke(window); if (FailHide) throw new IOException("private hide"); window.Hide(); Hides++; }
        public void Close(Window window) { if (FailClose) throw new IOException("private close"); window.Close(); Closes++; }
    }
    internal sealed class Placement : INativeWindowPlacementAdapter
    {
        internal ConcurrentQueue<(PixelRect Bounds, bool Show, bool Activate)> Calls { get; } = new();
        internal Func<bool, bool>? Result { get; set; }
        public bool SetWindowBounds(nint handle, PixelRect bounds, bool show, bool activate) { Calls.Enqueue((bounds, show, activate)); return Result?.Invoke(show) ?? true; }
        public bool TryGetWindowBounds(nint handle, out PixelRect bounds) { bounds = new(0, 0, 760, 520); return true; }
    }
    internal sealed class FakeProvider : ITranslationProvider
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource SecondEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal ConcurrentQueue<string> Requests { get; } = new();
        internal ConcurrentQueue<CancellationToken> Tokens { get; } = new();
        internal bool HoldFirst;
        internal bool HoldAll { get; set; }
        internal int Cancellations;
        public ProviderDescriptor Descriptor { get; } = new("fake", "Fake", "fake", "fake", true);
        public async IAsyncEnumerable<TranslationChunk> TranslateStreamAsync(ProviderTranslateRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Requests.Enqueue(request.Text); Tokens.Enqueue(cancellationToken); Entered.TrySetResult();
            if (Requests.Count == 2) SecondEntered.TrySetResult();
            using var registration = cancellationToken.Register(() => Interlocked.Increment(ref Cancellations));
            if (HoldAll || HoldFirst && Requests.Count == 1) await Release.Task.WaitAsync(cancellationToken);
            yield return new TranslationChunk("translated");
        }
        public Task<ProviderTestResult> TestAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
