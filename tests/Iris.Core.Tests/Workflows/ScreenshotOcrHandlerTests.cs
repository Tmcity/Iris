using Iris.Core.Actions;
using Iris.Core.Errors;
using Iris.Core.Imaging;
using Iris.Core.Ocr;
using Iris.Core.Platform;
using Iris.Core.Presentation;
using Iris.Core.Settings;
using Iris.Core.Translation;
using Iris.Core.Workflows;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;

namespace Iris.Core.Tests.Workflows;

public sealed class ScreenshotOcrHandlerTests
{
    private sealed class Fixture
    {
        internal readonly ITranslateWindowPresenter Presenter = Substitute.For<ITranslateWindowPresenter>();
        internal readonly ITranslateWindowOperation Operation = Substitute.For<ITranslateWindowOperation>();
        internal readonly IScreenshotService Screenshot = Substitute.For<IScreenshotService>();
        internal readonly IOcrService Ocr = Substitute.For<IOcrService>();
        internal readonly ISettingsService Settings = Substitute.For<ISettingsService>();
        internal readonly IClipboardService Clipboard = Substitute.For<IClipboardService>();
        internal readonly IScreenshotOcrFeedback Feedback = Substitute.For<IScreenshotOcrFeedback>();
        internal readonly List<TranslateWindowRequest> Requests = [];
        internal readonly ScreenshotResult Shot = new(new([1, 2, 3], ImageFormat.Png, 40, 50), new(-100, 200, 40, 50));

        internal Fixture()
        {
            Settings.Current.Returns(new AppSettings { Ocr = new() { AutoCopyOnOcr = true } });
            Presenter.TryBeginOperation(Arg.Any<CancellationToken>()).Returns(Operation);
            Presenter.TryShowAsync(Operation, Arg.Any<TranslateWindowRequest>()).Returns(c =>
            { Requests.Add(c.Arg<TranslateWindowRequest>()); return true; });
            Screenshot.CaptureRegionAsync(Arg.Any<ScreenshotPurpose>(), Arg.Any<CancellationToken>()).Returns(Shot);
            Ocr.RecognizeAsync(Shot.Image, null, Arg.Any<CancellationToken>()).Returns(Outcome("  原文😀\ntext  ", true));
        }

        internal IActionHandler Handler(int mode) => mode switch
        {
            0 => new ScreenshotTranslateHandler(Presenter, Screenshot, Ocr, NullLogger<ScreenshotTranslateHandler>.Instance),
            1 => new ScreenshotOcrHandler(Presenter, Screenshot, Ocr, Settings, NullLogger<ScreenshotOcrHandler>.Instance),
            _ => new SilentScreenshotOcrHandler(Screenshot, Ocr, Clipboard, Settings, Feedback, NullLogger<SilentScreenshotOcrHandler>.Instance)
        };
        internal Task Run(int mode, CancellationToken token = default) => Handler(mode).ExecuteAsync(new(ActionTrigger.Hotkey), token);
    }

    private static OcrOutcome Outcome(string text, bool fallback = false) => new(new("opaque-provider", [], null, TimeSpan.Zero), text, fallback);

    [Fact]
    public async Task SilentCancellation_KeepsHandlerUntilForwardedCallbacksExitAndReportsFailure()
    {
        var f = new Fixture(); using var caller = new CancellationTokenSource(); using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var recognition = new TaskCompletionSource<OcrOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationTokenRegistration registration = default;
        f.Ocr.RecognizeAsync(f.Shot.Image, null, Arg.Any<CancellationToken>()).Returns(c =>
        {
            registration = c.Arg<CancellationToken>().Register(() =>
            { entered.TrySetResult(); release.Wait(); throw new InvalidOperationException("private callback"); });
            return recognition.Task;
        });
        var work = f.Run(2, caller.Token);
        try
        {
            caller.Cancel(); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            recognition.TrySetResult(Outcome("late"));
            await Task.Delay(30, TestContext.Current.CancellationToken); work.IsCompleted.ShouldBeFalse();
            release.Set(); var failure = await Should.ThrowAsync<IrisException>(() => work);
            failure.Code.ShouldBe(ErrorCode.Unknown); failure.Message.ShouldBe(ErrorMessages.GetDefault(ErrorCode.Unknown));
            await f.Clipboard.DidNotReceive().SetTextAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        }
        finally
        {
            release.Set(); recognition.TrySetResult(Outcome("cleanup"));
            try { await work; } catch (IrisException) { }
            await registration.DisposeAsync();
        }
    }

