using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Iris.App.Services;
using Iris.App.Selection;
using Iris.App.Tray;
using Iris.Core.Errors;
using Iris.Core.Presentation;
using Iris.Core.Settings;
using Iris.Core.Storage;
using Iris.Core.Translation;
using Iris.Platform.Windows.SystemIntegration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Iris.App.Hosting;

internal sealed class AppStartupInfo(long startTimestamp, Iris.Core.Actions.IpcCommand? initialCommand = null)
{
    private Iris.Core.Actions.IpcCommand? _command = initialCommand;
    internal long StartTimestamp { get; } = startTimestamp;
    internal Iris.Core.Actions.IpcCommand? TakeCommand() => Interlocked.Exchange(ref _command, null);
}

/// <summary>
/// 协调应用程序的启动序列与退出清理流程。
/// </summary>
internal sealed partial class AppBootstrapper
{
    private readonly ISettingsService _settingsService;
    private readonly Iris.Core.Network.INetworkProxySnapshotSource? _networkSource;
    private readonly Iris.Core.Network.INetworkTransportLifetime? _networkLifetime;
    private readonly ITranslationProviderRegistry _translationRegistry;
    private readonly Iris.Core.Ocr.IOcrProviderRegistry? _ocrRegistry;
    private readonly Iris.Core.Ocr.IOcrService? _ocrService;
    private readonly ScreenshotOcrFeedback? _screenshotOcrFeedback;
    private readonly IDatabaseInitializer _databaseInitializer;
    private readonly LogLevelController _logLevelController;
    private readonly ThemeService _themeService;
    private readonly TrayIconController _trayIconController;
    private readonly TrayNotificationService _trayNotificationService;
    private readonly IHost _host;
    private readonly ILogger<AppBootstrapper> _logger;
    private readonly AppStartupInfo _startupInfo;
    private readonly Iris.App.Settings.ISettingsShutdownCoordinator? _settingsShutdownCoordinator;
    private readonly Iris.App.Translation.ITranslateWindowShutdownCoordinator? _translateShutdownCoordinator;
    private readonly Iris.App.History.IHistoryShutdownCoordinator? _historyShutdownCoordinator;
    private readonly HotkeyCoordinator? _hotkeyCoordinator;
    private readonly Iris.Core.Platform.IHotkeyCaptureService? _captureService;
    private readonly HotkeyRecordingCoordinator? _recordingCoordinator;
    private readonly Iris.Platform.Windows.Selection.CompositeSelectionReader? _selectionReader;
    private readonly ActionDispatcher? _actionDispatcher;
    private readonly SelectionReadCoordinator? _selectionReadCoordinator;
    private readonly SelectionIconController? _selectionIconController;
    private readonly Iris.Platform.Windows.Capture.GdiScreenCapturer? _screenCapturer;
    private readonly Iris.App.Capture.ScreenshotPreparationCoordinator? _screenshotPreparation;
    private readonly Iris.App.Capture.ScreenshotService? _screenshotService;
    private readonly Iris.Platform.Windows.Imaging.WinRtImagePreprocessor? _imagePreprocessor;
    private readonly IpcCommandCoordinator? _ipc;
    private readonly Iris.App.Updates.AppUpdateCoordinator? _updates;
    internal bool CleanupCompleted => Volatile.Read(ref _stopped) != 0;

    private readonly Lock _stopGate = new();
    private Task? _stopTask;
    private Task? _startTask;
    private int _stopRequested;
    private int _stopped;
    private int _syncStopped;
    private int _trayAttached;

