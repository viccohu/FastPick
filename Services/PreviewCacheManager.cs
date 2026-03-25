using FastPick.Models;
using Microsoft.UI.Xaml.Media.Imaging;
using System.Collections.Concurrent;

namespace FastPick.Services;

/// <summary>
/// 预览缓存管理器 - 独立管理 L2/L3 缓存
/// </summary>
public class PreviewCacheManager
{
    private readonly ConcurrentDictionary<string, CacheEntry> _l2Cache = new();
    private readonly ConcurrentDictionary<string, CacheEntry> _l3Cache = new();
    private readonly ConcurrentDictionary<string, PhotoItem> _photoItemMap = new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<string> _l2LruList = new();
    private readonly LinkedList<string> _l3LruList = new();
    private readonly SemaphoreSlim _cacheLock = new(1, 1);
    
    // 内存上限配置
    private const long MaxL2CacheSizeBytes = 200 * 1024 * 1024;  // 200MB
    private const long MaxL3CacheSizeBytes = 500 * 1024 * 1024;  // 500MB
    private const int MaxL2CacheCount = 50;
    private const int MaxL3CacheCount = 20;
    
    private long _currentL2Size = 0;
    private long _currentL3Size = 0;
    
    private class CacheEntry
    {
        public BitmapImage Image { get; }
        public long SizeBytes { get; }
        public LinkedListNode<string> LruNode { get; set; }
        
        public CacheEntry(BitmapImage image, long sizeBytes, LinkedListNode<string> node)
        {
            Image = image;
            SizeBytes = sizeBytes;
            LruNode = node;
        }
    }
    
    /// <summary>
    /// 添加缓存，自动淘汰超出限制的条目
    /// </summary>
    public async Task AddToCacheAsync(
        string key, 
        BitmapImage image, 
        PreviewQualityLevel level,
        PhotoItem? photoItem = null,
        CancellationToken cancellationToken = default)
    {
        await _cacheLock.WaitAsync(cancellationToken);
        try
        {
            var cache = level == PreviewQualityLevel.QuickPreview ? _l2Cache : _l3Cache;
            var lruList = level == PreviewQualityLevel.QuickPreview ? _l2LruList : _l3LruList;
            var maxSize = level == PreviewQualityLevel.QuickPreview ? MaxL2CacheSizeBytes : MaxL3CacheSizeBytes;
            var maxCount = level == PreviewQualityLevel.QuickPreview ? MaxL2CacheCount : MaxL3CacheCount;
            
            if (photoItem != null)
            {
                _photoItemMap[key] = photoItem;
            }
            
            // 如果已存在，先移除旧的
            if (cache.TryGetValue(key, out var existingEntry))
            {
                SubtractFromCurrentSize(level, existingEntry.SizeBytes);
                lruList.Remove(existingEntry.LruNode);
                cache.TryRemove(key, out _);
            }
            
            // 估算图像大小
            var imageSize = EstimateImageSize(image);
            
            // 淘汰直到有足够空间
            while ((GetCurrentSize(level) + imageSize > maxSize || cache.Count >= maxCount) 
                   && lruList.Count > 0)
            {
                EvictLruEntry(level);
            }
            
            // 添加新条目
            var node = lruList.AddFirst(key);
            var entry = new CacheEntry(image, imageSize, node);
            cache[key] = entry;
            
            AddToCurrentSize(level, imageSize);
        }
        finally
        {
            _cacheLock.Release();
        }
    }
    
    /// <summary>
    /// 获取缓存，更新 LRU
    /// </summary>
    public async Task<BitmapImage?> GetFromCacheAsync(
        string key, 
        PreviewQualityLevel level,
        CancellationToken cancellationToken = default)
    {
        await _cacheLock.WaitAsync(cancellationToken);
        try
        {
            var cache = level == PreviewQualityLevel.QuickPreview ? _l2Cache : _l3Cache;
            var lruList = level == PreviewQualityLevel.QuickPreview ? _l2LruList : _l3LruList;
            
            if (cache.TryGetValue(key, out var entry))
            {
                // 更新 LRU
                lruList.Remove(entry.LruNode);
                lruList.AddFirst(entry.LruNode);
                return entry.Image;
            }
            
            return null;
        }
        finally
        {
            _cacheLock.Release();
        }
    }
    
