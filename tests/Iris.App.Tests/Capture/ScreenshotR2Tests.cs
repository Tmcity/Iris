using Iris.Core.Errors;
using Iris.Core.Platform;
using Iris.Core.Presentation;
using Iris.App.Capture;
using Shouldly;

namespace Iris.App.Tests.Capture;

[Collection("ScreenshotRendering")]
public sealed class ScreenshotR2Tests
{
    [Theory]
    [InlineData(3, false, false)]
    [InlineData(3, true, false)]
    [InlineData(3, true, true)]
    [InlineData(4, true, false)]
    public Task RegisteredPeerClosesDuringShow_EndsSetupAndRetainsFailedCleanup(int screens, bool closePeer, bool failClose) =>
        ScreenshotTestScope.Run(async f =>
        {
            f.Monitors = Enumerable.Range(0, screens).Select(i => new MonitorInfo($"synthetic-{i}",
                new(i * 320, 0, 320, 240), new(i * 320, 0, 320, 240), 1, i == 0)).ToArray();
            using var caller = new CancellationTokenSource();
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            ScreenshotOverlayWindow? peer = null;
            f.Native.BeforeShow = () =>
            {
                if (f.Native.Windows.Count != 3) return;
                peer = f.Native.Windows[1];
                if (closePeer) peer.Close(); // Actual Closed event while the third external Show is in progress.
                entered.TrySetResult();
            };
            var task = f.Track(f.Service.CaptureRegionAsync(ScreenshotPurpose.Translate, caller.Token),
                failClose ? typeof(IrisException) : typeof(OperationCanceledException));
            try
            {
                f.Native.FailClose = failClose;
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
                peer.ShouldNotBeNull();
                if (!closePeer)
                {
                    await f.Ready();
                    f.Native.Windows.Count.ShouldBe(screens);
                    f.Native.Windows.ShouldAllBe(w => !w.IsClosed && w.Handle != 0);
                    f.Select();
                    (await task.WaitAsync(TimeSpan.FromSeconds(10))).ShouldNotBeNull();
                }
                else
                {
                    peer.IsClosed.ShouldBeTrue(); peer.Handle.ShouldBe(0);
                    if (failClose)
                    {
                        (await Should.ThrowAsync<IrisException>(() => task.WaitAsync(TimeSpan.FromSeconds(10)))).Code.ShouldBe(ErrorCode.CaptureFailed);
                        var retained = f.Service.Windows.ToArray();
                        retained.Length.ShouldBe(2);
                        f.A.Recording.CanStartRecording.ShouldBeFalse();
                        f.Native.FailClose = false;
                        await f.Service.RetryCleanupAsync();
                        retained.ShouldAllBe(w => w.IsClosed);
                    }
                    else (await task.WaitAsync(TimeSpan.FromSeconds(10))).ShouldBeNull();
                    f.Native.Activated.Task.IsCompleted.ShouldBeFalse();
                    f.Native.Windows.Count.ShouldBe(3); // No fourth Show after invalidation.
                    f.Capturer.Crops.ShouldBe(0);
                }
                caller.IsCancellationRequested.ShouldBeFalse();
                f.Native.Windows.ShouldAllBe(w => w.IsClosed);
                f.Native.Captured.ShouldBeNull();
                f.Service.Windows.ShouldBeEmpty();
                f.A.Recording.CanStartRecording.ShouldBeTrue();
            }
            finally
            {
                f.Native.BeforeShow = null; f.Native.FailClose = false;
                // Only failure-path cleanup may cancel the caller; the outer scope joins the actual task
                // and preserves both the primary assertion and cleanup errors.
                if (!task.IsCompleted) caller.Cancel();
            }
        });
}