    public AppBootstrapper(
        ISettingsService settingsService,
        ITranslationProviderRegistry translationRegistry,
        IDatabaseInitializer databaseInitializer,
        LogLevelController logLevelController,
        ThemeService themeService,
        TrayIconController trayIconController,
        TrayNotificationService trayNotificationService,
        IHost host,
        ILogger<AppBootstrapper> logger,
        AppStartupInfo startupInfo,
        Iris.App.Settings.ISettingsShutdownCoordinator? settingsShutdownCoordinator = null,
        Iris.App.Translation.ITranslateWindowShutdownCoordinator? translateShutdownCoordinator = null,
        HotkeyCoordinator? hotkeyCoordinator = null,
        Iris.Core.Platform.IHotkeyCaptureService? captureService = null,
        HotkeyRecordingCoordinator? recordingCoordinator = null,
        Iris.Platform.Windows.Selection.CompositeSelectionReader? selectionReader = null,
        Iris.Core.Actions.IActionDispatcher? actionDispatcher = null,
        SelectionReadCoordinator? selectionReadCoordinator = null,
        SelectionIconController? selectionIconController = null,
        Iris.Platform.Windows.Capture.GdiScreenCapturer? screenCapturer = null,
        Iris.App.Capture.ScreenshotPreparationCoordinator? screenshotPreparation = null,
        Iris.App.Capture.ScreenshotService? screenshotService = null,
        Iris.Platform.Windows.Imaging.WinRtImagePreprocessor? imagePreprocessor = null,
        Iris.Core.Ocr.IOcrProviderRegistry? ocrRegistry = null,
        Iris.Core.Ocr.IOcrService? ocrService = null,
        ScreenshotOcrFeedback? screenshotOcrFeedback = null,
        Iris.App.History.IHistoryShutdownCoordinator? historyShutdownCoordinator = null,
        Iris.Core.Network.INetworkProxySnapshotSource? networkSource = null,
        Iris.Core.Network.INetworkTransportLifetime? networkLifetime = null,
        IpcCommandCoordinator? ipc = null,
        Iris.App.Updates.AppUpdateCoordinator? updates = null)
    {
        _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
        _translationRegistry = translationRegistry ?? throw new ArgumentNullException(nameof(translationRegistry));
        _databaseInitializer = databaseInitializer ?? throw new ArgumentNullException(nameof(databaseInitializer));
        _logLevelController = logLevelController ?? throw new ArgumentNullException(nameof(logLevelController));
        _themeService = themeService ?? throw new ArgumentNullException(nameof(themeService));
        _trayIconController = trayIconController ?? throw new ArgumentNullException(nameof(trayIconController));
        _trayNotificationService = trayNotificationService ?? throw new ArgumentNullException(nameof(trayNotificationService));
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _startupInfo = startupInfo ?? throw new ArgumentNullException(nameof(startupInfo));
        _settingsShutdownCoordinator = settingsShutdownCoordinator;
        _translateShutdownCoordinator = translateShutdownCoordinator;
        _historyShutdownCoordinator = historyShutdownCoordinator;
        _networkSource = networkSource;
        _networkLifetime = networkLifetime;
        _hotkeyCoordinator = hotkeyCoordinator;
        _captureService = captureService;
        _recordingCoordinator = recordingCoordinator;
        _selectionReader = selectionReader;
        _actionDispatcher = actionDispatcher as ActionDispatcher;
        _selectionReadCoordinator = selectionReadCoordinator;
        _selectionIconController = selectionIconController;
        _screenCapturer = screenCapturer;
        _screenshotPreparation = screenshotPreparation;
        _screenshotService = screenshotService;
        _imagePreprocessor = imagePreprocessor;
        _ocrRegistry = ocrRegistry;
        _ocrService = ocrService;
        _screenshotOcrFeedback = screenshotOcrFeedback;
        _ipc = ipc;
        _updates = updates;
    }

    internal static Action<string, string> FatalErrorShower { get; set; } = FatalErrorDialog.Show;
    internal static Action<int> AppShutdowner { get; set; } = code =>
    {
        var app = Application.Current;
        if (app == null) return;
        if (app.Dispatcher.CheckAccess())
        {
            app.Shutdown(code);
        }
        else
        {
            app.Dispatcher.InvokeAsync(() => app.Shutdown(code));
        }
    };

    public Task StartAsync()
    {
        lock (_stopGate)
        {
            if (_stopRequested != 0) return Task.CompletedTask;
            return _startTask ??= StartCoreAsync();
        }
    }

