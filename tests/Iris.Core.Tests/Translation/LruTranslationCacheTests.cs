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

        public void TriggerChanged(AppSettings oldSettings, AppSettings newSettings)
        {
            Changed?.Invoke(this, new SettingsChangedEventArgs(oldSettings, newSettings));
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
    public void SingleEntry_ExactMaxEntryBytes_IsAdmitted_AndOverByOneCodeUnit_IsRejected()
    {
        var settingsService = new FakeSettingsService();
        using var cache = new LruTranslationCache(settingsService);

        var key = new TranslationCacheKey("p", Guid.NewGuid(), new string('0', 64));
        var g = cache.Generation;

        // 256 + 2 * (1 + 64 + text.Length) == 1,048,576
        // 256 + 130 + 2 * text.Length == 1,048,576
        // 2 * text.Length == 1,048,190 => text.Length == 524,095
        var exactText = new string('a', 524_095);
        cache.TrySet(key, exactText, g).ShouldBeTrue();
        cache.CurrentTotalBytes.ShouldBe(TranslationCacheLimits.MaxEntryBytes);
        cache.TryGet(key, out var retrieved).ShouldBeTrue();
        retrieved.ShouldBe(exactText);

        // 超出恰好 1 个 UTF-16 code unit (524,096 chars -> 1,048,578 bytes > 1,048,576)
        var overKey = new TranslationCacheKey("p", Guid.NewGuid(), new string('1', 64));
        var overText = new string('b', 524_096);
        cache.TrySet(overKey, overText, g).ShouldBeFalse();

        // 验证原条目仍保留，总字节数未变
        cache.Count.ShouldBe(1);
        cache.CurrentTotalBytes.ShouldBe(TranslationCacheLimits.MaxEntryBytes);
        cache.TryGet(key, out _).ShouldBeTrue();
        cache.TryGet(overKey, out _).ShouldBeFalse();
    }

    [Fact]
    public void TotalByteBudget_ExactMaxTotalBytes_IsRetained_AndAdditionalEntryEvictsLruTail_BeforeCapacityReached()
    {
        var settingsService = new FakeSettingsService();
        // 确保 Capacity 远大于 16，排除条数容量淘汰的干扰
        settingsService.Current = settingsService.Current with
        {
            Translation = settingsService.Current.Translation with { CacheCapacity = 500 }
        };
        using var cache = new LruTranslationCache(settingsService);
        var g = cache.Generation;

        // 每个条目恰好 1,048,576 bytes (1 MiB)
        // 16 条 * 1,048,576 bytes = 16,777,216 bytes = MaxTotalBytes (16 MiB)
        var keys = new List<TranslationCacheKey>(16);
        for (var i = 0; i < 16; i++)
        {
            var k = new TranslationCacheKey("p", Guid.NewGuid(), new string((char)('0' + (i % 10)), 64));
            keys.Add(k);
            var text = new string((char)('A' + i), 524_095);
            cache.TrySet(k, text, g).ShouldBeTrue();
        }

        // 此时恰好达到 MaxTotalBytes，且 Count 为 16 (远小于 CacheCapacity=500)
        cache.Count.ShouldBe(16);
        cache.CurrentTotalBytes.ShouldBe(TranslationCacheLimits.MaxTotalBytes);
        foreach (var k in keys)
        {
            cache.TryGet(k, out _).ShouldBeTrue();
        }

        // 插入第 17 条条目（任意正成本条目，此处为小文本）
        // 此时 Count=16 < 500，不会触发容量淘汰；必须仅因总字节超额淘汰 LRU 尾部（keys[0]）
        var k17 = new TranslationCacheKey("p", Guid.NewGuid(), new string('x', 64));
        var text17 = "small text";
        long cost17 = 256L + 2L * (1 + 64 + text17.Length); // 406 bytes

        cache.TrySet(k17, text17, g).ShouldBeTrue();

        // 验证 keys[0]（最久未访问项）被总预算淘汰，其余项保留
        cache.TryGet(keys[0], out _).ShouldBeFalse("LRU tail must be evicted due to byte budget overflow.");
        for (var i = 1; i < 16; i++)
        {
            cache.TryGet(keys[i], out _).ShouldBeTrue();
        }
        cache.TryGet(k17, out _).ShouldBeTrue();

        cache.Count.ShouldBe(16);
        long expectedTotalBytes = TranslationCacheLimits.MaxTotalBytes - TranslationCacheLimits.MaxEntryBytes + cost17;
        cache.CurrentTotalBytes.ShouldBe(expectedTotalBytes);
        cache.CurrentTotalBytes.ShouldBeLessThanOrEqualTo(TranslationCacheLimits.MaxTotalBytes);
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

    [Theory]
    [InlineData(true, 500)]
    [InlineData(false, 500)]
    [InlineData(true, 0)]
    public void Generation_InitialState_IsZero_ForEnabled_Disabled_AndZeroCapacity(bool enabled, int capacity)
    {
        var settingsService = new FakeSettingsService();
        settingsService.Current = settingsService.Current with
        {
            Translation = settingsService.Current.Translation with
            {
                CacheEnabled = enabled,
                CacheCapacity = capacity
            }
        };
        using var cache = new LruTranslationCache(settingsService);

        cache.Generation.ShouldBe(0);
    }

    [Fact]
    public void Generation_LifecycleSequence_Clear_CapacityZero_AndToggle_IncrementGeneration_AndRejectStaleWrites()
    {
        var settingsService = new FakeSettingsService();
        using var cache = new LruTranslationCache(settingsService);

        cache.Generation.ShouldBe(0);
        var k1 = CreateKey("p1");
        cache.TrySet(k1, "v1", 0).ShouldBeTrue();

        // 1. 首次显式 Clear -> Generation 递增至 1
        cache.Clear();
        cache.Generation.ShouldBe(1);
        cache.Count.ShouldBe(0);

        // 过期代次 0 写入被拒绝
        var k2 = CreateKey("p2");
        cache.TrySet(k2, "v2", 0).ShouldBeFalse();
        cache.TrySet(k2, "v2", 1).ShouldBeTrue();
        cache.Count.ShouldBe(1);

        // 2. 容量从正数变为 0 -> Generation 递增至 2，清空缓存
        settingsService.Current = settingsService.Current with
        {
            Translation = settingsService.Current.Translation with { CacheCapacity = 0 }
        };
        settingsService.TriggerChanged();

        cache.Generation.ShouldBe(2);
        cache.Count.ShouldBe(0);
        cache.TryGet(k2, out _).ShouldBeFalse();

        // 在容量为 0 下即使使用最新代次 2 写入也会失败
        var k3 = CreateKey("p3");
        cache.TrySet(k3, "v3", 1).ShouldBeFalse();
        cache.TrySet(k3, "v3", 2).ShouldBeFalse();

        // 3. 容量从 0 恢复为正数 -> Generation 递增至 3
        settingsService.Current = settingsService.Current with
        {
            Translation = settingsService.Current.Translation with { CacheCapacity = 500 }
        };
        settingsService.TriggerChanged();

        cache.Generation.ShouldBe(3);
        cache.TrySet(k3, "v3", 2).ShouldBeFalse();
        cache.TrySet(k3, "v3", 3).ShouldBeTrue();
        cache.Count.ShouldBe(1);

        // 4. 禁用缓存 -> Generation 递增至 4，清空缓存
        settingsService.Current = settingsService.Current with
        {
            Translation = settingsService.Current.Translation with { CacheEnabled = false }
        };
        settingsService.TriggerChanged();

        cache.Generation.ShouldBe(4);
        cache.Count.ShouldBe(0);
        cache.TryGet(k3, out _).ShouldBeFalse();

        // 5. 重新启用缓存 -> Generation 递增至 5
        settingsService.Current = settingsService.Current with
        {
            Translation = settingsService.Current.Translation with { CacheEnabled = true }
        };
        settingsService.TriggerChanged();

        cache.Generation.ShouldBe(5);
    }

    [Fact]
    public async Task Generation_GatedStaleOversizeWrite_PreservesNewValue_OtherItems_Order_AndCost()
    {
        var settingsService = new FakeSettingsService();
        using var cache = new LruTranslationCache(settingsService);

        var kOther = CreateKey("p_other");
        var kTarget = CreateKey("p_target");
        var gOld = cache.Generation; // 0

        cache.TrySet(kOther, "other_init", gOld).ShouldBeTrue();
        cache.TrySet(kTarget, "target_init", gOld).ShouldBeTrue();

        using var readyToSetOld = new System.Threading.ManualResetEventSlim(false);
        using var setOldCompleted = new System.Threading.ManualResetEventSlim(false);
        bool? lateResult = null;

        var hugeText = new string('x', 600_000); // 1.2 MB > MaxEntryBytes

        // 旧请求异步任务：已捕获 gOld，等待 Clear 和新代写入后再调用 TrySet
        var backgroundTask = Task.Run(() =>
        {
            if (!readyToSetOld.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken))
            {
                throw new TimeoutException("Timed out waiting for main thread to prepare new generation.");
            }
            lateResult = cache.TrySet(kTarget, hugeText, gOld);
            setOldCompleted.Set();
        }, TestContext.Current.CancellationToken);

        // 主线程：Clear 推进代次至 1，并以新代次重新写入新值与控制项
        cache.Clear();
        var gNew = cache.Generation; // 1

        cache.TrySet(kOther, "other_new", gNew).ShouldBeTrue();
        cache.TrySet(kTarget, "target_new", gNew).ShouldBeTrue();

        var expectedKeys = new[] { kTarget, kOther };
        cache.Keys.ShouldBe(expectedKeys);
        var expectedBytes = cache.CurrentTotalBytes;

        // 释放门闩，触发旧写入
        readyToSetOld.Set();
        setOldCompleted.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken).ShouldBeTrue();
        await backgroundTask;

        // 验证：旧超大写入被代次拒绝（返回 false）
        lateResult.ShouldBe(false);

        // 验证旧写入未更改 MRU 顺序与成本
        cache.Keys.ShouldBe(expectedKeys);
        cache.CurrentTotalBytes.ShouldBe(expectedBytes);

        // 新值与其它项保持正确
        cache.TryGet(kTarget, out var targetVal).ShouldBeTrue();
        targetVal.ShouldBe("target_new");

        cache.TryGet(kOther, out var otherVal).ShouldBeTrue();
        otherVal.ShouldBe("other_new");
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
    public void Settings_StaleEventPayload_DoesNotRevertCapacity_WhenCurrentIsUpdated()
    {
        var settingsService = new FakeSettingsService();
        settingsService.Current = settingsService.Current with
        {
            Translation = settingsService.Current.Translation with { CacheCapacity = 10 }
        };
        using var cache = new LruTranslationCache(settingsService);

        var k1 = CreateKey("p1");
        var k2 = CreateKey("p2");
        var g = cache.Generation;
        cache.TrySet(k1, "1", g).ShouldBeTrue();
        cache.TrySet(k2, "2", g).ShouldBeTrue();

        // 外部更新 Current 为容量 2
        settingsService.Current = settingsService.Current with
        {
            Translation = settingsService.Current.Translation with { CacheCapacity = 2 }
        };

        // 模拟外部下发一个载有陈旧旧容量 10 的事件通知
        var staleSettings = settingsService.Current with
        {
            Translation = settingsService.Current.Translation with { CacheCapacity = 10 }
        };
        settingsService.TriggerChanged(staleSettings, staleSettings);

        // 写入第 3 条条目：容量必须仍依据 Current 的 2 条限制，k1 应被 LRU 淘汰
        var k3 = CreateKey("p3");
        cache.TrySet(k3, "3", g).ShouldBeTrue();

        cache.Count.ShouldBe(2);
        cache.Keys.ShouldBe(new[] { k3, k2 });
        cache.TryGet(k1, out _).ShouldBeFalse();
        cache.TryGet(k2, out _).ShouldBeTrue();
        cache.TryGet(k3, out _).ShouldBeTrue();
    }

    [Fact]
    public void Concurrency_MultiThreadedReadWriteClear_MaintainsIntegrity()
    {
        var settingsService = new FakeSettingsService();
        settingsService.Current = settingsService.Current with
        {
            Translation = settingsService.Current.Translation with { CacheCapacity = 50 }
        };
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

        // 验证并发后结构完好、计数与边界一致
        var keys = cache.Keys;
        cache.Count.ShouldBe(keys.Count);
        cache.Count.ShouldBeLessThanOrEqualTo(50);
        cache.CurrentTotalBytes.ShouldBeLessThanOrEqualTo(TranslationCacheLimits.MaxTotalBytes);

        // 验证键可达性、无重复键，并深层核对每个项的成本之和等于 CurrentTotalBytes
        var seenKeys = new HashSet<TranslationCacheKey>();
        long calculatedTotalBytes = 0;

        foreach (var key in keys)
        {
            seenKeys.Add(key).ShouldBeTrue("Keys collection should not contain duplicate keys.");
            cache.TryGet(key, out var text).ShouldBeTrue("Every key in Keys must be reachable via TryGet.");
            text.ShouldNotBeNull();

            long entryCost = 256L + 2L * ((long)key.ProviderId.Length + key.RequestFingerprint.Length + text.Length);
            calculatedTotalBytes += entryCost;
        }

        cache.CurrentTotalBytes.ShouldBe(calculatedTotalBytes);
    }

    private sealed class InterceptingSettingsService : ISettingsService
    {
        private readonly FakeSettingsService _inner = new();
        public Action<AppSettings>? OnReadingCurrent { get; set; }
        public Action? OnEnteringChanged { get; set; }

        public AppSettings Current
        {
            get
            {
                var current = _inner.Current;
                OnReadingCurrent?.Invoke(current);
                return current;
            }
            set => _inner.Current = value;
        }

        public event EventHandler<SettingsChangedEventArgs>? Changed
        {
            add => _inner.Changed += value;
            remove => _inner.Changed -= value;
        }

        public Task LoadAsync(CancellationToken cancellationToken = default) => _inner.LoadAsync(cancellationToken);
        public Task UpdateAsync(Func<AppSettings, AppSettings> mutate, CancellationToken cancellationToken = default) => _inner.UpdateAsync(mutate, cancellationToken);

        public void TriggerChanged()
        {
            OnEnteringChanged?.Invoke();
            _inner.TriggerChanged();
        }

        public void TriggerChanged(AppSettings oldSettings, AppSettings newSettings)
        {
            OnEnteringChanged?.Invoke();
            _inner.TriggerChanged(oldSettings, newSettings);
        }
    }

    [Fact]
    public async Task Concurrency_SettingsUpdateAndWriteInterleaving_MaintainsLatestSettingsCapacityAndCost()
    {
        var settingsService = new InterceptingSettingsService();
        settingsService.Current = settingsService.Current with
        {
            Translation = settingsService.Current.Translation with { CacheCapacity = 3 }
        };
        using var cache = new LruTranslationCache(settingsService);

        var k1 = CreateKey("p1");
        var k2 = CreateKey("p2");
        var k3 = CreateKey("p3");
        var g = cache.Generation;

        cache.TrySet(k1, "v1", g).ShouldBeTrue();
        cache.TrySet(k2, "v2", g).ShouldBeTrue();

        using var capturedOldCurrent = new System.Threading.ManualResetEventSlim(false);
        using var enteredChanged = new System.Threading.ManualResetEventSlim(false);
        using var releaseTask1 = new System.Threading.ManualResetEventSlim(false);

        var ct = TestContext.Current.CancellationToken;
        bool hookActivated = false;
        int hookTriggered = 0;

        settingsService.OnReadingCurrent = current =>
        {
            if (hookActivated && current.Translation.CacheCapacity == 3 && Interlocked.Exchange(ref hookTriggered, 1) == 0)
            {
                capturedOldCurrent.Set();
                releaseTask1.Wait(TimeSpan.FromSeconds(5), ct);
            }
        };

        settingsService.OnEnteringChanged = () =>
        {
            enteredChanged.Set();
        };

        Task? task1 = null;
        Task? task2 = null;

        try
        {
            hookActivated = true;

            // 任务 1：发起写入 k3，读取旧 Current (CacheCapacity=3) 后暂停
            task1 = Task.Run(() =>
            {
                cache.TrySet(k3, "v3", g).ShouldBeTrue();
            }, ct);

            // 等待任务 1 捕获旧 Current 并暂停
            capturedOldCurrent.Wait(TimeSpan.FromSeconds(5), ct).ShouldBeTrue();

            // 任务 2：发布缩容配置 (CacheCapacity=2) 并触发 Changed
            task2 = Task.Run(() =>
            {
                settingsService.Current = settingsService.Current with
                {
                    Translation = settingsService.Current.Translation with { CacheCapacity = 2 }
                };
                settingsService.TriggerChanged();
            }, ct);

            // 等待任务 2 进入 Changed
            enteredChanged.Wait(TimeSpan.FromSeconds(5), ct).ShouldBeTrue();

            // 放行任务 1
            releaseTask1.Set();

            // 等待两任务全部完成
            await Task.WhenAll(task1, task2);

            // 断言最终状态严格符合最新缩容配置 (CacheCapacity=2)
            cache.Count.ShouldBe(2);
            cache.Keys.ShouldBe(new[] { k3, k2 });
            cache.TryGet(k1, out _).ShouldBeFalse("LRU tail k1 must be trimmed to satisfy shrunk capacity.");
            cache.TryGet(k2, out var v2).ShouldBeTrue();
            v2.ShouldBe("v2");
            cache.TryGet(k3, out var v3).ShouldBeTrue();
            v3.ShouldBe("v3");

            long expectedCost = (256L + 2L * (k3.ProviderId.Length + k3.RequestFingerprint.Length + "v3".Length))
                              + (256L + 2L * (k2.ProviderId.Length + k2.RequestFingerprint.Length + "v2".Length));
            cache.CurrentTotalBytes.ShouldBe(expectedCost);
        }
        finally
        {
            capturedOldCurrent.Set();
            enteredChanged.Set();
            releaseTask1.Set();
            if (task1 != null || task2 != null)
            {
                await Task.WhenAll(task1 ?? Task.CompletedTask, task2 ?? Task.CompletedTask);
            }
        }
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

    [Fact]
    public void Dispose_UnsubscribesFromSettingsChangedEvent()
    {
        var subSettings = Substitute.For<ISettingsService>();
        subSettings.Current.Returns(new AppSettings());

        var cache = new LruTranslationCache(subSettings);
        subSettings.Received(1).Changed += Arg.Any<EventHandler<SettingsChangedEventArgs>>();

        cache.Dispose();
        subSettings.Received(1).Changed -= Arg.Any<EventHandler<SettingsChangedEventArgs>>();

        // 对已释放的缓存触发事件，不会抛出未处理异常
        subSettings.Changed += Raise.Event<EventHandler<SettingsChangedEventArgs>>(
            subSettings,
            new SettingsChangedEventArgs(new AppSettings(), new AppSettings()));
    }
}
