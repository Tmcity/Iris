using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Iris.App.Settings;
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
/// 常规设置保存协调器：增量合并、冲突检测、开机自启系统应用与状态跟踪。
/// Scoped 生命周期，绑定到单次窗口会话。
/// 完整规则见 SETTINGS_EDITING.md §3、§4、§5。
/// </summary>
internal sealed partial class GeneralSettingsSaveCoordinator : IDisposable
{
    private static readonly string[] AllowedLogLevels = ["Verbose", "Debug", "Information", "Warning", "Error", "Fatal"];

    private readonly ISettingsService _settingsService;
    private readonly IAutoStartService _autoStartService;
    private readonly ILogger<GeneralSettingsSaveCoordinator> _logger;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Lock _admissionLock = new();
    private readonly HashSet<Task> _inFlightWork = [];
    private readonly List<CancellationTokenSource> _preCommitCtsList = [];
    private bool _isStopped;
    private bool _disposed;

    public GeneralSettingsSaveCoordinator(
        ISettingsService settingsService,
        IAutoStartService autoStartService,
        ILogger<GeneralSettingsSaveCoordinator> logger)
    {
        _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
        _autoStartService = autoStartService ?? throw new ArgumentNullException(nameof(autoStartService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    internal Task? ActiveWorkTask
    {
        get
        {
            lock (_admissionLock)
            {
                return _inFlightWork.Count > 0 ? Task.WhenAll(_inFlightWork.ToArray()) : null;
            }
        }
    }

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

    public async Task<bool> QueryAutoStartPendingAsync(CancellationToken cancellationToken = default)
    {
        var (admitted, tcs, preCommitCts) = TryAdmitWork(cancellationToken);
        if (!admitted || tcs == null || preCommitCts == null)
        {
            return false;
        }

        try
        {
            return await Task.Run(() =>
            {
                try
                {
                    preCommitCts.Token.ThrowIfCancellationRequested();
                    var isEnabled = _autoStartService.IsEnabled();
                    var shouldBeEnabled = _settingsService.Current.General.LaunchAtStartup;
                    return isEnabled != shouldBeEnabled;
                }
                catch (OperationCanceledException)
                {
                    return false;
                }
                catch
                {
                    return true;
                }
            }, preCommitCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch
        {
            return true;
        }
        finally
        {
            ExitWork(tcs, preCommitCts);
        }
    }

    private (bool Admitted, TaskCompletionSource? Tcs, CancellationTokenSource? PreCommitCts) TryAdmitWork(CancellationToken callerCt)
    {
        lock (_admissionLock)
        {
            if (_isStopped || _disposed)
            {
                return (false, null, null);
            }

            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _inFlightWork.Add(tcs.Task);

            CancellationTokenSource preCommitCts;
            if (callerCt.CanBeCanceled)
            {
                preCommitCts = CancellationTokenSource.CreateLinkedTokenSource(callerCt);
            }
            else
            {
                preCommitCts = new CancellationTokenSource();
            }
            _preCommitCtsList.Add(preCommitCts);

            return (true, tcs, preCommitCts);
        }
    }

    private void ExitWork(TaskCompletionSource tcs, CancellationTokenSource? preCommitCts)
    {
        lock (_admissionLock)
        {
            _inFlightWork.Remove(tcs.Task);
            if (preCommitCts != null)
            {
                _preCommitCtsList.Remove(preCommitCts);
            }
        }

        if (preCommitCts != null)
        {
            try
            {
                preCommitCts.Dispose();
            }
            catch
            {
            }
        }

        tcs.TrySetResult();
    }

    internal void CancelPreCommitWork() => StopAcceptanceAndCancelPreCommit();

    internal void StopAcceptanceAndCancelPreCommit()
    {
        List<CancellationTokenSource> ctsList;
        lock (_admissionLock)
        {
            _isStopped = true;
            ctsList = new List<CancellationTokenSource>(_preCommitCtsList);
        }

        foreach (var cts in ctsList)
        {
            try
            {
                cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }

    internal async Task WaitForActiveWorkAsync()
    {
        StopAcceptanceAndCancelPreCommit();

        while (true)
        {
            Task[] tasks;
            lock (_admissionLock)
            {
                if (_inFlightWork.Count == 0)
                {
                    break;
                }
                tasks = _inFlightWork.ToArray();
            }

            try
            {
                await Task.WhenAll(tasks).ConfigureAwait(false);
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

        if (!snapshot.HasAnyDirty)
        {
            return GeneralSaveResult.SavedAndApplied();
        }

        if (snapshot.ThemeDirty && !Enum.IsDefined(snapshot.TargetTheme))
        {
            return GeneralSaveResult.Uncommitted(UiText.SettingsStatusInvalidTheme);
        }

        if (snapshot.LogLevelDirty && !AllowedLogLevels.Contains(snapshot.TargetLogLevel, StringComparer.OrdinalIgnoreCase))
        {
            return GeneralSaveResult.Uncommitted(UiText.SettingsStatusInvalidLogLevel);
        }

        var (admitted, tcs, preCommitCts) = TryAdmitWork(cancellationToken);
        if (!admitted || tcs == null || preCommitCts == null)
        {
            return GeneralSaveResult.Uncommitted(UiText.SettingsStatusStopped);
        }

        try
        {
            var effectiveCt = preCommitCts.Token;
            effectiveCt.ThrowIfCancellationRequested();

            await _gate.WaitAsync(effectiveCt).ConfigureAwait(false);
            try
            {
                lock (_admissionLock)
                {
                    if (_isStopped || _disposed)
                    {
                        return GeneralSaveResult.Uncommitted(UiText.SettingsStatusStopped);
                    }
                }

                return await SaveAsyncCore(snapshot, preCommitCts).ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
            }
        }
        catch (OperationCanceledException)
        {
            return GeneralSaveResult.Uncommitted(UiText.SettingsStatusCanceled);
        }
        catch (Exception ex)
        {
            LogSaveUnexpectedFailed(_logger, ex.GetType().Name);
            return GeneralSaveResult.Uncommitted(UiText.SettingsStatusSaveFailed);
        }
        finally
        {
            ExitWork(tcs, preCommitCts);
        }
    }

    private async Task<GeneralSaveResult> SaveAsyncCore(
        GeneralDraftSnapshot snapshot,
        CancellationTokenSource preCommitCts)
    {
        try
        {
            await _settingsService.UpdateAsync(current =>
            {
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

                var themeSame = !snapshot.ThemeDirty || current.General.Theme == snapshot.TargetTheme;
                var launchSame = !snapshot.LaunchAtStartupDirty || current.General.LaunchAtStartup == snapshot.TargetLaunchAtStartup;
                var notifSame = !snapshot.ShowStartupNotificationDirty || current.General.ShowStartupNotification == snapshot.TargetShowStartupNotification;
                var logSame = !snapshot.LogLevelDirty || string.Equals(current.General.LogLevel, snapshot.TargetLogLevel, StringComparison.OrdinalIgnoreCase);

                if (themeSame && launchSame && notifSame && logSame)
                {
                    return current;
                }

                var updatedGeneral = current.General with
                {
                    Theme = snapshot.ThemeDirty ? snapshot.TargetTheme : current.General.Theme,
                    LaunchAtStartup = snapshot.LaunchAtStartupDirty ? snapshot.TargetLaunchAtStartup : current.General.LaunchAtStartup,
                    ShowStartupNotification = snapshot.ShowStartupNotificationDirty ? snapshot.TargetShowStartupNotification : current.General.ShowStartupNotification,
                    LogLevel = snapshot.LogLevelDirty ? snapshot.TargetLogLevel : current.General.LogLevel,
                };

                return current with { General = updatedGeneral };
            }, preCommitCts.Token).ConfigureAwait(false);
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

        // 提交边界已越过：JSON 已提交成功，提交后不再受 pre-commit 取消影响
        lock (_admissionLock)
        {
            _preCommitCtsList.Remove(preCommitCts);
        }

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

    public async Task<GeneralSaveResult> RetryAutoStartAsync(CancellationToken cancellationToken = default)
    {
        var (admitted, tcs, preCommitCts) = TryAdmitWork(cancellationToken);
        if (!admitted || tcs == null || preCommitCts == null)
        {
            return GeneralSaveResult.Uncommitted(UiText.SettingsStatusStopped);
        }

        try
        {
            var effectiveCt = preCommitCts.Token;
            effectiveCt.ThrowIfCancellationRequested();

            await _gate.WaitAsync(effectiveCt).ConfigureAwait(false);
            try
            {
                lock (_admissionLock)
                {
                    if (_isStopped || _disposed)
                    {
                        return GeneralSaveResult.Uncommitted(UiText.SettingsStatusStopped);
                    }
                }

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
            }
        }
        catch (OperationCanceledException)
        {
            return GeneralSaveResult.Uncommitted(UiText.SettingsStatusCanceled);
        }
        catch (Exception ex)
        {
            LogAutoStartApplicationFailed(_logger, ex.GetType().Name);
            return GeneralSaveResult.SavedApplicationPending(UiText.SettingsStatusAutoStartRetryFailed);
        }
        finally
        {
            ExitWork(tcs, preCommitCts);
        }
    }

    public void Dispose()
    {
        lock (_admissionLock)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            _isStopped = true;
        }

        _gate.Dispose();
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "常规设置保存冲突：字段 {FieldName}")]
    private static partial void LogConflictDetected(ILogger logger, string fieldName);

    [LoggerMessage(EventId = 2, Level = LogLevel.Error, Message = "常规设置保存失败，异常类型：{ExceptionType}")]
    private static partial void LogSaveSettingsFailed(ILogger logger, string exceptionType);

    [LoggerMessage(EventId = 3, Level = LogLevel.Warning, Message = "开机自启系统应用失败，异常类型：{ExceptionType}")]
    private static partial void LogAutoStartApplicationFailed(ILogger logger, string exceptionType);

    [LoggerMessage(EventId = 4, Level = LogLevel.Error, Message = "常规设置保存异常失败，异常类型：{ExceptionType}")]
    private static partial void LogSaveUnexpectedFailed(ILogger logger, string exceptionType);
}