    private async Task StartCoreAsync()
    {
        await Task.Yield();
        bool settingsRecovered = false;
        // 1. 加载设置
        try
        {
            await _settingsService.LoadAsync();

        }
        catch (IrisException ex) when (ex.Code == ErrorCode.SettingsCorrupted)
        {
            settingsRecovered = true;
        }
        catch (IrisException ex) when (ex.Code == ErrorCode.StorageFailure)
        {
            FatalErrorShower(UiText.AppName, FatalErrorFormatter.Format(ex));
            AppShutdowner(1);
            return;
        }

        if (Volatile.Read(ref _stopRequested) != 0) return;
        Iris.App.Resources.AppLanguage.Select(_settingsService.Current.General.UiLanguage);
        if (settingsRecovered) _trayNotificationService.Notify(NotificationLevel.Warning, UiText.AppName, UiText.SettingsCorruptedNotice);
        if (_settingsService.IsReadOnly)
            _trayNotificationService.Notify(NotificationLevel.Warning, UiText.AppName, UiText.SettingsFutureReadOnly);
        else if (_networkSource != null)
        {
            try
            {
                // Pure managed capture prepares the initial identity before any entry point or Provider refresh.
                // Invalid dormant Custom configuration remains repairable in Settings and all affected sends fail closed.
                await _networkSource.CaptureCurrentAsync(new Uri("http://localhost/"));
            }
            catch (IrisException ex) when (ex.Code is ErrorCode.InvalidArgument or ErrorCode.SecretUnavailable or ErrorCode.NetworkUnavailable)
            {
                _trayNotificationService.Notify(NotificationLevel.Warning, UiText.AppName, UiText.NetworkConfigurationUnavailable);
            }
        }

        // T-011: 设置成功加载（含损坏恢复）后，在用户入口启用前调用 ITranslationProviderRegistry.RefreshAsync()
        if (Volatile.Read(ref _stopRequested) != 0) return;
        await _translationRegistry.RefreshAsync();
        if (Volatile.Read(ref _stopRequested) != 0) return;
        if (_ocrRegistry != null) await _ocrRegistry.RefreshAsync();
        if (Volatile.Read(ref _stopRequested) != 0) return;

        // 2. 配置日志级别
        _logLevelController.ApplyCurrent();

        // 3. 应用主题
        _themeService.Apply(_settingsService.Current.General.Theme);

        // 4. 数据库初始化（在设置加载后、用户入口启用前）
        try
        {
            await _databaseInitializer.InitializeAsync();
        }
        catch (IrisException ex) when (ex.Code == ErrorCode.StorageFailure)
        {
            FatalErrorShower(UiText.AppName, FatalErrorFormatter.Format(ex));
            AppShutdowner(1);
            return;
        }

        if (Volatile.Read(ref _stopRequested) != 0) return;
        // 5. 创建托盘图标并输出排队通知（先订阅 Created，创建成功后才挂接通知）
        _trayIconController.Created += OnTrayIconCreated;
        _trayIconController.Initialize();
        if (_trayIconController.IsCreated)
        {
            EnsureTrayAttached();
        }

        // 注册热键协调器
        if (_hotkeyCoordinator != null)
        {
            await _hotkeyCoordinator.StartAsync().ConfigureAwait(false);
        }

        if (Volatile.Read(ref _stopRequested) != 0) return;
        if (_selectionIconController != null) await _selectionIconController.StartAsync().ConfigureAwait(false);
        // TODO(T-033): 悬浮球

        // 5. 启动后台 Host 服务
        if (Volatile.Read(ref _stopRequested) != 0) return;
        await _host.StartAsync();
        // Serialize the ready transition with stop acceptance, not just an earlier flag check.
        lock (_stopGate)
        {
            if (_stopRequested != 0) return;
            _ipc?.Start(_startupInfo.TakeCommand());
            if (_settingsService.Current.General.ShowStartupNotification)
            {
                _trayNotificationService.Notify(NotificationLevel.Info, UiText.AppName, UiText.StartupRunningInBackground);
            }
            var elapsedMs = Stopwatch.GetElapsedTime(_startupInfo.StartTimestamp).TotalMilliseconds;
            var trayState = _trayIconController.IsCreated ? "已创建" : "创建中（将自动重试）";
            LogStartupReady(_logger, elapsedMs, trayState);
            LogDebugEnabled(_logger);
        }
        _updates?.Start();
    }

    private void EnsureTrayAttached()
    {
        if (Interlocked.Exchange(ref _trayAttached, 1) == 0)
        {
            _trayNotificationService.AttachTray(_trayIconController);
        }
    }

    private void OnTrayIconCreated(object? sender, EventArgs e)
    {
        EnsureTrayAttached();
    }

    public void StopSynchronousParts()
    {
        StopIpcAcceptance();
        if (Volatile.Read(ref _syncStopped) != 0)
        {
            return;
        }

        _hotkeyCoordinator?.StopSynchronousFallback();
        _captureService?.Dispose();
        _trayIconController.Created -= OnTrayIconCreated;
        _themeService.Dispose();
        _logLevelController.Dispose();
        _trayIconController.Dispose();
        Volatile.Write(ref _syncStopped, 1);
    }

