using System.Reflection;
using System.Runtime.ExceptionServices;
using Iris.Core.Errors;
using Iris.Platform.Windows.Selection;
using Microsoft.Extensions.Time.Testing;
using Shouldly;

namespace Iris.Platform.Windows.Tests.Selection;

public sealed class UiaStartupCleanupTests
{
    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static Task Entered(Task task) => task.WaitAsync(TimeSpan.FromSeconds(10));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task ReadAsync_And_StopAsync_WhenStderrDrainPending_DoNotClaimCompletionUntilDrainObserved(bool stop) =>
        DrainScenarioAsync(new TestScope(), stop, null);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DrainScenario_WhenBodyThrows_FinallyReleasesAndJoinsAllOwnedWork(bool stop)
    {
        var scope = new TestScope();
        var injected = new InvalidOperationException("R6 body injection");
        var error = await Should.ThrowAsync<InvalidOperationException>(() => DrainScenarioAsync(scope, stop, injected));
        error.ShouldBeSameAs(injected);
        if (!stop) scope.GraceWaitObservedBeforeExit.ShouldBeTrue();
        scope.CleanupCompleted.ShouldBeTrue();
        scope.Tasks.ShouldAllBe(t => t.Task.IsCompleted);
        scope.Processes.ShouldAllBe(p => p.HasExited && p.DisposeCallCount == 1);
    }

    [Fact]
    public async Task DrainScenario_WhenBodyAndCleanupThrow_PreservesBothErrors()
    {
        var scope = new TestScope();
        var body = new InvalidOperationException("R6 body injection");
        var cleanup = new IOException("R6 cleanup injection");
        // This runs after real cleanup; it cannot prevent resource release.
        scope.AfterCleanup = () => throw cleanup;
        var error = await Should.ThrowAsync<AggregateException>(() => DrainScenarioAsync(scope, false, body));
        error.InnerExceptions.ShouldContain(body);
        error.InnerExceptions.ShouldContain(cleanup);
        scope.GraceWaitObservedBeforeExit.ShouldBeTrue();
        scope.CleanupCompleted.ShouldBeTrue();
        scope.Tasks.ShouldAllBe(t => t.Task.IsCompleted);
    }

