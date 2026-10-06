using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using Iris.App.Hosting;
using Iris.App.Services;
using Iris.App.Tests.TestSupport;
using Iris.App.Translation;
using Iris.App.Tray;
using Iris.Core.Actions;
using Iris.Core.Common;
using Iris.Core.DependencyInjection;
using Iris.Core.Errors;
using Iris.Core.Imaging;
using Iris.Core.Languages;
using Iris.Core.Platform;
using Iris.Core.Presentation;
using Iris.Core.Settings;
using Iris.Core.Translation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Iris.App.Tests.Workflows;

internal sealed class SelectionEntryTestScope
{
    public ISettingsService Settings { get; } = Substitute.For<ISettingsService>();
    public IDesktopContext Desktop { get; } = Substitute.For<IDesktopContext>();
    public ISelectionTextReader Reader { get; } = Substitute.For<ISelectionTextReader>();
    public AppSettings Current { get; set; } = new()
    {
        Selection = new SelectionSettings { OpenInputWhenEmpty = false },
        TranslateWindow = new TranslateWindowSettings { HideOnDeactivate = false },
        Translation = new TranslationSettings { AutoCopy = AutoCopyMode.None, SelectionLineBreaks = LineBreakHandling.MergeSmart }
    };
    public GatedUiDispatcher Ui { get; } = new();
    public NativeNotifications Native { get; } = new();
    public TrayIconController Tray { get; }
    public TrayNotificationService Notifications { get; }
    public TranslateWindowPresenter Presenter { get; }
    public ServiceProvider Provider { get; }
    public ActionDispatcher Dispatcher { get; }
    public HotkeyCoordinator Hotkeys { get; }
    public FakeNativeHotkeyAdapter HotkeyNative { get; } = new();
    public RecordingOrchestrator Orchestrator { get; } = new();
    public RecordingPreprocessor Preprocessor { get; }
    public List<ITranslateWindowOperation> Operations { get; } = [];
    public List<Action> Release { get; } = [];
    public List<(Task Task, Type? Allowed)> Tasks { get; } = [];
    public Action? AfterCleanup { get; set; }
    public int WindowsCreated { get; private set; }

    public SelectionEntryTestScope(bool attachTray = true)
    {
        Settings.Current.Returns(_ => Current);
        Desktop.GetForegroundApp().Returns(new ForegroundAppInfo("external.exe", Environment.ProcessId + 1, "private title", 1));
        Desktop.GetMonitors().Returns([new MonitorInfo("fake", new(0, 0, 1920, 1080), new(0, 0, 1920, 1040), 1, true)]);
        Desktop.GetCursorPosition().Returns(new PixelPoint(100, 100));
        var placement = Substitute.For<INativeWindowPlacementAdapter>();
        placement.SetWindowBounds(Arg.Any<nint>(), Arg.Any<PixelRect>(), Arg.Any<bool>(), Arg.Any<bool>()).Returns(true);
        var services = new ServiceCollection();
        services.AddLogging(); services.AddIrisCore();
        services.AddSingleton(Substitute.For<IScreenshotService>());
        services.AddSingleton(Substitute.For<IScreenshotOcrFeedback>());
        services.AddSingleton(Substitute.For<Iris.Core.Ocr.IOcrService>());
        services.AddSingleton(Settings); services.AddSingleton(Desktop); services.AddSingleton(Reader);
        services.AddSingleton<IUiDispatcher>(Ui); services.AddSingleton<ITranslationOrchestrator>(Orchestrator);
        services.AddSingleton(Substitute.For<IClipboardService>()); services.AddSingleton(Substitute.For<ISettingsWindowPresenter>());
        services.AddSingleton(Substitute.For<IAppLifetime>()); services.AddSingleton<ITrayNotificationNative>(Native);
        services.AddSingleton(placement); services.AddSingleton<TrayNotificationService>();
        services.AddSingleton<TrayIconController>();
        services.AddSingleton<INotificationService>(sp => sp.GetRequiredService<TrayNotificationService>());
        services.AddSingleton<TranslateWindowPresenter>();
        services.AddSingleton<ITranslateWindowPresenter>(sp => sp.GetRequiredService<TranslateWindowPresenter>());
        services.AddSingleton<ActionDispatcher>(); services.AddSingleton<IActionDispatcher>(sp => sp.GetRequiredService<ActionDispatcher>());
        services.AddSingleton<IHotkeyService>(new Iris.Platform.Windows.Hotkey.HotkeyService(NullLogger<Iris.Platform.Windows.Hotkey.HotkeyService>.Instance, HotkeyNative));
        services.AddSingleton<HotkeyCoordinator>();
        services.AddScoped<TranslateWindowViewModel>();
        services.AddTransient(_ =>
        {
            WindowsCreated++;
            return new TranslateWindow { ShowActivated = false, ShowInTaskbar = false, Opacity = 0,
                WindowStartupLocation = System.Windows.WindowStartupLocation.Manual, Left = -10000, Top = -10000 };
        });
        var original = services.Single(d => d.ServiceType == typeof(ITextPreprocessor)).ImplementationType!;
        services.AddSingleton<ITextPreprocessor>(sp => new RecordingPreprocessor((ITextPreprocessor)ActivatorUtilities.CreateInstance(sp, original)));
        Provider = services.BuildServiceProvider();
        Presenter = Provider.GetRequiredService<TranslateWindowPresenter>();
        Dispatcher = Provider.GetRequiredService<ActionDispatcher>();
        Notifications = Provider.GetRequiredService<TrayNotificationService>();
        Tray = Provider.GetRequiredService<TrayIconController>(); Tray.SuppressIconCreationForTesting = true;
        if (attachTray) Notifications.AttachTray(Tray);
        Hotkeys = Provider.GetRequiredService<HotkeyCoordinator>();
        Preprocessor = (RecordingPreprocessor)Provider.GetRequiredService<ITextPreprocessor>();
    }

