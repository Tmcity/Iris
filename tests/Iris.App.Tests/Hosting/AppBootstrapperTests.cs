using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Iris.App.Hosting;
using Iris.App.Services;
using Iris.App.Tray;
using Iris.Core.Common;
using Iris.Core.Errors;
using Iris.Core.Presentation;
using Iris.Core.Settings;
using Iris.Core.Storage;
using Iris.Core.Translation;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Iris.App.Tests.Hosting;

public sealed class AppBootstrapperTests : IDisposable
{
    private readonly Action<string, string> _originalFatalErrorShower;
    private readonly Action<int> _originalAppShutdowner;

    public AppBootstrapperTests()
    {
        _originalFatalErrorShower = AppBootstrapper.FatalErrorShower;
        _originalAppShutdowner = AppBootstrapper.AppShutdowner;
    }

    public void Dispose()
    {
        AppBootstrapper.FatalErrorShower = _originalFatalErrorShower;
        AppBootstrapper.AppShutdowner = _originalAppShutdowner;
    }

    [Fact]
    public async Task StartAsync_WhenAllServicesSucceed_ExecutesInStrictOrder_AndInitializesTrayAndHost()
    {
        var callOrder = new List<string>();

        var settingsService = Substitute.For<ISettingsService>();
        settingsService.Current.Returns(new AppSettings());
        settingsService.LoadAsync(Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask)
            .AndDoes(_ => callOrder.Add("settings"));

        var translationRegistry = Substitute.For<ITranslationProviderRegistry>();
        translationRegistry.RefreshAsync(Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask)
            .AndDoes(_ => callOrder.Add("registry"));

        var databaseInitializer = Substitute.For<IDatabaseInitializer>();
        databaseInitializer.InitializeAsync(Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask)
            .AndDoes(_ => callOrder.Add("database"));

        var appLifetime = Substitute.For<IAppLifetime>();
        var themeService = new ThemeService(settingsService);
        var logLevelSwitch = new Serilog.Core.LoggingLevelSwitch(Serilog.Events.LogEventLevel.Information);
        var logLevelController = new LogLevelController(settingsService, logLevelSwitch, NullLogger<LogLevelController>.Instance);

        var trayController = new TrayIconController(appLifetime, settingsService, NullLogger<TrayIconController>.Instance)
        {
            SuppressIconCreationForTesting = true
        };
        var trayNotificationService = new TrayNotificationService(NullLogger<TrayNotificationService>.Instance);

        var host = Substitute.For<IHost>();
        host.StartAsync(Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask)
            .AndDoes(_ => callOrder.Add("host"));

        var bootstrapper = new AppBootstrapper(
            settingsService,
            translationRegistry,
            databaseInitializer,
            logLevelController,
            themeService,
            trayController,
            trayNotificationService,
            host,
            NullLogger<AppBootstrapper>.Instance,
            new AppStartupInfo(Environment.TickCount64));

        await bootstrapper.StartAsync();

        callOrder.ShouldBe(["settings", "registry", "database", "host"]);
        trayController.WasInitialized.ShouldBeTrue();
    }

    [Fact]
    public async Task StartAsync_WhenDatabaseInitializerFailsWithStorageFailure_ShowsFatalError_ShutsDown_AndDoesNotStartTrayOrHost()
    {
        var settingsService = Substitute.For<ISettingsService>();
        settingsService.Current.Returns(new AppSettings());
        settingsService.LoadAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);

        var translationRegistry = Substitute.For<ITranslationProviderRegistry>();
        translationRegistry.RefreshAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);

        var databaseInitializer = Substitute.For<IDatabaseInitializer>();
        databaseInitializer.InitializeAsync(Arg.Any<CancellationToken>())
            .Returns<Task>(_ => Task.FromException(new IrisException(ErrorCode.StorageFailure, "Simulated database corrupt error")));

        var appLifetime = Substitute.For<IAppLifetime>();
        var themeService = new ThemeService(settingsService);
        var logLevelSwitch = new Serilog.Core.LoggingLevelSwitch(Serilog.Events.LogEventLevel.Information);
        var logLevelController = new LogLevelController(settingsService, logLevelSwitch, NullLogger<LogLevelController>.Instance);

        var trayController = new TrayIconController(appLifetime, settingsService, NullLogger<TrayIconController>.Instance)
        {
            SuppressIconCreationForTesting = true
        };
        var trayNotificationService = new TrayNotificationService(NullLogger<TrayNotificationService>.Instance);

        var host = Substitute.For<IHost>();

        var fatalErrorShown = false;
        string? fatalErrorMessage = null;
        int? exitCode = null;

        AppBootstrapper.FatalErrorShower = (title, message) =>
        {
            fatalErrorShown = true;
            fatalErrorMessage = message;
        };

        AppBootstrapper.AppShutdowner = code =>
        {
            exitCode = code;
        };

        var bootstrapper = new AppBootstrapper(
            settingsService,
            translationRegistry,
            databaseInitializer,
            logLevelController,
            themeService,
            trayController,
            trayNotificationService,
            host,
            NullLogger<AppBootstrapper>.Instance,
            new AppStartupInfo(Environment.TickCount64));

        await bootstrapper.StartAsync();

        fatalErrorShown.ShouldBeTrue();
        fatalErrorMessage.ShouldNotBeNull();
        exitCode.ShouldBe(1);

        // 验证托盘图标与 Host 从未被启动
        trayController.WasInitialized.ShouldBeFalse();
        await host.DidNotReceive().StartAsync(Arg.Any<CancellationToken>());
    }
}
