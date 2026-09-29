using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Iris.Core.Common;
using Iris.Core.Errors;
using Iris.Core.Settings;
using Iris.Core.Storage;
using Iris.Core.Translation;
using Iris.Infrastructure.Storage;
using Iris.Infrastructure.Tests.TestSupport;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

namespace Iris.Infrastructure.Tests.Storage;

public sealed class SqliteHistoryRepositoryTests
{
    private sealed class FakeSettingsService : ISettingsService
    {
        public AppSettings Current { get; set; } = new();

        public event EventHandler<SettingsChangedEventArgs>? Changed;

        public Task LoadAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task UpdateAsync(Func<AppSettings, AppSettings> mutate, CancellationToken cancellationToken = default)
        {
            var old = Current;
            Current = mutate(Current);
            Changed?.Invoke(this, new SettingsChangedEventArgs(old, Current));
            return Task.CompletedTask;
        }
    }

    private static (SqliteHistoryRepository Repo, DatabaseInitializer Initializer, SqliteConnectionFactory Factory, FakeSettingsService Settings)
        CreateRepository(string dataDirectory)
    {
        var paths = new FakeAppPaths(dataDirectory);
        var factory = new SqliteConnectionFactory(paths);
        var initializer = new DatabaseInitializer(factory, NullLogger<DatabaseInitializer>.Instance);
        var settings = new FakeSettingsService();
        var repo = new SqliteHistoryRepository(factory, initializer, settings, NullLogger<SqliteHistoryRepository>.Instance);
        return (repo, initializer, factory, settings);
    }

    private static HistoryEntry CreateEntry(
        string sourceText = "Hello world",
        string sourceLang = "en",
        string targetLang = "zh-CN",
        TextOrigin origin = TextOrigin.Input,
        bool isFavorite = false,
        DateTimeOffset? createdAt = null,
        IReadOnlyList<HistoryResult>? results = null)
    {
        return new HistoryEntry
        {
            SourceText = sourceText,
            SourceLanguage = sourceLang,
            TargetLanguage = targetLang,
            Origin = origin,
            IsFavorite = isFavorite,
            CreatedAt = createdAt ?? DateTimeOffset.UtcNow,
            Results = results ?? [new HistoryResult("p1", "Provider One", "model-1", "你好，世界")]
        };
    }

    [Fact]
    public async Task AddAsync_Validation_RejectsNullEmptyAndDuplicateProviders()
    {
        var ct = TestContext.Current.CancellationToken;
        using var tempDir = new TempDirectory();
        var (repo, initializer, _, _) = CreateRepository(tempDir.Path);
        using (repo)
        using (initializer)
        {
            await Should.ThrowAsync<ArgumentNullException>(async () => await repo.AddAsync(null!, ct));

            // 空白源文本
            await Should.ThrowAsync<ArgumentException>(async () =>
                await repo.AddAsync(CreateEntry(sourceText: "   "), ct));

            // 空白源语言
            await Should.ThrowAsync<ArgumentException>(async () =>
                await repo.AddAsync(CreateEntry(sourceLang: ""), ct));

            // 空白目标语言
            await Should.ThrowAsync<ArgumentException>(async () =>
                await repo.AddAsync(CreateEntry(targetLang: "  "), ct));

            // Results 集合为 null
            await Should.ThrowAsync<ArgumentException>(async () =>
                await repo.AddAsync(new HistoryEntry
                {
                    SourceText = "text",
                    SourceLanguage = "en",
                    TargetLanguage = "zh-CN",
                    Results = null!
                }, ct));

            // 结果中包含 null 项
            await Should.ThrowAsync<ArgumentException>(async () =>
                await repo.AddAsync(CreateEntry(results: [null!]), ct));

            // 结果中 ProviderId 为空
            await Should.ThrowAsync<ArgumentException>(async () =>
                await repo.AddAsync(CreateEntry(results: [new HistoryResult("", "name", null, "txt")]), ct));

            // 结果中 ProviderName 为空
            await Should.ThrowAsync<ArgumentException>(async () =>
                await repo.AddAsync(CreateEntry(results: [new HistoryResult("p1", "   ", null, "txt")]), ct));

            // 结果中 Text 为 null
            await Should.ThrowAsync<ArgumentException>(async () =>
                await repo.AddAsync(CreateEntry(results: [new HistoryResult("p1", "name", null, null!)]), ct));

            // 结果中包含重复 ProviderId
            await Should.ThrowAsync<ArgumentException>(async () =>
                await repo.AddAsync(CreateEntry(results: [
                    new HistoryResult("p1", "name1", null, "txt1"),
                    new HistoryResult("p1", "name2", null, "txt2")
                ]), ct));
        }
    }

