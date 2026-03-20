using FastPick.Models;
using Microsoft.UI.Xaml.Media.Imaging;
using System.Diagnostics;
using System.Collections.Concurrent;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.FileProperties;
using Windows.Storage.Streams;

namespace FastPick.Services;

public class PreviewImageService
{
    private const int MaxPreviewWidth = 1920;
    private const int MaxPreviewHeight = 1080;

    private readonly ConcurrentDictionary<string, bool> _embeddedPreviewFailedCache = new();
    private readonly ConcurrentDictionary<string, Task<BitmapImage?>> _loadingTasks = new();
    private readonly SemaphoreSlim _loadingSemaphore = new(3, 3);
    private readonly SemaphoreSlim _highResSemaphore = new(1, 1);

    public async Task<BitmapImage?> LoadPreviewAsync(PhotoItem photoItem, bool useEmbeddedPreview = true)
    {
        try
        {
            if (photoItem.HasJpg)
            {
                return await LoadJpgPreviewAsync(photoItem.JpgPath);
            }

            if (photoItem.HasRaw && photoItem.RawPath != null)
            {
                var rawPath = photoItem.RawPath;
                
                if (_loadingTasks.TryGetValue(rawPath, out var existingTask))
                {
                    return await existingTask;
                }

                var loadTask = LoadRawPreviewInternalAsync(rawPath, useEmbeddedPreview);
                _loadingTasks[rawPath] = loadTask;

                try
                {
                    return await loadTask;
                }
                finally
                {
                    _loadingTasks.TryRemove(rawPath, out _);
                }
            }

            return null;
        }
        catch (Exception ex)
        {

            return null;
        }
    }

    private async Task<BitmapImage?> LoadRawPreviewInternalAsync(string rawPath, bool useEmbeddedPreview)
    {
        if (!File.Exists(rawPath))
        {

            return null;
        }

        await _loadingSemaphore.WaitAsync();
        
        try
        {
            if (_embeddedPreviewFailedCache.ContainsKey(rawPath))
            {
                Debug.WriteLine($"[缓存命中] 直接解码完整 RAW: {Path.GetFileName(rawPath)}");
                return await LoadRawFullAsync(rawPath);
            }

            if (useEmbeddedPreview)
            {
                var embeddedPreview = await LoadRawEmbeddedPreviewAsync(rawPath);
                if (embeddedPreview != null)
                {
                    _embeddedPreviewFailedCache.TryRemove(rawPath, out _);
                    Debug.WriteLine($"[成功] 提取 RAW 内嵌预览: {Path.GetFileName(rawPath)}");
                    return embeddedPreview;
                }
                
                _embeddedPreviewFailedCache[rawPath] = true;
                Debug.WriteLine($"[降级] 内嵌预览提取失败，尝试解码完整 RAW: {Path.GetFileName(rawPath)}");
            }

            return await LoadRawFullAsync(rawPath);
        }
        finally
        {
            _loadingSemaphore.Release();
        }
    }

    private async Task<BitmapImage?> LoadJpgPreviewAsync(string filePath)
    {
        if (!File.Exists(filePath))
        {
            Debug.WriteLine($"JPG 文件不存在: {filePath}");
            return null;
        }

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
                (double)MaxPreviewWidth / orientedWidth,
                (double)MaxPreviewHeight / orientedHeight);
            
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

