using Iris.App.Selection;
using Iris.Core.Platform;
using Shouldly;

namespace Iris.App.Tests.Selection;

public sealed class MouseIconGeometryTests
{
    [Theory]
    [InlineData(-990, -490, 1.0, -978, -478, 24)]
    [InlineData(-990, -490, 1.5, -972, -472, 36)]
    [InlineData(-990, -490, 2.0, -966, -466, 48)]
    [InlineData(999, 499, 1.0, 976, 476, 24)]
    [InlineData(999, 499, 1.5, 964, 464, 36)]
    [InlineData(int.MinValue, int.MinValue, 1.0, -1000, -500, 24)]
    [InlineData(int.MaxValue, int.MaxValue, 1.0, 976, 476, 24)]
    public void Placement_UsesDipsPhysicalHalfOpenWorkAreaAndWidenedArithmetic(int x, int y, double dpi, int expectedX, int expectedY, int size)
    {
        var monitor = new MonitorInfo("fake", new(-1000, -500, 2000, 1000), new(-1000, -500, 2000, 1000), dpi, true);
        var bounds = SelectionIconGeometry.Place(new(x, y), monitor);
        bounds.X.ShouldBe(expectedX); bounds.Y.ShouldBe(expectedY); bounds.Width.ShouldBe(size); bounds.Height.ShouldBe(size);
    }
    [Theory]
    [InlineData(0)][InlineData(-1)][InlineData(double.NaN)][InlineData(double.PositiveInfinity)]
    public void UnknownDpi_IsRejectedWithoutGuessing(double dpi)
    {
        var monitor = new MonitorInfo("fake", new(0, 0, 100, 100), new(0, 0, 100, 100), dpi, true);
        Should.Throw<InvalidOperationException>(() => SelectionIconGeometry.Place(new(0, 0), monitor));
    }
}
