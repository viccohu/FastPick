using FastPick.Models;
using Microsoft.UI.Xaml.Media.Imaging;
using System.Collections.Concurrent;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

namespace FastPick.Services;

/// <summary>
/// WIC 图片解码服务
/// 提供异步加载缩略图、预览图，以及双路径图片扫描功能
/// </summary>
public static class WicImageService
{
    // RAW 格式扩展名白名单
    private static readonly HashSet<string> RawExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cr2", ".cr3", ".nef", ".arw", ".dng", ".raf", ".orf", ".rw2", ".pef", ".sr2"
    };

    // JPG 格式扩展名
    private static readonly HashSet<string> JpgExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg"
    };

    // 缩略图缓存
    private static readonly ConcurrentDictionary<string, BitmapImage> ThumbnailCache = new();
    
    // 预览图缓存
    private static readonly ConcurrentDictionary<string, BitmapImage> PreviewCache = new();

    /// <summary>
    /// 异步加载缩略图
    /// </summary>
    /// <param name="filePath">文件路径</param>
    /// <param name="size">缩略图尺寸（默认 100）</param>
    /// <returns>BitmapImage 缩略图</returns>
    public static async Task<BitmapImage?> LoadThumbnailAsync(string filePath, int size = 100)
    {
        if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
            return null;

        // 检查缓存
        var cacheKey = $"{filePath}_{size}";
        if (ThumbnailCache.TryGetValue(cacheKey, out var cachedImage))
        {
            return cachedImage;
        }

        try
        {
            var file = await StorageFile.GetFileFromPathAsync(filePath);
            
            using var stream = await file.OpenReadAsync();
            var decoder = await BitmapDecoder.CreateAsync(stream);
            
            // 计算缩放比例
            var originalWidth = decoder.PixelWidth;
            var originalHeight = decoder.PixelHeight;
            var scale = Math.Min((double)size / originalWidth, (double)size / originalHeight);
            var newWidth = (uint)(originalWidth * scale);
            var newHeight = (uint)(originalHeight * scale);

            // 使用软件位图进行缩放
            var transform = new BitmapTransform
            {
                ScaledWidth = newWidth,
                ScaledHeight = newHeight,
                InterpolationMode = BitmapInterpolationMode.Linear
            };

            var pixelData = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, transform, ExifOrientationMode.RespectExifOrientation, ColorManagementMode.ColorManageToSRgb);
            
            var bitmap = new BitmapImage();
            using (var memoryStream = new InMemoryRandomAccessStream())
            {
                var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, memoryStream);
                encoder.SetSoftwareBitmap(pixelData);
                await encoder.FlushAsync();
                
                bitmap.SetSource(memoryStream);
            }

            // 存入缓存
            ThumbnailCache[cacheKey] = bitmap;
            return bitmap;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"加载缩略图失败: {filePath}, 错误: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 异步加载预览图（较大尺寸）
    /// </summary>
    /// <param name="filePath">文件路径</param>
    /// <param name="maxSize">最大尺寸（默认 1920）</param>
    /// <returns>BitmapImage 预览图</returns>
    public static async Task<BitmapImage?> LoadPreviewAsync(string filePath, int maxSize = 1920)
    {
        if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
            return null;

        // 检查缓存
        var cacheKey = $"{filePath}_preview";
        if (PreviewCache.TryGetValue(cacheKey, out var cachedImage))
        {
            return cachedImage;
        }

        try
        {
            var file = await StorageFile.GetFileFromPathAsync(filePath);
            
            using var stream = await file.OpenReadAsync();
            var decoder = await BitmapDecoder.CreateAsync(stream);
            
            // 计算缩放比例
            var originalWidth = decoder.PixelWidth;
            var originalHeight = decoder.PixelHeight;
            var scale = Math.Min((double)maxSize / originalWidth, (double)maxSize / originalHeight);
            
            // 如果图片已经小于最大尺寸，不缩放
            if (scale >= 1.0)
            {
                scale = 1.0;
            }
            
            var newWidth = (uint)(originalWidth * scale);
            var newHeight = (uint)(originalHeight * scale);

            var transform = new BitmapTransform
            {
                ScaledWidth = newWidth,
                ScaledHeight = newHeight,
                InterpolationMode = BitmapInterpolationMode.Cubic
            };

            var pixelData = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, transform, ExifOrientationMode.RespectExifOrientation, ColorManagementMode.ColorManageToSRgb);
            
            var bitmap = new BitmapImage();
            using (var memoryStream = new InMemoryRandomAccessStream())
            {
                var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, memoryStream);
                encoder.SetSoftwareBitmap(pixelData);
                await encoder.FlushAsync();
                
                bitmap.SetSource(memoryStream);
            }

            // 存入缓存
            PreviewCache[cacheKey] = bitmap;
            return bitmap;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"加载预览图失败: {filePath}, 错误: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 扫描文件夹中的图片文件
    /// </summary>
    /// <param name="path">文件夹路径</param>
    /// <returns>文件路径列表</returns>
    public static async Task<List<string>> ScanFolderAsync(string path)
    {
        if (!Directory.Exists(path))
            return new List<string>();

        return await Task.Run(() =>
        {
            var files = new List<string>();
            var allFiles = Directory.GetFiles(path, "*.*", SearchOption.TopDirectoryOnly);
            
            foreach (var file in allFiles)
            {
                var ext = Path.GetExtension(file);
                if (JpgExtensions.Contains(ext) || RawExtensions.Contains(ext))
                {
                    files.Add(file);
                }
            }
            
            return files.OrderBy(f => f).ToList();
        });
    }

    /// <summary>
    /// 匹配 JPG 和 RAW 文件对
    /// </summary>
    /// <param name="files">文件路径列表</param>
    /// <returns>PhotoItem 列表</returns>
    public static List<PhotoItem> MatchJpgRawPairs(List<string> files)
    {
        var photoItems = new List<PhotoItem>();
        var fileGroups = files.GroupBy(f => Path.GetFileNameWithoutExtension(f).ToLowerInvariant());

        foreach (var group in fileGroups)
        {
            var jpgFile = group.FirstOrDefault(f => JpgExtensions.Contains(Path.GetExtension(f)));
            var rawFile = group.FirstOrDefault(f => RawExtensions.Contains(Path.GetExtension(f)));

            var photoItem = new PhotoItem
            {
                FileName = group.Key,
                JpgPath = jpgFile ?? string.Empty,
                RawPath = rawFile
            };

            photoItems.Add(photoItem);
        }

        return photoItems;
    }

    /// <summary>
    /// 从两个路径加载并合并图片
    /// </summary>
    /// <param name="path1">路径1</param>
    /// <param name="path2">路径2（可选）</param>
    /// <returns>PhotoItem 列表</returns>
    public static async Task<List<PhotoItem>> LoadPhotosFromPathsAsync(string path1, string? path2 = null)
    {
        var allFiles = new List<string>();

        var files1 = await ScanFolderAsync(path1);
        allFiles.AddRange(files1);

        if (!string.IsNullOrEmpty(path2))
        {
            var files2 = await ScanFolderAsync(path2);
            allFiles.AddRange(files2);
        }

        return MatchJpgRawPairs(allFiles);
    }

    /// <summary>
    /// 清除指定文件的缓存
    /// </summary>
    public static void ClearCache(string filePath)
    {
        // 清除缩略图缓存
        var thumbnailKeys = ThumbnailCache.Keys.Where(k => k.StartsWith(filePath)).ToList();
        foreach (var key in thumbnailKeys)
        {
            ThumbnailCache.TryRemove(key, out _);
        }

        // 清除预览图缓存
        if (PreviewCache.ContainsKey($"{filePath}_preview"))
        {
            PreviewCache.TryRemove($"{filePath}_preview", out _);
        }
    }

    /// <summary>
    /// 清除所有缓存
    /// </summary>
    public static void ClearAllCache()
    {
        ThumbnailCache.Clear();
        PreviewCache.Clear();
    }

    /// <summary>
    /// 获取文件类型
    /// </summary>
    public static FileTypeEnum GetFileType(string filePath)
    {
        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        
        if (JpgExtensions.Contains(ext))
            return FileTypeEnum.JpgOnly;
        if (RawExtensions.Contains(ext))
            return FileTypeEnum.RawOnly;
        
        return FileTypeEnum.JpgOnly;
    }

    /// <summary>
    /// 检查是否为支持的图片格式
    /// </summary>
    public static bool IsSupportedImage(string filePath)
    {
        var ext = Path.GetExtension(filePath);
        return JpgExtensions.Contains(ext) || RawExtensions.Contains(ext);
    }
}
