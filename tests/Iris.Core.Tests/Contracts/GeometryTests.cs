using Iris.Core.Imaging;
using Shouldly;
using Xunit;

namespace Iris.Core.Tests.Contracts;

public class GeometryTests
{
    [Fact]
    public void Contains_IncludesTopLeft_ExcludesBottomRight()
    {
        var rect = new PixelRect(10, 20, 100, 200);

        // Top-left inclusive
        rect.Contains(new PixelPoint(10, 20)).ShouldBeTrue();

        // Points inside
        rect.Contains(new PixelPoint(50, 50)).ShouldBeTrue();
        rect.Contains(new PixelPoint(109, 219)).ShouldBeTrue();

        // Right / Bottom boundaries exclusive
        rect.Contains(new PixelPoint(110, 20)).ShouldBeFalse();
        rect.Contains(new PixelPoint(10, 220)).ShouldBeFalse();
        rect.Contains(new PixelPoint(110, 220)).ShouldBeFalse();

        // Points outside
        rect.Contains(new PixelPoint(9, 20)).ShouldBeFalse();
        rect.Contains(new PixelPoint(10, 19)).ShouldBeFalse();
    }

    [Fact]
    public void IsEmpty_ReturnsTrueWhenWidthOrHeightLessOrEqualZero()
    {
        new PixelRect(0, 0, 0, 10).IsEmpty.ShouldBeTrue();
        new PixelRect(0, 0, 10, 0).IsEmpty.ShouldBeTrue();
        new PixelRect(0, 0, -1, 10).IsEmpty.ShouldBeTrue();
        new PixelRect(0, 0, 10, -5).IsEmpty.ShouldBeTrue();
        new PixelRect(0, 0, 0, 0).IsEmpty.ShouldBeTrue();

        new PixelRect(0, 0, 1, 1).IsEmpty.ShouldBeFalse();
        new PixelRect(10, 20, 100, 200).IsEmpty.ShouldBeFalse();
    }
}
