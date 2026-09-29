using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Iris.Core.Errors;
using Iris.Infrastructure.Storage;
using Iris.Infrastructure.Tests.TestSupport;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

namespace Iris.Infrastructure.Tests.Storage;

public sealed class DatabaseInitializerTests
{
    [Fact]
    public async Task InitializeAsync_FreshEmptyDatabase_InitializesToV2_EnablesWalAndForeignKeys()
    {
        var ct = TestContext.Current.CancellationToken;
        using var tempDir = new TempDirectory();
        var paths = new FakeAppPaths(tempDir.Path);
        var factory = new SqliteConnectionFactory(paths);
        using var initializer = new DatabaseInitializer(factory, NullLogger<DatabaseInitializer>.Instance);

        await initializer.InitializeAsync(ct);

        // 验证用户版本为 2
        using var conn = factory.CreateConnection();
        using var cmd = conn.CreateCommand();

        cmd.CommandText = "PRAGMA user_version;";
        var version = Convert.ToInt32(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
        version.ShouldBe(2);

        // 验证 journal_mode 为 WAL
        cmd.CommandText = "PRAGMA journal_mode;";
        var journalMode = (cmd.ExecuteScalar() as string)?.ToLowerInvariant();
        journalMode.ShouldBe("wal");

        // 验证外键启用
        cmd.CommandText = "PRAGMA foreign_keys;";
        var fk = Convert.ToInt64(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
        fk.ShouldBe(1);

        // 验证 busy_timeout
        cmd.CommandText = "PRAGMA busy_timeout;";
        var timeout = Convert.ToInt64(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
        timeout.ShouldBeGreaterThanOrEqualTo(3000);

        // 验证表存在
        cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name IN ('translation_history', 'translation_result', 'history_fts');";
        var tableCount = Convert.ToInt64(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
        tableCount.ShouldBe(3);

        // 验证 translation_result 具有 is_truncated 列
        cmd.CommandText = "PRAGMA table_info(translation_result);";
        var hasIsTruncated = false;
        using (var reader = cmd.ExecuteReader())
        {
            while (reader.Read())
            {
                if (string.Equals(reader.GetString(1), "is_truncated", StringComparison.OrdinalIgnoreCase))
                {
                    hasIsTruncated = true;
                    break;
                }
            }
        }
        hasIsTruncated.ShouldBeTrue();

        // 验证触发器存在
        cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='trigger' AND name='trg_history_delete_fts';";
        var triggerCount = Convert.ToInt64(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
        triggerCount.ShouldBe(1);
    }

    [Fact]
    public async Task InitializeAsync_ExistingV1DatabaseWithData_MigratesToV2_PreservesDataAndSetsTruncatedZero()
    {
        var ct = TestContext.Current.CancellationToken;
        using var tempDir = new TempDirectory();
        var paths = new FakeAppPaths(tempDir.Path);
        var factory = new SqliteConnectionFactory(paths);

        // 手动构造一个带数据的 V1 数据库
        using (var setupConn = factory.CreateConnection())
        {
            using var cmd = setupConn.CreateCommand();
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

                INSERT INTO translation_history (id, created_at, origin, source_text, source_lang, target_lang, text_hash, is_favorite)
                VALUES (42, '2026-09-01T12:00:00.0000000Z', 'Input', 'Hello world', 'en', 'zh-CN', 'abc123hash', 1);

                INSERT INTO translation_result (id, history_id, sort_order, provider_id, provider_name, model, result_text)
                VALUES (101, 42, 0, 'p1', 'Provider One', 'm1', '你好，世界');

                INSERT INTO history_fts (rowid, source_text, result_text)
                VALUES (42, 'Hello world', '你好，世界');
                """;
            cmd.ExecuteNonQuery();
        }

        using var initializer = new DatabaseInitializer(factory, NullLogger<DatabaseInitializer>.Instance);
        await initializer.InitializeAsync(ct);

        // 验证升级到 V2
        using var conn = factory.CreateConnection();
        using var checkCmd = conn.CreateCommand();

        checkCmd.CommandText = "PRAGMA user_version;";
        var version = Convert.ToInt32(checkCmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
        version.ShouldBe(2);

        // 验证原记录及 Id 保留
        checkCmd.CommandText = "SELECT id, source_text, is_favorite FROM translation_history WHERE id = 42;";
        using (var reader = checkCmd.ExecuteReader())
        {
            reader.Read().ShouldBeTrue();
            reader.GetInt64(0).ShouldBe(42);
            reader.GetString(1).ShouldBe("Hello world");
            reader.GetInt32(2).ShouldBe(1);
        }

        // 验证旧结果的 is_truncated 列默认值为 0 (false)
        checkCmd.CommandText = "SELECT id, history_id, result_text, is_truncated FROM translation_result WHERE id = 101;";
        using (var reader = checkCmd.ExecuteReader())
        {
            reader.Read().ShouldBeTrue();
            reader.GetInt64(0).ShouldBe(101);
            reader.GetInt64(1).ShouldBe(42);
            reader.GetString(2).ShouldBe("你好，世界");
            reader.GetInt32(3).ShouldBe(0);
        }

        // 验证 FTS 仍可查询旧记录
        checkCmd.CommandText = "SELECT rowid FROM history_fts WHERE history_fts MATCH '\"Hello world\"';";
        var ftsRowId = Convert.ToInt64(checkCmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
        ftsRowId.ShouldBe(42);
    }

    [Fact]
    public async Task InitializeAsync_RepeatedAndConcurrentCalls_AreIdempotentAndThreadSafe()
    {
        var ct = TestContext.Current.CancellationToken;
        using var tempDir = new TempDirectory();
        var paths = new FakeAppPaths(tempDir.Path);
        var factory = new SqliteConnectionFactory(paths);
        using var initializer = new DatabaseInitializer(factory, NullLogger<DatabaseInitializer>.Instance);

        // 并发触发 10 次初始化
        var tasks = new Task[10];
        for (var i = 0; i < tasks.Length; i++)
        {
            tasks[i] = initializer.InitializeAsync(ct);
        }

        await Task.WhenAll(tasks);

        // 重复调用
        await initializer.InitializeAsync(ct);

        // 确认数据库完整为 V2
        using var conn = factory.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA user_version;";
        var version = Convert.ToInt32(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
        version.ShouldBe(2);
    }

    [Fact]
    public async Task InitializeAsync_WhenCancelled_CanBeRetriedSuccessfully()
    {
        var ct = TestContext.Current.CancellationToken;
        using var tempDir = new TempDirectory();
        var paths = new FakeAppPaths(tempDir.Path);
        var factory = new SqliteConnectionFactory(paths);
        using var initializer = new DatabaseInitializer(factory, NullLogger<DatabaseInitializer>.Instance);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.Cancel();

        await Should.ThrowAsync<OperationCanceledException>(async () =>
        {
            await initializer.InitializeAsync(cts.Token);
        });

        // 重新使用有效 Token 调用应能成功初始化
        await initializer.InitializeAsync(ct);

        using var conn = factory.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA user_version;";
        var version = Convert.ToInt32(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
        version.ShouldBe(2);
    }

    [Fact]
    public async Task InitializeAsync_FutureVersion_ThrowsStorageFailure_AndDoesNotOverwriteOrDeleteFile()
    {
        var ct = TestContext.Current.CancellationToken;
        using var tempDir = new TempDirectory();
        var paths = new FakeAppPaths(tempDir.Path);
        var factory = new SqliteConnectionFactory(paths);

        // 写入未来版本 99
        using (var setupConn = factory.CreateConnection())
        {
            using var cmd = setupConn.CreateCommand();
            cmd.CommandText = "PRAGMA user_version = 99; CREATE TABLE dummy_future (val TEXT); INSERT INTO dummy_future VALUES ('secret_future_data');";
            cmd.ExecuteNonQuery();
        }

        using var initializer = new DatabaseInitializer(factory, NullLogger<DatabaseInitializer>.Instance);
        var ex = await Should.ThrowAsync<IrisException>(async () =>
        {
            await initializer.InitializeAsync(ct);
        });

        ex.Code.ShouldBe(ErrorCode.StorageFailure);

        // 验证文件依然存在且未被删除覆盖
        File.Exists(paths.DatabaseFile).ShouldBeTrue();
        using (var checkConn = factory.CreateConnection())
        {
            using var cmd = checkConn.CreateCommand();
            cmd.CommandText = "PRAGMA user_version;";
            var v = Convert.ToInt32(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
            v.ShouldBe(99);

            cmd.CommandText = "SELECT val FROM dummy_future;";
            var val = cmd.ExecuteScalar() as string;
            val.ShouldBe("secret_future_data");
        }
    }

    [Fact]
    public async Task InitializeAsync_CorruptedDatabase_ThrowsStorageFailure_AndDoesNotDeleteFile()
    {
        var ct = TestContext.Current.CancellationToken;
        using var tempDir = new TempDirectory();
        var paths = new FakeAppPaths(tempDir.Path);

        // 写入非 SQLite 文件的垃圾二进制数据
        File.WriteAllBytes(paths.DatabaseFile, [0xDE, 0xAD, 0xBE, 0xEF, 0x01, 0x02, 0x03, 0x04]);

        var factory = new SqliteConnectionFactory(paths);
        using var initializer = new DatabaseInitializer(factory, NullLogger<DatabaseInitializer>.Instance);

        var ex = await Should.ThrowAsync<IrisException>(async () =>
        {
            await initializer.InitializeAsync(ct);
        });

        ex.Code.ShouldBe(ErrorCode.StorageFailure);

        // 验证原损坏文件保留，未被静默删除或重置
        File.Exists(paths.DatabaseFile).ShouldBeTrue();
        File.ReadAllBytes(paths.DatabaseFile).Length.ShouldBe(8);
    }

    [Fact]
    public async Task InitializeAsync_IncompatibleTablesAtVersion0_ThrowsStorageFailure_AndPreservesOriginalTables()
    {
        var ct = TestContext.Current.CancellationToken;
        using var tempDir = new TempDirectory();
        var paths = new FakeAppPaths(tempDir.Path);
        var factory = new SqliteConnectionFactory(paths);

        // 在 user_version = 0 下提前创建一个不兼容的同名表
        using (var setupConn = factory.CreateConnection())
        {
            using var cmd = setupConn.CreateCommand();
            cmd.CommandText = "CREATE TABLE translation_history (incompatible_col INTEGER); INSERT INTO translation_history VALUES (123);";
            cmd.ExecuteNonQuery();
        }

        using var initializer = new DatabaseInitializer(factory, NullLogger<DatabaseInitializer>.Instance);
        var ex = await Should.ThrowAsync<IrisException>(async () =>
        {
            await initializer.InitializeAsync(ct);
        });

        ex.Code.ShouldBe(ErrorCode.StorageFailure);

        // 验证原表和数据未被覆盖
        using (var checkConn = factory.CreateConnection())
        {
            using var cmd = checkConn.CreateCommand();
            cmd.CommandText = "SELECT incompatible_col FROM translation_history;";
            var val = Convert.ToInt32(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
            val.ShouldBe(123);
        }
    }
}