            return await SoftwareBitmapToBitmapImageAsync(softwareBitmap);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"加载 JPG 预览失败: {filePath}, 错误: {ex.Message}");
            return null;
        }
    }

    private async Task<BitmapImage?> LoadRawEmbeddedPreviewAsync(string rawPath)
    {
        try
        {
            var storageFile = await StorageFile.GetFileFromPathAsync(rawPath);

            using var stream = await storageFile.OpenAsync(FileAccessMode.Read);
            var decoder = await BitmapDecoder.CreateAsync(stream);

            BitmapRotation orientationRotation = BitmapRotation.None;
            bool isFlippedHorizontal = false;
            try
            {
                var orientation = GetOrientationFromDecoder(decoder);
                (orientationRotation, isFlippedHorizontal) = ConvertOrientationToTransform(orientation);
                Debug.WriteLine($"[EXIF方向] {Path.GetFileName(rawPath)}: Orientation={orientation}, Rotation={orientationRotation}, Flip={isFlippedHorizontal}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[提示] 无法读取EXIF方向: {ex.Message}");
            }

            BitmapFrame? previewFrame = null;
            try
            {
                var previewStream = await decoder.GetPreviewAsync();
                if (previewStream != null)
                {
                    var previewDecoder = await BitmapDecoder.CreateAsync(previewStream);
                    previewFrame = await previewDecoder.GetFrameAsync(0);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[提示] {Path.GetFileName(rawPath)} 不支持内嵌预览: {ex.Message}");
                return null;
            }

            if (previewFrame == null)
            {
                Debug.WriteLine($"[提示] {Path.GetFileName(rawPath)} 内嵌预览为空");
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
                ColorManagementMode.DoNotColorManage);

            if (softwareBitmap == null)
            {
                Debug.WriteLine($"[提示] {Path.GetFileName(rawPath)} 无法获取 SoftwareBitmap");
                return null;
            }

            var scaleFactor = Math.Min(
                (double)MaxPreviewWidth / softwareBitmap.PixelWidth,
                (double)MaxPreviewHeight / softwareBitmap.PixelHeight);
            if (scaleFactor < 1)
            {
                softwareBitmap = await ResizeSoftwareBitmapAsync(softwareBitmap, scaleFactor);
            }

            return await SoftwareBitmapToBitmapImageAsync(softwareBitmap);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"加载 RAW 内嵌预览失败: {rawPath}, 错误: {ex.Message}");
            return null;
        }
    }

    private ushort GetOrientationFromDecoder(BitmapDecoder decoder)
    {
        try
        {
            var properties = decoder.BitmapProperties;
            const string orientationQuery = "System.Photo.Orientation";
            
            var result = properties.GetPropertiesAsync(new[] { orientationQuery }).AsTask().Result;
            if (result.TryGetValue(orientationQuery, out var orientationValue))
            {
                if (orientationValue.Value is ushort orientation)
                {
                    return orientation;
                }
            }
        }
        catch
        {
        }
        return 1;
    }

    private (BitmapRotation rotation, bool flipHorizontal) ConvertOrientationToTransform(ushort orientation)
    {
        return orientation switch
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

    private async Task<BitmapImage?> LoadRawFullAsync(string rawPath)
    {
        try
        {
            var storageFile = await StorageFile.GetFileFromPathAsync(rawPath);

            using var stream = await storageFile.OpenAsync(FileAccessMode.Read);
            var decoder = await BitmapDecoder.CreateAsync(stream);

            var orientedWidth = decoder.OrientedPixelWidth;
            var orientedHeight = decoder.OrientedPixelHeight;
            var originalWidth = decoder.PixelWidth;
            var originalHeight = decoder.PixelHeight;

            var transform = new BitmapTransform();
            var scaleFactor = Math.Min(
                (double)MaxPreviewWidth / orientedWidth,
                (double)MaxPreviewHeight / orientedHeight);
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

            var bitmap = await SoftwareBitmapToBitmapImageAsync(softwareBitmap);
            
            Debug.WriteLine($"[降级成功] 解码完整 RAW: {Path.GetFileName(rawPath)}");
            return bitmap;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"加载完整 RAW 失败: {rawPath}, 错误: {ex.Message}");
            return null;
        }
    }

    public async Task<BitmapImage?> LoadRawFullResolutionAsync(
        string rawPath,
        int targetWidth,
        int targetHeight,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(rawPath))
        {
            Debug.WriteLine($"[高分辨率] RAW 文件不存在: {rawPath}");
            return null;
        }

        await _highResSemaphore.WaitAsync(cancellationToken);
        
        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            var storageFile = await StorageFile.GetFileFromPathAsync(rawPath);
            using var stream = await storageFile.OpenAsync(FileAccessMode.Read);
            var decoder = await BitmapDecoder.CreateAsync(stream);

            cancellationToken.ThrowIfCancellationRequested();

            var orientedWidth = decoder.OrientedPixelWidth;
            var orientedHeight = decoder.OrientedPixelHeight;
            var originalWidth = decoder.PixelWidth;
            var originalHeight = decoder.PixelHeight;

            var transform = new BitmapTransform
            {
                InterpolationMode = BitmapInterpolationMode.Fant
            };

            var scaleFactor = Math.Min(
                (double)targetWidth / orientedWidth,
                (double)targetHeight / orientedHeight);
            
            if (scaleFactor > 1)
            {
                scaleFactor = Math.Min(scaleFactor, 1);
            }

            if (scaleFactor < 1)
            {
                transform.ScaledWidth = (uint)(originalWidth * scaleFactor);
                transform.ScaledHeight = (uint)(originalHeight * scaleFactor);
            }

            cancellationToken.ThrowIfCancellationRequested();

            var softwareBitmap = await decoder.GetSoftwareBitmapAsync(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Premultiplied,
                transform,
                ExifOrientationMode.RespectExifOrientation,
                ColorManagementMode.DoNotColorManage);

            cancellationToken.ThrowIfCancellationRequested();

            var bitmap = await SoftwareBitmapToBitmapImageAsync(softwareBitmap);
            
            Debug.WriteLine($"[高分辨率] 解码 RAW:  目标: {targetWidth}x{targetHeight}, 原始: {originalWidth}x{originalHeight}, 旋转后: {orientedWidth}x{orientedHeight}, 缩放: {scaleFactor:F2}");
            return bitmap;
        }
        catch (OperationCanceledException)
        {
            Debug.WriteLine($"[高分辨率] 解码取消: {Path.GetFileName(rawPath)}");
            throw;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[高分辨率] 解码失败: {rawPath}, 错误: {ex.Message}");
            return null;
        }
        finally
        {
            _highResSemaphore.Release();
        }
    }

    public async Task<BitmapImage?> LoadJpgFullResolutionAsync(
        string jpgPath,
        int targetWidth,
        int targetHeight,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(jpgPath))
        {
            Debug.WriteLine($"[高分辨率] JPG 文件不存在: {jpgPath}");
            return null;
        }

        await _highResSemaphore.WaitAsync(cancellationToken);
        
        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            var storageFile = await StorageFile.GetFileFromPathAsync(jpgPath);
            using var stream = await storageFile.OpenAsync(FileAccessMode.Read);
            var decoder = await BitmapDecoder.CreateAsync(stream);

            cancellationToken.ThrowIfCancellationRequested();

            var orientedWidth = decoder.OrientedPixelWidth;
            var orientedHeight = decoder.OrientedPixelHeight;
            var originalWidth = decoder.PixelWidth;
            var originalHeight = decoder.PixelHeight;

            var transform = new BitmapTransform
            {
                InterpolationMode = BitmapInterpolationMode.Fant
            };

            var scaleFactor = Math.Min(
                (double)targetWidth / orientedWidth,
                (double)targetHeight / orientedHeight);
            
            if (scaleFactor > 1)
            {
                scaleFactor = Math.Min(scaleFactor, 1);
            }

            if (scaleFactor < 1)
            {
                transform.ScaledWidth = (uint)(originalWidth * scaleFactor);
                transform.ScaledHeight = (uint)(originalHeight * scaleFactor);
            }

            cancellationToken.ThrowIfCancellationRequested();

            var softwareBitmap = await decoder.GetSoftwareBitmapAsync(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Premultiplied,
                transform,
                ExifOrientationMode.RespectExifOrientation,
                ColorManagementMode.DoNotColorManage);

            cancellationToken.ThrowIfCancellationRequested();

            var bitmap = await SoftwareBitmapToBitmapImageAsync(softwareBitmap);
            
            Debug.WriteLine($"[高分辨率] 解码 JPG: 目标: {targetWidth}x{targetHeight}, 原始: {originalWidth}x{originalHeight}, 旋转后: {orientedWidth}x{orientedHeight}, 缩放: {scaleFactor:F2}");
            return bitmap;
        }
        catch (OperationCanceledException)
        {
            Debug.WriteLine($"[高分辨率] 解码取消: {Path.GetFileName(jpgPath)}");
            throw;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[高分辨率] 解码失败: {jpgPath}, 错误: {ex.Message}");
            return null;
        }
        finally
        {
            _highResSemaphore.Release();
        }
    }

    private async Task<BitmapImage> SoftwareBitmapToBitmapImageAsync(SoftwareBitmap softwareBitmap)
    {
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

    private async Task<SoftwareBitmap> ResizeSoftwareBitmapAsync(SoftwareBitmap source, double scaleFactor)
    {
        var newWidth = (uint)(source.PixelWidth * scaleFactor);
        var newHeight = (uint)(source.PixelHeight * scaleFactor);

        using var outputStream = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.BmpEncoderId, outputStream);
        encoder.SetSoftwareBitmap(source);
        encoder.BitmapTransform.ScaledWidth = newWidth;
        encoder.BitmapTransform.ScaledHeight = newHeight;
        await encoder.FlushAsync();

        outputStream.Seek(0);
        var decoder = await BitmapDecoder.CreateAsync(outputStream);
        return await decoder.GetSoftwareBitmapAsync();
    }

    public async Task<List<BitmapImage>> LoadMultiPreviewAsync(List<PhotoItem> photoItems, int maxCount = 5)
    {
        var results = new List<BitmapImage>();
        var itemsToLoad = photoItems.Take(maxCount).ToList();

        foreach (var item in itemsToLoad)
        {
            var preview = await LoadPreviewAsync(item, useEmbeddedPreview: true);
            if (preview != null)
            {
                results.Add(preview);
            }

            if (results.Count >= maxCount)
                break;
        }

        return results;
    }

    public void ReleasePreview(BitmapImage? bitmap)
    {
        if (bitmap != null)
        {
            bitmap.UriSource = null;
        }
    }

    public async Task<BitmapImage?> LoadQuickPreviewAsync(PhotoItem photoItem, CancellationToken cancellationToken = default)
    {
        Debug.WriteLine($"[分级预取] LoadQuickPreviewAsync 开始: {photoItem.FileName}, DisplayPath: {photoItem.DisplayPath}");
        try
        {
            var filePath = photoItem.DisplayPath;
            if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
            {
                Debug.WriteLine($"[分级预取] LoadQuickPreviewAsync: 文件不存在，返回 null");
                return null;
            }

            Debug.WriteLine($"[分级预取] LoadQuickPreviewAsync: 开始获取 StorageFile");
            var storageFile = await StorageFile.GetFileFromPathAsync(filePath).AsTask(cancellationToken);
            Debug.WriteLine($"[分级预取] LoadQuickPreviewAsync: 开始获取缩略图 (1024px)");
            var thumbnail = await storageFile.GetThumbnailAsync(
                ThumbnailMode.SingleItem,
                1024,
                ThumbnailOptions.ResizeThumbnail).AsTask(cancellationToken);

            if (thumbnail != null)
            {
                Debug.WriteLine($"[分级预取] LoadQuickPreviewAsync: 缩略图获取成功，开始设置到 BitmapImage");
                var bitmap = new BitmapImage();
                await bitmap.SetSourceAsync(thumbnail).AsTask(cancellationToken);
                thumbnail.Dispose();
                Debug.WriteLine($"[分级预取] LoadQuickPreviewAsync: BitmapImage 设置完成");
                return bitmap;
            }
            else
            {
                Debug.WriteLine($"[分级预取] LoadQuickPreviewAsync: thumbnail 为 null");
            }
        }
        catch (OperationCanceledException)
        {
            Debug.WriteLine($"[分级预取] LoadQuickPreviewAsync: 被取消");
            throw;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[分级预取] 加载快速预览失败: {photoItem.DisplayPath}, 错误: {ex.Message}");
        }

        Debug.WriteLine($"[分级预取] LoadQuickPreviewAsync: 返回 null");
        return null;
    }

    public void ClearFailedCache()
    {
        _embeddedPreviewFailedCache.Clear();
    }
}
