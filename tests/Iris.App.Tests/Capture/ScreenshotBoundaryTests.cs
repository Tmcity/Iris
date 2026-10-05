using System.Windows;
using System.Windows.Input;
using Iris.App.Capture;
using Iris.Core.Errors;
using Iris.Core.Imaging;
using Iris.Core.Presentation;
using NSubstitute;
using Shouldly;

namespace Iris.App.Tests.Capture;

[Collection("ScreenshotRendering")]
public sealed class ScreenshotBoundaryTests
{
    [Fact]
    public Task SettingsAreCapturedOnceAndBusyDoesNotCancelOriginal() => ScreenshotTestScope.Run(async f =>
    {
        var gate = f.Codec.Gate(); f.Capturer.BeforeCapture = gate.WaitAsync;
        await f.Settings(new() { ConfirmOnMouseUp = true, CopyImageToClipboard = false });
        var task = f.Start(); await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Should.Throw<IrisException>(() => f.Service.CaptureRegionAsync(ScreenshotPurpose.Ocr, default));
        await f.Settings(new() { ConfirmOnMouseUp = false, CopyImageToClipboard = true });
        gate.Open(); await f.Ready(); f.Select(); (await task).ShouldNotBeNull();
        f.Capturer.Captures.ShouldBe(1); await f.Clipboard.DidNotReceive().SetImageAsync(Arg.Any<ImageData>(), Arg.Any<CancellationToken>());
    });

    [Theory]
    [InlineData(double.NaN)][InlineData(-.1)][InlineData(1.0)]
    public Task InvalidOpacityIsSafeAndLazy(double mask) => ScreenshotTestScope.Run(async f =>
    {
        await f.Settings(new() { MaskOpacity = mask });
        var task = f.Track(f.Service.CaptureRegionAsync(ScreenshotPurpose.Translate, default), typeof(IrisException));
        (await Should.ThrowAsync<IrisException>(() => task)).Code.ShouldBe(ErrorCode.InvalidArgument);
        f.Capturer.Captures.ShouldBe(0); f.Native.Windows.ShouldBeEmpty();
    });

    [Theory]
    [InlineData(false)][InlineData(true)]
    public Task CreationAndSourceInitializationFailureHaveOwnedCleanup(bool source) => ScreenshotTestScope.Run(async f =>
    {
        ScreenshotOverlayWindow? created = null;
        if (source) f.Factory.Created = window => { created = window; window.SourceInitialized += (_, _) => throw new IOException("synthetic source failure"); };
        else f.Factory.FailCreate = true;
        var task = f.Track(f.Service.CaptureRegionAsync(ScreenshotPurpose.Translate, default), typeof(IrisException));
        await Should.ThrowAsync<IrisException>(() => task);
        if (source) { created.ShouldNotBeNull(); created.IsClosed.ShouldBeTrue(); created.Handle.ShouldBe(0); }
        f.A.Recording.CanStartRecording.ShouldBeTrue(); f.Service.HasPendingOverlays.ShouldBeFalse();
    });

