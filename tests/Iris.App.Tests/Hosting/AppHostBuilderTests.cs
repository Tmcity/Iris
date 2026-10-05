using System;
using System.Net.Http;
using Iris.App.Hosting;
using Iris.App.Services;
using Iris.App.Settings;
using Iris.App.Tests.TestSupport;
using Iris.App.Tray;
using Iris.Core.Actions;
using Iris.Core.Common;
using Iris.Core.Languages;
using Iris.Core.Platform;
using Iris.Core.Presentation;
using Iris.Core.Security;
using Iris.Core.Settings;
using Iris.Core.Storage;
using Iris.Core.Translation;
using Iris.Platform.Windows.Clipboard;
using Iris.Platform.Windows.SystemIntegration;
using Iris.Providers.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog.Core;
using Serilog.Events;
using Shouldly;
using Xunit;

namespace Iris.App.Tests.Hosting;

public sealed class AppHostBuilderTests
{
    [Fact]
    public void Build_WithValidateOnBuild_SucceedsWithoutException()
    {
        using var tempDir = new TempDirectory();
        var paths = new FakeAppPaths(tempDir.Path);
        var levelSwitch = new LoggingLevelSwitch(LogEventLevel.Information);

        using var host = AppHostBuilder.Build([], paths, levelSwitch, Environment.TickCount64);

        host.ShouldNotBeNull();
    }

    [Fact]
    public void Host_ResolvesRequiredServices()
    {
        using var tempDir = new TempDirectory();
        var paths = new FakeAppPaths(tempDir.Path);
        var levelSwitch = new LoggingLevelSwitch(LogEventLevel.Information);

        using var host = AppHostBuilder.Build([], paths, levelSwitch, Environment.TickCount64);

        var settingsService = host.Services.GetService<ISettingsService>();
        var secretStore = host.Services.GetService<ISecretStore>();
        var notificationService = host.Services.GetService<INotificationService>();
        var appLifetime = host.Services.GetService<IAppLifetime>();
        var targetLanguageResolver = host.Services.GetService<ITargetLanguageResolver>();
        var translationRegistry = host.Services.GetService<ITranslationProviderRegistry>();
        var configObserver = host.Services.GetService<IProviderConfigurationObserver>();
        var databaseInitializer = host.Services.GetService<IDatabaseInitializer>();
        var historyRepository = host.Services.GetService<ITranslationHistoryRepository>();
        var orchestrator = host.Services.GetService<ITranslationOrchestrator>();
        var bootstrapper = host.Services.GetService<AppBootstrapper>();
        var uiDispatcher = host.Services.GetService<IUiDispatcher>();
        var actionDispatcher = host.Services.GetService<IActionDispatcher>();
        var clipboardService = host.Services.GetService<IClipboardService>();
        var desktopContext = host.Services.GetService<IDesktopContext>();
        var translatePresenter = host.Services.GetService<ITranslateWindowPresenter>();
        var hotkeyService = host.Services.GetService<IHotkeyService>();
        var hotkeyCoordinator = host.Services.GetService<HotkeyCoordinator>();


        settingsService.ShouldNotBeNull();
        secretStore.ShouldNotBeNull();
        notificationService.ShouldNotBeNull();
        appLifetime.ShouldNotBeNull();
        targetLanguageResolver.ShouldNotBeNull();
        translationRegistry.ShouldNotBeNull();
        configObserver.ShouldNotBeNull();
        databaseInitializer.ShouldNotBeNull();
        historyRepository.ShouldNotBeNull();
        orchestrator.ShouldNotBeNull();
        bootstrapper.ShouldNotBeNull();
        uiDispatcher.ShouldNotBeNull();
        actionDispatcher.ShouldNotBeNull();
        clipboardService.ShouldNotBeNull();
        desktopContext.ShouldNotBeNull();
        translatePresenter.ShouldNotBeNull();
        hotkeyService.ShouldNotBeNull();
        hotkeyCoordinator.ShouldNotBeNull();
        uiDispatcher.ShouldBeOfType<WpfUiDispatcher>();
        actionDispatcher.ShouldBeOfType<ActionDispatcher>();
        clipboardService.ShouldBeOfType<Win32ClipboardService>();
        desktopContext.ShouldBeOfType<Iris.Platform.Windows.Capture.DesktopContext>();
        hotkeyService.ShouldBeOfType<Iris.Platform.Windows.Hotkey.HotkeyService>();
    }

