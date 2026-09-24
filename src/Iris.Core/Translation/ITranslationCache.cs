using System.Diagnostics.CodeAnalysis;

namespace Iris.Core.Translation;

/// <param name="PromptFingerprint">模板 System+User 的 SHA-256（前 16 个十六进制字符），模板变化即缓存失效。</param>
/// <param name="TextSha256">预处理后文本的 SHA-256 十六进制。</param>
public readonly record struct TranslationCacheKey(
    string ProviderId,
    string? Model,
    string PromptFingerprint,
    string SourceLanguage,
    string TargetLanguage,
    string TextSha256);

/// <summary>进程内 LRU 缓存，容量 = Settings.Translation.CacheCapacity；线程安全；不持久化；截断结果不入缓存。</summary>
public interface ITranslationCache
{
    bool TryGet(in TranslationCacheKey key, [NotNullWhen(true)] out string? text);

    void Set(in TranslationCacheKey key, string text);

    void Clear();
}
