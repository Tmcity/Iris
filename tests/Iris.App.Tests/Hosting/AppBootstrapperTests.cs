using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Iris.App.Hosting;
using Iris.App.Services;
using Iris.App.Tray;
using Iris.Core.Actions;
using Iris.Core.Common;
using Iris.Core.Errors;
using Iris.Core.Platform;
using Iris.Core.Presentation;
using Iris.Core.Settings;
using Iris.Core.Storage;
using Iris.Core.Translation;
using Iris.Core.Workflows;
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
        AppBootstrapper.AppShutdowner = _ => { };
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

        var actionDispatcher = Substitute.For<IActionDispatcher>();
        var trayController = new TrayIconController(appLifetime, settingsService, actionDispatcher, NullLogger<TrayIconController>.Instance)
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

        var actionDispatcher = Substitute.For<IActionDispatcher>();
        var trayController = new TrayIconController(appLifetime, settingsService, actionDispatcher, NullLogger<TrayIconController>.Instance)
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

    [Fact]
    public async Task StopAsync_AwaitsSettingsShutdownBeforeStoppingSynchronousParts()
    {
        var settingsService = Substitute.For<ISettingsService>();
        var translationRegistry = Substitute.For<ITranslationProviderRegistry>();
        var databaseInitializer = Substitute.For<IDatabaseInitializer>();
        var appLifetime = Substitute.For<IAppLifetime>();
        var themeService = new ThemeService(settingsService);
        var logLevelSwitch = new Serilog.Core.LoggingLevelSwitch(Serilog.Events.LogEventLevel.Information);
        var logLevelController = new LogLevelController(settingsService, logLevelSwitch, NullLogger<LogLevelController>.Instance);
        var actionDispatcher = Substitute.For<IActionDispatcher>();
        var trayController = new TrayIconController(appLifetime, settingsService, actionDispatcher, NullLogger<TrayIconController>.Instance)
        {
            SuppressIconCreationForTesting = true
        };
        var trayNotificationService = new TrayNotificationService(NullLogger<TrayNotificationService>.Instance);
        var host = Substitute.For<IHost>();

        var shutdownCoordinator = Substitute.For<Iris.App.Settings.ISettingsShutdownCoordinator>();
        var shutdownCompleted = false;

        shutdownCoordinator.ShutdownAsync(Arg.Any<CancellationToken>()).Returns(async _ =>
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);
            shutdownCompleted = true;
        });

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
            new AppStartupInfo(Environment.TickCount64),
            shutdownCoordinator);

        await bootstrapper.StopAsync();

        shutdownCompleted.ShouldBeTrue();
        await shutdownCoordinator.Received(1).ShutdownAsync(Arg.Any<CancellationToken>());
        await host.Received(1).StopAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StopAsync_WhenSettingsShutdownFails_Throws_PreservesState_AllowsRetry_AndSharesConcurrentResult()
    {
        var settingsService = Substitute.For<ISettingsService>();
        var translationRegistry = Substitute.For<ITranslationProviderRegistry>();
        var databaseInitializer = Substitute.For<IDatabaseInitializer>();
        var appLifetime = Substitute.For<IAppLifetime>();
        var themeService = new ThemeService(settingsService);
        var logLevelSwitch = new Serilog.Core.LoggingLevelSwitch(Serilog.Events.LogEventLevel.Information);
        var logLevelController = new LogLevelController(settingsService, logLevelSwitch, NullLogger<LogLevelController>.Instance);
        var actionDispatcher = Substitute.For<IActionDispatcher>();
        var trayController = new TrayIconController(appLifetime, settingsService, actionDispatcher, NullLogger<TrayIconController>.Instance)
        {
            SuppressIconCreationForTesting = true
        };
        var trayNotificationService = new TrayNotificationService(NullLogger<TrayNotificationService>.Instance);
        var host = Substitute.For<IHost>();

        var shutdownCoordinator = Substitute.For<Iris.App.Settings.ISettingsShutdownCoordinator>();
        var shouldFail = true;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();

        shutdownCoordinator.ShutdownAsync(Arg.Any<CancellationToken>()).Returns(async _ =>
        {
            await Task.Yield();
            if (shouldFail)
            {
                entered.TrySetResult();
                if (!release.Wait(TimeSpan.FromSeconds(5)))
                {
                    throw new TimeoutException("Wait timed out");
                }
                throw new InvalidOperationException("Injected settings shutdown failure");
            }
        });

        int? exitCode = null;
        AppBootstrapper.AppShutdowner = code => exitCode = code;

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
            new AppStartupInfo(Environment.TickCount64),
            shutdownCoordinator);

        // 第一次退出：并发两个 StopAsync 请求
        var firstStop = bootstrapper.StopAsync();
        var secondStop = bootstrapper.StopAsync();

        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        // 放行失败
        release.Set();

        // 两者共享同一个错误，均观察到异常
        var ex1 = await Should.ThrowAsync<InvalidOperationException>(() => firstStop.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        ex1.Message.ShouldContain("Injected settings shutdown failure");

        var ex2 = await Should.ThrowAsync<InvalidOperationException>(() => secondStop.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        ex2.Message.ShouldContain("Injected settings shutdown failure");

        // 确认由于设置清理失败：Host.StopAsync 与 AppShutdowner 未被执行
        await host.DidNotReceiveWithAnyArgs().StopAsync(Arg.Any<CancellationToken>());
        exitCode.ShouldBeNull("设置关闭失败时不得调用 AppShutdowner 退出应用");

        // 解除故障，发起第二次退出（重试）
        shouldFail = false;
        await bootstrapper.StopAsync().WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        // 验证重试后顺利完成
        await host.Received(1).StopAsync(Arg.Any<CancellationToken>());
        exitCode.ShouldBe(0);
    }

    [Fact]
    public async Task StopAsync_ConcurrentCallsWhileHostStopPending_ShareOngoingTask_AndAwaitFullExit()
    {
        var settingsService = Substitute.For<ISettingsService>();
        var translationRegistry = Substitute.For<ITranslationProviderRegistry>();
        var databaseInitializer = Substitute.For<IDatabaseInitializer>();
        var appLifetime = Substitute.For<IAppLifetime>();
        var themeService = new ThemeService(settingsService);
        var logLevelSwitch = new Serilog.Core.LoggingLevelSwitch(Serilog.Events.LogEventLevel.Information);
        var logLevelController = new LogLevelController(settingsService, logLevelSwitch, NullLogger<LogLevelController>.Instance);
        var actionDispatcher = Substitute.For<IActionDispatcher>();
        var trayController = new TrayIconController(appLifetime, settingsService, actionDispatcher, NullLogger<TrayIconController>.Instance)
        {
            SuppressIconCreationForTesting = true
        };
        var trayNotificationService = new TrayNotificationService(NullLogger<TrayNotificationService>.Instance);

        var host = Substitute.For<IHost>();
        var hostEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var hostRelease = new ManualResetEventSlim();

        host.StopAsync(Arg.Any<CancellationToken>()).Returns(async _ =>
        {
            await Task.Yield();
            hostEntered.TrySetResult();
            if (!hostRelease.Wait(TimeSpan.FromSeconds(5)))
            {
                throw new TimeoutException("Host release wait timed out");
            }
        });

        var shutdownCoordinator = Substitute.For<Iris.App.Settings.ISettingsShutdownCoordinator>();
        shutdownCoordinator.ShutdownAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);

        var exitCalls = 0;
        var originalShutdown = AppBootstrapper.AppShutdowner;
        AppBootstrapper.AppShutdowner = _ => Interlocked.Increment(ref exitCalls);

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
            new AppStartupInfo(Environment.TickCount64),
            shutdownCoordinator);

        Task? firstStop = null;
        Task? secondStop = null;

        try
        {
            // 第一次发起 StopAsync，进入 Host.StopAsync 并挂起
            firstStop = bootstrapper.StopAsync();
            await hostEntered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

            // 第二次发起 StopAsync：必须共享本轮正在进行的退出任务，不得提前完成
            secondStop = bootstrapper.StopAsync();

            firstStop.IsCompleted.ShouldBeFalse("Host.StopAsync 尚未完成时，第一次 StopAsync 必须 pending");
            secondStop.IsCompleted.ShouldBeFalse("Host.StopAsync 尚未完成时，第二次 StopAsync 必须 pending，不得提前返回");
            ReferenceEquals(firstStop, secondStop).ShouldBeTrue("并发 StopAsync 必须共享同一个任务实例");
            exitCalls.ShouldBe(0, "Host 停止完成前不得执行应用退出委托");

            // 放行 Host.StopAsync
            hostRelease.Set();

            await Task.WhenAll(firstStop, secondStop).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

            // 验证 Host.StopAsync 与应用退出委托各执行一次
            await host.Received(1).StopAsync(Arg.Any<CancellationToken>());
            exitCalls.ShouldBe(1, "完整退出结束后应用退出委托必须恰好执行一次");
        }
        finally
        {
            hostRelease.Set();
            AppBootstrapper.AppShutdowner = originalShutdown;
            if (firstStop != null)
            {
                try { await firstStop.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken); } catch { }
            }
        }
    }

    [Fact]
    public async Task StartAsync_StartsHotkeyCoordinator_InCorrectOrder()
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

        var actionDispatcher = Substitute.For<IActionDispatcher>();
        var trayController = new TrayIconController(appLifetime, settingsService, actionDispatcher, NullLogger<TrayIconController>.Instance)
        {
            SuppressIconCreationForTesting = true
        };
        var trayNotificationService = new TrayNotificationService(NullLogger<TrayNotificationService>.Instance);

        var host = Substitute.For<IHost>();
        host.StartAsync(Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask)
            .AndDoes(_ => callOrder.Add("host"));

        var hotkeyService = Substitute.For<IHotkeyService>();
        hotkeyService.Register(Arg.Any<IrisAction>(), Arg.Any<HotkeyBinding>())
            .Returns(callInfo =>
            {
                callOrder.Add("hotkeys");
                return new HotkeyRegistrationResult(callInfo.Arg<IrisAction>(), true);
            });

        var uiDispatcher = Substitute.For<IUiDispatcher>();
        uiDispatcher.InvokeAsync(Arg.Any<Func<Task>>(), Arg.Any<CancellationToken>())
            .Returns(async callInfo => await callInfo.Arg<Func<Task>>()());

        var coordinator = new HotkeyCoordinator(
            settingsService,
            hotkeyService,
            actionDispatcher,
            [new InputTranslateHandler(Substitute.For<ITranslateWindowPresenter>())],
            uiDispatcher,
            Substitute.For<INotificationService>(),
            NullLogger<HotkeyCoordinator>.Instance);

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
            new AppStartupInfo(Environment.TickCount64),
            hotkeyCoordinator: coordinator);

        await bootstrapper.StartAsync();

        callOrder.ShouldBe(["settings", "registry", "database", "hotkeys", "host"]);
    }

    [Fact]
    public async Task StopAsync_StopsHotkeyCoordinatorFirst_AndDrainsDispatchesBeforeHostStop()
    {
        var callOrder = new List<string>();

        var settingsService = Substitute.For<ISettingsService>();
        settingsService.Current.Returns(new AppSettings());

        var translationRegistry = Substitute.For<ITranslationProviderRegistry>();
        var databaseInitializer = Substitute.For<IDatabaseInitializer>();
        var appLifetime = Substitute.For<IAppLifetime>();
        var themeService = new ThemeService(settingsService);
        var logLevelSwitch = new Serilog.Core.LoggingLevelSwitch(Serilog.Events.LogEventLevel.Information);
        var logLevelController = new LogLevelController(settingsService, logLevelSwitch, NullLogger<LogLevelController>.Instance);

        var actionDispatcher = Substitute.For<IActionDispatcher>();
        var trayController = new TrayIconController(appLifetime, settingsService, actionDispatcher, NullLogger<TrayIconController>.Instance)
        {
            SuppressIconCreationForTesting = true
        };
        var trayNotificationService = new TrayNotificationService(NullLogger<TrayNotificationService>.Instance);

        var host = Substitute.For<IHost>();
        host.StopAsync(Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask)
            .AndDoes(_ => callOrder.Add("host_stop"));

        var settingsShutdown = Substitute.For<Iris.App.Settings.ISettingsShutdownCoordinator>();
        settingsShutdown.ShutdownAsync(Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask)
            .AndDoes(_ => callOrder.Add("settings_shutdown"));

        var translateShutdown = Substitute.For<Iris.App.Translation.ITranslateWindowShutdownCoordinator>();
        translateShutdown.ShutdownAsync(Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask)
            .AndDoes(_ => callOrder.Add("translate_shutdown"));


        var hotkeyService = Substitute.For<IHotkeyService>();
        hotkeyService.When(x => x.Dispose())
            .Do(_ => callOrder.Add("hotkey_native_dispose"));

        var uiDispatcher = Substitute.For<IUiDispatcher>();
        uiDispatcher.InvokeAsync(Arg.Any<Func<Task>>(), Arg.Any<CancellationToken>())
            .Returns(async callInfo => await callInfo.Arg<Func<Task>>()());

        var coordinator = new HotkeyCoordinator(
            settingsService,
            hotkeyService,
            actionDispatcher,
            [new InputTranslateHandler(Substitute.For<ITranslateWindowPresenter>())],
            uiDispatcher,
            Substitute.For<INotificationService>(),
            NullLogger<HotkeyCoordinator>.Instance);

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
            new AppStartupInfo(Environment.TickCount64),
            settingsShutdownCoordinator: settingsShutdown,
            translateShutdownCoordinator: translateShutdown,
            hotkeyCoordinator: coordinator);

        var originalShutdown = AppBootstrapper.AppShutdowner;
        try
        {
            AppBootstrapper.AppShutdowner = _ => callOrder.Add("app_shutdown");
            await bootstrapper.StopAsync();

            callOrder.ShouldBe([
                "hotkey_native_dispose",
                "settings_shutdown",
                "translate_shutdown",
                "host_stop",
                "app_shutdown"
            ]);
        }
        finally
        {
            AppBootstrapper.AppShutdowner = originalShutdown;
        }
    }
}

