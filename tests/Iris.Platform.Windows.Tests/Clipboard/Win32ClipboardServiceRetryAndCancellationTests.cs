using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Iris.Core.Errors;
using Iris.Platform.Windows.Clipboard;
using Iris.Platform.Windows.Tests.Clipboard.TestSupport;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;

namespace Iris.Platform.Windows.Tests.Clipboard;

public sealed class Win32ClipboardServiceRetryAndCancellationTests
{
    [Fact]
    public async Task OpenClipboard_FirstAttemptSucceeds_OpensAndClosesOnce()
    {
        var adapter = new FakeNativeClipboardAdapter();
        var service = new Win32ClipboardService(null, null, adapter);

        await service.SetTextAsync("Test", TestContext.Current.CancellationToken);

        adapter.CallLog.Count(x => x.StartsWith("OpenClipboard", StringComparison.Ordinal)).ShouldBe(1);
        adapter.CallLog.Count(x => x == "CloseClipboard").ShouldBe(1);
    }

    [Fact]
    public async Task OpenClipboard_Attempts1To9Fail_Attempt10Succeeds_WithNineDelays()
    {
        var adapter = new FakeNativeClipboardAdapter();
        int openAttempts = 0;
        adapter.OnOpenClipboard = _ =>
        {
            openAttempts++;
            return openAttempts >= 10;
        };

        var timeProvider = new ControllableTimeProvider();
        var service = new Win32ClipboardService(timeProvider, null, adapter);

        var task = service.SetTextAsync("RetryTest", TestContext.Current.CancellationToken);

        await AdvanceDelaysAsync(timeProvider, 9);
        await task;

        openAttempts.ShouldBe(10);
        adapter.CallLog.Count(x => x.StartsWith("OpenClipboard", StringComparison.Ordinal)).ShouldBe(10);
        adapter.CallLog.Count(x => x == "CloseClipboard").ShouldBe(1);
    }

    [Fact]
    public async Task OpenClipboard_All10AttemptsFail_ThrowsClipboardBusy_WithoutSleepingAfterTenth()
    {
        var adapter = new FakeNativeClipboardAdapter();
        int openAttempts = 0;
        adapter.OnOpenClipboard = _ =>
        {
            openAttempts++;
            return false;
        };

        var timeProvider = new ControllableTimeProvider();
        var service = new Win32ClipboardService(timeProvider, null, adapter);

        var task = service.SetTextAsync("BusyTest", TestContext.Current.CancellationToken);

        await AdvanceDelaysAsync(timeProvider, 9);

        var ex = await Should.ThrowAsync<IrisException>(() => task);
        ex.Code.ShouldBe(ErrorCode.ClipboardBusy);

        openAttempts.ShouldBe(10);
        adapter.CallLog.Count(x => x.StartsWith("OpenClipboard", StringComparison.Ordinal)).ShouldBe(10);
        adapter.CallLog.Count(x => x == "CloseClipboard").ShouldBe(0, "失败的 Open 严禁调用 CloseClipboard");
    }

    [Fact]
    public async Task OpenClipboard_CancelledDuringRetryDelay_ThrowsOperationCanceledException()
    {
        var adapter = new FakeNativeClipboardAdapter();
        adapter.OnOpenClipboard = _ => false;

        using var cts = new CancellationTokenSource();
        var timeProvider = new ControllableTimeProvider();
        var service = new Win32ClipboardService(timeProvider, null, adapter);

        var task = service.SetTextAsync("CancelTest", cts.Token);

        // Wait deterministically until Task.Delay registers its timer
        await timeProvider.WaitForNextTimerAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        // Cancel during first delay
        cts.Cancel();

        await Should.ThrowAsync<OperationCanceledException>(() => task);
        adapter.CallLog.Count(x => x.StartsWith("OpenClipboard", StringComparison.Ordinal)).ShouldBe(1);
        adapter.CallLog.Count(x => x == "CloseClipboard").ShouldBe(0);
    }

