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
    private CancellationTokenSource? _runningCts;
    private WaitingItem? _waitingItem;
    private bool _isDisposed;
    private bool _isStopped;

    public bool HasActiveWork
    {
        get
        {
            lock (_lock)
            {
                return _runningTask != null || _waitingItem != null;
            }
        }
    }

    public Task EnqueueAsync(Func<CancellationToken, Task> work, CancellationToken callerCt = default)
    {
        ArgumentNullException.ThrowIfNull(work);

        WaitingItem? oldWaiting = null;
        WaitingItem newWaiting;
        CancellationTokenSource linkedCts;

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
            linkedCts = callerCt.CanBeCanceled
                ? CancellationTokenSource.CreateLinkedTokenSource(callerCt)
                : new CancellationTokenSource();

            newWaiting = new WaitingItem(work, tcs, linkedCts);

            // 2. 如果当前有正在运行的任务，请求取消当前运行任务，并将新请求存为唯一等待项
            if (_runningTask != null && !_runningTask.IsCompleted)
            {
                _waitingItem = newWaiting;
                try
                {
                    _runningCts?.Cancel();
                }
                catch (ObjectDisposedException)
                {
                }
            }
            else
            {
                StartRunning_Locked(newWaiting);
            }
        }

        if (oldWaiting != null)
        {
            try { oldWaiting.Cts.Cancel(); } catch { }
            oldWaiting.Tcs.TrySetCanceled(oldWaiting.Cts.Token);
            oldWaiting.Cts.Dispose();
        }

        return newWaiting.Tcs.Task;
    }

    private void StartRunning_Locked(WaitingItem item)
    {
        _runningCts = item.Cts;
        _runningTask = RunWorkAsync(item);
    }

    private async Task RunWorkAsync(WaitingItem item)
    {
        try
        {
            item.Cts.Token.ThrowIfCancellationRequested();
            await item.Work(item.Cts.Token).ConfigureAwait(false);
            item.Tcs.TrySetResult();
        }
        catch (OperationCanceledException)
        {
            item.Tcs.TrySetCanceled(item.Cts.Token);
        }
        catch (Exception ex)
        {
            item.Tcs.TrySetException(ex);
        }
        finally
        {
            WaitingItem? nextToRun = null;
            lock (_lock)
            {
                if (ReferenceEquals(_runningCts, item.Cts))
                {
                    _runningCts = null;
                }

                if (_waitingItem != null)
                {
                    if (!_isDisposed && !_isStopped)
                    {
                        nextToRun = _waitingItem;
                        _waitingItem = null;
                        StartRunning_Locked(nextToRun);
                    }
                    else
                    {
                        var dropped = _waitingItem;
                        _waitingItem = null;
                        try { dropped.Cts.Cancel(); } catch { }
                        dropped.Tcs.TrySetCanceled(dropped.Cts.Token);
                        dropped.Cts.Dispose();
                    }
                }
                else
                {
                    _runningTask = null;
                }
            }

            try
            {
                item.Cts.Dispose();
            }
            catch
            {
            }
        }
    }

    public async Task DrainAndCancelAllAsync()
    {
        WaitingItem? waitingToCancel;
        Task? taskToWait;
        CancellationTokenSource? ctsToCancel;

        lock (_lock)
        {
            _isStopped = true;
            waitingToCancel = _waitingItem;
            _waitingItem = null;
            ctsToCancel = _runningCts;
            taskToWait = _runningTask;
        }

        if (waitingToCancel != null)
        {
            try { waitingToCancel.Cts.Cancel(); } catch { }
            waitingToCancel.Tcs.TrySetCanceled(waitingToCancel.Cts.Token);
            waitingToCancel.Cts.Dispose();
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
        _ = DrainAndCancelAllAsync();
    }
}
