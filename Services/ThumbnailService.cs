using FastPick.Models;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using System.Collections.Concurrent;
using System.IO;

namespace FastPick.Services;

public class ThumbnailService
{
    private readonly LinkedList<string> _lruList = new();
    private readonly Dictionary<string, BitmapImage> _thumbnailCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, PhotoItem> _photoItemMap = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _cacheLock = new(1, 1);
    private readonly SemaphoreSlim _throttler = new(Environment.ProcessorCount, Environment.ProcessorCount);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<BitmapImage?>> _pendingLoads = new(StringComparer.OrdinalIgnoreCase);
    private const int MaxCacheSize = 1000;
    private const int ThumbnailWidth = 256;

    public async Task<BitmapImage?> GetThumbnailAsync(PhotoItem photoItem, CancellationToken cancellationToken = default)
    {
        var filePath = photoItem.DisplayPath;
        
        if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
            return null;

        await _cacheLock.WaitAsync(cancellationToken);
        try
        {
            if (_thumbnailCache.TryGetValue(filePath, out var cachedImage))
            {
                UpdateLruOrder(filePath);
                return cachedImage;
            }
        }
        finally
        {
            _cacheLock.Release();
        }

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
                        return wicThumbnail;
                    }

                    tcs.SetResult(null);
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
                System.Diagnostics.Debug.WriteLine($"获取缩略图失败: {filePath}, {ex.Message}");
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
            string failureReason = "";

            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                
                var preview = await decoder.GetPreviewAsync().AsTask(cancellationToken);
                if (preview != null && preview.Size > 0)
                {
                    decoderToUse = await BitmapDecoder.CreateAsync(preview).AsTask(cancellationToken);
                    strategyUsed = "Preview";
                    System.Diagnostics.Debug.WriteLine($"[DEBUG-缩略图] 文件: {Path.GetFileName(filePath)}, 使用策略: {strategyUsed}, Preview尺寸: {preview.Size}");
                }
                else
                {
                    failureReason = "Preview 为空或尺寸为0";
                }
            }
            catch (Exception ex)
            {
                failureReason = $"Preview 异常: {ex.Message}";
                cancellationToken.ThrowIfCancellationRequested();
            }

            if (!string.IsNullOrEmpty(failureReason))
            {
                System.Diagnostics.Debug.WriteLine($"[DEBUG-缩略图] 文件: {Path.GetFileName(filePath)}, {failureReason}, 回退到原图策略");
            }

            cancellationToken.ThrowIfCancellationRequested();

            var orientedWidth = decoderToUse.OrientedPixelWidth;
            var orientedHeight = decoderToUse.OrientedPixelHeight;
            var originalWidth = decoderToUse.PixelWidth;
            var originalHeight = decoderToUse.PixelHeight;

            System.Diagnostics.Debug.WriteLine($"[DEBUG-缩略图] 文件: {Path.GetFileName(filePath)}, 原始尺寸: {originalWidth}x{originalHeight}, 方向后尺寸: {orientedWidth}x{orientedHeight}");

            var transform = new BitmapTransform();
            transform.InterpolationMode = BitmapInterpolationMode.Fant;
            var scaleFactor = (double)ThumbnailWidth / Math.Max(orientedWidth, orientedHeight);
            
            if (scaleFactor < 1)
            {
                transform.ScaledWidth = (uint)(originalWidth * scaleFactor);
                transform.ScaledHeight = (uint)(originalHeight * scaleFactor);
            }

            var softwareBitmap = await decoderToUse.GetSoftwareBitmapAsync(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Premultiplied,
                transform,
                ExifOrientationMode.RespectExifOrientation,
                ColorManagementMode.DoNotColorManage).AsTask(cancellationToken);

            cancellationToken.ThrowIfCancellationRequested();

            using var outputStream = new InMemoryRandomAccessStream();
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.BmpEncoderId, outputStream).AsTask(cancellationToken);
            encoder.SetSoftwareBitmap(softwareBitmap);
            await encoder.FlushAsync().AsTask(cancellationToken);

            outputStream.Seek(0);
            var bitmap = new BitmapImage();
            await bitmap.SetSourceAsync(outputStream).AsTask(cancellationToken);
            System.Diagnostics.Debug.WriteLine($"[DEBUG-缩略图] 文件: {Path.GetFileName(filePath)}, 最终策略: {strategyUsed}, 输出尺寸: {bitmap.PixelWidth}x{bitmap.PixelHeight}");

            softwareBitmap.Dispose();
            return bitmap;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"WIC 生成缩略图失败: {filePath}, {ex.Message}");
            return null;
        }
    }

    private async Task AddToCacheAsync(string filePath, BitmapImage thumbnail, PhotoItem? photoItem = null, CancellationToken cancellationToken = default)
    {
        await _cacheLock.WaitAsync(cancellationToken);
        try
        {
            while (_thumbnailCache.Count >= MaxCacheSize && _lruList.Count > 0)
            {
                var oldestKey = _lruList.Last!.Value;
                _lruList.RemoveLast();
                
                // 清理对应的 PhotoItem.Thumbnail
                if (_photoItemMap.TryGetValue(oldestKey, out var oldPhotoItem))
                {
                    oldPhotoItem.Thumbnail = null;
                    _photoItemMap.Remove(oldestKey);
                }
                
                if (_thumbnailCache.TryGetValue(oldestKey, out var oldImage))
                {
                    oldImage.UriSource = null;
                }
                _thumbnailCache.Remove(oldestKey);
            }

            _thumbnailCache[filePath] = thumbnail;
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
    }

    private void UpdateLruOrder(string filePath)
    {
        _lruList.Remove(filePath);
        _lruList.AddFirst(filePath);
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
            
            foreach (var image in _thumbnailCache.Values)
            {
                image.UriSource = null;
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
            return (_thumbnailCache.Count, MaxCacheSize);
        }
        finally
        {
            _cacheLock.Release();
        }
    }
}