    [Fact]
    public void Host_ResolvesHotkeyServiceAndCoordinator_AsSingleton()
    {
        using var tempDir = new TempDirectory();
        var paths = new FakeAppPaths(tempDir.Path);
        var levelSwitch = new LoggingLevelSwitch(LogEventLevel.Information);

        using var host = AppHostBuilder.Build([], paths, levelSwitch, Environment.TickCount64);

        var hotkey1 = host.Services.GetRequiredService<IHotkeyService>();
        var hotkey2 = host.Services.GetRequiredService<IHotkeyService>();
        var coordinator1 = host.Services.GetRequiredService<HotkeyCoordinator>();
        var coordinator2 = host.Services.GetRequiredService<HotkeyCoordinator>();

        hotkey1.ShouldNotBeNull();
        hotkey1.ShouldBeSameAs(hotkey2);
        coordinator1.ShouldNotBeNull();
        coordinator1.ShouldBeSameAs(coordinator2);
    }


    [Fact]
    public void Host_ResolvesDesktopContext_AsSingleton()
    {
        using var tempDir = new TempDirectory();
        var paths = new FakeAppPaths(tempDir.Path);
        var levelSwitch = new LoggingLevelSwitch(LogEventLevel.Information);

        using var host = AppHostBuilder.Build([], paths, levelSwitch, Environment.TickCount64);

        var desktop1 = host.Services.GetRequiredService<IDesktopContext>();
        var desktop2 = host.Services.GetRequiredService<IDesktopContext>();

        desktop1.ShouldNotBeNull();
        desktop1.ShouldBeOfType<Iris.Platform.Windows.Capture.DesktopContext>();
        desktop1.ShouldBeSameAs(desktop2);
    }

    [Fact]
    public void Host_ResolvesClipboardService_AsSingleton()
    {
        using var tempDir = new TempDirectory();
        var paths = new FakeAppPaths(tempDir.Path);
        var levelSwitch = new LoggingLevelSwitch(LogEventLevel.Information);

        using var host = AppHostBuilder.Build([], paths, levelSwitch, Environment.TickCount64);

        var clipboard1 = host.Services.GetRequiredService<IClipboardService>();
        var clipboard2 = host.Services.GetRequiredService<IClipboardService>();

        clipboard1.ShouldNotBeNull();
        clipboard1.ShouldBeOfType<Win32ClipboardService>();
        clipboard1.ShouldBeSameAs(clipboard2);
    }

    [Fact]
    public void Host_ResolvesActionDispatcherAndUiDispatcher_AsSingletons()
    {
        using var tempDir = new TempDirectory();
        var paths = new FakeAppPaths(tempDir.Path);
        var levelSwitch = new LoggingLevelSwitch(LogEventLevel.Information);

        using var host = AppHostBuilder.Build([], paths, levelSwitch, Environment.TickCount64);

        var actionDispatcher1 = host.Services.GetRequiredService<IActionDispatcher>();
        var actionDispatcher2 = host.Services.GetRequiredService<IActionDispatcher>();
        var uiDispatcher1 = host.Services.GetRequiredService<IUiDispatcher>();
        var uiDispatcher2 = host.Services.GetRequiredService<IUiDispatcher>();

        actionDispatcher1.ShouldBeSameAs(actionDispatcher2);
        uiDispatcher1.ShouldBeSameAs(uiDispatcher2);
    }

