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
}
