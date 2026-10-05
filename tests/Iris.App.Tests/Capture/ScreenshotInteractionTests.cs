using System.Windows.Media.Imaging;
using Iris.App.Capture;
using Iris.Core.Errors;
using Iris.Core.Imaging;
using Iris.Core.Presentation;
using Iris.Core.Settings;
using NSubstitute;
using Shouldly;

namespace Iris.App.Tests.Capture;

[Collection("ScreenshotRendering")]
public sealed class ScreenshotInteractionTests
{
    [Theory]
    [InlineData(ScreenshotPurpose.Translate)][InlineData(ScreenshotPurpose.Ocr)][InlineData(ScreenshotPurpose.SilentOcr)]
    public Task ActualOverlay_EndpointInclusiveRegionAndPng(ScreenshotPurpose purpose) => ScreenshotTestScope.Run(async f =>
    {
        await f.Settings(new() { CopyImageToClipboard = true });
        var task = f.Start(purpose); await f.Ready();
        f.Service.Windows.Single().IsVisible.ShouldBeTrue();
        f.Select(); var result = await task;
        result!.Region.ShouldBe(new PixelRect(10, 20, 4, 4));
        result.Image.PixelWidth.ShouldBe(4); result.Image.PixelHeight.ShouldBe(4);
        using var stream = new MemoryStream(result.Image.Bytes);
        var decoder = new PngBitmapDecoder(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        decoder.Frames[0].PixelWidth.ShouldBe(4); decoder.Frames[0].PixelHeight.ShouldBe(4);
        await f.Clipboard.Received(1).SetImageAsync(Arg.Any<ImageData>(), Arg.Any<CancellationToken>());
        f.Capturer.Captures.ShouldBe(1); f.Capturer.Crops.ShouldBe(1); f.Native.Restores.ShouldBe(1);
        f.Native.Windows.ShouldAllBe(w => w.IsClosed && w.Handle == 0);
    });

    [Fact]
    public Task ActualGdiAndRealCodec_UsesSameFrameAndSingleCopy() => ScreenshotTestScope.Run(async f =>
    {
        await f.Settings(new() { CopyImageToClipboard = true });
        var task = f.Start(); await f.Ready(); f.Select(-40, -20, -37, -17);
        var result = await task;
        result!.Region.ShouldBe(new PixelRect(-40, -20, 4, 4));
        f.Codec.Native.Count("BitBlt").ShouldBe(1);
        await f.Clipboard.Received(1).SetImageAsync(Arg.Any<ImageData>(), Arg.Any<CancellationToken>());
        f.Codec.Native.Handles.ShouldBe(0);
    }, gdi: true);

    [Theory]
    [InlineData(13, 23, 10, 20, 10, 20, 4, 4)]
    [InlineData(-40, -20, 1000, 1000, -40, -20, 320, 240)]
    [InlineData(279, 219, -1000, -1000, -40, -20, 320, 240)]
    public Task PhysicalReverseAndClampedEdges(int x0, int y0, int x1, int y1, int x, int y, int width, int height) => ScreenshotTestScope.Run(async f =>
    {
        var task = f.Start(); await f.Ready(); f.Select(x0, y0, x1, y1);
        var result = await task; result!.Region.ShouldBe(new PixelRect(x, y, width, height));
        result.Image.PixelWidth.ShouldBe(width); result.Image.PixelHeight.ShouldBe(height);
    });

    [Theory]
    [InlineData(1.0)][InlineData(1.25)][InlineData(1.5)][InlineData(2.0)]
    public Task PerMonitorDpiAndHoles_KeepPhysicalDimensions(double dpi) => ScreenshotTestScope.Run(async f =>
    {
        f.Monitors = [new("left", new(-100, -50, 80, 60), new(-100, -50, 80, 60), dpi, true),
            new("right", new(30, 20, 100, 80), new(30, 20, 100, 80), 2, false),
            new("mirror", new(-100, -50, 80, 60), new(-100, -50, 80, 60), dpi, false)];
        var task = f.Start(); await f.Ready(); f.Service.Windows.Count.ShouldBe(3);
        f.Service.Windows[0].Monitor.DpiScale.ShouldBe(dpi);
        f.A.Mouse.Position.Calls.ShouldContain(call => call.Show && call.Bounds == f.Monitors[0].Bounds);
        f.Select(30, 20, 129, 99, screen: 1); var result = await task;
        result!.Region.ShouldBe(new PixelRect(30, 20, 100, 80)); f.Capturer.Captures.ShouldBe(1);
    });

    [Fact]
    public Task SmallRegionManualMovementClearAndEnter() => ScreenshotTestScope.Run(async f =>
    {
        await f.Settings(new() { ConfirmOnMouseUp = false });
        var task = f.Start(); await f.Ready();
        f.Select(10, 20, 13, 22); f.Input(ScreenshotInput.Enter); task.IsCompleted.ShouldBeFalse();
        f.Select(10, 20, 19, 29); task.IsCompleted.ShouldBeFalse();
        f.Input(ScreenshotInput.Down, 12, 22); f.Input(ScreenshotInput.Move, 999, 999); f.Input(ScreenshotInput.Up, 999, 999);
        f.Input(ScreenshotInput.Enter); f.Input(ScreenshotInput.Enter);
        var result = await task; result!.Region.ShouldBe(new PixelRect(270, 210, 10, 10)); f.Capturer.Crops.ShouldBe(1);
    });

    [Fact]
    public Task RightClearsThenCancelsAndDoubleClickConfirmsOnce() => ScreenshotTestScope.Run(async f =>
    {
        await f.Settings(new() { ConfirmOnMouseUp = false });
        var task = f.Start(); await f.Ready(); f.Select(); f.Input(ScreenshotInput.Right);
        task.IsCompleted.ShouldBeFalse(); f.Input(ScreenshotInput.Right); (await task).ShouldBeNull();
        f.Capturer.Crops.ShouldBe(0);
    });

    [Theory]
    [InlineData((int)ScreenshotInput.Escape)][InlineData((int)ScreenshotInput.LostCapture)][InlineData((int)ScreenshotInput.Topology)]
    public Task InteractionCancellation_ReturnsNull(int input) => ScreenshotTestScope.Run(async f =>
    {
        var task = f.Start(); await f.Ready(); f.Input((ScreenshotInput)input); (await task).ShouldBeNull(); f.Capturer.Crops.ShouldBe(0);
    });

    [Fact]
    public Task TopologyCheckAtConfirmRejectsFrozenMismatch() => ScreenshotTestScope.Run(async f =>
    {
        var task = f.Start(); await f.Ready();
        f.Monitors = [f.Monitors[0] with { DpiScale = 2 }]; f.Select();
        (await task).ShouldBeNull(); f.Capturer.Crops.ShouldBe(0);
    });

    [Fact]
    public Task ForeignForegroundCancelsButOwnedForegroundDoesNot() => ScreenshotTestScope.Run(async f =>
    {
        var task = f.Start(); await f.Ready(); f.Input(ScreenshotInput.Deactivated); task.IsCompleted.ShouldBeFalse();
        f.Native.Current = new(999, 123); f.Input(ScreenshotInput.Deactivated);
        (await task).ShouldBeNull(); f.Native.Restores.ShouldBe(0);
    });

    [Fact]
    public Task CopyFailureWarnsOnceAndPreservesPng() => ScreenshotTestScope.Run(async f =>
    {
        await f.Settings(new() { CopyImageToClipboard = true });
        f.Clipboard.SetImageAsync(Arg.Any<ImageData>(), Arg.Any<CancellationToken>()).Returns(Task.FromException(new IOException("PRIVATE_COPY")));
        var task = f.Start(); await f.Ready(); f.Select(); (await task).ShouldNotBeNull();
        f.Native.Warnings.ShouldBe(1); await f.Service.RetryCleanupAsync(); f.Native.Warnings.ShouldBe(1);
        await f.Clipboard.Received(1).SetImageAsync(Arg.Any<ImageData>(), Arg.Any<CancellationToken>());
    });

    [Fact]
    public Task InvalidAndUnused_AreLazy() => ScreenshotTestScope.Run(async f =>
    {
        Should.Throw<IrisException>(() => f.Service.CaptureRegionAsync((ScreenshotPurpose)999, default)).Code.ShouldBe(ErrorCode.InvalidArgument);
        Should.Throw<OperationCanceledException>(() => f.Service.CaptureRegionAsync(ScreenshotPurpose.Translate, new CancellationToken(true)));
        await f.Service.StopAsync(); f.Native.Flushes.ShouldBe(0); f.Native.Windows.ShouldBeEmpty();
        f.Capturer.Captures.ShouldBe(0);
    });
}
