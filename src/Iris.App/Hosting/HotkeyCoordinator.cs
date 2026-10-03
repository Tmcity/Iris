using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Iris.Core.Actions;
using Iris.Core.Errors;
using Iris.Core.Platform;
using Iris.Core.Presentation;
using Iris.Core.Settings;
using Iris.Platform.Windows.Hotkey;
using Microsoft.Extensions.Logging;

namespace Iris.App.Hosting;

/// <summary>
/// 热键生命周期协调器（App 层）。
/// 负责过滤已交付动作、设置变更语义重配、按键交换支持、Pressed 统一路由、两阶段安全退出、录入暂停恢复与运行时状态快照。
/// 完整规则见 HOTKEY_SERVICE.md §5、§6 与 HOTKEY_RECORDING_UI.md §5、§6。
/// </summary>
internal sealed partial class HotkeyCoordinator : IDisposable
{
    private readonly ISettingsService _settingsService;
    private readonly IHotkeyService _hotkeyService;
    private readonly IActionDispatcher _actionDispatcher;
    private readonly IUiDispatcher _uiDispatcher;
    private readonly INotificationService _notificationService;
    private readonly ILogger<HotkeyCoordinator> _logger;
    private readonly HashSet<IrisAction> _deliveredActions;

    private readonly Lock _gate = new();
    private readonly Dictionary<IrisAction, HotkeyBinding> _activeBindings = [];
    private readonly Dictionary<IrisAction, HotkeyBinding> _failedUnregisteredBindings = [];
    private readonly Dictionary<IrisAction, HotkeyRuntimeItemState> _runtimeItems = [];
    private readonly HashSet<IrisAction> _pendingDispatches = [];
    private readonly List<Task> _activeDispatchTasks = [];
    private readonly CancellationTokenSource _shutdownCts = new();

    private AppSettings? _pendingSettings;
    private AppSettings? _dispatchedSettings;
    private bool _isProcessingSettings;
    private long _dispatchGeneration;
    private long _processingGeneration;
    private long _lastStartedExecutionGeneration;
    private long _snapshotId;
    private bool _isStarted;
    private bool _isStopped;
    private bool _isPaused;
    private HotkeyPauseLease? _activePauseLease;
    private Task? _stopNativeTask;
    private Task? _waitDispatchesTask;

    public event EventHandler<HotkeyRuntimeSnapshot>? RuntimeSnapshotChanged;

