using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Iris.Core.Platform;
using Iris.Core.Settings;
using Microsoft.Extensions.Logging;

namespace Iris.App.Services;

internal enum GeneralSaveStatus
{
    /// <summary>校验、冲突、写盘失败或提交前取消；草稿保留。</summary>
    Uncommitted,

    /// <summary>配置已持久化或本来已相同，开机启动等必要应用已完成。</summary>
    SavedAndApplied,

    /// <summary>配置已持久化，但开机启动注册等后续系统动作失败；保留已保存事实，允许重试应用。</summary>
    SavedApplicationPending,
}

internal sealed record GeneralSaveResult(
    GeneralSaveStatus Status,
    string? FailureReason = null,
    bool IsConflict = false)
{
    public static GeneralSaveResult Uncommitted(string reason, bool isConflict = false) =>
        new(GeneralSaveStatus.Uncommitted, reason, isConflict);

    public static GeneralSaveResult SavedAndApplied() =>
        new(GeneralSaveStatus.SavedAndApplied);

    public static GeneralSaveResult SavedApplicationPending(string reason) =>
        new(GeneralSaveStatus.SavedApplicationPending, reason);
}

internal sealed record GeneralDraftSnapshot(
    AppTheme BaseTheme,
    AppTheme TargetTheme,
    bool ThemeDirty,

    bool BaseLaunchAtStartup,
    bool TargetLaunchAtStartup,
    bool LaunchAtStartupDirty,

    bool BaseShowStartupNotification,
    bool TargetShowStartupNotification,
    bool ShowStartupNotificationDirty,

    string BaseLogLevel,
    string TargetLogLevel,
    bool LogLevelDirty)
{
    public bool HasAnyDirty => ThemeDirty || LaunchAtStartupDirty || ShowStartupNotificationDirty || LogLevelDirty;
}

internal sealed class GeneralSettingsConflictException : Exception
{
    public string FieldName { get; }

    public GeneralSettingsConflictException(string fieldName)
        : base($"设置字段 '{fieldName}' 与当前设置发生冲突")
    {
        FieldName = fieldName;
    }
}

/// <summary>
/// 常规设置保存协调器：在 ISettingsService.UpdateAsync 的 mutate 中执行冲突检查与增量合并，
/// 并在提交成功后串行协调开机自启等系统级应用。
/// 完整规则见 SETTINGS_EDITING.md §3 与 §4。
/// </summary>
internal sealed partial class GeneralSettingsSaveCoordinator : IDisposable
{
    private static readonly string[] AllowedLogLevels = ["Verbose", "Debug", "Information", "Warning", "Error"];

    private readonly ISettingsService _settingsService;
    private readonly IAutoStartService _autoStartService;
    private readonly ILogger<GeneralSettingsSaveCoordinator> _logger;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Lock _workLock = new();
    private volatile Task? _activeExecutionTask;
    private volatile CancellationTokenSource? _activePreCommitCts;
    private volatile bool _isStopped;

