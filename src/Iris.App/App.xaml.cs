using System;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using Iris.App.Hosting;
using Iris.Core.Errors;
using Iris.Core.Storage;
using Iris.Infrastructure.Storage;
using Serilog;
using Serilog.Core;

namespace Iris.App;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    private LoggingLevelSwitch? _levelSwitch;

    protected override void OnStartup(StartupEventArgs e)
    {
        IAppPaths paths;
        try
        {
            paths = AppPathsFactory.CreateDefault();
        }
        catch (IrisException ex)
        {
            MessageBox.Show($"{ex.Message}\n{ex.Detail}", "Iris", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        _levelSwitch = new LoggingLevelSwitch();
        Log.Logger = IrisLogging.CreateLogger(paths, _levelSwitch);

        var version = Assembly.GetEntryAssembly()?
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion
            ?? Assembly.GetEntryAssembly()?.GetName().Version?.ToString()
            ?? "Unknown";

        Log.Information(
            "Iris starting up. Version: {Version}, DataDirectory: {DataDirectory}, IsPortable: {IsPortable}, OS: {OSVersion}, Runtime: {FrameworkDescription}",
            version,
            paths.DataDirectory,
            paths.IsPortable,
            Environment.OSVersion.ToString(),
            RuntimeInformation.FrameworkDescription);

        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Log.Information("Iris exiting with code {ExitCode}", e.ApplicationExitCode);
        Log.CloseAndFlush();
        base.OnExit(e);
    }
}
