using Iris.App.Services;
using Iris.Core.Errors;
using Iris.Core.Platform;
using Iris.Core.Settings;
using Iris.Platform.Windows.Selection;
using Iris.Platform.Windows.Tests.Selection;
using Shouldly;

namespace Iris.App.Tests.Selection;

public sealed class SelectionReadCoordinatorTests
{
    private static SelectionReadOptions UiaOptions(SelectionTestScope scope) =>
        SelectionTestScope.Options(true, SelectionReadMethod.UIAutomation) with { ExpectedSource = scope.Source };

    [Fact]
    public Task ConstructionAndUnusedDispose_ArePure_AndCoordinatorDoesNotOwnComposite() => SelectionTestScope.Run(async s =>
    {
        var coordinator = new SelectionReadCoordinator(s.Reader);
        coordinator.Dispose();
        s.Native.Calls.ShouldBeEmpty(); s.Keys.TotalNativeCalls.ShouldBe(0); s.Runner.TotalStartCalls.ShouldBe(0);
        Should.Throw<ObjectDisposedException>(() => coordinator.ReadAsync(UiaOptions(s), CancellationToken.None));
        Should.Throw<ObjectDisposedException>(() => coordinator.ReadPassiveAsync(UiaOptions(s), CancellationToken.None));
        s.UiaStatus = UiaSelectionStatus.Success;
        (await s.Reader.ReadAsync(UiaOptions(s), CancellationToken.None))!.Text.ShouldBe("uia text");
    });

    [Fact]
    public Task BusyPassiveAndInvalidExplicit_DoNotCancelOrQueue() => SelectionCoordinatorTestScope.Run(async (s, coordinator) =>
    {
        s.Seed();
        var passive = s.Track(coordinator.ReadPassiveAsync(SelectionTestScope.Options(), CancellationToken.None), typeof(OperationCanceledException));
        await s.Time.WaitForDelayAsync(15);
        int calls = s.Native.Calls.Count;
        (await coordinator.ReadPassiveAsync(UiaOptions(s), CancellationToken.None)).ShouldBeNull();
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        Should.Throw<OperationCanceledException>(() => coordinator.ReadAsync(UiaOptions(s), cancelled.Token));
        Should.Throw<IrisException>(() => coordinator.ReadAsync(UiaOptions(s) with { ClipboardTimeoutMs = 1 }, CancellationToken.None)).Code.ShouldBe(ErrorCode.InvalidArgument);
        Should.Throw<IrisException>(() => coordinator.ReadAsync(UiaOptions(s) with { ExpectedSource = new("private/path", 1, "private", 1) }, CancellationToken.None)).Code.ShouldBe(ErrorCode.InvalidArgument);
        s.Native.Calls.Count.ShouldBe(calls); s.Runner.TotalStartCalls.ShouldBe(0); passive.IsCompleted.ShouldBeFalse();
        await coordinator.StopAsync();
        await Should.ThrowAsync<OperationCanceledException>(() => passive);
        s.ClipboardText().ShouldBe("old text");
    });