    public Task StopAsync()
    {
        StopIpcAcceptance();
        _updates?.StopAcceptance();
        (_settingsShutdownCoordinator as Iris.App.Settings.SettingsWindowPresenter)?.StopAcceptance();
        _networkLifetime?.StopAcceptance();
        _historyShutdownCoordinator?.StopAcceptance();
        _screenshotOcrFeedback?.StopAcceptance();
        (_translateShutdownCoordinator as Iris.App.Translation.TranslateWindowPresenter)?.StopAcceptance();
        _ocrService?.StopAccepting();
        _ocrRegistry?.StopAccepting();
        if (_ocrService == null) _imagePreprocessor?.StopAcceptance();
        _screenshotService?.StopAcceptance();
        _screenshotPreparation?.StopAcceptance();
        _recordingCoordinator?.StopAcceptance();
        _screenCapturer?.StopAcceptance();
        LogStopping(_logger);
        _actionDispatcher?.StopAcceptance();
        _selectionIconController?.StopAcceptance();
        _selectionReadCoordinator?.StopAcceptance();

        lock (_stopGate)
        {
            if (_stopTask != null && !_stopTask.IsCompleted)
            {
                return _stopTask;
            }

            if (_stopped != 0)
            {
                return Task.CompletedTask;
            }

            if (_stopTask != null && _stopTask.IsCompletedSuccessfully)
            {
                return _stopTask;
            }

            _stopTask = StopCoreAsync();
            return _stopTask;
        }
    }

