using System.Runtime.InteropServices;
using Iris.Core.Errors;
using Iris.Core.Imaging;
using Iris.Core.Platform;
using Iris.Platform.Windows.Capture;
using Iris.Platform.Windows.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Iris.Platform.Windows.Tests.Capture;

public sealed class GdiScreenCapturerTests
{
    [Fact]
    public async Task ConstructionDiAndUnusedStop_DoNotCreateThreadsOrCallNative()
    {
        var services = new ServiceCollection(); services.AddIrisPlatformWindows();
        using var api = new FakeGdiCaptureApi(); var threads = new CaptureThreads(); var desktop = new CaptureDesktop();
        services.AddSingleton<IGdiCaptureApi>(api); services.AddSingleton<ICaptureThreadFactory>(threads); services.AddSingleton<IDesktopContext>(desktop);
        await using var provider = services.BuildServiceProvider();
        var service = provider.GetRequiredService<GdiScreenCapturer>();
        provider.GetRequiredService<IScreenCapturer>().ShouldBeSameAs(service);
        await service.StopAsync(); service.Dispose();
        api.Calls.ShouldBeEmpty(); threads.All.ShouldBeEmpty(); desktop.Threads.ShouldBeEmpty();
    }

    [Fact]
    public Task Capture_UsesActualAdapterTopDownDibAndPhysicalMonitorOffsets() => ScreenCaptureTestScope.Run(async f =>
    {
        var monitors = new List<MonitorInfo>
        {
            new("private-left", new(-2, -1, 2, 2), new(-2, -1, 2, 2), 1.5, false),
            new("private-right", new(1, 0, 2, 2), new(1, 0, 2, 2), 2, true),
        };
        f.Desktop.Monitors = monitors;
        f.Native.Before = stage =>
        {
            if (stage == "Flush" && f.Native.Count("Flush") == 1)
                Marshal.ReadByte(f.Native.Bits).ShouldBe((byte)0xCD); // Direct clear has not occurred before Flush.
        };
        var frame = await f.Service.CaptureVirtualScreenAsync(CancellationToken.None);
        frame.VirtualBounds.ShouldBe(new PixelRect(-2, -1, 5, 3)); frame.Stride.ShouldBe(20); frame.CapturedAt.Offset.ShouldBe(TimeSpan.Zero);
        f.Native.Header.biHeight.ShouldBe(-3); f.Native.Header.biBitCount.ShouldBe((ushort)32); f.Native.Header.biPlanes.ShouldBe((ushort)1);
        f.Native.Header.biSizeImage.ShouldBe(60u); f.Native.Header.biCompression.ShouldBe(0u); f.Native.DibArguments.ShouldBe((0u, (nint)0, 0u));
        f.Native.Blits.ShouldBe(new[] { (0, 0, 2, 2, -2, -1, 0x40CC0020u), (3, 1, 2, 2, 1, 0, 0x40CC0020u) });
        for (int y = 0; y < 3; y++)
        for (int x = 0; x < 5; x++)
        {
            int absoluteX = x - 2, absoluteY = y - 1;
            bool covered = monitors.Any(m => m.Bounds.Contains(new(absoluteX, absoluteY)));
            byte[] expected = covered ? [unchecked((byte)absoluteX), unchecked((byte)absoluteY), 37, 255] : [0, 0, 0, 255];
            frame.Bgra32Pixels.AsSpan(y * frame.Stride + x * 4, 4).ToArray().ShouldBe(expected);
        }
        frame.ToString().ShouldBe("CapturedFrame"); f.Native.Handles.ShouldBe(0); f.Native.Count("Flush").ShouldBe(2);
        f.Native.Calls.Select(c => c.Thread).Distinct().Single().ShouldBe(f.Threads.All.Single().ManagedThreadId);
        f.Desktop.Threads.ShouldAllBe(id => id == f.Threads.All.Single().ManagedThreadId);
        monitors.Clear(); frame.Monitors.Count.ShouldBe(2);
        Should.Throw<NotSupportedException>(() => ((IList<MonitorInfo>)frame.Monitors).Clear());
    });

