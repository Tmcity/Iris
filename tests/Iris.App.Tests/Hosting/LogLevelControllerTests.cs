using Iris.App.Hosting;
using Iris.Core.Settings;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Serilog.Core;
using Serilog.Events;
using Shouldly;
using Xunit;

namespace Iris.App.Tests.Hosting;

public sealed class LogLevelControllerTests
{
    [Fact]
    public void ApplyCurrent_AppliesConfiguredLogLevel()
    {
        var settingsService = Substitute.For<ISettingsService>();
        var settings = new AppSettings
        {
            General = new GeneralSettings { LogLevel = "Debug" },
        };
        settingsService.Current.Returns(settings);

        var levelSwitch = new LoggingLevelSwitch(LogEventLevel.Information);
        using var controller = new LogLevelController(settingsService, levelSwitch, NullLogger<LogLevelController>.Instance);

        controller.ApplyCurrent();

        levelSwitch.MinimumLevel.ShouldBe(LogEventLevel.Debug);
    }

    [Fact]
    public void OnSettingsChanged_UpdatesLogLevel()
    {
        var settingsService = Substitute.For<ISettingsService>();
        var oldSettings = new AppSettings
        {
            General = new GeneralSettings { LogLevel = "Information" },
        };
        var newSettings = new AppSettings
        {
            General = new GeneralSettings { LogLevel = "Error" },
        };
        settingsService.Current.Returns(oldSettings);

        var levelSwitch = new LoggingLevelSwitch(LogEventLevel.Information);
        using var controller = new LogLevelController(settingsService, levelSwitch, NullLogger<LogLevelController>.Instance);

        settingsService.Changed += Raise.Event<EventHandler<SettingsChangedEventArgs>>(
            settingsService,
            new SettingsChangedEventArgs(oldSettings, newSettings));

        levelSwitch.MinimumLevel.ShouldBe(LogEventLevel.Error);
    }

    [Fact]
    public void OnSettingsChanged_InvalidLevel_PreservesCurrentLevel()
    {
        var settingsService = Substitute.For<ISettingsService>();
        var oldSettings = new AppSettings
        {
            General = new GeneralSettings { LogLevel = "Warning" },
        };
        var newSettings = new AppSettings
        {
            General = new GeneralSettings { LogLevel = "NotAValidLogLevel" },
        };
        settingsService.Current.Returns(oldSettings);

        var levelSwitch = new LoggingLevelSwitch(LogEventLevel.Warning);
        using var controller = new LogLevelController(settingsService, levelSwitch, NullLogger<LogLevelController>.Instance);

        settingsService.Changed += Raise.Event<EventHandler<SettingsChangedEventArgs>>(
            settingsService,
            new SettingsChangedEventArgs(oldSettings, newSettings));

        levelSwitch.MinimumLevel.ShouldBe(LogEventLevel.Warning);
    }
}
