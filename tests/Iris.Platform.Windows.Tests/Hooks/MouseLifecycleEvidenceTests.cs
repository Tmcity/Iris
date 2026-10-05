using Iris.Core.Errors;
using Iris.Core.Platform;
using Iris.Platform.Windows.Hooks;
using Shouldly;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Iris.Platform.Windows.Tests.Hooks;

public sealed class MouseLifecycleEvidenceTests
{
    [Fact]
    public Task Start_RegistersGenerationSynchronouslyAndImmediateStopDrainsAttempt() => MouseTestRig.Use(async rig =>
    {
        var start = rig.Start(expectedFailure: true);
        rig.Monitor.Generation.ShouldBe(1);
        var stop = rig.Monitor.StopAsync();
        rig.Monitor.Generation.ShouldBe(2);
        try { await start; } catch (OperationCanceledException) { }
        await stop;
        rig.Invalidations.Single().Generation.ShouldBe(2);
        rig.Api.ActiveHandles.ShouldBe(0);
    });

    [Fact]
    public Task UiStartAndCancellation_UseBackgroundNativeAndLockFreeSubscribers() => MouseTestRig.Use(async rig =>
    {
        rig.Api.InstallGate = rig.Gate();
        using var cts = new CancellationTokenSource();
        var context = new RejectPostContext();
        int callerThread = 0;
        int invalidationThread = 0;
        var invalidation = rig.Gate();
        rig.Monitor.Invalidated += (_, _) =>
        {
            invalidationThread = Environment.CurrentManagedThreadId;
            invalidation.Block();
        };
        Task? start = null;
        var caller = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(context);
            callerThread = Environment.CurrentManagedThreadId;
            start = rig.Monitor.StartAsync(MouseTestRig.Options(), cts.Token);
        }) { IsBackground = true };
        caller.Start(); caller.Join();
        _ = rig.Track(start!, expectedFailure: true);
        await rig.Api.InstallGate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cts.Cancel();
        await invalidation.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        rig.Monitor.Generation.ShouldBe(2); // state lock remains available while user callback is blocked
        rig.Api.Calls.ShouldAllBe(call => call.Thread != callerThread);
        invalidationThread.ShouldNotBe(callerThread);
        context.Posts.ShouldBe(0);
        start!.IsCompleted.ShouldBeFalse();
        invalidation.Release(); rig.Api.InstallGate.Release();
        await Should.ThrowAsync<OperationCanceledException>(() => start);
        rig.Api.MessageThread!.IsAlive.ShouldBeFalse();
    });

    private sealed class RejectPostContext : SynchronizationContext
    {
        internal int Posts;
        public override void Post(SendOrPostCallback d, object? state)
        {
            Interlocked.Increment(ref Posts);
            throw new InvalidOperationException("UI_CONTEXT_MUST_NOT_BE_USED");
        }
    }

    [Fact]
    public Task ConsumerAndSubscribers_AreNotOnTheNativeMessageThread() => MouseTestRig.Use(async rig =>
    {
        int subscriberThread = 0;
        rig.Monitor.SelectionGestureDetected += (_, _) => subscriberThread = Environment.CurrentManagedThreadId;
        await rig.Start();
        await rig.Send(MouseMessage.LeftDown); await rig.Send(MouseMessage.LeftUp, 4); await rig.DrainAsync();
        subscriberThread.ShouldNotBe((int)rig.Api.ThreadId);
        subscriberThread.ShouldNotBe(0);
        rig.Desktop.Threads.ShouldAllBe(thread => thread != rig.Api.ThreadId);
        rig.Api.MetricCalls.ToArray().ShouldBe(new[] { (36, 144u), (37, 144u) });
        rig.Gestures.Single().App!.WindowTitle.ShouldBeNull();
    });

    [Fact]
    public Task CallbackAlreadyInFlight_AfterUnhookAndMessageThreadExitStillDelaysStop() => MouseTestRig.Use(async rig =>
    {
        await rig.Start();
        rig.Api.NextGate = rig.Gate();
        var callback = rig.Track(Task.Run(() => FakeMouseHookApi.Invoke(rig.Api.LastCallback!, MouseMessage.Move, default(MSLLHOOKSTRUCT))));
        await rig.Api.NextGate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var stop = rig.Monitor.StopAsync();
        // Joining the actual message thread is independent of this already-entered callback.
        await Task.Run(() => rig.Api.MessageThread!.Join());
        rig.Api.ActiveHandles.ShouldBe(0);
        stop.IsCompleted.ShouldBeFalse();
        Should.Throw<IrisException>(() => rig.Monitor.Dispose());
        rig.Api.NextGate.Release(); await callback; await stop;
        rig.Api.MessageThread!.IsAlive.ShouldBeFalse();
    });

    [Fact]
    public async Task PrimaryFailureAfterEntered_FinallyReleasesGatesThenRetainsCleanupErrorAndDrainsRetry()
    {
        var rig = new MouseTestRig();
        Exception? primary = null;
        Exception? cleanup = null;
        var finalErrors = new List<Exception>();
        try
        {
            rig.Api.InstallGate = rig.Gate();
            var start = rig.Start(token: TestContext.Current.CancellationToken);
            await rig.Api.InstallGate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            start.IsCompleted.ShouldBeFalse();
            throw new InvalidOperationException("INJECTED_TEST_PRIMARY_AFTER_ENTERED");
        }
        catch (Exception ex) { primary = ex; }
        finally
        {
            // Release first, then genuinely wait for the late installation and teardown.
            foreach (var gate in rig.Gates) gate.Release();
            foreach (var task in rig.Pending) { try { await task.Task; } catch (Exception ex) { finalErrors.Add(ex); } }
            rig.Api.UnhookFailures = 1;
            try { await rig.Monitor.StopAsync(); } catch (Exception ex) { cleanup = ex; }
            finalErrors.AddRange(await rig.CloseAsync());
        }
        var retained = new AggregateException(primary!, cleanup!);
        retained.InnerExceptions[0].Message.ShouldBe("INJECTED_TEST_PRIMARY_AFTER_ENTERED");
        var nativeFailure = retained.InnerExceptions[1].ShouldBeOfType<IrisException>();
        nativeFailure.Detail!.ShouldContain("cleanup=Unhook;cleanupCode=5");
        finalErrors.ShouldBeEmpty();
        rig.Api.ActiveHandles.ShouldBe(0); rig.Api.MessageThread!.IsAlive.ShouldBeFalse();
    }

    [Fact]
    public Task CancellationWithCleanupFailure_IsVisibleThenRetryFullyStops() => MouseTestRig.Use(async rig =>
    {
        using var cts = new CancellationTokenSource();
        rig.Api.InstallGate = rig.Gate();
        var start = rig.Start(expectedFailure: true, token: cts.Token);
        await rig.Api.InstallGate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        rig.Api.UnhookFailures = 1;
        cts.Cancel();
        var stop = rig.Monitor.StopAsync();
        rig.Api.InstallGate.Release();
        var first = await Should.ThrowAsync<IrisException>(() => start);
        var concurrent = await Should.ThrowAsync<IrisException>(() => stop);
        first.Detail.ShouldBe(concurrent.Detail);
        first.Detail!.ShouldContain("Unhook"); first.InnerException.ShouldBeNull();
        rig.Api.ActiveHandles.ShouldBe(1);
        await rig.Monitor.StopAsync();
        rig.Api.ActiveHandles.ShouldBe(0); rig.Api.MessageThread!.IsAlive.ShouldBeFalse();
        await rig.Start();
    });

    [Fact]
    public Task OldCallbackAndCapturedEvent_CannotAcquireNewRunGeneration() => MouseTestRig.Use(async rig =>
    {
        await rig.Start();
        var oldCallback = rig.Api.LastCallback!;
        await rig.Send(MouseMessage.LeftDown); await rig.Send(MouseMessage.LeftUp, 4); await rig.DrainAsync();
        var oldEvent = rig.Gestures.Single();
        await rig.Start();
        oldEvent.Generation.ShouldNotBe(rig.Monitor.Generation);
        FakeMouseHookApi.Invoke(oldCallback, MouseMessage.LeftDown, new MSLLHOOKSTRUCT { time = rig.Api.Now });
        FakeMouseHookApi.Invoke(oldCallback, MouseMessage.LeftUp, new MSLLHOOKSTRUCT { pt = new(40, 0), time = rig.Api.Now });
        await rig.DrainAsync(); rig.Gestures.Count.ShouldBe(1);
        await rig.Send(MouseMessage.LeftDown); await rig.Send(MouseMessage.LeftUp, 4); await rig.DrainAsync();
        rig.Gestures.Last().Generation.ShouldBe(rig.Monitor.Generation);
    });

    [Fact]
    public Task ConsumerFailure_InvalidatesWithSafeErrorAndRealCleanup() => MouseTestRig.Use(async rig =>
    {
        var invalidated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Monitor.Invalidated += (_, _) => invalidated.TrySetResult();
        await rig.Start();
        rig.Api.ThrowAt = nameof(rig.Api.TickCount);
        await rig.Send(MouseMessage.LeftDown);
        await invalidated.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try { await rig.Monitor.StopAsync(); } catch (IrisException ex) { ex.Detail!.ShouldContain("Consumer"); }
        await rig.Monitor.StopAsync();
        rig.Invalidations.Single().Error!.Detail.ShouldBeNull();
        rig.Api.ActiveHandles.ShouldBe(0); rig.Api.MessageThread!.IsAlive.ShouldBeFalse();
        rig.Api.ThrowAt = null; await rig.Start();
    });

    [Fact]
    public Task ThreadStartFailure_IsCapturedWithoutPretendingItStarted() => MouseTestRig.Use(async rig =>
    {
        var error = await Should.ThrowAsync<IrisException>(() => rig.Start(expectedFailure: true));
        error.Detail!.ShouldContain("ThreadStart");
        rig.Api.Calls.ShouldBeEmpty();
    }, new AlreadyStoppedThreadFactory());

    private sealed class AlreadyStoppedThreadFactory : IMouseThreadFactory
    {
        public Thread Create(ThreadStart action)
        {
            var thread = new Thread(static () => { }) { IsBackground = true };
            thread.Start(); thread.Join(); return thread;
        }
    }

    [Theory]
    [InlineData("Unhook")]
    [InlineData("PostThreadMessage")]
    public Task CleanupException_PreservesOwnershipAndSafeRetry(string stage) => MouseTestRig.Use(async rig =>
    {
        await rig.Start(); rig.Api.ThrowAt = stage;
        var error = await Should.ThrowAsync<IrisException>(() => rig.Monitor.StopAsync());
        error.Detail!.ShouldNotContain("PRIVATE"); error.InnerException.ShouldBeNull();
        rig.Api.MessageThread!.IsAlive.ShouldBeTrue();
        rig.Api.ThrowAt = null; await rig.Monitor.StopAsync();
        rig.Api.ActiveHandles.ShouldBe(0); rig.Api.MessageThread.IsAlive.ShouldBeFalse();
    });

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public Task FailureDuringExistingStop_IsReportedOnceAfterCompleteDrain(bool pump) => MouseTestRig.Use(async rig =>
    {
        await rig.Start();
        Task stop;
        if (pump)
        {
            rig.Api.ExitGate = rig.Gate();
            rig.Api.QuitResult = -1;
            stop = rig.Monitor.StopAsync();
            await rig.Api.ExitGate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            stop.IsCompleted.ShouldBeFalse();
            rig.Api.ExitGate.Release();
        }
        else
        {
            rig.Desktop.SourceGate = rig.Gate();
            await rig.Send(MouseMessage.LeftDown);
            await rig.Desktop.SourceGate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            stop = rig.Monitor.StopAsync();
            rig.Api.ThrowAt = nameof(rig.Api.TickCount);
            rig.Desktop.SourceGate.Release();
        }
        var error = await Should.ThrowAsync<IrisException>(() => stop);
        error.Detail!.ShouldContain(pump ? "phase=MessagePump" : "phase=Consumer");
        rig.Api.ActiveHandles.ShouldBe(0); rig.Api.MessageThread!.IsAlive.ShouldBeFalse();
        await rig.Monitor.StopAsync(); // The successfully drained historical fault is not replayed.
        rig.Api.ThrowAt = null; rig.Api.QuitResult = 0;
        await rig.Start();
    });
}