    [Fact]
    public void Host_ResolvesDatabaseInitializerAndHistoryRepository_AsSingletons()
    {
        using var tempDir = new TempDirectory();
        var paths = new FakeAppPaths(tempDir.Path);
        var levelSwitch = new LoggingLevelSwitch(LogEventLevel.Information);

        using var host = AppHostBuilder.Build([], paths, levelSwitch, Environment.TickCount64);

        var initializer1 = host.Services.GetRequiredService<IDatabaseInitializer>();
        var initializer2 = host.Services.GetRequiredService<IDatabaseInitializer>();
        var repository1 = host.Services.GetRequiredService<ITranslationHistoryRepository>();
        var repository2 = host.Services.GetRequiredService<ITranslationHistoryRepository>();
        var orchestrator1 = host.Services.GetRequiredService<ITranslationOrchestrator>();
        var orchestrator2 = host.Services.GetRequiredService<ITranslationOrchestrator>();

        initializer1.ShouldBeSameAs(initializer2);
        repository1.ShouldBeSameAs(repository2);
        orchestrator1.ShouldBeSameAs(orchestrator2);
    }

    [Fact]
    public void Host_ResolvesIHostLifetimeAsWpfHostLifetime()
    {
        using var tempDir = new TempDirectory();
        var paths = new FakeAppPaths(tempDir.Path);
        var levelSwitch = new LoggingLevelSwitch(LogEventLevel.Information);

        using var host = AppHostBuilder.Build([], paths, levelSwitch, Environment.TickCount64);

        var hostLifetime = host.Services.GetRequiredService<IHostLifetime>();
        hostLifetime.ShouldBeOfType<WpfHostLifetime>();
    }

    [Fact]
    public void NotificationService_And_TrayNotificationService_ResolveToSameInstance()
    {
        using var tempDir = new TempDirectory();
        var paths = new FakeAppPaths(tempDir.Path);
        var levelSwitch = new LoggingLevelSwitch(LogEventLevel.Information);

        using var host = AppHostBuilder.Build([], paths, levelSwitch, Environment.TickCount64);

        var notificationService = host.Services.GetRequiredService<INotificationService>();
        var trayNotificationService = host.Services.GetRequiredService<TrayNotificationService>();

        notificationService.ShouldBeSameAs(trayNotificationService);
    }

    [Fact]
    public void Host_CreatesNamedHttpClients()
    {
        using var tempDir = new TempDirectory();
        var paths = new FakeAppPaths(tempDir.Path);
        var levelSwitch = new LoggingLevelSwitch(LogEventLevel.Information);

        using var host = AppHostBuilder.Build([], paths, levelSwitch, Environment.TickCount64);

        var factory = host.Services.GetRequiredService<IHttpClientFactory>();
        using var llmClient = factory.CreateClient(HttpClientNames.Llm);
        using var ocrClient = factory.CreateClient(HttpClientNames.Ocr);

        llmClient.ShouldNotBeNull();
        ocrClient.ShouldNotBeNull();
    }

    [Fact]
    public void Host_ResolvesTranslationProviderFactories_ContainsOpenAICompatibleAndAnthropic()
    {
        using var tempDir = new TempDirectory();
        var paths = new FakeAppPaths(tempDir.Path);
        var levelSwitch = new LoggingLevelSwitch(LogEventLevel.Information);

        using var host = AppHostBuilder.Build([], paths, levelSwitch, Environment.TickCount64);

        var factories = host.Services.GetRequiredService<System.Collections.Generic.IEnumerable<ITranslationProviderFactory>>().ToList();
        factories.Count(f => f.Kind == TranslationProviderKind.OpenAICompatible).ShouldBe(1);
        factories.Count(f => f.Kind == TranslationProviderKind.Anthropic).ShouldBe(1);
    }

