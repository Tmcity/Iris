using Iris.Core.Languages;
using Shouldly;
using Xunit;

namespace Iris.Core.Tests.Languages;

public sealed class ScriptLanguageDetectorTests
{
    private readonly ScriptLanguageDetector _detector = new();

    [Theory]
    // 空 / 无字母
    [InlineData("", LanguageCodes.Auto)]
    [InlineData("   ", LanguageCodes.Auto)]
    [InlineData("12345", LanguageCodes.Auto)]
    [InlineData("!!!???", LanguageCodes.Auto)]
    [InlineData("😀😀😀", LanguageCodes.Auto)]
    // 中文
    [InlineData("你好，世界", LanguageCodes.ChineseSimplified)]
    [InlineData("今天天气很好，我们去公园吧。", LanguageCodes.ChineseSimplified)]
    [InlineData("我喜欢 Python 编程", LanguageCodes.ChineseSimplified)]
    [InlineData("這是繁體中文", LanguageCodes.ChineseSimplified)]
    // CJK 扩展 B（代理对）
    [InlineData("\U00020000\U00020001\U00020002", LanguageCodes.ChineseSimplified)]
    // 日文
    [InlineData("東京都に住んでいます", LanguageCodes.Japanese)]
    [InlineData("ありがとう", LanguageCodes.Japanese)]
    [InlineData("カタカナ", LanguageCodes.Japanese)]
    [InlineData("ｶﾀｶﾅ", LanguageCodes.Japanese)]
    [InlineData("さようなら", LanguageCodes.Japanese)]
    // 韩文
    [InlineData("안녕하세요", LanguageCodes.Korean)]
    [InlineData("감사합니다", LanguageCodes.Korean)]
    // 俄文 / 泰文 / 阿拉伯文
    [InlineData("Привет, как дела?", LanguageCodes.Russian)]
    [InlineData("Что это такое?", LanguageCodes.Russian)]
    [InlineData("สวัสดีครับ", LanguageCodes.Thai)]
    [InlineData("مرحبا بك", LanguageCodes.Arabic)]
    // 英文句子
    [InlineData("The quick brown fox jumps over the lazy dog", LanguageCodes.English)]
    [InlineData("This is a pen.", LanguageCodes.English)]
    [InlineData("How are you doing today?", LanguageCodes.English)]
    // 英文短语
    [InlineData("Hello", LanguageCodes.English)]
    [InlineData("Hello world", LanguageCodes.English)]
    [InlineData("Good morning", LanguageCodes.English)]
    [InlineData("don't", LanguageCodes.English)]
    [InlineData("e-mail", LanguageCodes.English)]
    [InlineData("Bonjour", LanguageCodes.English)]
    // 其他拉丁语言
    [InlineData("Je suis très heureux de vous voir", LanguageCodes.Auto)]
    [InlineData("Ich bin ein Berliner und das ist gut", LanguageCodes.Auto)]
    [InlineData("Tôi yêu Việt Nam", LanguageCodes.Auto)]
    [InlineData("Café au lait", LanguageCodes.Auto)]
    [InlineData("C'est la vie", LanguageCodes.Auto)]
    // 边界情况
    [InlineData("Ｈｅｌｌｏ", LanguageCodes.Auto)]
    [InlineData("Hello 世界", LanguageCodes.Auto)]
    public void Detect_ReturnsExpectedLanguageCode(string text, string expectedCode)
    {
        var result = _detector.Detect(text);
        result.Code.ShouldBe(expectedCode);
    }

    [Fact]
    public void Detect_EmptyOrNoLetters_ReturnsConfidenceZero()
    {
        _detector.Detect("").Confidence.ShouldBe(0.0);
        _detector.Detect("   ").Confidence.ShouldBe(0.0);
        _detector.Detect("12345").Confidence.ShouldBe(0.0);
        _detector.Detect("!!!???").Confidence.ShouldBe(0.0);
        _detector.Detect("😀😀😀").Confidence.ShouldBe(0.0);
    }

    [Fact]
    public void Detect_EnglishShortPhrases_ReturnsConfidencePointSix()
    {
        _detector.Detect("Hello").Confidence.ShouldBe(0.6);
        _detector.Detect("Hello world").Confidence.ShouldBe(0.6);
        _detector.Detect("don't").Confidence.ShouldBe(0.6);
        _detector.Detect("e-mail").Confidence.ShouldBe(0.6);
        _detector.Detect("Bonjour").Confidence.ShouldBe(0.6);
    }