    [Fact]
    public Task ExplicitPreemptsPassive_AfterRealRestore_AndFreezesPendingOptions() => SelectionCoordinatorTestScope.Run(async (s, coordinator) =>
    {
        s.Seed(); s.UiaStatus = UiaSelectionStatus.Success;
        var passive = s.Track(coordinator.ReadPassiveAsync(SelectionTestScope.Options(), CancellationToken.None), typeof(OperationCanceledException));
        await s.Time.WaitForDelayAsync(15);
        var gate = s.NativeGate("Set");
        var order = new List<SelectionReadMethod> { SelectionReadMethod.UIAutomation };
        var unsafeProcesses = new List<string>();
        var source = s.Source!;
        var pending = s.Track(coordinator.ReadAsync(new(order, true, 100, unsafeProcesses) { ExpectedSource = source }, CancellationToken.None));
        order.Clear(); unsafeProcesses.Add("editor.exe"); source = source with { ProcessId = 9 };
        await gate.Entered.WaitAsync(TimeSpan.FromSeconds(10));
        passive.IsCompleted.ShouldBeFalse(); pending.IsCompleted.ShouldBeFalse();
        s.Runner.TotalStartCalls.ShouldBe(0); s.Copies.ShouldBe(1); s.Native.WindowAlive.ShouldBeTrue();
        (await coordinator.ReadPassiveAsync(UiaOptions(s), CancellationToken.None)).ShouldBeNull();
        Should.Throw<IrisException>(() => coordinator.ReadAsync(UiaOptions(s), CancellationToken.None)).Message.ShouldContain("BUSY");
        Should.Throw<InvalidOperationException>(() => coordinator.Dispose());
        gate.Release.Set();
        await Should.ThrowAsync<OperationCanceledException>(() => passive);
        (await pending)!.Method.ShouldBe(SelectionReadMethod.UIAutomation);
        s.ClipboardText().ShouldBe("old text"); s.Runner.TotalStartCalls.ShouldBe(1); s.Copies.ShouldBe(1);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task CancelPendingOrStop_StillWaitsPreemptedRealCleanup(bool stop) => SelectionCoordinatorTestScope.Run(async (s, coordinator) =>
    {
        s.Seed(); using var cts = new CancellationTokenSource();
        var passive = s.Track(coordinator.ReadPassiveAsync(SelectionTestScope.Options(), CancellationToken.None), typeof(OperationCanceledException));
        await s.Time.WaitForDelayAsync(15);
        var gate = s.NativeGate("Set");
        var pending = s.Track(coordinator.ReadAsync(UiaOptions(s), cts.Token), typeof(OperationCanceledException));
        await gate.Entered.WaitAsync(TimeSpan.FromSeconds(10));
        Task? stopping = null;
        if (stop) { stopping = s.Track(coordinator.StopAsync()); coordinator.StopAsync().ShouldBeSameAs(stopping); }
        else cts.Cancel();
        pending.IsCompleted.ShouldBeFalse(); passive.IsCompleted.ShouldBeFalse();
        (stopping?.IsCompleted ?? false).ShouldBeFalse(); s.Runner.TotalStartCalls.ShouldBe(0);
        gate.Release.Set();
        await Should.ThrowAsync<OperationCanceledException>(() => pending);
        await Should.ThrowAsync<OperationCanceledException>(() => passive);
        if (stopping != null) await stopping;
        s.Runner.TotalStartCalls.ShouldBe(0); s.ClipboardText().ShouldBe("old text");
        if (stop)
        {
            (await coordinator.ReadPassiveAsync(UiaOptions(s), CancellationToken.None)).ShouldBeNull();
            await Should.ThrowAsync<OperationCanceledException>(() => coordinator.ReadAsync(UiaOptions(s), CancellationToken.None));
        }
        else
        {
            s.UiaStatus = UiaSelectionStatus.Success;
            (await coordinator.ReadAsync(UiaOptions(s), CancellationToken.None))!.Text.ShouldBe("uia text");
        }
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task BothEntrypoints_CaptureBeforeFirstWait(bool passive) => SelectionCoordinatorTestScope.Run(async (s, coordinator) =>
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new ManualResetEventSlim(); s.Gates.Add(release);
        s.OnSourceQuery = () => { entered.TrySetResult(); release.Wait(); };
        s.UiaStatus = UiaSelectionStatus.Success;
        var order = new List<SelectionReadMethod> { SelectionReadMethod.UIAutomation };
        var excluded = new List<string>();
        var options = new SelectionReadOptions(order, true, 100, excluded) { ExpectedSource = s.Source };
        var read = s.Track(passive ? coordinator.ReadPassiveAsync(options, CancellationToken.None) : coordinator.ReadAsync(options, CancellationToken.None));
        order.Clear(); excluded.Add("editor.exe"); options = options with { ExpectedSource = s.Source! with { ProcessId = 1 } };
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        if (!passive)
        {
            Should.Throw<IrisException>(() => coordinator.ReadAsync(UiaOptions(s), CancellationToken.None)).Code.ShouldBe(ErrorCode.CaptureFailed);
            (await coordinator.ReadPassiveAsync(UiaOptions(s), CancellationToken.None)).ShouldBeNull();
        }
        release.Set(); (await read)!.Text.ShouldBe("uia text"); s.Runner.TotalStartCalls.ShouldBe(1);
    });

    [Fact]
    public Task SourceChangeWhilePending_DoesNotStartNewTargetAfterOldRestore() => SelectionCoordinatorTestScope.Run(async (s, coordinator) =>
    {
        s.Seed();
        var passive = s.Track(coordinator.ReadPassiveAsync(SelectionTestScope.Options(), CancellationToken.None), typeof(OperationCanceledException));
        await s.Time.WaitForDelayAsync(15);
        var gate = s.NativeGate("Set");
        var pending = s.Track(coordinator.ReadAsync(UiaOptions(s), CancellationToken.None));
        await gate.Entered.WaitAsync(TimeSpan.FromSeconds(10));
        s.Source = s.Source! with { WindowHandle = 88 };
        gate.Release.Set();
        await Should.ThrowAsync<OperationCanceledException>(() => passive); (await pending).ShouldBeNull();
        s.ClipboardText().ShouldBe("old text"); s.Runner.TotalStartCalls.ShouldBe(0); s.Copies.ShouldBe(1);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task RestoreFailure_IsVisibleAndSameCompositeRetainsRetry(bool persistent) => SelectionCoordinatorTestScope.Run(async (s, coordinator) =>
    {
        s.Seed(); s.UiaStatus = UiaSelectionStatus.Success;
        var passive = s.Track(coordinator.ReadPassiveAsync(SelectionTestScope.Options(), CancellationToken.None), typeof(IrisException));
        await s.Time.WaitForDelayAsync(15);
        s.Native.Failures["Set"] = persistent ? -1 : 1;
        var pending = s.Track(coordinator.ReadAsync(UiaOptions(s), CancellationToken.None), persistent ? typeof(IrisException) : null);
        (await Should.ThrowAsync<IrisException>(() => passive)).Code.ShouldBe(ErrorCode.CaptureFailed);
        if (persistent)
        {
            await Should.ThrowAsync<IrisException>(() => pending); s.Runner.TotalStartCalls.ShouldBe(0);
            s.Native.Failures.Clear();
            (await coordinator.ReadAsync(UiaOptions(s), CancellationToken.None))!.Text.ShouldBe("uia text");
        }
        else (await pending)!.Text.ShouldBe("uia text");
        s.Runner.TotalStartCalls.ShouldBe(1); s.Copies.ShouldBe(1); s.ClipboardText().ShouldBe("old text");
    });

    [Fact]
    public Task CallerCancellation_WaitsActualRestoreThenAllowsRetry() => SelectionCoordinatorTestScope.Run(async (s, coordinator) =>
    {
        s.Seed(); using var cts = new CancellationTokenSource();
        var active = s.Track(coordinator.ReadAsync(SelectionTestScope.Options(), cts.Token), typeof(OperationCanceledException));
        await s.Time.WaitForDelayAsync(15);
        var gate = s.NativeGate("Set"); cts.Cancel();
        await gate.Entered.WaitAsync(TimeSpan.FromSeconds(10)); active.IsCompleted.ShouldBeFalse();
        gate.Release.Set(); await Should.ThrowAsync<OperationCanceledException>(() => active);
        s.UiaStatus = UiaSelectionStatus.Success;
        (await coordinator.ReadAsync(UiaOptions(s), CancellationToken.None))!.Text.ShouldBe("uia text");
    });

    [Fact]
    public Task StopAcceptance_IsImmediateAndDoesNotOwnCompositeShutdown() => SelectionCoordinatorTestScope.Run(async (s, coordinator) =>
    {
        coordinator.StopAcceptance();
        (await coordinator.ReadPassiveAsync(UiaOptions(s), CancellationToken.None)).ShouldBeNull();
        await Should.ThrowAsync<OperationCanceledException>(() => coordinator.ReadAsync(UiaOptions(s), CancellationToken.None));
        await coordinator.StopAsync();
        s.Native.Calls.ShouldBeEmpty(); s.Runner.TotalStartCalls.ShouldBe(0);
        s.UiaStatus = UiaSelectionStatus.Success;
        (await s.Reader.ReadAsync(UiaOptions(s), CancellationToken.None))!.Text.ShouldBe("uia text");
    });

    [Fact]
    public Task DrainedCoordinator_DoesNotClaimPlatformPendingRestoreIsComplete() => SelectionCoordinatorTestScope.Run(async (s, coordinator) =>
    {
        s.Seed();
        var passive = s.Track(coordinator.ReadPassiveAsync(SelectionTestScope.Options(), CancellationToken.None), typeof(IrisException));
        await s.Time.WaitForDelayAsync(15); s.Native.Failures["Set"] = -1;
        await coordinator.StopAsync();
        (await Should.ThrowAsync<IrisException>(() => passive)).Code.ShouldBe(ErrorCode.CaptureFailed);
        s.Native.WindowAlive.ShouldBeTrue();
        s.Native.Failures.Clear(); var gate = s.NativeGate("Set");
        var nativeStop = s.Track(s.Reader.StopAsync());
        await gate.Entered.WaitAsync(TimeSpan.FromSeconds(10));
        nativeStop.IsCompleted.ShouldBeFalse(); s.Native.WindowAlive.ShouldBeTrue();
        gate.Release.Set(); await nativeStop;
        s.Native.WindowAlive.ShouldBeFalse(); s.ClipboardText().ShouldBe("old text");
    });

    [Fact]
    public Task ErrorAndNullArePreserved_AndUsedSyncDisposeRequiresStop() => SelectionCoordinatorTestScope.Run(async (s, coordinator) =>
    {
        s.UiaStatus = UiaSelectionStatus.TooLong;
        (await Should.ThrowAsync<IrisException>(() => coordinator.ReadAsync(UiaOptions(s), CancellationToken.None))).Code.ShouldBe(ErrorCode.InputTooLong);
        s.UiaStatus = UiaSelectionStatus.Unsupported;
        (await coordinator.ReadPassiveAsync(UiaOptions(s), CancellationToken.None)).ShouldBeNull();
        Should.Throw<InvalidOperationException>(() => coordinator.Dispose());
        await coordinator.StopAsync(); coordinator.Dispose();
        Should.Throw<ObjectDisposedException>(() => coordinator.ReadAsync(UiaOptions(s), CancellationToken.None));
    });
}
