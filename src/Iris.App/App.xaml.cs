using System;
using System.Windows;
using Iris.App.Hosting;
using Iris.Platform.Windows.SystemIntegration;
using Microsoft.Extensions.Logging;

namespace Iris.App;

internal partial class App : Application
{
    private readonly AppBootstrapper _bootstrapper;
    private readonly GlobalExceptionHandler _exceptionHandler;
    private readonly ILogger<App> _logger;

    public App(
        AppBootstrapper bootstrapper,
        GlobalExceptionHandler exceptionHandler,
        ILogger<App> logger)
    {
        _bootstrapper = bootstrapper ?? throw new ArgumentNullException(nameof(bootstrapper));
        _exceptionHandler = exceptionHandler ?? throw new ArgumentNullException(nameof(exceptionHandler));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        _exceptionHandler.Install(this);
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        try
        {
            await _bootstrapper.StartAsync();
        }
        catch (Exception ex)
        {
            LogStartupFailed(_logger, ex);
            FatalErrorDialog.Show(UiText.AppName, FatalErrorFormatter.Format(ex));
            Shutdown(1);
        }
    }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        LogSessionEnding(_logger, e.ReasonSessionEnding);
        base.OnSessionEnding(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _bootstrapper.StopSynchronousParts();
        _exceptionHandler.Dispose();
        LogExited(_logger, e.ApplicationExitCode);
        base.OnExit(e);
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Windows 会话即将结束（{ReasonSessionEnding}）")]
    private static partial void LogSessionEnding(ILogger logger, ReasonSessionEnding reasonSessionEnding);

    [LoggerMessage(EventId = 2, Level = LogLevel.Information, Message = "Iris 已退出，退出码 {ExitCode}")]
    private static partial void LogExited(ILogger logger, int exitCode);

    [LoggerMessage(EventId = 3, Level = LogLevel.Critical, Message = "应用程序启动序列发生未处理异常")]
    private static partial void LogStartupFailed(ILogger logger, Exception ex);
}
