using FastPick.Models;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;
using Microsoft.UI.Dispatching;

namespace FastPick.Services;

/// <summary>
/// 三级渐进加载管理器
/// </summary>
public class ProgressiveLoadManager
{
    private readonly PreviewCacheManager _cacheManager;
    private readonly ThumbnailService _thumbnailService;
    private DispatcherQueue? _dispatcherQueue;
    
    // L2 快速预览目标尺寸
    private const int QuickPreviewMaxDimension = 1920;
    
    // RAW 格式扩展名
    private static readonly string[] RawExtensions = { ".arw", ".cr2", ".nef", ".dng", ".orf", ".rw2", ".raf", ".srw" };
    
    public ProgressiveLoadManager(
        PreviewCacheManager cacheManager, 
        ThumbnailService thumbnailService,
        DispatcherQueue? dispatcherQueue = null)
    {
        _cacheManager = cacheManager;
        _thumbnailService = thumbnailService;
        _dispatcherQueue = dispatcherQueue;
    }
    
    /// <summary>
    /// 初始化 DispatcherQueue
    /// </summary>
    public void InitializeDispatcherQueue(DispatcherQueue dispatcherQueue)
    {
        _dispatcherQueue = dispatcherQueue;
    }
    
    /// <summary>
    /// 加载指定级别的图像
    /// </summary>
    public async Task<BitmapImage?> LoadLevelAsync(
        PhotoItem item,
        PreviewQualityLevel level,
        CancellationToken cancellationToken = default)
    {
        var filePath = item.DisplayPath;
        if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
            return null;
        
        // 先检查缓存
        var cached = await _cacheManager.GetFromCacheAsync(filePath, level, cancellationToken);
        if (cached != null)
            return cached;
        
        // 检查 PhotoItem 内部缓存
        if (level == PreviewQualityLevel.QuickPreview && item.PreviewCache.QuickPreview != null)
            return item.PreviewCache.QuickPreview;
        if (level == PreviewQualityLevel.FullResolution && item.PreviewCache.FullResolution != null)
            return item.PreviewCache.FullResolution;
        
        // 尝试获取加载锁
        if (!item.PreviewCache.TryStartLoading(level))
            return null;
        
        try
        {
            BitmapImage? result = null;
            
            switch (level)
            {
                case PreviewQualityLevel.Thumbnail:
                    result = await LoadThumbnailAsync(item, cancellationToken);
                    break;
                case PreviewQualityLevel.QuickPreview:
                    result = await LoadQuickPreviewAsync(filePath, cancellationToken);
                    break;
                case PreviewQualityLevel.FullResolution:
                    result = await LoadFullResolutionAsync(filePath, cancellationToken);
                    break;
            }
            
            if (result != null)
            {
                // 存储到 PhotoItem 内部缓存
                if (level == PreviewQualityLevel.QuickPreview)
                    item.PreviewCache.QuickPreview = result;
                else if (level == PreviewQualityLevel.FullResolution)
                    item.PreviewCache.FullResolution = result;
                
                // 存储到全局缓存
                await _cacheManager.AddToCacheAsync(filePath, result, level, item, cancellationToken);
            }
            
            item.PreviewCache.MarkLoaded(level, result != null);
            return result;
        }
        catch (OperationCanceledException)
        {
            item.PreviewCache.MarkLoaded(level, false);
            throw;
        }
        catch (Exception ex)
        {
            DebugService.WriteLine($"[ProgressiveLoad] 加载失败: {filePath}, Level: {level}, Error: {ex.Message}");
            item.PreviewCache.MarkLoaded(level, false);
            return null;
        }
    }
    
    /// <summary>
    /// L1: 加载缩略图（复用 ThumbnailService）
    /// </summary>
    private async Task<BitmapImage?> LoadThumbnailAsync(PhotoItem item, CancellationToken cancellationToken)
    {
        var result = await _thumbnailService.GetThumbnailAsync(item, cancellationToken);
        DebugService.WriteLine($"[ProgressiveLoad-L1] 获取结果类型: {result?.GetType().Name ?? "null"}, 是否为BitmapImage: {result is BitmapImage}");
        return result as BitmapImage;
    }
    
    /// <summary>
    /// L2: 加载快速预览（WIC DCT 下采样或 RAW 内置预览）
    /// </summary>
    private async Task<BitmapImage?> LoadQuickPreviewAsync(string filePath, CancellationToken cancellationToken)
    {
        var extension = Path.GetExtension(filePath).ToLowerInvariant();
        var isRaw = RawExtensions.Contains(extension);
        
        if (isRaw)
        {
            // 尝试 RAW 内置预览
            var rawPreview = await LoadRawEmbeddedPreviewAsync(filePath, cancellationToken);
            if (rawPreview != null)
                return rawPreview;
        }
        
        // WIC DCT 下采样
        return await LoadWithDctDownsampleAsync(filePath, QuickPreviewMaxDimension, cancellationToken);
    }
    
