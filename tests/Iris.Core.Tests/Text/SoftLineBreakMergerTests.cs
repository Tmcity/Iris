using System;
using System.Collections.Generic;
using Iris.Core.Text;
using Shouldly;
using Xunit;

namespace Iris.Core.Tests.Text;

public sealed class SoftLineBreakMergerTests
{
    [Fact]
    public void Merge_LatinWrapping_MergesWithSpace()
    {
        var lines = new[] { "This is a long", "sentence that wraps." };
        SoftLineBreakMerger.Merge(lines).ShouldBe("This is a long sentence that wraps.");
    }

    [Fact]
    public void Merge_CjkWrapping_DirectlyConcatenates()
    {
        var lines = new[] { "第一行没有结束", "第二行继续。" };
        SoftLineBreakMerger.Merge(lines).ShouldBe("第一行没有结束第二行继续。");
    }

    [Fact]
    public void Merge_EndOfSentenceDot_PreservesBreak()
    {
        var lines = new[] { "End of sentence.", "Next sentence" };
        SoftLineBreakMerger.Merge(lines).ShouldBe("End of sentence.\nNext sentence");
    }

    [Fact]
    public void Merge_ClosingQuotesAfterSentencePunctuation_PreservesBreak()
    {
        var lines = new[] { "他说：“好。”", "然后离开" };
        SoftLineBreakMerger.Merge(lines).ShouldBe("他说：“好。”\n然后离开");
    }

    [Fact]
    public void Merge_ColonAndListMarkers_PreservesBreaks()
    {
        var lines = new[] { "Items:", "- apple", "- banana" };
        SoftLineBreakMerger.Merge(lines).ShouldBe("Items:\n- apple\n- banana");
    }

    [Fact]
    public void Merge_NumberedList_PreservesBreak()
    {
        var lines = new[] { "1. first", "2. second" };
        SoftLineBreakMerger.Merge(lines).ShouldBe("1. first\n2. second");
    }

    [Fact]
    public void Merge_DecimalNumbers_NotListMarker_Merges()
    {
        var lines1 = new[] { "The weight is", "1.5 kg" };
        SoftLineBreakMerger.Merge(lines1).ShouldBe("The weight is 1.5 kg");

        var lines2 = new[] { "Pi is about", "3.14 today" };
        SoftLineBreakMerger.Merge(lines2).ShouldBe("Pi is about 3.14 today");
    }

    [Fact]
    public void Merge_ParenthesisAndSesameNumberedList_PreservesBreak()
    {
        var lines1 = new[] { "1) a", "2) b" };
        SoftLineBreakMerger.Merge(lines1).ShouldBe("1) a\n2) b");

        var lines2 = new[] { "1、第一", "2、第二" };
        SoftLineBreakMerger.Merge(lines2).ShouldBe("1、第一\n2、第二");
    }

    [Fact]
    public void Merge_ParenthesizedNumberedList_PreservesBreak()
    {
        var lines = new[] { "（1）甲", "（2）乙" };
        SoftLineBreakMerger.Merge(lines).ShouldBe("（1）甲\n（2）乙");
    }

    [Fact]
    public void Merge_TrailingHyphen_StripsHyphenAndConcatenates()
    {
        var lines = new[] { "inter-", "national" };
        SoftLineBreakMerger.Merge(lines).ShouldBe("international");
    }

    [Fact]
    public void Merge_OneSideCjk_MergesWithSpace()
    {
        var lines = new[] { "中文", "English" };
        SoftLineBreakMerger.Merge(lines).ShouldBe("中文 English");
    }

    [Fact]
    public void Merge_Hangul_NotCjk_MergesWithSpace()
    {
        var lines = new[] { "안녕하세요", "반갑습니다" };
        SoftLineBreakMerger.Merge(lines).ShouldBe("안녕하세요 반갑습니다");
    }

    [Fact]
    public void Merge_EmptyLine_PreservesEmptyLine()
    {
        var lines = new[] { "段落一", "", "段落二" };
        SoftLineBreakMerger.Merge(lines).ShouldBe("段落一\n\n段落二");
    }

    [Fact]
    public void Merge_TrailingAndLeadingWhitespace_TrimsBeforeMerge()
    {
        var lines = new[] { "trailing   ", "   leading" };
        SoftLineBreakMerger.Merge(lines).ShouldBe("trailing leading");
    }

    [Fact]
    public void Merge_Semicolon_PreservesBreak()
    {
        var lines = new[] { "A;", "B" };
        SoftLineBreakMerger.Merge(lines).ShouldBe("A;\nB");
    }

    [Fact]
    public void Merge_HyphenFollowedByNonSpace_NotListMarker_Merges()
    {
        var lines = new[] { "Temperature is", "-5 degrees" };
        SoftLineBreakMerger.Merge(lines).ShouldBe("Temperature is -5 degrees");
    }

    [Fact]
    public void Merge_CjkExtensionB_SurrogatePairsDirectlyConcatenate()
    {
        var lines = new[] { "\U00020000", "\U00020001" };
        SoftLineBreakMerger.Merge(lines).ShouldBe("\U00020000\U00020001");
    }

    [Fact]
    public void Merge_IsParagraphBreakAfter_PreservesBreakOnlyWhenCallbackReturnsTrue()
    {
        var lines = new[] { "Line zero text", "Line one text", "Line two text" };
        var result = SoftLineBreakMerger.Merge(lines, index => index == 0);
        result.ShouldBe("Line zero text\nLine one text Line two text");
    }

    [Fact]
    public void Merge_EmptyOrNullInput_ReturnsEmptyString()
    {
        SoftLineBreakMerger.Merge(Array.Empty<string>()).ShouldBe(string.Empty);
    }
}