    public HotkeyCoordinator(
        ISettingsService settingsService,
        IHotkeyService hotkeyService,
        IActionDispatcher actionDispatcher,
        IEnumerable<IActionHandler> handlers,
        IUiDispatcher uiDispatcher,
        INotificationService notificationService,
        ILogger<HotkeyCoordinator> logger)
    {
        _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
        _hotkeyService = hotkeyService ?? throw new ArgumentNullException(nameof(hotkeyService));
        _actionDispatcher = actionDispatcher ?? throw new ArgumentNullException(nameof(actionDispatcher));
        ArgumentNullException.ThrowIfNull(handlers);
        _uiDispatcher = uiDispatcher ?? throw new ArgumentNullException(nameof(uiDispatcher));
        _notificationService = notificationService ?? throw new ArgumentNullException(nameof(notificationService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        _deliveredActions = handlers.Select(h => h.Action).ToHashSet();

        // 初始状态
        foreach (var action in _deliveredActions)
        {
            _runtimeItems[action] = new HotkeyRuntimeItemState(
                action,
                null,
                HotkeyRuntimeActionStatus.Disabled,
                "(未设置)",
                null,
                0);
        }
    }

    public HotkeyRuntimeSnapshot GetRuntimeSnapshot()
    {
        lock (_gate)
        {
            return new HotkeyRuntimeSnapshot(
                _snapshotId,
                _isPaused,
                new Dictionary<IrisAction, HotkeyRuntimeItemState>(_runtimeItems));
        }
    }

    private void PublishSnapshotLocked()
    {
        _snapshotId++;
        var snapshot = new HotkeyRuntimeSnapshot(
            _snapshotId,
            _isPaused,
            new Dictionary<IrisAction, HotkeyRuntimeItemState>(_runtimeItems));

        _ = Task.Run(() =>
        {
            try
            {
                RuntimeSnapshotChanged?.Invoke(this, snapshot);
            }
            catch (Exception ex)
            {
                LogSnapshotNotifyError(_logger, ex.GetType().Name);
            }
        });
    }

    public async Task StartAsync()
    {
        lock (_gate)
        {
            if (_isStarted || _isStopped)
            {
                return;
            }
            _isStarted = true;
        }

        _settingsService.Changed += OnSettingsChanged;
        _hotkeyService.Pressed += OnHotkeyPressed;

        await _uiDispatcher.InvokeAsync(() =>
        {
            lock (_gate)
            {
                if (_isStopped)
                {
                    return Task.CompletedTask;
                }
            }

            ApplySettingsCore(_settingsService.Current);
            return Task.CompletedTask;
        }, CancellationToken.None).ConfigureAwait(false);
    }

    public async Task<IHotkeyPauseLease> PauseAsync(string owner, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        cancellationToken.ThrowIfCancellationRequested();

        HotkeyPauseLease lease;
        lock (_gate)
        {
            if (_isStopped)
            {
                throw new InvalidOperationException("HotkeyCoordinator is stopped.");
            }
            if (_activePauseLease != null)
            {
                throw new InvalidOperationException("An active pause lease already exists.");
            }

            lease = new HotkeyPauseLease(this, Guid.NewGuid(), owner);
            _activePauseLease = lease;
            _isPaused = true;
            _dispatchGeneration++;
        }

        // 步骤 2: 在热键窗口 owner UI 线程注销全部 Iris 当前注册，失败保留清理状态并报告“无法开始录入”，禁止安装捕获 hook
        try
        {
            await _uiDispatcher.InvokeAsync(() =>
            {
                lock (_gate)
                {
                    if (_isStopped)
                    {
                        return Task.CompletedTask;
                    }
                }

                foreach (var (action, binding) in _activeBindings.ToList())
                {
                    try
                    {
                        _hotkeyService.Unregister(action);
                        _activeBindings.Remove(action);
                    }
                    catch (Exception ex)
                    {
                        LogUnregisterFailed(_logger, action, ex.GetType().Name);
                        _failedUnregisteredBindings[action] = binding;
                        lock (_gate)
                        {
                            if (_runtimeItems.TryGetValue(action, out var oldState))
                            {
                                _runtimeItems[action] = oldState with
                                {
                                    Status = HotkeyRuntimeActionStatus.Failed,
                                    Message = "注销失败"
                                };
                            }
                        }
                        throw;
                    }
                }

                lock (_gate)
                {
                    foreach (var action in _deliveredActions)
                    {
                        if (!_failedUnregisteredBindings.ContainsKey(action) && _runtimeItems.TryGetValue(action, out var oldItem))
                        {
                            if (oldItem.Status == HotkeyRuntimeActionStatus.Registered)
                            {
                                _runtimeItems[action] = oldItem with
                                {
                                    Status = HotkeyRuntimeActionStatus.Paused,
                                    Message = UiText.SettingsHotkeyStatusPaused
                                };
                            }
                        }
                    }
                }

                return Task.CompletedTask;
            }, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            lock (_gate)
            {
                _activePauseLease = null;
                _isPaused = false;
                PublishSnapshotLocked();
            }
            throw;
        }

        // 步骤 4: 等待已接纳且已开始的前置 Handler 工作真实退出
        await WaitForInFlightDispatchesCoreAsync().ConfigureAwait(false);

        lock (_gate)
        {
            PublishSnapshotLocked();
        }

        return lease;
    }

    internal async Task ReleasePauseAsync(IHotkeyPauseLease lease)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(_activePauseLease, lease))
            {
                // 旧 lease 不能释放新暂停
                return;
            }
            _activePauseLease = null;
            _isPaused = false;
            if (_isStopped)
            {
                return;
            }
        }

        // 释放先完成 hook 清理，再撤销暂停并按此时最新持久配置应用，绝不恢复录入开始时的旧绑定
        await _uiDispatcher.InvokeAsync(() =>
        {
            lock (_gate)
            {
                if (_isStopped)
                {
                    return Task.CompletedTask;
                }
            }

            ApplySettingsCore(_settingsService.Current);
            return Task.CompletedTask;
        }, CancellationToken.None).ConfigureAwait(false);
    }