    [Theory]
    [InlineData("DpiEnter")][InlineData("GetDC")][InlineData("CreateDC")][InlineData("CreateDIB")][InlineData("Select")][InlineData("BitBlt")][InlineData("Flush")]
    public Task NativeFailure_IsSafeCleansOwnedResourcesAndCanCaptureAgain(string stage) => ScreenCaptureTestScope.Run(async f =>
    {
        f.Native.Failures[stage] = 1;
        var error = await Should.ThrowAsync<IrisException>(() => f.Service.CaptureVirtualScreenAsync(CancellationToken.None));
        error.Code.ShouldBe(ErrorCode.CaptureFailed); error.Message.ShouldBe(ErrorMessages.GetDefault(ErrorCode.CaptureFailed));
        error.InnerException.ShouldBeNull(); error.Detail!.ShouldNotContain("private");
        f.Native.Handles.ShouldBe(0); f.Native.Count("LastError").ShouldBe(stage is "CreateDIB" or "BitBlt" ? 1 : 0);
        (await f.Service.CaptureVirtualScreenAsync(CancellationToken.None)).Bgra32Pixels.Length.ShouldBe(48);
    });

    [Theory]
    [InlineData("GetDC")][InlineData("CreateDC")][InlineData("CreateDIB")][InlineData("Select")][InlineData("BitBlt")][InlineData("Flush")]
    public Task NativeException_DoesNotExposeRawMessageOrReadStaleLastError(string stage) => ScreenCaptureTestScope.Run(async f =>
    {
        f.Native.ThrowAt = stage;
        var error = await Should.ThrowAsync<IrisException>(() => f.Service.CaptureVirtualScreenAsync(CancellationToken.None));
        error.ToString().ShouldNotContain("PRIVATE_DEVICE_OR_PIXELS"); error.InnerException.ShouldBeNull(); f.Native.Count("LastError").ShouldBe(0);
        f.Native.ThrowAt = null; await f.Service.CaptureVirtualScreenAsync(CancellationToken.None);
    });

    [Theory]
    [InlineData("DeleteBitmap")][InlineData("DeleteDC")][InlineData("ReleaseDC")][InlineData("DpiRestore")]
    public Task CleanupFailure_RetainsSameThreadAndOnlyRetriesRemainingResources(string stage) => ScreenCaptureTestScope.Run(async f =>
    {
        f.Native.Failures[stage] = -1;
        await Should.ThrowAsync<IrisException>(() => f.Service.CaptureVirtualScreenAsync(CancellationToken.None));
        var thread = f.Threads.All.Single(); thread.IsAlive.ShouldBeTrue();
        int created = f.Native.Count("CreateDIB");
        await Should.ThrowAsync<IrisException>(() => f.Service.CropAsync(ScreenCaptureTestScope.Frame(), new(-3, -2, 1, 1)));
        f.Encoding.Sessions.ShouldBeEmpty(); f.Native.Count("CreateDIB").ShouldBe(created);
        var before = f.Native.Calls.ToArray().Length;
        f.Native.Failures.Clear(); await f.Service.RetryCleanupAsync();
        f.Native.Calls.Skip(before).ShouldAllBe(c => c.Stage == stage && c.Thread == thread.ManagedThreadId);
        f.Native.Handles.ShouldBe(0); f.Native.Dpi.ShouldBe((nint)(-2));
        await f.Service.CropAsync(ScreenCaptureTestScope.Frame(), new(-3, -2, 1, 1)); f.Native.Count("CreateDIB").ShouldBe(created);
    });

    [Fact]
    public Task RestoreSelectionFailure_DeleteDcFallbackPrecedesBitmapDelete() => ScreenCaptureTestScope.Run(async f =>
    {
        f.Native.Failures["Restore"] = 1;
        var error = await Should.ThrowAsync<IrisException>(() => f.Service.CaptureVirtualScreenAsync(CancellationToken.None));
        error.Detail!.ShouldContain("RESTORE_SELECTION");
        var calls = f.Native.Calls.Select(c => c.Stage).ToList(); calls.IndexOf("DeleteDC").ShouldBeLessThan(calls.IndexOf("DeleteBitmap"));
        f.Native.Handles.ShouldBe(0); f.Native.Violations.ShouldBe(0);
    });

