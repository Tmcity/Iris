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
}