    [Fact]
    public void Detect_SpecificConfidenceValues_MatchExpectations()
    {
        // "你好" 全为汉字，L=2, Han=2, 置信度 1.0
        var chinese = _detector.Detect("你好");
        chinese.Code.ShouldBe(LanguageCodes.ChineseSimplified);
        chinese.Confidence.ShouldBe(1.0);

        // "Hello" W=1, 英文短语，置信度 0.6
        var english = _detector.Detect("Hello");
        english.Code.ShouldBe(LanguageCodes.English);
        english.Confidence.ShouldBe(0.6);

        // "東京都に住んでいます" 假名 6 + 汉字 4，L=10，置信度为假名与汉字比例之和 1.0
        var japanese = _detector.Detect("東京都に住んでいます");
        japanese.Code.ShouldBe(LanguageCodes.Japanese);
        japanese.Confidence.ShouldBe(1.0);
    }

    [Fact]
    public void Threshold_Han_BoundaryCases()
    {
        // 汉字 3 + 拉丁 7（new string('中', 3) + new string('x', 7)，比例 0.30）→ zh-Hans
        var text30 = new string('中', 3) + new string('x', 7);
        var result30 = _detector.Detect(text30);
        result30.Code.ShouldBe(LanguageCodes.ChineseSimplified);
        result30.Confidence.ShouldBe(0.30);

        // 汉字 29 + 拉丁 71（new string('中', 29) + new string('x', 71)，比例 0.29）→ auto
        var text29 = new string('中', 29) + new string('x', 71);
        var result29 = _detector.Detect(text29);
        result29.Code.ShouldBe(LanguageCodes.Auto);
        result29.Confidence.ShouldBe(0.0);
    }

    [Fact]
    public void Threshold_Kana_BoundaryCases()
    {
        // 假名 1 + 汉字 19（"あ" + new string('中', 19)，比例 1/20 = 0.05）→ ja
        var textKana05 = "あ" + new string('中', 19);
        var result05 = _detector.Detect(textKana05);
        result05.Code.ShouldBe(LanguageCodes.Japanese);
        result05.Confidence.ShouldBe(1.0); // 假名 1 + 汉字 19 = 20/20 = 1.0

        // 假名 1 + 汉字 20（"あ" + new string('中', 20)，比例 1/21 ≈ 0.0476 < 0.05，汉字 20/21 ≈ 0.952 >= 0.30）→ zh-Hans
        var textKana047 = "あ" + new string('中', 20);
        var result047 = _detector.Detect(textKana047);
        result047.Code.ShouldBe(LanguageCodes.ChineseSimplified);
    }

    [Fact]
    public void Threshold_Hangul_BoundaryCases()
    {
        // 谚文 3 + 拉丁 7（new string('가', 3) + new string('x', 7)，比例 3/10 = 0.30）→ ko
        var textKo30 = new string('가', 3) + new string('x', 7);
        var resultKo30 = _detector.Detect(textKo30);
        resultKo30.Code.ShouldBe(LanguageCodes.Korean);
        resultKo30.Confidence.ShouldBe(0.30);

        // 谚文 3 + 拉丁 8（new string('가', 3) + new string('x', 8)，比例 3/11 ≈ 0.2727 < 0.30，且拉丁 8/11 ≈ 0.7272 >= 0.70 但 x 不是英语）→ auto
        var textKo27 = new string('가', 3) + new string('x', 8);
        var resultKo27 = _detector.Detect(textKo27);
        resultKo27.Code.ShouldBe(LanguageCodes.Auto);
        resultKo27.Confidence.ShouldBe(0.0);
    }

    [Fact]
    public void Threshold_Cyrillic_BoundaryCases()
    {
        // 西里尔 5 + 拉丁 5（new string('ж', 5) + new string('x', 5)，比例 5/10 = 0.50）→ ru
        var textRu50 = new string('ж', 5) + new string('x', 5);
        var resultRu50 = _detector.Detect(textRu50);
        resultRu50.Code.ShouldBe(LanguageCodes.Russian);
        resultRu50.Confidence.ShouldBe(0.50);

        // 西里尔 5 + 拉丁 6（new string('ж', 5) + new string('x', 6)，比例 5/11 ≈ 0.4545 < 0.50）→ auto
        var textRu45 = new string('ж', 5) + new string('x', 6);
        var resultRu45 = _detector.Detect(textRu45);
        resultRu45.Code.ShouldBe(LanguageCodes.Auto);
        resultRu45.Confidence.ShouldBe(0.0);
    }
}
