using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Iris.Platform.Windows.Selection;
using Shouldly;
using Xunit;

namespace Iris.Platform.Windows.Tests.Selection;

public sealed class RealUiaProcessSessionTests
{
    private sealed class FaultableDisposable : IDisposable
    {
        public int DisposeCallCount { get; private set; }
        public bool ShouldThrow { get; set; } = true;

        public void Dispose()
        {
            DisposeCallCount++;
            if (ShouldThrow)
            {
                throw new InvalidOperationException("Controlled dispose failure");
            }
        }
    }

    private sealed class TrackedDisposable : IDisposable
    {
        public int DisposeCallCount { get; private set; }

        public void Dispose()
        {
            DisposeCallCount++;
        }
    }

    [Fact]
    public async Task DisposeAsync_WhenFirstAttemptFailsSynchronously_AllowsRetryAfterFaultCleared()
    {
        using var stdinStream = new MemoryStream();
        using var stdoutStream = new MemoryStream();
        var faultableStdin = new FaultableDisposable { ShouldThrow = true };
        var trackedStdout = new TrackedDisposable();
        var trackedProcess = new TrackedDisposable();
        var stderrTcs = new TaskCompletionSource();
        stderrTcs.SetResult();

        var session = new RealUiaProcessSession(
            process: null,
            stderrDrainTask: stderrTcs.Task,
            stdinStream: stdinStream,
            stdinDisposable: faultableStdin,
            stdoutStream: stdoutStream,
            stdoutDisposable: trackedStdout,
            processDisposable: trackedProcess);

        // First attempt: stdin throws
        var ex = await Should.ThrowAsync<AggregateException>(async () => await session.DisposeAsync());
        ex.InnerExceptions.Count.ShouldBe(1);
        ex.InnerExceptions[0].ShouldBeOfType<InvalidOperationException>();

        faultableStdin.DisposeCallCount.ShouldBe(1);
        trackedStdout.DisposeCallCount.ShouldBe(1);
        trackedProcess.DisposeCallCount.ShouldBe(1);

        // Clear the fault
        faultableStdin.ShouldThrow = false;

        // Second attempt: retry should attempt uncompleted stdin, and succeed!
        await session.DisposeAsync();

        faultableStdin.DisposeCallCount.ShouldBe(2);
        // Stdout and process were already disposed, should not be called again
        trackedStdout.DisposeCallCount.ShouldBe(1);
        trackedProcess.DisposeCallCount.ShouldBe(1);

        // Third attempt: already fully disposed, returns completed task immediately
        await session.DisposeAsync();
        faultableStdin.DisposeCallCount.ShouldBe(2);
    }

    [Fact]
    public async Task DisposeAsync_WhenMultipleCallersConcurrentlyDispose_SharesInFlightTask()
    {
        using var stdinStream = new MemoryStream();
        using var stdoutStream = new MemoryStream();
        var trackedStdin = new TrackedDisposable();
        var trackedStdout = new TrackedDisposable();
        var stderrTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var session = new RealUiaProcessSession(
            process: null,
            stderrDrainTask: stderrTcs.Task,
            stdinStream: stdinStream,
            stdinDisposable: trackedStdin,
            stdoutStream: stdoutStream,
            stdoutDisposable: trackedStdout);

        var t1 = session.DisposeAsync();
        var t2 = session.DisposeAsync();

        // Both should be awaiting the same task
        t1.IsCompleted.ShouldBeFalse();
        t2.IsCompleted.ShouldBeFalse();

        stderrTcs.SetResult();

        await t1;
        await t2;

        trackedStdin.DisposeCallCount.ShouldBe(1);
        trackedStdout.DisposeCallCount.ShouldBe(1);
    }

