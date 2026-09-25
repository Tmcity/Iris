using System;
using Iris.Core.Settings;
using Microsoft.Extensions.Logging;
using Serilog.Core;
using Serilog.Events;

namespace Iris.App.Hosting;

/// <summary>
/// 控制日志输出级别，同步应用设置并动态响应设置变更。
/// </summary>
internal sealed partial class LogLevelController : IDisposable
{
    private readonly ISettingsService _settingsService;
    private readonly LoggingLevelSwitch _levelSwitch;
    private readonly ILogger<LogLevelController> _logger;
    private bool _disposed;

    public LogLevelController(
        ISettingsService settingsService,
        LoggingLevelSwitch levelSwitch,
        ILogger<LogLevelController> logger)
    {
        _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
        _levelSwitch = levelSwitch ?? throw new ArgumentNullException(nameof(levelSwitch));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        _settingsService.Changed += OnSettingsChanged;
    }

    public void ApplyCurrent()
    {
        UpdateLevel(_settingsService.Current.General.LogLevel);
    }

    private void OnSettingsChanged(object? sender, SettingsChangedEventArgs e)
    {
        if (!string.Equals(e.OldSettings.General.LogLevel, e.NewSettings.General.LogLevel, StringComparison.OrdinalIgnoreCase))
        {
            UpdateLevel(e.NewSettings.General.LogLevel);
        }
    }

    private void UpdateLevel(string? rawLevel)
    {
        if (IrisLogging.TryParseLevel(rawLevel, out var level))
        {
            _levelSwitch.MinimumLevel = level;
        }
        else
        {
            LogInvalidLogLevel(_logger, rawLevel, _levelSwitch.MinimumLevel);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _settingsService.Changed -= OnSettingsChanged;
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "无法识别的日志级别配置 '{RawLevel}'，保持当前日志级别 {CurrentLevel}")]
    private static partial void LogInvalidLogLevel(ILogger logger, string? rawLevel, LogEventLevel currentLevel);
}
