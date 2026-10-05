using Iris.Core.Errors;
using Iris.Core.Platform;
using Iris.Core.Settings;
using Iris.Platform.Windows.Selection;
using Iris.Platform.Windows.Tests.Selection;
using Shouldly;

namespace Iris.App.Tests.Selection;

public sealed class SelectionCoordinationDrainTests
{
    private static SelectionReadOptions Options(SelectionTestScope scope) =>
        SelectionTestScope.Options(true, SelectionReadMethod.UIAutomation) with { ExpectedSource = scope.Source };

    [Theory]
    [InlineData("read")]
    [InlineData("cancel")]
    [InlineData("stop")]
    public Task Explicit_WaitsLateUiaStartupDisposalAndCancellationCallback(string outcome) => SelectionCoordinatorTestScope.Run(async (s, coordinator) =>
    {
        using var pendingCts = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var launch = new TaskCompletionSource<IUiaProcessSession>(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var disposeEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelRelease = new ManualResetEventSlim(); s.Gates.Add(cancelRelease);
        var disposeRelease = new ManualResetEventSlim(); s.Gates.Add(disposeRelease);
        var session = new FakeUiaProcessSession();
        bool cancellationReturned = false;
        bool disposalReturned = false;
        int cancellationThread = 0;
        CancellationTokenRegistration registration = default;
        var normal = s.Runner.StartAsyncFunc!;
        int starts = 0;
        s.UiaStatus = UiaSelectionStatus.Success;
        s.Runner.StartAsyncFunc = token =>
        {
            if (Interlocked.Increment(ref starts) != 1) return normal(token);
            registration = token.UnsafeRegister(_ =>
            {
                cancellationThread = Environment.CurrentManagedThreadId;
                cancelEntered.TrySetResult(); cancelRelease.Wait(); cancellationReturned = true;
            }, null);
            started.TrySetResult(); return launch.Task;
        };
        session.OnDispose = () => { disposeEntered.TrySetResult(); disposeRelease.Wait(); disposalReturned = true; };
        s.Release.Add(() => launch.TrySetResult(session));
        try
        {
            var passive = s.Track(coordinator.ReadPassiveAsync(Options(s), CancellationToken.None), typeof(OperationCanceledException));
            await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            int callerThread = 0;
            Task<SelectionReadResult?>? pendingCall = null;
            Exception? entryFailure = null;
            var caller = new Thread(() =>
            {
                callerThread = Environment.CurrentManagedThreadId;
                try { pendingCall = coordinator.ReadAsync(Options(s), pendingCts.Token); }
                catch (Exception ex) { entryFailure = ex; }
            }) { IsBackground = true };
            caller.Start(); caller.Join();
            if (entryFailure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(entryFailure).Throw();
            var pending = s.Track(pendingCall!, outcome == "read" ? null : typeof(OperationCanceledException));
            await cancelEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            cancellationThread.ShouldNotBe(callerThread);
            // A blocked external cancellation callback holds neither the coordinator state lock nor its caller.
            (await Task.Run(() => coordinator.ReadPassiveAsync(Options(s), CancellationToken.None))).ShouldBeNull();
            if (outcome == "cancel") pendingCts.Cancel();
            Task? stop = outcome == "stop" ? s.Track(coordinator.StopAsync()) : null;
            pending.IsCompleted.ShouldBeFalse(); passive.IsCompleted.ShouldBeFalse(); starts.ShouldBe(1);
            launch.TrySetResult(session);
            await disposeEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            pending.IsCompleted.ShouldBeFalse(); passive.IsCompleted.ShouldBeFalse(); disposalReturned.ShouldBeFalse();
            disposeRelease.Set();
            cancellationReturned.ShouldBeFalse(); pending.IsCompleted.ShouldBeFalse();
            cancelRelease.Set();
            await Should.ThrowAsync<OperationCanceledException>(() => passive);
            if (outcome == "read") (await pending)!.Text.ShouldBe("uia text");
            else await Should.ThrowAsync<OperationCanceledException>(() => pending);
            if (stop != null) await stop;
            cancellationReturned.ShouldBeTrue(); disposalReturned.ShouldBeTrue(); session.KillCalled.ShouldBeTrue(); session.Disposed.ShouldBeTrue();
            starts.ShouldBe(outcome == "read" ? 2 : 1);
        }
        finally
        {
            cancelRelease.Set(); disposeRelease.Set(); launch.TrySetResult(session);
            await coordinator.StopAsync();
            await registration.DisposeAsync();
        }
    });

    [Fact]
    public Task CancellationCallbackError_IsSafeObservedAndFullyDrained() => SelectionCoordinatorTestScope.Run(async (s, coordinator) =>
    {
        using var caller = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var launch = new TaskCompletionSource<IUiaProcessSession>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new ManualResetEventSlim(); s.Gates.Add(release);
        var session = new FakeUiaProcessSession();
        CancellationTokenRegistration registration = default;
        s.Runner.StartAsyncFunc = token =>
        {
            registration = token.UnsafeRegister(_ => { cancelled.TrySetResult(); release.Wait(); throw new IOException("PRIVATE_CALLBACK_CONTENT"); }, null);
            entered.TrySetResult(); return launch.Task;
        };
        s.Release.Add(() => launch.TrySetResult(session));
        try
        {
            var read = s.Track(coordinator.ReadAsync(Options(s), caller.Token), typeof(IrisException));
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            caller.Cancel(); // Never executes the external callback on this caller stack.
            await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var stop = coordinator.StopAsync();
            stop.IsCompleted.ShouldBeFalse(); read.IsCompleted.ShouldBeFalse();
            release.Set(); launch.TrySetResult(session);
            var error = await Should.ThrowAsync<IrisException>(() => read);
            error.Code.ShouldBe(ErrorCode.CaptureFailed); error.ToString().ShouldNotContain("PRIVATE_CALLBACK_CONTENT"); error.InnerException.ShouldBeNull();
            await Should.ThrowAsync<IrisException>(() => stop);
            await coordinator.StopAsync();
            session.Disposed.ShouldBeTrue(); session.KillCalled.ShouldBeTrue();
        }
        finally
        {
            release.Set(); launch.TrySetResult(session);
            try { await coordinator.StopAsync(); } catch (IrisException) { await coordinator.StopAsync(); }
            await registration.DisposeAsync();
        }
    });

    [Fact]
    public Task LateUiaCleanupError_IsNotConvertedToCancellationOrSuccess() => SelectionCoordinatorTestScope.Run(async (s, coordinator) =>
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var launch = new TaskCompletionSource<IUiaProcessSession>(TaskCreationOptions.RunContinuationsAsynchronously);
        var session = new FakeUiaProcessSession();
        bool fail = true; s.Release.Add(() => { fail = false; launch.TrySetResult(session); });
        session.OnDispose = () => { if (fail) throw new IOException("PRIVATE_CLEANUP_CONTENT"); };
        var normal = s.Runner.StartAsyncFunc!;
        s.Runner.StartAsyncFunc = _ => { entered.TrySetResult(); return launch.Task; };
        var passive = s.Track(coordinator.ReadPassiveAsync(Options(s), CancellationToken.None), typeof(IrisException));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var pending = s.Track(coordinator.ReadAsync(Options(s), CancellationToken.None), typeof(IrisException));
        // Start return is deliberately late and ignores its token; actual UiaSelectionReader drains it.
        launch.TrySetResult(session);
        var error = await Should.ThrowAsync<IrisException>(() => passive);
        error.ToString().ShouldNotContain("PRIVATE_CLEANUP_CONTENT");
        await Should.ThrowAsync<IrisException>(() => pending); s.Runner.TotalStartCalls.ShouldBe(1);
        fail = false; s.Runner.StartAsyncFunc = normal; s.UiaStatus = UiaSelectionStatus.Success;
        (await coordinator.ReadAsync(Options(s), CancellationToken.None))!.Text.ShouldBe("uia text");
        s.Runner.TotalStartCalls.ShouldBe(2); session.Disposed.ShouldBeTrue();
    });

    [Fact]
    public async Task InjectedFailureAfterRestoreEntered_FinallyRetainsPrimaryAndCleanupErrors()
    {
        var scope = new SelectionTestScope();
        var primary = new InvalidOperationException("test body injection");
        var cleanup = new IOException("test cleanup injection");
        scope.AfterCleanup = () => throw cleanup;
        var error = await Should.ThrowAsync<AggregateException>(() => SelectionCoordinatorTestScope.Run(async (s, coordinator) =>
        {
            s.Seed();
            _ = s.Track(coordinator.ReadPassiveAsync(SelectionTestScope.Options(), CancellationToken.None), typeof(OperationCanceledException));
            await s.Time.WaitForDelayAsync(15);
            var gate = s.NativeGate("Set");
            _ = s.Track(coordinator.ReadAsync(Options(s), CancellationToken.None), typeof(OperationCanceledException));
            await gate.Entered.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            throw primary;
        }, scope));
        error.InnerExceptions.ShouldContain(primary); error.InnerExceptions.ShouldContain(cleanup);
        scope.Joined.ShouldBeTrue(); scope.Tasks.ShouldAllBe(item => item.Task.IsCompleted); scope.Native.WindowAlive.ShouldBeFalse();
    }
}
