using Iris.App.Hosting;
using Iris.App.Services;
using Iris.App.Tests.TestSupport;
using Iris.Core.Actions;
using Iris.Core.Errors;
using Iris.Core.Imaging;
using Iris.Core.Ocr;
using Iris.Core.Presentation;
using Iris.Core.Storage;
using Iris.Core.Translation;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;

namespace Iris.App.Tests.Ocr;

[Collection("AppShutdownerSharedState")]
public sealed class OcrLifecycleTests
{
    [Theory][InlineData(false)][InlineData(true)]
    public Task Feedback_QueuedCallerCancellationOrStoppingRejectsAtActualUiPoint(bool stop) => WpfTestRunner.RunAsync(async () =>
    {
        await using var s = new OcrEntrypointScope(); using var caller = new CancellationTokenSource();
        s.Ui.Pause(); var work = s.Feedback.TryNotifyCopiedAsync(7, caller.Token); Task? stopping = null;
        try
        {
            await s.Ui.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if (stop) { stopping = s.Feedback.StopAsync(); stopping.IsCompleted.ShouldBeFalse(); }
            else caller.Cancel();
            s.Ui.Release(); (await work).ShouldBeFalse(); s.Native.Messages.ShouldBeEmpty();
            if (stopping != null) await stopping;
        }
        finally { s.Ui.Release(); await work; if (stopping != null) await stopping; }
    });

    [Fact]
    public Task Feedback_OriginalCallerCanceledBeforeForwarding_AndUnavailableTrayNeverCache() => WpfTestRunner.RunAsync(async () =>
    {
        await using var s = new OcrEntrypointScope(); using var caller = new CancellationTokenSource();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(caller.Token);
        Task<bool>? notification = null;
        using var later = caller.Token.Register(() =>
        { linked.IsCancellationRequested.ShouldBeFalse(); notification = s.Feedback.TryNotifyCopiedAsync(5, caller.Token); });
        caller.Cancel(); (await notification!).ShouldBeFalse();
        s.Native.IsCreated = false;
        (await s.Feedback.TryNotifyCopiedAsync(3, TestContext.Current.CancellationToken)).ShouldBeFalse();
        (await s.Feedback.TryNotifyErrorAsync(ErrorCode.Unknown, TestContext.Current.CancellationToken)).ShouldBeFalse();
        s.Native.IsCreated = true; s.Notifications.AttachTray(s.Tray); s.Native.Messages.ShouldBeEmpty();
        (await s.Feedback.TryNotifyCopiedAsync(3, TestContext.Current.CancellationToken)).ShouldBeTrue();
        (await s.Feedback.TryNotifyErrorAsync(ErrorCode.NetworkUnavailable, TestContext.Current.CancellationToken)).ShouldBeTrue();
        s.Native.Messages.Select(m => m.Text).ShouldBe(["已复制 3 个字符", ErrorMessages.GetDefault(ErrorCode.NetworkUnavailable)]);
        Should.Throw<ArgumentOutOfRangeException>(() => s.Feedback.TryNotifyCopiedAsync(-1, CancellationToken.None));
        Should.Throw<ArgumentOutOfRangeException>(() => s.Feedback.TryNotifyErrorAsync((ErrorCode)int.MaxValue, CancellationToken.None));
    });