    /// <summary>
    /// 检查缓存是否存在
    /// </summary>
    public bool HasCache(string key, PreviewQualityLevel level)
    {
        var cache = level == PreviewQualityLevel.QuickPreview ? _l2Cache : _l3Cache;
        return cache.ContainsKey(key);
    }
    
    private void EvictLruEntry(PreviewQualityLevel level)
    {
        var cache = level == PreviewQualityLevel.QuickPreview ? _l2Cache : _l3Cache;
        var lruList = level == PreviewQualityLevel.QuickPreview ? _l2LruList : _l3LruList;
        
        if (lruList.Count == 0) return;
        
        var lruKey = lruList.Last!.Value;
        if (cache.TryRemove(lruKey, out var entry))
        {
            SubtractFromCurrentSize(level, entry.SizeBytes);
            lruList.RemoveLast();
            
            if (_photoItemMap.TryGetValue(lruKey, out var photoItem))
            {
                if (level == PreviewQualityLevel.QuickPreview)
                {
                    photoItem.PreviewCache.QuickPreview = null;
                }
                else if (level == PreviewQualityLevel.FullResolution)
                {
                    photoItem.PreviewCache.FullResolution = null;
                }
                
                if (photoItem.PreviewCache.QuickPreview == null && 
                    photoItem.PreviewCache.FullResolution == null)
                {
                    _photoItemMap.TryRemove(lruKey, out _);
                }
            }
        }
    }
    
    private long GetCurrentSize(PreviewQualityLevel level)
    {
        return level == PreviewQualityLevel.QuickPreview ? 
            Interlocked.Read(ref _currentL2Size) : 
            Interlocked.Read(ref _currentL3Size);
    }
    
    private void AddToCurrentSize(PreviewQualityLevel level, long size)
    {
        if (level == PreviewQualityLevel.QuickPreview)
            Interlocked.Add(ref _currentL2Size, size);
        else
            Interlocked.Add(ref _currentL3Size, size);
    }
    
    private void SubtractFromCurrentSize(PreviewQualityLevel level, long size)
    {
        if (level == PreviewQualityLevel.QuickPreview)
            Interlocked.Add(ref _currentL2Size, -size);
        else
            Interlocked.Add(ref _currentL3Size, -size);
    }
    
    private long EstimateImageSize(BitmapImage image)
    {
        // 估算：宽 * 高 * 4 (BGRA8)
        return (long)(image.PixelWidth * image.PixelHeight * 4);
    }
    
    /// <summary>
    /// 内存压力时清理缓存
    /// </summary>
    public async Task ClearCacheAsync()
    {
        await _cacheLock.WaitAsync();
        try
        {
            foreach (var photoItem in _photoItemMap.Values)
            {
                photoItem.PreviewCache.QuickPreview = null;
                photoItem.PreviewCache.FullResolution = null;
            }
            
            _l2Cache.Clear();
            _l3Cache.Clear();
            _photoItemMap.Clear();
            _l2LruList.Clear();
            _l3LruList.Clear();
            _currentL2Size = 0;
            _currentL3Size = 0;
        }
        finally
        {
            _cacheLock.Release();
        }
    }
    
    /// <summary>
    /// 获取缓存统计信息
    /// </summary>
    public async Task<(int L2Count, int L3Count, long L2Size, long L3Size)> GetCacheStatsAsync()
    {
        await _cacheLock.WaitAsync();
        try
        {
            return (_l2Cache.Count, _l3Cache.Count, _currentL2Size, _currentL3Size);
        }
        finally
        {
            _cacheLock.Release();
        }
    }
}