    [Fact]
    public async Task AddAsync_InsertsNewRecord_ComputesHashAndUtcTimestamp_ReturnsNewId()
    {
        var ct = TestContext.Current.CancellationToken;
        using var tempDir = new TempDirectory();
        var (repo, initializer, _, _) = CreateRepository(tempDir.Path);
        using (repo)
        using (initializer)
        {
            var fixedTime = new DateTimeOffset(2026, 9, 29, 14, 30, 0, TimeSpan.FromHours(8));
            var entry = CreateEntry(
                sourceText: "Test insertion",
                sourceLang: "en",
                targetLang: "zh-CN",
                origin: TextOrigin.Screenshot,
                isFavorite: true,
                createdAt: fixedTime,
                results: [
                    new HistoryResult("openai", "OpenAI", "gpt-4o", "测试插入") { Truncated = true }
                ]);

            var id = await repo.AddAsync(entry, ct);
            id.ShouldBeGreaterThan(0);

            var detail = await repo.GetAsync(id, ct);
            detail.ShouldNotBeNull();
            detail.Id.ShouldBe(id);
            detail.SourceText.ShouldBe("Test insertion");
            detail.SourceLanguage.ShouldBe("en");
            detail.TargetLanguage.ShouldBe("zh-CN");
            detail.Origin.ShouldBe(TextOrigin.Screenshot);
            detail.IsFavorite.ShouldBeTrue();
            detail.CreatedAt.ShouldBe(fixedTime.ToUniversalTime());
            detail.Results.Count.ShouldBe(1);
            detail.Results[0].ProviderId.ShouldBe("openai");
            detail.Results[0].ProviderName.ShouldBe("OpenAI");
            detail.Results[0].Model.ShouldBe("gpt-4o");
            detail.Results[0].Text.ShouldBe("测试插入");
            detail.Results[0].Truncated.ShouldBeTrue();
        }
    }

    [Fact]
    public async Task AddAsync_Deduplication_WhenLatestRowIsUnfavoritedAndExactMatch_UpdatesExistingRow()
    {
        var ct = TestContext.Current.CancellationToken;
        using var tempDir = new TempDirectory();
        var (repo, initializer, _, _) = CreateRepository(tempDir.Path);
        using (repo)
        using (initializer)
        {
            var t1 = new DateTimeOffset(2026, 9, 29, 10, 0, 0, TimeSpan.Zero);
            var id1 = await repo.AddAsync(CreateEntry("Deduplicate text", createdAt: t1, isFavorite: false), ct);

            // 第二次写入相同源文本、源语言、目标语言，且前一条未收藏
            var t2 = new DateTimeOffset(2026, 9, 29, 10, 5, 0, TimeSpan.Zero);
            var id2 = await repo.AddAsync(CreateEntry(
                "Deduplicate text",
                createdAt: t2,
                isFavorite: false,
                results: [new HistoryResult("p2", "Provider Two", null, "新结果")]), ct);

            id2.ShouldBe(id1);

            // 验证只有 1 条记录
            var page = await repo.QueryAsync(new HistoryQuery(), ct);
            page.TotalCount.ShouldBe(1);

            var detail = await repo.GetAsync(id1, ct);
            detail.ShouldNotBeNull();
            detail.CreatedAt.ShouldBe(t2);
            detail.Results.Count.ShouldBe(1);
            detail.Results[0].ProviderId.ShouldBe("p2");
            detail.Results[0].Text.ShouldBe("新结果");

            // FTS 匹配新结果
            var ftsQuery = await repo.QueryAsync(new HistoryQuery("新结果"), ct);
            ftsQuery.TotalCount.ShouldBe(1);

            // 旧结果已被移除
            var oldFtsQuery = await repo.QueryAsync(new HistoryQuery("你好，世界"), ct);
            oldFtsQuery.TotalCount.ShouldBe(0);
        }
    }

    [Fact]
    public async Task AddAsync_Deduplication_WhenLanguageDiffers_InsertsNewRow()
    {
        var ct = TestContext.Current.CancellationToken;
        using var tempDir = new TempDirectory();
        var (repo, initializer, _, _) = CreateRepository(tempDir.Path);
        using (repo)
        using (initializer)
        {
            var id1 = await repo.AddAsync(CreateEntry("Same text", sourceLang: "en", targetLang: "zh-CN"), ct);

            // 改变目标语言
            var id2 = await repo.AddAsync(CreateEntry("Same text", sourceLang: "en", targetLang: "ja"), ct);
            id2.ShouldNotBe(id1);

            // 改变源语言
            var id3 = await repo.AddAsync(CreateEntry("Same text", sourceLang: "fr", targetLang: "ja"), ct);
            id3.ShouldNotBe(id2);

            var page = await repo.QueryAsync(new HistoryQuery(), ct);
            page.TotalCount.ShouldBe(3);
        }
    }

    [Fact]
    public async Task AddAsync_FavoriteProtection_WhenLatestRowIsFavorited_NeverOverwrites_InsertsNewRow()
    {
        var ct = TestContext.Current.CancellationToken;
        using var tempDir = new TempDirectory();
        var (repo, initializer, _, _) = CreateRepository(tempDir.Path);
        using (repo)
        using (initializer)
        {
            // 写入一条收藏记录
            var id1 = await repo.AddAsync(CreateEntry("Protected text", isFavorite: true), ct);

            // 再次写入相同文本和语言
            var id2 = await repo.AddAsync(CreateEntry("Protected text", isFavorite: false), ct);

            // 收藏保护：绝不覆盖，应创建新记录
            id2.ShouldNotBe(id1);

            var page = await repo.QueryAsync(new HistoryQuery(), ct);
            page.TotalCount.ShouldBe(2);

            // 第一条仍然保持收藏状态
            var d1 = await repo.GetAsync(id1, ct);
            d1.ShouldNotBeNull();
            d1.IsFavorite.ShouldBeTrue();

            var d2 = await repo.GetAsync(id2, ct);
            d2.ShouldNotBeNull();
            d2.IsFavorite.ShouldBeFalse();
        }
    }