    public GeneralSettingsSaveCoordinator(
        ISettingsService settingsService,
        IAutoStartService autoStartService,
        ILogger<GeneralSettingsSaveCoordinator> logger)
    {
        _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
        _autoStartService = autoStartService ?? throw new ArgumentNullException(nameof(autoStartService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    internal Task? ActiveWorkTask => _activeExecutionTask;

    public bool IsAutoStartPending()
    {
        try
        {
            return _autoStartService.IsEnabled() != _settingsService.Current.General.LaunchAtStartup;
        }
        catch
        {
            return true;
        }
    }

    internal void CancelPreCommitWork()
    {
        _isStopped = true;
        try
        {
            _activePreCommitCts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    internal void StopAcceptanceAndCancelPreCommit()
    {
        _isStopped = true;
        try
        {
            _activePreCommitCts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    internal async Task WaitForActiveWorkAsync()
    {
        StopAcceptanceAndCancelPreCommit();
        while (true)
        {
            var work = _activeExecutionTask;
            if (work == null)
            {
                break;
            }
            try
            {
                await work.ConfigureAwait(false);
            }
            catch
            {
            }
        }

        await _gate.WaitAsync().ConfigureAwait(false);
        _gate.Release();
    }

    public async Task<GeneralSaveResult> SaveAsync(GeneralDraftSnapshot snapshot, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        if (_isStopped)
        {
            return GeneralSaveResult.Uncommitted(UiText.SettingsStatusStopped);
        }

        // 1. 前置预校验：不依赖 Normalize 静默替换
        if (snapshot.ThemeDirty && !Enum.IsDefined(snapshot.TargetTheme))
        {
            return GeneralSaveResult.Uncommitted(UiText.SettingsStatusInvalidTheme);
        }

        if (snapshot.LogLevelDirty && !AllowedLogLevels.Contains(snapshot.TargetLogLevel, StringComparer.OrdinalIgnoreCase))
        {
            return GeneralSaveResult.Uncommitted(UiText.SettingsStatusInvalidLogLevel);
        }

        // 2. 无 dirty 字段时不写 JSON 或注册表
        if (!snapshot.HasAnyDirty)
        {
            return GeneralSaveResult.SavedAndApplied();
        }

        cancellationToken.ThrowIfCancellationRequested();

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        if (_isStopped)
        {
            _gate.Release();
            return GeneralSaveResult.Uncommitted(UiText.SettingsStatusStopped);
        }

        var executionTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _activePreCommitCts = linkedCts;
        _activeExecutionTask = executionTcs.Task;

        try
        {
            // 3. 执行设置持久化（UpdateAsync）
            try
            {
                await _settingsService.UpdateAsync(current =>
                {
                    // 在 mutate 内检查冲突
                    if (snapshot.ThemeDirty)
                    {
                        if (current.General.Theme != snapshot.BaseTheme && current.General.Theme != snapshot.TargetTheme)
                        {
                            throw new GeneralSettingsConflictException(nameof(GeneralSettings.Theme));
                        }
                    }

                    if (snapshot.LaunchAtStartupDirty)
                    {
                        if (current.General.LaunchAtStartup != snapshot.BaseLaunchAtStartup && current.General.LaunchAtStartup != snapshot.TargetLaunchAtStartup)
                        {
                            throw new GeneralSettingsConflictException(nameof(GeneralSettings.LaunchAtStartup));
                        }
                    }

                    if (snapshot.ShowStartupNotificationDirty)
                    {
                        if (current.General.ShowStartupNotification != snapshot.BaseShowStartupNotification && current.General.ShowStartupNotification != snapshot.TargetShowStartupNotification)
                        {
                            throw new GeneralSettingsConflictException(nameof(GeneralSettings.ShowStartupNotification));
                        }
                    }

                    if (snapshot.LogLevelDirty)
                    {
                        if (!string.Equals(current.General.LogLevel, snapshot.BaseLogLevel, StringComparison.OrdinalIgnoreCase) &&
                            !string.Equals(current.General.LogLevel, snapshot.TargetLogLevel, StringComparison.OrdinalIgnoreCase))
                        {
                            throw new GeneralSettingsConflictException(nameof(GeneralSettings.LogLevel));
                        }
                    }

                    // 检查是否已与目标完全一致
                    var themeSame = !snapshot.ThemeDirty || current.General.Theme == snapshot.TargetTheme;
                    var launchSame = !snapshot.LaunchAtStartupDirty || current.General.LaunchAtStartup == snapshot.TargetLaunchAtStartup;
                    var notifSame = !snapshot.ShowStartupNotificationDirty || current.General.ShowStartupNotification == snapshot.TargetShowStartupNotification;
                    var logSame = !snapshot.LogLevelDirty || string.Equals(current.General.LogLevel, snapshot.TargetLogLevel, StringComparison.OrdinalIgnoreCase);

                    if (themeSame && launchSame && notifSame && logSame)
                    {
                        return current;
                    }

                    // 仅在 current.General 上增量构造新值，保留所有未编辑及隐藏字段
                    var updatedGeneral = current.General with
                    {
                        Theme = snapshot.ThemeDirty ? snapshot.TargetTheme : current.General.Theme,
                        LaunchAtStartup = snapshot.LaunchAtStartupDirty ? snapshot.TargetLaunchAtStartup : current.General.LaunchAtStartup,
                        ShowStartupNotification = snapshot.ShowStartupNotificationDirty ? snapshot.TargetShowStartupNotification : current.General.ShowStartupNotification,
                        LogLevel = snapshot.LogLevelDirty ? snapshot.TargetLogLevel : current.General.LogLevel,
                    };

                    return current with { General = updatedGeneral };
                }, linkedCts.Token).ConfigureAwait(false);
            }
            catch (GeneralSettingsConflictException ex)
            {
                LogConflictDetected(_logger, ex.FieldName);
                return GeneralSaveResult.Uncommitted(UiText.SettingsStatusConflict, isConflict: true);
            }
            catch (OperationCanceledException)
            {
                return GeneralSaveResult.Uncommitted(UiText.SettingsStatusCanceled);
            }
            catch (Exception ex)
            {
                LogSaveSettingsFailed(_logger, ex.GetType().Name);
                return GeneralSaveResult.Uncommitted(UiText.SettingsStatusFileError);
            }

            // 4. 提交边界已越过：JSON 已提交成功。提交后不受调用方取消影响。
            _activePreCommitCts = null;

            // 5. 仅本次明确编辑的 LaunchAtStartup 触发开机启动注册
            if (snapshot.LaunchAtStartupDirty)
            {
                var targetLaunchAtStartup = snapshot.TargetLaunchAtStartup;
                try
                {
                    await Task.Run(() => _autoStartService.SetEnabled(targetLaunchAtStartup), CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    LogAutoStartApplicationFailed(_logger, ex.GetType().Name);
                    return GeneralSaveResult.SavedApplicationPending(UiText.SettingsStatusSavedAutoStartPending);
                }
            }

            return GeneralSaveResult.SavedAndApplied();
        }
        finally
        {
            _activePreCommitCts = null;
            _gate.Release();
            _activeExecutionTask = null;
            executionTcs.TrySetResult();
        }
    }

    public async Task<GeneralSaveResult> RetryAutoStartAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (_isStopped)
        {
            return GeneralSaveResult.Uncommitted(UiText.SettingsStatusStopped);
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        if (_isStopped)
        {
            _gate.Release();
            return GeneralSaveResult.Uncommitted(UiText.SettingsStatusStopped);
        }

        var executionTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _activeExecutionTask = executionTcs.Task;

        try
        {
            // 独立重试只读取最新已保存的 LaunchAtStartup 意图，不重复写 JSON
            var target = _settingsService.Current.General.LaunchAtStartup;
            try
            {
                await Task.Run(() => _autoStartService.SetEnabled(target), CancellationToken.None).ConfigureAwait(false);
                return GeneralSaveResult.SavedAndApplied();
            }
            catch (Exception ex)
            {
                LogAutoStartApplicationFailed(_logger, ex.GetType().Name);
                return GeneralSaveResult.SavedApplicationPending(UiText.SettingsStatusAutoStartRetryFailed);
            }
        }
        finally
        {
            _gate.Release();
            _activeExecutionTask = null;
            executionTcs.TrySetResult();
        }
    }

    public void Dispose()
    {
        _gate.Dispose();
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "常规设置保存冲突：字段 {FieldName}")]
    private static partial void LogConflictDetected(ILogger logger, string fieldName);

    [LoggerMessage(EventId = 2, Level = LogLevel.Error, Message = "常规设置保存失败，异常类型：{ExceptionType}")]
    private static partial void LogSaveSettingsFailed(ILogger logger, string exceptionType);

    [LoggerMessage(EventId = 3, Level = LogLevel.Warning, Message = "开机自启系统应用失败，异常类型：{ExceptionType}")]
    private static partial void LogAutoStartApplicationFailed(ILogger logger, string exceptionType);
}
