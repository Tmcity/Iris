using System;
using System.Collections.Generic;
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
        using var op1Release = new ManualResetEventSlim();

        var op2Executed = false;
        var op3Executed = false;

        // 1. 启动操作 1
        var t1 = queue.EnqueueAsync(async ct =>
        {
            op1Started.TrySetResult();
            op1Release.Wait(TimeSpan.FromSeconds(5), ct);
            await Task.Yield();
        }, TestContext.Current.CancellationToken);

        await op1Started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        // 2. 此时操作 1 正在运行。提交操作 2（进入等待槽位）
        var t2 = queue.EnqueueAsync(async ct =>
        {
            ct.ThrowIfCancellationRequested();
            op2Executed = true;
            await Task.Yield();
        }, TestContext.Current.CancellationToken);

        // 3. 紧接着提交操作 3：必须挤掉操作 2
        var t3 = queue.EnqueueAsync(async ct =>
        {
            op3Executed = true;
            await Task.Yield();
        }, TestContext.Current.CancellationToken);

        // 放行操作 1
        op1Release.Set();

        await Task.WhenAll(t1, t3).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        // 操作 2 应该被取消，不能被执行
        await Should.ThrowAsync<OperationCanceledException>(async () => await t2);
        op2Executed.ShouldBeFalse();
        op3Executed.ShouldBeTrue();
    }

    [Fact]
    public async Task DrainAndCancelAllAsync_AwaitsRunningTaskAndCancelsWaitingQueue()
    {
        var queue = new SettingsDraftOperationQueue();

        var op1Started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var op1Release = new ManualResetEventSlim();

        var op2Started = false;

        var t1 = queue.EnqueueAsync(async ct =>
        {
            op1Started.TrySetResult();
            op1Release.Wait(TimeSpan.FromSeconds(5), ct);
            await Task.Yield();
        }, TestContext.Current.CancellationToken);

        await op1Started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        var t2 = queue.EnqueueAsync(async ct =>
        {
            op2Started = true;
            await Task.Yield();
        }, TestContext.Current.CancellationToken);

        // 此时在主线程调用 DrainAndCancelAllAsync
        var drainTask = queue.DrainAndCancelAllAsync();

        // 放行操作 1
        op1Release.Set();

        await drainTask.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        // 操作 2 被丢弃，排空完成
        op2Started.ShouldBeFalse();
        queue.HasActiveWork.ShouldBeFalse();
    }
}
