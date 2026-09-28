using Iris.Core.Languages;
using Iris.Core.Settings;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Iris.Core.Tests.Languages;

public sealed class TargetLanguageResolverTests
{
    private readonly ISettingsService _settingsService = Substitute.For<ISettingsService>();
    private readonly ILanguageDetector _detector = Substitute.For<ILanguageDetector>();
    private readonly ILanguageCatalog _catalog = new LanguageCatalog();
    private readonly TargetLanguageResolver _resolver;

    public TargetLanguageResolverTests()
    {
        var settings = new AppSettings
        {
            Translation = new TranslationSettings
            {
                PrimaryTargetLanguage = LanguageCodes.ChineseSimplified,
                SecondaryTargetLanguage = LanguageCodes.English,
                AutoSwapTarget = true,
            },
        };
        _settingsService.Current.Returns(settings);
        _resolver = new TargetLanguageResolver(_settingsService, _detector, _catalog);
    }

    [Fact]
    public void Resolve_ExplicitSource_DoesNotCallDetect_AndSourceWasDetectedIsFalse()
    {
        var result = _resolver.Resolve("Hello world", LanguageCodes.Japanese, null);

        _detector.DidNotReceive().Detect(Arg.Any<string>());
        result.Source.ShouldBe(LanguageCodes.Japanese);
        result.SourceWasDetected.ShouldBeFalse();
        result.Target.ShouldNotBe(LanguageCodes.Auto);
    }

    [Theory]
    [InlineData("auto")]
    [InlineData("AUTO")]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Resolve_AutoOrEmptySource_CallsDetect_AndSourceWasDetectedIsTrue(string? source)
    {
        _detector.Detect("Some text").Returns(new LanguageDetectionResult(LanguageCodes.Japanese, 1.0));

        var result = _resolver.Resolve("Some text", source!, null);

        _detector.Received(1).Detect("Some text");
        result.Source.ShouldBe(LanguageCodes.Japanese);
        result.SourceWasDetected.ShouldBeTrue();
        result.Target.ShouldNotBe(LanguageCodes.Auto);
    }

    [Fact]
    public void Resolve_ExplicitTarget_NormalizesKnownAndPreservesUnknown()
    {
        // "ZH-HANS" 规范化为 "zh-Hans"
        var result1 = _resolver.Resolve("Hello", LanguageCodes.English, "ZH-HANS");
        result1.Target.ShouldBe(LanguageCodes.ChineseSimplified);
        result1.Target.ShouldNotBe(LanguageCodes.Auto);

        // "pl" 原样保留（去除首尾空白）
        var result2 = _resolver.Resolve("Hello", LanguageCodes.English, " pl ");
        result2.Target.ShouldBe("pl");
        result2.Target.ShouldNotBe(LanguageCodes.Auto);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("auto")]
    [InlineData("AUTO")]
    public void Resolve_AutoOrEmptyTarget_TreatedAsUnspecified(string? target)
    {
        _detector.Detect("Hello").Returns(new LanguageDetectionResult(LanguageCodes.English, 0.6));

        var result = _resolver.Resolve("Hello", LanguageCodes.Auto, target);

        // 默认 Primary 为 zh-Hans，因为 Source(en) != Primary(zh-Hans)，不触发交换，因此 Target 为 zh-Hans
        result.Target.ShouldBe(LanguageCodes.ChineseSimplified);
        result.Target.ShouldNotBe(LanguageCodes.Auto);
    }

    [Fact]
    public void Resolve_SourceMatchesPrimary_AutoSwapTargetTrue_SwapsToSecondary()
    {
        // 检测为 zh-Hans、主目标 zh-Hans、AutoSwapTarget = true → 次目标 en
        _detector.Detect("你好").Returns(new LanguageDetectionResult(LanguageCodes.ChineseSimplified, 1.0));

        var result = _resolver.Resolve("你好", LanguageCodes.Auto, null);

        result.Source.ShouldBe(LanguageCodes.ChineseSimplified);
        result.Target.ShouldBe(LanguageCodes.English);
        result.Target.ShouldNotBe(LanguageCodes.Auto);
    }