    [Fact]
    public async Task AddAsync_CallerModifyingResultsListAfterCall_DoesNotAffectPersistedData()
    {
        var ct = TestContext.Current.CancellationToken;
        using var tempDir = new TempDirectory();
        var (repo, initializer, _, _) = CreateRepository(tempDir.Path);
        using (repo)
        using (initializer)
        {
            var mutableList = new List<HistoryResult>
            {
                new("p1", "Provider One", null, "Original result")
            };

            var entry = CreateEntry("Defensive text", results: mutableList);

            var task = repo.AddAsync(entry, ct);

            // 调度调用后立即修改调用方的列表
            mutableList[0] = new HistoryResult("p1", "Provider One", null, "Tampered result");
            mutableList.Add(new HistoryResult("p2", "Provider Two", null, "Injected result"));

            var id = await task;

            // 验证实际持久化的是调用边界捕获的快照
            var detail = await repo.GetAsync(id, ct);
            detail.ShouldNotBeNull();
            detail.Results.Count.ShouldBe(1);
            detail.Results[0].Text.ShouldBe("Original result");
        }
    }

    [Fact]
    public async Task Trimming_MaxEntries_LimitsNonFavoritesOnly_PreservesFavorites()
    {
        var ct = TestContext.Current.CancellationToken;
        using var tempDir = new TempDirectory();
        var (repo, initializer, _, settings) = CreateRepository(tempDir.Path);
        using (repo)
        using (initializer)
        {
            settings.Current = settings.Current with
            {
                History = settings.Current.History with { MaxEntries = 3 }
            };

            // 写入 3 条收藏记录
            var fav1 = await repo.AddAsync(CreateEntry("Fav 1", isFavorite: true), ct);
            var fav2 = await repo.AddAsync(CreateEntry("Fav 2", isFavorite: true), ct);
            var fav3 = await repo.AddAsync(CreateEntry("Fav 3", isFavorite: true), ct);

            // 写入 5 条非收藏记录
            var n1 = await repo.AddAsync(CreateEntry("NonFav 1", isFavorite: false), ct);
            var n2 = await repo.AddAsync(CreateEntry("NonFav 2", isFavorite: false), ct);
            var n3 = await repo.AddAsync(CreateEntry("NonFav 3", isFavorite: false), ct);
            var n4 = await repo.AddAsync(CreateEntry("NonFav 4", isFavorite: false), ct);
            var n5 = await repo.AddAsync(CreateEntry("NonFav 5", isFavorite: false), ct);

            // 总记录数：3 条收藏全部保留 + 3 条最新的非收藏 (n3, n4, n5) = 6
            var page = await repo.QueryAsync(new HistoryQuery { Limit = 50 }, ct);
            page.TotalCount.ShouldBe(6);

            (await repo.GetAsync(fav1, ct)).ShouldNotBeNull();
            (await repo.GetAsync(fav2, ct)).ShouldNotBeNull();
            (await repo.GetAsync(fav3, ct)).ShouldNotBeNull();

            // 旧非收藏 n1, n2 已被修剪
            (await repo.GetAsync(n1, ct)).ShouldBeNull();
            (await repo.GetAsync(n2, ct)).ShouldBeNull();

            (await repo.GetAsync(n3, ct)).ShouldNotBeNull();
            (await repo.GetAsync(n4, ct)).ShouldNotBeNull();
            (await repo.GetAsync(n5, ct)).ShouldNotBeNull();
        }
    }

    [Fact]
    public async Task SetFavoriteAsync_Unfavoriting_TrimsIfExceedingMaxEntries()
    {
        var ct = TestContext.Current.CancellationToken;
        using var tempDir = new TempDirectory();
        var (repo, initializer, _, settings) = CreateRepository(tempDir.Path);
        using (repo)
        using (initializer)
        {
            settings.Current = settings.Current with
            {
                History = settings.Current.History with { MaxEntries = 2 }
            };

            // 初始：1 收藏，2 非收藏
            var favId = await repo.AddAsync(CreateEntry("Fav", isFavorite: true), ct);
            var n1 = await repo.AddAsync(CreateEntry("NonFav 1", isFavorite: false), ct);
            var n2 = await repo.AddAsync(CreateEntry("NonFav 2", isFavorite: false), ct);

            (await repo.QueryAsync(new HistoryQuery(), ct)).TotalCount.ShouldBe(3);

            // 取消收藏 favId -> 非收藏达到 3 条，超出 MaxEntries=2
            await repo.SetFavoriteAsync(favId, false, ct);

            // 此时最旧的一条非收藏（favId）被修剪，保留最新的 2 条
            var page = await repo.QueryAsync(new HistoryQuery(), ct);
            page.TotalCount.ShouldBe(2);

            (await repo.GetAsync(n1, ct)).ShouldNotBeNull();
            (await repo.GetAsync(n2, ct)).ShouldNotBeNull();
            (await repo.GetAsync(favId, ct)).ShouldBeNull();
        }
    }