    [Fact]
    public Task MultipleFailures_NeverDeleteSelectedBitmap_AndRetryOnOwnerThread() => ScreenCaptureTestScope.Run(async f =>
    {
        f.Native.Failures["BitBlt"] = 1; f.Native.Failures["Restore"] = -1; f.Native.Failures["DeleteDC"] = -1;
        f.Native.Failures["ReleaseDC"] = -1; f.Native.Failures["DpiRestore"] = -1;
        var error = await Should.ThrowAsync<IrisException>(() => f.Service.CaptureVirtualScreenAsync(CancellationToken.None));
        foreach (var stage in new[] { "BITBLT", "RESTORE_SELECTION", "DELETE_DC", "RELEASE_DC", "DPI_RESTORE" }) error.Detail!.ShouldContain(stage);
        f.Native.Count("DeleteBitmap").ShouldBe(0); f.Native.Selected.ShouldBeTrue();
        f.Native.Failures.Clear(); await f.Service.RetryCleanupAsync(); f.Native.Handles.ShouldBe(0);
    });

    [Fact]
    public Task InvalidDibBitsAndHgdiError_StillCleanAcquiredBitmap() => ScreenCaptureTestScope.Run(async f =>
    {
        f.Native.NullBits = true;
        await Should.ThrowAsync<IrisException>(() => f.Service.CaptureVirtualScreenAsync(CancellationToken.None));
        f.Native.Count("DeleteBitmap").ShouldBe(1); f.Native.Count("Select").ShouldBe(0);
        f.Native.NullBits = false; f.Native.HgdiError = true; f.Native.Failures["Select"] = 1;
        await Should.ThrowAsync<IrisException>(() => f.Service.CaptureVirtualScreenAsync(CancellationToken.None));
        f.Native.Handles.ShouldBe(0);
    });

    [Theory]
    [InlineData(false)][InlineData(true)]
    public Task TopologyChange_RetriesOnceOnlyAfterCleanup(bool alwaysChanges) => ScreenCaptureTestScope.Run(async f =>
    {
        int queries = 0;
        f.Desktop.Query = () => [new("same", new(0, 0, 4, 3), new(0, 0, 4, 3), alwaysChanges ? ++queries : ++queries == 1 ? 1 : 2, true)];
        if (alwaysChanges)
            (await Should.ThrowAsync<IrisException>(() => f.Service.CaptureVirtualScreenAsync(CancellationToken.None))).Detail!.ShouldContain("TOPOLOGY_CHANGED_TWICE");
        else (await f.Service.CaptureVirtualScreenAsync(CancellationToken.None)).Monitors.Single().DpiScale.ShouldBe(2);
        f.Native.Count("GetDC").ShouldBe(2); f.Native.Count("ReleaseDC").ShouldBe(2); queries.ShouldBe(4);
    });

    [Fact]
    public Task ReorderedOverlappingMonitors_AreSameTopology() => ScreenCaptureTestScope.Run(async f =>
    {
        var a = new MonitorInfo("a", new(-2, -1, 4, 3), new(-2, -1, 4, 3), 1, false);
        var b = a with { DeviceName = "b", IsPrimary = true }; int queries = 0;
        f.Desktop.Query = () => ++queries == 1 ? [a, b] : [b, a];
        var frame = await f.Service.CaptureVirtualScreenAsync(CancellationToken.None);
        frame.Bgra32Pixels[0].ShouldBe(unchecked((byte)-2)); f.Native.Count("GetDC").ShouldBe(1);
    });

    [Fact]
    public Task TopologyChangeWithCleanupFailure_DoesNotStartAnotherCapture() => ScreenCaptureTestScope.Run(async f =>
    {
        int query = 0; f.Desktop.Query = () => [new("a", new(0, 0, 1, 1), new(0, 0, 1, 1), ++query, true)];
        f.Native.Failures["DeleteBitmap"] = -1;
        await Should.ThrowAsync<IrisException>(() => f.Service.CaptureVirtualScreenAsync(CancellationToken.None));
        f.Native.Count("GetDC").ShouldBe(1);
    });

