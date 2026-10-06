using Iris.Core.Imaging;
using Iris.Core.Ocr;
using Shouldly;

namespace Iris.Core.Tests.Ocr;

public sealed class OcrNormalizerTests
{
    [Theory]
    [InlineData("hello", "world", "hello world")]
    [InlineData("hello.", "world", "hello.\nworld")]
    [InlineData("word", "- list", "word\n- list")]
    [InlineData("long-", "word", "longword")]
    [InlineData("你", "好", "你好")]
    [InlineData("𠀀", "字", "𠀀字")]
    [InlineData("한", "글", "한 글")]
    [InlineData("one\ntwo", "three", "one\ntwo three")]
    public void TextRulesUseExistingMerger(string a, string b, string expected) =>
        new OcrTextNormalizer().Normalize([new(a), new(b)], OcrTextLayout.MergeParagraphs).ShouldBe(expected);

    [Theory][InlineData(24, "a b")][InlineData(25, "a b")][InlineData(26, "a\nb")][InlineData(3, "a b")]
    public void StrictBottomGapThreshold(int nextY, string expected) =>
        new OcrTextNormalizer().Normalize([new("a", new(0, 0, 10, 10)), new("b", new(0, nextY, 10, 10))], OcrTextLayout.MergeParagraphs).ShouldBe(expected);
    [Fact]
    public void AllValidHeightsAndLongArithmetic_AreUsed()
    {
        var n = new OcrTextNormalizer();
        n.Normalize([new("a", new(0, 0, 5, 10)), new("b", new(0, 41, 5, 30))], OcrTextLayout.MergeParagraphs).ShouldBe("a\nb");
        n.Normalize([new("a", new(0, 0, 5, 10)), new("b", new(0, 40, 5, 30))], OcrTextLayout.MergeParagraphs).ShouldBe("a b");
        n.Normalize([new("a", new(0, int.MaxValue, 1, int.MaxValue)), new("b", new(0, int.MaxValue, 1, int.MaxValue))], OcrTextLayout.MergeParagraphs).ShouldBe("a b");
    }
    [Theory][InlineData(-1, 0, 1, 1)][InlineData(0, -1, 1, 1)][InlineData(0, 0, 0, 1)][InlineData(0, 0, 1, 0)]
    public void InvalidBoundsFallBackToText(int x, int y, int w, int h) =>
        new OcrTextNormalizer().Normalize([new("a", new PixelRect(x, y, w, h)), new("b", new(0, 1000, 5, 10))], OcrTextLayout.MergeParagraphs).ShouldBe("a b");
    [Fact]
    public void NullBoundsEmptyLinesAndKeepLinesPreserveInputs()
    {
        var n = new OcrTextNormalizer();
        OcrLine[] lines = [new("  a\r\nb  "), new(""), new("c\nd")];
        n.Normalize(lines, OcrTextLayout.KeepLines).ShouldBe("  a\r\nb  \n\nc\nd");
        n.Normalize([new("a"), new(""), new("b")], OcrTextLayout.MergeParagraphs).ShouldBe("a\n\nb");
        n.Normalize([new("a"), new("b", new(0, 1000, 1, 1))], OcrTextLayout.MergeParagraphs).ShouldBe("a b");
        n.Normalize([], OcrTextLayout.KeepLines).ShouldBeEmpty();
        Should.Throw<ArgumentNullException>(() => n.Normalize(null!, OcrTextLayout.KeepLines));
        Should.Throw<ArgumentException>(() => n.Normalize([null!], OcrTextLayout.KeepLines));
        Should.Throw<ArgumentException>(() => n.Normalize([new(null!)], OcrTextLayout.MergeParagraphs));
        Should.Throw<ArgumentOutOfRangeException>(() => n.Normalize([], (OcrTextLayout)100));
    }
}