    [Fact]
    public async Task DisposeAsync_WhenStderrDrainFaulted_ReportsErrorAndRecordsTerminalStateSoRetryDoesNotReFault()
    {
        using var stdinStream = new MemoryStream();
        using var stdoutStream = new MemoryStream();
        var faultableStdin = new FaultableDisposable { ShouldThrow = true };
        var trackedStdout = new TrackedDisposable();
        var stderrTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        stderrTcs.SetException(new IOException("Stderr stream corrupted"));

        var session = new RealUiaProcessSession(
            process: null,
            stderrDrainTask: stderrTcs.Task,
            stdinStream: stdinStream,
            stdinDisposable: faultableStdin,
            stdoutStream: stdoutStream,
            stdoutDisposable: trackedStdout);

        // First attempt: both stdin and stderr fail
        var ex1 = await Should.ThrowAsync<AggregateException>(async () => await session.DisposeAsync());
        ex1.InnerExceptions.Count.ShouldBe(2);

        // Clear stdin fault
        faultableStdin.ShouldThrow = false;

        // Second attempt: stderr drain was already observed, only stdin needs retry
        await session.DisposeAsync();

        faultableStdin.DisposeCallCount.ShouldBe(2);
        trackedStdout.DisposeCallCount.ShouldBe(1);
    }

    [Fact]
    public async Task WaitForExitAsync_WhenStderrDrainFaults_RecordsObservedAndThrows()
    {
        using var stdinStream = new MemoryStream();
        using var stdoutStream = new MemoryStream();
        var trackedStdin = new TrackedDisposable();
        var trackedStdout = new TrackedDisposable();
        var stderrTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        stderrTcs.SetException(new InvalidOperationException("Stderr read failed"));

        var session = new RealUiaProcessSession(
            process: null,
            stderrDrainTask: stderrTcs.Task,
            stdinStream: stdinStream,
            stdinDisposable: trackedStdin,
            stdoutStream: stdoutStream,
            stdoutDisposable: trackedStdout);

        await Should.ThrowAsync<InvalidOperationException>(async () => await session.WaitForExitAsync(CancellationToken.None));

        // When DisposeAsync runs next, stderrDrainTask is already marked observed and does not fail DisposeAsync
        await session.DisposeAsync();
        trackedStdin.DisposeCallCount.ShouldBe(1);
        trackedStdout.DisposeCallCount.ShouldBe(1);
    }

    private sealed class FakeProcessAdapter : IProcessAdapter
    {
        public bool StartResult { get; set; } = true;
        public Exception? ThrowOnStart { get; set; }
        public bool HasExited { get; set; }
        public int ExitCode { get; set; }
        public bool WaitForExitResult { get; set; } = true;
        public Exception? ThrowOnWaitForExit { get; set; }
        public Exception? ThrowOnKill { get; set; }
        public Exception? ThrowOnDispose { get; set; }

        public int StartCallCount { get; private set; }
        public int KillCallCount { get; private set; }
        public int WaitForExitCallCount { get; private set; }
        public int DisposeCallCount { get; private set; }

        public Stream StandardInput { get; set; } = new MemoryStream();
        public Stream StandardOutput { get; set; } = new MemoryStream();
        public Stream StandardError { get; set; } = new MemoryStream();
        public IDisposable StandardInputDisposable { get; set; } = new TrackedDisposable();
        public IDisposable StandardOutputDisposable { get; set; } = new TrackedDisposable();

        public bool Start()
        {
            StartCallCount++;
            if (ThrowOnStart != null) throw ThrowOnStart;
            return StartResult;
        }

        public void Kill()
        {
            KillCallCount++;
            if (ThrowOnKill != null) throw ThrowOnKill;
        }

        public bool WaitForExit(int milliseconds)
        {
            WaitForExitCallCount++;
            if (ThrowOnWaitForExit != null) throw ThrowOnWaitForExit;
            if (WaitForExitResult) HasExited = true;
            return WaitForExitResult;
        }

        public Task WaitForExitAsync(CancellationToken cancellationToken)
        {
            WaitForExitCallCount++;
            if (ThrowOnWaitForExit != null) throw ThrowOnWaitForExit;
            if (WaitForExitResult) HasExited = true;
            return Task.CompletedTask;
        }

        public void Dispose()
        {
            DisposeCallCount++;
            if (ThrowOnDispose != null) throw ThrowOnDispose;
        }
    }

    [Fact]
    public async Task RealUiaProcessRunner_StartAsync_WhenStartReturnsFalse_DisposesProcess_AndThrows()
    {
        var dummyPath = typeof(RealUiaProcessRunner).Assembly.Location;
        var fakeProcess = new FakeProcessAdapter { StartResult = false };
        var runner = new RealUiaProcessRunner(dummyPath, psi => fakeProcess);

        var ex = await Should.ThrowAsync<InvalidOperationException>(async () => await runner.StartAsync(CancellationToken.None));
        ex.Message.ShouldContain("Failed to start Iris.UiaHost process.");

        fakeProcess.StartCallCount.ShouldBe(1);
        fakeProcess.DisposeCallCount.ShouldBe(1);
    }

