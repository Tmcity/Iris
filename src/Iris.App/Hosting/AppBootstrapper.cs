using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Iris.App.Services;
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

internal sealed record AppStartupInfo(long StartTimestamp);

/// <summary>
/// 协调应用程序的启动序列与退出清理流程。
/// </summary>
internal sealed partial class AppBootstrapper
{
    private readonly ISettingsService _settingsService;
    private readonly ITranslationProviderRegistry _translationRegistry;
    private readonly IDatabaseInitializer _databaseInitializer;
    private readonly LogLevelController _logLevelController;
    private readonly ThemeService _themeService;
    private readonly TrayIconController _trayIconController;
    private readonly TrayNotificationService _trayNotificationService;
    private readonly IHost _host;
    private readonly ILogger<AppBootstrapper> _logger;
    private readonly AppStartupInfo _startupInfo;

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
        AppStartupInfo startupInfo)
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
    }

    internal static Action<string, string> FatalErrorShower { get; set; } = FatalErrorDialog.Show;
    internal static Action<int> AppShutdowner { get; set; } = code => Application.Current?.Shutdown(code);

    public async Task StartAsync()
    {
        // 1. 加载设置
        try
        {
            await _settingsService.LoadAsync();
        }
        catch (IrisException ex) when (ex.Code == ErrorCode.SettingsCorrupted)
        {
            _trayNotificationService.Notify(NotificationLevel.Warning, UiText.AppName, UiText.SettingsCorruptedNotice);
        }
        catch (IrisException ex) when (ex.Code == ErrorCode.StorageFailure)
        {
            FatalErrorShower(UiText.AppName, FatalErrorFormatter.Format(ex));
            AppShutdowner(1);
            return;
        }

        // T-011: 设置成功加载（含损坏恢复）后，在用户入口启用前调用 ITranslationProviderRegistry.RefreshAsync()
        await _translationRegistry.RefreshAsync();

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

        // 5. 创建托盘图标并输出排队通知（先订阅 Created，创建成功后才挂接通知）
        _trayIconController.Created += OnTrayIconCreated;
        _trayIconController.Initialize();
        if (_trayIconController.IsCreated)
        {
            EnsureTrayAttached();
        }

        // TODO(T-017): 注册热键
        // TODO(T-024): 鼠标监视
        // TODO(T-033): 悬浮球
        // TODO(T-037): 启动命名管道 IPC 服务

        // 5. 启动后台 Host 服务
        await _host.StartAsync();

        // 6. 启动通知
        if (_settingsService.Current.General.ShowStartupNotification)
        {
            _trayNotificationService.Notify(NotificationLevel.Info, UiText.AppName, UiText.StartupRunningInBackground);
        }

        // 7. 记录耗时日志
        var elapsedMs = Stopwatch.GetElapsedTime(_startupInfo.StartTimestamp).TotalMilliseconds;
        var trayState = _trayIconController.IsCreated ? "已创建" : "创建中（将自动重试）";
        LogStartupReady(_logger, elapsedMs, trayState);
        LogDebugEnabled(_logger);
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
        if (Interlocked.Exchange(ref _syncStopped, 1) != 0)
        {
            return;
        }

        _trayIconController.Created -= OnTrayIconCreated;
        _themeService.Dispose();
        _logLevelController.Dispose();
        _trayIconController.Dispose();
    }

    public async Task StopAsync()
    {
        LogStopping(_logger);

        if (Interlocked.Exchange(ref _stopped, 1) != 0)
        {
            return;
        }

        StopSynchronousParts();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            await _host.StopAsync(cts.Token);
        }
        catch (Exception ex)
        {
            LogHostStopFailed(_logger, ex);
        }

        Application.Current?.Shutdown(0);
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Iris 启动序列完成，耗时 {ElapsedMs:F1} ms，托盘图标{TrayState}")]
    private static partial void LogStartupReady(ILogger logger, double elapsedMs, string trayState);

    [LoggerMessage(EventId = 2, Level = LogLevel.Debug, Message = "调试级别日志已启用，当前日志级别为 Debug/Verbose")]
    private static partial void LogDebugEnabled(ILogger logger);

    [LoggerMessage(EventId = 3, Level = LogLevel.Error, Message = "Host 停止超时或遇到异常")]
    private static partial void LogHostStopFailed(ILogger logger, Exception ex);

    [LoggerMessage(EventId = 4, Level = LogLevel.Information, Message = "Iris 正在退出")]
    private static partial void LogStopping(ILogger logger);
}
