using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Iris.Core.Settings;
using Iris.Core.Translation;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Iris.Core.Tests.Translation;

public sealed class LruTranslationCacheTests
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

        public void TriggerChanged()
        {
            Changed?.Invoke(this, new SettingsChangedEventArgs(Current, Current));
        }
    }

    private static TranslationCacheKey CreateKey(string providerId = "p1", Guid? instanceId = null, string? fingerprint = null)
    {
        return new TranslationCacheKey(
            providerId,
            instanceId ?? Guid.NewGuid(),
            fingerprint ?? new string('a', 64));
    }

    [Fact]
    public void TryGet_WhenEmpty_ReturnsFalseAndNullText()
    {
        var settingsService = new FakeSettingsService();
        using var cache = new LruTranslationCache(settingsService);

        var key = CreateKey();
        var hit = cache.TryGet(key, out var text);

        hit.ShouldBeFalse();
        text.ShouldBeNull();
    }

    [Fact]
    public void TrySet_AndTryGet_ReturnsTrueAndCachedText()
    {
        var settingsService = new FakeSettingsService();
        using var cache = new LruTranslationCache(settingsService);

        var key = CreateKey();
        var gen = cache.Generation;

        var setSuccess = cache.TrySet(key, "translated text", gen);
        setSuccess.ShouldBeTrue();

        var hit = cache.TryGet(key, out var text);
        hit.ShouldBeTrue();
        text.ShouldBe("translated text");
    }

    [Fact]
    public void TrySet_WithNullText_ThrowsArgumentNullException()
    {
        var settingsService = new FakeSettingsService();
        using var cache = new LruTranslationCache(settingsService);

        var key = CreateKey();
        Should.Throw<ArgumentNullException>(() => cache.TrySet(key, null!, cache.Generation));
    }

    [Fact]
    public void TrySet_WithEmptyString_IsAllowedAndCached()
    {
        var settingsService = new FakeSettingsService();
        using var cache = new LruTranslationCache(settingsService);

        var key = CreateKey();
        cache.TrySet(key, string.Empty, cache.Generation).ShouldBeTrue();

        cache.TryGet(key, out var text).ShouldBeTrue();
        text.ShouldBe(string.Empty);
    }

    [Fact]
    public void LRU_CapacityEviction_EvictsOldestItem()
    {
        var settingsService = new FakeSettingsService();
        settingsService.Current = settingsService.Current with
        {
            Translation = settingsService.Current.Translation with { CacheCapacity = 3 }
        };
        using var cache = new LruTranslationCache(settingsService);

        var k1 = CreateKey("p1");
        var k2 = CreateKey("p2");
        var k3 = CreateKey("p3");
        var k4 = CreateKey("p4");

        var g = cache.Generation;
        cache.TrySet(k1, "1", g).ShouldBeTrue();
        cache.TrySet(k2, "2", g).ShouldBeTrue();
        cache.TrySet(k3, "3", g).ShouldBeTrue();

        cache.Keys.ShouldBe(new[] { k3, k2, k1 });

        // 加入第 4 个条目，应逐出最旧的 k1
        cache.TrySet(k4, "4", g).ShouldBeTrue();

        cache.Count.ShouldBe(3);
        cache.Keys.ShouldBe(new[] { k4, k3, k2 });
        cache.TryGet(k1, out _).ShouldBeFalse();
        cache.TryGet(k4, out var t4).ShouldBeTrue();
        t4.ShouldBe("4");
    }

    [Fact]
    public void LRU_Hit_PromotesToMru()
    {
        var settingsService = new FakeSettingsService();
        settingsService.Current = settingsService.Current with
        {
            Translation = settingsService.Current.Translation with { CacheCapacity = 3 }
        };
        using var cache = new LruTranslationCache(settingsService);

        var k1 = CreateKey("p1");
        var k2 = CreateKey("p2");
        var k3 = CreateKey("p3");
        var k4 = CreateKey("p4");

        var g = cache.Generation;
        cache.TrySet(k1, "1", g);
        cache.TrySet(k2, "2", g);
        cache.TrySet(k3, "3", g);

        // 命中 k1，k1 应提升为 MRU
        cache.TryGet(k1, out _).ShouldBeTrue();
        cache.Keys.ShouldBe(new[] { k1, k3, k2 });

        // 此时写入 k4，被逐出的应是尾部的 k2，而不是已提升的 k1
        cache.TrySet(k4, "4", g);
        cache.Keys.ShouldBe(new[] { k4, k1, k3 });
        cache.TryGet(k2, out _).ShouldBeFalse();
    }

    [Fact]
    public void LRU_Miss_DoesNotAlterOrder()
    {
        var settingsService = new FakeSettingsService();
        settingsService.Current = settingsService.Current with
        {
            Translation = settingsService.Current.Translation with { CacheCapacity = 2 }
        };
        using var cache = new LruTranslationCache(settingsService);

        var k1 = CreateKey("p1");
        var k2 = CreateKey("p2");
        var kMiss = CreateKey("p_miss");

        var g = cache.Generation;
        cache.TrySet(k1, "1", g);
        cache.TrySet(k2, "2", g);

        cache.Keys.ShouldBe(new[] { k2, k1 });

        cache.TryGet(kMiss, out _).ShouldBeFalse();
        cache.Keys.ShouldBe(new[] { k2, k1 });
    }

    [Fact]
    public void LRU_Overwrite_PromotesToMru_AndUpdatesCost()
    {
        var settingsService = new FakeSettingsService();
        settingsService.Current = settingsService.Current with
        {
            Translation = settingsService.Current.Translation with { CacheCapacity = 3 }
        };
        using var cache = new LruTranslationCache(settingsService);

        var k1 = CreateKey("p1");
        var k2 = CreateKey("p2");
        var k3 = CreateKey("p3");

        var g = cache.Generation;
        cache.TrySet(k1, "1", g);
        cache.TrySet(k2, "2", g);
        cache.TrySet(k3, "3", g);

        var bytesBefore = cache.CurrentTotalBytes;

        // 覆盖 k1
        cache.TrySet(k1, "1_longer_text", g).ShouldBeTrue();

        cache.Keys.ShouldBe(new[] { k1, k3, k2 });
        cache.TryGet(k1, out var t1).ShouldBeTrue();
        t1.ShouldBe("1_longer_text");

        // 验证成本差值精确更新：增加的字符数 * 2
        var expectedDiff = 2L * ("1_longer_text".Length - "1".Length);
        (cache.CurrentTotalBytes - bytesBefore).ShouldBe(expectedDiff);
    }

    [Fact]
    public void CostAccounting_ExactCostMatchesFormula()
    {
        var settingsService = new FakeSettingsService();
        using var cache = new LruTranslationCache(settingsService);

        var key = new TranslationCacheKey("my-provider", Guid.NewGuid(), new string('f', 64));
        var text = "Hello world";

        cache.TrySet(key, text, cache.Generation).ShouldBeTrue();

        long expectedCost = 256L + 2L * (key.ProviderId.Length + key.RequestFingerprint.Length + text.Length);
        cache.CurrentTotalBytes.ShouldBe(expectedCost);
    }

    [Fact]
    public void TrySet_SingleEntryExceedingMaxEntryBytes_ReturnsFalse_AndRemovesExistingEntryOnly()
    {
        var settingsService = new FakeSettingsService();
        using var cache = new LruTranslationCache(settingsService);

        var k1 = CreateKey("p1");
        var k2 = CreateKey("p2");
        var g = cache.Generation;

        cache.TrySet(k1, "normal text 1", g).ShouldBeTrue();
        cache.TrySet(k2, "normal text 2", g).ShouldBeTrue();
        cache.Count.ShouldBe(2);

        // 构造超过 MaxEntryBytes (1 MiB = 1048576 bytes) 的单项
        // text 长度为 600,000 chars * 2 = 1.2 MB > 1 MB
        var hugeText = new string('x', 600_000);

        // 针对已存在的 k2 写入超大单项：返回 false，仅删除 k2，不逐出 k1
        cache.TrySet(k2, hugeText, g).ShouldBeFalse();

        cache.Count.ShouldBe(1);
        cache.TryGet(k2, out _).ShouldBeFalse();
        cache.TryGet(k1, out _).ShouldBeTrue();
    }

    [Fact]
    public void Generation_Clear_IncrementsGeneration_AndRejectsLateWrite()
    {
        var settingsService = new FakeSettingsService();
        using var cache = new LruTranslationCache(settingsService);

        var g0 = cache.Generation;
        g0.ShouldBe(0);

        var k1 = CreateKey("p1");
        cache.TrySet(k1, "t1", g0).ShouldBeTrue();
        cache.Count.ShouldBe(1);

        cache.Clear();
        cache.Generation.ShouldBe(1);
        cache.Count.ShouldBe(0);
        cache.CurrentTotalBytes.ShouldBe(0);

        // 使用已过期的 g0 尝试写入：必须返回 false，且不影响缓存
        var kLate = CreateKey("p_late");
        var lateSuccess = cache.TrySet(kLate, "late text", g0);
        lateSuccess.ShouldBeFalse();
        cache.Count.ShouldBe(0);
    }

    [Fact]
    public void Generation_LateWrite_DoesNotEvictNewGenerationEntry()
    {
        var settingsService = new FakeSettingsService();
        using var cache = new LruTranslationCache(settingsService);

        var g0 = cache.Generation;

        // 捕获 g0 后清空
        cache.Clear();
        var g1 = cache.Generation;
        g1.ShouldBe(g0 + 1);

        // 新请求写入新代条目
        var kNew = CreateKey("p_new");
        cache.TrySet(kNew, "new value", g1).ShouldBeTrue();

        // 旧请求以 g0 尝试写入：被拒绝，新条目完全不受影响
        var kOld = CreateKey("p_old");
        cache.TrySet(kOld, "old value", g0).ShouldBeFalse();

        cache.Count.ShouldBe(1);
        cache.Keys.ShouldBe(new[] { kNew });
        cache.TryGet(kNew, out var text).ShouldBeTrue();
        text.ShouldBe("new value");
    }

    [Fact]
    public void Settings_CacheDisabled_ReturnsMiss_AndRefusesSet()
    {
        var settingsService = new FakeSettingsService();
        settingsService.Current = settingsService.Current with
        {
            Translation = settingsService.Current.Translation with { CacheEnabled = false }
        };
        using var cache = new LruTranslationCache(settingsService);

        var key = CreateKey();
        cache.TrySet(key, "text", cache.Generation).ShouldBeFalse();
        cache.TryGet(key, out _).ShouldBeFalse();
    }

    [Fact]
    public void Settings_ToggleCacheEnabled_ClearsCache_AndIncrementsGeneration()
    {
        var settingsService = new FakeSettingsService();
        using var cache = new LruTranslationCache(settingsService);

        var k1 = CreateKey();
        cache.TrySet(k1, "text", cache.Generation).ShouldBeTrue();
        var genBefore = cache.Generation;

        // 禁用缓存：清空并递增 Generation
        settingsService.Current = settingsService.Current with
        {
            Translation = settingsService.Current.Translation with { CacheEnabled = false }
        };
        settingsService.TriggerChanged();

        cache.Generation.ShouldBe(genBefore + 1);
        cache.Count.ShouldBe(0);
        cache.TryGet(k1, out _).ShouldBeFalse();

        // 重新启用缓存：再次清空并递增 Generation
        settingsService.Current = settingsService.Current with
        {
            Translation = settingsService.Current.Translation with { CacheEnabled = true }
        };
        settingsService.TriggerChanged();

        cache.Generation.ShouldBe(genBefore + 2);
    }

    [Fact]
    public void Settings_ShrinkCapacity_ImmediatelyTrimsTail_WithoutChangingGeneration()
    {
        var settingsService = new FakeSettingsService();
        settingsService.Current = settingsService.Current with
        {
            Translation = settingsService.Current.Translation with { CacheCapacity = 5 }
        };
        using var cache = new LruTranslationCache(settingsService);

        var g = cache.Generation;
        var k1 = CreateKey("p1");
        var k2 = CreateKey("p2");
        var k3 = CreateKey("p3");
        var k4 = CreateKey("p4");
        var k5 = CreateKey("p5");

        cache.TrySet(k1, "1", g);
        cache.TrySet(k2, "2", g);
        cache.TrySet(k3, "3", g);
        cache.TrySet(k4, "4", g);
        cache.TrySet(k5, "5", g);

        cache.Keys.ShouldBe(new[] { k5, k4, k3, k2, k1 });

        // 将正容量从 5 缩减为 3：即时按 LRU 修剪，不改变 Generation
        settingsService.Current = settingsService.Current with
        {
            Translation = settingsService.Current.Translation with { CacheCapacity = 3 }
        };
        settingsService.TriggerChanged();

        cache.Generation.ShouldBe(g);
        cache.Count.ShouldBe(3);
        cache.Keys.ShouldBe(new[] { k5, k4, k3 });
        cache.TryGet(k1, out _).ShouldBeFalse();
        cache.TryGet(k2, out _).ShouldBeFalse();
        cache.TryGet(k3, out _).ShouldBeTrue();
    }

    [Fact]
    public void Settings_UnrelatedSettingsChange_DoesNotClearOrIncrementGeneration()
    {
        var settingsService = new FakeSettingsService();
        using var cache = new LruTranslationCache(settingsService);

        var k1 = CreateKey();
        var g = cache.Generation;
        cache.TrySet(k1, "text", g).ShouldBeTrue();

        // 改变语言相关设置（无关设置）
        settingsService.Current = settingsService.Current with
        {
            Translation = settingsService.Current.Translation with { PrimaryTargetLanguage = "ja" }
        };
        settingsService.TriggerChanged();

        cache.Generation.ShouldBe(g);
        cache.Count.ShouldBe(1);
        cache.TryGet(k1, out _).ShouldBeTrue();
    }

    [Fact]
    public void Settings_PublicOperation_ConsultsLatestSettingsImmediately()
    {
        var settingsService = new FakeSettingsService();
        using var cache = new LruTranslationCache(settingsService);

        // 直接更新 Current 而不触发事件
        settingsService.Current = settingsService.Current with
        {
            Translation = settingsService.Current.Translation with { CacheEnabled = false }
        };

        // 公开方法调用会立即校准最新设置并拒绝写入
        cache.TrySet(CreateKey(), "text", cache.Generation).ShouldBeFalse();
    }

    [Fact]
    public void Concurrency_MultiThreadedReadWriteClear_MaintainsIntegrity()
    {
        var settingsService = new FakeSettingsService();
        using var cache = new LruTranslationCache(settingsService);

        Parallel.For(0, 200, i =>
        {
            var key = CreateKey($"p{i % 10}", Guid.NewGuid(), new string((char)('a' + (i % 20)), 64));
            var g = cache.Generation;

            if (i % 25 == 0)
            {
                cache.Clear();
            }
            else if (i % 3 == 0)
            {
                cache.TrySet(key, $"text_{i}", g);
            }
            else
            {
                cache.TryGet(key, out _);
            }
        });

        // 验证并发后结构完好
        cache.Count.ShouldBe(cache.Keys.Count);
        cache.CurrentTotalBytes.ShouldBeGreaterThanOrEqualTo(0);
    }

    [Fact]
    public void Dispose_IsIdempotent_AndThrowsObjectDisposedExceptionOnSubsequentCalls()
    {
        var settingsService = new FakeSettingsService();
        var cache = new LruTranslationCache(settingsService);

        cache.Dispose();
        cache.Dispose(); // 幂等

        var key = CreateKey();
        Should.Throw<ObjectDisposedException>(() => cache.TryGet(key, out _));
        Should.Throw<ObjectDisposedException>(() => cache.TrySet(key, "text", 0));
        Should.Throw<ObjectDisposedException>(() => cache.Clear());
        Should.Throw<ObjectDisposedException>(() => _ = cache.Generation);
    }
}
