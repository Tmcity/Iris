using System.Runtime.CompilerServices;
using Iris.App.Capture;
using Iris.Core.Errors;
using Iris.Core.Imaging;
using Iris.Core.Presentation;
using NSubstitute;
using Shouldly;

namespace Iris.App.Tests.Capture;

[Collection("ScreenshotRendering")]
public sealed class ScreenshotLifecycleTests
{
    [Theory]
    [InlineData("render")][InlineData("capture")][InlineData("copy")]
    public Task CallerCancellation_WaitsActualBoundary(string stage) => ScreenshotTestScope.Run(async f =>
    {
        using var caller = new CancellationTokenSource();
        var gate = f.Codec.Gate();
        await f.Settings(new() { CopyImageToClipboard = true });
        if (stage == "render") f.Native.BeforeFlush = gate.Block;
        if (stage == "capture") f.Capturer.BeforeCapture = gate.WaitAsync;
        if (stage == "copy") f.Clipboard.SetImageAsync(Arg.Any<ImageData>(), Arg.Any<CancellationToken>()).Returns(_ => gate.WaitAsync());
        var task = f.Track(f.Service.CaptureRegionAsync(ScreenshotPurpose.Translate, caller.Token), typeof(OperationCanceledException));
        if (stage == "copy") { await f.Ready(); f.Select(); }
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10)); caller.Cancel();
        task.IsCompleted.ShouldBeFalse();
        Should.Throw<IrisException>(() => f.Service.CaptureRegionAsync(ScreenshotPurpose.Translate, default));
        gate.Open(); await Should.ThrowAsync<OperationCanceledException>(() => task);
        f.Native.Windows.ShouldAllBe(w => w.IsClosed); f.A.Recording.CanStartRecording.ShouldBeTrue();
    });

    [Fact]
    public Task ActualCodecCancellation_DoesNotDisposeInFlightWork() => ScreenshotTestScope.Run(async f =>
    {
        using var caller = new CancellationTokenSource(); var gate = f.Codec.Gate();
        f.Codec.Encoding.Configure = io => io.Before = stage => stage == "Flush" ? gate.WaitAsync() : Task.CompletedTask;
        var task = f.Track(f.Service.CaptureRegionAsync(ScreenshotPurpose.Translate, caller.Token), typeof(OperationCanceledException));
        await f.Ready(); f.Select(); await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        caller.Cancel(); task.IsCompleted.ShouldBeFalse();
        f.Codec.Encoding.Sessions.Single().InFlight.ShouldBe(1);
        gate.Open(); await Should.ThrowAsync<OperationCanceledException>(() => task);
        f.Codec.Encoding.Sessions.Single().InFlight.ShouldBe(0); f.Codec.Encoding.Sessions.Single().StreamOwned.ShouldBeFalse();
    }, gdi: true, realEncoder: false);

    [Fact]
    public Task CloseFailure_RetainsSameWindowAndPreparation_NoCopyReplay() => ScreenshotTestScope.Run(async f =>
    {
        await f.A.OpenSettings(); var original = f.A.Settings.ActiveSession!;
        await f.Settings(new() { CopyImageToClipboard = true });
        var task = f.Track(f.Service.CaptureRegionAsync(ScreenshotPurpose.Translate, default), typeof(IrisException));
        await f.Ready(); var window = f.Service.Windows.Single(); f.Native.FailClose = true; f.Select();
        await Should.ThrowAsync<IrisException>(() => task);
        f.Service.Windows.Single().ShouldBeSameAs(window); window.IsClosed.ShouldBeFalse();
        original.Window.IsVisible.ShouldBeFalse(); f.A.Recording.CanStartRecording.ShouldBeFalse();
        f.Native.FailClose = false; await f.Service.RetryCleanupAsync();
        window.IsClosed.ShouldBeTrue(); original.Window.IsVisible.ShouldBeTrue(); f.A.Recording.CanStartRecording.ShouldBeTrue();
        await f.Clipboard.Received(1).SetImageAsync(Arg.Any<ImageData>(), Arg.Any<CancellationToken>());
    });

    [Theory]
    [InlineData("show")][InlineData("placement")][InlineData("capture")]
    public Task FailureAfterOwnedRegistration_ClosesActualWindow(string stage) => ScreenshotTestScope.Run(async f =>
    {
        if (stage == "show") f.Native.FailShow = true;
        if (stage == "placement") f.A.Mouse.Position.Result = show => !show;
        if (stage == "capture") f.Native.FailCapture = true;
        var task = f.Track(f.Service.CaptureRegionAsync(ScreenshotPurpose.Translate, default), typeof(IrisException));
        if (stage == "capture") { await f.Ready(); f.Input(ScreenshotInput.Down); }
        (await Should.ThrowAsync<IrisException>(() => task)).Code.ShouldBe(ErrorCode.CaptureFailed);
        f.Native.Windows.ShouldAllBe(w => w.IsClosed && w.Handle == 0); f.Service.Windows.ShouldBeEmpty();
    });

    [Fact]
    public Task NewDisplayDuringBlockedCapture_CancelsBeforeAnyOverlay() => ScreenshotTestScope.Run(async f =>
    {
        var gate = f.Codec.Gate(); f.Capturer.BeforeCapture = gate.WaitAsync;
        var task = f.Start(); await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        using var operation = f.A.Mouse.Presenter.TryBeginOperation();
        task.IsCompleted.ShouldBeFalse(); gate.Open(); (await task).ShouldBeNull(); f.Native.Windows.ShouldBeEmpty();
    });

    [Fact]
    public Task NewIntentAfterPreparationReturn_IsNotLostBeforeResult() => ScreenshotTestScope.Run(async f =>
    {
        await f.Settings(new() { CopyImageToClipboard = true });
        f.Clipboard.SetImageAsync(Arg.Any<ImageData>(), Arg.Any<CancellationToken>()).Returns(Task.FromException(new IOException("private clipboard")));
        bool released = false;
        f.Native.BeforeRestore = () =>
        {
            released = f.A.Recording.CanStartRecording;
            using var next = f.A.Mouse.Presenter.TryBeginOperation();
        };
        var task = f.Start(); await f.Ready(); f.Select();
        (await task).ShouldBeNull(); released.ShouldBeTrue(); f.Native.Warnings.ShouldBe(0); f.Native.Restores.ShouldBe(0);
    });

    [Fact]
    public Task ExternalForegroundAfterConfirmation_DoesNotCancelOrStealFocus() => ScreenshotTestScope.Run(async f =>
    {
        var gate = f.Codec.Gate(); f.Capturer.BeforeCrop = gate.WaitAsync;
        var task = f.Start(); await f.Ready(); var view = f.Service.Windows.Single(); f.Select();
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        f.Native.Current = new(555, 666);
        view.RaiseEvent(new ScreenshotInputEventArgs(ScreenshotInput.Deactivated, default));
        gate.Open(); (await task).ShouldNotBeNull(); f.Native.Restores.ShouldBe(0); f.Native.Current.Handle.ShouldBe((nint)555);
    });

    [Fact]
    public Task StopWaitsCopyAndKeepsOriginalWindowsHidden() => ScreenshotTestScope.Run(async f =>
    {
        await f.A.OpenSettings(); var settings = f.A.Settings.ActiveSession!;
        var gate = f.Codec.Gate(); await f.Settings(new() { CopyImageToClipboard = true });
        f.Clipboard.SetImageAsync(Arg.Any<ImageData>(), Arg.Any<CancellationToken>()).Returns(_ => gate.WaitAsync());
        var task = f.Start(); await f.Ready(); f.Select(); await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var stop = f.Track(f.Service.StopAsync()); stop.IsCompleted.ShouldBeFalse(); task.IsCompleted.ShouldBeFalse();
        gate.Open(); await stop; (await task).ShouldBeNull(); settings.Window.IsVisible.ShouldBeFalse();
        f.Native.Windows.ShouldAllBe(w => w.IsClosed); f.A.Recording.CanStartRecording.ShouldBeFalse();
    });

    [Fact]
    public Task CompletedRequest_DoesNotKeepPixelsImageOrClosedWindow() => ScreenshotTestScope.Run(async f =>
    {
        var weak = await CaptureWeakAsync(f);
        var control = await ControlAsync();
        for (int i = 0; i < 4; i++)
        {
            await System.Windows.Threading.Dispatcher.CurrentDispatcher.InvokeAsync(static () => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
            await Task.Run(static () => { }); Collect();
            if (weak.All(w => !w.IsAlive) && !control.IsAlive) break;
        }
        control.IsAlive.ShouldBeFalse();
        string states = string.Join(", ", weak.Select((w, i) => $"{i}={w.IsAlive}"));
        weak.ShouldAllBe(w => !w.IsAlive, $"0=pixels 1=image 2=window 3=bitmap: {states}");
        GC.KeepAlive(f.Service);
    });
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<WeakReference[]> CaptureWeakAsync(ScreenshotTestScope f)
    {
        var task = f.Service.CaptureRegionAsync(ScreenshotPurpose.Translate, default);
        await f.Ready(); var view = f.Service.Windows.Single();
        var bitmap = view.FrozenPixels!;
        f.Select(); var result = await task;
        var weak = new[] { f.Capturer.PixelReferences.Single(), new WeakReference(result!.Image.Bytes), new WeakReference(view), new WeakReference(bitmap) };
        f.Native.Windows.Clear(); return weak;
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<WeakReference> ControlAsync() => new(await Task.FromResult(new byte[64]));
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Collect()
    {
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, true, true); GC.WaitForPendingFinalizers();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, true, true);
    }
}
