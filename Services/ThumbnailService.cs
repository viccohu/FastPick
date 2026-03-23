using FastPick.Models;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using System.Collections.Concurrent;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Runtime.InteropServices;
using FastPick.Services;

namespace FastPick.Services;

public class ThumbnailService
{
    private readonly LinkedList<string> _lruList = new();
    private readonly Dictionary<string, WeakReference<BitmapImage>> _thumbnailCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, PhotoItem> _photoItemMap = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _cacheLock = new(1, 1);
    private readonly SemaphoreSlim _throttler = new(Environment.ProcessorCount, Environment.ProcessorCount);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<BitmapImage?>> _pendingLoads = new(StringComparer.OrdinalIgnoreCase);
    private int _maxCacheSize = 1000;
    private const int ThumbnailWidth = 256;
    private string? _cachePath;
    private const string CacheFolderName = "ThumbnailCache";
    private bool _memoryMonitoringEnabled = false;
    private bool _cacheInitFailed = false;
    
    // 视区信息
    private volatile int _viewportStartIndex = 0;
    private volatile int _viewportEndIndex = 0;
    
    #region 统计和监控
    private int _cacheHits = 0;
    private int _cacheMisses = 0;
    private int _localCacheHits = 0;
    private int _localCacheMisses = 0;
    private long _totalLoadTime = 0;
    private int _loadCount = 0;
    #endregion

    /// <summary>
    /// 更新视区信息，用于后台解码优先级排序
    /// </summary>
    /// <param name="startIndex">视区开始索引</param>
    /// <param name="endIndex">视区结束索引</param>
    public void UpdateViewportInfo(int startIndex, int endIndex)
    {
        _viewportStartIndex = startIndex;
        _viewportEndIndex = endIndex;
    }

    /// <summary>
    /// 获取缓存的缩略图（优先从内存缓存和本地缓存加载）
    /// </summary>
    /// <param name="photoItem">照片项</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>缓存的缩略图，如果不存在则返回 null</returns>
    public async Task<BitmapImage?> GetCachedThumbnailAsync(PhotoItem photoItem, CancellationToken cancellationToken = default)
    {
        var filePath = photoItem.DisplayPath;
        
        if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
            return null;

        // 首先检查内存缓存（无锁）
        if (_thumbnailCache.TryGetValue(filePath, out var weakRef))
        {
            if (weakRef.TryGetTarget(out var cachedImage))
            {
                _cacheHits++;
                // 异步更新 LRU 顺序，避免阻塞主线程
                _ = Task.Run(() => UpdateLruOrderAsync(filePath), cancellationToken);
                
                // 设置到 PhotoItem
                photoItem.Thumbnail = cachedImage;
                
                return cachedImage;
            }
        }

        // 然后检查本地缓存
        var localCacheImage = await GetFromLocalCacheAsync(filePath, cancellationToken);
        if (localCacheImage != null)
        {
            _localCacheHits++;
            // 设置到 PhotoItem
            photoItem.Thumbnail = localCacheImage;
            
            return localCacheImage;
        }

        // 缓存未命中
        return null;
    }

