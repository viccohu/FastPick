using FastPick.Models;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.FileProperties;
using Windows.Storage;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using System.Collections.Concurrent;

namespace FastPick.Services;

public class ThumbnailService
{
    private readonly LinkedList<string> _lruList = new();
    private readonly Dictionary<string, BitmapImage> _thumbnailCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, PhotoItem> _photoItemMap = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _cacheLock = new(1, 1);
    private readonly SemaphoreSlim _throttler = new(10, 12);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<BitmapImage?>> _pendingLoads = new(StringComparer.OrdinalIgnoreCase);
    private const int MaxCacheSize = 2000;
    private const int MaxThumbnailSize = 256;

    public async Task<BitmapImage?> GetThumbnailAsync(PhotoItem photoItem, CancellationToken cancellationToken = default)
    {
        var filePath = photoItem.DisplayPath;
        
        if (photoItem.HasJpg && !string.IsNullOrEmpty(photoItem.JpgPath) && File.Exists(photoItem.JpgPath))
        {
            filePath = photoItem.JpgPath;
        }
        
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
                    
                    var systemThumbnail = await GetSystemThumbnailAsync(filePath, cancellationToken);
                    if (systemThumbnail != null)
                    {
                        await AddToCacheAsync(filePath, systemThumbnail, photoItem, cancellationToken);
                        tcs.SetResult(systemThumbnail);
                        return systemThumbnail;
                    }

                    cancellationToken.ThrowIfCancellationRequested();
                    
                    var extension = Path.GetExtension(filePath).ToLowerInvariant();
                    var rawExtensions = new[] { ".arw", ".cr2", ".cr3", ".nef", ".nrw", ".orf", ".pef", ".raf", ".raw", ".rw2", ".srw" };
                    
                    if (rawExtensions.Contains(extension))
                    {
                        LoggerService.Instance.Verbose(LogCategory.Thumbnail, $"尝试获取RAW内嵌预览: {Path.GetFileName(filePath)}");
                        var embeddedPreview = await TryGetRawEmbeddedPreviewAsync(filePath, cancellationToken);
                        if (embeddedPreview != null)
                        {
                            var embeddedBitmap = await SoftwareBitmapToBitmapImageAsync(embeddedPreview, cancellationToken);
                            if (embeddedBitmap != null)
                            {
                                await AddToCacheAsync(filePath, embeddedBitmap, photoItem, cancellationToken);
                                tcs.SetResult(embeddedBitmap);
                                return embeddedBitmap;
                            }
                        }
                    }

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
                LoggerService.Instance.Error(LogCategory.Thumbnail, $"获取缩略图失败: {filePath}", ex);
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

    private async Task<BitmapImage?> GetSystemThumbnailAsync(string filePath, CancellationToken cancellationToken)
    {
        try
        {
            var storageFile = await StorageFile.GetFileFromPathAsync(filePath).AsTask(cancellationToken);

            LoggerService.Instance.Verbose(LogCategory.Thumbnail, $"获取系统缩略图: {Path.GetFileName(filePath)}");
            
            using var thumbnail = await storageFile.GetThumbnailAsync(
                ThumbnailMode.SingleItem,
                (uint)MaxThumbnailSize,
                ThumbnailOptions.UseCurrentScale).AsTask(cancellationToken);

            if (thumbnail == null)
                return null;

            var bitmap = new BitmapImage();
            await bitmap.SetSourceAsync(thumbnail).AsTask(cancellationToken);

            return bitmap;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            LoggerService.Instance.Warning(LogCategory.Thumbnail, $"获取系统缩略图失败: {filePath}: {ex.Message}");
        }

        return null;
    }

    private async Task<BitmapImage?> GenerateWicThumbnailAsync(string filePath, CancellationToken cancellationToken)
    {
        try
        {
            var storageFile = await StorageFile.GetFileFromPathAsync(filePath).AsTask(cancellationToken);
            using var stream = await storageFile.OpenAsync(FileAccessMode.Read).AsTask(cancellationToken);
            var decoder = await BitmapDecoder.CreateAsync(stream).AsTask(cancellationToken);

            uint width = decoder.OrientedPixelWidth;
            uint height = decoder.OrientedPixelHeight;

            double scale = Math.Min(
                (double)MaxThumbnailSize / width,
                (double)MaxThumbnailSize / height
            );

            var transform = new BitmapTransform();
            if (scale < 1)
            {
                transform.ScaledWidth = (uint)(width * scale);
                transform.ScaledHeight = (uint)(height * scale);
            }

            using var softwareBitmap = await decoder.GetSoftwareBitmapAsync(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Premultiplied,
                transform,
                ExifOrientationMode.RespectExifOrientation,
                ColorManagementMode.DoNotColorManage).AsTask(cancellationToken);

            using var outputStream = new InMemoryRandomAccessStream();
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.BmpEncoderId, outputStream).AsTask(cancellationToken);
            encoder.SetSoftwareBitmap(softwareBitmap);
            await encoder.FlushAsync().AsTask(cancellationToken);

            outputStream.Seek(0);
            var bitmap = new BitmapImage();
            await bitmap.SetSourceAsync(outputStream).AsTask(cancellationToken);

            return bitmap;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            LoggerService.Instance.Warning(LogCategory.Thumbnail, $"WIC 生成缩略图失败: {filePath}: {ex.Message}");
            return null;
        }
    }

