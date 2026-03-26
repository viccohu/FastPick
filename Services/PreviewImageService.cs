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
    private readonly SemaphoreSlim _quickLoadLock = new(3, 3);
    private readonly ImageCacheService _imageCache = new();

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
        catch
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
                LoggerService.Instance.Verbose(LogCategory.Cache, $"直接解码完整 RAW: {Path.GetFileName(rawPath)}");
                return await LoadRawFullAsync(rawPath);
            }

            if (useEmbeddedPreview)
            {
                var embeddedPreview = await LoadRawEmbeddedPreviewAsync(rawPath);
                if (embeddedPreview != null)
                {
                    _embeddedPreviewFailedCache.TryRemove(rawPath, out _);
                    LoggerService.Instance.Info(LogCategory.RawProcessing, $"提取 RAW 内嵌预览成功: {Path.GetFileName(rawPath)}");
                    return embeddedPreview;
                }
                
                _embeddedPreviewFailedCache[rawPath] = true;
                LoggerService.Instance.Warning(LogCategory.RawProcessing, $"内嵌预览提取失败，尝试解码完整 RAW: {Path.GetFileName(rawPath)}");
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
            LoggerService.Instance.Warning(LogCategory.ImageDecode, $"JPG 文件不存在: {filePath}");
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
            transform.InterpolationMode = BitmapInterpolationMode.Fant;
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
            LoggerService.Instance.Error(LogCategory.ImageDecode, $"加载 JPG 预览失败: {filePath}", ex);
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
                var orientation = await GetOrientationFromDecoderAsync(decoder);
                (orientationRotation, isFlippedHorizontal) = ConvertOrientationToTransform(orientation);
                LoggerService.Instance.Verbose(LogCategory.RawProcessing, $"{Path.GetFileName(rawPath)}: Orientation={orientation}, Rotation={orientationRotation}, Flip={isFlippedHorizontal}");
            }
            catch (Exception ex)
            {
                LoggerService.Instance.Verbose(LogCategory.RawProcessing, $"无法读取EXIF方向: {ex.Message}");
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
                LoggerService.Instance.Verbose(LogCategory.RawProcessing, $"{Path.GetFileName(rawPath)} 不支持内嵌预览: {ex.Message}");
                return null;
            }

            if (previewFrame == null)
            {
                LoggerService.Instance.Verbose(LogCategory.RawProcessing, $"{Path.GetFileName(rawPath)} 内嵌预览为空");
                return null;
            }

            var transform = new BitmapTransform
            {
                Rotation = orientationRotation,
                Flip = isFlippedHorizontal ? BitmapFlip.Horizontal : BitmapFlip.None,
                InterpolationMode = BitmapInterpolationMode.Fant
            };

            var softwareBitmap = await previewFrame.GetSoftwareBitmapAsync(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Premultiplied,
                transform,
                ExifOrientationMode.IgnoreExifOrientation,
                ColorManagementMode.DoNotColorManage);

            if (softwareBitmap == null)
            {
                LoggerService.Instance.Verbose(LogCategory.RawProcessing, $"{Path.GetFileName(rawPath)} 无法获取 SoftwareBitmap");
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
            LoggerService.Instance.Error(LogCategory.RawProcessing, $"加载 RAW 内嵌预览失败: {rawPath}", ex);
            return null;
        }
    }

    private async Task<ushort> GetOrientationFromDecoderAsync(BitmapDecoder decoder)
    {
        try
        {
            var properties = decoder.BitmapProperties;
            const string orientationQuery = "System.Photo.Orientation";
            
            var result = await properties.GetPropertiesAsync(new[] { orientationQuery }).AsTask();
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
            transform.InterpolationMode = BitmapInterpolationMode.Fant;
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

            LoggerService.Instance.Info(LogCategory.RawProcessing, $"解码完整 RAW: {Path.GetFileName(rawPath)}");
            return bitmap;
        }
        catch (Exception ex)
        {
            LoggerService.Instance.Error(LogCategory.RawProcessing, $"加载完整 RAW 失败: {rawPath}", ex);
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
            LoggerService.Instance.Warning(LogCategory.ImageDecode, $"RAW 文件不存在: {rawPath}");
            return null;
        }

        string cacheKey = $"raw_{rawPath}_{targetWidth}_{targetHeight}";
        var cachedImage = _imageCache.Get(cacheKey);
        if (cachedImage != null)
        {
            LoggerService.Instance.Verbose(LogCategory.Cache, $"RAW高清预览缓存命中: {Path.GetFileName(rawPath)}, 目标尺寸: {targetWidth}x{targetHeight}");
            _imageCache.LogCacheStatistics();
            return cachedImage;
        }

        LoggerService.Instance.Verbose(LogCategory.Cache, $"RAW高清预览缓存未命中: {Path.GetFileName(rawPath)}, 目标尺寸: {targetWidth}x{targetHeight}");
        _imageCache.LogCacheStatistics();

        await _highResSemaphore.WaitAsync(cancellationToken);

        using (LoggerService.Instance.StartTimer(LogCategory.ImageDecode, $"解码RAW高清: {Path.GetFileName(rawPath)}"))
        {
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

                var transform = new BitmapTransform();

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

                LoggerService.Instance.Verbose(LogCategory.ImageDecode,
                    $"解码RAW: 目标={targetWidth}x{targetHeight}, 原始={originalWidth}x{originalHeight}, 旋转后={orientedWidth}x{orientedHeight}, 缩放={scaleFactor:F2}");

                if (bitmap != null)
                {
                    _imageCache.Set(cacheKey, bitmap);
                }

                return bitmap;
            }
            catch (OperationCanceledException)
            {
                LoggerService.Instance.Verbose(LogCategory.ImageDecode, $"解码取消: {Path.GetFileName(rawPath)}");
                throw;
            }
            catch (Exception ex)
            {
                LoggerService.Instance.Error(LogCategory.ImageDecode, $"解码失败: {rawPath}", ex);
                return null;
            }
            finally
            {
                _highResSemaphore.Release();
            }
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
            LoggerService.Instance.Warning(LogCategory.ImageDecode, $"JPG 文件不存在: {jpgPath}");
            return null;
        }

        string cacheKey = $"jpg_{jpgPath}_{targetWidth}_{targetHeight}";
        var cachedImage = _imageCache.Get(cacheKey);
        if (cachedImage != null)
        {
            LoggerService.Instance.Verbose(LogCategory.Cache, $"JPG高清预览缓存命中: {Path.GetFileName(jpgPath)}, 目标尺寸: {targetWidth}x{targetHeight}");
            _imageCache.LogCacheStatistics();
            return cachedImage;
        }

        LoggerService.Instance.Verbose(LogCategory.Cache, $"JPG高清预览缓存未命中: {Path.GetFileName(jpgPath)}, 目标尺寸: {targetWidth}x{targetHeight}");
        _imageCache.LogCacheStatistics();

        await _highResSemaphore.WaitAsync(cancellationToken);

        using (LoggerService.Instance.StartTimer(LogCategory.ImageDecode, $"解码JPG高清: {Path.GetFileName(jpgPath)}"))
        {
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

                var transform = new BitmapTransform();

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

                LoggerService.Instance.Verbose(LogCategory.ImageDecode,
                    $"解码JPG: 目标={targetWidth}x{targetHeight}, 原始={originalWidth}x{originalHeight}, 旋转后={orientedWidth}x{orientedHeight}, 缩放={scaleFactor:F2}");

                if (bitmap != null)
                {
                    _imageCache.Set(cacheKey, bitmap);
                }

                return bitmap;
            }
            catch (OperationCanceledException)
            {
                LoggerService.Instance.Verbose(LogCategory.ImageDecode, $"解码取消: {Path.GetFileName(jpgPath)}");
                throw;
            }
            catch (Exception ex)
            {
                LoggerService.Instance.Error(LogCategory.ImageDecode, $"解码失败: {jpgPath}", ex);
                return null;
            }
            finally
            {
                _highResSemaphore.Release();
            }
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
        // 不再清空 UriSource，避免二次打开时黑屏闪烁
        // BitmapImage 不实现 IDisposable，无需手动释放
    }

    public async Task<BitmapImage?> LoadQuickPreviewAsync(PhotoItem photoItem, CancellationToken cancellationToken = default)
    {
        return await LoadQuickPreviewAsync(photoItem, 1024, cancellationToken);
    }

    public async Task<BitmapImage?> LoadQuickPreviewAsync(PhotoItem photoItem, int targetSize, CancellationToken cancellationToken = default)
    {
        LoggerService.Instance.Verbose(LogCategory.Hierarchical, $"开始加载快速预览: {photoItem.FileName}, 目标尺寸: {targetSize}");

        string cacheKey = $"quick_{photoItem.DisplayPath}_{targetSize}";
        var cachedImage = _imageCache.Get(cacheKey);
        if (cachedImage != null)
        {
            LoggerService.Instance.Verbose(LogCategory.Cache, $"快速预览缓存命中: {photoItem.FileName}, 目标尺寸: {targetSize}");
            _imageCache.LogCacheStatistics();
            return cachedImage;
        }

        LoggerService.Instance.Verbose(LogCategory.Cache, $"快速预览缓存未命中: {photoItem.FileName}, 目标尺寸: {targetSize}");
        _imageCache.LogCacheStatistics();

        using (LoggerService.Instance.StartTimer(LogCategory.Hierarchical, $"快速预览: {photoItem.FileName}"))
        {
            try
            {
                await _quickLoadLock.WaitAsync(cancellationToken);
                try
                {
                    BitmapImage? bitmap = null;

                    if (photoItem.HasJpg && !string.IsNullOrEmpty(photoItem.JpgPath) && File.Exists(photoItem.JpgPath))
                    {
                        bitmap = await LoadQuickPreviewDirectAsync(photoItem.JpgPath, targetSize, cancellationToken);
                    }

                    if (bitmap == null && photoItem.HasRaw && !string.IsNullOrEmpty(photoItem.RawPath) && File.Exists(photoItem.RawPath))
                    {
                        bitmap = await LoadQuickPreviewFromRawAsync(photoItem.RawPath, targetSize, cancellationToken);
                    }

                    if (bitmap == null && !string.IsNullOrEmpty(photoItem.DisplayPath) && File.Exists(photoItem.DisplayPath))
                    {
                        if (photoItem.HasJpg)
                        {
                            bitmap = await LoadQuickPreviewDirectAsync(photoItem.DisplayPath, targetSize, cancellationToken);
                        }
                        else
                        {
                            bitmap = await LoadQuickPreviewFromRawAsync(photoItem.DisplayPath, targetSize, cancellationToken);
                        }
                    }

                    if (bitmap != null)
                    {
                        _imageCache.Set(cacheKey, bitmap);
                    }

                    return bitmap;
                }
                finally
                {
                    _quickLoadLock.Release();
                }
            }
            catch (OperationCanceledException)
            {
                LoggerService.Instance.Verbose(LogCategory.Hierarchical, "快速预览加载被取消");
                throw;
            }
            catch (Exception ex)
            {
                LoggerService.Instance.Error(LogCategory.Hierarchical, $"加载快速预览失败: {photoItem.FileName}", ex);
                return null;
            }
        }
    }

    private async Task<BitmapImage?> LoadQuickPreviewDirectAsync(string filePath, int targetSize, CancellationToken cancellationToken)
    {
        try
        {
            var storageFile = await StorageFile.GetFileFromPathAsync(filePath).AsTask(cancellationToken);
            using var stream = await storageFile.OpenAsync(FileAccessMode.Read);
            var decoder = await BitmapDecoder.CreateAsync(stream);

            var orientedWidth = decoder.OrientedPixelWidth;
            var orientedHeight = decoder.OrientedPixelHeight;
            var originalWidth = decoder.PixelWidth;
            var originalHeight = decoder.PixelHeight;

            var transform = new BitmapTransform();
            var scaleFactor = Math.Min(
                (double)targetSize / orientedWidth,
                (double)targetSize / orientedHeight);
            
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
            LoggerService.Instance.Error(LogCategory.Hierarchical, $"快速预览加载失败: {Path.GetFileName(filePath)}", ex);
            return null;
        }
    }

    private async Task<BitmapImage?> LoadQuickPreviewFromRawAsync(string rawPath, int targetSize, CancellationToken cancellationToken)
    {
        try
        {
            var embeddedSoftwareBitmap = await LoadRawEmbeddedPreviewAsSoftwareBitmapAsync(rawPath);
            if (embeddedSoftwareBitmap != null)
            {
                LoggerService.Instance.Verbose(LogCategory.Hierarchical, "使用 RAW 内嵌预览");

                var scaleFactor = Math.Min(
                    (double)targetSize / embeddedSoftwareBitmap.PixelWidth,
                    (double)targetSize / embeddedSoftwareBitmap.PixelHeight);

                SoftwareBitmap finalBitmap = embeddedSoftwareBitmap;
                if (scaleFactor < 1)
                {
                    finalBitmap = await ResizeSoftwareBitmapAsync(embeddedSoftwareBitmap, scaleFactor);
                    embeddedSoftwareBitmap.Dispose();
                }

                var result = await SoftwareBitmapToBitmapImageAsync(finalBitmap);
                finalBitmap.Dispose();
                return result;
            }

            LoggerService.Instance.Verbose(LogCategory.Hierarchical, "内嵌预览失败，使用完整解码");
            return await LoadRawPreviewScaledAsync(rawPath, targetSize, cancellationToken);
        }
        catch (Exception ex)
        {
            LoggerService.Instance.Error(LogCategory.Hierarchical, $"RAW快速预览失败: {Path.GetFileName(rawPath)}", ex);
            return null;
        }
    }

    private async Task<SoftwareBitmap?> LoadRawEmbeddedPreviewAsSoftwareBitmapAsync(string rawPath)
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
                var orientation = await GetOrientationFromDecoderAsync(decoder);
                (orientationRotation, isFlippedHorizontal) = ConvertOrientationToTransform(orientation);
            }
            catch (Exception)
            {
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
            catch (Exception)
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
                Flip = isFlippedHorizontal ? BitmapFlip.Horizontal : BitmapFlip.None,
                InterpolationMode = BitmapInterpolationMode.Fant
            };

            var softwareBitmap = await previewFrame.GetSoftwareBitmapAsync(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Premultiplied,
                transform,
                ExifOrientationMode.IgnoreExifOrientation,
                ColorManagementMode.DoNotColorManage);

            return softwareBitmap;
        }
        catch (Exception ex)
        {
            LoggerService.Instance.Error(LogCategory.RawProcessing, $"加载 RAW 内嵌预览失败: {rawPath}", ex);
            return null;
        }
    }

    private async Task<BitmapImage?> LoadRawPreviewScaledAsync(string rawPath, int targetSize, CancellationToken cancellationToken)
    {
        try
        {
            var storageFile = await StorageFile.GetFileFromPathAsync(rawPath).AsTask(cancellationToken);
            using var stream = await storageFile.OpenAsync(FileAccessMode.Read);
            var decoder = await BitmapDecoder.CreateAsync(stream);

            var orientedWidth = decoder.OrientedPixelWidth;
            var orientedHeight = decoder.OrientedPixelHeight;
            var originalWidth = decoder.PixelWidth;
            var originalHeight = decoder.PixelHeight;

            var transform = new BitmapTransform();
            var scaleFactor = Math.Min(
                (double)targetSize / orientedWidth,
                (double)targetSize / orientedHeight);
            
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
            LoggerService.Instance.Error(LogCategory.Hierarchical, $"RAW缩放预览失败: {Path.GetFileName(rawPath)}", ex);
            return null;
        }
    }

    public void ClearFailedCache()
    {
        _embeddedPreviewFailedCache.Clear();
        _imageCache.Clear();
    }
}
