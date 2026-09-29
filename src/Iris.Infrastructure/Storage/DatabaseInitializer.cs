using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Iris.Core.Errors;
using Iris.Core.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Iris.Infrastructure.Storage;

internal sealed partial class DatabaseInitializer : IDatabaseInitializer, IDisposable
{
    private const int TargetSchemaVersion = 2;

    private readonly ISqliteConnectionFactory _connectionFactory;
    private readonly ILogger<DatabaseInitializer> _logger;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private bool _initialized;
    private bool _disposed;

    public DatabaseInitializer(
        ISqliteConnectionFactory connectionFactory,
        ILogger<DatabaseInitializer> logger)
    {
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_initialized)
        {
            return;
        }

        await _initLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (_initialized)
            {
                return;
            }

            await Task.Run(() => InitializeCore(cancellationToken), cancellationToken).ConfigureAwait(false);
            _initialized = true;
        }
        finally
        {
            _initLock.Release();
        }
    }

    private void InitializeCore(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        SqliteConnection? connection = null;
        try
        {
            connection = _connectionFactory.CreateConnection();

            // 1. 设置并核验 WAL 模式
            using (var pragmaCmd = connection.CreateCommand())
            {
                pragmaCmd.CommandText = "PRAGMA journal_mode = WAL;";
                var journalMode = pragmaCmd.ExecuteScalar() as string;
                if (!string.Equals(journalMode, "wal", StringComparison.OrdinalIgnoreCase))
                {
                    LogJournalModeMismatch(_logger, journalMode ?? "<null>");
                }

                // 2. 数据库快速完整性检查
                pragmaCmd.CommandText = "PRAGMA quick_check(1);";
                var checkResult = pragmaCmd.ExecuteScalar() as string;
                if (!string.Equals(checkResult, "ok", StringComparison.OrdinalIgnoreCase))
                {
                    throw new IrisException(ErrorCode.StorageFailure, "Database integrity check failed.");
                }

                // 3. 读取 user_version
                pragmaCmd.CommandText = "PRAGMA user_version;";
                var userVersion = Convert.ToInt32(pragmaCmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);

                if (userVersion > TargetSchemaVersion)
                {
                    throw new IrisException(
                        ErrorCode.StorageFailure,
                        $"Database version {userVersion} is newer than supported version {TargetSchemaVersion}.");
                }

                // 4. 版本 0 检查不兼容结构：不能用 IF NOT EXISTS 掩盖已有不兼容表
                if (userVersion == 0)
                {
                    pragmaCmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name IN ('translation_history', 'translation_result', 'history_fts');";
                    var existingIrisTableCount = Convert.ToInt64(pragmaCmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
                    if (existingIrisTableCount > 0)
                    {
                        throw new IrisException(
                            ErrorCode.StorageFailure,
                            "Database contains existing tables but has user_version 0.");
                    }
                }

                // 5. 顺序执行迁移：每个迁移独立事务，成功提交后更新版本
                if (userVersion < 1)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    MigrateV1(connection, cancellationToken);
                    userVersion = 1;
                }

                if (userVersion < 2)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    MigrateV2(connection, cancellationToken);
                    userVersion = 2;
                }

                // 6. 校验表结构与关键列完整性
                VerifySchemaIntegrity(connection);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (IrisException)
        {
            throw;
        }
        catch (SqliteException ex)
        {
            LogDatabaseInitSqliteFailed(_logger, ex.SqliteErrorCode);
            throw new IrisException(ErrorCode.StorageFailure, "Database initialization failed with SQLite error.", innerException: ex);
        }
        catch (Exception ex)
        {
            LogDatabaseInitFailed(_logger);
            throw new IrisException(ErrorCode.StorageFailure, "Database initialization failed.", innerException: ex);
        }
        finally
        {
            connection?.Dispose();
        }
    }

    private static void MigrateV1(SqliteConnection connection, CancellationToken cancellationToken)
    {
        using var tx = connection.BeginTransaction(deferred: false);
        try
        {
            using var cmd = connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                CREATE TABLE translation_history (
                    id              INTEGER PRIMARY KEY AUTOINCREMENT,
                    created_at      TEXT    NOT NULL,
                    origin          TEXT    NOT NULL,
                    source_text     TEXT    NOT NULL,
                    source_lang     TEXT    NOT NULL,
                    target_lang     TEXT    NOT NULL,
                    text_hash       TEXT    NOT NULL,
                    is_favorite     INTEGER NOT NULL DEFAULT 0 CHECK (is_favorite IN (0, 1))
                );

                CREATE INDEX ix_history_created_at ON translation_history (created_at DESC);
                CREATE INDEX ix_history_favorite   ON translation_history (is_favorite, created_at DESC);
                CREATE INDEX ix_history_hash       ON translation_history (text_hash, target_lang);

                CREATE TABLE translation_result (
                    id              INTEGER PRIMARY KEY AUTOINCREMENT,
                    history_id      INTEGER NOT NULL REFERENCES translation_history (id) ON DELETE CASCADE,
                    sort_order      INTEGER NOT NULL,
                    provider_id     TEXT    NOT NULL,
                    provider_name   TEXT    NOT NULL,
                    model           TEXT,
                    result_text     TEXT    NOT NULL
                );

                CREATE INDEX ix_result_history ON translation_result (history_id, sort_order);

                CREATE VIRTUAL TABLE history_fts USING fts5 (
                    source_text,
                    result_text,
                    tokenize = 'trigram'
                );

                CREATE TRIGGER trg_history_delete_fts
                AFTER DELETE ON translation_history
                BEGIN
                    DELETE FROM history_fts WHERE rowid = old.id;
                END;

                PRAGMA user_version = 1;
                """;
            cmd.ExecuteNonQuery();

            cancellationToken.ThrowIfCancellationRequested();
            tx.Commit();
        }
        catch
        {
            try { tx.Rollback(); } catch { }
            throw;
        }
    }

    private static void MigrateV2(SqliteConnection connection, CancellationToken cancellationToken)
    {
        using var tx = connection.BeginTransaction(deferred: false);
        try
        {
            using var cmd = connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                ALTER TABLE translation_result ADD COLUMN is_truncated INTEGER NOT NULL DEFAULT 0 CHECK (is_truncated IN (0, 1));

                DROP INDEX IF EXISTS ix_history_created_at;
                CREATE INDEX ix_history_created_at ON translation_history (created_at DESC, id DESC);
                DROP INDEX IF EXISTS ix_history_favorite;
                CREATE INDEX ix_history_favorite ON translation_history (is_favorite, created_at DESC, id DESC);
                DROP INDEX IF EXISTS ix_history_hash;
                CREATE INDEX ix_history_hash ON translation_history (text_hash, source_lang, target_lang);

                PRAGMA user_version = 2;
                """;
            cmd.ExecuteNonQuery();

            cancellationToken.ThrowIfCancellationRequested();
            tx.Commit();
        }
        catch
        {
            try { tx.Rollback(); } catch { }
            throw;
        }
    }

    private static void VerifySchemaIntegrity(SqliteConnection connection)
    {
        var historyColumns = GetTableColumns(connection, "translation_history");
        string[] requiredHistoryColumns = ["id", "created_at", "origin", "source_text", "source_lang", "target_lang", "text_hash", "is_favorite"];
        foreach (var col in requiredHistoryColumns)
        {
            if (!historyColumns.Contains(col))
            {
                throw new IrisException(ErrorCode.StorageFailure, $"Database schema missing column '{col}' on translation_history.");
            }
        }

        var resultColumns = GetTableColumns(connection, "translation_result");
        string[] requiredResultColumns = ["id", "history_id", "sort_order", "provider_id", "provider_name", "model", "result_text", "is_truncated"];
        foreach (var col in requiredResultColumns)
        {
            if (!resultColumns.Contains(col))
            {
                throw new IrisException(ErrorCode.StorageFailure, $"Database schema missing column '{col}' on translation_result.");
            }
        }

        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='history_fts';";
        var ftsCount = Convert.ToInt64(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
        if (ftsCount == 0)
        {
            throw new IrisException(ErrorCode.StorageFailure, "Database schema missing history_fts virtual table.");
        }
    }

    private static HashSet<string> GetTableColumns(SqliteConnection connection, string tableName)
    {
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({tableName});";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            columns.Add(reader.GetString(1)); // column 1 is 'name'
        }
        return columns;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _initLock.Dispose();
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "SQLite journal_mode is {JournalMode}, expected WAL.")]
    private static partial void LogJournalModeMismatch(ILogger logger, string journalMode);

    [LoggerMessage(EventId = 2, Level = LogLevel.Error, Message = "Database initialization failed with SQLite error code {SqliteErrorCode}.")]
    private static partial void LogDatabaseInitSqliteFailed(ILogger logger, int sqliteErrorCode);

    [LoggerMessage(EventId = 3, Level = LogLevel.Error, Message = "Database initialization failed with error.")]
    private static partial void LogDatabaseInitFailed(ILogger logger);
}
