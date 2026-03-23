using FastPick.Models;
using FastPick.Services;
using System.Collections.Concurrent;
using System.Diagnostics;

namespace FastPick.Services;

/// <summary>
/// 图片扫描服务 - 负责扫描文件夹、匹配 JPG/RAW、生成虚拟图片项
/// </summary>
public class ImageScanService
{
    // 支持的图片扩展名
    private static readonly string[] JpgExtensions = { ".jpg", ".jpeg", ".jpe", ".jfif" };
    private static readonly string[] RawExtensions = { 
        ".raw", ".cr2", ".cr3", ".nef", ".arw", ".dng", ".orf", ".rw2", ".pef", ".srw"
    };

    /// <summary>
    /// 扫描两个路径，生成虚拟图片项列表
    /// </summary>
    /// <param name="path1">路径1</param>
    /// <param name="path2">路径2（可选）</param>
    /// <param name="progress">进度报告</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>虚拟图片项列表</returns>
    public async Task<List<PhotoItem>> ScanPathsAsync(
        string path1, 
        string? path2 = null, 
        IProgress<(int current, int total, string message)>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var photoItems = new List<PhotoItem>();
        
        // 收集所有图片文件
        var allFiles = new ConcurrentDictionary<string, (string? jpgPath, string? rawPath)>(StringComparer.OrdinalIgnoreCase);
        
        // 扫描路径1
        progress?.Report((0, 100, "正在扫描路径1..."));
        await ScanSinglePathAsync(path1, allFiles, cancellationToken);
        
        // 扫描路径2（如果提供）
        if (!string.IsNullOrEmpty(path2) && Directory.Exists(path2))
        {
            progress?.Report((30, 100, "正在扫描路径2..."));
            await ScanSinglePathAsync(path2, allFiles, cancellationToken);
        }
        
        // 生成 PhotoItem 列表
        progress?.Report((60, 100, $"找到 {allFiles.Count} 个文件，正在生成列表..."));
        
        int index = 0;
        int total = allFiles.Count;
        
        foreach (var kvp in allFiles.OrderBy(x => x.Key))
        {
            cancellationToken.ThrowIfCancellationRequested();
            
            var fileName = kvp.Key;
            var (jpgPath, rawPath) = kvp.Value;
            
            // 跳过没有任何有效路径的项
            if (string.IsNullOrEmpty(jpgPath) && string.IsNullOrEmpty(rawPath))
                continue;
            
            var photoItem = new PhotoItem
            {
                FileName = fileName,
                JpgPath = jpgPath ?? string.Empty,
                RawPath = rawPath
            };
            
            // 获取文件信息（包含文件存在性验证）
            await PopulateFileInfoAsync(photoItem);
            
            // 跳过文件不存在的项
            if (string.IsNullOrEmpty(photoItem.JpgPath) && string.IsNullOrEmpty(photoItem.RawPath))
            {
                Debug.WriteLine($"[跳过] 文件不存在: {fileName}");
                continue;
            }
            
            photoItems.Add(photoItem);
            
            index++;
            if (index % 100 == 0)
            {
                progress?.Report((60 + (index * 40 / total), 100, $"正在处理 {index}/{total}..."));
            }
        }
        
        progress?.Report((100, 100, $"扫描完成，共 {photoItems.Count} 张照片"));
        return photoItems;
    }

    /// <summary>
    /// 扫描单个路径
    /// </summary>
    private async Task ScanSinglePathAsync(
        string path, 
        ConcurrentDictionary<string, (string? jpgPath, string? rawPath)> files,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(path))
            return;

