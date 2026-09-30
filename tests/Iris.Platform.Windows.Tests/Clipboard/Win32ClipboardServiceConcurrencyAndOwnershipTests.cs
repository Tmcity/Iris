using System;
using System.Threading;
using System.Threading.Tasks;
using Iris.Core.Errors;
using Iris.Platform.Windows.Clipboard;
using Iris.Platform.Windows.Tests.Clipboard.TestSupport;
using Shouldly;
using Xunit;

namespace Iris.Platform.Windows.Tests.Clipboard;

public sealed class Win32ClipboardServiceConcurrencyAndOwnershipTests
{
    [Fact]
    public async Task Concurrency_MultipleCalls_AreSerializedByGate()
    {
        var adapter = new FakeNativeClipboardAdapter();
        var latch1 = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var unblock1 = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        int concurrentNativeCount = 0;
        int maxConcurrentNativeCount = 0;

        adapter.OnOpenClipboard = _ =>
        {
            var count = Interlocked.Increment(ref concurrentNativeCount);
            if (count > maxConcurrentNativeCount) maxConcurrentNativeCount = count;
            return true;
        };

        adapter.OnBeforeSetClipboardData = () =>
        {
            if (latch1.TrySetResult())
            {
                unblock1.Task.Wait(TimeSpan.FromSeconds(5));
            }
            Interlocked.Decrement(ref concurrentNativeCount);
        };

        var service = new Win32ClipboardService(null, null, adapter);

        try
        {
            var call1 = service.SetTextAsync("First", TestContext.Current.CancellationToken);
            await latch1.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

            // Call 2 started while Call 1 is in progress
            var call2 = service.SetTextAsync("Second", TestContext.Current.CancellationToken);

            call1.IsCompleted.ShouldBeFalse();
            call2.IsCompleted.ShouldBeFalse();

            unblock1.TrySetResult();

            await Task.WhenAll(call1, call2).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

            maxConcurrentNativeCount.ShouldBe(1, "串行门必须确保同一时刻至多一个调用持有或执行原生操作");
        }
        finally
        {
            unblock1.TrySetResult();
        }
    }

    [Fact]
    public async Task Ownership_GlobalAllocFails_ThrowsStorageFailure_AndDoesNotCallOpenOrFree()
    {
        var adapter = new FakeNativeClipboardAdapter
        {
            OnGlobalAlloc = (_, _) => 0,
            LastError = 8 // ERROR_NOT_ENOUGH_MEMORY
        };

        var service = new Win32ClipboardService(null, null, adapter);

        var ex = await Should.ThrowAsync<IrisException>(() =>
            service.SetTextAsync("AllocFail", TestContext.Current.CancellationToken));

        ex.Code.ShouldBe(ErrorCode.StorageFailure);
        ex.Detail.ShouldNotBeNull();
        ex.Detail.ShouldContain("GlobalAlloc error 8");

        adapter.CallLog.ShouldNotContain("OpenClipboard");
        adapter.FreedHGlobals.ShouldBeEmpty();
    }

    [Fact]
    public async Task Ownership_GlobalLockFails_ThrowsStorageFailure_AndFreesAllocatedHandle()
    {
        var adapter = new FakeNativeClipboardAdapter
        {
            OnGlobalLock = _ => 0,
            LastError = 6 // ERROR_INVALID_HANDLE
        };

        var service = new Win32ClipboardService(null, null, adapter);

        var ex = await Should.ThrowAsync<IrisException>(() =>
            service.SetTextAsync("LockFail", TestContext.Current.CancellationToken));

        ex.Code.ShouldBe(ErrorCode.StorageFailure);
        ex.Detail.ShouldNotBeNull();
        ex.Detail.ShouldContain("GlobalLock error 6");

        adapter.CallLog.ShouldNotContain("OpenClipboard");
        adapter.AllocatedHGlobals.Count.ShouldBe(1);
        adapter.FreedHGlobals.ShouldContain(adapter.AllocatedHGlobals[0], "GlobalLock 失败必须释放已分配的内存");
    }

    [Fact]
    public async Task Ownership_CreateWindowExFails_ThrowsStorageFailure_AndFreesAllocatedHandle()
    {
        var adapter = new FakeNativeClipboardAdapter
        {
            OnCreateOwnerWindow = () => 0,
            LastError = 1400 // ERROR_INVALID_WINDOW_HANDLE
        };

        var service = new Win32ClipboardService(null, null, adapter);

        var ex = await Should.ThrowAsync<IrisException>(() =>
            service.SetTextAsync("WindowFail", TestContext.Current.CancellationToken));

        ex.Code.ShouldBe(ErrorCode.StorageFailure);
        ex.Detail.ShouldNotBeNull();
        ex.Detail.ShouldContain("CreateWindowEx error 1400");

        adapter.CallLog.ShouldNotContain("OpenClipboard");
        adapter.AllocatedHGlobals.Count.ShouldBe(1);
        adapter.FreedHGlobals.ShouldContain(adapter.AllocatedHGlobals[0]);
    }