    [Fact]
    public async Task SetFavoriteAsync_WhenIdDoesNotExist_IsNoOp_DoesNotTrimOtherNonFavoriteRecords()
    {
        var ct = TestContext.Current.CancellationToken;
        using var tempDir = new TempDirectory();
        var (repo, initializer, _, settings) = CreateRepository(tempDir.Path);
        using (repo)
        using (initializer)
        {
            settings.Current = settings.Current with
            {
                History = settings.Current.History with { MaxEntries = 200 }
            };

            // 写入 101 条非收藏记录
            for (var i = 0; i < 101; i++)
            {
                await repo.AddAsync(CreateEntry($"Record {i}"), ct);
            }

            (await repo.QueryAsync(new HistoryQuery { Limit = 200 }, ct)).TotalCount.ShouldBe(101);

            // 临时调小上限为 100
            settings.Current = settings.Current with
            {
                History = settings.Current.History with { MaxEntries = 100 }
            };

            // 对不存在的 id 执行取消收藏：必须为 no-op，严禁修剪其它记录
            await repo.SetFavoriteAsync(999999, false, ct);

            // 断言依然为 101 条，未被意外修剪
            (await repo.QueryAsync(new HistoryQuery { Limit = 200 }, ct)).TotalCount.ShouldBe(101);
        }
    }

    [Fact]
    public async Task AddAsync_QueuedWrite_ObservesLatestMaxEntries_WhenEnteringWriteLock()
    {
        var ct = TestContext.Current.CancellationToken;
        using var tempDir = new TempDirectory();
        var (repo, initializer, factory, settings) = CreateRepository(tempDir.Path);
        using (repo)
        using (initializer)
        {
            await initializer.InitializeAsync(ct);

            // 初始上限 200，先写入 101 条记录
            settings.Current = settings.Current with
            {
                History = settings.Current.History with { MaxEntries = 200 }
            };
            for (var i = 0; i < 101; i++)
            {
                await repo.AddAsync(CreateEntry($"Initial {i}"), ct);
            }
            (await repo.QueryAsync(new HistoryQuery { Limit = 200 }, ct)).TotalCount.ShouldBe(101);

            using var writer1InsideLock = new ManualResetEventSlim(false);
            using var releaseWriter1 = new ManualResetEventSlim(false);

            factory.OnConnectionOpened = _ =>
            {
                // 仅对第一个写任务拦截并暂停（此时 writer 1 已持有 _writeLock）
                if (!writer1InsideLock.IsSet)
                {
                    writer1InsideLock.Set();
                    releaseWriter1.Wait(TimeSpan.FromSeconds(5), ct);
                }
            };

            try
            {
                // 任务 1：发起写入，在获得锁并打开连接后暂停
                var task1 = Task.Run(() => repo.AddAsync(CreateEntry("Writer 1 record"), ct), ct);

                // 等待任务 1 持锁并暂停
                writer1InsideLock.Wait(TimeSpan.FromSeconds(5), ct).ShouldBeTrue();

                // 任务 2：排队发起写入（此时将等待 _writeLock）
                var task2 = Task.Run(() => repo.AddAsync(CreateEntry("Writer 2 record"), ct), ct);

                // 在任务 2 排队期间，将上限缩减为 100
                settings.Current = settings.Current with
                {
                    History = settings.Current.History with { MaxEntries = 100 }
                };

                // 放行任务 1
                releaseWriter1.Set();

                await Task.WhenAll(task1, task2);

                // 任务 2 获取锁后读取最新上限 100 并执行修剪，最终保留 100 条
                var finalPage = await repo.QueryAsync(new HistoryQuery { Limit = 200 }, ct);
                finalPage.TotalCount.ShouldBe(100);
            }
            finally
            {
                factory.OnConnectionOpened = null;
                releaseWriter1.Set();
            }
        }
    }

    [Fact]
    public async Task SetFavoriteAsync_And_DeleteAsync_Idempotent_And_RejectNonPositiveId()
    {
        var ct = TestContext.Current.CancellationToken;
        using var tempDir = new TempDirectory();
        var (repo, initializer, _, _) = CreateRepository(tempDir.Path);
        using (repo)
        using (initializer)
        {
            await Should.ThrowAsync<ArgumentOutOfRangeException>(async () => await repo.SetFavoriteAsync(0, true, ct));
            await Should.ThrowAsync<ArgumentOutOfRangeException>(async () => await repo.SetFavoriteAsync(-1, true, ct));
            await Should.ThrowAsync<ArgumentOutOfRangeException>(async () => await repo.DeleteAsync(0, ct));
            await Should.ThrowAsync<ArgumentOutOfRangeException>(async () => await repo.DeleteAsync(-5, ct));

            // 对不存在的 id 调用删除和收藏是幂等安全的
            await repo.DeleteAsync(9999, ct);
            await repo.SetFavoriteAsync(9999, true, ct);
        }
    }

