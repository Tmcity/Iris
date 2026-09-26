using Iris.Core.Languages;
using Iris.Core.Translation;
using Shouldly;
using Xunit;

namespace Iris.Core.Tests.Translation;

public sealed class PromptRendererTests
{
    private readonly LanguageCatalog _catalog = new();
    private readonly PromptRenderer _renderer;

    public PromptRendererTests()
    {
        _renderer = new PromptRenderer(_catalog);
    }

    [Fact]
    public void Constructor_NullCatalog_ThrowsArgumentNullException()
    {
        Should.Throw<ArgumentNullException>(() => new PromptRenderer(null!));
    }

    [Fact]
    public void Render_DefaultTemplate_EnglishToSimplifiedChinese_RendersExpectedPrompt()
    {
        var request = new ProviderTranslateRequest("Hello", LanguageCodes.English, LanguageCodes.ChineseSimplified);
        var rendered = _renderer.Render(BuiltInPrompts.Default, request);

        rendered.System.ShouldContain("from English into Simplified Chinese");
        rendered.User.ShouldBe("Hello");
    }

    [Fact]
    public void Render_SourceLanguageAuto_RendersAutoDetectedLanguage()
    {
        var request = new ProviderTranslateRequest("Bonjour", LanguageCodes.Auto, LanguageCodes.English);
        var rendered = _renderer.Render(BuiltInPrompts.Default, request);

        rendered.System.ShouldContain("the auto-detected language");
        rendered.User.ShouldBe("Bonjour");
    }

    [Fact]
    public void Render_UnknownAndCaseSensitivePlaceholders_PreservedVerbatim()
    {
        var template = new PromptTemplate
        {
            Id = "custom.test",
            Name = "Test",
            System = "System with {{foo}} and {{TEXT}} and {{source_language}}",
            User = "User with {{bar}} and {{text}}",
        };

        var request = new ProviderTranslateRequest("sample text", LanguageCodes.English, LanguageCodes.ChineseSimplified);
        var rendered = _renderer.Render(template, request);

        rendered.System.ShouldBe("System with {{foo}} and {{TEXT}} and English");
        rendered.User.ShouldBe("User with {{bar}} and sample text");
    }

    [Fact]
    public void Render_PromptInjectionAttemptInText_IsNotEvaluatedAgain()
    {
        const string injectionText = "{{target_language}} {{text}} {{source_language}}";
        var request = new ProviderTranslateRequest(injectionText, LanguageCodes.English, LanguageCodes.ChineseSimplified);
        var rendered = _renderer.Render(BuiltInPrompts.Default, request);

        rendered.User.ShouldBe(injectionText);
    }

    [Fact]
    public void Render_RegexSubstitutionPatternsInText_PreservedVerbatim()
    {
        const string patternsText = "$& $1 $$ $0";
        var request = new ProviderTranslateRequest(patternsText, LanguageCodes.English, LanguageCodes.ChineseSimplified);
        var rendered = _renderer.Render(BuiltInPrompts.Default, request);

        rendered.User.ShouldBe(patternsText);
    }

    [Fact]
    public void Render_TextWithLineBreaksAndEmoji_PreservedVerbatim()
    {
        const string complexText = "Line 1\nLine 2\r\nLine 3 \U0001F600\U0001F468\u200D\U0001F469\u200D\U0001F467";
        var request = new ProviderTranslateRequest(complexText, LanguageCodes.English, LanguageCodes.ChineseSimplified);
        var rendered = _renderer.Render(BuiltInPrompts.Default, request);

        rendered.User.ShouldBe(complexText);
    }
}
