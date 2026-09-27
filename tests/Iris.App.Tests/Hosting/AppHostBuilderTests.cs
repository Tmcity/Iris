using System;
using System.Net.Http;
using Iris.App.Hosting;
using Iris.App.Services;
using Iris.App.Tests.TestSupport;
using Iris.Core.Common;
using Iris.Core.Languages;
using Iris.Core.Presentation;
using Iris.Core.Security;
using Iris.Core.Settings;
using Iris.Core.Translation;
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

        settingsService.ShouldNotBeNull();
        secretStore.ShouldNotBeNull();
        notificationService.ShouldNotBeNull();
        appLifetime.ShouldNotBeNull();
        targetLanguageResolver.ShouldNotBeNull();
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
