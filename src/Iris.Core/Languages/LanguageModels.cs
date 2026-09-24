namespace Iris.Core.Languages;

/// <param name="Code">LanguageCodes 中的值。</param>
/// <param name="EnglishName">渲染到 Prompt 中的名称，如 "Simplified Chinese"。</param>
/// <param name="NativeName">UI 展示名，如 "简体中文"。</param>
public sealed record LanguageInfo(string Code, string EnglishName, string NativeName);

/// <param name="Code">检测结果；无法判定时为 LanguageCodes.Auto。</param>
/// <param name="Confidence">0~1。</param>
public sealed record LanguageDetectionResult(string Code, double Confidence);

/// <param name="Source">发给 Provider 的源语言；可能为 Auto（交给模型自行识别）。</param>
/// <param name="Target">最终目标语言，永不为 Auto。</param>
/// <param name="SourceWasDetected">Source 是否来自自动检测。</param>
public sealed record ResolvedLanguagePair(string Source, string Target, bool SourceWasDetected);
