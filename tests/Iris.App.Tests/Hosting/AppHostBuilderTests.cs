using System;
using Iris.App.Hosting;
using Iris.App.Services;
using Iris.App.Tests.TestSupport;
using Iris.Core.Presentation;
using Iris.Core.Security;
using Iris.Core.Settings;
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

        settingsService.ShouldNotBeNull();
        secretStore.ShouldNotBeNull();
        notificationService.ShouldNotBeNull();
        appLifetime.ShouldNotBeNull();
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
}
