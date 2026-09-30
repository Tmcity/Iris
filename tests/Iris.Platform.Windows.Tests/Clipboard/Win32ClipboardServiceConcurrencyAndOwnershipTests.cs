using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Iris.Core.Errors;
using Iris.Core.Imaging;
using Iris.Core.Platform;
using Iris.Platform.Windows.Clipboard;
using Iris.Platform.Windows.Tests.Clipboard.TestSupport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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

    [Fact]
    public async Task ContainerDisposal_WithActiveAndQueuedOperations_ActiveCompletes_QueuedCancelsCleanly_WithoutObjectDisposedException()
    {
        var services = new ServiceCollection();
        var adapter = new FakeNativeClipboardAdapter();
        services.AddSingleton<IClipboardService>(_ => new Win32ClipboardService(null, null, adapter));
        var provider = services.BuildServiceProvider();

        var service = provider.GetRequiredService<IClipboardService>();

        var activeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var activeRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        adapter.OnBeforeSetClipboardData = () =>
        {
            activeStarted.TrySetResult();
            activeRelease.Task.Wait(TimeSpan.FromSeconds(5));
        };

        using var queuedCts = new CancellationTokenSource();
        Task? activeTask = null;
        Task? queuedTask = null;

        try
        {
            // Active task enters SetClipboardData and blocks
            activeTask = service.SetTextAsync("ActiveRunningText", TestContext.Current.CancellationToken);
            await activeStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

            // Queued task tries to acquire gate and waits
            queuedTask = service.GetTextAsync(queuedCts.Token);

            activeTask.IsCompleted.ShouldBeFalse();
            queuedTask.IsCompleted.ShouldBeFalse();

            // Dispose container - must NOT dispose gate or throw ObjectDisposedException
            provider.Dispose();

            // Cancel the queued item
            queuedCts.Cancel();

            // Queued task must cancel cleanly within 5 seconds without hanging or ObjectDisposedException
            await queuedTask.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken)
                .ShouldThrowAsync<OperationCanceledException>();

            // Release active item
            activeRelease.TrySetResult();

            // Active task must complete cleanly without ObjectDisposedException
            await activeTask.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            adapter.ClipboardFormats.ContainsKey(DibV5Helper.CF_UNICODETEXT).ShouldBeTrue();
        }
        finally
        {
            activeRelease.TrySetResult();
            if (activeTask != null)
            {
                try { await activeTask.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken); } catch { }
            }
            if (queuedTask != null)
            {
                try { await queuedTask.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken); } catch { }
            }
        }
    }

    [Fact]
    public async Task SetTextAsync_WhenCalledFromStaThread_PerformsGlobalAllocAndLockOnBackgroundThread()
    {
        var adapter = new FakeNativeClipboardAdapter();
        var service = new Win32ClipboardService(null, null, adapter);

        int staThreadId = 0;
        var staDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var staThread = new Thread(() =>
        {
            staThreadId = Environment.CurrentManagedThreadId;
            try
            {
                service.SetTextAsync("STAText", TestContext.Current.CancellationToken).GetAwaiter().GetResult();
                staDone.TrySetResult();
            }
            catch (Exception ex)
            {
                staDone.TrySetException(ex);
            }
        });
        staThread.SetApartmentState(ApartmentState.STA);
        staThread.IsBackground = true;
        staThread.Start();

        try
        {
            await staDone.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            staThread.Join(5000).ShouldBeTrue();

            adapter.GlobalAllocThreadIds.Count.ShouldBeGreaterThan(0);
            adapter.GlobalLockThreadIds.Count.ShouldBeGreaterThan(0);

            adapter.GlobalAllocThreadIds[0].ShouldNotBe(staThreadId, "GlobalAlloc 严禁在调用方 STA 线程执行");
            adapter.GlobalLockThreadIds[0].ShouldNotBe(staThreadId, "GlobalLock 严禁在调用方 STA 线程执行");
        }
        finally
        {
            staThread.Join(1000);
        }
    }

    [Fact]
    public async Task SetImageAsync_WhenQueuedBehindActiveOperation_DoesNotStartDecodingUntilGateAcquired()
    {
        var adapter = new FakeNativeClipboardAdapter();
        var service = new Win32ClipboardService(null, null, adapter);

        var activeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var activeRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        adapter.OnBeforeSetClipboardData = () =>
        {
            activeStarted.TrySetResult();
            activeRelease.Task.Wait(TimeSpan.FromSeconds(5));
        };

        using var queuedCts = new CancellationTokenSource();
        Task? activeTask = null;
        Task? queuedTask = null;

        try
        {
            activeTask = service.SetTextAsync("ActiveFirst", TestContext.Current.CancellationToken);
            await activeStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

            // Pass invalid image bytes that would throw ImageInvalid if decoding actually started
            // But because it's queued behind the gate, canceling queued token will cause OCE, NOT ImageInvalid!
            byte[] invalidBmpBytes = [0x42, 0x4D, 0x00, 0x00];
            var queuedImage = new ImageData(invalidBmpBytes, ImageFormat.Bmp, 10, 10);

            queuedTask = service.SetImageAsync(queuedImage, queuedCts.Token);
            queuedTask.IsCompleted.ShouldBeFalse("排队图像调用在串行门放行前不得进入后台解码");

            queuedCts.Cancel();

            // Cancel must throw OperationCanceledException within 5 seconds, proving it never reached decoder and does not hang
            await queuedTask.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken)
                .ShouldThrowAsync<OperationCanceledException>();

            activeRelease.TrySetResult();
            await activeTask.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        }
        finally
        {
            activeRelease.TrySetResult();
            if (activeTask != null) try { await activeTask.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken); } catch { }
            if (queuedTask != null) try { await queuedTask.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken); } catch { }
        }
    }

    [Fact]
    public async Task Ownership_PrimaryFailureAndCleanupFailure_PreservesPrimaryFailure_AndLogsWarning()
    {
        var logger = new MemoryLogger<Win32ClipboardService>();
        var adapter = new FakeNativeClipboardAdapter
        {
            OnSetClipboardData = (_, _) => 0, // Primary failure
            OnCloseClipboard = () => false,   // Cleanup failure
            OnDestroyWindow = _ => false,     // Cleanup failure
            LastError = 5
        };

        var service = new Win32ClipboardService(null, logger, adapter);

        var ex = await Should.ThrowAsync<IrisException>(() =>
            service.SetTextAsync("DualFailure", TestContext.Current.CancellationToken));

        // Primary error must be thrown
        ex.Code.ShouldBe(ErrorCode.StorageFailure);
        ex.Detail.ShouldNotBeNull();
        ex.Detail.ShouldContain("SetClipboardData error 5");

        // Cleanup failures must be logged safely
        logger.Entries.Count(e => e.Level == LogLevel.Warning).ShouldBeGreaterThanOrEqualTo(2);
        logger.Entries.Any(e => e.Message.Contains("CloseClipboard")).ShouldBeTrue();
        logger.Entries.Any(e => e.Message.Contains("DestroyWindow")).ShouldBeTrue();
    }

    [Fact]
    public async Task Ownership_DestroyWindowFailsWithoutPrimaryError_ThrowsStorageFailure()
    {
        var adapter = new FakeNativeClipboardAdapter
        {
            OnDestroyWindow = _ => false,
            LastError = 1400 // ERROR_INVALID_WINDOW_HANDLE
        };

        var service = new Win32ClipboardService(null, null, adapter);

        var ex = await Should.ThrowAsync<IrisException>(() =>
            service.SetTextAsync("DestroyFail", TestContext.Current.CancellationToken));

        ex.Code.ShouldBe(ErrorCode.StorageFailure);
        ex.Detail.ShouldNotBeNull();
        ex.Detail.ShouldContain("DestroyWindow error 1400");
    }

    [Fact]
    public async Task Ownership_GlobalUnlock_WhenReturnsFalseWithNoError_Succeeds()
    {
        var adapter = new FakeNativeClipboardAdapter();
        adapter.OnGlobalUnlock = _ =>
        {
            // In Win32, GlobalUnlock returning false with GetLastError() == NO_ERROR (0)
            // indicates lock count decremented to 0, which is normal success.
            adapter.LastError = 0;
            return false;
        };

        var service = new Win32ClipboardService(null, null, adapter);

        await service.SetTextAsync("UnlockZeroError", TestContext.Current.CancellationToken);

        adapter.ClipboardFormats.ContainsKey(DibV5Helper.CF_UNICODETEXT).ShouldBeTrue();
    }

    [Fact]
    public async Task Ownership_GlobalUnlock_WhenReturnsFalseWithRealError_ThrowsStorageFailure()
    {
        var adapter = new FakeNativeClipboardAdapter();
        adapter.OnGlobalUnlock = _ =>
        {
            adapter.LastError = 6; // ERROR_INVALID_HANDLE
            return false;
        };

        var service = new Win32ClipboardService(null, null, adapter);

        var ex = await Should.ThrowAsync<IrisException>(() =>
            service.SetTextAsync("UnlockRealError", TestContext.Current.CancellationToken));

        ex.Code.ShouldBe(ErrorCode.StorageFailure);
        ex.Detail.ShouldNotBeNull();
        ex.Detail.ShouldContain("GlobalUnlock error 6");
    }

    [Fact]
    public async Task Ownership_GetText_WhenInvalidTextAndGlobalUnlockFails_PreservesInvalidResponse_AndLogsGlobalUnlockCleanupFailure()
    {
        var logger = new MemoryLogger<Win32ClipboardService>();
        var adapter = new FakeNativeClipboardAdapter();

        // Prepare text buffer with no null terminator within bounds -> causes InvalidResponse
        byte[] noNulBytes = [(byte)'A', 0, (byte)'B', 0];
        nint hData = adapter.GlobalAlloc(0, (nuint)noNulBytes.Length);
        nint ptr = adapter.GlobalLock(hData);
        Marshal.Copy(noNulBytes, 0, ptr, noNulBytes.Length);
        adapter.GlobalUnlock(hData);

        adapter.ClipboardFormats[DibV5Helper.CF_UNICODETEXT] = hData;

        // When GlobalUnlock is called in finally after read failure, it returns false with error 6
        adapter.OnGlobalUnlock = _ =>
        {
            adapter.LastError = 6; // ERROR_INVALID_HANDLE
            return false;
        };

        var service = new Win32ClipboardService(null, logger, adapter);

        var ex = await Should.ThrowAsync<IrisException>(() =>
            service.GetTextAsync(TestContext.Current.CancellationToken));

        // Primary failure InvalidResponse must NOT be overwritten by StorageFailure
        ex.Code.ShouldBe(ErrorCode.InvalidResponse);
        ex.Message.ShouldContain("null terminator");

        // GlobalUnlock cleanup failure must be safely logged with error code
        logger.Entries.Count(e => e.Level == LogLevel.Warning).ShouldBe(1);
        logger.Entries[0].Message.ShouldContain("GlobalUnlock");
        logger.Entries[0].Message.ShouldContain("6");
    }

    [Fact]
    public async Task Ownership_SetText_WhenCloseAndDestroyBothFailWithoutPrimaryError_ThrowsCloseError_AndLogsDestroyCleanupFailure()
    {
        var logger = new MemoryLogger<Win32ClipboardService>();
        var adapter = new FakeNativeClipboardAdapter();
        adapter.OnCloseClipboard = () =>
        {
            adapter.LastError = 1418; // ERROR_CLIPBOARD_NOT_OPEN
            return false;
        };
        adapter.OnDestroyWindow = _ =>
        {
            adapter.LastError = 1400; // ERROR_INVALID_WINDOW_HANDLE
            return false;
        };

        var service = new Win32ClipboardService(null, logger, adapter);

        var ex = await Should.ThrowAsync<IrisException>(() =>
            service.SetTextAsync("ValidText", TestContext.Current.CancellationToken));

        // First cleanup failure becomes primary error thrown
        ex.Code.ShouldBe(ErrorCode.StorageFailure);
        ex.Detail.ShouldNotBeNull();
        ex.Detail.ShouldContain("CloseClipboard error 1418");

        // Second cleanup failure must be safely logged, NOT lost
        logger.Entries.Count(e => e.Level == LogLevel.Warning).ShouldBe(1);
        logger.Entries[0].Message.ShouldContain("DestroyWindow");
        logger.Entries[0].Message.ShouldContain("1400");
    }
}
