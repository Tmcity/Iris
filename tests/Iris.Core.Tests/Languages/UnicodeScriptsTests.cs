using System.Text;
using Iris.Core.Languages;
using Shouldly;
using Xunit;

namespace Iris.Core.Tests.Languages;

public sealed class UnicodeScriptsTests
{
    [Theory]
    // 假名首尾
    [InlineData(0x3040, UnicodeScript.Kana)]
    [InlineData(0x309F, UnicodeScript.Kana)]
    [InlineData(0x30A0, UnicodeScript.Kana)]
    [InlineData(0x30FF, UnicodeScript.Kana)]
    [InlineData(0x31F0, UnicodeScript.Kana)]
    [InlineData(0x31FF, UnicodeScript.Kana)]
    [InlineData(0xFF66, UnicodeScript.Kana)]
    [InlineData(0xFF9F, UnicodeScript.Kana)]
    // 谚文首尾
    [InlineData(0x1100, UnicodeScript.Hangul)]
    [InlineData(0x11FF, UnicodeScript.Hangul)]
    [InlineData(0x3130, UnicodeScript.Hangul)]
    [InlineData(0x318F, UnicodeScript.Hangul)]
    [InlineData(0xA960, UnicodeScript.Hangul)]
    [InlineData(0xA97F, UnicodeScript.Hangul)]
    [InlineData(0xAC00, UnicodeScript.Hangul)]
    [InlineData(0xD7AF, UnicodeScript.Hangul)]
    [InlineData(0xD7B0, UnicodeScript.Hangul)]
    [InlineData(0xD7FF, UnicodeScript.Hangul)]
    // 汉字首尾
    [InlineData(0x3400, UnicodeScript.Han)]
    [InlineData(0x4DBF, UnicodeScript.Han)]
    [InlineData(0x4E00, UnicodeScript.Han)]
    [InlineData(0x9FFF, UnicodeScript.Han)]
    [InlineData(0xF900, UnicodeScript.Han)]
    [InlineData(0xFAFF, UnicodeScript.Han)]
    [InlineData(0x20000, UnicodeScript.Han)]
    [InlineData(0x323AF, UnicodeScript.Han)]
    // 西里尔首尾
    [InlineData(0x0400, UnicodeScript.Cyrillic)]
    [InlineData(0x052F, UnicodeScript.Cyrillic)]
    // 泰文首尾
    [InlineData(0x0E00, UnicodeScript.Thai)]
    [InlineData(0x0E7F, UnicodeScript.Thai)]
    // 阿拉伯首尾
    [InlineData(0x0600, UnicodeScript.Arabic)]
    [InlineData(0x06FF, UnicodeScript.Arabic)]
    [InlineData(0x0750, UnicodeScript.Arabic)]
    [InlineData(0x077F, UnicodeScript.Arabic)]
    [InlineData(0x08A0, UnicodeScript.Arabic)]
    [InlineData(0x08FF, UnicodeScript.Arabic)]
    [InlineData(0xFB50, UnicodeScript.Arabic)]
    [InlineData(0xFDFF, UnicodeScript.Arabic)]
    [InlineData(0xFE70, UnicodeScript.Arabic)]
    [InlineData(0xFEFF, UnicodeScript.Arabic)]
    // 拉丁首尾
    [InlineData(0x0041, UnicodeScript.Latin)]
    [InlineData(0x005A, UnicodeScript.Latin)]
    [InlineData(0x0061, UnicodeScript.Latin)]
    [InlineData(0x007A, UnicodeScript.Latin)]
    [InlineData(0x00C0, UnicodeScript.Latin)]
    [InlineData(0x024F, UnicodeScript.Latin)]
    [InlineData(0x1E00, UnicodeScript.Latin)]
    [InlineData(0x1EFF, UnicodeScript.Latin)]
    [InlineData(0xFF21, UnicodeScript.Latin)]
    [InlineData(0xFF3A, UnicodeScript.Latin)]
    [InlineData(0xFF41, UnicodeScript.Latin)]
    [InlineData(0xFF5A, UnicodeScript.Latin)]
    // 其他码点
    [InlineData(0x0020, UnicodeScript.Other)]
    [InlineData(0x0030, UnicodeScript.Other)]
    [InlineData(0x0370, UnicodeScript.Other)]
    internal void Classify_ReturnsExpectedUnicodeScript(int codePoint, UnicodeScript expected)
    {
        var rune = new Rune(codePoint);
        UnicodeScripts.Classify(rune).ShouldBe(expected);
    }

    [Theory]
    // 汉字、假名、'。'（U+3002）、全角 'Ａ'（U+FF21）→ true
    [InlineData(0x4E2D, true)] // 汉字 '中'
    [InlineData(0x3042, true)] // 假名 'あ'
    [InlineData(0x3002, true)] // '。'
    [InlineData(0xFF21, true)] // 全角 'Ａ'
    [InlineData(0x20000, true)] // CJK 扩展 B
    // 谚文、拉丁、西里尔 → false
    [InlineData(0xAC00, false)] // 谚文 '가'
    [InlineData('A', false)]    // 拉丁
    [InlineData('x', false)]    // 拉丁
    [InlineData(0x0416, false)] // 西里尔 'Ж'
    [InlineData(0x0E01, false)] // 泰文
    [InlineData(0x0627, false)] // 阿拉伯
    public void IsCjkForLineMerge_ReturnsExpectedResult(int codePoint, bool expected)
    {
        var rune = new Rune(codePoint);
        UnicodeScripts.IsCjkForLineMerge(rune).ShouldBe(expected);
    }
}
