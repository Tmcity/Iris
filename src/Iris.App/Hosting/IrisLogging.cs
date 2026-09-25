using System;
using System.Globalization;
using System.IO;
using System.Text;
using Iris.Core.Storage;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace Iris.App.Hosting;

internal static class IrisLogging
{
    private const string OutputTemplate = "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] ({SourceContext}) {Message:lj}{NewLine}{Exception}";

    /// <summary>创建日志器。levelSwitch 初始级别为 Information。</summary>
    public static Logger CreateLogger(IAppPaths paths, LoggingLevelSwitch levelSwitch)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(levelSwitch);

        levelSwitch.MinimumLevel = LogEventLevel.Information;

        var logFilePath = Path.Combine(paths.LogDirectory, "iris-.log");
        var utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

        var config = new LoggerConfiguration()
            .MinimumLevel.ControlledBy(levelSwitch)
            .Enrich.FromLogContext()
            .WriteTo.File(
                path: logFilePath,
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 7,
                fileSizeLimitBytes: 10 * 1024 * 1024,
                rollOnFileSizeLimit: true,
                encoding: utf8NoBom,
                outputTemplate: OutputTemplate,
                formatProvider: CultureInfo.InvariantCulture);

        return config.CreateLogger();
    }

    /// <summary>把 GeneralSettings.LogLevel 字符串（Verbose/Debug/Information/Warning/Error，不区分大小写）解析为 LogEventLevel。</summary>
    public static bool TryParseLevel(string? value, out LogEventLevel level)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            level = LogEventLevel.Information;
            return false;
        }

        switch (value.Trim().ToLowerInvariant())
        {
            case "verbose":
                level = LogEventLevel.Verbose;
                return true;
            case "debug":
                level = LogEventLevel.Debug;
                return true;
            case "information":
            case "info":
                level = LogEventLevel.Information;
                return true;
            case "warning":
            case "warn":
                level = LogEventLevel.Warning;
                return true;
            case "error":
                level = LogEventLevel.Error;
                return true;
            case "fatal":
                level = LogEventLevel.Fatal;
                return true;
            default:
                level = LogEventLevel.Information;
                return false;
        }
    }
}
