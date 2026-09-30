using System;
using System.Net.Http;
using Iris.App.Hosting;
using Iris.App.Services;
using Iris.App.Tests.TestSupport;
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
        uiDispatcher.ShouldBeOfType<WpfUiDispatcher>();
        actionDispatcher.ShouldBeOfType<ActionDispatcher>();
        clipboardService.ShouldBeOfType<Win32ClipboardService>();
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
}
