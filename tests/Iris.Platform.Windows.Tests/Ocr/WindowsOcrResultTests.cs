using Iris.Core.Errors;
using Iris.Core.Imaging;
using Iris.Platform.Windows.Ocr;
using Shouldly;
using static Iris.Platform.Windows.Tests.Ocr.WindowsOcrTestScope;

namespace Iris.Platform.Windows.Tests.Ocr;

public sealed class WindowsOcrResultTests
{
    [Theory]
    [InlineData("你 好 世 界", "你好世界")][InlineData("𠀀 𠀁", "𠀀𠀁")][InlineData("あ い", "あい")]
    [InlineData("한 글", "한 글")][InlineData("你  好", "你  好")][InlineData("你\t好", "你\t好")]
    [InlineData("你\u00A0好", "你\u00A0好")][InlineData("A 中", "A 中")][InlineData("中 A", "中 A")]
    [InlineData(" 你 好 ", " 你好 ")][InlineData("中 ， 文", "中，文")][InlineData("", "")]
    public Task RuneSpacing_RemovesOnlySpecifiedSingleSpaces(string input, string expected) => Run(async f =>
    {
        f.Result = [new(input, [])]; var result = await f.Recognize(await Image());
        result.Lines.Single().Text.ShouldBe(expected); result.Lines[0].Bounds.ShouldBeNull(); result.Lines[0].Confidence.ShouldBeNull(); result.DetectedLanguage.ShouldBeNull();
    });
    [Fact]
    public Task WordBounds_AreSafeFloorCeilingUnionAndLineOrderIsPreserved() => Run(async f =>
    {
        f.Result = [new("first", [new(.2, 1.8, 4.1, 6.3), new(7.1, .1, 1, 2)]), new("second", []), new("third", [new(20, 21, 2, 3)])];
        var result = await f.Recognize(await Image());
        result.Lines.Select(l => l.Text).ShouldBe(new[] { "first", "second", "third" });
        result.Lines[0].Bounds.ShouldBe(new PixelRect(0, 0, 9, 9)); result.Lines[1].Bounds.ShouldBeNull(); result.Lines[2].Bounds.ShouldBe(new PixelRect(20, 21, 2, 3));
    });
    [Theory]
    [InlineData(double.NaN, 0, 1, 1)][InlineData(0, double.PositiveInfinity, 1, 1)][InlineData(-1, 0, 1, 1)]
    [InlineData(0, 0, 0, 1)][InlineData(0, 0, 1, -1)][InlineData(2147483647, 0, 1, 1)]
    [InlineData(0, 0, double.PositiveInfinity, 1)]
    public Task InvalidNativeRectangles_AreRejected(double x, double y, double width, double height) => Run(async f =>
    {
        f.Result = [new("private OCR", [new(x, y, width, height)])]; var image = await Image();
        var error = await Should.ThrowAsync<IrisException>(() => f.Recognize(image, expected: typeof(IrisException)));
        error.Code.ShouldBe(ErrorCode.InvalidResponse); error.Detail!.ShouldNotContain("private");
    });
    [Theory]
    [InlineData(0)][InlineData(1)][InlineData(2)][InlineData(3)][InlineData(4)]
    public Task InvalidOutputAndResultBudgets_AreRejected(int kind) => Run(async f =>
    {
        f.Result = kind switch
        {
            0 => null!, 1 => [new(null, [])], 2 => Enumerable.Repeat(new WindowsOcrNativeLine("x", []), 20001).ToArray(),
            3 => [new("x", Enumerable.Repeat(new WindowsOcrBox(0, 0, 1, 1), 100001).ToArray())],
            _ => [new(new string('x', 1000001), [])]
        };
        var image = await Image();
        (await Should.ThrowAsync<IrisException>(() => f.Recognize(image, expected: typeof(IrisException)))).Code.ShouldBe(ErrorCode.InvalidResponse);
    });
    [Fact]
    public Task EmptyNativeResult_IsSuccessful() => Run(async f =>
    {
        f.Result = []; (await f.Recognize(await Image())).Lines.ShouldBeEmpty();
    });
}
