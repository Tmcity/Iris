using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using Iris.Core.Settings;

namespace Iris.Core.Translation;

internal sealed class LruTranslationCache : ITranslationCache
{
    private sealed class CacheEntry
    {
        public required TranslationCacheKey Key { get; init; }
        public required string Text { get; init; }
        public required long Cost { get; init; }
    }

    private readonly ISettingsService _settingsService;
    private readonly object _lock = new();

    private readonly LinkedList<CacheEntry> _lruList = new();
    private readonly Dictionary<TranslationCacheKey, LinkedListNode<CacheEntry>> _map = new();

    private long _generation;
    private long _currentTotalBytes;
    private bool _disposed;
    private bool _hasInitializedSettings;
    private bool _lastActive;
    private int _lastCapacity;

    public LruTranslationCache(ISettingsService settingsService)
    {
        _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
        _settingsService.Changed += OnSettingsChanged;
    }

    public long Generation
    {
        get
        {
            lock (_lock)
            {
                ThrowIfDisposed();
                var settings = _settingsService.Current.Translation;
                ApplySettingsUnderLock(settings);
                return _generation;
            }
        }
    }

    public bool TryGet(in TranslationCacheKey key, [NotNullWhen(true)] out string? text)
    {
        lock (_lock)
        {
            ThrowIfDisposed();
            var settings = _settingsService.Current.Translation;
            ApplySettingsUnderLock(settings);

            if (!_lastActive)
            {
                text = null;
                return false;
            }

            if (_map.TryGetValue(key, out var node))
            {
                if (node != _lruList.First)
                {
                    _lruList.Remove(node);
                    _lruList.AddFirst(node);
                }
                text = node.Value.Text;
                return true;
            }

            text = null;
            return false;
        }
    }

    public bool TrySet(in TranslationCacheKey key, string text, long generation)
    {
        ArgumentNullException.ThrowIfNull(text);

        lock (_lock)
        {
            ThrowIfDisposed();
            var settings = _settingsService.Current.Translation;
            ApplySettingsUnderLock(settings);

            // 1. 代次不符优先拒绝（连同键旧条目也不能删除）
            if (generation != _generation)
            {
                return false;
            }

            // 2. 缓存未启用或容量无效
            if (!_lastActive)
            {
                return false;
            }

            long entryCost = CalculateCost(key, text);

            // 3. 超过单条或总预算的单项写入返回 false；若同键旧条目存在，仅删除该旧条目，不逐出无关条目
            if (entryCost > TranslationCacheLimits.MaxEntryBytes || entryCost > TranslationCacheLimits.MaxTotalBytes)
            {
                if (_map.TryGetValue(key, out var existingNode))
                {
                    _lruList.Remove(existingNode);
                    _map.Remove(key);
                    _currentTotalBytes -= existingNode.Value.Cost;
                }
                return false;
            }

            // 4. 若同键旧条目存在，先扣除旧成本
            if (_map.TryGetValue(key, out var oldNode))
            {
                _lruList.Remove(oldNode);
                _map.Remove(key);
                _currentTotalBytes -= oldNode.Value.Cost;
            }

            // 5. 准入与淘汰：从 LRU 尾部淘汰，直到条数与总预算均满足
            int capacity = settings.CacheCapacity;
            while ((_map.Count >= capacity || (_currentTotalBytes + entryCost > TranslationCacheLimits.MaxTotalBytes))
                   && _lruList.Last != null)
            {
                var tail = _lruList.Last;
                _lruList.RemoveLast();
                _map.Remove(tail.Value.Key);
                _currentTotalBytes -= tail.Value.Cost;
            }

            if (_map.Count >= capacity || _currentTotalBytes + entryCost > TranslationCacheLimits.MaxTotalBytes)
            {
                return false;
            }

            // 6. 成功插入为 MRU（头部）
            var entry = new CacheEntry
            {
                Key = key,
                Text = text,
                Cost = entryCost
            };
            var newNode = _lruList.AddFirst(entry);
            _map.Add(key, newNode);
            _currentTotalBytes += entryCost;
            return true;
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            ThrowIfDisposed();
            var settings = _settingsService.Current.Translation;
            ApplySettingsUnderLock(settings);
            ClearCore();
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _settingsService.Changed -= OnSettingsChanged;
            _lruList.Clear();
            _map.Clear();
            _currentTotalBytes = 0;
        }
    }

    private void OnSettingsChanged(object? sender, SettingsChangedEventArgs e)
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            var settings = _settingsService.Current.Translation;
            ApplySettingsUnderLock(settings);
        }
    }

    private void ApplySettingsUnderLock(TranslationSettings settings)
    {
        bool newActive = settings.CacheEnabled && settings.CacheCapacity > 0;
        int newCapacity = Math.Max(0, settings.CacheCapacity);

        if (!_hasInitializedSettings)
        {
            _hasInitializedSettings = true;
            _lastActive = newActive;
            _lastCapacity = newCapacity;
            return;
        }

        if (newActive != _lastActive)
        {
            _lastActive = newActive;
            _lastCapacity = newCapacity;
            ClearCore();
            return;
        }

        if (newActive)
        {
            if (newCapacity < _lastCapacity)
            {
                TrimToCapacityUnderLock(newCapacity);
            }
            _lastCapacity = newCapacity;
        }
        else
        {
            _lastCapacity = newCapacity;
        }
    }

    private void ClearCore()
    {
        _generation++;
        _lruList.Clear();
        _map.Clear();
        _currentTotalBytes = 0;
    }

    private void TrimToCapacityUnderLock(int capacity)
    {
        while (_map.Count > capacity && _lruList.Last != null)
        {
            var tail = _lruList.Last;
            _lruList.RemoveLast();
            _map.Remove(tail.Value.Key);
            _currentTotalBytes -= tail.Value.Cost;
        }
    }

    private static long CalculateCost(in TranslationCacheKey key, string text)
    {
        return TranslationCacheLimits.EntryOverheadBytes +
               2L * ((long)key.ProviderId.Length + key.RequestFingerprint.Length + text.Length);
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    internal int Count
    {
        get
        {
            lock (_lock)
            {
                return _map.Count;
            }
        }
    }

    internal long CurrentTotalBytes
    {
        get
        {
            lock (_lock)
            {
                return _currentTotalBytes;
            }
        }
    }

    internal IReadOnlyList<TranslationCacheKey> Keys
    {
        get
        {
            lock (_lock)
            {
                var keys = new List<TranslationCacheKey>(_lruList.Count);
                for (var node = _lruList.First; node != null; node = node.Next)
                {
                    keys.Add(node.Value.Key);
                }
                return keys;
            }
        }
    }
}
