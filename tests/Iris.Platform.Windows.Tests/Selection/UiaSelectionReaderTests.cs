using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Iris.Core.Errors;
using Iris.Platform.Windows.Selection;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;

namespace Iris.Platform.Windows.Tests.Selection;

public sealed class UiaSelectionReaderTests
{
    private readonly FakeUiaProcessRunner _processRunner = new();
    private readonly FakeTargetWindowValidator _targetValidator = new();
    private readonly FakeTimeProvider _timeProvider = new();

    private UiaSelectionReader CreateReader()
    {
        return new UiaSelectionReader(_processRunner, _targetValidator, _timeProvider);
    }

    [Fact]
    public void Constructor_DoesNotCallProcessOrTargetValidator()
    {
        var reader = CreateReader();
        reader.ShouldNotBeNull();
        _processRunner.TotalStartCalls.ShouldBe(0);
        _targetValidator.TotalCalls.ShouldBe(0);
    }

    [Theory]
    [InlineData(0, 4242)]
    [InlineData(0x1000, 0)]
    [InlineData(0, 0)]
    public async Task ReadAsync_WhenInvalidWindowOrPid_ThrowsInvalidArgumentException(nint hwnd, uint pid)
    {
        await using var reader = CreateReader();
        var ex = await Should.ThrowAsync<IrisException>(() => reader.ReadAsync(hwnd, pid, TestContext.Current.CancellationToken));
        ex.Code.ShouldBe(ErrorCode.InvalidArgument);
    }

    [Fact]
    public async Task ReadAsync_WhenTargetNotForegroundPreFlight_ReturnsTargetChanged()
    {
        await using var reader = CreateReader();
        _targetValidator.DefaultIsValid = false;

        var result = await reader.ReadAsync(0x1000, 4242, TestContext.Current.CancellationToken);

        result.Status.ShouldBe(UiaSelectionStatus.TargetChanged);
        result.Text.ShouldBeNull();
        _processRunner.TotalStartCalls.ShouldBe(0);
    }

    [Fact]
    public async Task ReadAsync_WhenHostExecutableNotFound_ReturnsFailedWithSafeCode()
    {
        await using var reader = CreateReader();
        _processRunner.ThrowOnStart = new FileNotFoundException("Iris.UiaHost.exe not found");

        var result = await reader.ReadAsync(0x1000, 4242, TestContext.Current.CancellationToken);

        result.Status.ShouldBe(UiaSelectionStatus.Failed);
        result.ErrorCode.ShouldBe("HOST_EXECUTABLE_NOT_FOUND");
    }