    [Fact]
    public Task Feedback_StopObservesCallbackFailureAndCanRetryWithoutRunningItAgain() => WpfTestRunner.RunAsync(async () =>
    {
        await using var s = new OcrEntrypointScope(); int callbacks = 0;
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = s.Feedback.StoppingToken.Register(() =>
        { callbacks++; entered.TrySetResult(); release.Wait(); throw new InvalidOperationException("private callback"); });
        var stopping = s.Feedback.StopAsync();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            s.Feedback.StoppingToken.IsCancellationRequested.ShouldBeTrue(); stopping.IsCompleted.ShouldBeFalse();
            (await s.Feedback.TryNotifyCopiedAsync(9, TestContext.Current.CancellationToken)).ShouldBeFalse();
            release.Set(); var failure = await Should.ThrowAsync<IrisException>(() => stopping);
            failure.Message.ShouldBe(ErrorMessages.GetDefault(ErrorCode.Unknown));
            await s.Feedback.StopAsync(); callbacks.ShouldBe(1); s.Native.Messages.ShouldBeEmpty();
        }
        finally { release.Set(); try { await stopping; } catch (IrisException) { } }
    });

    [Theory][InlineData(IrisAction.ScreenshotTranslate)][InlineData(IrisAction.ScreenshotOcr)][InlineData(IrisAction.SilentScreenshotOcr)]
    public Task ScreenshotGroup_IsExclusiveThroughoutNonCooperativeOcr(IrisAction active) => WpfTestRunner.RunAsync(async () =>
    {
        await using var s = new OcrEntrypointScope(); using var caller = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<OcrOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        s.Ocr.RecognizeAsync(Arg.Any<ImageData>(), null, Arg.Any<CancellationToken>()).Returns(_ => { entered.TrySetResult(); return release.Task; });
        var work = s.Run(active, caller.Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); caller.Cancel();
            foreach (var action in new[] { IrisAction.ScreenshotTranslate, IrisAction.ScreenshotOcr, IrisAction.SilentScreenshotOcr })
                await s.Run(action, TestContext.Current.CancellationToken);
            s.Captures.Count.ShouldBe(1); work.IsCompleted.ShouldBeFalse();
            release.TrySetResult(OcrEntrypointScope.Outcome()); await work;
            s.Copies.ShouldBeEmpty(); s.Native.Messages.ShouldBeEmpty();
        }
        finally { release.TrySetResult(OcrEntrypointScope.Outcome()); await work; }
    });

    [Theory][InlineData(false, false)][InlineData(false, true)][InlineData(true, false)][InlineData(true, true)]
    public Task Bootstrapper_WaitsForActualHandlerWorkAndRetriesFailureBeforeHostStop(bool clipboard, bool failCallback) => WpfTestRunner.RunAsync(async () =>
    {
        await using var s = new OcrEntrypointScope();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (clipboard)
            s.Clipboard.SetTextAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(_ => { entered.TrySetResult(); return release.Task; });
        else
            s.Ocr.RecognizeAsync(Arg.Any<ImageData>(), null, Arg.Any<CancellationToken>()).Returns(async _ =>
            { entered.TrySetResult(); await release.Task; return OcrEntrypointScope.Outcome(); });
        var host = Substitute.For<IHost>(); int hostStops = 0, exits = 0;
        host.StopAsync(Arg.Any<CancellationToken>()).Returns(_ => { hostStops++; return Task.CompletedTask; });
        var previous = AppBootstrapper.AppShutdowner; AppBootstrapper.AppShutdowner = _ => exits++;
        var bootstrap = new AppBootstrapper(s.Settings, Substitute.For<ITranslationProviderRegistry>(), Substitute.For<IDatabaseInitializer>(),
            new LogLevelController(s.Settings, new Serilog.Core.LoggingLevelSwitch(), NullLogger<LogLevelController>.Instance), new ThemeService(s.Settings),
            s.Tray, s.Notifications, host, NullLogger<AppBootstrapper>.Instance, new(0), translateShutdownCoordinator: s.Presenter,
            actionDispatcher: s.Dispatcher, ocrService: s.Ocr, screenshotOcrFeedback: s.Feedback);
        using var registration = s.Feedback.StoppingToken.Register(() => { if (failCallback) throw new InvalidOperationException("private callback"); });
        var work = s.Run(clipboard ? IrisAction.SilentScreenshotOcr : IrisAction.ScreenshotOcr, TestContext.Current.CancellationToken);
        Task? stopping = null;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            stopping = bootstrap.StopAsync(); await Task.Delay(50, TestContext.Current.CancellationToken);
            stopping.IsCompleted.ShouldBeFalse(); work.IsCompleted.ShouldBeFalse(); hostStops.ShouldBe(0); exits.ShouldBe(0);
            await s.Run(IrisAction.ScreenshotTranslate, TestContext.Current.CancellationToken); s.Captures.Count.ShouldBe(1);
            release.TrySetResult(); await work;
            if (failCallback)
            {
                await Should.ThrowAsync<IrisException>(() => stopping); hostStops.ShouldBe(0);
                stopping = bootstrap.StopAsync();
            }
            await stopping.WaitAsync(TimeSpan.FromSeconds(5)); hostStops.ShouldBe(1); exits.ShouldBe(1);
            s.Native.Messages.ShouldBeEmpty();
        }
        finally
        {
            release.TrySetResult(); await work;
            if (stopping != null) try { await stopping; } catch (IrisException) when (failCallback) { }
            try { await bootstrap.StopAsync(); } finally { AppBootstrapper.AppShutdowner = previous; }
        }
    });

    [Fact]
    public Task SilentClipboard_KeepsScreenshotGroupUntilRealWriteExits() => WpfTestRunner.RunAsync(async () =>
    {
        await using var s = new OcrEntrypointScope();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        s.Clipboard.SetTextAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(release.Task);
        var work = s.Run(IrisAction.SilentScreenshotOcr, TestContext.Current.CancellationToken);
        try
        {
            await s.Run(IrisAction.ScreenshotOcr, TestContext.Current.CancellationToken);
            s.Captures.Count.ShouldBe(1); work.IsCompleted.ShouldBeFalse();
            release.SetResult(); await work;
        }
        finally { release.TrySetResult(); await work; }
    });
}
