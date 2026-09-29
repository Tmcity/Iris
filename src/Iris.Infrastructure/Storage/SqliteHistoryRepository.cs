using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Iris.Core.Common;
using Iris.Core.Errors;
using Iris.Core.Settings;
using Iris.Core.Storage;
using Iris.Core.Translation;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Iris.Infrastructure.Storage;

internal sealed partial class SqliteHistoryRepository : ITranslationHistoryRepository, IDisposable
{
    private readonly ISqliteConnectionFactory _connectionFactory;
    private readonly IDatabaseInitializer _databaseInitializer;
    private readonly ISettingsService _settingsService;
    private readonly ILogger<SqliteHistoryRepository> _logger;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private bool _disposed;

    internal Action? OnReadSnapshotAcquired { get; set; }

    public SqliteHistoryRepository(
        ISqliteConnectionFactory connectionFactory,
        IDatabaseInitializer databaseInitializer,
        ISettingsService settingsService,
        ILogger<SqliteHistoryRepository> logger)
    {
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
        _databaseInitializer = databaseInitializer ?? throw new ArgumentNullException(nameof(databaseInitializer));
        _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<long> AddAsync(HistoryEntry entry, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(entry);

        if (string.IsNullOrWhiteSpace(entry.SourceText))
        {
            throw new ArgumentException("SourceText cannot be empty or whitespace.", nameof(entry));
        }

        if (string.IsNullOrWhiteSpace(entry.SourceLanguage))
        {
            throw new ArgumentException("SourceLanguage cannot be empty or whitespace.", nameof(entry));
        }

        if (string.IsNullOrWhiteSpace(entry.TargetLanguage))
        {
            throw new ArgumentException("TargetLanguage cannot be empty or whitespace.", nameof(entry));
        }

        if (entry.Results == null)
        {
            throw new ArgumentException("Results cannot be null.", nameof(entry));
        }

        // 调用边界防御性复制，固定不可变记录值
        var copiedResults = new List<HistoryResult>(entry.Results.Count);
        var seenProviderIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var r in entry.Results)
        {
            if (r == null)
            {
                throw new ArgumentException("Result element cannot be null.", nameof(entry));
            }

            if (string.IsNullOrWhiteSpace(r.ProviderId))
            {
                throw new ArgumentException("ProviderId cannot be empty or whitespace.", nameof(entry));
            }

            if (string.IsNullOrWhiteSpace(r.ProviderName))
            {
                throw new ArgumentException("ProviderName cannot be empty or whitespace.", nameof(entry));
            }

            if (r.Text == null)
            {
                throw new ArgumentException("Result text cannot be null.", nameof(entry));
            }

            if (!seenProviderIds.Add(r.ProviderId))
            {
                throw new ArgumentException("Duplicate ProviderId in results.", nameof(entry));
            }

            copiedResults.Add(new HistoryResult(r.ProviderId, r.ProviderName, r.Model, r.Text)
            {
                Truncated = r.Truncated
            });
        }

        var sourceText = entry.SourceText;
        var sourceLang = entry.SourceLanguage;
        var targetLang = entry.TargetLanguage;
        var origin = entry.Origin.ToString();
        var createdAt = entry.CreatedAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
        var isFavorite = entry.IsFavorite ? 1 : 0;

        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(sourceText));
        var textHash = Convert.ToHexStringLower(hashBytes);
        var concatenatedResults = string.Join('\n', copiedResults.Select(r => r.Text));

        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        return await Task.Run(
            () => AddCore(
                sourceText,
                sourceLang,
                targetLang,
                origin,
                createdAt,
                isFavorite,
                textHash,
                concatenatedResults,
                copiedResults,
                cancellationToken),
            cancellationToken).ConfigureAwait(false);
    }

    private long AddCore(
        string sourceText,
        string sourceLang,
        string targetLang,
        string origin,
        string createdAt,
        int isFavorite,
        string textHash,
        string concatenatedResults,
        IReadOnlyList<HistoryResult> results,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        _writeLock.Wait(cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            // 在取得写门闩后读取最新设置上限，确保排队写入能够观察到最新配置
            var maxEntries = _settingsService.Current.History.MaxEntries;

            SqliteConnection? connection = null;
            SqliteTransaction? tx = null;
            try
            {
                connection = _connectionFactory.CreateConnection();
                tx = connection.BeginTransaction(deferred: false);

                // 查询最新一条记录
                const string queryLatestSql = """
                    SELECT id, created_at, origin, source_text, source_lang, target_lang, text_hash, is_favorite
                    FROM translation_history
                    ORDER BY created_at DESC, id DESC
                    LIMIT 1;
                    """;

                var latest = connection.QueryFirstOrDefault<HistoryRowDto>(queryLatestSql, transaction: tx);

                bool shouldDeduplicate = latest != null
                    && latest.is_favorite == 0
                    && string.Equals(latest.text_hash, textHash, StringComparison.Ordinal)
                    && string.Equals(latest.source_text, sourceText, StringComparison.Ordinal)
                    && string.Equals(latest.source_lang, sourceLang, StringComparison.Ordinal)
                    && string.Equals(latest.target_lang, targetLang, StringComparison.Ordinal);

                long effectiveId;

                if (shouldDeduplicate)
                {
                    effectiveId = latest!.id;

                    // 更新主记录
                    const string updateSql = """
                        UPDATE translation_history
                        SET created_at = @createdAt, origin = @origin, is_favorite = @isFavorite
                        WHERE id = @id;
                        """;
                    connection.Execute(updateSql, new { createdAt, origin, isFavorite, id = effectiveId }, transaction: tx);

                    // 删除旧结果并重新插入
                    connection.Execute("DELETE FROM translation_result WHERE history_id = @id;", new { id = effectiveId }, transaction: tx);

                    InsertResults(connection, tx, effectiveId, results);

                    // 重建 FTS 索引
                    connection.Execute("DELETE FROM history_fts WHERE rowid = @id;", new { id = effectiveId }, transaction: tx);
                    connection.Execute(
                        "INSERT INTO history_fts (rowid, source_text, result_text) VALUES (@id, @sourceText, @concatenatedResults);",
                        new { id = effectiveId, sourceText, concatenatedResults },
                        transaction: tx);
                }
                else
                {
                    // 插入新记录
                    const string insertSql = """
                        INSERT INTO translation_history (created_at, origin, source_text, source_lang, target_lang, text_hash, is_favorite)
                        VALUES (@createdAt, @origin, @sourceText, @sourceLang, @targetLang, @textHash, @isFavorite);
                        SELECT last_insert_rowid();
                        """;

                    effectiveId = connection.ExecuteScalar<long>(
                        insertSql,
                        new { createdAt, origin, sourceText, sourceLang, targetLang, textHash, isFavorite },
                        transaction: tx);

                    InsertResults(connection, tx, effectiveId, results);

                    connection.Execute(
                        "INSERT INTO history_fts (rowid, source_text, result_text) VALUES (@id, @sourceText, @concatenatedResults);",
                        new { id = effectiveId, sourceText, concatenatedResults },
                        transaction: tx);
                }

                // 修剪非收藏记录
                TrimNonFavorites(connection, tx, maxEntries);

                cancellationToken.ThrowIfCancellationRequested();
                tx.Commit();

                return effectiveId;
            }
            catch (OperationCanceledException)
            {
                try { tx?.Rollback(); } catch { }
                throw;
            }
            catch (IrisException)
            {
                try { tx?.Rollback(); } catch { }
                throw;
            }
            catch (SqliteException ex)
            {
                try { tx?.Rollback(); } catch { }
                LogSqliteOperationFailed(_logger, "AddAsync", ex.SqliteErrorCode);
                throw new IrisException(ErrorCode.StorageFailure, "Database write failed with SQLite error.");
            }
            catch (Exception)
            {
                try { tx?.Rollback(); } catch { }
                LogOperationFailed(_logger, "AddAsync");
                throw new IrisException(ErrorCode.StorageFailure, "Database write failed.");
            }
            finally
            {
                tx?.Dispose();
                connection?.Dispose();
            }
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private static void InsertResults(
        SqliteConnection connection,
        SqliteTransaction tx,
        long historyId,
        IReadOnlyList<HistoryResult> results)
    {
        const string insertResultSql = """
            INSERT INTO translation_result (history_id, sort_order, provider_id, provider_name, model, result_text, is_truncated)
            VALUES (@historyId, @sortOrder, @providerId, @providerName, @model, @resultText, @isTruncated);
            """;

        for (var i = 0; i < results.Count; i++)
        {
            var r = results[i];
            connection.Execute(
                insertResultSql,
                new
                {
                    historyId,
                    sortOrder = i,
                    providerId = r.ProviderId,
                    providerName = r.ProviderName,
                    model = r.Model,
                    resultText = r.Text,
                    isTruncated = r.Truncated ? 1 : 0
                },
                transaction: tx);
        }
    }

    public async Task<PagedResult<HistorySummary>> QueryAsync(HistoryQuery query, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(query);

        if (query.Offset < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(query), "Offset must be non-negative.");
        }

        if (query.Limit < 1 || query.Limit > 200)
        {
            throw new ArgumentOutOfRangeException(nameof(query), "Limit must be between 1 and 200.");
        }

        if (query.Keyword != null && query.Keyword.Contains('\0'))
        {
            throw new ArgumentException("Keyword cannot contain NUL characters.", nameof(query));
        }

        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        return await Task.Run(() => QueryCore(query, cancellationToken), cancellationToken).ConfigureAwait(false);
    }

    private PagedResult<HistorySummary> QueryCore(HistoryQuery query, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        SqliteConnection? connection = null;
        SqliteTransaction? tx = null;
        try
        {
            connection = _connectionFactory.CreateConnection();
            // 使用显式延迟读事务（deferred: true），在 WAL 模式下仅获取共享读锁，绝不阻塞并发写事务
            tx = connection.BeginTransaction(deferred: true);

            cancellationToken.ThrowIfCancellationRequested();

            var favoritesOnly = query.FavoritesOnly ? 1 : 0;
            var hasKeyword = !string.IsNullOrWhiteSpace(query.Keyword);

            int totalCount;
            List<SummaryRowDto> rows;

            if (!hasKeyword)
            {
                const string countSql = """
                    SELECT COUNT(*)
                    FROM translation_history h
                    WHERE (@favoritesOnly = 0 OR h.is_favorite = 1);
                    """;

                totalCount = connection.ExecuteScalar<int>(countSql, new { favoritesOnly }, transaction: tx);
                OnReadSnapshotAcquired?.Invoke();

                const string pageSql = """
                    SELECT
                        h.id,
                        h.created_at,
                        h.origin,
                        substr(h.source_text, 1, 201) AS raw_preview,
                        h.source_lang,
                        h.target_lang,
                        h.is_favorite,
                        (SELECT COUNT(*) FROM translation_result r WHERE r.history_id = h.id) AS result_count,
                        COALESCE((SELECT MAX(r.is_truncated) FROM translation_result r WHERE r.history_id = h.id), 0) AS has_truncated_results
                    FROM translation_history h
                    WHERE (@favoritesOnly = 0 OR h.is_favorite = 1)
                    ORDER BY h.created_at DESC, h.id DESC
                    LIMIT @limit OFFSET @offset;
                    """;

                rows = connection.Query<SummaryRowDto>(
                    pageSql,
                    new { favoritesOnly, limit = query.Limit, offset = query.Offset },
                    transaction: tx).ToList();
            }
            else
            {
                var trimmed = query.Keyword!.Trim();
                var scalarCount = trimmed.EnumerateRunes().Count();

                if (scalarCount >= 3)
                {
                    var ftsMatch = "\"" + trimmed.Replace("\"", "\"\"") + "\"";

                    const string countSql = """
                        SELECT COUNT(*)
                        FROM translation_history h
                        WHERE (@favoritesOnly = 0 OR h.is_favorite = 1)
                          AND h.id IN (SELECT rowid FROM history_fts WHERE history_fts MATCH @ftsMatch);
                        """;

                    totalCount = connection.ExecuteScalar<int>(countSql, new { favoritesOnly, ftsMatch }, transaction: tx);
                    OnReadSnapshotAcquired?.Invoke();

                    const string pageSql = """
                        SELECT
                            h.id,
                            h.created_at,
                            h.origin,
                            substr(h.source_text, 1, 201) AS raw_preview,
                            h.source_lang,
                            h.target_lang,
                            h.is_favorite,
                            (SELECT COUNT(*) FROM translation_result r WHERE r.history_id = h.id) AS result_count,
                            COALESCE((SELECT MAX(r.is_truncated) FROM translation_result r WHERE r.history_id = h.id), 0) AS has_truncated_results
                        FROM translation_history h
                        WHERE (@favoritesOnly = 0 OR h.is_favorite = 1)
                          AND h.id IN (SELECT rowid FROM history_fts WHERE history_fts MATCH @ftsMatch)
                        ORDER BY h.created_at DESC, h.id DESC
                        LIMIT @limit OFFSET @offset;
                        """;

                    rows = connection.Query<SummaryRowDto>(
                        pageSql,
                        new { favoritesOnly, ftsMatch, limit = query.Limit, offset = query.Offset },
                        transaction: tx).ToList();
                }
                else
                {
                    var likePattern = EscapeLikePattern(trimmed);

                    const string countSql = """
                        SELECT COUNT(*)
                        FROM translation_history h
                        WHERE (@favoritesOnly = 0 OR h.is_favorite = 1)
                          AND (h.source_text LIKE @likePattern ESCAPE '\'
                               OR EXISTS (SELECT 1 FROM translation_result r WHERE r.history_id = h.id AND r.result_text LIKE @likePattern ESCAPE '\'));
                        """;

                    totalCount = connection.ExecuteScalar<int>(countSql, new { favoritesOnly, likePattern }, transaction: tx);
                    OnReadSnapshotAcquired?.Invoke();

                    const string pageSql = """
                        SELECT
                            h.id,
                            h.created_at,
                            h.origin,
                            substr(h.source_text, 1, 201) AS raw_preview,
                            h.source_lang,
                            h.target_lang,
                            h.is_favorite,
                            (SELECT COUNT(*) FROM translation_result r WHERE r.history_id = h.id) AS result_count,
                            COALESCE((SELECT MAX(r.is_truncated) FROM translation_result r WHERE r.history_id = h.id), 0) AS has_truncated_results
                        FROM translation_history h
                        WHERE (@favoritesOnly = 0 OR h.is_favorite = 1)
                          AND (h.source_text LIKE @likePattern ESCAPE '\'
                               OR EXISTS (SELECT 1 FROM translation_result r WHERE r.history_id = h.id AND r.result_text LIKE @likePattern ESCAPE '\'))
                        ORDER BY h.created_at DESC, h.id DESC
                        LIMIT @limit OFFSET @offset;
                        """;

                    rows = connection.Query<SummaryRowDto>(
                        pageSql,
                        new { favoritesOnly, likePattern, limit = query.Limit, offset = query.Offset },
                        transaction: tx).ToList();
                }
            }

            var summaries = new List<HistorySummary>(rows.Count);
            foreach (var r in rows)
            {
                var preview = CreateSourcePreview(r.raw_preview);
                var createdAt = DateTimeOffset.Parse(r.created_at, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
                var origin = Enum.Parse<TextOrigin>(r.origin, ignoreCase: true);

                summaries.Add(new HistorySummary(
                    Id: r.id,
                    CreatedAt: createdAt,
                    Origin: origin,
                    SourcePreview: preview,
                    SourceLanguage: r.source_lang,
                    TargetLanguage: r.target_lang,
                    IsFavorite: r.is_favorite == 1,
                    ResultCount: r.result_count,
                    HasTruncatedResults: r.has_truncated_results != 0));
            }

            cancellationToken.ThrowIfCancellationRequested();
            tx.Commit();

            return new PagedResult<HistorySummary>(summaries.AsReadOnly(), totalCount);
        }
        catch (OperationCanceledException)
        {
            try { tx?.Rollback(); } catch { }
            throw;
        }
        catch (IrisException)
        {
            try { tx?.Rollback(); } catch { }
            throw;
        }
        catch (SqliteException ex)
        {
            try { tx?.Rollback(); } catch { }
            LogSqliteOperationFailed(_logger, "QueryAsync", ex.SqliteErrorCode);
            throw new IrisException(ErrorCode.StorageFailure, "Database query failed with SQLite error.");
        }
        catch (Exception)
        {
            try { tx?.Rollback(); } catch { }
            LogOperationFailed(_logger, "QueryAsync");
            throw new IrisException(ErrorCode.StorageFailure, "Database query failed.");
        }
        finally
        {
            tx?.Dispose();
            connection?.Dispose();
        }
    }

    public async Task<HistoryEntry?> GetAsync(long id, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (id <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(id), "Id must be positive.");
        }

        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        return await Task.Run(() => GetCore(id, cancellationToken), cancellationToken).ConfigureAwait(false);
    }

    private HistoryEntry? GetCore(long id, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        SqliteConnection? connection = null;
        SqliteTransaction? tx = null;
        try
        {
            connection = _connectionFactory.CreateConnection();
            // 使用显式延迟读事务（deferred: true），在同一只读快照内获取记录头与有序子结果
            tx = connection.BeginTransaction(deferred: true);

            cancellationToken.ThrowIfCancellationRequested();

            const string historySql = """
                SELECT id, created_at, origin, source_text, source_lang, target_lang, is_favorite
                FROM translation_history
                WHERE id = @id;
                """;

            var historyRow = connection.QueryFirstOrDefault<HistoryDetailDto>(historySql, new { id }, transaction: tx);
            if (historyRow == null)
            {
                tx.Commit();
                return null;
            }

            OnReadSnapshotAcquired?.Invoke();

            const string resultsSql = """
                SELECT provider_id, provider_name, model, result_text, is_truncated
                FROM translation_result
                WHERE history_id = @id
                ORDER BY sort_order ASC, id ASC;
                """;

            var resultRows = connection.Query<ResultDetailDto>(resultsSql, new { id }, transaction: tx).ToList();
            var results = new List<HistoryResult>(resultRows.Count);
            foreach (var r in resultRows)
            {
                results.Add(new HistoryResult(r.provider_id, r.provider_name, r.model, r.result_text)
                {
                    Truncated = r.is_truncated == 1
                });
            }

            var createdAt = DateTimeOffset.Parse(historyRow.created_at, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
            var origin = Enum.Parse<TextOrigin>(historyRow.origin, ignoreCase: true);

            cancellationToken.ThrowIfCancellationRequested();
            tx.Commit();

            return new HistoryEntry
            {
                Id = historyRow.id,
                CreatedAt = createdAt,
                Origin = origin,
                SourceText = historyRow.source_text,
                SourceLanguage = historyRow.source_lang,
                TargetLanguage = historyRow.target_lang,
                IsFavorite = historyRow.is_favorite == 1,
                Results = results.AsReadOnly(),
            };
        }
        catch (OperationCanceledException)
        {
            try { tx?.Rollback(); } catch { }
            throw;
        }
        catch (IrisException)
        {
            try { tx?.Rollback(); } catch { }
            throw;
        }
        catch (SqliteException ex)
        {
            try { tx?.Rollback(); } catch { }
            LogSqliteOperationFailed(_logger, "GetAsync", ex.SqliteErrorCode);
            throw new IrisException(ErrorCode.StorageFailure, "Database read failed with SQLite error.");
        }
        catch (Exception)
        {
            try { tx?.Rollback(); } catch { }
            LogOperationFailed(_logger, "GetAsync");
            throw new IrisException(ErrorCode.StorageFailure, "Database read failed.");
        }
        finally
        {
            tx?.Dispose();
            connection?.Dispose();
        }
    }

    public async Task SetFavoriteAsync(long id, bool isFavorite, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (id <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(id), "Id must be positive.");
        }

        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        await Task.Run(() => SetFavoriteCore(id, isFavorite, cancellationToken), cancellationToken).ConfigureAwait(false);
    }

    private void SetFavoriteCore(long id, bool isFavorite, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        _writeLock.Wait(cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            var maxEntries = _settingsService.Current.History.MaxEntries;

            SqliteConnection? connection = null;
            SqliteTransaction? tx = null;
            try
            {
                connection = _connectionFactory.CreateConnection();
                tx = connection.BeginTransaction(deferred: false);

                const string sql = "UPDATE translation_history SET is_favorite = @fav WHERE id = @id;";
                var affectedRows = connection.Execute(sql, new { fav = isFavorite ? 1 : 0, id }, transaction: tx);

                // 仅当记录确实存在且由收藏变更为非收藏时，才触发容量修剪；若记录不存在，此操作为 no-op，严禁修剪其它记录
                if (!isFavorite && affectedRows > 0)
                {
                    TrimNonFavorites(connection, tx, maxEntries);
                }

                cancellationToken.ThrowIfCancellationRequested();
                tx.Commit();
            }
            catch (OperationCanceledException)
            {
                try { tx?.Rollback(); } catch { }
                throw;
            }
            catch (IrisException)
            {
                try { tx?.Rollback(); } catch { }
                throw;
            }
            catch (SqliteException ex)
            {
                try { tx?.Rollback(); } catch { }
                LogSqliteOperationFailed(_logger, "SetFavoriteAsync", ex.SqliteErrorCode);
                throw new IrisException(ErrorCode.StorageFailure, "Database update failed with SQLite error.");
            }
            catch (Exception)
            {
                try { tx?.Rollback(); } catch { }
                LogOperationFailed(_logger, "SetFavoriteAsync");
                throw new IrisException(ErrorCode.StorageFailure, "Database update failed.");
            }
            finally
            {
                tx?.Dispose();
                connection?.Dispose();
            }
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task DeleteAsync(long id, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (id <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(id), "Id must be positive.");
        }

        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        await Task.Run(() => DeleteCore(id, cancellationToken), cancellationToken).ConfigureAwait(false);
    }

    private void DeleteCore(long id, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        _writeLock.Wait(cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            SqliteConnection? connection = null;
            SqliteTransaction? tx = null;
            try
            {
                connection = _connectionFactory.CreateConnection();
                tx = connection.BeginTransaction(deferred: false);

                connection.Execute("DELETE FROM translation_history WHERE id = @id;", new { id }, transaction: tx);

                cancellationToken.ThrowIfCancellationRequested();
                tx.Commit();
            }
            catch (OperationCanceledException)
            {
                try { tx?.Rollback(); } catch { }
                throw;
            }
            catch (IrisException)
            {
                try { tx?.Rollback(); } catch { }
                throw;
            }
            catch (SqliteException ex)
            {
                try { tx?.Rollback(); } catch { }
                LogSqliteOperationFailed(_logger, "DeleteAsync", ex.SqliteErrorCode);
                throw new IrisException(ErrorCode.StorageFailure, "Database delete failed with SQLite error.");
            }
            catch (Exception)
            {
                try { tx?.Rollback(); } catch { }
                LogOperationFailed(_logger, "DeleteAsync");
                throw new IrisException(ErrorCode.StorageFailure, "Database delete failed.");
            }
            finally
            {
                tx?.Dispose();
                connection?.Dispose();
            }
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task ClearAsync(bool keepFavorites, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        await Task.Run(() => ClearCore(keepFavorites, cancellationToken), cancellationToken).ConfigureAwait(false);
    }

    private void ClearCore(bool keepFavorites, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        _writeLock.Wait(cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            SqliteConnection? connection = null;
            SqliteTransaction? tx = null;
            try
            {
                connection = _connectionFactory.CreateConnection();
                tx = connection.BeginTransaction(deferred: false);

                if (keepFavorites)
                {
                    connection.Execute("DELETE FROM translation_history WHERE is_favorite = 0;", transaction: tx);
                }
                else
                {
                    connection.Execute("DELETE FROM translation_history;", transaction: tx);
                    connection.Execute("DELETE FROM history_fts;", transaction: tx);
                }

                cancellationToken.ThrowIfCancellationRequested();
                tx.Commit();
            }
            catch (OperationCanceledException)
            {
                try { tx?.Rollback(); } catch { }
                throw;
            }
            catch (IrisException)
            {
                try { tx?.Rollback(); } catch { }
                throw;
            }
            catch (SqliteException ex)
            {
                try { tx?.Rollback(); } catch { }
                LogSqliteOperationFailed(_logger, "ClearAsync", ex.SqliteErrorCode);
                throw new IrisException(ErrorCode.StorageFailure, "Database clear failed with SQLite error.");
            }
            catch (Exception)
            {
                try { tx?.Rollback(); } catch { }
                LogOperationFailed(_logger, "ClearAsync");
                throw new IrisException(ErrorCode.StorageFailure, "Database clear failed.");
            }
            finally
            {
                tx?.Dispose();
                connection?.Dispose();
            }
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private static void TrimNonFavorites(SqliteConnection connection, SqliteTransaction tx, int maxEntries)
    {
        if (maxEntries <= 0)
        {
            connection.Execute("DELETE FROM translation_history WHERE is_favorite = 0;", transaction: tx);
        }
        else
        {
            const string trimSql = """
                DELETE FROM translation_history
                WHERE is_favorite = 0 AND id NOT IN (
                    SELECT id FROM translation_history
                    WHERE is_favorite = 0
                    ORDER BY created_at DESC, id DESC
                    LIMIT @maxEntries
                );
                """;
            connection.Execute(trimSql, new { maxEntries }, transaction: tx);
        }
    }

    internal static string CreateSourcePreview(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var runes = text.EnumerateRunes().ToList();
        if (runes.Count <= 200)
        {
            return text;
        }

        var sb = new StringBuilder(201);
        for (var i = 0; i < 200; i++)
        {
            sb.Append(runes[i]);
        }
        sb.Append('…');
        return sb.ToString();
    }

    internal static string EscapeLikePattern(string input)
    {
        var sb = new StringBuilder(input.Length + 8);
        foreach (var c in input)
        {
            if (c is '\\' or '%' or '_')
            {
                sb.Append('\\');
            }
            sb.Append(c);
        }
        return $"%{sb}%";
    }

    private async Task EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        await _databaseInitializer.InitializeAsync(cancellationToken).ConfigureAwait(false);
    }

    public void Dispose()
    {
        _disposed = true;
        // 注意：不销毁 _writeLock，防止排队等待或在 finally 中执行 Release 的任务被破坏
    }

    private sealed class HistoryRowDto
    {
        public long id { get; set; }
        public string created_at { get; set; } = string.Empty;
        public string origin { get; set; } = string.Empty;
        public string source_text { get; set; } = string.Empty;
        public string source_lang { get; set; } = string.Empty;
        public string target_lang { get; set; } = string.Empty;
        public string text_hash { get; set; } = string.Empty;
        public int is_favorite { get; set; }
    }

    private sealed class SummaryRowDto
    {
        public long id { get; set; }
        public string created_at { get; set; } = string.Empty;
        public string origin { get; set; } = string.Empty;
        public string raw_preview { get; set; } = string.Empty;
        public string source_lang { get; set; } = string.Empty;
        public string target_lang { get; set; } = string.Empty;
        public int is_favorite { get; set; }
        public int result_count { get; set; }
        public int has_truncated_results { get; set; }
    }

    private sealed class HistoryDetailDto
    {
        public long id { get; set; }
        public string created_at { get; set; } = string.Empty;
        public string origin { get; set; } = string.Empty;
        public string source_text { get; set; } = string.Empty;
        public string source_lang { get; set; } = string.Empty;
        public string target_lang { get; set; } = string.Empty;
        public int is_favorite { get; set; }
    }

    private sealed class ResultDetailDto
    {
        public string provider_id { get; set; } = string.Empty;
        public string provider_name { get; set; } = string.Empty;
        public string? model { get; set; }
        public string result_text { get; set; } = string.Empty;
        public int is_truncated { get; set; }
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Error, Message = "History repository operation {Operation} failed with SQLite error code {SqliteErrorCode}.")]
    private static partial void LogSqliteOperationFailed(ILogger logger, string operation, int sqliteErrorCode);

    [LoggerMessage(EventId = 2, Level = LogLevel.Error, Message = "History repository operation {Operation} failed.")]
    private static partial void LogOperationFailed(ILogger logger, string operation);
}
