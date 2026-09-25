using System;
using System.IO;
using System.Linq;
using Iris.App.Hosting;
using Iris.App.Tests.TestSupport;
using Serilog.Core;
using Serilog.Events;
using Shouldly;
using Xunit;

namespace Iris.App.Tests.Hosting;

public sealed class IrisLoggingTests
{
    [Theory]
    [InlineData("verbose", LogEventLevel.Verbose)]
    [InlineData("VERBOSE", LogEventLevel.Verbose)]
    [InlineData("debug", LogEventLevel.Debug)]
    [InlineData("Debug", LogEventLevel.Debug)]
    [InlineData("information", LogEventLevel.Information)]
    [InlineData("Information", LogEventLevel.Information)]
    [InlineData("info", LogEventLevel.Information)]
    [InlineData("INFO", LogEventLevel.Information)]
    [InlineData("warning", LogEventLevel.Warning)]
    [InlineData("Warning", LogEventLevel.Warning)]
    [InlineData("warn", LogEventLevel.Warning)]
    [InlineData("WARN", LogEventLevel.Warning)]
    [InlineData("error", LogEventLevel.Error)]
    [InlineData("Error", LogEventLevel.Error)]
    [InlineData("fatal", LogEventLevel.Fatal)]
    [InlineData("Fatal", LogEventLevel.Fatal)]
    public void TryParseLevel_ValidLevels_ParsedCorrectly(string input, LogEventLevel expected)
    {
        var success = IrisLogging.TryParseLevel(input, out var level);
        success.ShouldBeTrue();
        level.ShouldBe(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("InvalidLevel")]
    [InlineData("unknown")]
    [InlineData("123")]
    public void TryParseLevel_InvalidOrNull_ReturnsFalse_AndDefaultsToInformation(string? input)
    {
        var success = IrisLogging.TryParseLevel(input, out var level);
        success.ShouldBeFalse();
        level.ShouldBe(LogEventLevel.Information);
    }

    [Fact]
    public void CreateLogger_DoesNotModifyPassedLevelSwitch()
    {
        using var dir = new TempDirectory();
        var paths = new FakeAppPaths(dir.Path);

        var levelSwitch = new LoggingLevelSwitch(LogEventLevel.Warning);
        using var logger = IrisLogging.CreateLogger(paths, levelSwitch);

        levelSwitch.MinimumLevel.ShouldBe(LogEventLevel.Warning);
    }

    [Fact]
    public void CreateLogger_WritesLogToFile_AndContainsMessage()
    {
        using var dir = new TempDirectory();
        var paths = new FakeAppPaths(dir.Path);

        var levelSwitch = new LoggingLevelSwitch(LogEventLevel.Information);
        using (var logger = IrisLogging.CreateLogger(paths, levelSwitch))
        {
            logger.Information("Hello Iris Test Log Message");
        }

        Directory.Exists(paths.LogDirectory).ShouldBeTrue();
        var logFiles = Directory.GetFiles(paths.LogDirectory, "iris-*.log");
        logFiles.Length.ShouldBeGreaterThan(0);

        var logContent = File.ReadAllText(logFiles.First());
        logContent.ShouldContain("Hello Iris Test Log Message");
        logContent.ShouldContain("[INF]");
    }
}