    private static Task DrainScenarioAsync(TestScope scope, bool stop, Exception? injected) => scope.RunAsync(async () =>
    {
        var stderr = new GatedReadStream();
        scope.Release.Add(stderr.Release);
        var process = scope.Own(new FakeProcessAdapter { StandardErrorOverride = stderr });
        var disposing = Gate();
        var waiting = Gate();
        ((TrackedDisposable)process.StandardOutputDisposable).OnDispose = () => disposing.TrySetResult();
        process.OnWaitForExit = () => waiting.TrySetResult();
        RealUiaProcessSession? session = null;
        var reader = scope.CreateReader(process, (p, drain) =>
        {
            scope.Track(drain);
            return session = new RealUiaProcessSession(p, drain);
        });
        var read = scope.Track(reader.ReadAsync(0x1000, 4242), typeof(OperationCanceledException));
        process.EnqueueReady();
        var request = await RequestAsync(process);
        await Entered(stderr.Entered.Task);
        Task? stopping = null;
        if (stop)
        {
            stopping = scope.Track(reader.StopAsync());
            await Entered(waiting.Task);
            await Entered(stderr.Closed.Task);
            stopping.IsCompleted.ShouldBeFalse();
        }
        else
        {
            // Force the previously racing ordering: the reader consumes Reply before exit.
            // Its real WaitForExit also owns stderr drain, so stdout disposal cannot precede
            // releasing stderr on this path. Do not depend on a non-advanced fake timeout.
            process.EnqueueReply(new UiaReplyFrame
            {
                ProtocolVersion = 1, Kind = "Reply", RequestId = request.RequestId,
                Status = UiaSelectionStatus.Success, Text = "R6 recovered text"
            });
            await Entered(waiting.Task);
            process.HasExited.ShouldBeFalse();
            scope.GraceWaitObservedBeforeExit = true;
            process.SignalExit();
            disposing.Task.IsCompleted.ShouldBeFalse();
        }
        stderr.Completed.Task.IsCompleted.ShouldBeFalse();
        read.IsCompleted.ShouldBeFalse();
        if (injected != null) ExceptionDispatchInfo.Capture(injected).Throw();
        stderr.Release();
        await Entered(disposing.Task);
        if (stop)
        {
            await stopping!;
            await Should.ThrowAsync<OperationCanceledException>(() => read);
        }
        else
        {
            var result = await read;
            result.Status.ShouldBe(UiaSelectionStatus.Success);
            result.Text.ShouldBe("R6 recovered text");
        }
        await stderr.Completed.Task;
        session.ShouldNotBeNull().IsFullyDisposed.ShouldBeTrue();
        process.DisposeCallCount.ShouldBe(1);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task ReadAsync_WhenRealRunnerWrappingFails_WaitsForActualStderrDrain(bool stop) => new TestScope().RunAsync(async scope =>
    {
        var stderr = new GatedReadStream();
        scope.Release.Add(stderr.Release);
        var process = scope.Own(new FakeProcessAdapter { StandardErrorOverride = stderr });
        var waiting = Gate();
        process.OnWaitForExit = () => waiting.TrySetResult();
        var captured = new TaskCompletionSource<Task>(TaskCreationOptions.RunContinuationsAsynchronously);
        var reader = scope.CreateReader(process, (_, drain) =>
        {
            scope.Track(drain);
            captured.TrySetResult(drain);
            throw new InvalidOperationException("R6 wrapping failure");
        });
        var read = scope.Track(reader.ReadAsync(0x1000, 4242), typeof(OperationCanceledException));
        var actualDrain = await captured.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await Entered(stderr.Entered.Task);
        await Entered(waiting.Task);
        await Entered(stderr.Closed.Task);
        process.HasExited.ShouldBeTrue();
        actualDrain.IsCompleted.ShouldBeFalse();
        read.IsCompleted.ShouldBeFalse();
        var stopping = stop ? scope.Track(reader.StopAsync()) : null;
        if (stop) await WaitForLateStartAwaitAsync(reader);
        stopping?.IsCompleted.ShouldBeFalse();
        stderr.Release();
        if (stop)
        {
            await stopping!;
            await Should.ThrowAsync<OperationCanceledException>(() => read);
        }
        else
        {
            var result = await read;
            result.Status.ShouldBe(UiaSelectionStatus.Failed);
            result.ErrorCode.ShouldBe("PROCESS_START_FAILED");
        }
        await actualDrain;
        await stderr.Completed.Task;
        process.DisposeCallCount.ShouldBe(1);
    });

    [Theory]
    [InlineData("waitFalse")]
    [InlineData("killThrows")]
    [InlineData("waitThrows")]
    [InlineData("unstartedDispose")]
    public Task ReadAsync_WhenStartupCleanupFails_RetryRestoresSameReaderProtocol(string fault) => new TestScope().RunAsync(async scope =>
    {
        var process = scope.Own(new FakeProcessAdapter());
        process.StartResult = fault != "unstartedDispose";
        process.WaitForExitResult = fault != "waitFalse";
        if (fault == "killThrows") process.ThrowOnKill = new IOException("R6 Kill fault");
        if (fault == "waitThrows") process.ThrowOnWaitForExit = new IOException("R6 Wait fault");
        if (fault == "unstartedDispose") process.ThrowOnDispose = new IOException("R6 Dispose fault");
        scope.Release.Add(() => Restore(process));
        var next = scope.Own(new FakeProcessAdapter());
        int starts = 0;
        var runner = new RealUiaProcessRunner(typeof(RealUiaProcessRunner).Assembly.Location,
            _ => Interlocked.Increment(ref starts) == 1 ? process : next,
            (p, drain) =>
            {
                scope.Track(drain);
                if (ReferenceEquals(p, process)) throw new InvalidOperationException("R6 wrapping failure");
                return new RealUiaProcessSession(p, drain);
            });
        var reader = scope.CreateReader(runner);
        var error = await Should.ThrowAsync<IrisException>(() => reader.ReadAsync(0x1000, 4242));
        error.Code.ShouldBe(ErrorCode.CaptureFailed);
        error.Message.ShouldContain("Child process startup cleanup failed");
        error.InnerException.ShouldBeNull();
        process.DisposeCallCount.ShouldBe(fault == "unstartedDispose" ? 1 : 0);
        process.KillCallCount.ShouldBe(fault == "unstartedDispose" ? 0 : 1);
        process.WaitForExitCallCount.ShouldBe(fault is "killThrows" or "unstartedDispose" ? 0 : 1);
        await Should.ThrowAsync<IrisException>(() => reader.ReadAsync(0x1000, 4242));
        starts.ShouldBe(1);
        Restore(process);
        if (process.StartResult) process.SignalExit();
        await scope.Track(reader.RetryCleanupAsync());
        process.DisposeCallCount.ShouldBe(fault == "unstartedDispose" ? 2 : 1);
        if (fault == "unstartedDispose")
        {
            process.KillCallCount.ShouldBe(0);
            process.WaitForExitCallCount.ShouldBe(0);
        }
        var recovered = scope.Track(reader.ReadAsync(0x1000, 4242), typeof(OperationCanceledException));
        next.EnqueueReady();
        ReplyAndExit(next, await RequestAsync(next));
        var result = await recovered;
        result.Status.ShouldBe(UiaSelectionStatus.Success);
        result.Text.ShouldBe("R6 recovered text");
        starts.ShouldBe(2);
        next.DisposeCallCount.ShouldBe(1);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task ReadAsync_WhenDelayedStartCleanupFails_AfterTimeoutOrCancellation_RetainsOwner(bool cancel) => new TestScope().RunAsync(async scope =>
    {
        using var caller = new CancellationTokenSource();
        var time = new FakeTimeProvider();
        var process = scope.Own(new FakeProcessAdapter());
        var owner = new RealUiaProcessSession(process, Task.CompletedTask);
        var start = new TaskCompletionSource<IUiaProcessSession>(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = Gate();
        var canceled = Gate();
        CancellationTokenRegistration registration = default;
        scope.AfterJoin.Add(() => registration.Dispose());
        scope.Release.Add(() => start.TrySetException(new ProcessStartupCleanupException(owner, "R6 late cleanup")));
        var runner = new FakeUiaProcessRunner
        {
            StartAsyncFunc = token =>
            {
                registration = token.Register(() => canceled.TrySetResult());
                entered.TrySetResult();
                return start.Task;
            }
        };
        var reader = scope.CreateReader(runner, time);
        var read = scope.Track(reader.ReadAsync(0x1000, 4242, caller.Token), typeof(IrisException));
        await Entered(entered.Task);
        // Observe the real WaitAsync continuation, so cancellation cannot race ahead
        // of installing the start wait and accidentally test the immediate-fault path.
        await WaitUntilAsync(() => typeof(Task).GetField("m_continuationObject", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(start.Task) != null);
        start.Task.IsCompleted.ShouldBeFalse();
        read.IsCompleted.ShouldBeFalse();
        if (cancel) caller.Cancel();
        else time.Advance(TimeSpan.FromMilliseconds(1600));
        await Entered(canceled.Task);
        await WaitForLateStartAwaitAsync(reader);
        // The reader has left WaitAsync and is now awaiting the actual late Start Task.
        read.IsCompleted.ShouldBeFalse();
        start.TrySetException(new ProcessStartupCleanupException(owner, "R6 late cleanup"));
        var error = await Should.ThrowAsync<IrisException>(() => read);
        error.Code.ShouldBe(ErrorCode.CaptureFailed);
        error.Message.ShouldContain("Child process startup cleanup failed");
        process.DisposeCallCount.ShouldBe(0);
        await Should.ThrowAsync<IrisException>(() => reader.ReadAsync(0x1000, 4242));
        await scope.Track(reader.RetryCleanupAsync());
        owner.IsFullyDisposed.ShouldBeTrue();
        process.DisposeCallCount.ShouldBe(1);
    });

    [Fact]
    public Task DisposeAsync_WhenOnlyActualDrainFaults_RetryDoesNotRepeatSuccessfulDisposals() => new TestScope().RunAsync(async scope =>
    {
        var stderr = new GatedReadStream { ReadError = new IOException("R6 stderr fault") };
        scope.Release.Add(stderr.Release);
        var process = scope.Own(new FakeProcessAdapter { StandardErrorOverride = stderr });
        var stdin = (TrackedDisposable)process.StandardInputDisposable;
        var stdout = (TrackedDisposable)process.StandardOutputDisposable;
        var disposing = Gate();
        stdout.OnDispose = () => disposing.TrySetResult();
        var reader = scope.CreateReader(process, (p, drain) =>
        {
            scope.Track(drain, typeof(IOException));
            return new RealUiaProcessSession(p, drain);
        });
        var read = scope.Track(reader.ReadAsync(0x1000, 4242), typeof(IrisException));
        process.EnqueueReady();
        ReplyAndExit(process, await RequestAsync(process));
        await Entered(stderr.Entered.Task);
        await Entered(disposing.Task);
        stderr.Release();
        var error = await Should.ThrowAsync<IrisException>(() => read);
        error.Code.ShouldBe(ErrorCode.CaptureFailed);
        error.Message.ShouldContain("CLEANUP_FAILED phase=DISPOSE");
        error.InnerException.ShouldBeNull();
        await stderr.Completed.Task;
        stdin.DisposeCallCount.ShouldBe(1);
        stdout.DisposeCallCount.ShouldBe(1);
        process.DisposeCallCount.ShouldBe(1);
        await scope.Track(reader.RetryCleanupAsync());
        stdin.DisposeCallCount.ShouldBe(1);
        stdout.DisposeCallCount.ShouldBe(1);
        process.DisposeCallCount.ShouldBe(1);
    });

    [Fact]
    public Task RetryCleanupAsync_WhenActualDrainPending_ConcurrentRetryAndStopJoinSameAttempt() => new TestScope().RunAsync(async scope =>
    {
        var stderr = new GatedReadStream();
        scope.Release.Add(stderr.Release);
        var process = scope.Own(new FakeProcessAdapter
        {
            StandardErrorOverride = stderr,
            ThrowOnWaitForExit = new IOException("R6 rollback wait fault")
        });
        scope.Release.Add(() => Restore(process));
        var disposing = Gate();
        var stdin = (TrackedDisposable)process.StandardInputDisposable;
        var stdout = (TrackedDisposable)process.StandardOutputDisposable;
        stdout.OnDispose = () => disposing.TrySetResult();
        var reader = scope.CreateReader(process, (_, drain) =>
        {
            scope.Track(drain);
            throw new InvalidOperationException("R6 wrapping failure");
        });
        var error = await Should.ThrowAsync<IrisException>(() => reader.ReadAsync(0x1000, 4242));
        error.Code.ShouldBe(ErrorCode.CaptureFailed);
        await Entered(stderr.Entered.Task);
        Restore(process);
        var retry1 = scope.Track(reader.RetryCleanupAsync());
        await Entered(disposing.Task);
        retry1.IsCompleted.ShouldBeFalse();
        stderr.Completed.Task.IsCompleted.ShouldBeFalse();
        // Read-only observation of the real coordinator's Task: count actual awaiters,
        // not merely two scheduled calls that might run after cleanup has finished.
        var coordinator = typeof(UiaSelectionReader).GetField("_failedCleanupCoordinator", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(reader)!;
        var attemptField = coordinator.GetType().GetField("_activeCleanupTask", BindingFlags.Instance | BindingFlags.NonPublic)!;
        Task? pendingAttempt = null;
        // Dispose can enter synchronously inside ExecuteCleanupCoreAsync, before its caller
        // publishes the returned Task field. Observe that publication before counting awaiters.
        await WaitUntilAsync(() => (pendingAttempt = (Task?)attemptField.GetValue(coordinator)) != null);
        var attempt = pendingAttempt!;
        await WaitUntilAsync(() => ContinuationCount(attempt) >= 1);
        var retry2 = scope.Track(reader.RetryCleanupAsync());
        await WaitUntilAsync(() => ContinuationCount(attempt) >= 2);
        var stop = scope.Track(reader.StopAsync());
        await WaitUntilAsync(() => ContinuationCount(attempt) >= 3);
        reader.StopAsync().ShouldBeSameAs(stop);
        retry1.IsCompleted.ShouldBeFalse();
        retry2.IsCompleted.ShouldBeFalse();
        stop.IsCompleted.ShouldBeFalse();
        stdin.DisposeCallCount.ShouldBe(1);
        stdout.DisposeCallCount.ShouldBe(1);
        process.DisposeCallCount.ShouldBe(0);
        stderr.Release();
        await Task.WhenAll(retry1, retry2, stop);
        attempt.IsCompletedSuccessfully.ShouldBeTrue();
        stdin.DisposeCallCount.ShouldBe(1);
        stdout.DisposeCallCount.ShouldBe(1);
        process.DisposeCallCount.ShouldBe(1);
        await Should.ThrowAsync<InvalidOperationException>(() => reader.ReadAsync(0x1000, 4242));
    });

    private static int ContinuationCount(Task task)
    {
        var continuation = typeof(Task).GetField("m_continuationObject", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(task);
        return continuation is System.Collections.ICollection list ? list.Count : continuation == null ? 0 : 1;
    }

    private static async Task WaitForLateStartAwaitAsync(UiaSelectionReader reader)
    {
        var operation = typeof(UiaSelectionReader).GetField("_activeOperation", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(reader)!;
        var start = (Task)operation.GetType().GetProperty("StartTask")!.GetValue(operation)!;
        await WaitUntilAsync(() =>
        {
            var continuation = typeof(Task).GetField("m_continuationObject", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(start);
            // WaitAsync installs CancellationPromise; direct await in the late-start
            // branch installs the reader's async state-machine box instead.
            return HasReaderContinuation(continuation);
        });
    }

    private static bool HasReaderContinuation(object? continuation)
    {
        if (continuation is System.Collections.IList list)
        {
            for (int i = 0; i < list.Count; i++)
                if (HasReaderContinuation(list[i])) return true;
            return false;
        }
        if (continuation is Action action) return HasReaderContinuation(action.Target);
        return continuation?.GetType().Name.Contains("AsyncStateMachineBox", StringComparison.Ordinal) == true;
    }

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!predicate())
        {
            timeout.Token.ThrowIfCancellationRequested();
            await Task.Yield();
        }
    }

    private static async Task<UiaRequestFrame> RequestAsync(FakeProcessAdapter process)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var line = await UiaProtocolFrameReader.ReadLineWithLimitAsync(process.TestStdinReader, UiaProtocolConstants.MaxFrameBytes, timeout.Token);
        return UiaProtocolCodec.DeserializeRequest(line!)!;
    }

    private static void ReplyAndExit(FakeProcessAdapter process, UiaRequestFrame request)
    {
        process.EnqueueReplyAndExit(new UiaReplyFrame
        {
            ProtocolVersion = 1, Kind = "Reply", RequestId = request.RequestId,
            Status = UiaSelectionStatus.Success, Text = "R6 recovered text"
        });
    }

    private static void Restore(FakeProcessAdapter process)
    {
        process.ThrowOnDispose = null;
        process.ThrowOnKill = null;
        process.ThrowOnWaitForExit = null;
        process.WaitForExitResult = true;
    }

    // External stream only: Close is observable but intentionally cannot finish a
    // pending read. The test owns release; the real runner owns the drain loop.
    private sealed class GatedReadStream : Stream
    {
        private readonly TaskCompletionSource _release = Gate();
        public TaskCompletionSource Entered { get; } = Gate();
        public TaskCompletionSource Closed { get; } = Gate();
        public TaskCompletionSource Completed { get; } = Gate();
        public Exception? ReadError { get; init; }
        public void Release() => _release.TrySetResult();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Entered.TrySetResult();
            try
            {
                await _release.Task;
                if (ReadError != null) ExceptionDispatchInfo.Capture(ReadError).Throw();
                return 0;
            }
            finally { Completed.TrySetResult(); }
        }
        protected override void Dispose(bool disposing) { Closed.TrySetResult(); base.Dispose(disposing); }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class TestScope
    {
        public List<Action> Release { get; } = [];
        public List<Action> AfterJoin { get; } = [];
        public List<FakeProcessAdapter> Processes { get; } = [];
        public List<(Task Task, Type? Expected)> Tasks { get; } = [];
        private readonly List<UiaSelectionReader> _readers = [];
        public Action? AfterCleanup { get; set; }
        public bool CleanupCompleted { get; private set; }
        public bool GraceWaitObservedBeforeExit { get; set; }
        public FakeProcessAdapter Own(FakeProcessAdapter process) { Processes.Add(process); return process; }
        public T Track<T>(T task, Type? expected = null) where T : Task
        {
            lock (Tasks) Tasks.Add((task, expected));
            return task;
        }
        public UiaSelectionReader CreateReader(FakeProcessAdapter process, Func<IProcessAdapter, Task, IUiaProcessSession> factory) =>
            CreateReader(new RealUiaProcessRunner(typeof(RealUiaProcessRunner).Assembly.Location, _ => process, factory));
        public UiaSelectionReader CreateReader(IUiaProcessRunner runner, TimeProvider? time = null)
        {
            var reader = new UiaSelectionReader(runner, new FakeTargetWindowValidator(), time ?? new FakeTimeProvider());
            _readers.Add(reader);
            return reader;
        }
        public Task RunAsync(Func<TestScope, Task> body) => RunAsync(() => body(this));
        public async Task RunAsync(Func<Task> body)
        {
            Exception? primary = null;
            var errors = new List<Exception>();
            try { await body(); }
            catch (Exception ex) { primary = ex; }
            finally
            {
                // No awaits before releasing EVERY gate and restoring fault switches.
                foreach (var release in Release)
                    try { release(); } catch (Exception ex) { errors.Add(ex); }
                foreach (var process in Processes)
                    try { Restore(process); if (!process.HasExited && process.DisposeCallCount == 0) process.SignalExit(); }
                    catch (Exception ex) { errors.Add(ex); }
                foreach (var reader in _readers)
                    try { await reader.StopAsync(); } catch (Exception ex) { errors.Add(ex); }
                // Stop has joined the worker, so no more tasks can be registered.
                foreach (var (task, expected) in Tasks)
                    try { await task; }
                    catch (Exception ex) { if (expected == null || !expected.IsInstanceOfType(ex)) errors.Add(ex); }
                foreach (var reader in _readers)
                {
                    try { await reader.RetryCleanupAsync(); } catch (Exception ex) { errors.Add(ex); }
                    try { await reader.DisposeAsync(); } catch (Exception ex) { errors.Add(ex); }
                }
                foreach (var process in Processes)
                    try { if (process.DisposeCallCount == 0) process.Dispose(); } catch (Exception ex) { errors.Add(ex); }
                foreach (var dispose in AfterJoin)
                    try { dispose(); } catch (Exception ex) { errors.Add(ex); }
                CleanupCompleted = true;
                try { AfterCleanup?.Invoke(); } catch (Exception ex) { errors.Add(ex); }
            }
            if (primary != null && errors.Count == 0) ExceptionDispatchInfo.Capture(primary).Throw();
            if (primary != null) errors.Insert(0, primary);
            if (errors.Count != 0) throw new AggregateException("R6 body/cleanup failures", errors);
        }
    }
}