    public async Task<HotkeyRuntimeSnapshot> RetryApplyAsync()
    {
        lock (_gate)
        {
            if (_isStopped)
            {
                throw new InvalidOperationException("HotkeyCoordinator is stopped.");
            }
            if (_isPaused)
            {
                throw new InvalidOperationException("Cannot retry hotkeys while paused.");
            }
        }

        await _uiDispatcher.InvokeAsync(() =>
        {
            lock (_gate)
            {
                if (_isStopped || _isPaused)
                {
                    return Task.CompletedTask;
                }
            }

            ApplySettingsCore(_settingsService.Current, retryOnly: true);
            return Task.CompletedTask;
        }, CancellationToken.None).ConfigureAwait(false);

        return GetRuntimeSnapshot();
    }

    public Task StopAcceptanceAndNativeAsync()
    {
        lock (_gate)
        {
            if (_stopNativeTask != null && !_stopNativeTask.IsCompleted)
            {
                return _stopNativeTask;
            }

            if (_isStopped && _stopNativeTask != null && _stopNativeTask.IsCompletedSuccessfully)
            {
                return Task.CompletedTask;
            }

            _isStopped = true;
            _isPaused = false;
            _activePauseLease = null;
            _pendingSettings = null;
            _dispatchedSettings = null;
            _isProcessingSettings = false;
            _processingGeneration = 0;
            _dispatchGeneration++;

            try
            {
                _shutdownCts.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }

            _settingsService.Changed -= OnSettingsChanged;
            _hotkeyService.Pressed -= OnHotkeyPressed;

            _stopNativeTask = StopNativeCoreAsync();
            return _stopNativeTask;
        }
    }

    private async Task StopNativeCoreAsync()
    {
        await _uiDispatcher.InvokeAsync(() =>
        {
            _hotkeyService.Dispose();
            _activeBindings.Clear();
            _failedUnregisteredBindings.Clear();
            lock (_gate)
            {
                PublishSnapshotLocked();
            }
            return Task.CompletedTask;
        }, CancellationToken.None).ConfigureAwait(false);
    }

    public Task WaitForInFlightDispatchesAsync()
    {
        lock (_gate)
        {
            if (_waitDispatchesTask != null && !_waitDispatchesTask.IsCompleted)
            {
                return _waitDispatchesTask;
            }

            if (_waitDispatchesTask != null && _waitDispatchesTask.IsCompletedSuccessfully)
            {
                return Task.CompletedTask;
            }

            _waitDispatchesTask = WaitForInFlightDispatchesCoreAsync();
            return _waitDispatchesTask;
        }
    }

    private async Task WaitForInFlightDispatchesCoreAsync()
    {
        Task[] tasksToWait;
        lock (_gate)
        {
            tasksToWait = [.. _activeDispatchTasks];
        }

        if (tasksToWait.Length > 0)
        {
            try
            {
                await Task.WhenAll(tasksToWait).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogDispatchWaitException(_logger, ex.GetType().Name);
            }
        }
    }

