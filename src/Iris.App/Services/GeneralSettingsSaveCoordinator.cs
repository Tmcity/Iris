using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Iris.App.Settings;
using Iris.Core.Platform;
using Iris.Core.Settings;
using Iris.Core.Translation;
using Microsoft.Extensions.Logging;

namespace Iris.App.Services;

internal enum GeneralSaveStatus
{
    /// <summary>校验、冲突、写盘失败或提交前取消；草稿保留。</summary>
    Uncommitted,

    /// <summary>配置已持久化或本来已相同，开机启动等必要应用已完成。</summary>
    SavedAndApplied,

    /// <summary>配置已持久化，但开机启动注册或服务刷新等后续系统动作未完成；保留已保存事实，允许重试应用。</summary>
    SavedApplicationPending,

    /// <summary>配置已持久化，但服务刷新与开机启动注册均未完成；允许分别重试。</summary>
    SavedPendingApplyAndAutoStart,
}

internal sealed record GeneralSaveResult(
    GeneralSaveStatus Status,
    string? FailureReason = null,
    bool IsConflict = false,
    bool IsQuotaFull = false,
    ISettingsApplyReceipt? PendingReceipt = null)
{
    public static GeneralSaveResult Uncommitted(string reason, bool isConflict = false, bool isQuotaFull = false) =>
        new(GeneralSaveStatus.Uncommitted, reason, isConflict, isQuotaFull);

    public static GeneralSaveResult SavedAndApplied() =>
        new(GeneralSaveStatus.SavedAndApplied);

    public static GeneralSaveResult SavedApplicationPending(string reason, ISettingsApplyReceipt? pendingReceipt = null) =>
        new(GeneralSaveStatus.SavedApplicationPending, reason, false, false, pendingReceipt);

    public static GeneralSaveResult SavedPendingApplyAndAutoStart(string reason, ISettingsApplyReceipt? pendingReceipt = null) =>
        new(GeneralSaveStatus.SavedPendingApplyAndAutoStart, reason, false, false, pendingReceipt);
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

/// <summary>
/// 设置保存协调器：统一构建 SettingsEditPlan 并委托 ISettingsEditCoordinator。
/// 处理在途回执配额预留、增量合并、开机自启系统应用与状态跟踪。
/// Scoped 生命周期，绑定到单次窗口会话。
/// 完整规则见 SETTINGS_EDITING.md 与 PROVIDER_SETTINGS_UI.md §5、§6。
/// </summary>
internal sealed partial class GeneralSettingsSaveCoordinator : IDisposable
{
    private static readonly string[] AllowedLogLevels = ["Verbose", "Debug", "Information", "Warning", "Error"];

    private readonly ISettingsService _settingsService;
    private readonly ISettingsEditCoordinator? _editCoordinator;
    private readonly IAutoStartService _autoStartService;
    private readonly ISettingsApplyReceiptManager? _receiptManager;
    private readonly ILogger<GeneralSettingsSaveCoordinator> _logger;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Lock _admissionLock = new();
    private readonly HashSet<Task> _inFlightWork = [];
    private readonly List<CancellationTokenSource> _preCommitCtsList = [];
    private bool _isStopped;
    private bool _disposed;

    public GeneralSettingsSaveCoordinator(
        ISettingsService settingsService,
        ISettingsEditCoordinator editCoordinator,
        IAutoStartService autoStartService,
        ILogger<GeneralSettingsSaveCoordinator> logger)
        : this(settingsService, editCoordinator, autoStartService, null, logger)
    {
    }

    public GeneralSettingsSaveCoordinator(
        ISettingsService settingsService,
        ISettingsEditCoordinator editCoordinator,
        IAutoStartService autoStartService,
        ISettingsApplyReceiptManager? receiptManager,
        ILogger<GeneralSettingsSaveCoordinator> logger)
    {
        _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
        _editCoordinator = editCoordinator ?? throw new ArgumentNullException(nameof(editCoordinator));
        _autoStartService = autoStartService ?? throw new ArgumentNullException(nameof(autoStartService));
        _receiptManager = receiptManager;
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
            throw new OperationCanceledException("Coordinator is stopped or disposed.");
        }

        try
        {
            return await Task.Run(() =>
            {
                preCommitCts.Token.ThrowIfCancellationRequested();
                var isEnabled = _autoStartService.IsEnabled();
                var shouldBeEnabled = _settingsService.Current.General.LaunchAtStartup;
                return isEnabled != shouldBeEnabled;
            }, preCommitCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
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

        var themeEdit = snapshot.ThemeDirty ? new SettingsFieldEdit<AppTheme>(snapshot.BaseTheme, snapshot.TargetTheme) : null;
        var launchEdit = snapshot.LaunchAtStartupDirty ? new SettingsFieldEdit<bool>(snapshot.BaseLaunchAtStartup, snapshot.TargetLaunchAtStartup) : null;
        var notifEdit = snapshot.ShowStartupNotificationDirty ? new SettingsFieldEdit<bool>(snapshot.BaseShowStartupNotification, snapshot.TargetShowStartupNotification) : null;
        var logEdit = snapshot.LogLevelDirty ? new SettingsFieldEdit<string>(snapshot.BaseLogLevel, snapshot.TargetLogLevel) : null;

        var generalPatch = new GeneralSettingsPatch(themeEdit, launchEdit, notifEdit, logEdit);
        var plan = new SettingsEditPlan(generalPatch, []);

        return await CommitPlanAsync(plan, snapshot.LaunchAtStartupDirty, snapshot.TargetLaunchAtStartup, cancellationToken).ConfigureAwait(false);
    }

    public async Task<GeneralSaveResult> CommitPlanAsync(
        SettingsEditPlan plan,
        bool launchAtStartupDirty,
        bool targetLaunchAtStartup,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);

        if (_editCoordinator == null)
        {
            throw new InvalidOperationException("ISettingsEditCoordinator must be provided to commit plans.");
        }

        bool hasProviderChanges = plan.Network is { ProxyMode: not null } or { CustomProxyUrl: not null } or { BypassProxyOnLocal: not null } or { Credentials: not null } || (plan.TranslationChanges != null && plan.TranslationChanges.Count > 0) || plan.TranslationOrder != null
            || plan.OcrChanges.Count > 0 || plan.Ocr is { } o && (o.ActiveProviderId != null || o.FallbackProviderId != null || o.TextLayout != null || o.LanguageHint != null || o.AutoCopyOnOcr != null || o.SilentOcrShowNotification != null)
            || plan.Screenshot is { } shot && (shot.ConfirmOnMouseUp != null || shot.ShowMagnifier != null || shot.HideIrisWindows != null || shot.CopyImageToClipboard != null || shot.MaskOpacity != null);
        bool slotReserved = false;

        if (hasProviderChanges && _receiptManager != null)
        {
            if (!_receiptManager.TryReserveSlot())
            {
                return GeneralSaveResult.Uncommitted(UiText.SettingsStatusPendingApplyQuotaFull, isConflict: false, isQuotaFull: true);
            }
            slotReserved = true;
        }

        var (admitted, tcs, preCommitCts) = TryAdmitWork(cancellationToken);
        if (!admitted || tcs == null || preCommitCts == null)
        {
            if (slotReserved)
            {
                _receiptManager?.ReleaseReservation();
            }
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
                        if (slotReserved)
                        {
                            _receiptManager?.ReleaseReservation();
                            slotReserved = false;
                        }
                        return GeneralSaveResult.Uncommitted(UiText.SettingsStatusStopped);
                    }
                }

                SettingsEditResult editResult;
                try
                {
                    editResult = await _editCoordinator.CommitAsync(plan, effectiveCt).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    if (slotReserved)
                    {
                        _receiptManager?.ReleaseReservation();
                        slotReserved = false;
                    }
                    return GeneralSaveResult.Uncommitted(UiText.SettingsStatusCanceled);
                }
                catch (Exception ex)
                {
                    if (slotReserved)
                    {
                        _receiptManager?.ReleaseReservation();
                        slotReserved = false;
                    }
                    LogSaveUnexpectedFailed(_logger, ex.GetType().Name);
                    return GeneralSaveResult.Uncommitted(UiText.SettingsStatusSaveFailed);
                }

                // 提交边界已越过：无论后续如何，不再受 pre-commit 取消影响
                lock (_admissionLock)
                {
                    _preCommitCtsList.Remove(preCommitCts);
                }

                switch (editResult.Status)
                {
                    case SettingsEditStatus.Applied:
                        if (slotReserved)
                        {
                            _receiptManager?.ReleaseReservation();
                            slotReserved = false;
                        }

                        if (launchAtStartupDirty)
                        {
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

                    case SettingsEditStatus.ApplyPending:
                        if (slotReserved)
                        {
                            _receiptManager?.ConvertReservationToPending(editResult.PendingApply!, editResult.Error?.Message);
                            slotReserved = false;
                        }

                        bool autoStartFailed = false;
                        if (launchAtStartupDirty)
                        {
                            try
                            {
                                await Task.Run(() => _autoStartService.SetEnabled(targetLaunchAtStartup), CancellationToken.None).ConfigureAwait(false);
                            }
                            catch (Exception ex)
                            {
                                LogAutoStartApplicationFailed(_logger, ex.GetType().Name);
                                autoStartFailed = true;
                            }
                        }

                        var reason = editResult.Error?.Message ?? UiText.SettingsStatusSavedPendingApply;
                        if (autoStartFailed)
                        {
                            return GeneralSaveResult.SavedPendingApplyAndAutoStart(UiText.SettingsStatusSavedPendingApplyAndAutoStart, editResult.PendingApply);
                        }
                        return GeneralSaveResult.SavedApplicationPending(reason, editResult.PendingApply);

                    case SettingsEditStatus.Uncommitted:
                    default:
                        if (slotReserved)
                        {
                            _receiptManager?.ReleaseReservation();
                            slotReserved = false;
                        }

                        if (editResult.IsConflict)
                        {
                            LogConflictDetected(_logger, "SettingsEditPlan");
                            return GeneralSaveResult.Uncommitted(UiText.SettingsStatusConflict, isConflict: true);
                        }

                        var errMsg = editResult.Error?.Message ?? UiText.SettingsStatusSaveFailed;
                        return GeneralSaveResult.Uncommitted(errMsg, isConflict: false);
                }
            }
            finally
            {
                _gate.Release();
            }
        }
        catch (OperationCanceledException)
        {
            if (slotReserved)
            {
                _receiptManager?.ReleaseReservation();
            }
            return GeneralSaveResult.Uncommitted(UiText.SettingsStatusCanceled);
        }
        catch (Exception ex)
        {
            if (slotReserved)
            {
                _receiptManager?.ReleaseReservation();
            }
            LogSaveUnexpectedFailed(_logger, ex.GetType().Name);
            return GeneralSaveResult.Uncommitted(UiText.SettingsStatusSaveFailed);
        }
        finally
        {
            ExitWork(tcs, preCommitCts);
        }
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

    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "设置保存冲突：{Detail}")]
    private static partial void LogConflictDetected(ILogger logger, string detail);

    [LoggerMessage(EventId = 2, Level = LogLevel.Error, Message = "设置保存失败，异常类型：{ExceptionType}")]
    private static partial void LogSaveSettingsFailed(ILogger logger, string exceptionType);

    [LoggerMessage(EventId = 3, Level = LogLevel.Warning, Message = "开机自启系统应用失败，异常类型：{ExceptionType}")]
    private static partial void LogAutoStartApplicationFailed(ILogger logger, string exceptionType);

    [LoggerMessage(EventId = 4, Level = LogLevel.Error, Message = "设置保存异常失败，异常类型：{ExceptionType}")]
    private static partial void LogSaveUnexpectedFailed(ILogger logger, string exceptionType);
}