    [Theory][InlineData(0)][InlineData(1)][InlineData(2)]
    public async Task Success_PreservesTextPurposeIdentityAnchorAndActionSemantics(int mode)
    {
        var f = new Fixture(); await f.Run(mode, TestContext.Current.CancellationToken);
        await f.Screenshot.Received(1).CaptureRegionAsync((ScreenshotPurpose)mode, Arg.Any<CancellationToken>());
        await f.Ocr.Received(1).RecognizeAsync(f.Shot.Image, null, Arg.Any<CancellationToken>());
        if (mode == 2)
        {
            f.Presenter.DidNotReceive().TryBeginOperation(Arg.Any<CancellationToken>());
            f.Requests.ShouldBeEmpty();
            await f.Clipboard.Received(1).SetTextAsync("  原文😀\ntext  ", Arg.Any<CancellationToken>());
            await f.Feedback.Received(1).TryNotifyCopiedAsync("  原文😀\ntext  ".Length, Arg.Any<CancellationToken>());
        }
        else
        {
            f.Requests.Count.ShouldBe(2);
            var loading = f.Requests[0]; var result = f.Requests[1];
            loading.Text.ShouldBeNull(); loading.AutoTranslate.ShouldBeFalse(); loading.StatusMessage.ShouldBe("正在识别文字…");
            result.Text.ShouldBe("  原文😀\ntext  "); result.AutoTranslate.ShouldBe(mode == 0);
            result.AutoCopySource.ShouldBe(mode == 1); result.NotifyOcrFallback.ShouldBeTrue();
            f.Requests.ShouldAllBe(r => r.Origin == TextOrigin.Screenshot && r.Anchor == new PixelPoint(-60, 250));
            f.Operation.Received(1).Dispose();
            await f.Clipboard.DidNotReceive().SetTextAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        }
    }

    [Theory][InlineData(0)][InlineData(1)]
    public async Task RejectedAdmission_DoesNotCapture(int mode)
    {
        var f = new Fixture(); f.Presenter.TryBeginOperation(Arg.Any<CancellationToken>()).Returns((ITranslateWindowOperation?)null);
        await f.Run(mode, TestContext.Current.CancellationToken);
        await f.Screenshot.DidNotReceive().CaptureRegionAsync(Arg.Any<ScreenshotPurpose>(), Arg.Any<CancellationToken>());
    }

