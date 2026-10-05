using System;
using System.Threading;
using System.Threading.Tasks;
using Iris.Core.Platform;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Iris.App.Hosting;

/// <summary>
/// 快捷键短时录入协调器（App 层）。
/// 协调 HotkeyCoordinator 的暂停租约与 IHotkeyCaptureService 的底层键盘捕获会话。
/// 规则见 HOTKEY_RECORDING_UI.md §5、§8。
/// </summary>
internal sealed class HotkeyRecordingCoordinator : IAsyncDisposable
{
    private readonly HotkeyCoordinator? _hotkeyCoordinator;
    private readonly IHotkeyCaptureService? _captureService;
    private readonly ILogger<HotkeyRecordingCoordinator> _logger;

    private readonly Lock _gate = new();
    private ActiveRecordingContext? _activeContext;
    private HotkeyCaptureResult? _lastCapturedResult;
    private bool _isStopped;
    private int _preparationPauses;
    private long _preparationEpoch;
    internal long PreparationEpoch { get { lock (_gate) return _preparationEpoch; } }
    internal bool CanStartRecording { get { lock (_gate) return !_isStopped && _preparationPauses == 0; } }
    internal event Func<Task>? AdmissionChanged;
    private async Task NotifyAdmissionAsync() => await Task.WhenAll(AdmissionChanged?.GetInvocationList()
        .Cast<Func<Task>>().Select(async handler => await handler().ConfigureAwait(false)) ?? []).ConfigureAwait(false);
    internal PreparationPause PauseForPreparation()
    {
        lock (_gate) { _preparationPauses++; _preparationEpoch++; }
        return new PreparationPause(this);
    }
    internal void StopAcceptance() { lock (_gate) { _isStopped = true; _preparationEpoch++; } }
    internal async Task CancelForPreparationAsync()
    {
        lock (_gate) { if (_activeContext != null) _activeContext.DiscardResult = true; _lastCapturedResult = null; }
        try { await CancelRecordingAsync().ConfigureAwait(false); }
        catch when (HasFailedCleanup) { /* Retry only the retained physical cleanup below. */ }
        if (HasFailedCleanup) await RetryCleanupAsync().ConfigureAwait(false);
    }
    internal sealed class PreparationPause(HotkeyRecordingCoordinator owner) : IAsyncDisposable
    {
        private readonly object _gate = new();
        private bool _decremented;
        private Task? _notification, _release;
        internal Task Ready
        {
            get
            {
                lock (_gate)
                {
                    if (_notification == null || (_notification.IsFaulted || _notification.IsCanceled)) _notification = Task.Run(owner.NotifyAdmissionAsync);
                    return _notification;
                }
            }
        }
        public ValueTask DisposeAsync()
        {
            lock (_gate)
            {
                if (_release == null || (_release.IsFaulted || _release.IsCanceled)) _release = Task.Run(ReleaseAsync);
                return new(_release);
            }
        }
        private async Task ReleaseAsync()
        {
            await Ready.ConfigureAwait(false);
            lock (owner._gate)
            {
                if (!_decremented) { _decremented = true; owner._preparationPauses--; }
            }
            await owner.NotifyAdmissionAsync().ConfigureAwait(false);
        }
    }


    public HotkeyRecordingCoordinator(
        HotkeyCoordinator? hotkeyCoordinator = null,
        IHotkeyCaptureService? captureService = null,
        ILogger<HotkeyRecordingCoordinator>? logger = null)
    {
        _hotkeyCoordinator = hotkeyCoordinator;
        _captureService = captureService;
        _logger = logger ?? NullLogger<HotkeyRecordingCoordinator>.Instance;
    }

    public bool IsRecording
    {
        get
        {
            lock (_gate)
            {
                return _activeContext != null && !_activeContext.IsCompleted;
            }
        }
    }

    public bool HasFailedCleanup
    {
        get
        {
            lock (_gate)
            {
                return _activeContext?.HasFailedCleanup == true;
            }
        }
    }

    public HotkeyCaptureResult? LastCapturedResult
    {
        get
        {
            lock (_gate)
            {
                return _activeContext?.CapturedResult ?? _lastCapturedResult;
            }
        }
    }

    public string? ActiveOwner
    {
        get
        {
            lock (_gate)
            {
                return _activeContext?.Owner;
            }
        }
    }

