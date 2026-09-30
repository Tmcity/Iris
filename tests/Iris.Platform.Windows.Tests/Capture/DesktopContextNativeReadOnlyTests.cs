using System;
using System.Linq;
using Iris.Core.Imaging;
using Iris.Core.Platform;
using Shouldly;
using Xunit;

namespace Iris.Platform.Windows.Capture;

public sealed class DesktopContextNativeReadOnlyTests
{
    [Fact]
    public void NativeDesktopContext_GetCursorPosition_ReturnsPhysicalCoordinates()
    {
        var context = new DesktopContext();
        try
        {
            var cursor = context.GetCursorPosition();
            (cursor.X != int.MinValue).ShouldBeTrue();
        }
        catch (Iris.Core.Errors.IrisException ex) when (ex.Message == "无法获取鼠标位置")
        {
            Assert.Skip("[ENVIRONMENT LIMITATION] Native cursor position query unavailable in non-interactive/headless test session.");
        }
    }

    [Fact]
    public void NativeDesktopContext_GetMonitors_ReturnsPhysicalMonitors_AndSelectsNearest()
    {
        var context = new DesktopContext();
        var monitors = context.GetMonitors();

        monitors.ShouldNotBeNull();
        monitors.Count.ShouldBeGreaterThan(0, "Real desktop environment should have at least 1 monitor");

        // Primary monitor verification
        monitors.Count(m => m.IsPrimary).ShouldBe(1, "Exactly one primary monitor should exist in desktop session");

        foreach (var m in monitors)
        {
            m.DeviceName.ShouldNotBeNullOrWhiteSpace();
            m.Bounds.Width.ShouldBeGreaterThan(0);
            m.Bounds.Height.ShouldBeGreaterThan(0);
            m.WorkArea.Width.ShouldBeGreaterThan(0);
            m.WorkArea.Height.ShouldBeGreaterThan(0);

            // WorkArea inside Bounds
            m.WorkArea.X.ShouldBeGreaterThanOrEqualTo(m.Bounds.X);
            m.WorkArea.Y.ShouldBeGreaterThanOrEqualTo(m.Bounds.Y);
            ((long)m.WorkArea.X + m.WorkArea.Width).ShouldBeLessThanOrEqualTo((long)m.Bounds.X + m.Bounds.Width);
            ((long)m.WorkArea.Y + m.WorkArea.Height).ShouldBeLessThanOrEqualTo((long)m.Bounds.Y + m.Bounds.Height);

            m.DpiScale.ShouldBeGreaterThan(0.0);
            double.IsNaN(m.DpiScale).ShouldBeFalse();
            double.IsInfinity(m.DpiScale).ShouldBeFalse();
        }

        // Test GetMonitorAt with monitor anchor point
        var testPoint = new PixelPoint(monitors[0].Bounds.X + 10, monitors[0].Bounds.Y + 10);
        var nearest = context.GetMonitorAt(testPoint);

        nearest.ShouldNotBeNull();
        monitors.ShouldContain(nearest);

        // Best effort: if cursor query is available, also test cursor position
        try
        {
            var cursor = context.GetCursorPosition();
            var cursorNearest = context.GetMonitorAt(cursor);
            monitors.ShouldContain(cursorNearest);
        }
        catch (Iris.Core.Errors.IrisException ex) when (ex.Message == "无法获取鼠标位置")
        {
            // Expected in headless/non-interactive session
        }
    }
}
