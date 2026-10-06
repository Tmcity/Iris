using System;
using System.Threading;
using System.Threading.Tasks;
using Iris.Core.Presentation;

namespace Iris.App.Translation;

/// <summary>
/// 翻译窗口操作租约。
/// 租约具有独立 Id 与取消令牌，维护调用方持有、TryShow 移交、窗口活跃会话与后台工作所有权。
/// 完整所有权与回收规则见 TRANSLATION_WINDOW.md §2 与 CCR-013。
/// </summary>
internal sealed class TranslateWindowOperation : ITranslateWindowOperation
{
    private readonly TranslateWindowPresenter _presenter;
    private readonly CancellationTokenSource _cts = new();
    private readonly Lock _gate = new();
    private readonly CancellationTokenRegistration _callerReg;
    private readonly CancellationToken _callerToken;

    private bool _hasCallerOwnership = true;
    private bool _hasTryShowOwnership;
    private bool _hasSessionOwnership;
    private int _activeWorkCount;
    private int _activeCancelCallbacks;
    private int _autoTranslateClaimed;
    private int _autoCopySourceClaimed;
    private int _ocrFallbackClaimed;
    private bool _isRetired;
    private Task? _cancelTask;

    public TranslateWindowOperation(TranslateWindowPresenter presenter, CancellationToken callerToken)
    {
        _presenter = presenter ?? throw new ArgumentNullException(nameof(presenter));
        Id = Guid.NewGuid();
        _callerToken = callerToken;

        if (callerToken.CanBeCanceled)
        {
            _callerReg = callerToken.Register(OnCallerTokenCanceled);
        }
    }

    public Guid Id { get; }

    public CancellationToken CancellationToken => _cts.Token;

    internal TranslateWindowPresenter Presenter => _presenter;

    // Cancellation callbacks run in reverse registration order. The original caller
    // can already be canceled while our forwarding callback has not run yet.
    internal bool IsCancellationRequested => _callerToken.IsCancellationRequested || _cts.IsCancellationRequested;

    internal bool TryClaimAutoCopySource() => Interlocked.CompareExchange(ref _autoCopySourceClaimed, 1, 0) == 0;
    internal bool TryClaimOcrFallback() => Interlocked.CompareExchange(ref _ocrFallbackClaimed, 1, 0) == 0;

    internal bool IsCallerDisposed
    {
        get
        {
            lock (_gate)
            {
                return !_hasCallerOwnership;
            }
        }
    }

    internal bool HasInFlightTryShow
    {
        get
        {
            lock (_gate)
            {
                return _hasTryShowOwnership;
            }
        }
    }

    internal bool HasSessionOwnership
    {
        get
        {
            lock (_gate)
            {
                return _hasSessionOwnership;
            }
        }
    }

    internal bool HasWorkOwnership
    {
        get
        {
            lock (_gate)
            {
                return _hasSessionOwnership || _activeWorkCount > 0;
            }
        }
    }

    internal int ActiveWorkCount
    {
        get
        {
            lock (_gate)
            {
                return _activeWorkCount;
            }
        }
    }

    internal int ActiveCancelCallbacks
    {
        get
        {
            lock (_gate)
            {
                return _activeCancelCallbacks;
            }
        }
    }

    internal bool IsRetired
    {
        get
        {
            lock (_gate)
            {
                return _isRetired;
            }
        }
    }

    internal Task? CancelTask
    {
        get
        {
            lock (_gate)
            {
                return _cancelTask;
            }
        }
    }

    internal bool TryClaimAutoTranslate()
    {
        return Interlocked.CompareExchange(ref _autoTranslateClaimed, 1, 0) == 0;
    }

    internal bool BeginTryShow()
    {
        lock (_gate)
        {
            if (_isRetired || !_hasCallerOwnership || IsCancellationRequested)
            {
                return false;
            }

            if (_hasTryShowOwnership)
            {
                throw new InvalidOperationException("同一操作已有在途的发布正在执行。");
            }

            _hasTryShowOwnership = true;
            return true;
        }
    }

    internal void EndTryShow(bool transferredWorkOwnership = false)
    {
        bool shouldRetire = false;
        lock (_gate)
        {
            _hasTryShowOwnership = false;
            if (transferredWorkOwnership && !_isRetired && !IsCancellationRequested)
            {
                _hasSessionOwnership = true;
            }
            else
            {
                _hasSessionOwnership = false;
            }
            shouldRetire = TryMarkRetiredLocked();
        }

        if (shouldRetire)
        {
            FinalizeRetire();
        }
    }