    private async Task StopCoreAsync()
    {
        await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        var earlyFailures = new System.Collections.Generic.List<string>();
        if (_settingsShutdownCoordinator is Iris.App.Settings.SettingsWindowPresenter settingsPresenter)
            try { await settingsPresenter.StopSessionAcceptanceAsync().ConfigureAwait(false); }
            catch (Exception) { earlyFailures.Add("SETTINGS_ADMISSION"); }
        if (_updates != null)
            try { await _updates.StopAsync().ConfigureAwait(false); }
            catch (Exception) { earlyFailures.Add("UPDATES"); }
        if (_startTask != null)
            try { await _startTask.ConfigureAwait(false); }
            catch (Exception) { /* Start owns its safe user-facing failure; still close retained resources. */ }
        if (_ipc != null)
            try { await _ipc.StopTransportAsync().ConfigureAwait(false); }
            catch (Exception) { earlyFailures.Add("IPC_TRANSPORT"); }
        if (_screenshotOcrFeedback != null)
        {
            try { await _screenshotOcrFeedback.StopAsync().ConfigureAwait(false); }
            catch (Exception ex) { earlyFailures.Add($"OCR_FEEDBACK:0x{ex.HResult:X8}"); }
        }
        bool ocrPending = false;
        if (_ocrService != null)
        {
            try { await _ocrService.StopAsync().ConfigureAwait(false); }
            catch (Exception ex) { ocrPending = true; earlyFailures.Add($"OCR_SERVICE:0x{ex.HResult:X8}"); }
        }
        bool preparationPending = false;
        if (_screenshotService != null)
        {
            try { await _screenshotService.StopAsync().ConfigureAwait(false); }
            catch (Exception ex) { earlyFailures.Add(ex is IrisException { Detail: not null } known
                ? $"SCREENSHOT[{known.Detail}]" : $"SCREENSHOT:0x{ex.HResult:X8}"); }
        }
        // A is a dependency of any still-owned overlay. Do not release it through a second path.
        if (_screenshotPreparation != null && _screenshotService?.HasPendingOverlays != true)
        {
            try { await _screenshotPreparation.StopAsync().ConfigureAwait(false); }
            catch (Exception ex) { preparationPending = true; earlyFailures.Add(ex is IrisException { Detail: not null } known
                ? $"PREPARATION[{known.Detail}]" : $"PREPARATION:0x{ex.HResult:X8}"); }
        }
        if (_imagePreprocessor != null && !ocrPending)
        {
            try { await _imagePreprocessor.StopAsync().ConfigureAwait(false); }
            catch (Exception ex) { earlyFailures.Add(ex is IrisException { Detail: not null } known
                ? $"IMAGE_PREPROCESSOR[{known.Detail}]" : $"IMAGE_PREPROCESSOR:0x{ex.HResult:X8}"); }
        }
        string? screenFailure = null;
        if (_screenCapturer != null)
        {
            try { await _screenCapturer.StopAsync().ConfigureAwait(false); }
            catch (Exception ex)
            {
                screenFailure = ex is IrisException { Detail: not null } known
                    ? $"SCREEN[{known.Detail}]" : $"SCREEN:0x{ex.HResult:X8}";
            }
        }
        // 1. 先关闭热键/设置重配准入，在 UI 注销/销毁自己的消息窗口并取消待处理动作
        if (_hotkeyCoordinator != null)
        {
            try
            {
                await _hotkeyCoordinator.StopAcceptanceAndNativeAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                LogHotkeyShutdownFailed(_logger, ex.GetType().Name);
                earlyFailures.Add($"HOTKEY:0x{ex.HResult:X8}");
            }
        }

        // 2. 撤销录入准入并结束录入控制器（含等待捕获和原生清理）
        if (_recordingCoordinator != null)
        {
            try
            {
                await _recordingCoordinator.StopAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                LogCaptureShutdownFailed(_logger, ex.GetType().Name);
                earlyFailures.Add($"RECORDING:0x{ex.HResult:X8}");
            }
        }
        else if (_captureService != null)
        {
            try
            {
                await _captureService.StopAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                LogCaptureShutdownFailed(_logger, ex.GetType().Name);
                earlyFailures.Add($"RECORDING:0x{ex.HResult:X8}");
            }
        }

        // Selection Stop includes conditional restoration before stopping its clipboard worker.
        var selectionFailures = earlyFailures;
        if (screenFailure != null) selectionFailures.Add(screenFailure);
        ErrorCode selectionError = ErrorCode.CaptureFailed;
        if (_selectionIconController != null)
        {
            try { await _selectionIconController.StopAsync().ConfigureAwait(false); }
            catch (Exception ex) { selectionError = ErrorCode.HookInstallFailed; selectionFailures.Add($"MOUSE:0x{ex.HResult:X8}"); }
        }
        if (_selectionReadCoordinator != null)
        {
            try { await _selectionReadCoordinator.StopAsync().ConfigureAwait(false); }
            catch (Exception ex) { selectionFailures.Add($"COORDINATOR:0x{ex.HResult:X8}"); }
        }
        if (_selectionReader != null)
        {
            try { await _selectionReader.StopAsync().ConfigureAwait(false); }
            catch (Exception ex) { selectionFailures.Add($"COMPOSITE:0x{ex.HResult:X8}"); }
        }
        bool ownersReady = !preparationPending && _screenshotService?.HasPendingOverlays != true;
        if (!ownersReady) selectionFailures.Add("OWNERS_DEPEND_ON_OVERLAY");

        // 3. 然后配合既有设置/翻译 Presenter 完整退出
        if (_settingsShutdownCoordinator != null && !preparationPending && _screenshotService?.HasPendingOverlays != true)
        {
            try
            {
                await _settingsShutdownCoordinator.ShutdownAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                LogSettingsShutdownFailed(_logger, ex.GetType().Name);
                ownersReady = false; selectionFailures.Add($"SETTINGS_OWNER:0x{ex.HResult:X8}");
            }
        }

        if (_historyShutdownCoordinator != null && !preparationPending && _screenshotService?.HasPendingOverlays != true)
        {
            try { await _historyShutdownCoordinator.ShutdownAsync().ConfigureAwait(false); }
            catch (Exception ex) { ownersReady = false; selectionFailures.Add($"HISTORY_OWNER:0x{ex.HResult:X8}"); }
        }

        if (_translateShutdownCoordinator != null && !preparationPending && _screenshotService?.HasPendingOverlays != true)
        {
            try
            {
                await _translateShutdownCoordinator.ShutdownAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                LogTranslateShutdownFailed(_logger, ex.GetType().Name);
                ownersReady = false; selectionFailures.Add($"TRANSLATION_OWNER:0x{ex.HResult:X8}");
            }
        }

        // Owners must release their OCR leases before registry shutdown can drain them.
        if (ownersReady)
        {
            if (_hotkeyCoordinator != null)
                try { await _hotkeyCoordinator.WaitForInFlightDispatchesAsync().ConfigureAwait(false); }
                catch (Exception ex) { ownersReady = false; selectionFailures.Add($"HOTKEY_DISPATCH:0x{ex.HResult:X8}"); }
            if (_actionDispatcher != null)
                try { await _actionDispatcher.WaitForInFlightAsync().ConfigureAwait(false); }
                catch (Exception ex) { ownersReady = false; selectionFailures.Add($"DISPATCH:0x{ex.HResult:X8}"); }
            if (_ipc != null)
                try { await _ipc.WaitForDispatchesAsync().ConfigureAwait(false); }
                catch (Exception) { ownersReady = false; selectionFailures.Add("IPC_DISPATCH"); }
        }
        if (_ocrRegistry != null)
        {
            try
            {
                var stop = _ocrRegistry.StopAsync();
                if ((ownersReady && !ocrPending) || stop.IsCompleted) await stop.ConfigureAwait(false);
                else
                {
                    // The registry owns this stop even when a failed window still owns a lease.
                    // Observe its eventual failure without waiting indefinitely in this failed owner path.
                    _ = stop.ContinueWith(done =>
                    {
                        if (done.Exception != null) LogOcrShutdownFailed(_logger);
                    }, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                    await _ocrRegistry.RetryCleanupAsync().ConfigureAwait(false);
                    selectionFailures.Add("OCR_PENDING_OWNER");
                }
            }
            catch (Exception ex) { selectionFailures.Add(ex is IrisException { Detail: not null } known
                ? $"OCR_REGISTRY[{known.Detail}]" : $"OCR_REGISTRY:0x{ex.HResult:X8}"); }
        }
        if (selectionFailures.Count != 0) throw new IrisException(selectionError, ErrorMessages.GetDefault(selectionError), detail: string.Join(';', selectionFailures));

        if (_networkLifetime != null) await _networkLifetime.StopAsync().ConfigureAwait(false);

        // 4. Only successful owner/registry drain can proceed to Host and Dispatcher teardown.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            await _host.StopAsync(cts.Token);
        }
        catch (Exception ex)
        {
            LogHostStopFailed(_logger, ex);
            throw;
        }

        // Keep the tray Exit entry alive if Host shutdown fails, so the same owner can retry.
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.CheckAccess()) StopSynchronousParts();
        else await dispatcher.InvokeAsync(StopSynchronousParts).Task.ConfigureAwait(false);