    [Fact]
    public void Resolve_SourceSameFamilyAsPrimary_AutoSwapTargetTrue_SwapsToSecondary()
    {
        // 检测为 zh-Hant（与 zh-Hans 同族）→ 同样切换为 en
        _detector.Detect("繁體").Returns(new LanguageDetectionResult(LanguageCodes.ChineseTraditional, 1.0));

        var result = _resolver.Resolve("繁體", LanguageCodes.Auto, null);

        result.Source.ShouldBe(LanguageCodes.ChineseTraditional);
        result.Target.ShouldBe(LanguageCodes.English);
        result.Target.ShouldNotBe(LanguageCodes.Auto);
    }

    [Fact]
    public void Resolve_SourceMatchesPrimary_AutoSwapTargetFalse_UsesPrimary()
    {
        var settings = new AppSettings
        {
            Translation = new TranslationSettings
            {
                PrimaryTargetLanguage = LanguageCodes.ChineseSimplified,
                SecondaryTargetLanguage = LanguageCodes.English,
                AutoSwapTarget = false,
            },
        };
        _settingsService.Current.Returns(settings);

        _detector.Detect("你好").Returns(new LanguageDetectionResult(LanguageCodes.ChineseSimplified, 1.0));

        var result = _resolver.Resolve("你好", LanguageCodes.Auto, null);

        result.Source.ShouldBe(LanguageCodes.ChineseSimplified);
        result.Target.ShouldBe(LanguageCodes.ChineseSimplified);
        result.Target.ShouldNotBe(LanguageCodes.Auto);
    }

    [Fact]
    public void Resolve_DetectedAuto_UsesPrimary_AndSourceIsAuto()
    {
        _detector.Detect("12345").Returns(new LanguageDetectionResult(LanguageCodes.Auto, 0.0));

        var result = _resolver.Resolve("12345", LanguageCodes.Auto, null);

        result.Source.ShouldBe(LanguageCodes.Auto);
        result.Target.ShouldBe(LanguageCodes.ChineseSimplified);
        result.SourceWasDetected.ShouldBeTrue();
        result.Target.ShouldNotBe(LanguageCodes.Auto);
    }

    [Fact]
    public void Resolve_NoSettingsCaching_ReflectsUpdatedSettings()
    {
        _detector.Detect("Test").Returns(new LanguageDetectionResult(LanguageCodes.Japanese, 1.0));

        // 第一次调用：Primary 为 zh-Hans
        var result1 = _resolver.Resolve("Test", LanguageCodes.Auto, null);
        result1.Target.ShouldBe(LanguageCodes.ChineseSimplified);

        // 修改设置替身返回值：Primary 改为 fr
        var newSettings = new AppSettings
        {
            Translation = new TranslationSettings
            {
                PrimaryTargetLanguage = LanguageCodes.French,
                SecondaryTargetLanguage = LanguageCodes.English,
                AutoSwapTarget = true,
            },
        };
        _settingsService.Current.Returns(newSettings);

        // 第二次调用：应当立即反映新设置，Target 为 fr
        var result2 = _resolver.Resolve("Test", LanguageCodes.Auto, null);
        result2.Target.ShouldBe(LanguageCodes.French);
        result2.Target.ShouldNotBe(LanguageCodes.Auto);
    }

    [Fact]
    public void Resolve_ExplicitSettings_UsesProvidedSettings_AndDoesNotReadSettingsService()
    {
        _detector.Detect("Test").Returns(new LanguageDetectionResult(LanguageCodes.English, 1.0));

        var settingsA = new TranslationSettings
        {
            PrimaryTargetLanguage = LanguageCodes.Japanese,
            SecondaryTargetLanguage = LanguageCodes.Korean,
            AutoSwapTarget = false,
        };

        // 令 _settingsService.Current 抛出异常，证明显式重载绝不访问全局设置服务
        _settingsService.Current.Returns(_ => throw new InvalidOperationException("Should not access SettingsService.Current"));

        var result = _resolver.Resolve("Test", LanguageCodes.Auto, null, settingsA);
        result.Target.ShouldBe(LanguageCodes.Japanese);
    }

    [Fact]
    public void Resolve_ExplicitSettings_Null_ThrowsArgumentNullException()
    {
        Should.Throw<ArgumentNullException>(() => _resolver.Resolve("Test", LanguageCodes.Auto, null, null!));
    }
}