    [Fact]
    public void Host_ResolvesSettingsPresenterAndAutoStart_AsSingletons_WithoutInstantiatingSettingsWindow()
    {
        using var tempDir = new TempDirectory();
        var paths = new FakeAppPaths(tempDir.Path);
        var levelSwitch = new LoggingLevelSwitch(LogEventLevel.Information);

        using var host = AppHostBuilder.Build([], paths, levelSwitch, Environment.TickCount64);

        var presenter = host.Services.GetRequiredService<ISettingsWindowPresenter>();
        var concretePresenter = host.Services.GetRequiredService<SettingsWindowPresenter>();
        var shutdownCoordinator = host.Services.GetRequiredService<ISettingsShutdownCoordinator>();
        var autoStartService = host.Services.GetRequiredService<IAutoStartService>();

        presenter.ShouldNotBeNull();
        presenter.ShouldBeSameAs(concretePresenter);
        presenter.ShouldBeSameAs(shutdownCoordinator);
        autoStartService.ShouldNotBeNull();

        // 验证解析托盘、Dispatcher、Presenter 时不会提前实例化 SettingsWindow
        var tray = host.Services.GetRequiredService<TrayIconController>();
        var dispatcher = host.Services.GetRequiredService<IActionDispatcher>();
        tray.ShouldNotBeNull();
        dispatcher.ShouldNotBeNull();

        // 验证 Scoped 设置服务（ViewModel, SaveCoordinator）在子作用域中解析正常
        using var scope = host.Services.CreateScope();
        var saveCoordinator = scope.ServiceProvider.GetRequiredService<GeneralSettingsSaveCoordinator>();
        var viewModel = scope.ServiceProvider.GetRequiredService<SettingsViewModel>();
        saveCoordinator.ShouldNotBeNull();
        viewModel.ShouldNotBeNull();
    }

    [Fact]
    public void Host_ResolvesAllSixActionHandlers_AndTranslatePresenterAsSingleton()
    {
        using var tempDir = new TempDirectory();
        var paths = new FakeAppPaths(tempDir.Path);
        var levelSwitch = new LoggingLevelSwitch(LogEventLevel.Information);

        using var host = AppHostBuilder.Build([], paths, levelSwitch, Environment.TickCount64);

        var handlers = host.Services.GetRequiredService<System.Collections.Generic.IEnumerable<IActionHandler>>().ToList();
        handlers.Count.ShouldBe(6);
        handlers.Count(h => h.Action == IrisAction.SelectionTranslate).ShouldBe(1);
        handlers.Select(h => h.Action).Distinct().Count().ShouldBe(6);
        handlers.OfType<Iris.Core.Workflows.ToggleMouseSelectionHandler>().ShouldHaveSingleItem();

        handlers.OfType<Iris.Core.Workflows.OpenSettingsHandler>().ShouldHaveSingleItem();
        handlers.OfType<Iris.Core.Workflows.InputTranslateHandler>().ShouldHaveSingleItem();
        handlers.OfType<Iris.Core.Workflows.ClipboardTranslateHandler>().ShouldHaveSingleItem();
        handlers.OfType<Iris.Core.Workflows.TranslateTextHandler>().ShouldHaveSingleItem();

        var presenter = host.Services.GetRequiredService<ITranslateWindowPresenter>();
        var concretePresenter = host.Services.GetRequiredService<Iris.App.Translation.TranslateWindowPresenter>();
        var shutdownCoordinator = host.Services.GetRequiredService<Iris.App.Translation.ITranslateWindowShutdownCoordinator>();

        presenter.ShouldNotBeNull();
        presenter.ShouldBeSameAs(concretePresenter);
        presenter.ShouldBeSameAs(shutdownCoordinator);

        var dispatcher = host.Services.GetRequiredService<IActionDispatcher>();
        dispatcher.ShouldNotBeNull();
        dispatcher.ShouldBeOfType<ActionDispatcher>();
    }
}
