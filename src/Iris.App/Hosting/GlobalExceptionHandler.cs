using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Iris.Core.Errors;
using Iris.Core.Presentation;
using Microsoft.Extensions.Logging;

namespace Iris.App.Hosting;

/// <summary>
/// 全局未捕获异常处理：拦截 UI 线程异常与未观察 Task 异常，防止进程意外崩溃并进行限流通知。
/// </summary>
internal sealed partial class GlobalExceptionHandler : IDisposable
{
    private readonly ILogger<GlobalExceptionHandler> _logger;
    private readonly INotificationService _notificationService;
    private readonly NotificationThrottle _throttle;
    private Application? _app;
    private bool _installed;
    private bool _disposed;

    public GlobalExceptionHandler(
        ILogger<GlobalExceptionHandler> logger,
        INotificationService notificationService,
        NotificationThrottle throttle)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _notificationService = notificationService ?? throw new ArgumentNullException(nameof(notificationService));
        _throttle = throttle ?? throw new ArgumentNullException(nameof(throttle));
    }

    public void Install(Application app)
    {
        ArgumentNullException.ThrowIfNull(app);
        if (_installed)
        {
            throw new InvalidOperationException("GlobalExceptionHandler 已经被安装，无法重复安装。");
        }

        _installed = true;
        _app = app;

        _app.DispatcherUnhandledException += OnDispatcherUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        AppDomain.CurrentDomain.UnhandledException += OnAppDomainUnhandledException;
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        LogDispatcherException(_logger, e.Exception);
        e.Handled = true;

        if (_throttle.TryAcquire())
        {
            var message = e.Exception is IrisException irisEx
                ? irisEx.Message
                : UiText.DispatcherUnhandledMessage;

            _notificationService.Notify(NotificationLevel.Error, UiText.AppName, message);
        }
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        LogUnobservedTaskException(_logger, e.Exception);
        e.SetObserved();
    }

    private void OnAppDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        var ex = e.ExceptionObject as Exception;
        LogAppDomainFatalException(_logger, ex);
        Serilog.Log.CloseAndFlush();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_app != null)
        {
            _app.DispatcherUnhandledException -= OnDispatcherUnhandledException;
            _app = null;
        }

        TaskScheduler.UnobservedTaskException -= OnUnobservedTaskException;
        AppDomain.CurrentDomain.UnhandledException -= OnAppDomainUnhandledException;
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Error, Message = "UI 线程未捕获异常")]
    private static partial void LogDispatcherException(ILogger logger, Exception ex);

    [LoggerMessage(EventId = 2, Level = LogLevel.Error, Message = "Task 未观察到的异常")]
    private static partial void LogUnobservedTaskException(ILogger logger, Exception ex);

    [LoggerMessage(EventId = 3, Level = LogLevel.Critical, Message = "AppDomain 致命未捕获异常，进程即将终止")]
    private static partial void LogAppDomainFatalException(ILogger logger, Exception? ex);
}