    /// <summary>
    /// L3: 加载全分辨率图像
    /// </summary>
    private async Task<BitmapImage?> LoadFullResolutionAsync(string filePath, CancellationToken cancellationToken)
    {
        var storageFile = await StorageFile.GetFileFromPathAsync(filePath).AsTask(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        
        using var stream = await storageFile.OpenAsync(FileAccessMode.Read).AsTask(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        
        var decoder = await BitmapDecoder.CreateAsync(stream).AsTask(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        
        var softwareBitmap = await decoder.GetSoftwareBitmapAsync(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Premultiplied).AsTask(cancellationToken);
        
        cancellationToken.ThrowIfCancellationRequested();
        
        return await SoftwareBitmapToBitmapImageAsync(softwareBitmap, cancellationToken);
    }
    
    /// <summary>
    /// 加载 RAW 内置预览图片
    /// </summary>
    private async Task<BitmapImage?> LoadRawEmbeddedPreviewAsync(string rawPath, CancellationToken cancellationToken)
    {
        try
        {
            var storageFile = await StorageFile.GetFileFromPathAsync(rawPath).AsTask(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            
            using var stream = await storageFile.OpenAsync(FileAccessMode.Read).AsTask(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            
            var decoder = await BitmapDecoder.CreateAsync(stream).AsTask(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            
            // 尝试获取预览
            IRandomAccessStream? previewStream = null;
            try
            {
                previewStream = await decoder.GetPreviewAsync().AsTask(cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                
                var previewDecoder = await BitmapDecoder.CreateAsync(previewStream).AsTask(cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                
                var softwareBitmap = await previewDecoder.GetSoftwareBitmapAsync(
                    BitmapPixelFormat.Bgra8,
                    BitmapAlphaMode.Premultiplied).AsTask(cancellationToken);
                
                return await SoftwareBitmapToBitmapImageAsync(softwareBitmap, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                DebugService.WriteLine($"RAW 内置预览提取失败: {rawPath}, {ex.Message}");
                return null;
            }
            finally
            {
                previewStream?.Dispose();
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            DebugService.WriteLine($"RAW 文件访问失败: {rawPath}, {ex.Message}");
            return null;
        }
    }
    
    /// <summary>
    /// 使用 WIC DCT 下采样加载图像
    /// </summary>
    private async Task<BitmapImage?> LoadWithDctDownsampleAsync(
        string filePath, 
        int maxDimension, 
        CancellationToken cancellationToken)
    {
        var storageFile = await StorageFile.GetFileFromPathAsync(filePath).AsTask(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        
        using var stream = await storageFile.OpenAsync(FileAccessMode.Read).AsTask(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        
        var decoder = await BitmapDecoder.CreateAsync(stream).AsTask(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        
        var orientedWidth = decoder.OrientedPixelWidth;
        var orientedHeight = decoder.OrientedPixelHeight;
        var originalWidth = decoder.PixelWidth;
        var originalHeight = decoder.PixelHeight;
        
        var transform = new BitmapTransform();
        
        // 计算缩放因子
        var scaleFactor = (double)maxDimension / Math.Max(orientedWidth, orientedHeight);
        if (scaleFactor < 1)
        {
            transform.ScaledWidth = (uint)(originalWidth * scaleFactor);
            transform.ScaledHeight = (uint)(originalHeight * scaleFactor);
            transform.InterpolationMode = BitmapInterpolationMode.Fant;
        }
        
        var softwareBitmap = await decoder.GetSoftwareBitmapAsync(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Premultiplied,
            transform,
            ExifOrientationMode.RespectExifOrientation,
            ColorManagementMode.DoNotColorManage).AsTask(cancellationToken);
        
        cancellationToken.ThrowIfCancellationRequested();
        
        return await SoftwareBitmapToBitmapImageAsync(softwareBitmap, cancellationToken);
    }
    
    /// <summary>
    /// 将 SoftwareBitmap 转换为 BitmapImage
    /// </summary>
    private async Task<BitmapImage?> SoftwareBitmapToBitmapImageAsync(
        SoftwareBitmap softwareBitmap, 
        CancellationToken cancellationToken)
    {
        if (_dispatcherQueue != null && !_dispatcherQueue.HasThreadAccess)
        {
            var tcs = new TaskCompletionSource<BitmapImage?>();
            
            _dispatcherQueue.TryEnqueue(() =>
            {
                try
                {
                    var bitmap = new BitmapImage();
                    using var stream = new InMemoryRandomAccessStream();
                    var encoderTask = BitmapEncoder.CreateAsync(BitmapEncoder.BmpEncoderId, stream).AsTask();
                    encoderTask.Wait();
                    var encoder = encoderTask.Result;
                    encoder.SetSoftwareBitmap(softwareBitmap);
                    encoder.FlushAsync().AsTask().Wait();
                    stream.Seek(0);
                    bitmap.SetSource(stream);
                    tcs.SetResult(bitmap);
                }
                catch (Exception ex)
                {
                    DebugService.WriteLine($"SoftwareBitmapToBitmapImage 失败: {ex.Message}");
                    tcs.SetResult(null);
                }
            });
            
            softwareBitmap.Dispose();
            return await tcs.Task;
        }
        else
        {
            using var outputStream = new InMemoryRandomAccessStream();
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.BmpEncoderId, outputStream).AsTask(cancellationToken);
            encoder.SetSoftwareBitmap(softwareBitmap);
            await encoder.FlushAsync().AsTask(cancellationToken);
            
            outputStream.Seek(0);
            var bitmap = new BitmapImage();
            await bitmap.SetSourceAsync(outputStream).AsTask(cancellationToken);
            
            softwareBitmap.Dispose();
            return bitmap;
        }
    }
}
