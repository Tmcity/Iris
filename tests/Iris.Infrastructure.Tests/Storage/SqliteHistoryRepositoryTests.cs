using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
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
                    SourceText = "test",
                    SourceLanguage = "en",
                    TargetLanguage = "zh-CN",
                    Results = null!
                }, ct));

            // Results 元素为 null
            await Should.ThrowAsync<ArgumentException>(async () =>
                await repo.AddAsync(CreateEntry(results: [null!]), ct));

            // ProviderId 为空白
            await Should.ThrowAsync<ArgumentException>(async () =>
                await repo.AddAsync(CreateEntry(results: [new HistoryResult("", "P1", "m", "text")]), ct));

            // ProviderName 为空白
            await Should.ThrowAsync<ArgumentException>(async () =>
                await repo.AddAsync(CreateEntry(results: [new HistoryResult("p1", "   ", "m", "text")]), ct));

            // 结果文本为 null
            await Should.ThrowAsync<ArgumentException>(async () =>
                await repo.AddAsync(CreateEntry(results: [new HistoryResult("p1", "P1", "m", null!)]), ct));

            // 重复 ProviderId
            await Should.ThrowAsync<ArgumentException>(async () =>
                await repo.AddAsync(CreateEntry(results:
                [
                    new HistoryResult("p1", "P1", "m", "text1"),
                    new HistoryResult("p1", "P1", "m", "text2")
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
            var createdTime = new DateTimeOffset(2026, 9, 29, 14, 30, 0, TimeSpan.FromHours(8));
            var entry = CreateEntry(
                sourceText: "Test text",
                createdAt: createdTime,
                results:
                [
                    new HistoryResult("p1", "Provider One", "m1", "测试文本 1") { Truncated = false },
                    new HistoryResult("p2", "Provider Two", null, "测试文本 2") { Truncated = true }
                ]);

            var id = await repo.AddAsync(entry, ct);
            id.ShouldBeGreaterThan(0);

            var retrieved = await repo.GetAsync(id, ct);
            retrieved.ShouldNotBeNull();
            retrieved.Id.ShouldBe(id);
            retrieved.SourceText.ShouldBe("Test text");
            retrieved.SourceLanguage.ShouldBe("en");
            retrieved.TargetLanguage.ShouldBe("zh-CN");
            retrieved.CreatedAt.ToUniversalTime().ShouldBe(createdTime.ToUniversalTime());
            retrieved.Results.Count.ShouldBe(2);

            retrieved.Results[0].ProviderId.ShouldBe("p1");
            retrieved.Results[0].Text.ShouldBe("测试文本 1");
            retrieved.Results[0].Truncated.ShouldBeFalse();

            retrieved.Results[1].ProviderId.ShouldBe("p2");
            retrieved.Results[1].Model.ShouldBeNull();
            retrieved.Results[1].Text.ShouldBe("测试文本 2");
            retrieved.Results[1].Truncated.ShouldBeTrue();
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
            var time1 = new DateTimeOffset(2026, 9, 29, 10, 0, 0, TimeSpan.Zero);
            var entry1 = CreateEntry(
                sourceText: "Duplicate text",
                sourceLang: "en",
                targetLang: "zh-CN",
                origin: TextOrigin.Input,
                isFavorite: false,
                createdAt: time1,
                results: [new HistoryResult("p1", "P1", "m1", "Old translation")]);

            var id1 = await repo.AddAsync(entry1, ct);

            // 再次添加相同内容（不同时间、不同来源、更新结果），最新记录未收藏
            var time2 = new DateTimeOffset(2026, 9, 29, 10, 5, 0, TimeSpan.Zero);
            var entry2 = CreateEntry(
                sourceText: "Duplicate text",
                sourceLang: "en",
                targetLang: "zh-CN",
                origin: TextOrigin.Clipboard,
                isFavorite: false,
                createdAt: time2,
                results: [new HistoryResult("p1", "P1", "m1", "Updated translation")]);

            var id2 = await repo.AddAsync(entry2, ct);

            // 去重更新：保留原 Id
            id2.ShouldBe(id1);

            var retrieved = await repo.GetAsync(id1, ct);
            retrieved.ShouldNotBeNull();
            retrieved.CreatedAt.ShouldBe(time2);
            retrieved.Origin.ShouldBe(TextOrigin.Clipboard);
            retrieved.Results.Count.ShouldBe(1);
            retrieved.Results[0].Text.ShouldBe("Updated translation");

            // 总数仍为 1
            var page = await repo.QueryAsync(new HistoryQuery(), ct);
            page.TotalCount.ShouldBe(1);
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
            var text = "Same source text";

            var id1 = await repo.AddAsync(CreateEntry(sourceText: text, sourceLang: "en", targetLang: "zh-CN"), ct);
            var id2 = await repo.AddAsync(CreateEntry(sourceText: text, sourceLang: "auto", targetLang: "zh-CN"), ct);
            var id3 = await repo.AddAsync(CreateEntry(sourceText: text, sourceLang: "en", targetLang: "ja"), ct);

            id2.ShouldNotBe(id1);
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
            var entry1 = CreateEntry(sourceText: "Favorite protection text", isFavorite: true);
            var id1 = await repo.AddAsync(entry1, ct);

            // 再次添加完全相同内容
            var entry2 = CreateEntry(sourceText: "Favorite protection text", isFavorite: false);
            var id2 = await repo.AddAsync(entry2, ct);

            id2.ShouldNotBe(id1);

            var first = await repo.GetAsync(id1, ct);
            first.ShouldNotBeNull();
            first.IsFavorite.ShouldBeTrue();

            var second = await repo.GetAsync(id2, ct);
            second.ShouldNotBeNull();
            second.IsFavorite.ShouldBeFalse();

            var page = await repo.QueryAsync(new HistoryQuery(), ct);
            page.TotalCount.ShouldBe(2);
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
            var mutableResults = new List<HistoryResult>
            {
                new("p1", "P1", "m1", "Original text 1"),
                new("p2", "P2", "m2", "Original text 2")
            };

            var entry = new HistoryEntry
            {
                SourceText = "Boundary test",
                SourceLanguage = "en",
                TargetLanguage = "zh-CN",
                Results = mutableResults
            };

            var id = await repo.AddAsync(entry, ct);

            // 调用方改动原列表
            mutableResults.Clear();
            mutableResults.Add(new HistoryResult("p3", "P3", "m3", "Tampered"));

            // 验证存储内容不受影响
            var retrieved = await repo.GetAsync(id, ct);
            retrieved.ShouldNotBeNull();
            retrieved.Results.Count.ShouldBe(2);
            retrieved.Results[0].Text.ShouldBe("Original text 1");
            retrieved.Results[1].Text.ShouldBe("Original text 2");
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
            // 设定最大保留 3 条非收藏
            settings.Current = settings.Current with
            {
                History = settings.Current.History with { MaxEntries = 3 }
            };

            // 写入 2 条收藏
            var fav1 = await repo.AddAsync(CreateEntry("Fav 1", isFavorite: true), ct);
            var fav2 = await repo.AddAsync(CreateEntry("Fav 2", isFavorite: true), ct);

            // 写入 5 条非收藏
            var nonFavIds = new List<long>();
            for (var i = 1; i <= 5; i++)
            {
                var id = await repo.AddAsync(CreateEntry($"NonFav {i}", isFavorite: false), ct);
                nonFavIds.Add(id);
            }

            // 验证：2 条收藏全部保留
            (await repo.GetAsync(fav1, ct)).ShouldNotBeNull();
            (await repo.GetAsync(fav2, ct)).ShouldNotBeNull();

            // 最旧的 2 条非收藏被修剪
            (await repo.GetAsync(nonFavIds[0], ct)).ShouldBeNull();
            (await repo.GetAsync(nonFavIds[1], ct)).ShouldBeNull();

            // 最新的 3 条非收藏保留
            (await repo.GetAsync(nonFavIds[2], ct)).ShouldNotBeNull();
            (await repo.GetAsync(nonFavIds[3], ct)).ShouldNotBeNull();
            (await repo.GetAsync(nonFavIds[4], ct)).ShouldNotBeNull();

            // 总数为 2 收藏 + 3 非收藏 = 5
            var page = await repo.QueryAsync(new HistoryQuery(Limit: 50), ct);
            page.TotalCount.ShouldBe(5);
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
    public async Task SetFavoriteAsync_And_DeleteAsync_Idempotent_And_RejectNonPositiveId()
    {
        var ct = TestContext.Current.CancellationToken;
        using var tempDir = new TempDirectory();
        var (repo, initializer, _, _) = CreateRepository(tempDir.Path);
        using (repo)
        using (initializer)
        {
            // 非正 ID 抛出 ArgumentOutOfRangeException
            await Should.ThrowAsync<ArgumentOutOfRangeException>(async () => await repo.SetFavoriteAsync(0, true, ct));
            await Should.ThrowAsync<ArgumentOutOfRangeException>(async () => await repo.SetFavoriteAsync(-1, true, ct));
            await Should.ThrowAsync<ArgumentOutOfRangeException>(async () => await repo.DeleteAsync(0, ct));
            await Should.ThrowAsync<ArgumentOutOfRangeException>(async () => await repo.DeleteAsync(-5, ct));
            await Should.ThrowAsync<ArgumentOutOfRangeException>(async () => await repo.GetAsync(0, ct));

            // 不存在的正 ID 幂等无异常
            await repo.SetFavoriteAsync(999999, true, ct);
            await repo.DeleteAsync(999999, ct);
            (await repo.GetAsync(999999, ct)).ShouldBeNull();
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
            var id = await repo.AddAsync(CreateEntry("Cascade test text"), ct);

            // 删除该条历史
            await repo.DeleteAsync(id, ct);

            // 主表无该记录
            (await repo.GetAsync(id, ct)).ShouldBeNull();

            // 直接查询 SQLite 验证子结果级联清理、触发器清理 FTS
            using var conn = factory.CreateConnection();
            using var cmd = conn.CreateCommand();

            cmd.CommandText = "SELECT COUNT(*) FROM translation_result WHERE history_id = @id;";
            cmd.Parameters.AddWithValue("@id", id);
            Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture).ShouldBe(0);

            cmd.CommandText = "SELECT COUNT(*) FROM history_fts WHERE rowid = @id;";
            Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture).ShouldBe(0);
        }
    }

    [Fact]
    public async Task ClearAsync_KeepFavorites_And_DoNotKeepFavorites()
    {
        var ct = TestContext.Current.CancellationToken;
        using var tempDir = new TempDirectory();
        var (repo, initializer, factory, _) = CreateRepository(tempDir.Path);
        using (repo)
        using (initializer)
        {
            var f1 = await repo.AddAsync(CreateEntry("Fav 1", isFavorite: true), ct);
            var f2 = await repo.AddAsync(CreateEntry("Fav 2", isFavorite: true), ct);
            var n1 = await repo.AddAsync(CreateEntry("NonFav 1", isFavorite: false), ct);
            var n2 = await repo.AddAsync(CreateEntry("NonFav 2", isFavorite: false), ct);

            // 1. 保留收藏清空
            await repo.ClearAsync(keepFavorites: true, ct);

            var pageFavOnly = await repo.QueryAsync(new HistoryQuery(), ct);
            pageFavOnly.TotalCount.ShouldBe(2);
            (await repo.GetAsync(f1, ct)).ShouldNotBeNull();
            (await repo.GetAsync(f2, ct)).ShouldNotBeNull();
            (await repo.GetAsync(n1, ct)).ShouldBeNull();
            (await repo.GetAsync(n2, ct)).ShouldBeNull();

            // 2. 全部清空
            await repo.ClearAsync(keepFavorites: false, ct);

            var pageEmpty = await repo.QueryAsync(new HistoryQuery(), ct);
            pageEmpty.TotalCount.ShouldBe(0);

            // 验证 FTS 彻底清空
            using var conn = factory.CreateConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM history_fts;";
            Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture).ShouldBe(0);
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
            // 构造 205 个 emoji（每个 emoji 占 2 个 UTF-16 code units / 1 个 Unicode scalar）
            var sb = new StringBuilder();
            for (var i = 0; i < 205; i++)
            {
                sb.Append("🙂");
            }
            var emojiText = sb.ToString();

            var id = await repo.AddAsync(CreateEntry(sourceText: emojiText), ct);

            var page = await repo.QueryAsync(new HistoryQuery(), ct);
            page.Items.Count.ShouldBe(1);

            var preview = page.Items[0].SourcePreview;
            var previewRunes = preview.EnumerateRunes().ToList();

            // 前 200 个为 emoji，第 201 个为省略号 '…'
            previewRunes.Count.ShouldBe(201);
            previewRunes[^1].Value.ShouldBe('…');
            for (var i = 0; i < 200; i++)
            {
                previewRunes[i].Value.ShouldBe(new Rune(0x1F642).Value);
            }

            // 验证 GetAsync 返回完整原文
            var detail = await repo.GetAsync(id, ct);
            detail.ShouldNotBeNull();
            detail.SourceText.Length.ShouldBe(emojiText.Length);
            detail.SourceText.EnumerateRunes().Count().ShouldBe(205);
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
            var baseTime = new DateTimeOffset(2026, 9, 29, 10, 0, 0, TimeSpan.Zero);
            var ids = new List<long>();
            for (var i = 1; i <= 5; i++)
            {
                var id = await repo.AddAsync(CreateEntry($"Item {i}", createdAt: baseTime.AddMinutes(i)), ct);
                ids.Add(id);
            }

            // Page 1: Offset = 0, Limit = 2 -> 应该是最新添加的 5 和 4
            var p1 = await repo.QueryAsync(new HistoryQuery(Offset: 0, Limit: 2), ct);
            p1.TotalCount.ShouldBe(5);
            p1.Items.Count.ShouldBe(2);
            p1.Items[0].Id.ShouldBe(ids[4]);
            p1.Items[1].Id.ShouldBe(ids[3]);

            // Page 2: Offset = 2, Limit = 2 -> 3 和 2
            var p2 = await repo.QueryAsync(new HistoryQuery(Offset: 2, Limit: 2), ct);
            p2.Items.Count.ShouldBe(2);
            p2.Items[0].Id.ShouldBe(ids[2]);
            p2.Items[1].Id.ShouldBe(ids[1]);

            // Page 3: Offset = 4, Limit = 2 -> 1
            var p3 = await repo.QueryAsync(new HistoryQuery(Offset: 4, Limit: 2), ct);
            p3.Items.Count.ShouldBe(1);
            p3.Items[0].Id.ShouldBe(ids[0]);
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
            // 插入若干特殊条目
            await repo.AddAsync(CreateEntry("中文检索测试内容"), ct);
            await repo.AddAsync(CreateEntry("Emoji🙂🙂🙂表情"), ct);
            await repo.AddAsync(CreateEntry("literal test OR boolean"), ct);
            await repo.AddAsync(CreateEntry("test AND not matched"), ct);
            await repo.AddAsync(CreateEntry("has quote\" inside"), ct);
            await repo.AddAsync(CreateEntry("exact 50% discount"), ct);
            await repo.AddAsync(CreateEntry("under_score value"), ct);
            await repo.AddAsync(CreateEntry(@"back\slash value"), ct);

            // 1. FTS5 中文检索 (>= 3 Unicode 标量)
            var r1 = await repo.QueryAsync(new HistoryQuery(Keyword: "检索测试"), ct);
            r1.TotalCount.ShouldBe(1);

            // 2. FTS5 Emoji (>= 3 标量)
            var r2 = await repo.QueryAsync(new HistoryQuery(Keyword: "🙂🙂🙂"), ct);
            r2.TotalCount.ShouldBe(1);

            // 3. FTS5 字面量 "test OR"（不应解析为布尔 OR，仅匹配包含 "test OR" 的条目）
            var r3 = await repo.QueryAsync(new HistoryQuery(Keyword: "test OR"), ct);
            r3.TotalCount.ShouldBe(1);
            r3.Items[0].SourcePreview.ShouldContain("literal test OR boolean");

            // 4. FTS5 双引号
            var r4 = await repo.QueryAsync(new HistoryQuery(Keyword: "quote\""), ct);
            r4.TotalCount.ShouldBe(1);

            // 5. LIKE 通配符 % 转义 (< 3 标量)
            var r5 = await repo.QueryAsync(new HistoryQuery(Keyword: "%"), ct);
            r5.TotalCount.ShouldBe(1);
            r5.Items[0].SourcePreview.ShouldContain("50%");

            // 6. LIKE 通配符 _ 转义 (< 3 标量)
            var r6 = await repo.QueryAsync(new HistoryQuery(Keyword: "_"), ct);
            r6.TotalCount.ShouldBe(1);
            r6.Items[0].SourcePreview.ShouldContain("under_score");

            // 7. LIKE 反斜杠 转义 (< 3 标量)
            var r7 = await repo.QueryAsync(new HistoryQuery(Keyword: @"\"), ct);
            r7.TotalCount.ShouldBe(1);
            r7.Items[0].SourcePreview.ShouldContain(@"back\slash");

            // 8. LIKE 2 字符短中文
            var r8 = await repo.QueryAsync(new HistoryQuery(Keyword: "中文"), ct);
            r8.TotalCount.ShouldBe(1);

            // 9. NUL 字符非法
            await Should.ThrowAsync<ArgumentException>(async () =>
                await repo.QueryAsync(new HistoryQuery(Keyword: "foo\0bar"), ct));
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
            // 原文中不含 target_match，但在两个 Provider 结果中都包含
            var entry = CreateEntry(
                sourceText: "Common source text",
                results:
                [
                    new HistoryResult("p1", "P1", "m", "target_match in result 1"),
                    new HistoryResult("p2", "P2", "m", "target_match in result 2")
                ]);

            await repo.AddAsync(entry, ct);

            // FTS 搜索 target_match
            var page = await repo.QueryAsync(new HistoryQuery(Keyword: "target_match"), ct);
            page.TotalCount.ShouldBe(1);
            page.Items.Count.ShouldBe(1);
        }
    }

    [Fact]
    public async Task Transactions_CancellationBeforeCommit_RollsBack_AndLeavesNoData()
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
                await repo.AddAsync(CreateEntry("Cancelled before commit"), cts.Token);
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
    public async Task Transactions_MidOperationSqlFailure_RollsBack_LeavesNoPartialRecords()
    {
        var ct = TestContext.Current.CancellationToken;
        using var tempDir = new TempDirectory();
        var (repo, initializer, factory, _) = CreateRepository(tempDir.Path);
        using (repo)
        using (initializer)
        {
            await initializer.InitializeAsync(ct);

            // 在 translation_result 插入前注入受控失败触发器：模拟主表插入成功后、子表插入时发生 SQL 异常
            using (var conn = factory.CreateConnection())
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = """
                    CREATE TRIGGER trg_test_abort
                    BEFORE INSERT ON translation_result
                    BEGIN
                        SELECT RAISE(ABORT, 'Simulated mid-transaction failure');
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
    public async Task Threading_PublicOperationExecutesOnBackgroundTask()
    {
        var ct = TestContext.Current.CancellationToken;
        using var tempDir = new TempDirectory();
        var (repo, initializer, _, _) = CreateRepository(tempDir.Path);
        using (repo)
        using (initializer)
        {
            var callerThreadId = Environment.CurrentManagedThreadId;
            var id = await repo.AddAsync(CreateEntry("Thread test"), ct);

            // 后台执行验证：通过查询确保顺利返回
            var retrieved = await repo.GetAsync(id, ct);
            retrieved.ShouldNotBeNull();
        }
    }
}
