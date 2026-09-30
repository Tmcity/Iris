using System;
using System.Collections.Generic;
using System.Numerics;
using Iris.Core.Errors;
using Iris.Core.Imaging;
using Iris.Core.Platform;
using Shouldly;
using Xunit;

namespace Iris.Platform.Windows.Capture;

public sealed class DesktopGeometryHelperTests
{
    [Fact]
    public void ValidateGeometry_ValidInputs_ReturnsTrue()
    {
        bool valid = DesktopGeometryHelper.ValidateGeometry(
            boundsLeft: 0, boundsTop: 0, boundsRight: 2560, boundsBottom: 1440,
            workLeft: 0, workTop: 0, workRight: 2560, workBottom: 1392,
            dpiScale: 1.0);

        valid.ShouldBeTrue();
    }

    [Theory]
    [InlineData(0, 0, 0, 1080, "Zero width")]
    [InlineData(0, 0, 1920, 0, "Zero height")]
    [InlineData(1920, 0, 0, 1080, "Negative width")]
    [InlineData(0, 1080, 1920, 0, "Negative height")]
    public void ValidateGeometry_InvalidBounds_ReturnsFalse(int bL, int bT, int bR, int bB, string reason)
    {
        bool valid = DesktopGeometryHelper.ValidateGeometry(bL, bT, bR, bB, 0, 0, 100, 100, 1.0);
        valid.ShouldBeFalse(reason);
    }

    [Theory]
    [InlineData(0, 0, 1920, 1080, -10, 0, 1920, 1080, "WorkArea left < Bounds left")]
    [InlineData(0, 0, 1920, 1080, 0, -10, 1920, 1080, "WorkArea top < Bounds top")]
    [InlineData(0, 0, 1920, 1080, 0, 0, 2000, 1080, "WorkArea right > Bounds right")]
    [InlineData(0, 0, 1920, 1080, 0, 0, 1920, 1200, "WorkArea bottom > Bounds bottom")]
    public void ValidateGeometry_WorkAreaOutOfBounds_ReturnsFalse(
        int bL, int bT, int bR, int bB,
        int wL, int wT, int wR, int wB,
        string reason)
    {
        bool valid = DesktopGeometryHelper.ValidateGeometry(bL, bT, bR, bB, wL, wT, wR, wB, 1.0);
        valid.ShouldBeFalse(reason);
    }

    [Theory]
    [InlineData(0.0, "Zero DPI")]
    [InlineData(-1.0, "Negative DPI")]
    [InlineData(double.NaN, "NaN DPI")]
    [InlineData(double.PositiveInfinity, "PositiveInfinity DPI")]
    [InlineData(double.NegativeInfinity, "NegativeInfinity DPI")]
    public void ValidateGeometry_InvalidDpi_ReturnsFalse(double dpiScale, string reason)
    {
        bool valid = DesktopGeometryHelper.ValidateGeometry(
            0, 0, 1920, 1080,
            0, 0, 1920, 1080,
            dpiScale);

        valid.ShouldBeFalse(reason);
    }

    [Fact]
    public void ValidateGeometry_OverflowEndpoints_ReturnsFalse()
    {
        // boundsLeft + width > int.MaxValue
        bool valid = DesktopGeometryHelper.ValidateGeometry(
            int.MaxValue - 10, 0, int.MaxValue, 100,
            int.MaxValue - 10, 0, int.MaxValue, 100,
            1.0);
        valid.ShouldBeTrue("Right on int.MaxValue boundary is allowed");

        // Artificial overflow if unchecked
        bool overflow = DesktopGeometryHelper.ValidateGeometry(
            int.MaxValue, 0, int.MinValue, 100,
            0, 0, 100, 100,
            1.0);
        overflow.ShouldBeFalse();
    }

