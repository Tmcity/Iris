using Iris.Core.Common;
using Iris.Core.Translation;

namespace Iris.Core.Storage;

public sealed record HistoryResult(string ProviderId, string ProviderName, string? Model, string Text);

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

/// <param name="Keyword">null/空 = 不过滤；长度 ≥ 3 用 FTS5 trigram，否则用 LIKE。</param>
public sealed record HistoryQuery(string? Keyword = null, bool FavoritesOnly = false, int Offset = 0, int Limit = 50);

/// <summary>
/// 翻译历史（Infrastructure 实现，SQLite，DDL 见 specs/DB_SCHEMA.sql）。
/// AddAsync 去重规则：若最新一条记录的 text_hash 与 target_lang 均相同，则更新其结果与时间而非插入。
/// AddAsync 后自动按 HistorySettings.MaxEntries 修剪（收藏不修剪）。
/// </summary>
public interface ITranslationHistoryRepository
{
    Task<long> AddAsync(HistoryEntry entry, CancellationToken cancellationToken = default);

    Task<PagedResult<HistoryEntry>> QueryAsync(HistoryQuery query, CancellationToken cancellationToken = default);

    Task SetFavoriteAsync(long id, bool isFavorite, CancellationToken cancellationToken = default);

    Task DeleteAsync(long id, CancellationToken cancellationToken = default);

    Task ClearAsync(bool keepFavorites, CancellationToken cancellationToken = default);
}

/// <summary>启动时执行：创建/升级数据库（PRAGMA user_version）。</summary>
public interface IDatabaseInitializer
{
    Task InitializeAsync(CancellationToken cancellationToken = default);
}