    public ITranslateWindowOperation Begin(CancellationToken token = default)
    {
        var op = Presenter.TryBeginOperation(token)!; Operations.Add(op); return op;
    }
    public T Track<T>(T task, Type? allowed = null) where T : Task { Tasks.Add((task, allowed)); return task; }
    public static Task Run(Func<SelectionEntryTestScope, Task> body, bool attachTray = true) => WpfTestRunner.RunAsync(async () =>
    {
        var s = new SelectionEntryTestScope(attachTray); Exception? primary = null; var errors = new List<Exception>();
        try { await body(s); } catch (Exception ex) { primary = ex; }
        finally
        {
            foreach (var release in s.Release) try { release(); } catch (Exception ex) { errors.Add(ex); }
            s.Ui.ReleaseAll();
            s.Dispatcher.StopAcceptance();
            foreach (var op in s.Operations) op.Dispose();
            try { await s.Hotkeys.StopAcceptanceAndNativeAsync(); } catch (Exception ex) { errors.Add(ex); }
            foreach (var (task, allowed) in s.Tasks) try { await task; } catch (Exception ex) { if (allowed == null || !allowed.IsInstanceOfType(ex)) errors.Add(ex); }
            try { await s.Hotkeys.WaitForInFlightDispatchesAsync(); await s.Dispatcher.WaitForInFlightAsync(); } catch (Exception ex) { errors.Add(ex); }
            try { await s.Presenter.ShutdownAsync(); } catch (Exception ex) { errors.Add(ex); }
            try { await s.Provider.DisposeAsync(); } catch (Exception ex) { errors.Add(ex); }
            try { s.AfterCleanup?.Invoke(); } catch (Exception ex) { errors.Add(ex); }
        }
        if (primary != null && errors.Count == 0) ExceptionDispatchInfo.Capture(primary).Throw();
        if (primary != null) errors.Insert(0, primary);
        if (errors.Count != 0) throw new AggregateException(errors);
    });

    internal sealed class GatedUiDispatcher : IUiDispatcher
    {
        private readonly WpfUiDispatcher _inner = new();
        private UiGate? _next;
        private readonly List<UiGate> _gates = [];
        public int Calls { get; private set; }
        public UiGate GateNext() { var gate = new UiGate(); _gates.Add(gate); _next = gate; return gate; }
        public bool CheckAccess() => _inner.CheckAccess();
        public Task InvokeAsync(Func<Task> callback, CancellationToken cancellationToken)
        {
            Calls++;
            var gate = Interlocked.Exchange(ref _next, null);
            return _inner.InvokeAsync(async () =>
            {
                if (gate != null)
                {
                    gate.Entered.TrySetResult(); await gate.Release.Task;
                    if (gate.Failure != null) throw gate.Failure;
                }
                await callback();
            }, cancellationToken);
        }
        public void ReleaseAll() { foreach (var gate in _gates) gate.Release.TrySetResult(); }
    }
    internal sealed class UiGate
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Exception? Failure { get; set; }
    }
    internal sealed class NativeNotifications : ITrayNotificationNative
    {
        public bool IsCreated { get; set; } = true;
        public Exception? Failure { get; set; }
        public List<(string Title, string Message, NotificationLevel Level, bool Ui)> Sent { get; } = [];
        public void Show(string title, string message, NotificationLevel level)
        {
            if (Failure != null) throw Failure;
            Sent.Add((title, message, level, System.Windows.Application.Current.Dispatcher.CheckAccess()));
        }
    }
    internal sealed class RecordingPreprocessor(ITextPreprocessor inner) : ITextPreprocessor
    {
        public ConcurrentQueue<(string Raw, TextOrigin Origin, string Prepared)> Calls { get; } = new();
        public string Prepare(string raw, TextOrigin origin)
        { var prepared = inner.Prepare(raw, origin); Calls.Enqueue((raw, origin, prepared)); return prepared; }
        public string Prepare(string raw, TextOrigin origin, TranslationSettings settings)
        { var prepared = inner.Prepare(raw, origin, settings); Calls.Enqueue((raw, origin, prepared)); return prepared; }
    }
    internal sealed class RecordingOrchestrator : ITranslationOrchestrator
    {
        public ConcurrentQueue<TranslationRequest> Requests { get; } = new();
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async IAsyncEnumerable<TranslationEvent> TranslateAsync(TranslationRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Requests.Enqueue(request); Started.TrySetResult(); await Task.Yield(); cancellationToken.ThrowIfCancellationRequested();
            yield return new TranslationStarted(request.RequestId, new ResolvedLanguagePair("en", "zh-Hans", false), [new ProviderDescriptor("fake", "Fake", "fake", "fake", true)]);
            yield return new ProviderFailed(request.RequestId, "fake", new ErrorInfo(ErrorCode.ProviderUnavailable, "safe"), TimeSpan.Zero);
            yield return new TranslationFinished(request.RequestId, 0, 1);
        }
    }
}