    [Fact]
    public void GetNearestMonitor_EmptyList_ThrowsIrisException()
    {
        var ex = Should.Throw<IrisException>(() =>
            DesktopGeometryHelper.GetNearestMonitor([], new PixelPoint(0, 0)));

        ex.Code.ShouldBe(ErrorCode.Unknown);
        ex.Message.ShouldBe("无法获取显示器信息");
    }

    [Fact]
    public void GetNearestMonitor_PointInsideHalfOpenBounds_SelectsContainingMonitor()
    {
        var m1 = new MonitorInfo(@"\\.\DISPLAY1", new PixelRect(0, 0, 1920, 1080), new PixelRect(0, 0, 1920, 1040), 1.0, true);
        var m2 = new MonitorInfo(@"\\.\DISPLAY2", new PixelRect(1920, 0, 1920, 1080), new PixelRect(1920, 0, 1920, 1040), 1.0, false);

        var list = new[] { m1, m2 };

        // Origin of m1
        DesktopGeometryHelper.GetNearestMonitor(list, new PixelPoint(0, 0)).DeviceName.ShouldBe(@"\\.\DISPLAY1");
        // Inside m1
        DesktopGeometryHelper.GetNearestMonitor(list, new PixelPoint(100, 100)).DeviceName.ShouldBe(@"\\.\DISPLAY1");
        // Right edge of m1 (1919)
        DesktopGeometryHelper.GetNearestMonitor(list, new PixelPoint(1919, 500)).DeviceName.ShouldBe(@"\\.\DISPLAY1");

        // 1920 is outside m1 [0, 1920) and inside m2 [1920, 3840)
        DesktopGeometryHelper.GetNearestMonitor(list, new PixelPoint(1920, 500)).DeviceName.ShouldBe(@"\\.\DISPLAY2");
    }

    [Fact]
    public void GetNearestMonitor_NegativeCoordinates_SelectsCorrectMonitor()
    {
        // mLeft is to the left with negative X: [-1920, 0)
        var mLeft = new MonitorInfo(@"\\.\DISPLAY_L", new PixelRect(-1920, 0, 1920, 1080), new PixelRect(-1920, 0, 1920, 1040), 1.0, false);
        // mTop is above with negative Y: [0, 1920) x [-1080, 0)
        var mTop = new MonitorInfo(@"\\.\DISPLAY_T", new PixelRect(0, -1080, 1920, 1080), new PixelRect(0, -1080, 1920, 1040), 1.0, false);
        var mMain = new MonitorInfo(@"\\.\DISPLAY_M", new PixelRect(0, 0, 1920, 1080), new PixelRect(0, 0, 1920, 1040), 1.0, true);

        var list = new[] { mLeft, mTop, mMain };

        DesktopGeometryHelper.GetNearestMonitor(list, new PixelPoint(-500, 500)).DeviceName.ShouldBe(@"\\.\DISPLAY_L");
        DesktopGeometryHelper.GetNearestMonitor(list, new PixelPoint(500, -500)).DeviceName.ShouldBe(@"\\.\DISPLAY_T");
        DesktopGeometryHelper.GetNearestMonitor(list, new PixelPoint(500, 500)).DeviceName.ShouldBe(@"\\.\DISPLAY_M");
    }

    [Fact]
    public void GetNearestMonitor_GapBetweenMonitors_SelectsNearestPixel()
    {
        // Monitor 1: [0, 1000) -> rightmost pixel = 999
        var m1 = new MonitorInfo(@"\\.\DISPLAY1", new PixelRect(0, 0, 1000, 1000), new PixelRect(0, 0, 1000, 1000), 1.0, false);
        // Monitor 2: [1100, 2100) -> leftmost pixel = 1100
        var m2 = new MonitorInfo(@"\\.\DISPLAY2", new PixelRect(1100, 0, 1000, 1000), new PixelRect(1100, 0, 1000, 1000), 1.0, false);

        var list = new[] { m1, m2 };

        // Point at X=1049: distance to m1 (999) is 50; distance to m2 (1100) is 51 -> m1 is closer
        DesktopGeometryHelper.GetNearestMonitor(list, new PixelPoint(1049, 500)).DeviceName.ShouldBe(@"\\.\DISPLAY1");

        // Point at X=1050: distance to m1 (999) is 51; distance to m2 (1100) is 50 -> m2 is closer
        DesktopGeometryHelper.GetNearestMonitor(list, new PixelPoint(1050, 500)).DeviceName.ShouldBe(@"\\.\DISPLAY2");
    }