    private async Task<SoftwareBitmap?> TryGetRawEmbeddedPreviewAsync(string filePath, CancellationToken cancellationToken)
    {
        try
        {
            var storageFile = await StorageFile.GetFileFromPathAsync(filePath).AsTask(cancellationToken);
            using var stream = await storageFile.OpenAsync(FileAccessMode.Read).AsTask(cancellationToken);
            var decoder = await BitmapDecoder.CreateAsync(stream).AsTask(cancellationToken);

            BitmapRotation orientationRotation = BitmapRotation.None;
            bool isFlippedHorizontal = false;
            try
            {
                var properties = decoder.BitmapProperties;
                const string orientationQuery = "System.Photo.Orientation";
                var result = await properties.GetPropertiesAsync(new[] { orientationQuery }).AsTask(cancellationToken);
                if (result.TryGetValue(orientationQuery, out var orientationValue) && orientationValue.Value is ushort orientation)
                {
                    (orientationRotation, isFlippedHorizontal) = orientation switch
                    {
                        1 => (BitmapRotation.None, false),
                        2 => (BitmapRotation.None, true),
                        3 => (BitmapRotation.Clockwise180Degrees, false),
                        4 => (BitmapRotation.Clockwise180Degrees, true),
                        5 => (BitmapRotation.Clockwise90Degrees, true),
                        6 => (BitmapRotation.Clockwise90Degrees, false),
                        7 => (BitmapRotation.Clockwise270Degrees, true),
                        8 => (BitmapRotation.Clockwise270Degrees, false),
                        _ => (BitmapRotation.None, false)
                    };
                }
            }
            catch
            {
            }

            BitmapFrame? previewFrame = null;
            try
            {
                var previewStream = await decoder.GetPreviewAsync().AsTask(cancellationToken);
                if (previewStream != null)
                {
                    var previewDecoder = await BitmapDecoder.CreateAsync(previewStream).AsTask(cancellationToken);
                    previewFrame = await previewDecoder.GetFrameAsync(0).AsTask(cancellationToken);
                }
            }
            catch
            {
                return null;
            }

            if (previewFrame == null)
            {
                return null;
            }

            var transform = new BitmapTransform
            {
                Rotation = orientationRotation,
                Flip = isFlippedHorizontal ? BitmapFlip.Horizontal : BitmapFlip.None
            };

            var softwareBitmap = await previewFrame.GetSoftwareBitmapAsync(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Premultiplied,
                transform,
                ExifOrientationMode.IgnoreExifOrientation,
                ColorManagementMode.DoNotColorManage).AsTask(cancellationToken);

            return softwareBitmap;
        }
        catch
        {
            return null;
        }
    }

    private async Task<BitmapImage?> SoftwareBitmapToBitmapImageAsync(SoftwareBitmap softwareBitmap, CancellationToken cancellationToken)
    {
        try
        {
            var orientedWidth = softwareBitmap.PixelWidth;
            var orientedHeight = softwareBitmap.PixelHeight;

            var transform = new BitmapTransform();
            var scaleFactor = (double)MaxThumbnailSize / Math.Max(orientedWidth, orientedHeight);

            SoftwareBitmap finalBitmap = softwareBitmap;
            if (scaleFactor < 1)
            {
                var newWidth = (uint)(orientedWidth * scaleFactor);
                var newHeight = (uint)(orientedHeight * scaleFactor);

                using var outputStream = new InMemoryRandomAccessStream();
                var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.BmpEncoderId, outputStream).AsTask(cancellationToken);
                encoder.SetSoftwareBitmap(softwareBitmap);
                encoder.BitmapTransform.ScaledWidth = newWidth;
                encoder.BitmapTransform.ScaledHeight = newHeight;
                await encoder.FlushAsync().AsTask(cancellationToken);

                outputStream.Seek(0);
                var decoder = await BitmapDecoder.CreateAsync(outputStream).AsTask(cancellationToken);
                finalBitmap = await decoder.GetSoftwareBitmapAsync().AsTask(cancellationToken);
                softwareBitmap.Dispose();
            }

            using var bmpStream = new InMemoryRandomAccessStream();
            var bmpEncoder = await BitmapEncoder.CreateAsync(BitmapEncoder.BmpEncoderId, bmpStream).AsTask(cancellationToken);
            bmpEncoder.SetSoftwareBitmap(finalBitmap);
            await bmpEncoder.FlushAsync().AsTask(cancellationToken);

            bmpStream.Seek(0);
            var bitmap = new BitmapImage();
            await bitmap.SetSourceAsync(bmpStream).AsTask(cancellationToken);

            finalBitmap.Dispose();
            return bitmap;
        }
        catch
        {
            softwareBitmap?.Dispose();
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

    /// <summary>
    /// 预加载缩略图，保证翻阅流畅
    /// </summary>
    /// <param name="items">所有图片项</param>
    /// <param name="currentIndex">当前索引</param>
    /// <param name="preloadCount">预加载数量（默认8张）</param>
    /// <param name="cancellationToken">取消令牌</param>
    public async Task PreloadThumbnailsAsync(
        List<PhotoItem> items, 
        int currentIndex, 
        int preloadCount = 8,
        CancellationToken cancellationToken = default)
    {
        if (items == null || items.Count == 0)
            return;

        var start = Math.Max(0, currentIndex - 2);
        var end = Math.Min(items.Count - 1, currentIndex + preloadCount);

        var preloadTasks = new List<Task>();
        
        for (int i = start; i <= end; i++)
        {
            if (cancellationToken.IsCancellationRequested)
                break;
                
            try
            {
                var task = GetThumbnailAsync(items[i], cancellationToken);
                preloadTasks.Add(task);
            }
            catch
            {
            }
        }

        try
        {
            await Task.WhenAll(preloadTasks);
        }
        catch
        {
        }
    }
}