    public async Task<BitmapImage?> GetThumbnailAsync(PhotoItem photoItem, CancellationToken cancellationToken = default)
    {
        var filePath = photoItem.DisplayPath;
        
        if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
            return null;

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        // 快速检查内存缓存（无锁）
        BitmapImage? cachedImage = null;
        if (_thumbnailCache.TryGetValue(filePath, out var weakRef))
        {
            if (weakRef.TryGetTarget(out cachedImage))
            {
                _cacheHits++;
                // 异步更新 LRU 顺序，避免阻塞主线程
                _ = Task.Run(() => UpdateLruOrderAsync(filePath), cancellationToken);
                stopwatch.Stop();
                UpdateLoadStats(stopwatch.ElapsedMilliseconds);
                return cachedImage;
            }
            else
            {
                // 引用已失效，异步清理
                _ = Task.Run(() => RemoveInvalidReferenceAsync(filePath), cancellationToken);
            }
        }

        _cacheMisses++;

        // 启动内存监控（如果尚未启动）
        if (!_memoryMonitoringEnabled)
        {
            StartMemoryMonitoring();
        }

        // 尝试从本地缓存读取
        var localCacheImage = await GetFromLocalCacheAsync(filePath, cancellationToken);
        if (localCacheImage != null)
        {
            _localCacheHits++;
            // 异步添加到内存缓存，避免阻塞主线程
            _ = Task.Run(() => AddToCacheAsync(filePath, localCacheImage, photoItem, cancellationToken), cancellationToken);
            stopwatch.Stop();
            UpdateLoadStats(stopwatch.ElapsedMilliseconds);
            return localCacheImage;
        }

        _localCacheMisses++;

        var tcs = new TaskCompletionSource<BitmapImage?>();
        if (_pendingLoads.TryAdd(filePath, tcs))
        {
            try
            {
                await _throttler.WaitAsync(cancellationToken);
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    
                    var wicThumbnail = await GenerateWicThumbnailAsync(filePath, cancellationToken);
                    if (wicThumbnail != null)
                    {
                        await AddToCacheAsync(filePath, wicThumbnail, photoItem, cancellationToken);
                        tcs.SetResult(wicThumbnail);
                        stopwatch.Stop();
                        UpdateLoadStats(stopwatch.ElapsedMilliseconds);
                        return wicThumbnail;
                    }

                    tcs.SetResult(null);
                    stopwatch.Stop();
                    UpdateLoadStats(stopwatch.ElapsedMilliseconds);
                    return null;
                }
                finally
                {
                    _throttler.Release();
                }
            }
            catch (OperationCanceledException)
            {
                tcs.SetCanceled();
                throw;
            }
            catch (Exception ex)
            {
                DebugService.WriteLine($"获取缩略图失败: {filePath}, {ex.Message}");
                tcs.SetResult(null);
                return null;
            }
            finally
            {
                _pendingLoads.TryRemove(filePath, out _);
            }
        }
        else
        {
            if (_pendingLoads.TryGetValue(filePath, out var existingTcs))
            {
                return await existingTcs.Task;
            }
            return null;
        }
    }

    private async Task<BitmapImage?> GenerateWicThumbnailAsync(string filePath, CancellationToken cancellationToken)
    {
        try
        {
            var storageFile = await StorageFile.GetFileFromPathAsync(filePath).AsTask(cancellationToken);
            using var stream = await storageFile.OpenAsync(FileAccessMode.Read).AsTask(cancellationToken);
            var decoder = await BitmapDecoder.CreateAsync(stream).AsTask(cancellationToken);

            cancellationToken.ThrowIfCancellationRequested();

            BitmapDecoder decoderToUse = decoder;
            string strategyUsed = "原图";

            // 对 RAW 格式保留 Preview 逻辑
            var extension = Path.GetExtension(filePath).ToLower();
            var isRaw = new[] { ".arw", ".cr2", ".nef", ".dng", ".orf", ".rw2", ".raf" }.Contains(extension);
            
            if (isRaw)
            {
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    
                    var preview = await decoder.GetPreviewAsync().AsTask(cancellationToken);
                    if (preview != null && preview.Size > 0)
                    {
                        decoderToUse = await BitmapDecoder.CreateAsync(preview).AsTask(cancellationToken);
                        strategyUsed = "Preview";
                        // DebugService.WriteLine($"[DEBUG-缩略图] 文件: {Path.GetFileName(filePath)}, 使用策略: {strategyUsed}, Preview尺寸: {preview.Size}");
                    }
                }
                catch (Exception ex)
                {
                    // DebugService.WriteLine($"[DEBUG-缩略图] 文件: {Path.GetFileName(filePath)}, Preview 异常: {ex.Message}, 回退到原图策略");
                    cancellationToken.ThrowIfCancellationRequested();
                }
            }

            var orientedWidth = decoderToUse.OrientedPixelWidth;
            var orientedHeight = decoderToUse.OrientedPixelHeight;
            var originalWidth = decoderToUse.PixelWidth;
            var originalHeight = decoderToUse.PixelHeight;

            // 尝试获取 Exif 方向信息（始终从原图获取，而不是从预览图获取）
            uint orientation = 1; // 默认方向
            try
            {
                var metadataReader = decoder.BitmapProperties; // 使用原图的解码器获取方向信息
                var orientationProp = await metadataReader.GetPropertiesAsync(new[] { "System.Photo.Orientation" }).AsTask(cancellationToken);
                if (orientationProp.ContainsKey("System.Photo.Orientation"))
                {
                    var value = orientationProp["System.Photo.Orientation"].Value;
                    // 处理不同类型的方向值
                    try
                    {
                        if (value is uint uintValue)
                        {
                            orientation = uintValue;
                        }
                        else if (value is ushort ushortValue)
                        {
                            orientation = ushortValue;
                        }
                        else if (value is short shortValue)
                        {
                            orientation = (uint)shortValue;
                        }
                        else if (value is int intValue)
                        {
                            orientation = (uint)intValue;
                        }
                        else if (value is object objValue)
                        {
                            // 尝试转换其他类型
                            if (uint.TryParse(objValue.ToString(), out var parsedValue))
                            {
                                orientation = parsedValue;
                            }
                            else
                            {
                                // DebugService.WriteLine($"[DEBUG-缩略图] 文件: {Path.GetFileName(filePath)}, 无法转换方向值: {objValue} (类型: {objValue.GetType().Name})");
                            }
                        }
                        // DebugService.WriteLine($"[DEBUG-缩略图] 文件: {Path.GetFileName(filePath)}, 方向值类型: {value.GetType().Name}, 值: {value}");
                    }
                    catch (Exception ex)
                    {
                        // DebugService.WriteLine($"[DEBUG-缩略图] 文件: {Path.GetFileName(filePath)}, 方向值转换失败: {ex.Message}");
                    }
                }
                else
                {
                    // DebugService.WriteLine($"[DEBUG-缩略图] 文件: {Path.GetFileName(filePath)}, 未找到方向信息");
                }
            }
            catch (Exception ex)
            {
                // DebugService.WriteLine($"[DEBUG-缩略图] 文件: {Path.GetFileName(filePath)}, 获取方向信息失败: {ex.Message}");
            }

            // DebugService.WriteLine($"[DEBUG-缩略图] 文件: {Path.GetFileName(filePath)}, 原始尺寸: {originalWidth}x{originalHeight}, 方向后尺寸: {orientedWidth}x{orientedHeight}, Exif方向: {orientation}");
            // DebugService.WriteLine($"[DEBUG-缩略图] 文件: {Path.GetFileName(filePath)}, 是RAW: {isRaw}, 策略: {strategyUsed}");

            var transform = new BitmapTransform();
            // 移除 Fant 插值算法，使用 NearestNeighbor 提高效率
            transform.InterpolationMode = BitmapInterpolationMode.NearestNeighbor;
            
            // 方向值对应的旋转和翻转
            // 1: 正常（0°）
            // 2: 水平翻转
            // 3: 旋转180°
            // 4: 垂直翻转
            // 5: 顺时针旋转90° + 水平翻转
            // 6: 顺时针旋转90°
            // 7: 顺时针旋转90° + 垂直翻转
            // 8: 逆时针旋转90°（或顺时针270°）
            
            // 设置旋转和翻转
            switch (orientation)
            {
                case 2: // 水平翻转
                    transform.Flip = BitmapFlip.Horizontal;
                    break;
                case 3: // 旋转180°
                    transform.Rotation = BitmapRotation.Clockwise180Degrees;
                    break;
                case 4: // 垂直翻转
                    transform.Flip = BitmapFlip.Vertical;
                    break;
                case 5: // 顺时针旋转90° + 水平翻转
                    transform.Rotation = BitmapRotation.Clockwise90Degrees;
                    transform.Flip = BitmapFlip.Horizontal;
                    break;
                case 6: // 顺时针旋转90°
                    transform.Rotation = BitmapRotation.Clockwise90Degrees;
                    break;
                case 7: // 顺时针旋转90° + 垂直翻转
                    transform.Rotation = BitmapRotation.Clockwise90Degrees;
                    transform.Flip = BitmapFlip.Vertical;
                    break;
                case 8: // 逆时针旋转90°（或顺时针270°）
                    transform.Rotation = BitmapRotation.Clockwise270Degrees;
                    break;
            }
            
            // 计算调整后的尺寸（用于缩放计算）
            uint adjustedWidth = originalWidth;
            uint adjustedHeight = originalHeight;
            if (orientation == 5 || orientation == 6 || orientation == 7 || orientation == 8)
            {
                // 旋转90度，交换宽高
                adjustedWidth = originalHeight;
                adjustedHeight = originalWidth;
            }
            
            var scaleFactor = (double)ThumbnailWidth / Math.Max(adjustedWidth, adjustedHeight);
            
            if (scaleFactor < 1)
            {
                // 使用原始尺寸进行缩放，让 BitmapTransform 自动处理旋转后的尺寸
                transform.ScaledWidth = (uint)(originalWidth * scaleFactor);
                transform.ScaledHeight = (uint)(originalHeight * scaleFactor);
            }

            var softwareBitmap = await decoderToUse.GetSoftwareBitmapAsync(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Premultiplied,
                transform,
                ExifOrientationMode.IgnoreExifOrientation, // 忽略预览图的方向，使用我们自己计算的方向
                ColorManagementMode.DoNotColorManage).AsTask(cancellationToken);

            cancellationToken.ThrowIfCancellationRequested();

            using var outputStream = new InMemoryRandomAccessStream();
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.BmpEncoderId, outputStream).AsTask(cancellationToken);
            encoder.SetSoftwareBitmap(softwareBitmap);
            await encoder.FlushAsync().AsTask(cancellationToken);

            outputStream.Seek(0);
            var bitmap = new BitmapImage();
            await bitmap.SetSourceAsync(outputStream).AsTask(cancellationToken);
            // DebugService.WriteLine($"[DEBUG-缩略图] 文件: {Path.GetFileName(filePath)}, 策略: {strategyUsed}, 输出尺寸: {bitmap.PixelWidth}x{bitmap.PixelHeight}");
            // DebugService.WriteLine($"[DEBUG-缩略图] 文件: {Path.GetFileName(filePath)}, 方向处理完成: 原始{originalWidth}x{originalHeight} → 方向值{orientation} → 调整后{adjustedWidth}x{adjustedHeight} → 输出{bitmap.PixelWidth}x{bitmap.PixelHeight}");

            // 保存到本地缓存
            await SaveSoftwareBitmapToLocalCacheAsync(filePath, softwareBitmap, cancellationToken);

            softwareBitmap.Dispose();
            return bitmap;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            DebugService.WriteLine($"WIC 生成缩略图失败: {filePath}, {ex.Message}");
            return null;
        }
    }

    private async Task AddToCacheAsync(string filePath, BitmapImage thumbnail, PhotoItem? photoItem = null, CancellationToken cancellationToken = default)
    {
        await _cacheLock.WaitAsync(cancellationToken);
        try
        {
            // 检查内存使用情况，动态调整缓存大小
            AdjustCacheSizeBasedOnMemoryUsage();

            while (_thumbnailCache.Count >= _maxCacheSize && _lruList.Count > 0)
            {
                var oldestKey = _lruList.Last!.Value;
                _lruList.RemoveLast();
                
                // 清理对应的 PhotoItem.Thumbnail（不设置为 null，避免影响缓存检查）
                if (_photoItemMap.TryGetValue(oldestKey, out var oldPhotoItem))
                {
                    // 不设置 oldPhotoItem.Thumbnail = null，因为缩略图可能还在内存缓存中
                    // 只是从 _photoItemMap 中移除，让缓存检查方法能够正常工作
                    _photoItemMap.Remove(oldestKey);
                }
                
                if (_thumbnailCache.TryGetValue(oldestKey, out var oldWeakRef))
                {
                    if (oldWeakRef.TryGetTarget(out var oldImage))
                    {
                        oldImage.UriSource = null;
                    }
                }
                _thumbnailCache.Remove(oldestKey);
            }

            _thumbnailCache[filePath] = new WeakReference<BitmapImage>(thumbnail);
            _lruList.AddFirst(filePath);
            
            // 记录 PhotoItem 引用
            if (photoItem != null)
            {
                _photoItemMap[filePath] = photoItem;
            }
        }
        finally
        {
            _cacheLock.Release();
        }

        // 保存到本地缓存
        await SaveToLocalCacheAsync(filePath, thumbnail, cancellationToken);
    }

    private void UpdateLruOrder(string filePath)
    {
        _lruList.Remove(filePath);
        _lruList.AddFirst(filePath);
        
        // 如果文件在 _photoItemMap 中，确保其引用仍然有效
        if (_photoItemMap.TryGetValue(filePath, out var photoItem) && photoItem.Thumbnail != null)
        {
            // 不需要做任何操作，因为 photoItem.Thumbnail 已经存在
            // 这确保了即使从缓存清理了，只要弱引用还存在，photoItem.Thumbnail 仍然有效
        }
    }

    /// <summary>
    /// 异步更新 LRU 顺序
    /// </summary>
    private async Task UpdateLruOrderAsync(string filePath)
    {
        await _cacheLock.WaitAsync();
        try
        {
            UpdateLruOrder(filePath);
        }
        finally
        {
            _cacheLock.Release();
        }
    }

    /// <summary>
    /// 异步移除失效的引用
    /// </summary>
    private async Task RemoveInvalidReferenceAsync(string filePath)
    {
        await _cacheLock.WaitAsync();
        try
        {
            if (_thumbnailCache.TryGetValue(filePath, out var weakRef))
            {
                if (!weakRef.TryGetTarget(out _))
                {
                    _thumbnailCache.Remove(filePath);
                    _lruList.Remove(filePath);
                }
            }
        }
        finally
        {
            _cacheLock.Release();
        }
    }

    public async Task ClearCacheAsync()
    {
        await _cacheLock.WaitAsync();
        try
        {
            // 清理所有 PhotoItem.Thumbnail
            foreach (var photoItem in _photoItemMap.Values)
            {
                photoItem.Thumbnail = null;
            }
            
            foreach (var weakRef in _thumbnailCache.Values)
            {
                if (weakRef.TryGetTarget(out var image))
                {
                    image.UriSource = null;
                }
            }
            _thumbnailCache.Clear();
            _photoItemMap.Clear();
            _lruList.Clear();
        }
        finally
        {
            _cacheLock.Release();
        }
    }

    public async Task<(int count, int maxSize)> GetCacheStatsAsync()
    {
        await _cacheLock.WaitAsync();
        try
        {
            // 统计有效引用的数量
            int validCount = 0;
            foreach (var weakRef in _thumbnailCache.Values)
            {
                if (weakRef.TryGetTarget(out _))
                {
                    validCount++;
                }
            }
            return (validCount, _maxCacheSize);
        }
        finally
        {
            _cacheLock.Release();
        }
    }

    /// <summary>
    /// 获取本地缓存目录路径
    /// </summary>
    public async Task<string?> GetCachePathAsync(CancellationToken cancellationToken = default)
    {
        await InitializeCacheFolderAsync(cancellationToken);
        return _cachePath;
    }

    /// <summary>
    /// 获取本地缓存大小（字节）
    /// </summary>
    public async Task<long> GetLocalCacheSizeAsync(CancellationToken cancellationToken = default)
    {
        await InitializeCacheFolderAsync(cancellationToken);
        if (_cachePath == null || !Directory.Exists(_cachePath))
            return 0;

        return GetDirectorySize(_cachePath);
    }

    /// <summary>
    /// 清除所有本地缓存文件
    /// </summary>
    public async Task ClearLocalCacheAsync(CancellationToken cancellationToken = default)
    {
        await InitializeCacheFolderAsync(cancellationToken);
        if (_cachePath == null || !Directory.Exists(_cachePath))
            return;

        try
        {
            var files = Directory.GetFiles(_cachePath, "*", SearchOption.AllDirectories);
            foreach (var file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    File.Delete(file);
                }
                catch (Exception ex)
                {
                    DebugService.WriteLine($"删除缓存文件失败: {file}, 错误: {ex.Message}");
                }
            }

            // 同时清理内存缓存
            await ClearCacheAsync();
            
            DebugService.WriteLine($"已清除所有本地缓存，共 {files.Length} 个文件");
        }
        catch (Exception ex)
        {
            DebugService.WriteLine($"清除本地缓存失败: {ex.Message}");
        }
    }

    #region 统计和监控方法

    /// <summary>
    /// 更新加载统计
    /// </summary>
    private void UpdateLoadStats(long elapsedMilliseconds)
    {
        _totalLoadTime += elapsedMilliseconds;
        _loadCount++;
    }

    /// <summary>
    /// 获取详细缓存统计信息
    /// </summary>
    public async Task<CacheStats> GetDetailedCacheStatsAsync()
    {
        await _cacheLock.WaitAsync();
        try
        {
            // 统计有效引用的数量
            int validCount = 0;
            foreach (var weakRef in _thumbnailCache.Values)
            {
                if (weakRef.TryGetTarget(out _))
                {
                    validCount++;
                }
            }

            // 计算缓存命中率
            int totalCacheAccesses = _cacheHits + _cacheMisses;
            double memoryCacheHitRate = totalCacheAccesses > 0 ? (double)_cacheHits / totalCacheAccesses * 100 : 0;

            int totalLocalCacheAccesses = _localCacheHits + _localCacheMisses;
            double localCacheHitRate = totalLocalCacheAccesses > 0 ? (double)_localCacheHits / totalLocalCacheAccesses * 100 : 0;

            // 计算平均加载时间
            long averageLoadTime = _loadCount > 0 ? _totalLoadTime / _loadCount : 0;

            return new CacheStats
            {
                MemoryCacheCount = validCount,
                MemoryCacheMaxSize = _maxCacheSize,
                MemoryCacheHitRate = memoryCacheHitRate,
                LocalCacheHitRate = localCacheHitRate,
                AverageLoadTimeMs = averageLoadTime,
                TotalLoadCount = _loadCount
            };
        }
        finally
        {
            _cacheLock.Release();
        }
    }

    /// <summary>
    /// 重置统计数据
    /// </summary>
    public void ResetStats()
    {
        _cacheHits = 0;
        _cacheMisses = 0;
        _localCacheHits = 0;
        _localCacheMisses = 0;
        _totalLoadTime = 0;
        _loadCount = 0;
    }

    /// <summary>
    /// 打印统计信息
    /// </summary>
    public async Task PrintStatsAsync()
    {
        var stats = await GetDetailedCacheStatsAsync();
        DebugService.WriteLine($"=== 缩略图缓存统计 ===");
        DebugService.WriteLine($"内存缓存数量: {stats.MemoryCacheCount}/{stats.MemoryCacheMaxSize}");
        DebugService.WriteLine($"内存缓存命中率: {stats.MemoryCacheHitRate:F2}%");
        DebugService.WriteLine($"本地缓存命中率: {stats.LocalCacheHitRate:F2}%");
        DebugService.WriteLine($"平均加载时间: {stats.AverageLoadTimeMs}ms");
        DebugService.WriteLine($"总加载次数: {stats.TotalLoadCount}");
        DebugService.WriteLine($"====================");
    }

    #endregion

    #region 内存管理方法

    /// <summary>
    /// 启动内存监控
    /// </summary>
    private void StartMemoryMonitoring()
    {
        try
        {
            _memoryMonitoringEnabled = true;
            DebugService.WriteLine("内存监控已启动");
        }
        catch (Exception ex)
        {
            DebugService.WriteLine($"启动内存监控失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 根据内存使用情况调整缓存大小
    /// </summary>
    private void AdjustCacheSizeBasedOnMemoryUsage()
    {
        try
        {
            // 获取当前内存使用情况
            var memoryUsage = GC.GetTotalMemory(false);
            var memoryUsageMB = memoryUsage / (1024 * 1024);

            // 获取设备总内存（近似值）
            var totalMemoryMB = GetTotalMemoryMB();
            
            // 计算内存使用百分比
            var memoryUsagePercent = totalMemoryMB > 0 ? (double)memoryUsageMB / totalMemoryMB * 100 : 0;

            // 根据内存使用情况和设备性能动态调整缓存大小
            if (memoryUsagePercent > 70 || memoryUsageMB > 500)
            {
                // 内存压力大，减小缓存大小
                _maxCacheSize = 300;
                DebugService.WriteLine($"内存压力大，缓存大小调整为: {_maxCacheSize}");
            }
            else if (memoryUsagePercent > 50 || memoryUsageMB > 300)
            {
                // 内存压力中等，保持适中缓存大小
                _maxCacheSize = 600;
                DebugService.WriteLine($"内存压力中等，缓存大小调整为: {_maxCacheSize}");
            }
            else if (memoryUsagePercent > 30)
            {
                // 内存压力小，使用较大缓存大小
                _maxCacheSize = 800;
                DebugService.WriteLine($"内存压力小，缓存大小调整为: {_maxCacheSize}");
            }
            else
            {
                // 内存充足，使用最大缓存大小
                _maxCacheSize = 1000;
                DebugService.WriteLine($"内存充足，缓存大小调整为: {_maxCacheSize}");
            }

            // 清理失效的引用
            CleanupWeakReferences();
        }
        catch (Exception ex)
        {
            DebugService.WriteLine($"调整缓存大小失败: {ex.Message}");
        }
    }
    
    /// <summary>
    /// 获取设备总内存（近似值）
    /// </summary>
    /// <returns>总内存大小（MB）</returns>
    private int GetTotalMemoryMB()
    {
        try
        {
            // 使用 Environment.WorkingSet 作为近似值
            // 注意：这不是设备的实际总内存，但可以作为参考
            var workingSetMB = (int)(Environment.WorkingSet / (1024 * 1024));
            // 假设工作集占总内存的一定比例，估算总内存
            return workingSetMB * 4; // 粗略估算，实际值可能不同
        }
        catch
        {
            // 如果获取失败，返回默认值
            return 8192; // 假设8GB内存
        }
    }

    /// <summary>
    /// 清理失效的弱引用
    /// </summary>
    private void CleanupWeakReferences()
    {
        try
        {
            var keysToRemove = new List<string>();
            foreach (var kvp in _thumbnailCache)
            {
                if (!kvp.Value.TryGetTarget(out _))
                {
                    keysToRemove.Add(kvp.Key);
                }
            }

            foreach (var key in keysToRemove)
            {
                _thumbnailCache.Remove(key);
                _lruList.Remove(key);
                
                // 清理对应的 PhotoItem.Thumbnail
                if (_photoItemMap.TryGetValue(key, out var photoItem))
                {
                    photoItem.Thumbnail = null;
                    _photoItemMap.Remove(key);
                }
            }

            if (keysToRemove.Count > 0)
            {
                DebugService.WriteLine($"清理了 {keysToRemove.Count} 个失效的弱引用");
            }
        }
        catch (Exception ex)
        {
            DebugService.WriteLine($"清理弱引用失败: {ex.Message}");
        }
    }

    #endregion

    #region 本地缓存方法

    /// <summary>
    /// 初始化缓存目录
    /// </summary>
    private async Task InitializeCacheFolderAsync(CancellationToken cancellationToken = default)
    {
        // 如果已经初始化或之前失败了，直接返回
        if (_cachePath != null || _cacheInitFailed)
            return;

        // 使用信号量确保只有一个线程在执行初始化
        await _cacheLock.WaitAsync(cancellationToken);
        try
        {
            // 双重检查
            if (_cachePath != null || _cacheInitFailed)
                return;

            string cacheBasePath;
            try
            {
                // 尝试获取 MSIX 打包路径
                cacheBasePath = ApplicationData.Current.LocalCacheFolder.Path;
            }
            catch (InvalidOperationException)
            {
                // 非打包环境 (Unpackaged)，回退到 AppData/Local
                cacheBasePath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "FastPick", 
                    "Cache");
            }

            var fullPath = Path.Combine(cacheBasePath, CacheFolderName);
            if (!Directory.Exists(fullPath))
            {
                Directory.CreateDirectory(fullPath);
            }

            _cachePath = fullPath;
            DebugService.WriteLine($"缓存目录初始化成功: {fullPath}");
            
            // 初始化时清理过期缓存
            await CleanupLocalCacheAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _cacheInitFailed = true;
            DebugService.WriteLine($"彻底初始化失败: {ex.Message}");
        }
        finally
        {
            _cacheLock.Release();
        }
    }

    /// <summary>
    /// 并行批量读取本地缓存
    /// </summary>
    public async Task<Dictionary<string, BitmapImage?>> GetThumbnailsAsync(IEnumerable<PhotoItem> photoItems, CancellationToken cancellationToken = default)
    {
        var tasks = photoItems.Select(item => Task.Run(async () =>
        {
            var thumbnail = await GetThumbnailAsync(item, cancellationToken);
            return (item.DisplayPath, thumbnail);
        }, cancellationToken));

        var results = await Task.WhenAll(tasks);
        return results.ToDictionary(r => r.DisplayPath, r => r.thumbnail);
    }

    /// <summary>
    /// 预加载附近的缩略图缓存
    /// </summary>
    public async Task PreloadThumbnailsAsync(IEnumerable<PhotoItem> photoItems, CancellationToken cancellationToken = default)
    {
        try
        {
            // 限制预加载的数量
            var itemsToPreload = photoItems.Take(20).ToList();
            if (itemsToPreload.Count == 0)
                return;

            // 并行预加载，触发解码
            var tasks = itemsToPreload.Select(item => Task.Run(async () =>
            {
                // 触发完整的缩略图加载和解码
                await GetThumbnailAsync(item, cancellationToken);
            }, cancellationToken));

            await Task.WhenAll(tasks);
            DebugService.WriteLine($"预加载了 {itemsToPreload.Count} 个缩略图");
        }
        catch (Exception ex)
        {
            DebugService.WriteLine($"预加载缩略图失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 后台持续解码缩略图
    /// </summary>
    public async Task StartBackgroundDecodingAsync(IEnumerable<PhotoItem> allPhotoItems, CancellationToken cancellationToken = default)
    {
        try
        {
            var photoItems = allPhotoItems.ToList();
            if (photoItems.Count == 0)
                return;

            // 筛选出还没有缩略图的照片
            var itemsWithoutThumbnail = photoItems.Where(item => item.Thumbnail == null).ToList();
            if (itemsWithoutThumbnail.Count == 0)
            {
                DebugService.WriteLine("所有缩略图已解码完成");
                return;
            }

            // 根据视区信息对缩略图进行优先级排序
            var prioritizedItems = itemsWithoutThumbnail.OrderBy(item =>
            {
                int itemIndex = photoItems.IndexOf(item);
                // 计算与视区的距离
                if (itemIndex >= _viewportStartIndex && itemIndex <= _viewportEndIndex)
                {
                    // 视区内的缩略图优先级最高
                    return 0;
                }
                else if (itemIndex >= _viewportStartIndex - 10 && itemIndex <= _viewportEndIndex + 10)
                {
                    // 视区附近的缩略图优先级次之
                    return 1;
                }
                else
                {
                    // 其他区域的缩略图优先级最低
                    return 2;
                }
            }).ToList();

            // 按批次解码，每批10个，避免占用过多资源
            int batchSize = 10;
            int totalBatches = (int)Math.Ceiling((double)prioritizedItems.Count / batchSize);

            for (int i = 0; i < totalBatches; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var batch = prioritizedItems.Skip(i * batchSize).Take(batchSize).ToList();
                if (batch.Count == 0)
                    break;

                // 顺序处理当前批次，避免并发冲突
                foreach (var item in batch)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    
                    try
                    {
                        // 只有当缩略图为null时才加载，避免重复加载
                        if (item.Thumbnail == null)
                        {
                            await GetThumbnailAsync(item, cancellationToken);
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        // 忽略取消异常
                    }
                    catch (Exception ex)
                    {
                        DebugService.WriteLine($"后台解码失败: {item.DisplayPath}, {ex.Message}");
                    }
                }

                DebugService.WriteLine($"后台解码完成批次 {i + 1}/{totalBatches}");

                // 每批次后短暂延迟，避免UI卡顿
                await Task.Delay(150, cancellationToken);
            }

            DebugService.WriteLine($"后台解码完成，共处理 {prioritizedItems.Count} 个缩略图");
        }
        catch (OperationCanceledException)
        {
            DebugService.WriteLine("后台解码任务已取消");
        }
        catch (Exception ex)
        {
            DebugService.WriteLine($"后台解码任务失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 清理本地缓存
    /// </summary>
    private async Task CleanupLocalCacheAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await InitializeCacheFolderAsync(cancellationToken);
            if (_cachePath == null)
                return;

            var cachePath = _cachePath;
            var maxCacheSizeMB = 100; // 最大缓存大小 100MB
            
            // 清理过期缓存
            var cleanedCount = await CleanupExpiredCacheAsync(cachePath, cancellationToken);
            
            // 检查缓存大小
            var cacheSizeMB = GetDirectorySize(cachePath) / (1024 * 1024);
            if (cacheSizeMB > maxCacheSizeMB)
            {
                // 按文件修改时间删除最旧的缓存
                await CleanupOldestCacheAsync(cachePath, maxCacheSizeMB, cancellationToken);
            }
        }
        catch (Exception ex)
        {
            DebugService.WriteLine($"清理本地缓存失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 清理过期缓存
    /// </summary>
    private async Task<int> CleanupExpiredCacheAsync(string cachePath, CancellationToken cancellationToken = default)
    {
        int cleanedCount = 0;
        try
        {
            var files = Directory.GetFiles(cachePath, "*.png", SearchOption.AllDirectories);
            foreach (var file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                
                var fileInfo = new FileInfo(file);
                var cacheAge = DateTime.UtcNow - fileInfo.LastWriteTimeUtc;
                if (cacheAge.TotalDays > 7)
                {
                    try
                    {
                        File.Delete(file);
                        cleanedCount++;
                    }
                    catch (Exception ex)
                    {
                        DebugService.WriteLine($"删除过期缓存文件失败: {ex.Message}");
                    }
                }
            }
            
            if (cleanedCount > 0)
            {
                DebugService.WriteLine($"清理了 {cleanedCount} 个过期缓存文件");
            }
        }
        catch (Exception ex)
        {
            DebugService.WriteLine($"清理过期缓存失败: {ex.Message}");
        }
        return cleanedCount;
    }

    /// <summary>
    /// 清理最旧的缓存以限制大小
    /// </summary>
    private async Task CleanupOldestCacheAsync(string cachePath, long maxSizeMB, CancellationToken cancellationToken = default)
    {
        try
        {
            var files = Directory.GetFiles(cachePath, "*.png", SearchOption.AllDirectories)
                .Select(path => new FileInfo(path))
                .OrderBy(info => info.LastWriteTimeUtc)
                .ToList();
            
            long currentSizeMB = GetDirectorySize(cachePath) / (1024 * 1024);
            int deletedCount = 0;
            
            foreach (var file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                
                if (currentSizeMB <= maxSizeMB)
                    break;
                
                try
                {
                    var fileSizeMB = file.Length / (1024 * 1024);
                    File.Delete(file.FullName);
                    currentSizeMB -= fileSizeMB;
                    deletedCount++;
                }
                catch (Exception ex)
                {
                    DebugService.WriteLine($"删除缓存文件失败: {ex.Message}");
                }
            }
            
            if (deletedCount > 0)
            {
                DebugService.WriteLine($"为限制缓存大小，删除了 {deletedCount} 个最旧的缓存文件");
            }
        }
        catch (Exception ex)
        {
            DebugService.WriteLine($"清理最旧缓存失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 获取目录大小
    /// </summary>
    private long GetDirectorySize(string directoryPath)
    {
        try
        {
            var files = Directory.GetFiles(directoryPath, "*.*", SearchOption.AllDirectories);
            long size = 0;
            foreach (var file in files)
            {
                try
                {
                    size += new FileInfo(file).Length;
                }
                catch { }
            }
            return size;
        }
        catch (Exception ex)
        {
            DebugService.WriteLine($"获取目录大小失败: {ex.Message}");
            return 0;
        }
    }

    /// <summary>
    /// 生成缓存键
    /// </summary>
    private string GenerateCacheKey(string filePath)
    {
        try
        {
            var fileInfo = new FileInfo(filePath);
            var lastWriteTime = fileInfo.LastWriteTimeUtc.ToString("yyyyMMddHHmmssfff");
            var combined = $"{filePath}_{lastWriteTime}_{ThumbnailWidth}";
            
            using var md5 = MD5.Create();
            var bytes = Encoding.UTF8.GetBytes(combined);
            var hash = md5.ComputeHash(bytes);
            var cacheKey = BitConverter.ToString(hash).Replace("-", "").ToLower();
            
            // 创建目录结构，避免文件名过长
            var dir1 = cacheKey.Substring(0, 2);
            var dir2 = cacheKey.Substring(2, 2);
            return Path.Combine(dir1, dir2, $"{cacheKey}.png");
        }
        catch (Exception ex)
        {
            DebugService.WriteLine($"生成缓存键失败: {ex.Message}");
            return Path.GetFileName(filePath) + ".png";
        }
    }

    /// <summary>
    /// 从本地缓存读取缩略图
    /// </summary>
    private async Task<BitmapImage?> GetFromLocalCacheAsync(string filePath, CancellationToken cancellationToken = default)
    {
        // 如果缓存之前初始化失败，直接返回 null
        if (_cacheInitFailed)
            return null;
            
        try
        {
            await InitializeCacheFolderAsync(cancellationToken);
            if (_cachePath == null)
                return null;
            
            var cacheKey = GenerateCacheKey(filePath);
            var cacheFilePath = Path.Combine(_cachePath, cacheKey);

            if (File.Exists(cacheFilePath))
            {
                // 检查缓存是否有效（文件未修改）
                if (IsCacheValid(filePath, cacheFilePath))
                {
                    using var fileStream = new FileStream(cacheFilePath, FileMode.Open, FileAccess.Read);
                    using var randomAccessStream = fileStream.AsRandomAccessStream();
                    
                    var bitmap = new BitmapImage();
                    await bitmap.SetSourceAsync(randomAccessStream).AsTask(cancellationToken);
                    
                    // 从本地缓存加载成功后，更新内存缓存和LRU顺序
                    await AddToCacheAsync(filePath, bitmap, null, cancellationToken);
                    
                    return bitmap;
                }
                else
                {
                    // 缓存无效，删除并返回 null
                    try
                    {
                        File.Delete(cacheFilePath);
                        DebugService.WriteLine($"[DEBUG-缩略图] 文件: {Path.GetFileName(filePath)}, 缓存已过期，已删除");
                    }
                    catch (Exception ex)
                    {
                        DebugService.WriteLine($"删除过期缓存失败: {ex.Message}");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            DebugService.WriteLine($"从本地缓存读取失败: {ex.Message}");
        }
        return null;
    }

    /// <summary>
    /// 检查缓存是否有效
    /// </summary>
    private bool IsCacheValid(string filePath, string cacheFilePath)
    {
        try
        {
            var fileInfo = new FileInfo(filePath);
            var cacheInfo = new FileInfo(cacheFilePath);
            
            // 检查文件修改时间
            var fileLastWriteTime = fileInfo.LastWriteTimeUtc;
            var cacheLastWriteTime = cacheInfo.LastWriteTimeUtc;
            
            // 检查缓存是否过期（7天）
            var cacheAge = DateTime.UtcNow - cacheLastWriteTime;
            if (cacheAge.TotalDays > 7)
            {
                return false;
            }
            
            // 检查缓存文件名中的时间戳是否与文件修改时间匹配
            // 由于缓存键包含了文件修改时间，这里可以简化检查
            // 只要缓存文件存在且未过期，就认为有效
            return true;
        }
        catch (Exception ex)
        {
            DebugService.WriteLine($"检查缓存有效性失败: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 保存缩略图到本地缓存
    /// </summary>
    private async Task SaveToLocalCacheAsync(string filePath, BitmapImage thumbnail, CancellationToken cancellationToken = default)
    {
        // 此方法暂时未使用，使用 SaveSoftwareBitmapToLocalCacheAsync 替代
    }

    /// <summary>
    /// 将 SoftwareBitmap 保存到本地缓存
    /// </summary>
    private async Task SaveSoftwareBitmapToLocalCacheAsync(string filePath, SoftwareBitmap softwareBitmap, CancellationToken cancellationToken = default)
    {
        // 如果缓存之前初始化失败，直接返回
        if (_cacheInitFailed)
            return;
            
        try
        {
            await InitializeCacheFolderAsync(cancellationToken);
            if (_cachePath == null)
                return;

            var cacheKey = GenerateCacheKey(filePath);
            var cacheFilePath = Path.Combine(_cachePath, cacheKey);

            // 创建目录结构
            var directory = Path.GetDirectoryName(cacheFilePath);
            if (!Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory!);
            }

            // 将 SoftwareBitmap 编码为 PNG 格式
            using var fileStream = new FileStream(cacheFilePath, FileMode.Create, FileAccess.Write);
            using var randomAccessStream = fileStream.AsRandomAccessStream();
            
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, randomAccessStream).AsTask(cancellationToken);
            encoder.SetSoftwareBitmap(softwareBitmap);
            await encoder.FlushAsync().AsTask(cancellationToken);
            
            DebugService.WriteLine($"[DEBUG-缩略图] 文件: {Path.GetFileName(filePath)}, 保存到本地缓存: {cacheFilePath}");
        }
        catch (Exception ex)
        {
            DebugService.WriteLine($"保存 SoftwareBitmap 到本地缓存失败: {ex.Message}");
        }
    }

    #endregion
}

/// <summary>
/// 缓存统计信息
/// </summary>
public class CacheStats
{
    /// <summary>
    /// 内存缓存数量
    /// </summary>
    public int MemoryCacheCount { get; set; }
    
    /// <summary>
    /// 内存缓存最大大小
    /// </summary>
    public int MemoryCacheMaxSize { get; set; }
    
    /// <summary>
    /// 内存缓存命中率
    /// </summary>
    public double MemoryCacheHitRate { get; set; }
    
    /// <summary>
    /// 本地缓存命中率
    /// </summary>
    public double LocalCacheHitRate { get; set; }
    
    /// <summary>
    /// 平均加载时间（毫秒）
    /// </summary>
    public long AverageLoadTimeMs { get; set; }
    
    /// <summary>
    /// 总加载次数
    /// </summary>
    public int TotalLoadCount { get; set; }
}