    [Fact]
    public async Task ReadAsync_WithMatchingRequestId_SucceedsAndReleasesResources()
    {
        await using var reader = CreateReader();

        var session = new FakeUiaProcessSession();
        _processRunner.Session = session;

        session.EnqueueReady();

        var readTask = Task.Run(async () => await reader.ReadAsync(0x1000, 4242, TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);

        var reqLine = await UiaProtocolFrameReader.ReadLineWithLimitAsync(session.TestStdinReader, UiaProtocolConstants.MaxFrameBytes, TestContext.Current.CancellationToken);
        var req = UiaProtocolCodec.DeserializeRequest(reqLine!);
        req.ShouldNotBeNull();

        session.EnqueueReply(new UiaReplyFrame
        {
            ProtocolVersion = 1,
            Kind = "Reply",
            RequestId = req.RequestId,
            Status = UiaSelectionStatus.Success,
            Text = "Extracted Text"
        });
        session.SignalExit();

        var result = await readTask;

        result.Status.ShouldBe(UiaSelectionStatus.Success);
        result.Text.ShouldBe("Extracted Text");
        session.Disposed.ShouldBeTrue();
    }

    [Fact]
    public async Task ReadAsync_WhenReplyTimesOut300ms_KillsProcess_AwaitsExit_ReturnsTimedOut()
    {
        await using var reader = CreateReader();

        var session = new FakeUiaProcessSession();
        _processRunner.Session = session;
        session.EnqueueReady();

        var readTask = Task.Run(async () => await reader.ReadAsync(0x1000, 4242, TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);

        // Wait until Request frame is received on stdin so we know 300ms timeout CTS has been registered
        var reqLine = await UiaProtocolFrameReader.ReadLineWithLimitAsync(session.TestStdinReader, UiaProtocolConstants.MaxFrameBytes, TestContext.Current.CancellationToken);
        reqLine.ShouldNotBeNull();

        // Advance past 300ms request timeout
        _timeProvider.Advance(TimeSpan.FromMilliseconds(350));

        var result = await readTask;

        result.Status.ShouldBe(UiaSelectionStatus.TimedOut);
        session.KillCalled.ShouldBeTrue();
        session.Disposed.ShouldBeTrue();
    }

    [Fact]
    public async Task ReadAsync_WhenReadyTimesOut1500ms_KillsProcess_AwaitsExit_ReturnsTimedOut()
    {
        await using var reader = CreateReader();

        var session = new FakeUiaProcessSession();
        _processRunner.Session = session;

        var readTask = Task.Run(async () => await reader.ReadAsync(0x1000, 4242, TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);

        // Wait until Ready frame reading has actually started
        await session.StdoutReadStarted.Task.WaitAsync(TestContext.Current.CancellationToken);

        // Advance past 1500ms ready timeout
        _timeProvider.Advance(TimeSpan.FromMilliseconds(1600));

        var result = await readTask;

        result.Status.ShouldBe(UiaSelectionStatus.TimedOut);
        session.KillCalled.ShouldBeTrue();
        session.Disposed.ShouldBeTrue();
    }

    [Fact]
    public async Task ReadAsync_WhenConcurrentCall_ReturnsBusy()
    {
        await using var reader = CreateReader();

        var session = new FakeUiaProcessSession();
        _processRunner.Session = session;

        var firstTask = Task.Run(async () => await reader.ReadAsync(0x1000, 4242, TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);

        // Wait until first task is actively reading stdout
        await session.StdoutReadStarted.Task.WaitAsync(TestContext.Current.CancellationToken);

        // Second call while first is in-flight
        var secondResult = await reader.ReadAsync(0x1000, 4242, TestContext.Current.CancellationToken);

        secondResult.Status.ShouldBe(UiaSelectionStatus.Busy);
        secondResult.Text.ShouldBeNull();

        // Release first call by advancing time past timeout
        _timeProvider.Advance(TimeSpan.FromMilliseconds(1600));
        var firstResult = await firstTask;
        firstResult.Status.ShouldBe(UiaSelectionStatus.TimedOut);
    }

    [Fact]
    public async Task ReadAsync_WhenTargetChangesPostReply_DiscardsTextAndReturnsTargetChanged()
    {
        await using var reader = CreateReader();

        var session = new FakeUiaProcessSession();
        _processRunner.Session = session;
        session.EnqueueReady();

        _targetValidator.Responses.Enqueue(true);
        _targetValidator.Responses.Enqueue(false);

        var readTask = Task.Run(async () => await reader.ReadAsync(0x1000, 4242, TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);

        var reqLine = await UiaProtocolFrameReader.ReadLineWithLimitAsync(session.TestStdinReader, UiaProtocolConstants.MaxFrameBytes, TestContext.Current.CancellationToken);
        var req = UiaProtocolCodec.DeserializeRequest(reqLine!);

        session.EnqueueReply(new UiaReplyFrame
        {
            ProtocolVersion = 1,
            Kind = "Reply",
            RequestId = req!.RequestId,
            Status = UiaSelectionStatus.Success,
            Text = "Confidential Info"
        });
        session.SignalExit();

        var result = await readTask;

        result.Status.ShouldBe(UiaSelectionStatus.TargetChanged);
        result.Text.ShouldBeNull();
    }

    [Fact]
    public async Task ReadAsync_WhenCancelled_KillsProcess_ThrowsOperationCanceledException()
    {
        await using var reader = CreateReader();

        var session = new FakeUiaProcessSession();
        _processRunner.Session = session;
        session.EnqueueReady();

        using var cts = new CancellationTokenSource();
        var readTask = Task.Run(async () => await reader.ReadAsync(0x1000, 4242, cts.Token), TestContext.Current.CancellationToken);

        await session.StdoutReadStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
        cts.Cancel();

        await Should.ThrowAsync<OperationCanceledException>(async () => await readTask);

        session.KillCalled.ShouldBeTrue();
        session.Disposed.ShouldBeTrue();
    }

    [Fact]
    public async Task StopAsync_PermanentlyRevokesAdmission()
    {
        await using var reader = CreateReader();

        await reader.StopAsync(TestContext.Current.CancellationToken);

        await Should.ThrowAsync<InvalidOperationException>(() => reader.ReadAsync(0x1000, 4242, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ReadAsync_WhenCleanupFails_RetainsSession_ThrowsException_AndAllowsRetry()
    {
        await using var reader = CreateReader();

        var session = new FakeUiaProcessSession { RefuseToExit = true };
        _processRunner.Session = session;

        var readTask = Task.Run(async () => await reader.ReadAsync(0x1000, 4242, TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);

        // Wait until Ready read starts
        await session.StdoutReadStarted.Task.WaitAsync(TestContext.Current.CancellationToken);

        // Advance past 1500ms ready timeout to trigger cleanup
        _timeProvider.Advance(TimeSpan.FromMilliseconds(1600));

        // Wait until Kill has been called and WaitForExitAsync is entered with kill confirmation timer
        await session.WaitForExitStarted.Task.WaitAsync(TestContext.Current.CancellationToken);

        // Advance past 1000ms kill confirmation budget
        _timeProvider.Advance(TimeSpan.FromMilliseconds(1100));

        var ex = await Should.ThrowAsync<IrisException>(async () => await readTask);
        ex.Code.ShouldBe(ErrorCode.CaptureFailed);

        await Should.ThrowAsync<IrisException>(() => reader.ReadAsync(0x1000, 4242, TestContext.Current.CancellationToken));

        session.RefuseToExit = false;
        await reader.RetryCleanupAsync(TestContext.Current.CancellationToken);

        session.SignalExit();
    }

    [Fact]
    public void UiaSelectionResult_ToString_NeverContainsTextOrWindowTitle()
    {
        var secretText = "SuperSecretPassword12345";
        var result = UiaSelectionResult.CreateSuccess("req-1", secretText);

        var str = result.ToString();
        str.ShouldNotContain(secretText);
        str.ShouldContain("Status=Success");
        str.ShouldContain("RequestId=req-1");
    }

    [Fact]
    public void ReadAsync_WhenCalledFromStaThread_ExecutesTargetValidationAndProcessStartOffStaThread()
    {
        int callerThreadId = 0;
        int startThreadId = 0;
        Exception? threadEx = null;

        var thread = new Thread(() =>
        {
            try
            {
                callerThreadId = Environment.CurrentManagedThreadId;
                var session = new FakeUiaProcessSession();
                _processRunner.Session = session;
                session.EnqueueReady();

                _targetValidator.DefaultIsValid = true;
                _targetValidator.Responses.Enqueue(true); // preflight
                _targetValidator.Responses.Enqueue(true); // post-reply

                _processRunner.StartAsyncFunc = ct =>
                {
                    startThreadId = Environment.CurrentManagedThreadId;
                    return Task.FromResult<IUiaProcessSession>(session);
                };

                var reader = CreateReader();
                var readTask = reader.ReadAsync(0x1000, 4242, CancellationToken.None);

                // Drain request on background
                Task.Run(async () =>
                {
                    var reqLine = await UiaProtocolFrameReader.ReadLineWithLimitAsync(session.TestStdinReader, UiaProtocolConstants.MaxFrameBytes, CancellationToken.None);
                    var req = UiaProtocolCodec.DeserializeRequest(reqLine!);
                    session.EnqueueReply(new UiaReplyFrame
                    {
                        ProtocolVersion = 1,
                        Kind = "Reply",
                        RequestId = req!.RequestId,
                        Status = UiaSelectionStatus.Success,
                        Text = "STA Safe Text"
                    });
                    session.SignalExit();
                });

                var result = readTask.GetAwaiter().GetResult();
                result.Status.ShouldBe(UiaSelectionStatus.Success);
                result.Text.ShouldBe("STA Safe Text");
            }
            catch (Exception ex)
            {
                threadEx = ex;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (threadEx != null) throw threadEx;

        startThreadId.ShouldNotBe(0);
        startThreadId.ShouldNotBe(callerThreadId);
    }

    [Fact]
    public async Task ReadAsync_WhenStartTakesTooLong_TimesOutUnderReadyBudget()
    {
        await using var reader = CreateReader();

        var session = new FakeUiaProcessSession();
        var startTcs = new TaskCompletionSource<IUiaProcessSession>(TaskCreationOptions.RunContinuationsAsynchronously);
        var startEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        _processRunner.StartAsyncFunc = ct =>
        {
            startEntered.TrySetResult();
            // Simulate slow process start (finishes late after caller timeout)
            return startTcs.Task;
        };

        var readTask = Task.Run(async () => await reader.ReadAsync(0x1000, 4242, TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);

        await startEntered.Task.WaitAsync(TestContext.Current.CancellationToken);

        // Advance time past 1500ms Ready budget while StartAsync is still in-flight
        _timeProvider.Advance(TimeSpan.FromMilliseconds(1600));

        var result = await readTask;

        result.Status.ShouldBe(UiaSelectionStatus.TimedOut);

        // Later, the delayed child process finishes starting
        startTcs.SetResult(session);

        // Await the asynchronous cleanup task to reach exit wait
        await session.WaitForExitStarted.Task.WaitAsync(TestContext.Current.CancellationToken);

        // Ensure delayed child session is cleaned up
        session.KillCalled.ShouldBeTrue();
    }

    [Fact]
    public async Task ReadAsync_WhenCalledConcurrentlyDuringPreflight_DirectlyReturnsBusy()
    {
        await using var reader = CreateReader();

        var session = new FakeUiaProcessSession();
        _processRunner.Session = session;

        var firstCallEnteredPreflight = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releasePreflight = new ManualResetEventSlim(false);

        _targetValidator.DefaultIsValid = true;
        _targetValidator.OnCall = (hwnd, pid) =>
        {
            if (firstCallEnteredPreflight.TrySetResult())
            {
                releasePreflight.Wait(TestContext.Current.CancellationToken);
            }
        };

        var firstTask = Task.Run(async () => await reader.ReadAsync(0x1000, 4242, TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);

        await firstCallEnteredPreflight.Task.WaitAsync(TestContext.Current.CancellationToken);

        // Second call while first call is in preflight
        var secondResult = await reader.ReadAsync(0x1000, 4242, TestContext.Current.CancellationToken);

        secondResult.Status.ShouldBe(UiaSelectionStatus.Busy);
        secondResult.Text.ShouldBeNull();

        // Release first call preflight
        releasePreflight.Set();

        // Wait until first call starts reading stdout
        await session.StdoutReadStarted.Task.WaitAsync(TestContext.Current.CancellationToken);

        // Release first call via timeout
        _timeProvider.Advance(TimeSpan.FromMilliseconds(1600));
        var firstResult = await firstTask;
        firstResult.Status.ShouldBe(UiaSelectionStatus.TimedOut);

        _processRunner.TotalStartCalls.ShouldBe(1);
    }

    [Fact]
    public async Task ReadAsync_WhenCanceledDuringExitWait_DiscardsTextAndThrowsOperationCanceledException()
    {
        await using var reader = CreateReader();

        var session = new FakeUiaProcessSession { ExitDelay = TimeSpan.FromMilliseconds(500) };
        _processRunner.Session = session;
        session.EnqueueReady();

        using var cts = new CancellationTokenSource();
        var readTask = Task.Run(async () => await reader.ReadAsync(0x1000, 4242, cts.Token), TestContext.Current.CancellationToken);

        var reqLine = await UiaProtocolFrameReader.ReadLineWithLimitAsync(session.TestStdinReader, UiaProtocolConstants.MaxFrameBytes, TestContext.Current.CancellationToken);
        var req = UiaProtocolCodec.DeserializeRequest(reqLine!);

        session.EnqueueReply(new UiaReplyFrame
        {
            ProtocolVersion = 1,
            Kind = "Reply",
            RequestId = req!.RequestId,
            Status = UiaSelectionStatus.Success,
            Text = "Confidential Selection"
        });

        // Wait until exit wait starts
        await session.WaitForExitStarted.Task.WaitAsync(TestContext.Current.CancellationToken);

        // Cancel during exit wait
        cts.Cancel();

        await Should.ThrowAsync<OperationCanceledException>(async () => await readTask);
    }

    [Fact]
    public async Task ReadAsync_WhenStoppedDuringExitWait_DiscardsTextAndThrowsOperationCanceledException()
    {
        await using var reader = CreateReader();

        var session = new FakeUiaProcessSession { ExitDelay = TimeSpan.FromMilliseconds(500) };
        _processRunner.Session = session;
        session.EnqueueReady();

        var readTask = Task.Run(async () => await reader.ReadAsync(0x1000, 4242, TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);

        var reqLine = await UiaProtocolFrameReader.ReadLineWithLimitAsync(session.TestStdinReader, UiaProtocolConstants.MaxFrameBytes, TestContext.Current.CancellationToken);
        var req = UiaProtocolCodec.DeserializeRequest(reqLine!);

        session.EnqueueReply(new UiaReplyFrame
        {
            ProtocolVersion = 1,
            Kind = "Reply",
            RequestId = req!.RequestId,
            Status = UiaSelectionStatus.Success,
            Text = "Confidential Selection"
        });

        // Wait until exit wait starts
        await session.WaitForExitStarted.Task.WaitAsync(TestContext.Current.CancellationToken);

        // Stop reader during exit wait
        var stopTask = reader.StopAsync(TestContext.Current.CancellationToken);

        await Should.ThrowAsync<OperationCanceledException>(async () => await readTask);
        await stopTask;
    }

    [Fact]
    public async Task ReadAsync_WhenChildExitsWithNonZeroExitCode_ReturnsFailedProcessExitUnexpected()
    {
        await using var reader = CreateReader();

        var session = new FakeUiaProcessSession { ExitCode = 7 };
        _processRunner.Session = session;
        session.EnqueueReady();

        var readTask = Task.Run(async () => await reader.ReadAsync(0x1000, 4242, TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);

        var reqLine = await UiaProtocolFrameReader.ReadLineWithLimitAsync(session.TestStdinReader, UiaProtocolConstants.MaxFrameBytes, TestContext.Current.CancellationToken);
        var req = UiaProtocolCodec.DeserializeRequest(reqLine!);

        session.EnqueueReply(new UiaReplyFrame
        {
            ProtocolVersion = 1,
            Kind = "Reply",
            RequestId = req!.RequestId,
            Status = UiaSelectionStatus.Success,
            Text = "Will Be Discarded"
        });
        session.SignalExit();

        var result = await readTask;

        result.Status.ShouldBe(UiaSelectionStatus.Failed);
        result.ErrorCode.ShouldBe("PROCESS_EXIT_UNEXPECTED");
        result.Text.ShouldBeNull();
    }

    [Fact]
    public async Task ReadAsync_WhenChildSendsExtraReplyFrameBeforeExit_ReturnsFailedProtocolError()
    {
        await using var reader = CreateReader();

        var session = new FakeUiaProcessSession();
        _processRunner.Session = session;
        session.EnqueueReady();

        var readTask = Task.Run(async () => await reader.ReadAsync(0x1000, 4242, TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);

        var reqLine = await UiaProtocolFrameReader.ReadLineWithLimitAsync(session.TestStdinReader, UiaProtocolConstants.MaxFrameBytes, TestContext.Current.CancellationToken);
        var req = UiaProtocolCodec.DeserializeRequest(reqLine!);

        session.EnqueueReply(new UiaReplyFrame
        {
            ProtocolVersion = 1,
            Kind = "Reply",
            RequestId = req!.RequestId,
            Status = UiaSelectionStatus.Success,
            Text = "First Reply"
        });

        // Child sends a duplicate/extra reply frame
        session.EnqueueReply(new UiaReplyFrame
        {
            ProtocolVersion = 1,
            Kind = "Reply",
            RequestId = req.RequestId,
            Status = UiaSelectionStatus.Success,
            Text = "Second Malicious Reply"
        });
        session.SignalExit();

        var result = await readTask;

        result.Status.ShouldBe(UiaSelectionStatus.Failed);
        result.ErrorCode.ShouldBe("PROTOCOL_ERROR");
        result.Text.ShouldBeNull();
    }

    [Fact]
    public async Task Dispose_WhenActiveReadInProgress_ThrowsInvalidOperationException()
    {
        var reader = CreateReader();

        var session = new FakeUiaProcessSession();
        _processRunner.Session = session;

        var readTask = Task.Run(async () => await reader.ReadAsync(0x1000, 4242, TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);

        await session.StdoutReadStarted.Task.WaitAsync(TestContext.Current.CancellationToken);

        Should.Throw<InvalidOperationException>(() => reader.Dispose());

        // Cancel and cleanup properly
        _timeProvider.Advance(TimeSpan.FromMilliseconds(1600));
        await readTask;
        await reader.DisposeAsync();
    }

    [Fact]
    public async Task ReadAsync_WhenKillThrows_RetainsFailedSession_BlocksSubsequentReads_AndAllowsRetry()
    {
        await using var reader = CreateReader();

        var session = new FakeUiaProcessSession
        {
            RefuseToExit = true,
            ThrowOnKillException = new InvalidOperationException("Process kill failed due to access denied.")
        };
        _processRunner.Session = session;

        var readTask = Task.Run(async () => await reader.ReadAsync(0x1000, 4242, TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);

        await session.StdoutReadStarted.Task.WaitAsync(TestContext.Current.CancellationToken);

        // Advance ready timeout to trigger kill
        _timeProvider.Advance(TimeSpan.FromMilliseconds(1600));

        // Wait until Kill has been attempted and WaitForExitAsync is entered with kill confirmation timer
        await session.WaitForExitStarted.Task.WaitAsync(TestContext.Current.CancellationToken);

        // Advance kill confirmation budget
        _timeProvider.Advance(TimeSpan.FromMilliseconds(1100));

        var ex = await Should.ThrowAsync<IrisException>(async () => await readTask);
        ex.Code.ShouldBe(ErrorCode.CaptureFailed);

        // Subsequent read is blocked with CaptureFailed
        await Should.ThrowAsync<IrisException>(() => reader.ReadAsync(0x1000, 4242, TestContext.Current.CancellationToken));

        // Retry cleanup clears error once exit is permitted
        session.ThrowOnKillException = null;
        session.RefuseToExit = false;
        await reader.RetryCleanupAsync(TestContext.Current.CancellationToken);

        session.SignalExit();
    }

    [Theory]
    [InlineData("NORMAL_CODE", "NORMAL_CODE")]
    [InlineData("COM_0x80040204", "COM_0x80040204")]
    [InlineData("INVALID lowercase", "INVALID_ERROR_CODE")]
    [InlineData("CODE_WITH_SPACES AND MUCH LONGER TEXT", "INVALID_ERROR_CODE")]
    public void UiaSelectionResult_SanitizesArbitraryErrorCode_InResultAndToString(string inputCode, string expectedCode)
    {
        var result = UiaSelectionResult.CreateFailed("req-1", inputCode);
        result.ErrorCode.ShouldBe(expectedCode);
        result.ToString().ShouldContain($"ErrorCode={expectedCode}");
    }
}