    [Fact]
    public async Task OpenClipboard_CancellationTakesPrecedenceOverClipboardBusy_OnExhaustion()
    {
        var adapter = new FakeNativeClipboardAdapter();
        int openAttempts = 0;
        using var cts = new CancellationTokenSource();

        adapter.OnOpenClipboard = _ =>
        {
            openAttempts++;
            if (openAttempts == 10)
            {
                // Trigger cancellation right as 10th attempt fails
                cts.Cancel();
            }
            return false;
        };

        var timeProvider = new ControllableTimeProvider();
        var service = new Win32ClipboardService(timeProvider, null, adapter);

        var task = service.SetTextAsync("RaceCancelTest", cts.Token);

        await AdvanceDelaysAsync(timeProvider, 9);

        await Should.ThrowAsync<OperationCanceledException>(() => task);
        openAttempts.ShouldBe(10);
    }

    [Fact]
    public async Task CommitPoint_CancellationBeforeEmpty_DoesNotCallEmptyOrSet_AndFreesMemory()
    {
        var adapter = new FakeNativeClipboardAdapter();
        using var cts = new CancellationTokenSource();

        // Right after OpenClipboard succeeds, cancel token before EmptyClipboard is called
        adapter.OnOpenClipboard = _ =>
        {
            cts.Cancel();
            return true;
        };

        var service = new Win32ClipboardService(null, null, adapter);

        var ex = await Should.ThrowAsync<OperationCanceledException>(() =>
            service.SetTextAsync("CommitPointCancel", cts.Token));

        adapter.ClipboardFormats.ContainsKey(DibV5Helper.CF_UNICODETEXT).ShouldBeFalse("取消后严禁写入数据");
        adapter.CallLog.ShouldNotContain("EmptyClipboard");
        adapter.CallLog.ShouldNotContain("SetClipboardData");
        adapter.CallLog.ShouldContain("CloseClipboard");

        // Assert local memory was freed
        adapter.AllocatedHGlobals.Count.ShouldBe(1);
        adapter.FreedHGlobals.ShouldContain(adapter.AllocatedHGlobals[0], "未进入提交阶段取消时，本地必须释放 HGLOBAL");
    }

    private static async Task AdvanceDelaysAsync(ControllableTimeProvider timeProvider, int delayCount)
    {
        for (int i = 1; i <= delayCount; i++)
        {
            await timeProvider.WaitForNextTimerAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            timeProvider.Advance(TimeSpan.FromMilliseconds(30));
        }
    }

    [Fact]
    public async Task CommitPoint_LateCancellationAfterEmpty_CompletesSetAndClose_WithoutThrowingOCE()
    {
        var adapter = new FakeNativeClipboardAdapter();
        using var cts = new CancellationTokenSource();

        // After EmptyClipboard succeeds, cancel token right before SetClipboardData
        adapter.OnBeforeSetClipboardData = () =>
        {
            cts.Cancel();
        };

        var service = new Win32ClipboardService(null, null, adapter);

        // Must NOT throw OperationCanceledException! Must complete write!
        await service.SetTextAsync("LateCancelText", cts.Token);

        adapter.ClipboardFormats.ContainsKey(DibV5Helper.CF_UNICODETEXT).ShouldBeTrue("提交点后迟到取消必须完成 Set");
        adapter.CallLog.ShouldContain("CloseClipboard");

        // Assert local memory was NOT freed because system took ownership
        adapter.AllocatedHGlobals.Count.ShouldBe(1);
        adapter.FreedHGlobals.ShouldNotContain(adapter.AllocatedHGlobals[0], "系统接管的 HGLOBAL 本地严禁 GlobalFree");
    }

    private sealed class ControllableTimeProvider : TimeProvider, IDisposable
    {
        private readonly FakeTimeProvider _fake = new();
        private readonly SemaphoreSlim _timerSemaphore = new(0);

        public async Task WaitForNextTimerAsync(TimeSpan timeout, CancellationToken cancellationToken)
        {
            bool acquired = await _timerSemaphore.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
            acquired.ShouldBeTrue("Timer must be registered within timeout.");
        }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = _fake.CreateTimer(callback, state, dueTime, period);
            _timerSemaphore.Release();
            return timer;
        }

        public void Advance(TimeSpan delta) => _fake.Advance(delta);
        public override DateTimeOffset GetUtcNow() => _fake.GetUtcNow();
        public override long GetTimestamp() => _fake.GetTimestamp();

        public void Dispose()
        {
            _timerSemaphore.Dispose();
        }
    }
}