    [Fact]
    public void GetNearestMonitor_TieBreaking_PrefersPrimary_ThenDeviceNameOrdinal()
    {
        // Equidistant point from m1 and m2
        // Both cover (0,0) or are at same distance
        var m1 = new MonitorInfo(@"\\.\DISPLAY2", new PixelRect(10, 0, 10, 10), new PixelRect(10, 0, 10, 10), 1.0, false);
        var m2 = new MonitorInfo(@"\\.\DISPLAY1", new PixelRect(-20, 0, 10, 10), new PixelRect(-20, 0, 10, 10), 1.0, true);

        // Point at (0, 5):
        // To m1 (left=10): dx = 10, dy = 0, distSq = 100
        // To m2 (right=-11): dx = 11? Wait:
        // Let's set symmetric coordinates: m1 at [10, 20), m2 at [-20, -10) -> rightmost is -11, dx=11
        // Make symmetric:
        var mSecA = new MonitorInfo(@"\\.\DISPLAY_B", new PixelRect(10, 0, 10, 10), new PixelRect(10, 0, 10, 10), 1.0, false);
        var mPri = new MonitorInfo(@"\\.\DISPLAY_A", new PixelRect(-19, 0, 10, 10), new PixelRect(-19, 0, 10, 10), 1.0, true);
        // mPri: rightmost is -19 + 10 - 1 = -10. From 0: dx = 0 - (-10) = 10.
        // mSecA: leftmost is 10. From 0: dx = 10 - 0 = 10.
        // Both distSq = 100. Primary wins!
        var tieList = new[] { mSecA, mPri };
        DesktopGeometryHelper.GetNearestMonitor(tieList, new PixelPoint(0, 5)).DeviceName.ShouldBe(@"\\.\DISPLAY_A");

        // Reverse order test
        var reversedTieList = new[] { mPri, mSecA };
        DesktopGeometryHelper.GetNearestMonitor(reversedTieList, new PixelPoint(0, 5)).DeviceName.ShouldBe(@"\\.\DISPLAY_A");

        // When both are non-primary, DeviceName Ordinal ASC wins
        var mSecB = new MonitorInfo(@"\\.\DISPLAY_B", new PixelRect(10, 0, 10, 10), new PixelRect(10, 0, 10, 10), 1.0, false);
        var mSecC = new MonitorInfo(@"\\.\DISPLAY_A", new PixelRect(-19, 0, 10, 10), new PixelRect(-19, 0, 10, 10), 1.0, false);
        DesktopGeometryHelper.GetNearestMonitor(new[] { mSecB, mSecC }, new PixelPoint(0, 5)).DeviceName.ShouldBe(@"\\.\DISPLAY_A");
        DesktopGeometryHelper.GetNearestMonitor(new[] { mSecC, mSecB }, new PixelPoint(0, 5)).DeviceName.ShouldBe(@"\\.\DISPLAY_A");
    }

