using System.Globalization;
using Iris.App;
using Iris.App.Hosting;
using Iris.App.Resources;
using Iris.Core.Actions;
using Iris.Core.Storage;
using Iris.Platform.Windows.SystemIntegration;
using Microsoft.Extensions.Hosting;
using Serilog.Core;

internal static class PrimaryLifetimeProbe
{
    internal static void Verify(string fault)
    {
        var previous = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = new CultureInfo("zh-CN");
            Require(AppLanguage.Name == "zh-CN", "Earlier scenario leaked a selected language");
            var runtime = new Runtime(fault);
            // The only language scope is the actual production Program/DesktopStartupRuntime scope.
            int result = Iris.App.Program.Run([], runtime);
            Require(result == 1, "Cleanup failure did not reach the primary exit code");
            Require(runtime.Messages.SequenceEqual(["Iris startup or cleanup failed. Please retry later."]),
                "Primary cleanup feedback did not retain the selected English language");
            Require(runtime.CleanupOrder.SequenceEqual(["host", "logger", "mutex"]), "Primary cleanup order or ownership changed");
            Require(runtime.ObservedLanguages.All(value => value == "en-US"), "A final cleanup or feedback escaped the primary language scope");
            Require(AppLanguage.Name == "zh-CN" && UiText.SettingsTitle == "设置", "Primary scope did not restore culture isolation after returning");
        }
        finally { CultureInfo.CurrentUICulture = previous; }
    }

    private static void Require(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }

    private sealed class Runtime(string fault) : DesktopStartupRuntime
    {
        internal readonly List<string> Messages = [], CleanupOrder = [], ObservedLanguages = [];
        internal override IpcIdentity GetIdentity() => new("owned-lifetime-no-pipe", "owned-lifetime-no-mutex");
        internal override IDisposable Acquire(IpcIdentity identity) => new Guard(this);
        internal override Task<IpcSendResult> ForwardAsync(IpcIdentity identity, IpcCommand command)
            => throw new InvalidOperationException("Primary lifetime probe must not forward IPC");
        internal override IAppPaths CreatePaths() => new Paths();
        internal override void OpenLogger(IAppPaths paths, LoggingLevelSwitch levelSwitch) { }
        internal override IHost BuildHost(IpcIdentity identity, IpcCommand? command, IAppPaths paths, LoggingLevelSwitch levelSwitch, long startTimestamp)
            => new Host(this);
        internal override int RunWpfApplication(IHost host)
        {
            AppLanguage.Select("en-US");
            return 0;
        }
        internal override void CloseLogger() => Cleanup("logger");
        internal override void LogFailure(string exceptionType) { }
        internal override void Show(string message)
        {
            ObservedLanguages.Add(AppLanguage.Name);
            Messages.Add(message);
        }
        private void Cleanup(string name)
        {
            CleanupOrder.Add(name);
            ObservedLanguages.Add(AppLanguage.Name);
            if (name == fault) throw new IOException("Synthetic owned cleanup failure");
        }
        private sealed class Guard(Runtime owner) : IDisposable
        {
            public void Dispose() => owner.Cleanup("mutex");
        }
        private sealed class Host(Runtime owner) : IHost
        {
            public IServiceProvider Services => throw new InvalidOperationException("Synthetic host has no service operations");
            public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
            public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
            public void Dispose() => owner.Cleanup("host");
        }
    }
}
