using Iris.Core.Common;
using Iris.Core.Translation;

namespace Iris.Core.Storage;

public sealed record HistoryResult(string ProviderId, string ProviderName, string? Model, string Text)
{
    /// <summary>该结果因输出上限截断；历史展示和复制时不得当作完整译文。</summary>
    public bool Truncated { get; init; }
}

public sealed record HistoryEntry
{
    /// <summary>新建时为 0，由仓储分配。</summary>
    public long Id { get; init; }

    public DateTimeOffset CreatedAt { get; init; }
    public TextOrigin Origin { get; init; }
    public required string SourceText { get; init; }
    public required string SourceLanguage { get; init; }
    public required string TargetLanguage { get; init; }
    public bool IsFavorite { get; init; }
    public IReadOnlyList<HistoryResult> Results { get; init; } = [];
}

/// <summary>历史列表摘要，不包含完整原文/译文；展开、复制或重译前使用 GetAsync 取得详情。</summary>
public sealed record HistorySummary(
    long Id,
    DateTimeOffset CreatedAt,
    TextOrigin Origin,
    string SourcePreview,
    string SourceLanguage,
    string TargetLanguage,
    bool IsFavorite,
    int ResultCount,
    bool HasTruncatedResults);

/// <param name="Keyword">null/空白 = 不过滤；Trim 后按字面量子串搜索，Unicode 标量数 >= 3 用 FTS5，否则用转义 LIKE；NUL 非法。</param>
/// <param name="Offset">非负偏移；结果按 created_at DESC,id DESC 排序。</param>
/// <param name="Limit">每页 1..200 条，默认 50；只返回摘要。</param>
public sealed record HistoryQuery(string? Keyword = null, bool FavoritesOnly = false, int Offset = 0, int Limit = 50);

/// <summary>
/// 翻译历史（Infrastructure，SQLite），规则见 HISTORY_STORAGE.md，DDL 见 DB_SCHEMA.sql。
/// AddAsync 在调用边界捕获数据；只对最新且未收藏、原文与语言对完全相同的记录去重，返回实际 Id。
/// 结果/索引/修剪同事务，收藏不被隐式替换；MaxEntries 限制非收藏条数，收藏额外保留。
/// 所有数据库 I/O 在后台执行；提交前取消回滚，提交后不伪报取消；存储异常安全映射为 StorageFailure。
/// </summary>
public interface ITranslationHistoryRepository
{
    Task<long> AddAsync(HistoryEntry entry, CancellationToken cancellationToken = default);

    /// <summary>同一读快照返回 Total 和摘要页；SourcePreview 最多 200 个 Unicode 标量，超出追加省略号。</summary>
    Task<PagedResult<HistorySummary>> QueryAsync(HistoryQuery query, CancellationToken cancellationToken = default);

    /// <summary>取得完整详情与有序结果，正 id 不存在时返回 null；不使用摘要代替全文。</summary>
    Task<HistoryEntry?> GetAsync(long id, CancellationToken cancellationToken = default);

    Task SetFavoriteAsync(long id, bool isFavorite, CancellationToken cancellationToken = default);

    Task DeleteAsync(long id, CancellationToken cancellationToken = default);

    Task ClearAsync(bool keepFavorites, CancellationToken cancellationToken = default);
}

/// <summary>后台创建/顺序迁移历史库（PRAGMA user_version），幂等且并发安全；失败可重试，未来版本/损坏库不覆盖。</summary>
public interface IDatabaseInitializer
{
    Task InitializeAsync(CancellationToken cancellationToken = default);
}
