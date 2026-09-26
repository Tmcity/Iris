using Iris.Core.Settings;
using Iris.Core.Translation;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Iris.Core.Tests.Translation;

public sealed class TextPreprocessorTests
{
    private readonly ISettingsService _settings = Substitute.For<ISettingsService>();

    private void SetSelectionLineBreaks(LineBreakHandling handling)
    {
        _settings.Current.Returns(new AppSettings
        {
            Translation = new TranslationSettings
            {
                SelectionLineBreaks = handling,
            },
        });
    }

    [Fact]
    public void Prepare_TrimsLeadingAndTrailingWhitespace()
    {
        SetSelectionLineBreaks(LineBreakHandling.Keep);
        var preprocessor = new TextPreprocessor(_settings);

        preprocessor.Prepare("  hello  ", TextOrigin.Input).ShouldBe("hello");
    }

    [Fact]
    public void Prepare_NormalizesCrLfAndCrToLf()
    {
        SetSelectionLineBreaks(LineBreakHandling.Keep);
        var preprocessor = new TextPreprocessor(_settings);

        preprocessor.Prepare("a\r\nb\rc", TextOrigin.Input).ShouldBe("a\nb\nc");
    }

    [Fact]
    public void Prepare_NormalizesUnicodeLineBreaksToLf()
    {
        SetSelectionLineBreaks(LineBreakHandling.Keep);
        var preprocessor = new TextPreprocessor(_settings);

        preprocessor.Prepare("a\u2028b\u2029c\u0085d", TextOrigin.Input).ShouldBe("a\nb\nc\nd");
    }

    [Fact]
    public void Prepare_RemovesInvisibleCharacters()
    {
        SetSelectionLineBreaks(LineBreakHandling.Keep);
        var preprocessor = new TextPreprocessor(_settings);

        preprocessor.Prepare("he\u200Bllo\uFEFF\u2060", TextOrigin.Input).ShouldBe("hello");
    }

    [Fact]
    public void Prepare_PreservesZwjInCompositeEmoji()
    {
        SetSelectionLineBreaks(LineBreakHandling.Keep);
        var preprocessor = new TextPreprocessor(_settings);
        const string input = "\U0001F468\u200D\U0001F469\u200D\U0001F467";

        preprocessor.Prepare(input, TextOrigin.Input).ShouldBe(input);
    }

    [Fact]
    public void Prepare_PreservesZwnjInPersianOrIndicText()
    {
        SetSelectionLineBreaks(LineBreakHandling.Keep);
        var preprocessor = new TextPreprocessor(_settings);
        const string input = "می\u200Cخواهم";

        preprocessor.Prepare(input, TextOrigin.Input).ShouldBe(input);
    }

    [Fact]
    public void Prepare_ReplacesNbspWithStandardSpace()
    {
        SetSelectionLineBreaks(LineBreakHandling.Keep);
        var preprocessor = new TextPreprocessor(_settings);

        preprocessor.Prepare("a\u00A0b", TextOrigin.Input).ShouldBe("a b");
    }

    [Theory]
    [InlineData("a\n\n\n\nb", "a\n\nb")]
    [InlineData("a\n   \n\t\nb", "a\n\nb")]
    public void Prepare_CompressesConsecutiveEmptyLinesToOne(string input, string expected)
    {
        SetSelectionLineBreaks(LineBreakHandling.Keep);
        var preprocessor = new TextPreprocessor(_settings);

        preprocessor.Prepare(input, TextOrigin.Input).ShouldBe(expected);
    }

    [Fact]
    public void Prepare_PreservesIndentationOnCode()
    {
        SetSelectionLineBreaks(LineBreakHandling.Keep);
        var preprocessor = new TextPreprocessor(_settings);

        const string code = "def f():\n    return 1";
        preprocessor.Prepare(code, TextOrigin.Input).ShouldBe(code);
    }

    [Fact]
    public void Prepare_SelectionWithMergeSmart_MergesSoftLineBreaks()
    {
        SetSelectionLineBreaks(LineBreakHandling.MergeSmart);
        var preprocessor = new TextPreprocessor(_settings);

        preprocessor.Prepare("This is\nwrapped.", TextOrigin.Selection).ShouldBe("This is wrapped.");
    }

    [Fact]
    public void Prepare_SelectionWithKeep_PreservesLineBreaks()
    {
        SetSelectionLineBreaks(LineBreakHandling.Keep);
        var preprocessor = new TextPreprocessor(_settings);

        preprocessor.Prepare("This is\nwrapped.", TextOrigin.Selection).ShouldBe("This is\nwrapped.");
    }

    [Fact]
    public void Prepare_InputWithMergeSmart_DoesNotMerge()
    {
        SetSelectionLineBreaks(LineBreakHandling.MergeSmart);
        var preprocessor = new TextPreprocessor(_settings);

        preprocessor.Prepare("This is\nwrapped.", TextOrigin.Input).ShouldBe("This is\nwrapped.");
    }

    [Fact]
    public void Prepare_SettingsUpdatedBetweenCalls_UsesNewSettingsWithoutCaching()
    {
        SetSelectionLineBreaks(LineBreakHandling.Keep);
        var preprocessor = new TextPreprocessor(_settings);

        preprocessor.Prepare("This is\nwrapped.", TextOrigin.Selection).ShouldBe("This is\nwrapped.");

        // 修改设置
        SetSelectionLineBreaks(LineBreakHandling.MergeSmart);
        preprocessor.Prepare("This is\nwrapped.", TextOrigin.Selection).ShouldBe("This is wrapped.");
    }
}
