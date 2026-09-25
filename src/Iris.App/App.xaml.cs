using System;
using System.Windows;
using Iris.App.Hosting;
using Iris.Platform.Windows.SystemIntegration;
using Serilog;

namespace Iris.App;

internal partial class App : Application
{
    private readonly AppBootstrapper _bootstrapper;
    private readonly GlobalExceptionHandler _exceptionHandler;

    public App(AppBootstrapper bootstrapper, GlobalExceptionHandler exceptionHandler)
    {
        _bootstrapper = bootstrapper ?? throw new ArgumentNullException(nameof(bootstrapper));
        _exceptionHandler = exceptionHandler ?? throw new ArgumentNullException(nameof(exceptionHandler));

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
            Log.ForContext<App>().Fatal(ex, "应用程序启动序列发生未处理异常");
            FatalErrorDialog.Show(UiText.AppName, $"{UiText.FatalUnknown}\n{ex.Message}");
            Shutdown(1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _bootstrapper.StopSynchronousParts();
        _exceptionHandler.Dispose();
        base.OnExit(e);
    }
}