    [Fact]
    public async Task Ownership_EmptyClipboardFails_ThrowsStorageFailure_ClosesAndDestroys_AndFreesMemory()
    {
        var adapter = new FakeNativeClipboardAdapter
        {
            OnEmptyClipboard = () => false,
            LastError = 5 // ERROR_ACCESS_DENIED
        };

        var service = new Win32ClipboardService(null, null, adapter);

        var ex = await Should.ThrowAsync<IrisException>(() =>
            service.SetTextAsync("EmptyFail", TestContext.Current.CancellationToken));

        ex.Code.ShouldBe(ErrorCode.StorageFailure);
        ex.Detail.ShouldNotBeNull();
        ex.Detail.ShouldContain("EmptyClipboard error 5");

        adapter.CallLog.ShouldContain("CloseClipboard");
        adapter.CreatedWindows.Count.ShouldBe(1);
        adapter.DestroyedWindows.ShouldContain(adapter.CreatedWindows[0]);
        adapter.AllocatedHGlobals.Count.ShouldBe(1);
        adapter.FreedHGlobals.ShouldContain(adapter.AllocatedHGlobals[0]);
    }

    [Fact]
    public async Task Ownership_SetClipboardDataFails_ThrowsStorageFailure_ClosesAndDestroys_AndFreesMemory()
    {
        var adapter = new FakeNativeClipboardAdapter
        {
            OnSetClipboardData = (_, _) => 0,
            LastError = 87 // ERROR_INVALID_PARAMETER
        };

        var service = new Win32ClipboardService(null, null, adapter);

        var ex = await Should.ThrowAsync<IrisException>(() =>
            service.SetTextAsync("SetFail", TestContext.Current.CancellationToken));

        ex.Code.ShouldBe(ErrorCode.StorageFailure);
        ex.Detail.ShouldNotBeNull();
        ex.Detail.ShouldContain("SetClipboardData error 87");

        adapter.CallLog.ShouldContain("CloseClipboard");
        adapter.CreatedWindows.Count.ShouldBe(1);
        adapter.DestroyedWindows.ShouldContain(adapter.CreatedWindows[0]);
        adapter.AllocatedHGlobals.Count.ShouldBe(1);
        adapter.FreedHGlobals.ShouldContain(adapter.AllocatedHGlobals[0], "SetClipboardData 失败时，系统未取得所有权，本地必须释放");
    }

    [Fact]
    public async Task Ownership_SetClipboardDataSucceeds_DoesNotFreeMemoryLocally()
    {
        var adapter = new FakeNativeClipboardAdapter();
        var service = new Win32ClipboardService(null, null, adapter);

        await service.SetTextAsync("SetSuccess", TestContext.Current.CancellationToken);

        adapter.AllocatedHGlobals.Count.ShouldBe(1);
        adapter.FreedHGlobals.ShouldBeEmpty("SetClipboardData 成功后所有权属于系统，本地严禁释放");
        adapter.CreatedWindows.Count.ShouldBe(1);
        adapter.DestroyedWindows.ShouldContain(adapter.CreatedWindows[0]);
    }

    [Fact]
    public async Task Ownership_GetTextBorrowedHandle_IsNeverFreedLocally()
    {
        var adapter = new FakeNativeClipboardAdapter();
        await service_setup();

        async Task service_setup()
        {
            var service = new Win32ClipboardService(null, null, adapter);
            await service.SetTextAsync("BorrowedRead", TestContext.Current.CancellationToken);

            adapter.FreedHGlobals.Clear();

            var text = await service.GetTextAsync(TestContext.Current.CancellationToken);
            text.ShouldBe("BorrowedRead");

            adapter.FreedHGlobals.ShouldBeEmpty("读取借用的剪贴板句柄严禁 GlobalFree");
        }
    }

    [Fact]
    public async Task Ownership_PrimaryFailureTakesPrecedence_WhenCloseAlsoFails()
    {
        var adapter = new FakeNativeClipboardAdapter
        {
            OnSetClipboardData = (_, _) => 0, // Primary failure
            OnCloseClipboard = () => false,   // Secondary cleanup failure
            LastError = 5
        };

        var service = new Win32ClipboardService(null, null, adapter);

        var ex = await Should.ThrowAsync<IrisException>(() =>
            service.SetTextAsync("PrimaryWins", TestContext.Current.CancellationToken));

        ex.Code.ShouldBe(ErrorCode.StorageFailure);
        ex.Detail.ShouldNotBeNull();
        ex.Detail.ShouldContain("SetClipboardData error 5", customMessage: "主错误必须优先于清理错误");
    }

    [Fact]
    public async Task Ownership_CloseFailsWithoutPrimaryError_ThrowsStorageFailure()
    {
        var adapter = new FakeNativeClipboardAdapter
        {
            OnCloseClipboard = () => false,
            LastError = 1418 // ERROR_CLIPBOARD_NOT_OPEN
        };

        var service = new Win32ClipboardService(null, null, adapter);

        var ex = await Should.ThrowAsync<IrisException>(() =>
            service.SetTextAsync("CloseFail", TestContext.Current.CancellationToken));

        ex.Code.ShouldBe(ErrorCode.StorageFailure);
        ex.Detail.ShouldNotBeNull();
        ex.Detail.ShouldContain("CloseClipboard error 1418");
    }
}