    [Fact]
    public async Task DeleteAsync_CascadesToResultsAndFts()
    {
        var ct = TestContext.Current.CancellationToken;
        using var tempDir = new TempDirectory();
        var (repo, initializer, factory, _) = CreateRepository(tempDir.Path);
        using (repo)
        using (initializer)
        {
            var id = await repo.AddAsync(CreateEntry("Text to delete", results: [
                new HistoryResult("p1", "P1", null, "Result 1"),
                new HistoryResult("p2", "P2", null, "Result 2")
            ]), ct);

            (await repo.GetAsync(id, ct)).ShouldNotBeNull();

            // 删除该条目
            await repo.DeleteAsync(id, ct);

            (await repo.GetAsync(id, ct)).ShouldBeNull();

            // 验证子表已被级联删除
            using var conn = factory.CreateConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM translation_result WHERE history_id = @id;";
            cmd.Parameters.AddWithValue("@id", id);
            Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture).ShouldBe(0);

            // 验证 FTS 触发器已清除对应 rowid
            cmd.CommandText = "SELECT COUNT(*) FROM history_fts WHERE rowid = @id;";
            Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture).ShouldBe(0);
        }
    }

    [Fact]
    public async Task ClearAsync_KeepFavorites_And_DoNotKeepFavorites()
    {
        var ct = TestContext.Current.CancellationToken;
        using var tempDir = new TempDirectory();
        var (repo, initializer, _, _) = CreateRepository(tempDir.Path);
        using (repo)
        using (initializer)
        {
            var fav = await repo.AddAsync(CreateEntry("Fav", isFavorite: true), ct);
            var nonFav1 = await repo.AddAsync(CreateEntry("NonFav1", isFavorite: false), ct);
            var nonFav2 = await repo.AddAsync(CreateEntry("NonFav2", isFavorite: false), ct);

            // 清空但保留收藏
            await repo.ClearAsync(keepFavorites: true, ct);

            var page1 = await repo.QueryAsync(new HistoryQuery(), ct);
            page1.TotalCount.ShouldBe(1);
            (await repo.GetAsync(fav, ct)).ShouldNotBeNull();
            (await repo.GetAsync(nonFav1, ct)).ShouldBeNull();
            (await repo.GetAsync(nonFav2, ct)).ShouldBeNull();

            // 清空所有（包含收藏）
            await repo.ClearAsync(keepFavorites: false, ct);

            var page2 = await repo.QueryAsync(new HistoryQuery(), ct);
            page2.TotalCount.ShouldBe(0);
            (await repo.GetAsync(fav, ct)).ShouldBeNull();
        }
    }

    [Fact]
    public async Task QueryAsync_SourcePreview_TruncatesAt200UnicodeScalars_WithEllipsis_AndDoesNotSplitSurrogatePairs()
    {
        var ct = TestContext.Current.CancellationToken;
        using var tempDir = new TempDirectory();
        var (repo, initializer, _, _) = CreateRepository(tempDir.Path);
        using (repo)
        using (initializer)
        {
            // 构造 205 个字符的长文本（包含 200 个汉字 + 5 个汉字）
            var longText = new string('中', 205);
            var id = await repo.AddAsync(CreateEntry(sourceText: longText), ct);

            var page = await repo.QueryAsync(new HistoryQuery(), ct);
            page.Items.Count.ShouldBe(1);

            var summary = page.Items[0];
            summary.SourcePreview.Length.ShouldBe(201); // 200 个汉字 + 1 个省略号
            summary.SourcePreview.EndsWith('…').ShouldBeTrue();
            summary.SourcePreview.Substring(0, 200).ShouldBe(new string('中', 200));

            // 测试刚好 200 标量不加省略号
            var exactText = new string('文', 200);
            await repo.AddAsync(CreateEntry(sourceText: exactText, isFavorite: true), ct);

            var pageExact = await repo.QueryAsync(new HistoryQuery(FavoritesOnly: true), ct);
            pageExact.Items[0].SourcePreview.ShouldBe(exactText);

            // 详情 GetAsync 读取完整原文
            var detail = await repo.GetAsync(id, ct);
            detail.ShouldNotBeNull();
            detail.SourceText.Length.ShouldBe(205);
        }
    }

    [Fact]
    public async Task QueryAsync_PagingAndSorting_StableCreatedAtAndIdDesc()
    {
        var ct = TestContext.Current.CancellationToken;
        using var tempDir = new TempDirectory();
        var (repo, initializer, _, _) = CreateRepository(tempDir.Path);
        using (repo)
        using (initializer)
        {
            // 写入同一秒内的 5 条记录
            var now = DateTimeOffset.UtcNow;
            for (var i = 1; i <= 5; i++)
            {
                await repo.AddAsync(CreateEntry($"Record {i}", createdAt: now, isFavorite: false), ct);
            }

            // 分页查询：第 1 页 2 条
            var p1 = await repo.QueryAsync(new HistoryQuery(Offset: 0, Limit: 2), ct);
            p1.TotalCount.ShouldBe(5);
            p1.Items.Count.ShouldBe(2);
            p1.Items[0].SourcePreview.ShouldBe("Record 5");
            p1.Items[1].SourcePreview.ShouldBe("Record 4");

            // 第 2 页 2 条
            var p2 = await repo.QueryAsync(new HistoryQuery(Offset: 2, Limit: 2), ct);
            p2.TotalCount.ShouldBe(5);
            p2.Items.Count.ShouldBe(2);
            p2.Items[0].SourcePreview.ShouldBe("Record 3");
            p2.Items[1].SourcePreview.ShouldBe("Record 2");

            // 第 3 页 1 条
            var p3 = await repo.QueryAsync(new HistoryQuery(Offset: 4, Limit: 2), ct);
            p3.TotalCount.ShouldBe(5);
            p3.Items.Count.ShouldBe(1);
            p3.Items[0].SourcePreview.ShouldBe("Record 1");
        }
    }

    [Fact]
    public async Task QueryAsync_Search_FtsAndLike_RoutingAndEscaping()
    {
        var ct = TestContext.Current.CancellationToken;
        using var tempDir = new TempDirectory();
        var (repo, initializer, _, _) = CreateRepository(tempDir.Path);
        using (repo)
        using (initializer)
        {
            await repo.AddAsync(CreateEntry("中文长文本测试示例"), ct);
            await repo.AddAsync(CreateEntry("特殊字符 100% 成功率_测试\\路径"), ct);
            await repo.AddAsync(CreateEntry("Emoji 测试 😊🎉🐱 欢迎"), ct);
            await repo.AddAsync(CreateEntry("Short ab"), ct);

            // 1. 标量 >= 3 走 FTS5 trigram
            var r1 = await repo.QueryAsync(new HistoryQuery(Keyword: "长文本"), ct);
            r1.TotalCount.ShouldBe(1);
            r1.Items[0].SourcePreview.ShouldBe("中文长文本测试示例");

            // 2. FTS5 中包含双引号与语法关键字（OR / NEAR 等作为普通字面量）
            var r2 = await repo.QueryAsync(new HistoryQuery(Keyword: "\"测试\" OR NEAR"), ct);
            r2.TotalCount.ShouldBe(0); // 作为纯字面量无匹配，严禁报 FTS 语法错误

            // 3. 包含通配符的短词 (< 3 标量) 走 LIKE 并安全转义
            var r3 = await repo.QueryAsync(new HistoryQuery(Keyword: "100%"), ct);
            r3.TotalCount.ShouldBe(1);

            var r4 = await repo.QueryAsync(new HistoryQuery(Keyword: "率_"), ct);
            r4.TotalCount.ShouldBe(1);

            var r5 = await repo.QueryAsync(new HistoryQuery(Keyword: "测试\\"), ct);
            r5.TotalCount.ShouldBe(1);

            // 4. Emoji 标量检索 (>= 3 标量)
            var r6 = await repo.QueryAsync(new HistoryQuery(Keyword: "😊🎉🐱"), ct);
            r6.TotalCount.ShouldBe(1);

            // 5. NUL 字符非法
            await Should.ThrowAsync<ArgumentException>(async () =>
                await repo.QueryAsync(new HistoryQuery(Keyword: "abc\0def"), ct));
        }
    }

    [Fact]
    public async Task QueryAsync_SearchMatchesResultText_ReturnsSingleHistorySummary()
    {
        var ct = TestContext.Current.CancellationToken;
        using var tempDir = new TempDirectory();
        var (repo, initializer, _, _) = CreateRepository(tempDir.Path);
        using (repo)
        using (initializer)
        {
            await repo.AddAsync(CreateEntry("English original text", results: [
                new HistoryResult("p1", "Provider One", null, "独特的目标语言翻译成果")
            ]), ct);

            // 搜索译文内容 (>= 3 标量走 FTS)
            var page = await repo.QueryAsync(new HistoryQuery(Keyword: "独特的目标语言"), ct);
            page.TotalCount.ShouldBe(1);
            page.Items.Count.ShouldBe(1);
            page.Items[0].SourcePreview.ShouldBe("English original text");
        }
    }

    [Fact]
    public async Task QueryAsync_And_GetAsync_UseDeferredReadTransactions_AllowingConcurrentWriters()
    {
        var ct = TestContext.Current.CancellationToken;
        using var tempDir = new TempDirectory();
        var (repo, initializer, factory, _) = CreateRepository(tempDir.Path);
        using (repo)
        using (initializer)
        {
            await initializer.InitializeAsync(ct);

            // 预先写入 3 条记录
            var id1 = await repo.AddAsync(CreateEntry("Record 1"), ct);
            var id2 = await repo.AddAsync(CreateEntry("Record 2"), ct);
            var id3 = await repo.AddAsync(CreateEntry("Record 3"), ct);

            // 1. 验证 QueryAsync 延迟读事务期间，并发写事务不受阻塞且能顺利提交，且读取结果保持快照一致
            using var querySnapshotAcquired = new ManualResetEventSlim(false);
            using var releaseQueryTx = new ManualResetEventSlim(false);

            repo.OnReadSnapshotAcquired = () =>
            {
                querySnapshotAcquired.Set();
                releaseQueryTx.Wait(TimeSpan.FromSeconds(5), ct);
            };

            var queryTask = Task.Run(() => repo.QueryAsync(new HistoryQuery(), ct), ct);

            // 等待 QueryAsync 读取 TotalCount（锁定快照）并暂停在读取分页数据前
            querySnapshotAcquired.Wait(TimeSpan.FromSeconds(5), ct).ShouldBeTrue();

            // 独立连接启动立即写事务（deferred: false），写入一条新记录
            using (var writeConn = factory.CreateConnection())
            using (var writeTx = writeConn.BeginTransaction(deferred: false))
            {
                writeConn.Execute("""
                    INSERT INTO translation_history (created_at, origin, source_text, source_lang, target_lang, text_hash, is_favorite)
                    VALUES ('2026-09-01T12:00:00.0000000Z', 'Input', 'Concurrent write during query', 'en', 'zh-CN', 'hash_during_query', 0);
                    """, transaction: writeTx);
                writeTx.Commit(); // 必须成功提交，证明读事务未获取写锁（未发生 SQLite code 5）
            }

            // 放行 QueryAsync
            releaseQueryTx.Set();
            var queryResult = await queryTask;

            // 读快照保持一致：TotalCount 与 Items 均来自快照，不受并发写入的第 4 条数据影响
            queryResult.TotalCount.ShouldBe(3);
            queryResult.Items.Count.ShouldBe(3);
            queryResult.Items.Any(r => r.SourcePreview.Contains("Concurrent write")).ShouldBeFalse();

            // 2. 验证 GetAsync 延迟读事务期间，并发写事务同样不受阻塞，且头与子结果来自同一快照
            using var getSnapshotAcquired = new ManualResetEventSlim(false);
            using var releaseGetTx = new ManualResetEventSlim(false);

            repo.OnReadSnapshotAcquired = () =>
            {
                getSnapshotAcquired.Set();
                releaseGetTx.Wait(TimeSpan.FromSeconds(5), ct);
            };

            var getTask = Task.Run(() => repo.GetAsync(id1, ct), ct);

            getSnapshotAcquired.Wait(TimeSpan.FromSeconds(5), ct).ShouldBeTrue();

            using (var writeConn2 = factory.CreateConnection())
            using (var writeTx2 = writeConn2.BeginTransaction(deferred: false))
            {
                // 在读取结果前，向 id1 插入一条额外结果
                writeConn2.Execute("""
                    INSERT INTO translation_result (history_id, sort_order, provider_id, provider_name, model, result_text, is_truncated)
                    VALUES (@historyId, 99, 'p_late', 'Late Provider', null, 'Late result text', 0);
                    """, new { historyId = id1 }, transaction: writeTx2);
                writeTx2.Commit(); // 必须成功提交
            }

            releaseGetTx.Set();
            var getResult = await getTask;
            getResult.ShouldNotBeNull();
            getResult.Id.ShouldBe(id1);
            // 结果列表仍保持读快照状态（只有初始的 1 条结果，不受并发插入影响）
            getResult.Results.Count.ShouldBe(1);
            getResult.Results.Any(r => r.ProviderId == "p_late").ShouldBeFalse();

            repo.OnReadSnapshotAcquired = null;
        }
    }

    [Fact]
    public async Task Transactions_CancellationBeforeInvocation_RollsBack_AndLeavesNoData()
    {
        var ct = TestContext.Current.CancellationToken;
        using var tempDir = new TempDirectory();
        var (repo, initializer, factory, _) = CreateRepository(tempDir.Path);
        using (repo)
        using (initializer)
        {
            await initializer.InitializeAsync(ct);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.Cancel();

            await Should.ThrowAsync<OperationCanceledException>(async () =>
            {
                await repo.AddAsync(CreateEntry("Cancelled before invocation"), cts.Token);
            });

            // 验证未写入任何记录
            using var conn = factory.CreateConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM translation_history;";
            Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture).ShouldBe(0);

            cmd.CommandText = "SELECT COUNT(*) FROM translation_result;";
            Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture).ShouldBe(0);

            cmd.CommandText = "SELECT COUNT(*) FROM history_fts;";
            Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture).ShouldBe(0);
        }
    }

    [Fact]
    public async Task Transactions_InFlightCancellation_DuringExecution_RollsBack_AndLeavesNoData()
    {
        var ct = TestContext.Current.CancellationToken;
        using var tempDir = new TempDirectory();
        var (repo, initializer, factory, _) = CreateRepository(tempDir.Path);
        using (repo)
        using (initializer)
        {
            await initializer.InitializeAsync(ct);

            using var inFlightCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

            // 注入测试函数与触发器：在主表插入已完成、子表即将插入时触发取消
            factory.OnConnectionCreated = conn =>
            {
                conn.CreateFunction("test_cancel_in_flight", () =>
                {
                    inFlightCts.Cancel();
                    return 1;
                });
            };

            using (var setupConn = factory.CreateConnection())
            {
                using var cmd = setupConn.CreateCommand();
                cmd.CommandText = """
                    CREATE TRIGGER trg_test_cancel_in_flight
                    BEFORE INSERT ON translation_result
                    BEGIN
                        SELECT test_cancel_in_flight();
                    END;
                    """;
                cmd.ExecuteNonQuery();
            }

            // 发起写入：主表 INSERT 已执行，子表插入时触发 cancel，随后应被 OCE 捕获并回滚
            await Should.ThrowAsync<OperationCanceledException>(async () =>
            {
                await repo.AddAsync(CreateEntry("In-flight cancel record"), inFlightCts.Token);
            });

            // 验证事务完整回滚：主表、子表、FTS 全无半条残留记录
            using (var checkConn = factory.CreateConnection())
            {
                using var cmd = checkConn.CreateCommand();
                cmd.CommandText = "SELECT COUNT(*) FROM translation_history;";
                Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture).ShouldBe(0);

                cmd.CommandText = "SELECT COUNT(*) FROM translation_result;";
                Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture).ShouldBe(0);

                cmd.CommandText = "SELECT COUNT(*) FROM history_fts;";
                Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture).ShouldBe(0);
            }

            factory.OnConnectionCreated = null;
        }
    }

    [Fact]
    public async Task Transactions_MidOperationSqlFailure_RollsBack_LeavesNoPartialRecords_AndSanitizesException()
    {
        var ct = TestContext.Current.CancellationToken;
        using var tempDir = new TempDirectory();
        var (repo, initializer, factory, _) = CreateRepository(tempDir.Path);
        using (repo)
        using (initializer)
        {
            await initializer.InitializeAsync(ct);

            // 在 translation_result 插入前注入受控失败触发器：抛出假敏感值
            const string fakeSecret = "SUPER_SECRET_TOKEN_DO_NOT_LEAK_987654";
            using (var conn = factory.CreateConnection())
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = $"""
                    CREATE TRIGGER trg_test_abort
                    BEFORE INSERT ON translation_result
                    BEGIN
                        SELECT RAISE(ABORT, '{fakeSecret}');
                    END;
                    """;
                cmd.ExecuteNonQuery();
            }

            // 尝试写入：主表 INSERT 成功后，子表 INSERT 会触发 RAISE(ABORT)
            var ex = await Should.ThrowAsync<IrisException>(async () =>
            {
                await repo.AddAsync(CreateEntry("Will fail midway"), ct);
            });

            ex.Code.ShouldBe(ErrorCode.StorageFailure);

            // 严格验证异常隐私安全：Message、Detail、ToString() 及整个异常链绝不包含敏感值
            ex.Message.Contains(fakeSecret).ShouldBeFalse();
            ex.ToString().Contains(fakeSecret).ShouldBeFalse();
            (ex.Detail?.Contains(fakeSecret) == true).ShouldBeFalse();
            ex.InnerException.ShouldBeNull(); // 绝不泄漏原始 SqliteException

            // 验证事务完整回滚：主表、子表、FTS 全无半条孤儿记录
            using (var checkConn = factory.CreateConnection())
            {
                using var checkCmd = checkConn.CreateCommand();
                checkCmd.CommandText = "SELECT COUNT(*) FROM translation_history;";
                Convert.ToInt64(checkCmd.ExecuteScalar(), CultureInfo.InvariantCulture).ShouldBe(0);

                checkCmd.CommandText = "SELECT COUNT(*) FROM translation_result;";
                Convert.ToInt64(checkCmd.ExecuteScalar(), CultureInfo.InvariantCulture).ShouldBe(0);

                checkCmd.CommandText = "SELECT COUNT(*) FROM history_fts;";
                Convert.ToInt64(checkCmd.ExecuteScalar(), CultureInfo.InvariantCulture).ShouldBe(0);
            }
        }
    }

    [Fact]
    public async Task ExternalLockConflict_DuringOperation_ThrowsSafeStorageFailure_WithoutLeakingSqliteException()
    {
        var ct = TestContext.Current.CancellationToken;
        using var tempDir = new TempDirectory();
        var (repo, initializer, factory, _) = CreateRepository(tempDir.Path);
        using (repo)
        using (initializer)
        {
            await initializer.InitializeAsync(ct);

            // 外部连接开启独占锁
            using var externalConn = factory.CreateConnection();
            using var externalTx = externalConn.BeginTransaction(deferred: false);
            using var lockCmd = externalConn.CreateCommand();
            lockCmd.Transaction = externalTx;
            lockCmd.CommandText = "PRAGMA locking_mode = EXCLUSIVE; SELECT COUNT(*) FROM translation_history;";
            lockCmd.ExecuteNonQuery();

            using var shortCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
            var ex = await Should.ThrowAsync<IrisException>(async () =>
            {
                await repo.AddAsync(CreateEntry("Lock conflict entry"), shortCts.Token);
            });

            ex.Code.ShouldBe(ErrorCode.StorageFailure);
            ex.InnerException.ShouldBeNull();
        }
    }

    [Fact]
    public void Threading_PublicOperationExecutesOnBackgroundTask()
    {
        var ct = TestContext.Current.CancellationToken;
        using var tempDir = new TempDirectory();
        var (repo, initializer, factory, _) = CreateRepository(tempDir.Path);
        using (repo)
        using (initializer)
        {
            int callerThreadId = 0;
            var sqlThreadIds = new HashSet<int>();
            var isThreadPoolList = new List<bool>();

            factory.OnConnectionOpened = tid =>
            {
                lock (sqlThreadIds)
                {
                    sqlThreadIds.Add(tid);
                    isThreadPoolList.Add(Thread.CurrentThread.IsThreadPoolThread);
                }
            };

            // 使用专用调用线程（非线程池工作线程）发起公开仓储调用，验证底层操作均切换至后台线程池
            var dedicatedThread = new Thread(() =>
            {
                callerThreadId = Environment.CurrentManagedThreadId;
                var id = repo.AddAsync(CreateEntry("Thread test"), ct).GetAwaiter().GetResult();
                var retrieved = repo.GetAsync(id, ct).GetAwaiter().GetResult();
                retrieved.ShouldNotBeNull();
            })
            {
                IsBackground = true,
                Name = "DedicatedCallerThread"
            };

            dedicatedThread.Start();
            dedicatedThread.Join(TimeSpan.FromSeconds(10)).ShouldBeTrue();

            // 严格断言打开连接与执行 SQL 的线程均为后台工作线程，非当前专用调用线程
            callerThreadId.ShouldBeGreaterThan(0);
            sqlThreadIds.Count.ShouldBeGreaterThanOrEqualTo(1);
            foreach (var tid in sqlThreadIds)
            {
                tid.ShouldNotBe(callerThreadId);
            }

            isThreadPoolList.All(isTp => isTp).ShouldBeTrue();

            factory.OnConnectionOpened = null;
        }
    }
}