        lock (_stopGate)
        {
            _stopped = 1;
        }

        AppShutdowner(0);
    }

    private void StopIpcAcceptance()
    {
        lock (_stopGate)
        {
            _stopRequested = 1;
            _startupInfo.TakeCommand();
            _ipc?.StopAcceptance();
        }
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Iris 启动序列完成，耗时 {ElapsedMs:F1} ms，托盘图标{TrayState}")]
    private static partial void LogStartupReady(ILogger logger, double elapsedMs, string trayState);

    [LoggerMessage(EventId = 2, Level = LogLevel.Debug, Message = "调试级别日志已启用，当前日志级别为 Debug/Verbose")]
    private static partial void LogDebugEnabled(ILogger logger);

    [LoggerMessage(EventId = 3, Level = LogLevel.Error, Message = "Host 停止超时或遇到异常")]
    private static partial void LogHostStopFailed(ILogger logger, Exception ex);

    [LoggerMessage(EventId = 4, Level = LogLevel.Information, Message = "Iris 正在退出")]
    private static partial void LogStopping(ILogger logger);

    [LoggerMessage(EventId = 5, Level = LogLevel.Warning, Message = "设置窗口关闭或持久化任务收尾遇到异常：{ExceptionType}")]
    private static partial void LogSettingsShutdownFailed(ILogger logger, string exceptionType);

    [LoggerMessage(EventId = 6, Level = LogLevel.Warning, Message = "翻译窗口关闭或活跃任务收尾遇到异常：{ExceptionType}")]
    private static partial void LogTranslateShutdownFailed(ILogger logger, string exceptionType);

    [LoggerMessage(EventId = 7, Level = LogLevel.Warning, Message = "热键服务关闭或注销原生资源遇到异常：{ExceptionType}")]
    private static partial void LogHotkeyShutdownFailed(ILogger logger, string exceptionType);

    [LoggerMessage(EventId = 9, Level = LogLevel.Warning, Message = "OCR registry shutdown remains incomplete after owner failure")]
    private static partial void LogOcrShutdownFailed(ILogger logger);

    [LoggerMessage(EventId = 8, Level = LogLevel.Warning, Message = "热键捕获服务关闭或卸载原生资源遇到异常：{ExceptionType}")]
    private static partial void LogCaptureShutdownFailed(ILogger logger, string exceptionType);
}