    [Fact]
    public Task RealWpfKeyRouteAndDoubleClickSealInputOnce() => ScreenshotTestScope.Run(async f =>
    {
        await f.Settings(new() { ConfirmOnMouseUp = false });
        var task = f.Start(); await f.Ready(); f.Select(); var window = f.Service.Windows.Single();
        window.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window)!, 0, Key.Enter)
        { RoutedEvent = Keyboard.PreviewKeyDownEvent });
        window.RaiseEvent(new ScreenshotInputEventArgs(ScreenshotInput.DoubleClick, new(11, 21)));
        (await task).ShouldNotBeNull(); f.Capturer.Crops.ShouldBe(1);
    });

    [Fact]
    public Task ManualDoubleClickInsideSelectionConfirmsDespiteFirstDownMovement() => ScreenshotTestScope.Run(async f =>
    {
        await f.Settings(new() { ConfirmOnMouseUp = false });
        var task = f.Start(); await f.Ready(); f.Select();
        f.Input(ScreenshotInput.Down, 11, 21); f.Input(ScreenshotInput.DoubleClick, 11, 21);
        (await task)!.Region.ShouldBe(new PixelRect(10, 20, 4, 4)); f.Capturer.Crops.ShouldBe(1);
    });

    [Fact]
    public Task NewIntentInPendingUiQueueCannotCreateLateOverlay() => ScreenshotTestScope.Run(async f =>
    {
        f.A.Ui.HoldNext = 1;
        var task = f.Start(); await f.A.Ui.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        using var next = f.A.Mouse.Presenter.TryBeginOperation();
        f.A.Ui.Release.TrySetResult(); (await task).ShouldBeNull(); f.Native.Windows.ShouldBeEmpty();
    });

    [Fact]
    public Task NewIntentAtWarningAcceptanceSuppressesWarningAndResult() => ScreenshotTestScope.Run(async f =>
    {
        await f.Settings(new() { CopyImageToClipboard = true });
        f.Clipboard.SetImageAsync(Arg.Any<ImageData>(), Arg.Any<CancellationToken>()).Returns(Task.FromException(new IOException("private copy")));
        f.Native.BeforeWarning = () => { using var next = f.A.Mouse.Presenter.TryBeginOperation(); };
        var task = f.Start(); await f.Ready(); f.Select();
        (await task).ShouldBeNull(); f.Native.Warnings.ShouldBe(0);
    });

    [Fact]
    public Task FailedNotificationStillReturnsConfirmedImage() => ScreenshotTestScope.Run(async f =>
    {
        await f.Settings(new() { CopyImageToClipboard = true }); f.Native.FailWarning = true;
        f.Clipboard.SetImageAsync(Arg.Any<ImageData>(), Arg.Any<CancellationToken>()).Returns(Task.FromException(new IOException("private copy")));
        var task = f.Start(); await f.Ready(); f.Select(); (await task).ShouldNotBeNull(); f.Native.Warnings.ShouldBe(1);
    });

    [Fact]
    public Task ForegroundChangesAtNativeAcceptanceAreNotOverwritten() => ScreenshotTestScope.Run(async f =>
    {
        f.Native.BeforeRestore = () => f.Native.Current = new(777, 888);
        var task = f.Start(); await f.Ready(); f.Select(); (await task).ShouldNotBeNull();
        f.Native.Restores.ShouldBe(0); f.Native.Current.Handle.ShouldBe((nint)777);
    });

    [Fact]
    public Task MultipleWindowFailuresAndNextRequestKeepOriginalDependencies() => ScreenshotTestScope.Run(async f =>
    {
        f.Monitors = [new("one", new(0, 0, 100, 100), new(0, 0, 100, 100), 1, true), new("two", new(100, 0, 100, 100), new(100, 0, 100, 100), 1, false)];
        var task = f.Track(f.Service.CaptureRegionAsync(ScreenshotPurpose.Translate, default), typeof(IrisException));
        await f.Ready(); var windows = f.Service.Windows.ToArray(); f.Native.FailClose = true; f.Select();
        var error = await Should.ThrowAsync<IrisException>(() => task); error.Detail!.Split("CLOSE=").Length.ShouldBe(3);
        var next = f.Track(f.Service.CaptureRegionAsync(ScreenshotPurpose.Translate, default), typeof(IrisException));
        await Should.ThrowAsync<IrisException>(() => next); f.Capturer.Captures.ShouldBe(1);
        f.Service.Windows.ShouldBe(windows); windows.ShouldAllBe(w => w.FrozenPixels != null && !w.IsClosed);
        f.Native.FailClose = false; await f.Service.RetryCleanupAsync(); windows.ShouldAllBe(w => w.IsClosed && w.FrozenPixels == null);
    });

    [Fact]
    public Task PreparationRestoreFailureRetainsLeaseUntilRetry() => ScreenshotTestScope.Run(async f =>
    {
        await f.A.OpenSettings(); var window = f.A.Settings.ActiveSession!.Window;
        var visibility = new ScreenshotPreparationTests.Visibility { FailShow = true }; f.A.Preparation.Visibility = visibility;
        var task = f.Track(f.Service.CaptureRegionAsync(ScreenshotPurpose.Translate, default), typeof(IrisException));
        await f.Ready(); f.Select(); await Should.ThrowAsync<IrisException>(() => task);
        f.Service.Windows.ShouldBeEmpty(); window.IsVisible.ShouldBeFalse();
        visibility.FailShow = false; await f.Service.RetryCleanupAsync(); window.IsVisible.ShouldBeTrue(); f.Capturer.Captures.ShouldBe(1);
    });

    [Fact]
    public Task ActualCodecFailureClosesOverlayAndDoesNotCopy() => ScreenshotTestScope.Run(async f =>
    {
        await f.Settings(new() { CopyImageToClipboard = true });
        f.Codec.Encoding.Configure = io => io.Failures["Flush"] = 1;
        var task = f.Track(f.Service.CaptureRegionAsync(ScreenshotPurpose.Translate, default), typeof(IrisException));
        await f.Ready(); f.Select();
        var error = await Should.ThrowAsync<IrisException>(() => task); error.ToString().ShouldNotContain("private codec");
        f.Service.Windows.ShouldBeEmpty(); f.A.Recording.CanStartRecording.ShouldBeTrue();
        await f.Clipboard.DidNotReceive().SetImageAsync(Arg.Any<ImageData>(), Arg.Any<CancellationToken>());
    }, gdi: true, realEncoder: false);

    [Theory]
    [InlineData(int.MinValue)][InlineData(int.MaxValue - 3)]
    public Task ExtremePhysicalCoordinatesRemainHalfOpen(int origin) => ScreenshotTestScope.Run(async f =>
    {
        f.Monitors = [new("edge", new(origin, origin, 4, 4), new(origin, origin, 4, 4), 1, true)];
        var task = f.Start(); await f.Ready(); f.Select(origin, origin, origin + 3, origin + 3);
        (await task)!.Region.ShouldBe(new PixelRect(origin, origin, 4, 4));
    });

    [Theory]
    [InlineData(false)][InlineData(true)]
    public Task OverlayHandleWithDifferentPidIsExternal(bool confirmed) => ScreenshotTestScope.Run(async f =>
    {
        var gate = f.Codec.Gate(); f.Capturer.BeforeCrop = gate.WaitAsync;
        var task = f.Start(); await f.Ready(); nint handle = f.Service.Windows.Single().Handle;
        if (confirmed) { f.Select(); await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10)); }
        f.Native.Current = new(handle, uint.MaxValue);
        if (!confirmed) f.Input(ScreenshotInput.Deactivated);
        gate.Open(); var result = await task;
        if (confirmed) result.ShouldNotBeNull(); else result.ShouldBeNull();
        f.Native.Restores.ShouldBe(0); f.Native.Current.ShouldBe(new ScreenshotFocus(handle, uint.MaxValue));
    });
}
