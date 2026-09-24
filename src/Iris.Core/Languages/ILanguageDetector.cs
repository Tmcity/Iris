namespace Iris.Core.Languages;

/// <summary>
/// 本地轻量语言检测（无网络）。实现要求：基于 Unicode 脚本统计 —— 含假名→ja；含谚文→ko；
/// 汉字为主→zh-Hans；西里尔→ru；泰文→th；阿拉伯文→ar；拉丁字母为主时用英文停用词比例判定 en，
/// 否则返回 Auto。空白文本返回 Auto / 0。
/// </summary>
public interface ILanguageDetector
{
    LanguageDetectionResult Detect(string text);
}

/// <summary>
/// 目标语言决策规则：
/// 1) Source = requestedSource != Auto ? requestedSource : Detect(text).Code；
/// 2) requestedTarget 非空则直接使用；
/// 3) 否则若 AutoSwapTarget 且 Source 与 PrimaryTargetLanguage 同族 → SecondaryTargetLanguage；
/// 4) 否则 → PrimaryTargetLanguage。
/// </summary>
public interface ITargetLanguageResolver
{
    ResolvedLanguagePair Resolve(string text, string requestedSource, string? requestedTarget);
}
