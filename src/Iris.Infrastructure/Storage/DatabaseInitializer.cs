using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
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

            cancellationToken.ThrowIfCancellationRequested();

            // 2. 以读写模式打开连接
            connection = _connectionFactory.CreateConnection();

            cancellationToken.ThrowIfCancellationRequested();

            using var cmd = connection.CreateCommand();

            // 在执行任何写操作（包括 PRAGMA journal_mode = WAL）之前，必须先读取并再次验证写阶段实际版本与结构适用性！
            cmd.CommandText = "PRAGMA user_version;";
            var userVersion = Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);

            if (userVersion is < 0 or > TargetSchemaVersion)
            {
                throw new IrisException(
                    ErrorCode.StorageFailure,
                    $"Database version {userVersion} is not supported (supported versions: 0..{TargetSchemaVersion}).");
            }

            // 对实际看到的当前版本验证结构兼容性（不能假定只读检查永久有效）
            if (userVersion == 0)
            {
                cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name IN ('translation_history', 'translation_result', 'history_fts');";
                var tableCount = Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
                if (tableCount > 0)
                {
                    throw new IrisException(ErrorCode.StorageFailure, "Database contains existing tables but has user_version 0.");
                }
            }
            else if (userVersion == 1)
            {
                VerifyV1SchemaIntegrity(connection);
            }
            else if (userVersion == 2)
            {
                VerifyV2SchemaIntegrity(connection);
            }

            // 版本与结构确认受支持后，才允许设置并核验 WAL 模式
            cmd.CommandText = "PRAGMA journal_mode = WAL;";
            var journalMode = cmd.ExecuteScalar() as string;
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
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (IrisException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw;
        }
        catch (SqliteException ex)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LogDatabaseInitSqliteFailed(_logger, ex.SqliteErrorCode);
            throw new IrisException(ErrorCode.StorageFailure, "Database initialization failed with SQLite error.");
        }
        catch (Exception)
        {
            cancellationToken.ThrowIfCancellationRequested();
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

        if (userVersion is < 0 or > TargetSchemaVersion)
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
            cmd.CommandText = "PRAGMA user_version;";
            var currentVersion = Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
            if (currentVersion != 0)
            {
                throw new IrisException(ErrorCode.StorageFailure, $"Cannot migrate to V1 from version {currentVersion}.");
            }

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
            cmd.CommandText = "PRAGMA user_version;";
            var currentVersion = Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
            if (currentVersion != 1)
            {
                throw new IrisException(ErrorCode.StorageFailure, $"Cannot migrate to V2 from version {currentVersion}.");
            }

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
        VerifyTranslationHistoryTable(connection);
        VerifyTranslationResultTable(connection, schemaVersion: 1);
        VerifyForeignKeys(connection);
        VerifyFtsTable(connection);
        VerifyDeleteTrigger(connection);
        VerifyV1Indices(connection);
    }

    private static void VerifyV2SchemaIntegrity(SqliteConnection connection)
    {
        VerifyTranslationHistoryTable(connection);
        VerifyTranslationResultTable(connection, schemaVersion: 2);
        VerifyForeignKeys(connection);
        VerifyFtsTable(connection);
        VerifyDeleteTrigger(connection);
        VerifyV2Indices(connection);
    }

    private static void VerifyTranslationHistoryTable(SqliteConnection connection)
    {
        var cols = GetTableColumns(connection, "translation_history");
        if (cols.Count != 8)
        {
            throw new IrisException(ErrorCode.StorageFailure, $"translation_history has {cols.Count} columns, expected 8.");
        }

        var colMap = cols.ToDictionary(c => c.Name, StringComparer.OrdinalIgnoreCase);

        AssertColumn(colMap, "id", "INTEGER", pk: 1);
        AssertColumn(colMap, "created_at", "TEXT", notnull: true, pk: 0);
        AssertColumn(colMap, "origin", "TEXT", notnull: true, pk: 0);
        AssertColumn(colMap, "source_text", "TEXT", notnull: true, pk: 0);
        AssertColumn(colMap, "source_lang", "TEXT", notnull: true, pk: 0);
        AssertColumn(colMap, "target_lang", "TEXT", notnull: true, pk: 0);
        AssertColumn(colMap, "text_hash", "TEXT", notnull: true, pk: 0);
        AssertColumn(colMap, "is_favorite", "INTEGER", notnull: true, pk: 0);
    }

    private static void VerifyTranslationResultTable(SqliteConnection connection, int schemaVersion)
    {
        var cols = GetTableColumns(connection, "translation_result");
        var expectedCount = schemaVersion == 1 ? 7 : 8;
        if (cols.Count != expectedCount)
        {
            throw new IrisException(ErrorCode.StorageFailure, $"translation_result has {cols.Count} columns, expected {expectedCount}.");
        }

        var colMap = cols.ToDictionary(c => c.Name, StringComparer.OrdinalIgnoreCase);

        AssertColumn(colMap, "id", "INTEGER", pk: 1);
        AssertColumn(colMap, "history_id", "INTEGER", notnull: true, pk: 0);
        AssertColumn(colMap, "sort_order", "INTEGER", notnull: true, pk: 0);
        AssertColumn(colMap, "provider_id", "TEXT", notnull: true, pk: 0);
        AssertColumn(colMap, "provider_name", "TEXT", notnull: true, pk: 0);
        AssertColumn(colMap, "model", "TEXT", notnull: false, pk: 0);
        AssertColumn(colMap, "result_text", "TEXT", notnull: true, pk: 0);

        if (schemaVersion >= 2)
        {
            AssertColumn(colMap, "is_truncated", "INTEGER", notnull: true, pk: 0);
        }
    }

    private static void AssertColumn(
        Dictionary<string, TableColumnInfo> colMap,
        string name,
        string expectedType,
        bool notnull = false,
        int pk = 0)
    {
        if (!colMap.TryGetValue(name, out var col))
        {
            throw new IrisException(ErrorCode.StorageFailure, $"Database schema missing column '{name}'.");
        }

        if (!string.Equals(col.Type, expectedType, StringComparison.OrdinalIgnoreCase))
        {
            throw new IrisException(ErrorCode.StorageFailure, $"Column '{name}' has type '{col.Type}', expected '{expectedType}'.");
        }

        if (col.PrimaryKey != pk)
        {
            throw new IrisException(ErrorCode.StorageFailure, $"Column '{name}' has pk={col.PrimaryKey}, expected {pk}.");
        }

        if (notnull && !col.NotNull)
        {
            throw new IrisException(ErrorCode.StorageFailure, $"Column '{name}' is expected to be NOT NULL.");
        }
    }

    private static void VerifyForeignKeys(SqliteConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "PRAGMA foreign_key_list(translation_result);";
        var hasCascadeFk = false;
        var fkCount = 0;
        using (var reader = cmd.ExecuteReader())
        {
            while (reader.Read())
            {
                fkCount++;
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
                }
            }
        }

        if (!hasCascadeFk || fkCount != 1)
        {
            throw new IrisException(ErrorCode.StorageFailure, "Database schema missing or invalid foreign key cascade on translation_result.");
        }
    }

    private static void VerifyFtsTable(SqliteConnection connection)
    {
        using var cmd = connection.CreateCommand();

        // 1. 检查 table_list: 必须是 virtual table
        cmd.CommandText = "PRAGMA table_list('history_fts');";
        var isVirtual = false;
        using (var reader = cmd.ExecuteReader())
        {
            while (reader.Read())
            {
                var tableName = reader.GetString(1);
                var tableType = reader.GetString(2);
                if (string.Equals(tableName, "history_fts", StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(tableType, "virtual", StringComparison.OrdinalIgnoreCase))
                {
                    isVirtual = true;
                    break;
                }
            }
        }

        if (!isVirtual)
        {
            throw new IrisException(ErrorCode.StorageFailure, "history_fts is not a virtual table.");
        }

        // 2. 检查列: 必须恰好为 source_text 和 result_text
        var cols = GetTableColumns(connection, "history_fts");
        if (cols.Count != 2 ||
            !cols.Any(c => string.Equals(c.Name, "source_text", StringComparison.OrdinalIgnoreCase)) ||
            !cols.Any(c => string.Equals(c.Name, "result_text", StringComparison.OrdinalIgnoreCase)))
        {
            throw new IrisException(ErrorCode.StorageFailure, "history_fts columns do not match expected (source_text, result_text).");
        }

        // 3. 检查 sqlite_master 中 DDL: 必须使用 fts5 与 tokenize = 'trigram'
        cmd.CommandText = "SELECT sql FROM sqlite_master WHERE type='table' AND name='history_fts';";
        var sql = cmd.ExecuteScalar() as string;
        if (string.IsNullOrWhiteSpace(sql))
        {
            throw new IrisException(ErrorCode.StorageFailure, "Missing history_fts DDL in sqlite_master.");
        }

        var normalizedSql = NormalizeSql(sql);
        var match = Regex.IsMatch(
            normalizedSql,
            @"^create\s+virtual\s+table\s+(?:if\s+not\s+exists\s+)?(?:main\.)?history_fts\s+using\s+fts5\s*\(\s*source_text\s*,\s*result_text\s*,\s*tokenize\s*=\s*['""]?trigram['""]?\s*\);?$",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);

        if (!match)
        {
            throw new IrisException(ErrorCode.StorageFailure, "history_fts virtual table DDL does not match required FTS5 trigram specification.");
        }
    }

    private static void VerifyDeleteTrigger(SqliteConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT sql FROM sqlite_master WHERE type='trigger' AND name='trg_history_delete_fts';";
        var sql = cmd.ExecuteScalar() as string;
        if (string.IsNullOrWhiteSpace(sql))
        {
            throw new IrisException(ErrorCode.StorageFailure, "Missing required trigger trg_history_delete_fts.");
        }

        var normalizedSql = NormalizeSql(sql);
        var match = Regex.IsMatch(
            normalizedSql,
            @"^create\s+trigger\s+(?:if\s+not\s+exists\s+)?(?:main\.)?trg_history_delete_fts\s+after\s+delete\s+on\s+translation_history\s+begin\s+delete\s+from\s+history_fts\s+where\s+rowid\s*=\s*old\.id\s*;?\s*end;?$",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);

        if (!match)
        {
            throw new IrisException(ErrorCode.StorageFailure, "Trigger trg_history_delete_fts DDL does not match required predicate (WHERE rowid = old.id).");
        }
    }

    private static void VerifyV1Indices(SqliteConnection connection)
    {
        var historyIndices = GetTableIndices(connection, "translation_history");
        string[] requiredHistoryIndices = ["ix_history_created_at", "ix_history_favorite", "ix_history_hash"];
        foreach (var idx in requiredHistoryIndices)
        {
            if (!historyIndices.Contains(idx))
            {
                throw new IrisException(ErrorCode.StorageFailure, $"Database schema missing index '{idx}' on translation_history.");
            }
        }

        var resultIndices = GetTableIndices(connection, "translation_result");
        if (!resultIndices.Contains("ix_result_history"))
        {
            throw new IrisException(ErrorCode.StorageFailure, "Database schema missing index 'ix_result_history' on translation_result.");
        }

        AssertIndex(connection, "ix_history_created_at", [("created_at", true)]);
        AssertIndex(connection, "ix_history_favorite", [("is_favorite", false), ("created_at", true)]);
        AssertIndex(connection, "ix_history_hash", [("text_hash", false), ("target_lang", false)]);
        AssertIndex(connection, "ix_result_history", [("history_id", false), ("sort_order", false)]);
    }

    private static void VerifyV2Indices(SqliteConnection connection)
    {
        var historyIndices = GetTableIndices(connection, "translation_history");
        string[] requiredHistoryIndices = ["ix_history_created_at", "ix_history_favorite", "ix_history_hash"];
        foreach (var idx in requiredHistoryIndices)
        {
            if (!historyIndices.Contains(idx))
            {
                throw new IrisException(ErrorCode.StorageFailure, $"Database schema missing index '{idx}' on translation_history.");
            }
        }

        var resultIndices = GetTableIndices(connection, "translation_result");
        if (!resultIndices.Contains("ix_result_history"))
        {
            throw new IrisException(ErrorCode.StorageFailure, "Database schema missing index 'ix_result_history' on translation_result.");
        }

        AssertIndex(connection, "ix_history_created_at", [("created_at", true), ("id", true)]);
        AssertIndex(connection, "ix_history_favorite", [("is_favorite", false), ("created_at", true), ("id", true)]);
        AssertIndex(connection, "ix_history_hash", [("text_hash", false), ("source_lang", false), ("target_lang", false)]);
        AssertIndex(connection, "ix_result_history", [("history_id", false), ("sort_order", false)]);
    }

    private static void AssertIndex(
        SqliteConnection connection,
        string indexName,
        (string Name, bool IsDesc)[] expectedColumns)
    {
        var actual = GetIndexColumns(connection, indexName);
        if (actual.Count != expectedColumns.Length)
        {
            throw new IrisException(ErrorCode.StorageFailure, $"Index '{indexName}' has {actual.Count} columns, expected {expectedColumns.Length}.");
        }

        for (var i = 0; i < expectedColumns.Length; i++)
        {
            var exp = expectedColumns[i];
            var act = actual[i];
            if (!string.Equals(act.Name, exp.Name, StringComparison.OrdinalIgnoreCase) || act.IsDesc != exp.IsDesc)
            {
                throw new IrisException(ErrorCode.StorageFailure, $"Index '{indexName}' column {i} mismatch: got ({act.Name}, desc={act.IsDesc}), expected ({exp.Name}, desc={exp.IsDesc}).");
            }
        }
    }

    private static List<TableColumnInfo> GetTableColumns(SqliteConnection connection, string tableName)
    {
        var list = new List<TableColumnInfo>();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({tableName});";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var cid = reader.GetInt32(0);
            var name = reader.GetString(1);
            var type = reader.IsDBNull(2) ? string.Empty : reader.GetString(2);
            var notnull = reader.GetInt32(3) != 0;
            var dflt = reader.IsDBNull(4) ? null : reader.GetString(4);
            var pk = reader.GetInt32(5);
            list.Add(new TableColumnInfo(cid, name, type, notnull, dflt, pk));
        }
        return list;
    }

    private static HashSet<string> GetTableIndices(SqliteConnection connection, string tableName)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"PRAGMA index_list({tableName});";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var indexName = reader.GetString(1);
            set.Add(indexName);
        }
        return set;
    }

    private static List<IndexColumnInfo> GetIndexColumns(SqliteConnection connection, string indexName)
    {
        var list = new List<IndexColumnInfo>();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"PRAGMA index_xinfo({indexName});";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var seqno = reader.GetInt32(0);
            var name = reader.IsDBNull(2) ? string.Empty : reader.GetString(2);
            var isDesc = reader.GetInt32(3) != 0;
            var isKey = reader.GetInt32(5) != 0;
            if (isKey)
            {
                list.Add(new IndexColumnInfo(seqno, name, isDesc, isKey));
            }
        }
        return list;
    }

    private static string NormalizeSql(string sql)
    {
        var noLineComments = Regex.Replace(sql, @"--.*?$", "", RegexOptions.Multiline);
        var noBlockComments = Regex.Replace(noLineComments, @"/\*.*?\*/", "", RegexOptions.Singleline);
        return Regex.Replace(noBlockComments, @"\s+", " ").Trim();
    }

    public void Dispose()
    {
        _disposed = true;
        // 注意：不销毁 _initLock，防止仍持有锁或处于 finally 块中的异步工作单元因 ObjectDisposedException 覆盖原始异常
    }

    private readonly record struct TableColumnInfo(
        int Cid,
        string Name,
        string Type,
        bool NotNull,
        string? DefaultValue,
        int PrimaryKey);

    private readonly record struct IndexColumnInfo(
        int SeqNo,
        string Name,
        bool IsDesc,
        bool IsKey);

    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "SQLite journal_mode is {JournalMode}, expected WAL.")]
    private static partial void LogJournalModeMismatch(ILogger logger, string journalMode);

    [LoggerMessage(EventId = 2, Level = LogLevel.Error, Message = "Database initialization failed with SQLite error code {SqliteErrorCode}.")]
    private static partial void LogDatabaseInitSqliteFailed(ILogger logger, int sqliteErrorCode);

    [LoggerMessage(EventId = 3, Level = LogLevel.Error, Message = "Database initialization failed with error.")]
    private static partial void LogDatabaseInitFailed(ILogger logger);
}