    public async Task<HotkeyCaptureResult> RecordAsync(
        string owner,
        nint windowHandle,
        Action<HotkeyCaptureProgress>? onProgress,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        if (windowHandle == 0)
        {
            throw new ArgumentException("Window handle must not be zero.", nameof(windowHandle));
        }

        ActiveRecordingContext context;
        lock (_gate)
        {
            if (_isStopped || _preparationPauses != 0)
            {
                throw new InvalidOperationException("HotkeyRecordingCoordinator is stopped.");
            }
            if (_activeContext != null && !_activeContext.IsCompleted)
            {
                throw new InvalidOperationException("An active recording session is already in progress.");
            }

            context = new ActiveRecordingContext(owner, cancellationToken);
            _activeContext = context;
        }

        IHotkeyPauseLease? pauseLease = null;
        IHotkeyCaptureSession? captureSession = null;
        HotkeyCaptureResult? result = null;
        Exception? cleanupException = null;

        try
        {
            // 步骤 1: 取得唯一暂停资格并暂停全局热键
            if (_hotkeyCoordinator != null)
            {
                pauseLease = await _hotkeyCoordinator.PauseAsync(owner, context.LinkedToken).ConfigureAwait(false);
                context.SetPauseLease(pauseLease);
            }

            // 步骤 2: 启动底层键盘捕获会话
            if (_captureService == null)
            {
                throw new InvalidOperationException("IHotkeyCaptureService is not available.");
            }

            captureSession = await _captureService.BeginAsync(windowHandle, context.LinkedToken).ConfigureAwait(false);
            context.SetCaptureSession(captureSession);

            if (onProgress != null)
            {
                captureSession.ProgressChanged += (_, _) =>
                {
                    if (!context.IsCompleted)
                    {
                        onProgress(captureSession.Current);
                    }
                };
            }

            // 步骤 3: 等待逻辑结果完成
            result = await captureSession.Completion.ConfigureAwait(false);
            _lastCapturedResult = result;
            context.SetResult(result);
        }
        finally
        {
            // 步骤 4: 结束先清理捕获，再释放暂停租约按最新持久目标恢复
            if (captureSession != null)
            {
                try
                {
                    await captureSession.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    cleanupException = ex;
                }
            }
            else if (_captureService != null)
            {
                // Begin 失败且未返回 session 时，仍调用并等待服务 StopAsync 的真实回滚
                try
                {
                    await _captureService.StopAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    cleanupException = ex;
                }
            }

            if (cleanupException == null && pauseLease != null)
            {
                try
                {
                    await pauseLease.DisposeAsync().ConfigureAwait(false);
                    pauseLease = null;
                }
                catch (Exception ex)
                {
                    cleanupException = ex;
                }
            }

            if (cleanupException == null)
            {
                lock (_gate)
                {
                    if (ReferenceEquals(_activeContext, context))
                    {
                        _activeContext = null;
                    }
                }

                context.MarkCompleted();
            }
            else
            {
                context.SetCleanupFailed(cleanupException);
            }
        }

        if (cleanupException != null)
        {
            throw new InvalidOperationException("结束录入清理失败", cleanupException);
        }

        return result!;
    }

    private Task<HotkeyCaptureResult?>? _retryCleanupTask;

    public Task<HotkeyCaptureResult?> RetryCleanupAsync()
    {
        lock (_gate)
        {
            if (_retryCleanupTask != null && !_retryCleanupTask.IsCompleted)
            {
                return _retryCleanupTask;
            }

            var task = PerformRetryCleanupAsync();
            _retryCleanupTask = task;
            return task;
        }
    }

    private async Task<HotkeyCaptureResult?> PerformRetryCleanupAsync()
    {
        ActiveRecordingContext? context;
        lock (_gate)
        {
            context = _activeContext;
        }

        if (context == null || !context.HasFailedCleanup)
        {
            return null;
        }

        context.ResetForRetry();

        Exception? cleanupException = null;
        if (context.CaptureSession != null)
        {
            try
            {
                await context.CaptureSession.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                cleanupException = ex;
            }
        }
        else if (_captureService != null)
        {
            try
            {
                await _captureService.StopAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                cleanupException = ex;
            }
        }

        if (cleanupException == null && context.PauseLease != null)
        {
            try
            {
                await context.PauseLease.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                cleanupException = ex;
            }
        }

        if (cleanupException == null)
        {
            var res = context.DiscardResult ? null : context.CapturedResult;
            lock (_gate)
            {
                if (ReferenceEquals(_activeContext, context))
                {
                    _activeContext = null;
                }
            }

            context.MarkCompleted();
            return res;
        }
        else
        {
            context.SetCleanupFailed(cleanupException);
            throw new InvalidOperationException("重试结束录入清理失败", cleanupException);
        }
    }

    public async Task CancelRecordingAsync()
    {
        ActiveRecordingContext? context;
        lock (_gate)
        {
            context = _activeContext;
        }

        if (context != null)
        {
            context.Cancel();
            await context.CompletionTask.ConfigureAwait(false);
        }
    }

    public async Task StopAsync()
    {
        lock (_gate)
        {
            _isStopped = true;
        }

        await CancelRecordingAsync().ConfigureAwait(false);

        if (_captureService != null)
        {
            await _captureService.StopAsync().ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
    }

    private sealed class ActiveRecordingContext
    {
        private readonly CancellationTokenSource _linkedCts;
        private TaskCompletionSource _completionTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _completed;

        public string Owner { get; }
        public CancellationToken LinkedToken => _linkedCts.Token;
        public IHotkeyPauseLease? PauseLease { get; private set; }
        public IHotkeyCaptureSession? CaptureSession { get; private set; }
        public HotkeyCaptureResult? CapturedResult { get; private set; }
        public bool HasFailedCleanup { get; private set; }
        internal bool DiscardResult { get; set; }
        public Exception? CleanupException { get; private set; }

        public bool IsCompleted => Volatile.Read(ref _completed) != 0;
        public Task CompletionTask => _completionTcs.Task;

        public ActiveRecordingContext(string owner, CancellationToken externalToken)
        {
            Owner = owner;
            _linkedCts = CancellationTokenSource.CreateLinkedTokenSource(externalToken);
        }

        public void SetPauseLease(IHotkeyPauseLease lease) => PauseLease = lease;
        public void SetCaptureSession(IHotkeyCaptureSession session) => CaptureSession = session;
        public void SetResult(HotkeyCaptureResult result) => CapturedResult = result;

        public void SetCleanupFailed(Exception ex)
        {
            HasFailedCleanup = true;
            CleanupException = ex;
            _completionTcs.TrySetException(ex);
        }

        public void ResetForRetry()
        {
            _completionTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public void Cancel()
        {
            try
            {
                _linkedCts.Cancel();
            }
            catch
            {
            }
        }

        public void MarkCompleted()
        {
            HasFailedCleanup = false;
            CleanupException = null;
            Interlocked.Exchange(ref _completed, 1);
            _completionTcs.TrySetResult();
        }
    }
}