        await Task.Run(() =>
        {
            try
            {
                // 获取所有文件
                var allFiles = Directory.EnumerateFiles(path, "*.*", SearchOption.TopDirectoryOnly);
                
                foreach (var file in allFiles)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    
                    var extension = Path.GetExtension(file).ToLowerInvariant();
                    var fileNameWithoutExt = Path.GetFileNameWithoutExtension(file);
                    
                    // 检查是否是 JPG
                    if (JpgExtensions.Contains(extension))
                    {
                        files.AddOrUpdate(fileNameWithoutExt,
                            (file, null),
                            (key, oldValue) => (file, oldValue.rawPath));
                    }
                    // 检查是否是 RAW
                    else if (RawExtensions.Contains(extension))
                    {
                        files.AddOrUpdate(fileNameWithoutExt,
                            (null, file),
                            (key, oldValue) => (oldValue.jpgPath, file));
                    }
                }
            }
            catch (Exception ex)
            {
                DebugService.WriteLine($"扫描路径失败: {path}, 错误: {ex.Message}");
            }
        }, cancellationToken);
    }

    /// <summary>
    /// 填充文件信息
    /// </summary>
    private async Task PopulateFileInfoAsync(PhotoItem photoItem)
    {
        try
        {
            // 验证 JPG 文件是否存在
            if (!string.IsNullOrEmpty(photoItem.JpgPath) && !File.Exists(photoItem.JpgPath))
            {
                Debug.WriteLine($"[警告] JPG 文件不存在: {photoItem.JpgPath}");
                photoItem.JpgPath = string.Empty;
            }
            
            // 验证 RAW 文件是否存在
            if (!string.IsNullOrEmpty(photoItem.RawPath) && !File.Exists(photoItem.RawPath))
            {
                Debug.WriteLine($"[警告] RAW 文件不存在: {photoItem.RawPath}");
                photoItem.RawPath = null;
            }
            
            // 如果两个文件都不存在，跳过此项
            if (string.IsNullOrEmpty(photoItem.JpgPath) && string.IsNullOrEmpty(photoItem.RawPath))
            {
                return;
            }
            
            // 优先从 JPG 获取文件信息
            var filePath = photoItem.HasJpg ? photoItem.JpgPath : photoItem.RawPath;
            
            if (!string.IsNullOrEmpty(filePath) && File.Exists(filePath))
            {
                // 读取元数据
                var metadata = await MetadataService.ReadMetadataAsync(filePath);
                if (metadata != null)
                {
                    photoItem.Width = metadata.Width;
                    photoItem.Height = metadata.Height;
                    photoItem.Dpi = metadata.Dpi;
                    photoItem.FileSize = metadata.FileSize;
                    photoItem.DateTimeTaken = metadata.DateTimeTaken;
                    photoItem.CameraModel = metadata.CameraModel;
                    photoItem.LensModel = metadata.LensModel;
                    photoItem.ISO = metadata.ISO;
                    photoItem.FNumber = metadata.FNumber;
                    photoItem.ExposureTime = metadata.ExposureTime;
                    photoItem.Flash = metadata.Flash;
                    photoItem.ExposureBias = metadata.ExposureBias;
                    photoItem.ModifiedDate = metadata.ModifiedDate;
                    photoItem.Dimensions = metadata.Width > 0 ? $"{metadata.Width} x {metadata.Height}" : null;
                }
                else
                {
                    // 如果元数据读取失败，使用基本信息
                    var fileInfo = new FileInfo(filePath);
                    photoItem.FileSize = fileInfo.Length;
                    photoItem.ModifiedDate = fileInfo.LastWriteTime;
                }
            }
            
            // 如果同时有 JPG 和 RAW，计算总大小
            if (photoItem.HasJpg && photoItem.HasRaw && photoItem.RawPath != null)
            {
                var rawInfo = new FileInfo(photoItem.RawPath);
                photoItem.FileSize += rawInfo.Length;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"获取文件信息失败: {ex.Message}");
            // 异常时清除路径，防止加载不存在的文件
            photoItem.JpgPath = string.Empty;
            photoItem.RawPath = null;
        }
    }

    /// <summary>
    /// 检查路径是否有效
    /// </summary>
    public bool IsPathValid(string? path)
    {
        return !string.IsNullOrEmpty(path) && Directory.Exists(path);
    }

    /// <summary>
    /// 获取路径统计信息
    /// </summary>
    public async Task<(int jpgCount, int rawCount, int bothCount)> GetPathStatisticsAsync(string path)
    {
        return await Task.Run(() =>
        {
            int jpgCount = 0, rawCount = 0, bothCount = 0;
            
            if (!Directory.Exists(path))
                return (jpgCount, rawCount, bothCount);

            var files = new Dictionary<string, (bool hasJpg, bool hasRaw)>(StringComparer.OrdinalIgnoreCase);

            foreach (var file in Directory.EnumerateFiles(path, "*.*", SearchOption.TopDirectoryOnly))
            {
                var extension = Path.GetExtension(file).ToLowerInvariant();
                var fileNameWithoutExt = Path.GetFileNameWithoutExtension(file);

                if (!files.ContainsKey(fileNameWithoutExt))
                    files[fileNameWithoutExt] = (false, false);

                var current = files[fileNameWithoutExt];

                if (JpgExtensions.Contains(extension))
                    files[fileNameWithoutExt] = (true, current.hasRaw);
                else if (RawExtensions.Contains(extension))
                    files[fileNameWithoutExt] = (current.hasJpg, true);
            }

            foreach (var item in files.Values)
            {
                if (item.hasJpg && item.hasRaw)
                    bothCount++;
                else if (item.hasJpg)
                    jpgCount++;
                else if (item.hasRaw)
                    rawCount++;
            }

            return (jpgCount, rawCount, bothCount);
        });
    }

    /// <summary>
    /// 快速扫描 - 仅获取文件名，不读取元数据
    /// </summary>
    public async Task<List<PhotoItem>> ScanFilesQuickAsync(
        string path1, string? path2, CancellationToken cancellationToken)
    {
        var photoItems = new List<PhotoItem>();
        var allFiles = new ConcurrentDictionary<string, (string? jpgPath, string? rawPath)>(StringComparer.OrdinalIgnoreCase);

        // 扫描路径1
        await ScanSinglePathAsync(path1, allFiles, cancellationToken);

        // 扫描路径2
        if (!string.IsNullOrEmpty(path2) && Directory.Exists(path2))
        {
            await ScanSinglePathAsync(path2, allFiles, cancellationToken);
        }

        // 快速生成 PhotoItem（仅包含文件名和路径）
        foreach (var kvp in allFiles.OrderBy(x => x.Key))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var fileName = kvp.Key;
            var (jpgPath, rawPath) = kvp.Value;

            if (string.IsNullOrEmpty(jpgPath) && string.IsNullOrEmpty(rawPath))
                continue;

            photoItems.Add(new PhotoItem
            {
                FileName = fileName,
                JpgPath = jpgPath ?? string.Empty,
                RawPath = rawPath
            });
        }

        return photoItems;
    }

    /// <summary>
    /// 填充基础信息 - 仅文件大小和修改日期
    /// </summary>
    public async Task PopulateBasicInfoAsync(PhotoItem photoItem)
    {
        try
        {
            // 验证文件存在性
            if (!string.IsNullOrEmpty(photoItem.JpgPath) && !File.Exists(photoItem.JpgPath))
            {
                photoItem.JpgPath = string.Empty;
            }
            if (!string.IsNullOrEmpty(photoItem.RawPath) && !File.Exists(photoItem.RawPath))
            {
                photoItem.RawPath = null;
            }

            if (string.IsNullOrEmpty(photoItem.JpgPath) && string.IsNullOrEmpty(photoItem.RawPath))
                return;

            // 优先从 JPG 获取文件信息
            var filePath = photoItem.HasJpg ? photoItem.JpgPath : photoItem.RawPath;

            if (!string.IsNullOrEmpty(filePath) && File.Exists(filePath))
            {
                var fileInfo = new FileInfo(filePath);
                photoItem.FileSize = fileInfo.Length;
                photoItem.ModifiedDate = fileInfo.LastWriteTime;
            }

            // 如果同时有 JPG 和 RAW，计算总大小
            if (photoItem.HasJpg && photoItem.HasRaw && photoItem.RawPath != null)
            {
                if (File.Exists(photoItem.RawPath))
                {
                    var rawInfo = new FileInfo(photoItem.RawPath);
                    photoItem.FileSize += rawInfo.Length;
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"获取基础信息失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 填充完整元数据 - EXIF、尺寸等
    /// </summary>
    public async Task PopulateFullMetadataAsync(PhotoItem photoItem)
    {
        try
        {
            var filePath = photoItem.HasJpg ? photoItem.JpgPath : photoItem.RawPath;

            if (!string.IsNullOrEmpty(filePath) && File.Exists(filePath))
            {
                var metadata = await MetadataService.ReadMetadataAsync(filePath);
                if (metadata != null)
                {
                    photoItem.Width = metadata.Width;
                    photoItem.Height = metadata.Height;
                    photoItem.Dpi = metadata.Dpi;
                    photoItem.DateTimeTaken = metadata.DateTimeTaken;
                    photoItem.CameraModel = metadata.CameraModel;
                    photoItem.LensModel = metadata.LensModel;
                    photoItem.ISO = metadata.ISO;
                    photoItem.FNumber = metadata.FNumber;
                    photoItem.ExposureTime = metadata.ExposureTime;
                    photoItem.Flash = metadata.Flash;
                    photoItem.ExposureBias = metadata.ExposureBias;
                    photoItem.Dimensions = metadata.Width > 0 ? $"{metadata.Width} x {metadata.Height}" : null;
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"获取元数据失败: {ex.Message}");
        }
    }
}
