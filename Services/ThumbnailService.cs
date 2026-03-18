using FastPick.Models;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.FileProperties;
using Windows.Storage;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace FastPick.Services;

public class ThumbnailService
{
    private readonly LinkedList<string> _lruList = new();
    private readonly Dictionary<string, BitmapImage> _thumbnailCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _cacheLock = new(1, 1);
    private const int MaxCacheSize = 200;
    private const int ThumbnailWidth = 100;
    private const int ThumbnailHeight = 80;

    public async Task<BitmapImage?> GetThumbnailAsync(PhotoItem photoItem)
    {
        var filePath = photoItem.DisplayPath;
        
        if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
            return null;

        await _cacheLock.WaitAsync();
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

        var systemThumbnail = await GetSystemThumbnailAsync(filePath);
        if (systemThumbnail != null)
        {
            await AddToCacheAsync(filePath, systemThumbnail);
            return systemThumbnail;
        }

        var wicThumbnail = await GenerateWicThumbnailAsync(filePath);
        if (wicThumbnail != null)
        {
            await AddToCacheAsync(filePath, wicThumbnail);
            return wicThumbnail;
        }

        return null;
    }

    private async Task<BitmapImage?> GetSystemThumbnailAsync(string filePath)
    {
        try
        {
            var storageFile = await StorageFile.GetFileFromPathAsync(filePath);
            var thumbnail = await storageFile.GetThumbnailAsync(
                ThumbnailMode.SingleItem,
                (uint)ThumbnailWidth,
                ThumbnailOptions.UseCurrentScale);

            if (thumbnail != null)
            {
                var bitmap = new BitmapImage();
                await bitmap.SetSourceAsync(thumbnail);
                thumbnail.Dispose();
                return bitmap;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"获取系统缩略图失败: {filePath}, {ex.Message}");
        }

        return null;
    }

    private async Task<BitmapImage?> GenerateWicThumbnailAsync(string filePath)
    {
        try
        {
            var storageFile = await StorageFile.GetFileFromPathAsync(filePath);
            using var stream = await storageFile.OpenAsync(FileAccessMode.Read);
            var decoder = await BitmapDecoder.CreateAsync(stream);

            var orientedWidth = decoder.OrientedPixelWidth;
            var orientedHeight = decoder.OrientedPixelHeight;
            var originalWidth = decoder.PixelWidth;
            var originalHeight = decoder.PixelHeight;

            var transform = new BitmapTransform();
            var scaleFactor = Math.Min(
                (double)ThumbnailWidth / orientedWidth,
                (double)ThumbnailHeight / orientedHeight);
            
            if (scaleFactor < 1)
            {
                transform.ScaledWidth = (uint)(originalWidth * scaleFactor);
                transform.ScaledHeight = (uint)(originalHeight * scaleFactor);
            }

            var softwareBitmap = await decoder.GetSoftwareBitmapAsync(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Premultiplied,
                transform,
                ExifOrientationMode.RespectExifOrientation,
                ColorManagementMode.DoNotColorManage);

            using var outputStream = new InMemoryRandomAccessStream();
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.BmpEncoderId, outputStream);
            encoder.SetSoftwareBitmap(softwareBitmap);
            await encoder.FlushAsync();

            outputStream.Seek(0);
            var bitmap = new BitmapImage();
            await bitmap.SetSourceAsync(outputStream);

            softwareBitmap.Dispose();
            return bitmap;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"WIC 生成缩略图失败: {filePath}, {ex.Message}");
            return null;
        }
    }

    private async Task AddToCacheAsync(string filePath, BitmapImage thumbnail)
    {
        await _cacheLock.WaitAsync();
        try
        {
            while (_thumbnailCache.Count >= MaxCacheSize && _lruList.Count > 0)
            {
                var oldestKey = _lruList.Last!.Value;
                _lruList.RemoveLast();
                if (_thumbnailCache.TryGetValue(oldestKey, out var oldImage))
                {
                    oldImage.UriSource = null;
                }
                _thumbnailCache.Remove(oldestKey);
            }

            _thumbnailCache[filePath] = thumbnail;
            _lruList.AddFirst(filePath);
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
            foreach (var image in _thumbnailCache.Values)
            {
                image.UriSource = null;
            }
            _thumbnailCache.Clear();
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

    public async Task PreloadThumbnailsAsync(List<PhotoItem> items, int startIndex, int count)
    {
        var endIndex = Math.Min(startIndex + count, items.Count);
        
        for (int i = startIndex; i < endIndex; i++)
        {
            try
            {
                await GetThumbnailAsync(items[i]);
                await Task.Delay(10);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"预加载缩略图失败: {ex.Message}");
            }
        }
    }
}