    public void StopSynchronousFallback()
    {
        lock (_gate)
        {
            _isStopped = true;
            _isPaused = false;
            _activePauseLease = null;
            _pendingSettings = null;
            _dispatchedSettings = null;
            _isProcessingSettings = false;
            _processingGeneration = 0;
            _dispatchGeneration++;
        }

        try
        {
            _shutdownCts.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        _settingsService.Changed -= OnSettingsChanged;
        _hotkeyService.Pressed -= OnHotkeyPressed;

        if (_uiDispatcher.CheckAccess())
        {
            try
            {
                _hotkeyService.Dispose();
                _activeBindings.Clear();
                _failedUnregisteredBindings.Clear();
            }
            catch (Exception ex)
            {
                LogSyncFallbackFailed(_logger, ex.GetType().Name);
            }
        }
    }

    public void Dispose()
    {
        StopSynchronousFallback();
        _shutdownCts.Dispose();
    }

    private void OnSettingsChanged(object? sender, SettingsChangedEventArgs e)
    {
        if (AreHotkeysEqual(e.OldSettings.Hotkeys, e.NewSettings.Hotkeys))
        {
            return;
        }

        lock (_gate)
        {
            if (_isStopped)
            {
                return;
            }

            _pendingSettings = e.NewSettings;

            if (_isPaused)
            {
                // 暂停期间 settings Changed 仍只保留一个最新快照，但不重新注册；界面显示“已保存，录入结束后应用”
                UpdatePausedItemsWithNewSettingsLocked(e.NewSettings);
                PublishSnapshotLocked();
                return;
            }

            if (_isProcessingSettings)
            {
                return;
            }

            ScheduleProcessingLocked();
        }
    }

    private void UpdatePausedItemsWithNewSettingsLocked(AppSettings newSettings)
    {
        _dispatchGeneration++;
        foreach (var action in _deliveredActions)
        {
            newSettings.Hotkeys.TryGetValue(action, out var newBinding);
            var keyComboText = FormatKeyCombo(newBinding);
            _runtimeItems[action] = new HotkeyRuntimeItemState(
                action,
                newBinding,
                HotkeyRuntimeActionStatus.Paused,
                keyComboText,
                UiText.SettingsHotkeyStatusSavedPending,
                _dispatchGeneration);
        }
    }

    private void ScheduleProcessingLocked()
    {
        if (_isStopped || _pendingSettings == null)
        {
            return;
        }

        _isProcessingSettings = true;
        long generation = ++_dispatchGeneration;
        _processingGeneration = generation;
        _dispatchedSettings = _pendingSettings;
        Task? dispatchTask = null;
        try
        {
            dispatchTask = _uiDispatcher.InvokeAsync(() => ProcessPendingSettingsAsync(generation), CancellationToken.None);
        }
        catch (Exception ex)
        {
            if (_processingGeneration == generation)
            {
                _isProcessingSettings = false;
                _dispatchedSettings = null;
            }
            LogDispatchSettingsFailed(_logger, ex.GetType().Name);
            return;
        }

        if (dispatchTask != null)
        {
            _ = dispatchTask.ContinueWith(t => OnDispatchCompleted(generation, t), TaskScheduler.Default);
        }
    }

    private void OnDispatchCompleted(long generation, Task task)
    {
        if (task.IsCompletedSuccessfully)
        {
            return;
        }

        bool wasExecutionStarted;
        lock (_gate)
        {
            wasExecutionStarted = (_lastStartedExecutionGeneration == generation);
        }

        if (!wasExecutionStarted)
        {
            var ex = task.Exception?.InnerException ?? task.Exception;
            string exceptionType = task.IsCanceled ? "OperationCanceledException" : (ex?.GetType().Name ?? "Unknown");
            LogDispatchSettingsFailed(_logger, exceptionType);
        }

        lock (_gate)
        {
            if (_processingGeneration != generation)
            {
                // 代次保护：旧代次的完成（失败或取消）绝不得清除更新代次的 _isProcessingSettings
                return;
            }

            _isProcessingSettings = false;
            bool hasNewPending = _pendingSettings != null && !ReferenceEquals(_pendingSettings, _dispatchedSettings);
            _dispatchedSettings = null;

            if (!_isStopped && !_isPaused && hasNewPending)
            {
                // 在故障调度执行期间到达了新的配置快照，转交启动消费新快照
                ScheduleProcessingLocked();
            }
            else
            {
                // 仅本次调度自身失败且无更新快照排队时丢弃，避免无限重试相同失败项
                _pendingSettings = null;
            }
        }
    }

    private Task ProcessPendingSettingsAsync(long generation)
    {
        lock (_gate)
        {
            _lastStartedExecutionGeneration = generation;
        }

        try
        {
            while (true)
            {
                AppSettings targetSettings;
                lock (_gate)
                {
                    if (_isStopped || _isPaused || _dispatchGeneration != generation || _pendingSettings == null)
                    {
                        if (_processingGeneration == generation)
                        {
                            _isProcessingSettings = false;
                            _dispatchedSettings = null;
                        }
                        if (!_isStopped && !_isPaused && _pendingSettings != null && !_isProcessingSettings)
                        {
                            ScheduleProcessingLocked();
                        }
                        return Task.CompletedTask;
                    }

                    targetSettings = _pendingSettings;
                    _pendingSettings = null;
                }

                ApplySettingsCore(targetSettings);
            }
        }
        catch (Exception ex)
        {
            lock (_gate)
            {
                if (_processingGeneration == generation)
                {
                    _isProcessingSettings = false;
                    _dispatchedSettings = null;
                }
                if (!_isStopped && !_isPaused && _pendingSettings != null && !_isProcessingSettings)
                {
                    ScheduleProcessingLocked();
                }
            }
            LogProcessSettingsFailed(_logger, ex.GetType().Name);
            throw;
        }
    }

    private void ApplySettingsCore(AppSettings settings, bool retryOnly = false)
    {
        lock (_gate)
        {
            if (_isStopped)
            {
                return;
            }
        }

        var targetHotkeys = settings.Hotkeys;
        var failedResults = new List<(IrisAction Action, HotkeyBinding Binding, HotkeyRegistrationResult Result)>();

        // 步骤 0：若之前有注销失败的项，先重试注销
        if (_failedUnregisteredBindings.Count > 0)
        {
            foreach (var (failedAction, failedBinding) in _failedUnregisteredBindings.ToList())
            {
                try
                {
                    _hotkeyService.Unregister(failedAction);
                    _failedUnregisteredBindings.Remove(failedAction);
                    _activeBindings.Remove(failedAction);
                }
                catch (Exception ex)
                {
                    LogUnregisterFailed(_logger, failedAction, ex.GetType().Name);
                    failedResults.Add((failedAction, failedBinding, new HotkeyRegistrationResult(
                        failedAction, false, ErrorCode.Unknown, ErrorMessages.GetDefault(ErrorCode.Unknown))));
                }
            }
        }

        var actionsToUnregister = new List<IrisAction>();
        var actionsToRegister = new List<(IrisAction Action, HotkeyBinding Binding)>();

        foreach (var action in _deliveredActions)
        {
            bool hasCurrent = _activeBindings.TryGetValue(action, out var currentBinding);
            bool hasNew = targetHotkeys.TryGetValue(action, out var newBinding) && newBinding.Enabled;

            if (!retryOnly)
            {
                if (hasCurrent)
                {
                    if (!hasNew || !AreBindingsEqual(currentBinding!, newBinding!))
                    {
                        actionsToUnregister.Add(action);
                    }
                }

                if (hasNew)
                {
                    if (!_failedUnregisteredBindings.ContainsKey(action))
                    {
                        if (!hasCurrent || !AreBindingsEqual(currentBinding!, newBinding!))
                        {
                            actionsToRegister.Add((action, newBinding!));
                        }
                    }
                }
            }
            else
            {
                // 重试模式：只注册目标为启用但当前未在活跃绑定中的项
                if (hasNew && (!hasCurrent || !AreBindingsEqual(currentBinding!, newBinding!)))
                {
                    if (!_failedUnregisteredBindings.ContainsKey(action))
                    {
                        actionsToRegister.Add((action, newBinding!));
                    }
                }
            }
        }

        // 步骤 1：先注销所有受影响的旧绑定，支持按键组合互相交换
        foreach (var action in actionsToUnregister)
        {
            var oldBinding = _activeBindings[action];
            _activeBindings.Remove(action);

            try
            {
                _hotkeyService.Unregister(action);
            }
            catch (Exception ex)
            {
                LogUnregisterFailed(_logger, action, ex.GetType().Name);
                _failedUnregisteredBindings[action] = oldBinding;
                failedResults.Add((action, oldBinding, new HotkeyRegistrationResult(
                    action, false, ErrorCode.Unknown, ErrorMessages.GetDefault(ErrorCode.Unknown))));
                actionsToRegister.RemoveAll(x => x.Action == action);
            }
        }

        // 步骤 2：按 Action 数值稳定顺序注册新绑定
        foreach (var (action, binding) in actionsToRegister.OrderBy(x => (int)x.Action))
        {
            var result = _hotkeyService.Register(action, binding);
            if (result.Success)
            {
                _activeBindings[action] = binding;
            }
            else
            {
                failedResults.Add((action, binding, result));
            }
        }

        // 更新运行时不可变快照状态
        lock (_gate)
        {
            foreach (var action in _deliveredActions)
            {
                targetHotkeys.TryGetValue(action, out var targetBinding);
                var keyComboText = FormatKeyCombo(targetBinding);

                if (_failedUnregisteredBindings.TryGetValue(action, out var unregBinding))
                {
                    _runtimeItems[action] = new HotkeyRuntimeItemState(
                        action,
                        targetBinding,
                        HotkeyRuntimeActionStatus.Failed,
                        FormatKeyCombo(unregBinding),
                        "注销失败",
                        _dispatchGeneration);
                }
                else if (_activeBindings.TryGetValue(action, out var activeBinding))
                {
                    _runtimeItems[action] = new HotkeyRuntimeItemState(
                        action,
                        activeBinding,
                        HotkeyRuntimeActionStatus.Registered,
                        FormatKeyCombo(activeBinding),
                        UiText.SettingsHotkeyStatusRegistered,
                        _dispatchGeneration);
                }
                else
                {
                    var failure = failedResults.FirstOrDefault(f => f.Action == action);
                    if (failure != default)
                    {
                        var status = failure.Result.Error switch
                        {
                            ErrorCode.HotkeyConflict => HotkeyRuntimeActionStatus.Conflict,
                            ErrorCode.HotkeyInvalid => HotkeyRuntimeActionStatus.Invalid,
                            ErrorCode.InvalidArgument => HotkeyRuntimeActionStatus.Invalid,
                            _ => failure.Result.Message?.Contains("占用") == true
                                ? HotkeyRuntimeActionStatus.Conflict
                                : HotkeyRuntimeActionStatus.Failed
                        };
                        string message = failure.Result.Message ?? ErrorMessages.GetDefault(failure.Result.Error ?? ErrorCode.Unknown);

                        _runtimeItems[action] = new HotkeyRuntimeItemState(
                            action,
                            failure.Binding,
                            status,
                            keyComboText,
                            message,
                            _dispatchGeneration);
                    }
                    else
                    {
                        // 禁用或未设置
                        _runtimeItems[action] = new HotkeyRuntimeItemState(
                            action,
                            targetBinding,
                            HotkeyRuntimeActionStatus.Disabled,
                            keyComboText,
                            null,
                            _dispatchGeneration);
                    }
                }
            }

            PublishSnapshotLocked();
        }

        // 步骤 3：失败项汇总为安全提示通知用户
        if (failedResults.Count > 0 && !retryOnly)
        {
            NotifyFailures(failedResults);
        }
    }

    private void NotifyFailures(List<(IrisAction Action, HotkeyBinding Binding, HotkeyRegistrationResult Result)> failures)
    {
        var lines = new List<string>(failures.Count);
        foreach (var (action, binding, result) in failures)
        {
            string actionName = GetActionDisplayName(action);
            string keyCombo = FormatKeyCombo(binding);
            string errorText = result.Message ?? ErrorMessages.GetDefault(result.Error ?? ErrorCode.Unknown);
            lines.Add($"【{actionName}】({keyCombo}): {errorText}");
        }

        string summary = string.Join("\n", lines);
        try
        {
            _notificationService.Notify(NotificationLevel.Warning, UiText.AppName, summary);
        }
        catch (Exception ex)
        {
            LogNotifyFailed(_logger, ex.GetType().Name);
        }
    }

    private static string GetActionDisplayName(IrisAction action) => action switch
    {
        IrisAction.InputTranslate => UiText.TrayMenuInputTranslate,
        IrisAction.ClipboardTranslate => UiText.TrayMenuClipboardTranslate,
        IrisAction.TranslateText => "翻译文本",
        IrisAction.OpenSettings => UiText.SettingsTitle,
        IrisAction.SelectionTranslate => "划词翻译",
        IrisAction.ScreenshotTranslate => "截图翻译",
        IrisAction.ScreenshotOcr => "截图 OCR",
        IrisAction.SilentScreenshotOcr => "静默截图 OCR",
        IrisAction.ToggleFloatingBall => "悬浮球",
        _ => action.ToString(),
    };

    internal static string FormatKeyCombo(HotkeyModifiers modifiers, string? key)
    {
        var parts = new List<string>(5);
        if ((modifiers & HotkeyModifiers.Control) != 0) parts.Add("Ctrl");
        if ((modifiers & HotkeyModifiers.Alt) != 0) parts.Add("Alt");
        if ((modifiers & HotkeyModifiers.Shift) != 0) parts.Add("Shift");
        if ((modifiers & HotkeyModifiers.Win) != 0) parts.Add("Win");

        if (!string.IsNullOrWhiteSpace(key))
        {
            if (KeyNameMapper.TryGetCanonicalKeyName(key, out var canonicalKey))
            {
                parts.Add(canonicalKey);
            }
            else
            {
                parts.Add("(无效键)");
            }
        }

        return parts.Count > 0 ? string.Join("+", parts) : string.Empty;
    }

    internal static string FormatKeyCombo(HotkeyBinding? binding)
    {
        if (binding == null)
        {
            return "(未设置)";
        }

        string formatted = FormatKeyCombo(binding.Modifiers, binding.Key);
        if (string.IsNullOrEmpty(formatted))
        {
            return "(未设置)";
        }

        return binding.Enabled ? formatted : $"{formatted} (已禁用)";
    }

    private void OnHotkeyPressed(object? sender, HotkeyPressedEventArgs e)
    {
        TaskCompletionSource? tcs = null;
        long generation;
        lock (_gate)
        {
            if (_isStopped || _isPaused)
            {
                return;
            }

            if (!_deliveredActions.Contains(e.Action))
            {
                return;
            }

            // 限制每 Action 的待处理数量，避免无界创建 Task
            if (_pendingDispatches.Contains(e.Action))
            {
                return;
            }

            generation = _dispatchGeneration;
            _pendingDispatches.Add(e.Action);
            tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _activeDispatchTasks.Add(tcs.Task);
        }

        // 离开 WndProc 线程切到线程池执行业务分发，确保 WndProc 立即返回且业务绝不内联在 WndProc 上；
        // 且在业务开始前（在锁内）即已将 tcs.Task 纳入 _activeDispatchTasks 退出跟踪。
        _ = Task.Run(async () =>
        {
            try
            {
                await DispatchActionAsync(e.Action, generation).ConfigureAwait(false);
            }
            finally
            {
                tcs.TrySetResult();
                lock (_gate)
                {
                    _activeDispatchTasks.Remove(tcs.Task);
                }
            }
        });
    }

    private async Task DispatchActionAsync(IrisAction action, long generation)
    {
        try
        {
            // 排队但尚未执行的旧热键动作在实际 UI 执行点再次校验代次/暂停/停止状态并退出，不能在恢复后补执行。
            await _uiDispatcher.InvokeAsync(async () =>
            {
                lock (_gate)
                {
                    if (_isStopped || _isPaused || _dispatchGeneration != generation)
                    {
                        return;
                    }
                }

                var context = new ActionContext(ActionTrigger.Hotkey);
                await _actionDispatcher.DispatchAsync(action, context, _shutdownCts.Token).ConfigureAwait(false);
            }, _shutdownCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 正常取消静默退出
        }
        catch (Exception ex)
        {
            LogDispatchFailed(_logger, action, ex.GetType().Name);
        }
        finally
        {
            lock (_gate)
            {
                _pendingDispatches.Remove(action);
            }
        }
    }

    private static bool AreHotkeysEqual(
        IReadOnlyDictionary<IrisAction, HotkeyBinding> a,
        IReadOnlyDictionary<IrisAction, HotkeyBinding> b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a.Count != b.Count) return false;

        foreach (var (action, bindingA) in a)
        {
            if (!b.TryGetValue(action, out var bindingB))
            {
                return false;
            }

            if (!AreBindingsEqual(bindingA, bindingB))
            {
                return false;
            }
        }

        return true;
    }

    private static bool AreBindingsEqual(HotkeyBinding a, HotkeyBinding b)
    {
        if (ReferenceEquals(a, b)) return true;
        return a.Enabled == b.Enabled
            && a.Modifiers == b.Modifiers
            && string.Equals(a.Key?.Trim(), b.Key?.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private sealed class HotkeyPauseLease : IHotkeyPauseLease
    {
        private readonly HotkeyCoordinator _coordinator;
        private int _disposed;

        public Guid LeaseId { get; }
        public string Owner { get; }

        public HotkeyPauseLease(HotkeyCoordinator coordinator, Guid leaseId, string owner)
        {
            _coordinator = coordinator;
            LeaseId = leaseId;
            Owner = owner;
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                await _coordinator.ReleasePauseAsync(this).ConfigureAwait(false);
            }
        }
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "注销热键失败: Action={Action}, ExceptionType={ExceptionType}")]
    private static partial void LogUnregisterFailed(ILogger logger, IrisAction action, string exceptionType);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning, Message = "等待在途热键分发任务异常: ExceptionType={ExceptionType}")]
    private static partial void LogDispatchWaitException(ILogger logger, string exceptionType);

    [LoggerMessage(EventId = 3, Level = LogLevel.Warning, Message = "同步兜底清理热键异常: ExceptionType={ExceptionType}")]
    private static partial void LogSyncFallbackFailed(ILogger logger, string exceptionType);

    [LoggerMessage(EventId = 4, Level = LogLevel.Warning, Message = "热键失败通知展示异常: ExceptionType={ExceptionType}")]
    private static partial void LogNotifyFailed(ILogger logger, string exceptionType);

    [LoggerMessage(EventId = 5, Level = LogLevel.Error, Message = "热键分发遇到异常: Action={Action}, ExceptionType={ExceptionType}")]
    private static partial void LogDispatchFailed(ILogger logger, IrisAction action, string exceptionType);

    [LoggerMessage(EventId = 6, Level = LogLevel.Warning, Message = "调度设置热键处理任务失败: ExceptionType={ExceptionType}")]
    private static partial void LogDispatchSettingsFailed(ILogger logger, string exceptionType);

    [LoggerMessage(EventId = 7, Level = LogLevel.Error, Message = "处理设置热键遇到异常: ExceptionType={ExceptionType}")]
    private static partial void LogProcessSettingsFailed(ILogger logger, string exceptionType);

    [LoggerMessage(EventId = 8, Level = LogLevel.Warning, Message = "通知热键运行时快照变化异常: ExceptionType={ExceptionType}")]
    private static partial void LogSnapshotNotifyError(ILogger logger, string exceptionType);
}