    [Theory][InlineData(0)][InlineData(1)][InlineData(2)]
    public async Task CaptureCanceled_DoesNotShowOrRecognize(int mode)
    {
        var f = new Fixture(); f.Screenshot.CaptureRegionAsync(Arg.Any<ScreenshotPurpose>(), Arg.Any<CancellationToken>()).Returns((ScreenshotResult?)null);
        await f.Run(mode, TestContext.Current.CancellationToken); f.Requests.ShouldBeEmpty();
        await f.Ocr.DidNotReceive().RecognizeAsync(Arg.Any<ImageData>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Theory][InlineData(0, false)][InlineData(1, false)][InlineData(2, false)]
    [InlineData(0, true)][InlineData(1, true)][InlineData(2, true)]
    public async Task Failure_UsesSafeDefaultAtCorrectStage(int mode, bool duringOcr)
    {
        var f = new Fixture(); var error = new IrisException(ErrorCode.NetworkUnavailable, "private text", detail: "private detail");
        if (duringOcr) f.Ocr.RecognizeAsync(f.Shot.Image, null, Arg.Any<CancellationToken>()).Returns<Task<OcrOutcome>>(_ => throw error);
        else f.Screenshot.CaptureRegionAsync(Arg.Any<ScreenshotPurpose>(), Arg.Any<CancellationToken>()).Returns<Task<ScreenshotResult?>>(_ => throw error);
        await f.Run(mode, TestContext.Current.CancellationToken);
        if (mode == 2) await f.Feedback.Received(1).TryNotifyErrorAsync(error.Code, Arg.Any<CancellationToken>());
        else if (!duringOcr)
        {
            f.Requests.ShouldBeEmpty(); await f.Presenter.Received(1).TryNotifyAsync(f.Operation, NotificationLevel.Error, error.Code);
        }
        else
        {
            var result = f.Requests.Last(); result.Text.ShouldBeNull(); result.AutoTranslate.ShouldBeFalse();
            result.AutoCopySource.ShouldBeFalse(); result.NotifyOcrFallback.ShouldBeFalse();
            result.Error.ShouldBe(new ErrorInfo(error.Code, ErrorMessages.GetDefault(error.Code)));
        }
        await f.Clipboard.DidNotReceive().SetTextAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Theory][InlineData(0)][InlineData(1)][InlineData(2)]
    public async Task BlankOcr_IsNoTextErrorAndNeverCopies(int mode)
    {
        var f = new Fixture(); f.Ocr.RecognizeAsync(f.Shot.Image, null, Arg.Any<CancellationToken>()).Returns(Outcome(" \n\t"));
        await f.Run(mode, TestContext.Current.CancellationToken);
        if (mode == 2) await f.Feedback.Received(1).TryNotifyErrorAsync(ErrorCode.OcrNoTextFound, Arg.Any<CancellationToken>());
        else { f.Requests.Last().Text.ShouldBeNull(); f.Requests.Last().Error!.Code.ShouldBe(ErrorCode.OcrNoTextFound); }
        await f.Clipboard.DidNotReceive().SetTextAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Theory][InlineData(0)][InlineData(1)]
    public async Task RejectedLoading_DoesNotRecognizeAndReturnsLease(int mode)
    {
        var f = new Fixture(); f.Presenter.TryShowAsync(f.Operation, Arg.Any<TranslateWindowRequest>()).Returns(false);
        await f.Run(mode, TestContext.Current.CancellationToken); f.Operation.Received(1).Dispose();
        await f.Ocr.DidNotReceive().RecognizeAsync(Arg.Any<ImageData>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Theory][InlineData(1)][InlineData(2)]
    public async Task Settings_AreCapturedBeforeScreenshot(int mode)
    {
        var f = new Fixture();
        f.Screenshot.CaptureRegionAsync(Arg.Any<ScreenshotPurpose>(), Arg.Any<CancellationToken>()).Returns(_ =>
        { f.Settings.Current.Returns(new AppSettings { Ocr = new() { AutoCopyOnOcr = false, SilentOcrShowNotification = false } }); return f.Shot; });
        await f.Run(mode, TestContext.Current.CancellationToken);
        if (mode == 1) f.Requests.Last().AutoCopySource.ShouldBeTrue();
        else await f.Feedback.Received(1).TryNotifyCopiedAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Theory][InlineData(0)][InlineData(1)][InlineData(2)]
    public async Task NonCooperativeOcr_CancellationWaitsForExitAndSuppressesResult(int mode)
    {
        var f = new Fixture(); using var caller = new CancellationTokenSource();
        var gate = new TaskCompletionSource<OcrOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Ocr.RecognizeAsync(f.Shot.Image, null, Arg.Any<CancellationToken>()).Returns(gate.Task);
        var work = f.Run(mode, caller.Token);
        try
        {
            caller.Cancel(); work.IsCompleted.ShouldBeFalse(); f.Operation.DidNotReceive().Dispose();
            gate.SetResult(Outcome("late"));
            if (mode == 2) await work;
            else await Should.ThrowAsync<OperationCanceledException>(() => work);
            if (mode != 2) { f.Operation.Received(1).Dispose(); f.Requests.Count.ShouldBe(1); }
            await f.Clipboard.DidNotReceive().SetTextAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        }
        finally { gate.TrySetResult(Outcome("cleanup")); try { await work; } catch (OperationCanceledException) { } }
    }

    [Theory][InlineData(0)][InlineData(1)][InlineData(2)]
    public async Task CleanupFailure_AfterCancellationIsNotSwallowed(int mode)
    {
        var f = new Fixture(); using var caller = new CancellationTokenSource();
        f.Ocr.RecognizeAsync(f.Shot.Image, null, Arg.Any<CancellationToken>()).Returns<Task<OcrOutcome>>(_ =>
        { caller.Cancel(); throw new IrisException(ErrorCode.StorageFailure, "private cleanup"); });
        var ex = await Should.ThrowAsync<IrisException>(() => f.Run(mode, caller.Token));
        ex.Code.ShouldBe(ErrorCode.StorageFailure); ex.Message.ShouldBe(ErrorMessages.GetDefault(ex.Code));
    }

    [Theory][InlineData(0)][InlineData(1)]
    public async Task PresentationFailure_IsNotRepublished(int mode)
    {
        var f = new Fixture(); f.Presenter.TryShowAsync(f.Operation, Arg.Any<TranslateWindowRequest>()).Returns<Task<bool>>(_ => throw new InvalidOperationException());
        await Should.ThrowAsync<InvalidOperationException>(() => f.Run(mode, TestContext.Current.CancellationToken));
        await f.Presenter.Received(1).TryShowAsync(f.Operation, Arg.Any<TranslateWindowRequest>());
        await f.Presenter.DidNotReceive().TryNotifyAsync(f.Operation, Arg.Any<NotificationLevel>(), Arg.Any<ErrorCode>());
        f.Operation.Received(1).Dispose();
    }
}
