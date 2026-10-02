using System;
using Iris.App.Translation;
using Iris.Core.Imaging;
using Iris.Core.Platform;
using Iris.Core.Settings;
using Shouldly;
using Xunit;

namespace Iris.App.Tests.Translation;

public sealed class TranslateWindowPlacementTests
{
    private static readonly MonitorInfo PrimaryMonitor = new(
        @"\\.\DISPLAY1",
        new PixelRect(0, 0, 1920, 1080),
        new PixelRect(0, 0, 1920, 1040),
        1.0,
        true);

    private static readonly MonitorInfo LeftNegativeMonitor = new(
        @"\\.\DISPLAY2",
        new PixelRect(-1920, 0, 1920, 1080),
        new PixelRect(-1920, 0, 1920, 1040),
        1.25,
        false);

    private static readonly MonitorInfo HighDpiMonitor = new(
        @"\\.\DISPLAY3",
        new PixelRect(1920, 0, 3840, 2160),
        new PixelRect(1920, 0, 3840, 2100),
        2.0,
        false);

    [Fact]
    public void ScreenCenter_PlacesWindowAtCenterOfWorkArea()
    {
        var bounds = WindowPlacementHelper.CalculateBounds(
            WindowPlacementMode.ScreenCenter,
            anchor: null,
            lastBounds: null,
            targetMonitor: PrimaryMonitor,
            desiredWidthPx: 460,
            desiredHeightPx: 480,
            fallbackCursor: new PixelPoint(500, 500));

        bounds.X.ShouldBe((1920 - 460) / 2);
        bounds.Y.ShouldBe((1040 - 480) / 2);
        bounds.Width.ShouldBe(460);
        bounds.Height.ShouldBe(480);
    }

    [Fact]
    public void FollowCursor_PlacesWindow16PixelsBottomRightOfAnchor()
    {
        var anchor = new PixelPoint(200, 200);
        var bounds = WindowPlacementHelper.CalculateBounds(
            WindowPlacementMode.FollowCursor,
            anchor: anchor,
            lastBounds: null,
            targetMonitor: PrimaryMonitor,
            desiredWidthPx: 400,
            desiredHeightPx: 300,
            fallbackCursor: anchor);

        bounds.X.ShouldBe(200 + 16);
        bounds.Y.ShouldBe(200 + 16);
        bounds.Width.ShouldBe(400);
        bounds.Height.ShouldBe(300);
    }

    [Fact]
    public void FollowCursor_WhenOverflowsWorkAreaRightOrBottom_FlipsToLeftOrTop()
    {
        // 靠近右下边缘的光标
        var anchor = new PixelPoint(1800, 1000);
        var bounds = WindowPlacementHelper.CalculateBounds(
            WindowPlacementMode.FollowCursor,
            anchor: anchor,
            lastBounds: null,
            targetMonitor: PrimaryMonitor,
            desiredWidthPx: 400,
            desiredHeightPx: 300,
            fallbackCursor: anchor);

        // 翻转到左上
        bounds.X.ShouldBe(1800 - 16 - 400);
        bounds.Y.ShouldBe(1000 - 16 - 300);
        bounds.Right.ShouldBeLessThanOrEqualTo(PrimaryMonitor.WorkArea.Right);
        bounds.Bottom.ShouldBeLessThanOrEqualTo(PrimaryMonitor.WorkArea.Bottom);
    }

    [Fact]
    public void WindowLargerThanWorkArea_ShrinksWindowToFitWorkArea()
    {
        var smallWorkAreaMonitor = new MonitorInfo(
            @"\\.\DISPLAY_SMALL",
            new PixelRect(0, 0, 800, 600),
            new PixelRect(0, 0, 800, 400),
            1.0,
            true);

        var bounds = WindowPlacementHelper.CalculateBounds(
            WindowPlacementMode.ScreenCenter,
            anchor: null,
            lastBounds: null,
            targetMonitor: smallWorkAreaMonitor,
            desiredWidthPx: 1000,
            desiredHeightPx: 600,
            fallbackCursor: new PixelPoint(100, 100));

        bounds.Width.ShouldBe(800);
        bounds.Height.ShouldBe(400);
        bounds.X.ShouldBe(0);
        bounds.Y.ShouldBe(0);
    }

    [Fact]
    public void MultiMonitor_SyntheticNegativeCoordinates_PlacesCorrectlyInTargetMonitor()
    {
        var anchor = new PixelPoint(-1000, 300);
        var bounds = WindowPlacementHelper.CalculateBounds(
            WindowPlacementMode.FollowCursor,
            anchor: anchor,
            lastBounds: null,
            targetMonitor: LeftNegativeMonitor,
            desiredWidthPx: 500,
            desiredHeightPx: 400,
            fallbackCursor: anchor);

        bounds.X.ShouldBe(-1000 + 16);
        bounds.Y.ShouldBe(300 + 16);
        bounds.X.ShouldBeGreaterThanOrEqualTo(LeftNegativeMonitor.WorkArea.X);
        bounds.Right.ShouldBeLessThanOrEqualTo(LeftNegativeMonitor.WorkArea.Right);
    }

    [Fact]
    public void RememberLast_WhenMonitorExists_ClampsLastBoundsToWorkArea()
    {
        var lastBounds = new PixelRect(100, 100, 460, 480);
        var targetMonitor = WindowPlacementHelper.FindTargetMonitor(
            WindowPlacementMode.RememberLast,
            anchor: null,
            lastBounds: lastBounds,
            monitors: [PrimaryMonitor, LeftNegativeMonitor],
            fallbackCursor: new PixelPoint(0, 0));

        targetMonitor.ShouldNotBeNull();
        targetMonitor!.DeviceName.ShouldBe(PrimaryMonitor.DeviceName);

        var bounds = WindowPlacementHelper.CalculateBounds(
            WindowPlacementMode.RememberLast,
            anchor: null,
            lastBounds: lastBounds,
            targetMonitor: targetMonitor,
            desiredWidthPx: 460,
            desiredHeightPx: 480,
            fallbackCursor: new PixelPoint(0, 0));

        bounds.X.ShouldBe(100);
        bounds.Y.ShouldBe(100);
        bounds.Width.ShouldBe(460);
        bounds.Height.ShouldBe(480);
    }

    [Fact]
    public void RememberLast_WhenMonitorDisconnected_FallsBackToPrimaryScreen()
    {
        // 曾处于已拔出的显示器位置（如 X=5000）
        var oldBounds = new PixelRect(5000, 200, 460, 480);
        var targetMonitor = WindowPlacementHelper.FindTargetMonitor(
            WindowPlacementMode.RememberLast,
            anchor: null,
            lastBounds: oldBounds,
            monitors: [PrimaryMonitor],
            fallbackCursor: new PixelPoint(500, 500));

        targetMonitor.ShouldNotBeNull();
        targetMonitor!.DeviceName.ShouldBe(PrimaryMonitor.DeviceName);
        targetMonitor.IsPrimary.ShouldBeTrue();
    }

    [Fact]
    public void AnchorInGapBetweenMonitors_FindsNearestMonitor()
    {
        // 坐标落在两个显示器之间虚空区域
        var voidPoint = new PixelPoint(-50, 2000);
        var targetMonitor = WindowPlacementHelper.FindTargetMonitor(
            WindowPlacementMode.FollowCursor,
            anchor: voidPoint,
            lastBounds: null,
            monitors: [PrimaryMonitor, LeftNegativeMonitor],
            fallbackCursor: voidPoint);

        targetMonitor.ShouldNotBeNull();
    }
}
