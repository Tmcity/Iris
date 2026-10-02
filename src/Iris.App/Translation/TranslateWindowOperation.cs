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

    private bool _hasCallerOwnership = true;
    private bool _hasTryShowOwnership;
    private bool _hasSessionOwnership;
    private int _activeWorkCount;
    private int _activeCancelCallbacks;
    private int _autoTranslateClaimed;
    private bool _isRetired;

    public TranslateWindowOperation(TranslateWindowPresenter presenter, CancellationToken callerToken)
    {
        _presenter = presenter ?? throw new ArgumentNullException(nameof(presenter));
        Id = Guid.NewGuid();

        if (callerToken.CanBeCanceled)
        {
            _callerReg = callerToken.Register(OnCallerTokenCanceled);
        }
    }

    public Guid Id { get; }

    public CancellationToken CancellationToken => _cts.Token;

    internal TranslateWindowPresenter Presenter => _presenter;

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

    internal bool TryClaimAutoTranslate()
    {
        return Interlocked.CompareExchange(ref _autoTranslateClaimed, 1, 0) == 0;
    }

    internal bool BeginTryShow()
    {
        lock (_gate)
        {
            if (_isRetired || !_hasCallerOwnership || _cts.IsCancellationRequested)
            {
                return false;
            }

            if (_hasTryShowOwnership)
            {
                throw new InvalidOperationException("同一操作已有在途的 TryShowAsync 正在执行。");
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
            if (transferredWorkOwnership)
            {
                _hasSessionOwnership = true;
            }
            shouldRetire = CheckCanRetireLocked();
        }

        if (shouldRetire)
        {
            Retire();
        }
    }

    internal void AttachSession()
    {
        lock (_gate)
        {
            _hasSessionOwnership = true;
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
            shouldRetire = CheckCanRetireLocked();
        }

        if (shouldRetire)
        {
            Retire();
        }
    }

    internal void EndWorkOwnership()
    {
        bool shouldRetire = false;
        lock (_gate)
        {
            _hasSessionOwnership = false;
            _activeWorkCount = 0;
            shouldRetire = CheckCanRetireLocked();
        }

        if (shouldRetire)
        {
            Retire();
        }
    }

    internal void IncrementWork()
    {
        lock (_gate)
        {
            _activeWorkCount++;
        }
    }

    internal void DecrementWork()
    {
        bool shouldRetire = false;
        lock (_gate)
        {
            _activeWorkCount--;
            shouldRetire = CheckCanRetireLocked();
        }

        if (shouldRetire)
        {
            Retire();
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
            shouldRetire = CheckCanRetireLocked();
        }

        if (shouldRetire)
        {
            Retire();
        }
    }

    internal void Cancel()
    {
        try
        {
            _cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
        catch
        {
        }
    }

    private void OnCallerTokenCanceled()
    {
        lock (_gate)
        {
            _activeCancelCallbacks++;
        }

        try
        {
            Cancel();
        }
        finally
        {
            bool shouldRetire = false;
            lock (_gate)
            {
                _activeCancelCallbacks--;
                shouldRetire = CheckCanRetireLocked();
            }

            if (shouldRetire)
            {
                Retire();
            }
        }
    }

    private bool CheckCanRetireLocked()
    {
        if (_isRetired)
        {
            return false;
        }

        return !_hasCallerOwnership
            && !_hasTryShowOwnership
            && !_hasSessionOwnership
            && _activeWorkCount <= 0
            && _activeCancelCallbacks <= 0;
    }

    private void Retire()
    {
        lock (_gate)
        {
            if (_isRetired)
            {
                return;
            }
            _isRetired = true;
        }

        try
        {
            _callerReg.Dispose();
        }
        catch
        {
        }

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
