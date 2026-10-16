using Iris.Core.Actions;
using Iris.Core.Storage;
using Iris.Infrastructure.Storage;
using Iris.Platform.Windows.SystemIntegration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace Iris.App.Hosting;

internal abstract class StartupRuntime
{
    internal virtual bool IsFastHook(string[] args) => false;
    internal virtual int RunFastHook(string[] args) => 2;
    internal virtual bool MaintenanceBlocked() => false;
    internal virtual void BootstrapPrimary() { }
    internal virtual Task FinishPrimaryAsync(bool successful) => Task.CompletedTask;
    internal virtual IDisposable? BeginPrimaryRun() => null;
    internal abstract IpcIdentity GetIdentity();
    internal abstract IDisposable? Acquire(IpcIdentity identity);
    internal abstract Task<IpcSendResult> ForwardAsync(IpcIdentity identity, IpcCommand command);
    internal abstract IAppPaths CreatePaths();
    internal abstract void OpenLogger(IAppPaths paths, LoggingLevelSwitch levelSwitch);
    internal abstract IHost BuildHost(IpcIdentity identity, IpcCommand? command, IAppPaths paths, LoggingLevelSwitch levelSwitch, long startTimestamp);
    internal abstract int RunApplication(IHost host);
    internal abstract void CloseLogger();
    internal abstract void LogFailure(string exceptionType);
    internal abstract void Show(string message);
}

internal class DesktopStartupRuntime : StartupRuntime, IDisposable
{
    private readonly Iris.App.Updates.UpdateHandoff _handoff = new();
    internal override bool IsFastHook(string[] args) => Iris.App.Updates.VelopackStartup.IsHookRequest(args);
    internal override int RunFastHook(string[] args) => Iris.App.Updates.VelopackStartup.RunHook(args);
    internal override bool MaintenanceBlocked() => Iris.Platform.Windows.Maintenance.MaintenanceGate.IsBlocked(Iris.Platform.Windows.Maintenance.MaintenancePaths.StateRoot);
    internal override void BootstrapPrimary() => Iris.App.Updates.VelopackStartup.RunPrimary();
    internal override Task FinishPrimaryAsync(bool successful) => _handoff.CompleteMainAsync(successful);
    public void Dispose() { _handoff.Dispose(); GC.SuppressFinalize(this); }
    internal override IDisposable BeginPrimaryRun() => Iris.App.Resources.AppLanguage.BeginRun();
    internal override IpcIdentity GetIdentity() => IpcIdentity.ForCurrentUser();
    internal override IDisposable? Acquire(IpcIdentity identity) => SingleInstanceGuard.TryAcquire(identity.MutexName);
    internal override async Task<IpcSendResult> ForwardAsync(IpcIdentity identity, IpcCommand command)
    {
        await using var client = new NamedPipeIpcClient(identity, new IpcPipeFactory(), TimeProvider.System);
        return await client.RunAsync(command).ConfigureAwait(false);
    }
    internal override void Show(string message) => FatalErrorDialog.Show(UiText.AppName, message);
    internal override IAppPaths CreatePaths() => AppPathsFactory.CreateDefault();
    internal override void OpenLogger(IAppPaths paths, LoggingLevelSwitch levelSwitch)
        => Log.Logger = IrisLogging.CreateLogger(paths, levelSwitch);
    internal override IHost BuildHost(IpcIdentity identity, IpcCommand? command, IAppPaths paths, LoggingLevelSwitch levelSwitch, long startTimestamp)
        => AppHostBuilder.Build([], paths, levelSwitch, startTimestamp, services => { services.AddSingleton(identity); services.AddSingleton(_handoff); }, command);
    internal override int RunApplication(IHost host)
    {
        return RunWpfApplication(host);
    }
    internal virtual int RunWpfApplication(IHost host)
    {
        var app = host.Services.GetRequiredService<App>();
        app.InitializeComponent();
        int result = app.Run();
        return result == 0 && !host.Services.GetRequiredService<AppBootstrapper>().CleanupCompleted ? 1 : result;
    }
    internal override void CloseLogger() => Log.CloseAndFlush();
    internal override void LogFailure(string exceptionType)
        => Log.ForContext(typeof(Program)).Fatal("Primary startup or cleanup failed: {ExceptionType}", exceptionType);
}
