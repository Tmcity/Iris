using System.Globalization;
using Iris.App.Hosting;
using Iris.App.Resources;
using Iris.Core.Actions;
using Iris.Core.Storage;
using Iris.Platform.Windows.SystemIntegration;
using Microsoft.Extensions.Hosting;
using NSubstitute;
using Serilog.Core;
using Shouldly;

namespace Iris.App.Tests.Localization;

[Collection("Localization")]
public sealed class PrimaryLanguageLifetimeTests
{
    [Theory][InlineData("host")][InlineData("logger")][InlineData("mutex")]
    public void ProductionRunScopeKeepsEnglishThroughCleanupFeedbackOnChineseSystem(string failure)
    {
        var previous = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = new("zh-CN");
            var runtime = new Runtime(failure);
            // No test-owned language scope: use the production Program and DesktopStartupRuntime scope.
            Program.Run([], runtime).ShouldBe(1);
            runtime.Messages.ShouldBe(["Iris startup or cleanup failed. Please retry later."]);
            runtime.CleanupLanguages.ShouldAllBe(language => language == "en-US");
            runtime.MutexReleased.ShouldBeTrue();
        }
        finally { CultureInfo.CurrentUICulture = previous; }
    }
    private sealed class Runtime(string failure) : DesktopStartupRuntime
    {
        internal readonly List<string> Messages = [], CleanupLanguages = [];
        internal bool MutexReleased;
        internal override IpcIdentity GetIdentity() => new("test-unused-pipe", "test-unused-mutex");
        internal override IDisposable Acquire(IpcIdentity identity) => new Guard(this, failure);
        internal override IAppPaths CreatePaths() => Substitute.For<IAppPaths>();
        internal override void OpenLogger(IAppPaths paths, LoggingLevelSwitch levelSwitch) { }
        internal override IHost BuildHost(IpcIdentity identity, IpcCommand? command, IAppPaths paths, LoggingLevelSwitch levelSwitch, long startTimestamp)
        {
            var host = Substitute.For<IHost>();
            host.When(value => value.Dispose()).Do(_ =>
            {
                CleanupLanguages.Add(AppLanguage.Name);
                if (failure == "host") throw new IOException("synthetic-cleanup");
            });
            return host;
        }
        internal override int RunWpfApplication(IHost host) { AppLanguage.Select("en-US"); return 0; }
        internal override void CloseLogger()
        {
            CleanupLanguages.Add(AppLanguage.Name);
            if (failure == "logger") throw new IOException("synthetic-logger");
        }
        internal override void LogFailure(string exceptionType) { }
        internal override void Show(string message) => Messages.Add(message);
        private sealed class Guard(Runtime owner, string failure) : IDisposable
        {
            public void Dispose()
            {
                owner.MutexReleased = true; owner.CleanupLanguages.Add(AppLanguage.Name);
                if (failure == "mutex") throw new IOException("synthetic-mutex");
            }
        }
    }
}
