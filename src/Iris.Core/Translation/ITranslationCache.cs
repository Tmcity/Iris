using System;
using System.Diagnostics.CodeAnalysis;

namespace Iris.Core.Translation;

/// <summary>进程内缓存键；InstanceId 隔离配置/凭据代次，RequestFingerprint 是实际请求的完整 SHA-256。
/// 精确编码见 TRANSLATION_EXECUTION.md；不保存原文、Prompt、地址或凭据。</summary>
public readonly record struct TranslationCacheKey(string ProviderId, Guid InstanceId, string RequestFingerprint);

/// <summary>缓存记账预算，不等于整个进程或 CLR 实际分配量。</summary>
public static class TranslationCacheLimits
{
    public const long MaxTotalBytes = 16L * 1024 * 1024;
    public const long MaxEntryBytes = 1L * 1024 * 1024;
    public const long EntryOverheadBytes = 256;
}

/// <summary>进程内线程安全 LRU，受当前 CacheEnabled/CacheCapacity 与固定字节预算共同限制。
/// 不持久化；编排器只写完整成功结果。Dispose 幂等并取消订阅，之后操作抛 ObjectDisposedException。</summary>
public interface ITranslationCache : IDisposable
{
    /// <summary>当前清空代次，初始为 0；调用方开始请求时捕获，迟到写入必须携带原值。</summary>
    long Generation { get; }

    /// <summary>命中移至 MRU；未命中返回 false 且 text 为 null。</summary>
    bool TryGet(in TranslationCacheKey key, [NotNullWhen(true)] out string? text);

    /// <summary>原子校验 generation 并尝试写入；过期、禁用或超预算返回 false；null text 抛 ArgumentNullException。
    /// 准入/替换/淘汰规则见 TRANSLATION_EXECUTION.md，不得保留完整请求作为键。</summary>
    bool TrySet(in TranslationCacheKey key, string text, long generation);

    /// <summary>原子清空并递增 Generation，旧请求不能重新填入。</summary>
    void Clear();
}
