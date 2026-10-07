using System;
using System.Collections.Generic;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using Iris.App.Settings;
using Shouldly;
using Xunit;

namespace Iris.App.Tests.Settings;

public sealed class SettingsDraftOperationQueueTests
{
    [Fact]
    public async Task EnqueueAsync_WhenNewWorkArrives_ReplacesWaitingWorkAndCancelsIt()
    {
        var queue = new SettingsDraftOperationQueue();

        var op1Started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var op1Release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken op1Token = default;

        var op2Executed = false;
        var op3Executed = false;

        Task? t1 = null, t2 = null, t3 = null, drainTask = null;
        Exception? primary = null;
        var cleanupErrors = new List<Exception>();
        try
        {
            t1 = queue.EnqueueAsync(async ct =>
            {
                op1Token = ct;
                op1Started.TrySetResult();
                // 取消只提出退出请求；显式放行前仍占有运行槽位。
                await op1Release.Task;
            }, TestContext.Current.CancellationToken);

            await op1Started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            t1.IsCompleted.ShouldBeFalse();

            t2 = queue.EnqueueAsync(ct =>
            {
                ct.ThrowIfCancellationRequested();
                op2Executed = true;
                return Task.CompletedTask;
            }, TestContext.Current.CancellationToken);
            op1Token.IsCancellationRequested.ShouldBeTrue();
            t2.IsCompleted.ShouldBeFalse();

            t3 = queue.EnqueueAsync(ct =>
            {
                op3Executed = true;
                return Task.CompletedTask;
            }, TestContext.Current.CancellationToken);

            // 不放行首项也必须完成等待项替换，最新项仍不能执行。
            t2.IsCanceled.ShouldBeTrue();
            await Should.ThrowAsync<OperationCanceledException>(async () => await t2);
            op2Executed.ShouldBeFalse();
            t1.IsCompleted.ShouldBeFalse();
            t3.IsCompleted.ShouldBeFalse();
            op3Executed.ShouldBeFalse();
            queue.HasActiveWork.ShouldBeTrue();

            op1Release.TrySetResult();
            await Task.WhenAll(t1, t3).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            // 首项委托故意不抛取消，放行后应正常完成。
            t1.IsCompletedSuccessfully.ShouldBeTrue();
            t3.IsCompletedSuccessfully.ShouldBeTrue();
            op2Executed.ShouldBeFalse();
            op3Executed.ShouldBeTrue();

            drainTask = queue.DrainAndCancelAllAsync();
            await drainTask.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            queue.HasActiveWork.ShouldBeFalse();
        }
        catch (Exception ex) { primary = ex; }
        finally
        {
            op1Release.TrySetResult();
            foreach (var task in new[] { t1, t2, t3, drainTask })
            {
                if (task == null) continue;
                try { await task; }
                catch (OperationCanceledException) when (task.IsCanceled) { }
                catch (Exception ex) { cleanupErrors.Add(ex); }
            }
            try { await queue.DrainAndCancelAllAsync(); } catch (Exception ex) { cleanupErrors.Add(ex); }
            try { queue.Dispose(); } catch (Exception ex) { cleanupErrors.Add(ex); }
        }
        if (primary != null && cleanupErrors.Count == 0) ExceptionDispatchInfo.Capture(primary).Throw();
        if (primary != null) cleanupErrors.Insert(0, primary);
        if (cleanupErrors.Count != 0) throw new AggregateException(cleanupErrors);
    }

    [Fact]
    public async Task DrainAndCancelAllAsync_AwaitsRunningTaskAndCancelsWaitingQueue()
    {
        var queue = new SettingsDraftOperationQueue();

        var op1Started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var op1Release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken op1Token = default;

        var op2Started = false;

        Task? t1 = null, t2 = null, drainTask = null;
        Exception? primary = null;
        var cleanupErrors = new List<Exception>();
        try
        {
            t1 = queue.EnqueueAsync(async ct =>
            {
                op1Token = ct;
                op1Started.TrySetResult();
                // 收到取消后仍等待门闩，才能证明排空等待真实退出。
                await op1Release.Task;
            }, TestContext.Current.CancellationToken);

            await op1Started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            t1.IsCompleted.ShouldBeFalse();

            t2 = queue.EnqueueAsync(ct =>
            {
                op2Started = true;
                return Task.CompletedTask;
            }, TestContext.Current.CancellationToken);
            t2.IsCompleted.ShouldBeFalse();

            drainTask = queue.DrainAndCancelAllAsync();

            op1Token.IsCancellationRequested.ShouldBeTrue();
            drainTask.IsCompleted.ShouldBeFalse();
            t1.IsCompleted.ShouldBeFalse();
            t2.IsCanceled.ShouldBeTrue();
            await Should.ThrowAsync<OperationCanceledException>(async () => await t2);
            op2Started.ShouldBeFalse();
            queue.HasActiveWork.ShouldBeTrue();

            op1Release.TrySetResult();
            await Task.WhenAll(t1, drainTask).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            t1.IsCompletedSuccessfully.ShouldBeTrue();
            op2Started.ShouldBeFalse();
            queue.HasActiveWork.ShouldBeFalse();
        }
        catch (Exception ex) { primary = ex; }
        finally
        {
            op1Release.TrySetResult();
            foreach (var task in new[] { t1, t2, drainTask })
            {
                if (task == null) continue;
                try { await task; }
                catch (OperationCanceledException) when (task.IsCanceled) { }
                catch (Exception ex) { cleanupErrors.Add(ex); }
            }
            try { await queue.DrainAndCancelAllAsync(); } catch (Exception ex) { cleanupErrors.Add(ex); }
            try { queue.Dispose(); } catch (Exception ex) { cleanupErrors.Add(ex); }
        }
        if (primary != null && cleanupErrors.Count == 0) ExceptionDispatchInfo.Capture(primary).Throw();
        if (primary != null) cleanupErrors.Insert(0, primary);
        if (cleanupErrors.Count != 0) throw new AggregateException(cleanupErrors);
    }
}
