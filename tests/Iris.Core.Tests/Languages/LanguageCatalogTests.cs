using System.Linq;
using Iris.Core.Languages;
using Shouldly;
using Xunit;

namespace Iris.Core.Tests.Languages;

public sealed class LanguageCatalogTests
{
    private readonly LanguageCatalog _catalog = new();

    [Fact]
    public void All_ContainsExactFourteenLanguages_InExpectedOrder()
    {
        var expectedCodes = new[]
        {
            LanguageCodes.ChineseSimplified,
            LanguageCodes.ChineseTraditional,
            LanguageCodes.English,
            LanguageCodes.Japanese,
            LanguageCodes.Korean,
            LanguageCodes.French,
            LanguageCodes.German,
            LanguageCodes.Spanish,
            LanguageCodes.Russian,
            LanguageCodes.Portuguese,
            LanguageCodes.Italian,
            LanguageCodes.Vietnamese,
            LanguageCodes.Thai,
            LanguageCodes.Arabic,
        };

        _catalog.All.Count.ShouldBe(14);
        _catalog.All.Select(l => l.Code).ShouldBe(expectedCodes);
    }

    [Theory]
    [InlineData("zh-Hans", "zh-Hans", "Simplified Chinese", "简体中文")]
    [InlineData("ZH-HANS", "zh-Hans", "Simplified Chinese", "简体中文")]
    [InlineData("zh-hans", "zh-Hans", "Simplified Chinese", "简体中文")]
    [InlineData("en", "en", "English", "English")]
    [InlineData("EN", "en", "English", "English")]
    [InlineData("ja", "ja", "Japanese", "日本語")]
    public void Find_ValidCodes_ReturnsCanonicalLanguageInfo(string input, string expectedCode, string expectedEnglish, string expectedNative)
    {
        var info = _catalog.Find(input);

        info.ShouldNotBeNull();
        info.Code.ShouldBe(expectedCode);
        info.EnglishName.ShouldBe(expectedEnglish);
        info.NativeName.ShouldBe(expectedNative);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("auto")]
    [InlineData("AUTO")]
    [InlineData("unknown")]
    [InlineData("xyz")]
    public void Find_NullEmptyAutoOrUnknown_ReturnsNull(string? code)
    {
        _catalog.Find(code!).ShouldBeNull();
    }

    [Theory]
    [InlineData("zh-Hans", "zh-Hant", true)]
    [InlineData("en", "en-US", true)]
    [InlineData("EN", "en", true)]
    [InlineData("en", "fr", false)]
    [InlineData("auto", "auto", false)]
    [InlineData(null, "en", false)]
    [InlineData("en", null, false)]
    [InlineData("", "en", false)]
    [InlineData("   ", "en", false)]
    public void IsSameFamily_ReturnsExpectedResult(string? codeA, string? codeB, bool expected)
    {
        _catalog.IsSameFamily(codeA!, codeB!).ShouldBe(expected);
    }

    [Theory]
    [InlineData("auto", "the auto-detected language")]
    [InlineData("AUTO", "the auto-detected language")]
    [InlineData(null, "the auto-detected language")]
    [InlineData("", "the auto-detected language")]
    [InlineData("   ", "the auto-detected language")]
    [InlineData("zh-Hans", "Simplified Chinese")]
    [InlineData("ZH-HANS", "Simplified Chinese")]
    [InlineData("en", "English")]
    [InlineData(" pl ", "pl")]
    [InlineData("nl", "nl")]
    public void GetPromptName_ReturnsExpectedPromptName(string? code, string expected)
    {
        _catalog.GetPromptName(code!).ShouldBe(expected);
    }
}