    [Theory]
    [InlineData(0, 1, ErrorCode.CaptureFailed)]
    [InlineData(32769, 1, ErrorCode.ImageTooLarge)]
    [InlineData(32768, 32768, ErrorCode.ImageTooLarge)]
    public Task BudgetAndGeometry_AreCheckedBeforeGdiAllocation(int width, int height, ErrorCode expected) => ScreenCaptureTestScope.Run(async f =>
    {
        f.Desktop.Monitors = [new("private", new(0, 0, width, height), new(0, 0, width, height), 1, true)];
        (await Should.ThrowAsync<IrisException>(() => f.Service.CaptureVirtualScreenAsync(CancellationToken.None))).Code.ShouldBe(expected);
        f.Native.Count("GetDC").ShouldBe(0); f.Native.Count("CreateDIB").ShouldBe(0);
    });

    [Theory]
    [InlineData(false)][InlineData(true)]
    public Task CancelOrStopDuringNative_WaitsRealReturnAndFullThreadExit(bool stop) => ScreenCaptureTestScope.Run(async f =>
    {
        using var cts = new CancellationTokenSource(); var gate = f.Gate(); f.Native.Before = stage => { if (stage == "BitBlt") gate.Block(); };
        var capture = f.Track(f.Service.CaptureVirtualScreenAsync(cts.Token), typeof(OperationCanceledException));
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Should.Throw<IrisException>(() => f.Service.CropAsync(ScreenCaptureTestScope.Frame(), new(-3, -2, 1, 1))).Detail!.ShouldContain("BUSY");
        Should.Throw<InvalidOperationException>(() => f.Service.Dispose());
        Task? stopping = null;
        if (stop) { stopping = f.Track(f.Service.StopAsync()); f.Service.StopAsync().ShouldBeSameAs(stopping); f.Service.RetryCleanupAsync().ShouldBeSameAs(stopping); }
        else cts.Cancel();
        capture.IsCompleted.ShouldBeFalse(); f.Threads.All.Single().IsAlive.ShouldBeTrue();
        gate.Open(); await Should.ThrowAsync<OperationCanceledException>(() => capture);
        if (stopping != null) { await stopping; f.Threads.All.Single().IsAlive.ShouldBeFalse(); }
    });

    [Fact]
    public Task CancellationDuringCleanup_WithFailureReportsCancelledAndRetainsResponsibility() => ScreenCaptureTestScope.Run(async f =>
    {
        using var cts = new CancellationTokenSource();
        f.Native.Before = stage => { if (stage == "ReleaseDC") cts.Cancel(); }; f.Native.Failures["ReleaseDC"] = -1;
        var error = await Should.ThrowAsync<IrisException>(() => f.Service.CaptureVirtualScreenAsync(cts.Token));
        error.Detail!.ShouldContain("cancelled=True"); error.Detail!.ShouldContain("RELEASE_DC");
        await Should.ThrowAsync<IrisException>(() => f.Service.StopAsync()); f.Threads.All.Single().IsAlive.ShouldBeTrue();
        f.Native.Failures.Clear(); await f.Service.StopAsync(); f.Threads.All.Single().IsAlive.ShouldBeFalse();
    });

    [Fact]
    public Task PreCancellationThreadCreationFailureAndFinalCancellation_AreSafe() => ScreenCaptureTestScope.Run(async f =>
    {
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        Should.Throw<OperationCanceledException>(() => f.Service.CaptureVirtualScreenAsync(cancelled.Token));
        f.Native.Calls.ShouldBeEmpty(); f.Threads.All.ShouldBeEmpty();
        f.Threads.FailCreate = true;
        (await Should.ThrowAsync<IrisException>(() => f.Service.CaptureVirtualScreenAsync(CancellationToken.None))).Detail!.ShouldContain("WORKER_CREATE");
        f.Threads.FailCreate = false;
        using var late = new CancellationTokenSource(); f.Native.After = stage => { if (stage == "DpiRestore") late.Cancel(); };
        await Should.ThrowAsync<OperationCanceledException>(() => f.Service.CaptureVirtualScreenAsync(late.Token));
    });

