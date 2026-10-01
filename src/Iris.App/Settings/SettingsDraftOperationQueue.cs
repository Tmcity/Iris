using System;
using System.Threading;
using System.Threading.Tasks;

namespace Iris.App.Settings;

/// <summary>
/// 管理设置草稿操作队列：每窗口最多一个实际运行操作、一个最新等待请求。
/// 新请求取消旧请求并等待退出；被替换的等待项以取消完成，不累积无限队列。
/// 完整规则见 specs/PROVIDER_SETTINGS_UI.md §7。
/// </summary>
internal sealed class SettingsDraftOperationQueue : IDisposable
{
    private sealed record WaitingItem(
        Func<CancellationToken, Task> Work,
        TaskCompletionSource Tcs,
        CancellationTokenSource Cts);

    private readonly Lock _lock = new();
    private Task? _runningTask;
    private WaitingItem? _runningItem;
    private WaitingItem? _waitingItem;
    private bool _isDisposed;
    private bool _isStopped;
    private int _currentLoopId;

    public bool HasActiveWork
    {
        get
        {
            lock (_lock)
            {
                return _runningTask != null || _runningItem != null || _waitingItem != null;
            }
        }
    }

    public Task EnqueueAsync(Func<CancellationToken, Task> work, CancellationToken callerCt = default)
    {
        ArgumentNullException.ThrowIfNull(work);

        WaitingItem? oldWaiting = null;
        CancellationTokenSource? ctsToCancel = null;
        WaitingItem newWaiting;
        bool shouldStart = false;
        TaskCompletionSource? loopTcs = null;
        int loopId = 0;

        lock (_lock)
        {
            if (_isDisposed || _isStopped)
            {
                throw new OperationCanceledException("操作队列已停止接纳新请求");
            }

            // 1. 如果已有等待项，它被最新的请求替换，旧等待项以取消完成
            if (_waitingItem != null)
            {
                oldWaiting = _waitingItem;
                _waitingItem = null;
            }

            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var linkedCts = callerCt.CanBeCanceled
                ? CancellationTokenSource.CreateLinkedTokenSource(callerCt)
                : new CancellationTokenSource();

            newWaiting = new WaitingItem(work, tcs, linkedCts);

            // 2. 如果当前有正在运行的任务，请求取消当前运行任务，并将新请求存为唯一等待项
            if (_runningItem != null)
            {
                _waitingItem = newWaiting;
                ctsToCancel = _runningItem.Cts;
            }
            else
            {
                _runningItem = newWaiting;
                loopTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _runningTask = loopTcs.Task;
                loopId = ++_currentLoopId;
                shouldStart = true;
            }
        }

        // 委托与取消回调必须在锁外执行
        if (oldWaiting != null)
        {
            try { oldWaiting.Cts.Cancel(); } catch { }
            oldWaiting.Tcs.TrySetCanceled(oldWaiting.Cts.Token);
            try { oldWaiting.Cts.Dispose(); } catch { }
        }

        if (ctsToCancel != null)
        {
            try { ctsToCancel.Cancel(); } catch { }
        }

        if (shouldStart)
        {
            _ = RunLoopAsync(newWaiting, loopTcs!, loopId);
        }

        return newWaiting.Tcs.Task;
    }

    private async Task RunLoopAsync(WaitingItem firstItem, TaskCompletionSource loopTcs, int loopId)
    {
        try
        {
            var currentItem = firstItem;
            while (currentItem != null)
            {
                try
                {
                    currentItem.Cts.Token.ThrowIfCancellationRequested();
                    await currentItem.Work(currentItem.Cts.Token).ConfigureAwait(false);
                    currentItem.Tcs.TrySetResult();
                }
                catch (OperationCanceledException)
                {
                    currentItem.Tcs.TrySetCanceled(currentItem.Cts.Token);
                }
                catch (Exception ex)
                {
                    currentItem.Tcs.TrySetException(ex);
                }
                finally
                {
                    try { currentItem.Cts.Dispose(); } catch { }
                }

                WaitingItem? nextItem = null;
                WaitingItem? droppedItem = null;

                lock (_lock)
                {
                    if (_waitingItem != null && !_isDisposed && !_isStopped)
                    {
                        nextItem = _waitingItem;
                        _waitingItem = null;
                        _runningItem = nextItem;
                    }
                    else
                    {
                        _runningItem = null;
                        if (_waitingItem != null)
                        {
                            droppedItem = _waitingItem;
                            _waitingItem = null;
                        }
                    }
                }

                if (droppedItem != null)
                {
                    try { droppedItem.Cts.Cancel(); } catch { }
                    droppedItem.Tcs.TrySetCanceled(droppedItem.Cts.Token);
                    try { droppedItem.Cts.Dispose(); } catch { }
                }

                currentItem = nextItem;
            }
        }
        finally
        {
            lock (_lock)
            {
                if (_currentLoopId == loopId)
                {
                    _runningItem = null;
                    _runningTask = null;
                }
            }
            loopTcs.TrySetResult();
        }
    }

    public void CancelInFlight()
    {
        WaitingItem? waitingToCancel;
        CancellationTokenSource? ctsToCancel;

        lock (_lock)
        {
            waitingToCancel = _waitingItem;
            _waitingItem = null;
            ctsToCancel = _runningItem?.Cts;
        }

        if (waitingToCancel != null)
        {
            try { waitingToCancel.Cts.Cancel(); } catch { }
            waitingToCancel.Tcs.TrySetCanceled(waitingToCancel.Cts.Token);
            try { waitingToCancel.Cts.Dispose(); } catch { }
        }

        if (ctsToCancel != null)
        {
            try { ctsToCancel.Cancel(); } catch { }
        }
    }

    public async Task DrainAndCancelInFlightAsync()
    {
        WaitingItem? waitingToCancel;
        Task? taskToWait;
        CancellationTokenSource? ctsToCancel;

        lock (_lock)
        {
            waitingToCancel = _waitingItem;
            _waitingItem = null;
            ctsToCancel = _runningItem?.Cts;
            taskToWait = _runningTask;
        }

        if (waitingToCancel != null)
        {
            try { waitingToCancel.Cts.Cancel(); } catch { }
            waitingToCancel.Tcs.TrySetCanceled(waitingToCancel.Cts.Token);
            try { waitingToCancel.Cts.Dispose(); } catch { }
        }

        if (ctsToCancel != null)
        {
            try { ctsToCancel.Cancel(); } catch { }
        }

        if (taskToWait != null)
        {
            try
            {
                await taskToWait.ConfigureAwait(false);
            }
            catch
            {
            }
        }
    }

    public async Task DrainAndCancelAllAsync()
    {
        lock (_lock)
        {
            _isStopped = true;
        }

        await DrainAndCancelInFlightAsync().ConfigureAwait(false);
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_isDisposed)
            {
                return;
            }
            _isDisposed = true;
            _isStopped = true;
        }

        CancelInFlight();
    }
}