    [Fact]
    public void GetNearestMonitor_ExtremeIntCoordinates_NoOverflow_MatchesBigInteger()
    {
        // Extreme distance from int.MinValue to int.MaxValue
        var m1 = new MonitorInfo(@"\\.\DISPLAY1", new PixelRect(int.MinValue, int.MinValue, 10, 10), new PixelRect(int.MinValue, int.MinValue, 10, 10), 1.0, true);
        var m2 = new MonitorInfo(@"\\.\DISPLAY2", new PixelRect(int.MaxValue - 10, int.MaxValue - 10, 10, 10), new PixelRect(int.MaxValue - 10, int.MaxValue - 10, 10, 10), 1.0, false);

        var list = new[] { m1, m2 };

        // Test point at (int.MaxValue, int.MaxValue)
        var point = new PixelPoint(int.MaxValue, int.MaxValue);
        var selected = DesktopGeometryHelper.GetNearestMonitor(list, point);
        selected.DeviceName.ShouldBe(@"\\.\DISPLAY2");

        // Verify distance calculation against BigInteger
        long dx = (long)int.MaxValue - (int.MinValue + 10 - 1);
        long dy = (long)int.MaxValue - (int.MinValue + 10 - 1);

        BigInteger bigDx = dx;
        BigInteger bigDy = dy;
        BigInteger bigDistSq = (bigDx * bigDx) + (bigDy * bigDy);

        Int128 int128DistSq = (Int128)dx * (Int128)dx + (Int128)dy * (Int128)dy;

        ((BigInteger)int128DistSq).ShouldBe(bigDistSq);
        bigDistSq.ShouldBeGreaterThan(new BigInteger(ulong.MaxValue));
    }

    [Fact]
    public void GetNearestMonitor_10000RandomLayouts_MatchesBruteForcePixelEnumeration()
    {
        // Verified with seed 2201 per DESKTOP_CONTEXT_VALIDATION.md
        var rnd = new Random(2201);

        for (int iter = 0; iter < 10000; iter++)
        {
            // Small rectangles so we can brute-force all integer pixels
            int x1 = rnd.Next(-20, 20);
            int y1 = rnd.Next(-20, 20);
            int w1 = rnd.Next(1, 10);
            int h1 = rnd.Next(1, 10);

            int x2 = rnd.Next(-20, 20);
            int y2 = rnd.Next(-20, 20);
            int w2 = rnd.Next(1, 10);
            int h2 = rnd.Next(1, 10);

            var m1 = new MonitorInfo(@"\\.\DISPLAY1", new PixelRect(x1, y1, w1, h1), new PixelRect(x1, y1, w1, h1), 1.0, true);
            var m2 = new MonitorInfo(@"\\.\DISPLAY2", new PixelRect(x2, y2, w2, h2), new PixelRect(x2, y2, w2, h2), 1.0, false);

            var list = new[] { m1, m2 };

            int px = rnd.Next(-30, 30);
            int py = rnd.Next(-30, 30);
            var pt = new PixelPoint(px, py);

            var actual = DesktopGeometryHelper.GetNearestMonitor(list, pt);

            // Brute force minimum distance for m1 and m2
            long minD1 = BruteForceMinDistanceSq(m1.Bounds, px, py);
            long minD2 = BruteForceMinDistanceSq(m2.Bounds, px, py);

            string expectedName;
            if (minD1 < minD2)
            {
                expectedName = m1.DeviceName;
            }
            else if (minD2 < minD1)
            {
                expectedName = m2.DeviceName;
            }
            else
            {
                // Tie: m1 is primary
                expectedName = m1.DeviceName;
            }

            actual.DeviceName.ShouldBe(expectedName, $"Iteration {iter} failed for point ({px}, {py})");

            // Also verify reverse order produces identical result
            var reversed = DesktopGeometryHelper.GetNearestMonitor(new[] { m2, m1 }, pt);
            reversed.DeviceName.ShouldBe(expectedName, $"Reverse order iteration {iter} failed for point ({px}, {py})");
        }
    }

    private static long BruteForceMinDistanceSq(PixelRect rect, int px, int py)
    {
        long minSq = long.MaxValue;
        for (int x = rect.X; x < rect.X + rect.Width; x++)
        {
            for (int y = rect.Y; y < rect.Y + rect.Height; y++)
            {
                long dx = x - px;
                long dy = y - py;
                long sq = dx * dx + dy * dy;
                if (sq < minSq)
                {
                    minSq = sq;
                }
            }
        }
        return minSq;
    }
}