    internal void EndTryNotify()
    {
        bool shouldRetire;
        lock (_gate)
        {
            _hasTryShowOwnership = false;
            // Notify did not acquire or replace the VM's session ownership.
            shouldRetire = TryMarkRetiredLocked();
        }
        if (shouldRetire) FinalizeRetire();
    }

    internal bool AttachSession()
    {
        lock (_gate)
        {
            if (_isRetired || IsCancellationRequested)
            {
                return false;
            }
            _hasSessionOwnership = true;
            return true;
        }
    }

    internal void DetachSession()
    {
        bool shouldRetire = false;
        lock (_gate)
        {
            if (!_hasSessionOwnership)
            {
                return;
            }

            _hasSessionOwnership = false;
            shouldRetire = TryMarkRetiredLocked();
        }

        if (shouldRetire)
        {
            FinalizeRetire();
        }
    }

    internal void EndWorkOwnership()
    {
        bool shouldRetire = false;
        lock (_gate)
        {
            _hasSessionOwnership = false;
            _activeWorkCount = 0;
            shouldRetire = TryMarkRetiredLocked();
        }

        if (shouldRetire)
        {
            FinalizeRetire();
        }
    }

    internal bool IncrementWork()
    {
        lock (_gate)
        {
            if (_isRetired)
            {
                return false;
            }
            _activeWorkCount++;
            return true;
        }
    }

    internal void DecrementWork()
    {
        bool shouldRetire = false;
        lock (_gate)
        {
            _activeWorkCount--;
            shouldRetire = TryMarkRetiredLocked();
        }

        if (shouldRetire)
        {
            FinalizeRetire();
        }
    }

    public void Dispose()
    {
        bool shouldRetire = false;
        lock (_gate)
        {
            if (!_hasCallerOwnership)
            {
                return;
            }

            _hasCallerOwnership = false;
            shouldRetire = TryMarkRetiredLocked();
        }

        if (shouldRetire)
        {
            FinalizeRetire();
        }
    }

    internal void Cancel()
    {
        _ = CancelAsync();
    }

    internal Task CancelAsync()
    {
        Task? taskToRun = null;
        lock (_gate)
        {
            if (_isRetired)
            {
                return Task.CompletedTask;
            }

            if (_cancelTask != null)
            {
                return _cancelTask;
            }

            _activeCancelCallbacks++;
            taskToRun = ExecuteCancellationCoreAsync();
            _cancelTask = taskToRun;
        }

        return taskToRun;
    }

    private async Task ExecuteCancellationCoreAsync()
    {
        try
        {
            await _cts.CancelAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _presenter.LogOperationCancelError(ex.GetType().Name);
        }
        finally
        {
            bool shouldRetire = false;
            lock (_gate)
            {
                _activeCancelCallbacks--;
                shouldRetire = TryMarkRetiredLocked();
            }

            if (shouldRetire)
            {
                FinalizeRetire();
            }
        }
    }

    private void OnCallerTokenCanceled()
    {
        _ = CancelAsync();
    }

    private bool TryMarkRetiredLocked()
    {
        if (_isRetired)
        {
            return false;
        }

        if (!_hasCallerOwnership
            && !_hasTryShowOwnership
            && !_hasSessionOwnership
            && _activeWorkCount <= 0
            && _activeCancelCallbacks <= 0)
        {
            _isRetired = true;
            return true;
        }

        return false;
    }

    private Task? _retireTask;

    private void FinalizeRetire()
    {
        lock (_gate)
        {
            if (_retireTask != null)
            {
                return;
            }

            try
            {
                var vt = _callerReg.DisposeAsync();
                if (vt.IsCompletedSuccessfully)
                {
                    _presenter.OnOperationRetired(this);
                    try
                    {
                        _cts.Dispose();
                    }
                    catch
                    {
                    }
                    _retireTask = Task.CompletedTask;
                    return;
                }

                _retireTask = CompleteRetireAsync(vt.AsTask());
            }
            catch (Exception ex)
            {
                _presenter.LogOperationCancelError(ex.GetType().Name);
                _presenter.OnOperationRetired(this);
                try
                {
                    _cts.Dispose();
                }
                catch
                {
                }
                _retireTask = Task.CompletedTask;
            }
        }
    }

    private async Task CompleteRetireAsync(Task disposeTask)
    {
        try
        {
            await disposeTask.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _presenter.LogOperationCancelError(ex.GetType().Name);
        }
        finally
        {
            _presenter.OnOperationRetired(this);
            try
            {
                _cts.Dispose();
            }
            catch
            {
            }
        }
    }
}