    [Fact]
    public async Task RealUiaProcessRunner_StartAsync_WhenStartThrows_DisposesProcess_AndThrows()
    {
        var dummyPath = typeof(RealUiaProcessRunner).Assembly.Location;
        var fakeProcess = new FakeProcessAdapter { ThrowOnStart = new InvalidOperationException("Process start failed at OS level.") };
        var runner = new RealUiaProcessRunner(dummyPath, psi => fakeProcess);

        var ex = await Should.ThrowAsync<InvalidOperationException>(async () => await runner.StartAsync(CancellationToken.None));
        ex.Message.ShouldContain("Process start failed at OS level.");

        fakeProcess.StartCallCount.ShouldBe(1);
        fakeProcess.DisposeCallCount.ShouldBe(1);
    }

    [Fact]
    public async Task RealUiaProcessRunner_StartAsync_WhenWrappingFails_AndProcessExitsCleanly_KillsAndDisposesProcess()
    {
        var dummyPath = typeof(RealUiaProcessRunner).Assembly.Location;
        var fakeProcess = new FakeProcessAdapter { StartResult = true, WaitForExitResult = true };
        var runner = new RealUiaProcessRunner(
            dummyPath,
            psi => fakeProcess,
            (proc, task) => throw new InvalidOperationException("Session wrapping failed."));

        var ex = await Should.ThrowAsync<InvalidOperationException>(async () => await runner.StartAsync(CancellationToken.None));
        ex.Message.ShouldContain("Session wrapping failed.");

        fakeProcess.KillCallCount.ShouldBe(1);
        fakeProcess.WaitForExitCallCount.ShouldBe(1);
        fakeProcess.DisposeCallCount.ShouldBe(1);
    }

    [Fact]
    public async Task RealUiaProcessRunner_StartAsync_WhenWrappingFails_AndProcessWaitTimesOut_ThrowsProcessStartupCleanupException_WithRetainedSession()
    {
        var dummyPath = typeof(RealUiaProcessRunner).Assembly.Location;
        var fakeProcess = new FakeProcessAdapter { StartResult = true, WaitForExitResult = false }; // WaitForExit returns false (timeout)
        var runner = new RealUiaProcessRunner(
            dummyPath,
            psi => fakeProcess,
            (proc, task) => throw new InvalidOperationException("Session wrapping failed."));

        var ex = await Should.ThrowAsync<ProcessStartupCleanupException>(async () => await runner.StartAsync(CancellationToken.None));
        ex.UnconfirmedSession.ShouldNotBeNull();

        fakeProcess.KillCallCount.ShouldBe(1);
        fakeProcess.WaitForExitCallCount.ShouldBe(1);
        fakeProcess.DisposeCallCount.ShouldBe(0); // Not disposed, retained in UnconfirmedSession
    }

    [Fact]
    public async Task RealUiaProcessRunner_StartAsync_WhenWrappingFails_AndKillThrows_ThrowsProcessStartupCleanupException_WithRetainedSession()
    {
        var dummyPath = typeof(RealUiaProcessRunner).Assembly.Location;
        var fakeProcess = new FakeProcessAdapter
        {
            StartResult = true,
            ThrowOnKill = new InvalidOperationException("Kill access denied.")
        };
        var runner = new RealUiaProcessRunner(
            dummyPath,
            psi => fakeProcess,
            (proc, task) => throw new InvalidOperationException("Session wrapping failed."));

        var ex = await Should.ThrowAsync<ProcessStartupCleanupException>(async () => await runner.StartAsync(CancellationToken.None));
        ex.UnconfirmedSession.ShouldNotBeNull();
        ex.InnerException.ShouldNotBeNull();
        ex.InnerException.Message.ShouldContain("Kill access denied.");

        fakeProcess.KillCallCount.ShouldBe(1);
        fakeProcess.DisposeCallCount.ShouldBe(0);
    }
}