    [Theory]
    [InlineData("Restore")][InlineData("DeleteBitmap")][InlineData("DeleteDC")][InlineData("ReleaseDC")][InlineData("DpiRestore")]
    public Task CleanupException_StillAttemptsIndependentReleasesAndPreservesSafeError(string stage) => ScreenCaptureTestScope.Run(async f =>
    {
        f.Native.ThrowAt = stage;
        var error = await Should.ThrowAsync<IrisException>(() => f.Service.CaptureVirtualScreenAsync(CancellationToken.None));
        error.InnerException.ShouldBeNull(); error.Detail!.ShouldNotContain("PRIVATE_DEVICE_OR_PIXELS"); f.Native.Count("LastError").ShouldBe(0);
        f.Native.Count("DpiRestore").ShouldBe(1);
        f.Native.ThrowAt = null; await f.Service.RetryCleanupAsync(); f.Native.Handles.ShouldBe(0);
    });

    [Theory]
    [InlineData("empty")][InlineData("null")][InlineData("entry")][InlineData("dpi")][InlineData("work")][InlineData("query")]
    public Task InvalidMonitorSnapshot_IsSafeAndNeverAllocatesGdi(string scenario) => ScreenCaptureTestScope.Run(async f =>
    {
        var monitor = f.Desktop.Monitors.Single();
        switch (scenario)
        {
            case "empty": f.Desktop.Monitors = []; break;
            case "null": f.Desktop.Monitors = null!; break;
            case "entry": f.Desktop.Monitors = [null!]; break;
            case "dpi": f.Desktop.Monitors = [monitor with { DpiScale = double.NaN }]; break;
            case "work": f.Desktop.Monitors = [monitor with { WorkArea = new(-10, 0, 100, 100) }]; break;
            case "query": f.Desktop.Query = () => throw new IOException("private device"); break;
        }
        var error = await Should.ThrowAsync<IrisException>(() => f.Service.CaptureVirtualScreenAsync(CancellationToken.None));
        error.Code.ShouldBe(ErrorCode.CaptureFailed); error.Detail!.ShouldNotContain("private device"); f.Native.Count("GetDC").ShouldBe(0);
    });

    [Fact]
    public Task FailedFinalFlush_NeverPublishesAndStillCleans() => ScreenCaptureTestScope.Run(async f =>
    {
        f.Native.Before = stage => { if (stage == "Flush" && f.Native.Count("Flush") == 2) f.Native.Failures["Flush"] = 1; };
        var error = await Should.ThrowAsync<IrisException>(() => f.Service.CaptureVirtualScreenAsync(CancellationToken.None));
        error.Detail!.ShouldContain("FLUSH_BEFORE_COPY"); f.Native.Count("BitBlt").ShouldBe(1); f.Native.Handles.ShouldBe(0);
    });

    [Fact]
    public Task CompletedCapture_IsNotRetainedAsHiddenFrameCache() => ScreenCaptureTestScope.Run(async f =>
    {
        var weak = await CaptureWeakReference(f.Service);
        await f.Service.RetryCleanupAsync();
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        weak.TryGetTarget(out _).ShouldBeFalse();
    });
    private static async Task<WeakReference<CapturedFrame>> CaptureWeakReference(GdiScreenCapturer service) =>
        new(await service.CaptureVirtualScreenAsync(CancellationToken.None));

    [Fact]
    public async Task InjectedPrimaryAfterEntered_ReleasesGateAndRetainsRealCleanupError()
    {
        var f = new ScreenCaptureTestScope(); var gate = f.Gate();
        var primary = new InvalidOperationException("test primary after entered"); Exception? observed = null; IrisException? cleanup = null;
        Task<CapturedFrame>? capture = null;
        try
        {
            f.Native.Before = stage => { if (stage == "BitBlt") gate.Block(); }; f.Native.Failures["ReleaseDC"] = -1;
            capture = f.Track(f.Service.CaptureVirtualScreenAsync(TestContext.Current.CancellationToken), typeof(IrisException));
            await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            throw primary;
        }
        catch (Exception ex) { observed = ex; }
        finally
        {
            gate.Open();
            if (capture != null) try { await capture; } catch (IrisException ex) { cleanup = ex; }
            await f.DisposeAsync();
        }
        var errors = new AggregateException(observed!, cleanup!);
        errors.InnerExceptions[0].ShouldBeSameAs(primary);
        errors.InnerExceptions[1].ShouldBeOfType<IrisException>().Detail!.ShouldContain("RELEASE_DC");
        f.Threads.All.ShouldAllBe(t => !t.IsAlive); f.Native.Handles.ShouldBe(0);
    }
}
