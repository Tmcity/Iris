using System.Windows;
using System.Windows.Input;
using Iris.App.Capture;
using Iris.App.Tests.Capture;
using Iris.Core.Errors;
using Iris.Core.Imaging;
using Iris.Core.Presentation;
using NSubstitute;

internal static class LifecycleChecks
{
    internal static async Task Run(Func<string, Func<Task>, Task> run)
    {
        await run("DWM_NATIVE_HRESULT_IS_PRESERVED", () => ScreenshotTestScope.Run(async f =>
        {
            f.Native.ReviewFlushResult = unchecked((int)0x81230017);
            var task = f.Track(f.Service.CaptureRegionAsync(ScreenshotPurpose.Translate, default), typeof(IrisException));
            try
            {
                var failure = await Failure(task);
                bool nativeResult = failure.Detail?.Contains("81230017", StringComparison.OrdinalIgnoreCase) == true;
                Console.WriteLine($"DWM_FAILURE code={failure.Code} nativeHResultPreserved={nativeResult} captures={f.Capturer.Captures}");
                Require(failure.Code == ErrorCode.CaptureFailed && f.Capturer.Captures == 0 && nativeResult, "safe error lost the actual failed DwmFlush HRESULT");
            }
            finally { f.Native.ReviewFlushResult = 0; }
        }));

        foreach (bool warningFails in new[] { false, true })
            await run("COPY_FAILURE_PRESERVES_PNG_" + warningFails, () => ScreenshotTestScope.Run(async f =>
            {
                await f.Settings(new() { CopyImageToClipboard = true });
                f.Native.FailWarning = warningFails;
                f.Clipboard.SetImageAsync(Arg.Any<ImageData>(), Arg.Any<CancellationToken>()).Returns(Task.FromException(new IOException("synthetic copy failure")));
                var task = f.Start(); await f.Ready(); f.Select();
                var result = await task;
                Require(result != null && result.Image.Format == ImageFormat.Png, "optional copy/notification failure lost the selected image");
                await f.Service.RetryCleanupAsync();
                Require(f.Native.Warnings == 1, "copy warning repeated during cleanup");
                await f.Clipboard.Received(1).SetImageAsync(Arg.Any<ImageData>(), Arg.Any<CancellationToken>());
            }));

        await run("CALLER_CANCEL_WAITS_ACCEPTED_CROP", () => ScreenshotTestScope.Run(async f =>
        {
            using var caller = new CancellationTokenSource();
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            f.Releases.Add(() => release.TrySetResult());
            f.Capturer.BeforeCrop = async () => { entered.TrySetResult(); await release.Task; };
            var task = f.Track(f.Service.CaptureRegionAsync(ScreenshotPurpose.Translate, caller.Token), typeof(OperationCanceledException));
            try
            {
                await f.Ready(); f.Select(); await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
                caller.Cancel();
                Require(!task.IsCompleted, "caller cancellation abandoned the accepted crop operation");
                release.TrySetResult();
                bool canceled = false;
                try { await task; } catch (OperationCanceledException) { canceled = true; }
                Require(canceled && f.Native.Windows.All(w => w.IsClosed) && f.Native.Captured == null, "cancellation did not finish actual owned resources");
                Require(f.Native.Restores == 0 && f.Native.Warnings == 0, "canceled session restored focus or warned late");
            }
            finally { release.TrySetResult(); f.Capturer.BeforeCrop = null; }
        }));

        await run("CONFIRMED_CAPTURE_IGNORES_LATE_POINTER_FAILURE", () => ScreenshotTestScope.Run(async f =>
        {
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            f.Releases.Add(() => release.TrySetResult());
            f.Capturer.BeforeCrop = async () => { entered.TrySetResult(); await release.Task; };
            var task = f.Start();
            try
            {
                await f.Ready(); var window = f.Service.Windows.Single(); f.Select();
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
                f.Native.ReviewPhysicalPoint = (_, _) => throw new InvalidOperationException("synthetic late pointer read failure");
                window.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 2) { RoutedEvent = Mouse.PreviewMouseMoveEvent });
                release.TrySetResult();
                var result = await task;
                Console.WriteLine($"LATE_POINTER_AFTER_CONFIRM imagePreserved={result != null} crops={f.Capturer.Crops}");
                Require(result != null && result.Region == new PixelRect(10, 20, 4, 4), "input already sealed by confirmation still canceled the selected image");
            }
            finally { f.Native.ReviewPhysicalPoint = null; f.Capturer.BeforeCrop = null; release.TrySetResult(); }
        }));

        await run("ACCEPTED_CONFIRM_RELEASE_FAILURE_REMAINS_RETRYABLE", () => ScreenshotTestScope.Run(async f =>
        {
            await f.Settings(new() { ConfirmOnMouseUp = false });
            var task = f.Track(f.Service.CaptureRegionAsync(ScreenshotPurpose.Translate, default), typeof(IrisException));
            try
            {
                await f.Ready(); f.Select(); f.Input(ScreenshotInput.Down, 11, 21);
                f.Native.FailRelease = true;
                f.Input(ScreenshotInput.DoubleClick, 11, 21);
                var failure = await Failure(task).WaitAsync(TimeSpan.FromSeconds(10));
                Require(failure.Code == ErrorCode.CaptureFailed && f.Native.Captured != null && !f.A.Recording.CanStartRecording,
                    "an accepted confirmation's release failure was ignored after input was sealed");
                f.Native.FailRelease = false;
                await f.Service.RetryCleanupAsync();
                Require(f.Native.Captured == null && f.Native.Windows.All(w => w.IsClosed), "accepted input cleanup did not retry its retained capture");
            }
            finally { f.Native.FailRelease = false; }
        }));

        await run("FAILED_CLOSE_RETRIES_SAME_WINDOWS_WITHOUT_RECOPY", () => ScreenshotTestScope.Run(async f =>
        {
            await f.Settings(new() { CopyImageToClipboard = true });
            var task = f.Track(f.Service.CaptureRegionAsync(ScreenshotPurpose.Translate, default), typeof(IrisException));
            try
            {
                await f.Ready(); var original = f.Service.Windows.Single();
                f.Native.FailClose = true; f.Select();
                var failure = await Failure(task);
                Require(failure.Code == ErrorCode.CaptureFailed && original.IsVisible && !original.IsClosed, "failed close did not retain its actual window");
                Require(!f.A.Recording.CanStartRecording, "failed overlay released its preparation dependency early");
                f.Native.FailClose = false; await f.Service.RetryCleanupAsync();
                Require(original.IsClosed && f.A.Recording.CanStartRecording, "retry did not close original view and return preparation");
                await f.Clipboard.Received(1).SetImageAsync(Arg.Any<ImageData>(), Arg.Any<CancellationToken>());
                var readyAgain = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                f.Native.ReviewAfterActivate = () => readyAgain.TrySetResult();
                var next = f.Start(); await readyAgain.Task.WaitAsync(TimeSpan.FromSeconds(10)); f.Input(ScreenshotInput.Escape);
                Require(await next == null && f.Capturer.Captures == 2 && f.Capturer.Crops == 1, "recovered service did not accept the next independent session");
            }
            finally { f.Native.FailClose = false; f.Native.ReviewAfterActivate = null; }
        }));

        await run("CALLBACK_AND_CLOSE_ERRORS_RETAINED_AND_RETRYABLE", () => ScreenshotTestScope.Run(async f =>
        {
            using var caller = new CancellationTokenSource();
            CancellationTokenRegistration callback = default;
            f.Capturer.ReviewCaptureToken = token => callback = token.Register(() => throw new IOException("synthetic cancellation observer"));
            var task = f.Track(f.Service.CaptureRegionAsync(ScreenshotPurpose.Translate, caller.Token), typeof(IrisException));
            try
            {
                await f.Ready(); f.Native.FailClose = true; caller.Cancel();
                var failure = await Failure(task);
                bool closeRecorded = failure.Detail?.Contains("CLOSE", StringComparison.Ordinal) == true;
                bool cancelRecorded = failure.Detail?.Contains("CANCEL", StringComparison.OrdinalIgnoreCase) == true;
                f.Native.FailClose = false;
                bool retrySucceeded = true;
                try { await f.Service.RetryCleanupAsync(); } catch (Exception) { retrySucceeded = false; }
                Console.WriteLine($"CALLBACK_CLOSE closeRecorded={closeRecorded} cancelRecorded={cancelRecorded} retrySucceeded={retrySucceeded} allWindowsClosed={f.Native.Windows.All(w => w.IsClosed)}");
                Require(closeRecorded && cancelRecorded && retrySucceeded, "cancellation callback failure overwrote an error or permanently poisoned cleanup retry");
            }
            finally
            {
                f.Native.FailClose = false;
                f.Capturer.ReviewCaptureToken = null;
                await callback.DisposeAsync();
            }
        }));

        await run("PREPARATION_MULTI_FAILURES_REMAIN_VISIBLE", () => ScreenshotTestScope.Run(async f =>
        {
            await f.A.OpenSettings();
            using var operation = f.A.Mouse.Presenter.TryBeginOperation()!;
            await f.A.Mouse.Presenter.TryShowAsync(operation, new() { Text = "synthetic visible content", AutoTranslate = false, FocusInput = false });
            var restore = new RestoreBoundary(); f.A.Preparation.Visibility = restore;
            var task = f.Track(f.Service.CaptureRegionAsync(ScreenshotPurpose.Translate, default), typeof(IrisException));
            try
            {
                await f.Ready(); f.Select(); var failure = await Failure(task);
                bool settings = failure.Detail?.Contains("SETTINGS", StringComparison.Ordinal) == true;
                bool translation = failure.Detail?.Contains("TRANSLATION", StringComparison.Ordinal) == true;
                restore.Fail = false; await f.Service.RetryCleanupAsync();
                Console.WriteLine($"PREPARATION_ERRORS settingsRecorded={settings} translationRecorded={translation} settingsRestored={f.A.Settings.ActiveSession!.Window.IsVisible} translationRestored={f.A.Mouse.Presenter.Window!.IsVisible}");
                Require(settings && translation, "B collapsed A's independent safe cleanup failures into a single HRESULT");
            }
            finally { restore.Fail = false; }
        }));

        await run("NEW_INTENT_AFTER_PREPARATION_RETURN_BLOCKS_FINAL_UI", () => ScreenshotTestScope.Run(async f =>
        {
            await f.Settings(new() { CopyImageToClipboard = true });
            f.Clipboard.SetImageAsync(Arg.Any<ImageData>(), Arg.Any<CancellationToken>()).Returns(Task.FromException(new IOException("synthetic copy failure")));
            int held = 0;
            f.A.Ui.ReviewBeforeInvoke = () =>
            {
                if (f.Native.Windows.Count > 0 && f.Native.Windows.All(w => w.IsClosed) && f.A.Recording.CanStartRecording && Interlocked.Exchange(ref held, 1) == 0)
                    f.A.Ui.HoldNext = 1;
            };
            var task = f.Start();
            try
            {
                await f.Ready(); f.Select(); await f.A.Ui.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
                Require(f.Native.Windows.All(w => w.IsClosed) && f.A.Recording.CanStartRecording, "final UI queue was not reached after overlay/preparation release");
                using var newer = f.A.Mouse.Presenter.TryBeginOperation();
                f.A.Ui.Release.TrySetResult();
                Require(await task == null && f.Native.Restores == 0 && f.Native.Warnings == 0, "late final UI ignored a new display intent");
            }
            finally { f.A.Ui.ReviewBeforeInvoke = null; f.A.Ui.Release.TrySetResult(); }
        }));

        await run("NEW_INTENT_AT_FINAL_FOREGROUND_QUERY_BLOCKS_RESTORE", () => ScreenshotTestScope.Run(async f =>
        {
            int emptyQueries = 0;
            bool injected = false;
            var task = f.Start(); await f.Ready();
            f.Native.ReviewForeground = () =>
            {
                if (f.Native.Current.Handle == 0 && Interlocked.Increment(ref emptyQueries) == 2)
                { injected = true; using var newer = f.A.Mouse.Presenter.TryBeginOperation(); }
            };
            try
            {
                f.Select(); var result = await task;
                Console.WriteLine($"FOREGROUND_QUERY newIntentObserved={injected} emptyQueries={emptyQueries} resultCanceled={result == null} nativeRestores={f.Native.Restores}");
                Require(injected, "final native foreground query was not reached; adapt final helper call ordering before interpreting a product result");
                Require(result == null && f.Native.Restores == 0, "focus restore used a stale decision after the final native query admitted a new display intent");
            }
            finally { f.Native.ReviewForeground = null; }
        }));

        await run("UNEXPECTED_INACTIVE_OVERLAY_CLOSE_ENDS_SESSION", () => ScreenshotTestScope.Run(async f =>
        {
            using var caller = new CancellationTokenSource();
            f.Monitors = [new("primary", new(0, 0, 320, 240), new(0, 0, 320, 240), 1, true),
                new("secondary", new(320, 0, 320, 240), new(320, 0, 320, 240), 1, false)];
            var task = f.Track(f.Service.CaptureRegionAsync(ScreenshotPurpose.Translate, caller.Token), typeof(OperationCanceledException));
            try
            {
                await f.Ready(); var inactive = f.Service.Windows[1];
                Require(f.Native.Current.Handle == f.Service.Windows[0].Handle, "primary overlay must remain foreground while its peer closes");
                inactive.Close(); // Actual Closed notification; no invented foreground transition.
                Require(inactive.IsClosed, "owned peer did not actually close");
                var result = await task.WaitAsync(TimeSpan.FromSeconds(10));
                Require(result == null && f.Native.Windows.All(w => w.IsClosed), "a disappeared overlay left an incomplete screenshot interaction");
            }
            finally { caller.Cancel(); } // The outer owned fixture awaits the original task even after a timeout.
        }));
    }
    private static async Task<IrisException> Failure(Task task)
    {
        try { await task; } catch (IrisException ex) { return ex; }
        throw new InvalidOperationException("Expected a safe screenshot failure.");
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private sealed class RestoreBoundary : IPreparationVisibility
    {
        internal bool Fail = true;
        public void Hide(Window window) => window.Hide();
        public void Show(Window window) { if (Fail) throw new IOException("synthetic restoration failure"); window.Show(); }
    }
}
