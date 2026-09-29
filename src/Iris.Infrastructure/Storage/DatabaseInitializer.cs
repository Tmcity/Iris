using System;
using System.Collections.Generic;
using System.Globalization;
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
            // 1. 若文件已存在，先以只读模式进行严格的兼容性与完整性预检，防止修改未来版本或损坏/不兼容数据库
            if (_connectionFactory.DatabaseExists)
            {
                PrecheckExistingDatabase(cancellationToken);
            }

            // 2. 以读写模式打开连接并实施 WAL 模式配置与必要迁移
            connection = _connectionFactory.CreateConnection();

            using (var pragmaCmd = connection.CreateCommand())
            {
                // 读取 user_version
                pragmaCmd.CommandText = "PRAGMA user_version;";
                var userVersion = Convert.ToInt32(pragmaCmd.ExecuteScalar(), CultureInfo.InvariantCulture);

                // 设置并核验 WAL 模式
                pragmaCmd.CommandText = "PRAGMA journal_mode = WAL;";
                var journalMode = pragmaCmd.ExecuteScalar() as string;
                if (!string.Equals(journalMode, "wal", StringComparison.OrdinalIgnoreCase))
                {
                    LogJournalModeMismatch(_logger, journalMode ?? "<null>");
                    throw new IrisException(ErrorCode.StorageFailure, $"Failed to enable WAL journal mode, current mode is {journalMode}.");
                }

                // 顺序执行迁移：每个迁移独立事务，成功提交后更新版本
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

                // 迁移后最终校验 V2 完整结构
                VerifyV2SchemaIntegrity(connection);
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
            throw new IrisException(ErrorCode.StorageFailure, "Database initialization failed with SQLite error.");
        }
        catch (Exception)
        {
            LogDatabaseInitFailed(_logger);
            throw new IrisException(ErrorCode.StorageFailure, "Database initialization failed.");
        }
        finally
        {
            connection?.Dispose();
        }
    }

    private void PrecheckExistingDatabase(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        using var readConn = _connectionFactory.CreateReadOnlyConnection();
        using var cmd = readConn.CreateCommand();

        // 1. 快速完整性校验
        cmd.CommandText = "PRAGMA quick_check(1);";
        var checkResult = cmd.ExecuteScalar() as string;
        if (!string.Equals(checkResult, "ok", StringComparison.OrdinalIgnoreCase))
        {
            throw new IrisException(ErrorCode.StorageFailure, "Database integrity check failed.");
        }

        // 2. 读取 user_version
        cmd.CommandText = "PRAGMA user_version;";
        var userVersion = Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);

        if (userVersion > TargetSchemaVersion)
        {
            throw new IrisException(
                ErrorCode.StorageFailure,
                $"Database version {userVersion} is newer than supported version {TargetSchemaVersion}.");
        }

        // 3. 版本 0 时检查已有表
        if (userVersion == 0)
        {
            cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name IN ('translation_history', 'translation_result', 'history_fts');";
            var existingIrisTableCount = Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
            if (existingIrisTableCount > 0)
            {
                throw new IrisException(
                    ErrorCode.StorageFailure,
                    "Database contains existing tables but has user_version 0.");
            }
        }

        // 4. 版本 1 时预检 V1 必需结构
        if (userVersion == 1)
        {
            VerifyV1SchemaIntegrity(readConn);
        }

        // 5. 版本 2 时预检 V2 必需结构
        if (userVersion == 2)
        {
            VerifyV2SchemaIntegrity(readConn);
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

    private static void VerifyV1SchemaIntegrity(SqliteConnection connection)
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
        string[] requiredResultColumns = ["id", "history_id", "sort_order", "provider_id", "provider_name", "model", "result_text"];
        foreach (var col in requiredResultColumns)
        {
            if (!resultColumns.Contains(col))
            {
                throw new IrisException(ErrorCode.StorageFailure, $"Database schema missing column '{col}' on translation_result.");
            }
        }

        VerifyForeignKeysAndTriggers(connection);
    }

    private static void VerifyV2SchemaIntegrity(SqliteConnection connection)
    {
        VerifyV1SchemaIntegrity(connection);

        var resultColumns = GetTableColumns(connection, "translation_result");
        if (!resultColumns.Contains("is_truncated"))
        {
            throw new IrisException(ErrorCode.StorageFailure, "Database schema missing column 'is_truncated' on translation_result.");
        }

        // 校验必需索引
        var indices = GetIndices(connection);
        string[] requiredIndices = ["ix_history_created_at", "ix_history_favorite", "ix_history_hash", "ix_result_history"];
        foreach (var idx in requiredIndices)
        {
            if (!indices.Contains(idx))
            {
                throw new IrisException(ErrorCode.StorageFailure, $"Database schema missing index '{idx}'.");
            }
        }
    }

    private static void VerifyForeignKeysAndTriggers(SqliteConnection connection)
    {
        using var cmd = connection.CreateCommand();

        // 1. 校验 history_fts 虚表及 trigram
        cmd.CommandText = "SELECT sql FROM sqlite_master WHERE type='table' AND name='history_fts';";
        var ftsSql = cmd.ExecuteScalar() as string;
        if (string.IsNullOrEmpty(ftsSql) ||
            !ftsSql.Contains("fts5", StringComparison.OrdinalIgnoreCase) ||
            !ftsSql.Contains("trigram", StringComparison.OrdinalIgnoreCase))
        {
            throw new IrisException(ErrorCode.StorageFailure, "Database schema missing history_fts FTS5 trigram virtual table.");
        }

        // 2. 校验 trg_history_delete_fts 触发器
        cmd.CommandText = "SELECT sql FROM sqlite_master WHERE type='trigger' AND name='trg_history_delete_fts';";
        var triggerSql = cmd.ExecuteScalar() as string;
        if (string.IsNullOrEmpty(triggerSql) ||
            !triggerSql.Contains("DELETE FROM history_fts", StringComparison.OrdinalIgnoreCase))
        {
            throw new IrisException(ErrorCode.StorageFailure, "Database schema missing required trg_history_delete_fts trigger.");
        }

        // 3. 校验外键级联
        cmd.CommandText = "PRAGMA foreign_key_list(translation_result);";
        var hasCascadeFk = false;
        using (var reader = cmd.ExecuteReader())
        {
            while (reader.Read())
            {
                var targetTable = reader.GetString(2);
                var fromCol = reader.GetString(3);
                var toCol = reader.GetString(4);
                var onDelete = reader.GetString(6);

                if (string.Equals(targetTable, "translation_history", StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(fromCol, "history_id", StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(toCol, "id", StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(onDelete, "CASCADE", StringComparison.OrdinalIgnoreCase))
                {
                    hasCascadeFk = true;
                    break;
                }
            }
        }

        if (!hasCascadeFk)
        {
            throw new IrisException(ErrorCode.StorageFailure, "Database schema missing foreign key cascade on translation_result.");
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
            columns.Add(reader.GetString(1));
        }
        return columns;
    }

    private static HashSet<string> GetIndices(SqliteConnection connection)
    {
        var indices = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='index';";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            if (!reader.IsDBNull(0))
            {
                indices.Add(reader.GetString(0));
            }
        }
        return indices;
    }

    public void Dispose()
    {
        _disposed = true;
        // 注意：不销毁 _initLock，防止仍持有锁或处于 finally 块中的异步工作单元因 ObjectDisposedException 覆盖原始异常
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "SQLite journal_mode is {JournalMode}, expected WAL.")]
    private static partial void LogJournalModeMismatch(ILogger logger, string journalMode);

    [LoggerMessage(EventId = 2, Level = LogLevel.Error, Message = "Database initialization failed with SQLite error code {SqliteErrorCode}.")]
    private static partial void LogDatabaseInitSqliteFailed(ILogger logger, int sqliteErrorCode);

    [LoggerMessage(EventId = 3, Level = LogLevel.Error, Message = "Database initialization failed with error.")]
    private static partial void LogDatabaseInitFailed(ILogger logger);
}
