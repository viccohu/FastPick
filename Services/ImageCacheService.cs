using Microsoft.UI.Xaml.Media.Imaging;
using System.Collections.Concurrent;

namespace FastPick.Services;

public class ImageCacheService
{
    private class CacheItem
    {
        public BitmapImage Image { get; set; }
        public DateTime LastAccessed { get; set; }
        public long Size { get; set; }
    }

    private readonly ConcurrentDictionary<string, CacheItem> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _cacheLock = new();
    private readonly int _maxCacheSize;
    private readonly int _maxItemCount;

    private long _totalCacheSize;
    private int _cacheHits;
    private int _cacheMisses;

    public ImageCacheService(int maxItemCount = 100, int maxCacheSizeMb = 1000)
    {
        _maxItemCount = maxItemCount;
        _maxCacheSize = maxCacheSizeMb * 1024 * 1024;
    }

    public BitmapImage? Get(string key)
    {
        if (_cache.TryGetValue(key, out var item))
        {
            lock (_cacheLock)
            {
                item.LastAccessed = DateTime.Now;
            }
            Interlocked.Increment(ref _cacheHits);
            return item.Image;
        }

        Interlocked.Increment(ref _cacheMisses);
        return null;
    }

    public void Set(string key, BitmapImage image, long estimatedSize = 0)
    {
        if (image == null) return;

        var item = new CacheItem
        {
            Image = image,
            LastAccessed = DateTime.Now,
            Size = estimatedSize > 0 ? estimatedSize : EstimateImageSize(image)
        };

        lock (_cacheLock)
        {
            if (_cache.TryGetValue(key, out var existingItem))
            {
                _totalCacheSize -= existingItem.Size;
            }

            _cache[key] = item;
            _totalCacheSize += item.Size;

            CleanupIfNeeded();
        }
    }

    public bool Contains(string key)
    {
        return _cache.ContainsKey(key);
    }

    public void Remove(string key)
    {
        lock (_cacheLock)
        {
            if (_cache.TryRemove(key, out var item))
            {
                _totalCacheSize -= item.Size;
                ReleaseImage(item.Image);
            }
        }
    }

    public void Clear()
    {
        lock (_cacheLock)
        {
            foreach (var item in _cache.Values)
            {
                ReleaseImage(item.Image);
            }
            _cache.Clear();
            _totalCacheSize = 0;
        }
    }

    public (int hits, int misses, double hitRate) GetStatistics()
    {
        var total = _cacheHits + _cacheMisses;
        var hitRate = total > 0 ? (double)_cacheHits / total : 0;
        return (_cacheHits, _cacheMisses, hitRate);
    }

    public (int itemCount, long sizeBytes) GetCacheInfo()
    {
        lock (_cacheLock)
        {
            return (_cache.Count, _totalCacheSize);
        }
    }

    public void LogCacheStatistics()
    {
        var stats = GetStatistics();
        var cacheInfo = GetCacheInfo();
        var sizeMb = cacheInfo.sizeBytes / (1024.0 * 1024.0);

        LoggerService.Instance.Info(LogCategory.Cache,
            $"缓存统计 - 命中率: {stats.hitRate:P0}, " +
            $"命中: {stats.hits}, 未命中: {stats.misses}, " +
            $"项目数: {cacheInfo.itemCount}, 大小: {sizeMb:F2}MB");
    }

    private void CleanupIfNeeded()
    {
        while (_cache.Count > _maxItemCount || _totalCacheSize > _maxCacheSize)
        {
            var oldestItem = _cache.OrderBy(kvp => kvp.Value.LastAccessed).FirstOrDefault();
            if (oldestItem.Key == null) break;

            if (_cache.TryRemove(oldestItem.Key, out var removedItem))
            {
                _totalCacheSize -= removedItem.Size;
                ReleaseImage(removedItem.Image);
            }
        }
    }

    private long EstimateImageSize(BitmapImage image)
    {
        try
        {
            var width = image.PixelWidth;
            var height = image.PixelHeight;
            return width * height * 4;
        }
        catch
        {
            return 1024 * 1024;
        }
    }

    private void ReleaseImage(BitmapImage? image)
    {
    }
}
