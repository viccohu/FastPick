using FastPick.Models;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace FastPick.Services;

/// <summary>
/// 文件操作服务
/// 处理批量删除（回收站/永久删除）和导出功能
/// </summary>
public static class FileOperationService
{
    // Windows API 常量
    private const int FO_DELETE = 0x0003;
    private const int FOF_ALLOWUNDO = 0x0040;
    private const int FOF_NOCONFIRMATION = 0x0010;
    private const int FOF_SILENT = 0x0004;

    /// <summary>
    /// 导出选项
    /// </summary>
    public class ExportOptions
    {
        /// <summary>
        /// 导出文件类型选项
        /// </summary>
        public ExportOptionEnum ExportOption { get; set; } = ExportOptionEnum.Both;

        /// <summary>
        /// JPG 文件夹名称
        /// </summary>
        public string JpgFolderName { get; set; } = "JPG";

        /// <summary>
        /// RAW 文件夹名称
        /// </summary>
        public string RawFolderName { get; set; } = "RAW";

        /// <summary>
        /// 最小导出评级（只导出大于等于此评级的图片）
        /// </summary>
        public int MinRating { get; set; } = 1;
    }

    /// <summary>
    /// 导出进度报告
    /// </summary>
    public class ExportProgress
    {
        public int TotalFiles { get; set; }
        public int ProcessedFiles { get; set; }
        public int SuccessCount { get; set; }
        public int FailedCount { get; set; }
        public int JpgCount { get; set; }
        public int RawCount { get; set; }
        public string CurrentFile { get; set; } = "";
        public bool IsCompleted { get; set; }
    }

    /// <summary>
    /// 将单个文件移动到回收站
    /// </summary>
    /// <param name="filePath">要删除的文件路径</param>
    /// <param name="permanentlyDelete">是否永久删除（不放入回收站）</param>
    /// <returns>是否成功</returns>
    public class DeleteResult
    {
        public bool Success { get; set; }
        public int DeletedCount { get; set; }
        public bool WasForcedPermanent { get; set; }
        public string? WarningMessage { get; set; }
        public List<string> RemovableDrivePaths { get; set; } = new();
    }

    public static DeleteResult CheckAndPrepareDelete(List<PhotoItem> photos, bool userWantsRecycleBin)
    {
        var result = new DeleteResult();
        var removablePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var shouldForcePermanent = false;

        foreach (var photo in photos)
        {
            if (!string.IsNullOrEmpty(photo.JpgPath) && File.Exists(photo.JpgPath))
            {
                if (DriveTypeService.Instance.IsRemovableDrive(photo.JpgPath))
                {
                    removablePaths.Add(DriveTypeService.Instance.GetDriveDescription(photo.JpgPath));
                    shouldForcePermanent = true;
                }
            }

            if (!string.IsNullOrEmpty(photo.RawPath) && File.Exists(photo.RawPath))
            {
                if (DriveTypeService.Instance.IsRemovableDrive(photo.RawPath))
                {
                    removablePaths.Add(DriveTypeService.Instance.GetDriveDescription(photo.RawPath));
                    shouldForcePermanent = true;
                }
            }
        }

        if (shouldForcePermanent && userWantsRecycleBin)
        {
            result.WasForcedPermanent = true;
            result.RemovableDrivePaths = removablePaths.ToList();
            result.WarningMessage = $"检测到 {string.Join("、", removablePaths)}，已自动切换为直接删除模式";
        }

        return result;
    }

    public static bool MoveToRecycleBin(string filePath, bool permanentlyDelete = false)
    {
        if (!File.Exists(filePath))
            return false;

        try
        {
            if (permanentlyDelete)
            {
                File.Delete(filePath);
                return true;
            }
            else
            {
                if (DriveTypeService.Instance.IsRemovableDrive(filePath))
                {
                    File.Delete(filePath);
                    return true;
                }
                return SendToRecycleBin(filePath);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"删除文件失败: {filePath}, 错误: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 将文件移动到回收站（批量）
    /// </summary>
    /// <param name="files">要删除的文件路径列表</param>
    /// <param name="permanentlyDelete">是否永久删除（不放入回收站）</param>
    /// <returns>成功删除的文件列表</returns>
    public static async Task<List<string>> MoveToRecycleBinAsync(List<string> files, bool permanentlyDelete = false)
    {
        var successFiles = new List<string>();

        await Task.Run(() =>
        {
            foreach (var file in files)
            {
                if (MoveToRecycleBin(file, permanentlyDelete))
                {
                    successFiles.Add(file);
                }
            }
        });

        return successFiles;
    }

    /// <summary>
    /// 删除 PhotoItem（根据选项删除 JPG/RAW/全部）
    /// </summary>
    /// <param name="photos">要删除的图片项列表</param>
    /// <param name="option">删除选项</param>
    /// <param name="permanentlyDelete">是否永久删除</param>
    /// <returns>成功删除的文件数量</returns>
    public static async Task<int> DeletePhotosAsync(List<PhotoItem> photos, DeleteOptionEnum option, bool permanentlyDelete = false)
    {
        var filesToDelete = new List<string>();

        foreach (var photo in photos)
        {
            switch (option)
            {
                case DeleteOptionEnum.Both:
                    if (!string.IsNullOrEmpty(photo.JpgPath))
                        filesToDelete.Add(photo.JpgPath);
                    if (!string.IsNullOrEmpty(photo.RawPath))
                        filesToDelete.Add(photo.RawPath);
                    break;
                case DeleteOptionEnum.JpgOnly:
                    if (!string.IsNullOrEmpty(photo.JpgPath))
                        filesToDelete.Add(photo.JpgPath);
                    break;
                case DeleteOptionEnum.RawOnly:
                    if (!string.IsNullOrEmpty(photo.RawPath))
                        filesToDelete.Add(photo.RawPath);
                    break;
            }
        }

        var successFiles = await MoveToRecycleBinAsync(filesToDelete, permanentlyDelete);
        return successFiles.Count;
    }

    /// <summary>
    /// 导出图片到指定路径
    /// </summary>
    /// <param name="photos">要导出的图片项列表</param>
    /// <param name="targetPath">目标路径</param>
    /// <param name="options">导出选项</param>
    /// <param name="progress">进度报告回调</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>成功导出的 (JPG数量, RAW数量)</returns>
    public static async Task<(int JpgCount, int RawCount)> ExportPhotosAsync(
        List<PhotoItem> photos, 
        string targetPath, 
        ExportOptions options,
        IProgress<ExportProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(targetPath))
        {
            Directory.CreateDirectory(targetPath);
        }

        // 创建子文件夹
        var jpgFolder = Path.Combine(targetPath, options.JpgFolderName);
        var rawFolder = Path.Combine(targetPath, options.RawFolderName);

        Directory.CreateDirectory(jpgFolder);
        Directory.CreateDirectory(rawFolder);

        var exportProgress = new ExportProgress
        {
            TotalFiles = photos.Count
        };

        var jpgCount = 0;
        var rawCount = 0;

        await Task.Run(() =>
        {
            foreach (var photo in photos)
            {
                if (cancellationToken.IsCancellationRequested)
                    break;

                exportProgress.CurrentFile = photo.FileName;

                try
                {
                    // 导出 JPG：只要有评级就导出（Rating > 0）
                    if (!string.IsNullOrEmpty(photo.JpgPath) && File.Exists(photo.JpgPath) && photo.Rating > 0)
                    {
                        var destPath = Path.Combine(jpgFolder, Path.GetFileName(photo.JpgPath));
                        File.Copy(photo.JpgPath, destPath, true);
                        jpgCount++;
                    }

                    // 导出 RAW：按照评级控制选项筛选
                    if (!string.IsNullOrEmpty(photo.RawPath) && File.Exists(photo.RawPath) && photo.Rating >= options.MinRating)
                    {
                        var destPath = Path.Combine(rawFolder, Path.GetFileName(photo.RawPath));
                        File.Copy(photo.RawPath, destPath, true);
                        // 迁移评级到新位置
                        RatingStoreService.Instance.TransferRating(photo.RawPath, destPath);
                        rawCount++;
                    }

                    exportProgress.JpgCount = jpgCount;
                    exportProgress.RawCount = rawCount;
                    exportProgress.SuccessCount = jpgCount + rawCount;
                }
                catch (Exception ex)
                {
                    exportProgress.FailedCount++;
                    Debug.WriteLine($"导出文件失败: {photo.FileName}, 错误: {ex.Message}");
                }

                exportProgress.ProcessedFiles++;
                progress?.Report(exportProgress);
            }

            exportProgress.IsCompleted = true;
            progress?.Report(exportProgress);
        }, cancellationToken);

        return (jpgCount, rawCount);
    }

    /// <summary>
    /// 在资源管理器中打开文件所在位置
    /// </summary>
    /// <param name="filePath">文件路径</param>
    public static void OpenInExplorer(string filePath)
    {
        if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
            return;

        try
        {
            Process.Start("explorer.exe", $"/select,\"{filePath}\"");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"打开资源管理器失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 在资源管理器中打开文件夹
    /// </summary>
    /// <param name="folderPath">文件夹路径</param>
    public static void OpenFolderInExplorer(string folderPath)
    {
        if (string.IsNullOrEmpty(folderPath) || !Directory.Exists(folderPath))
            return;

        try
        {
            Process.Start("explorer.exe", folderPath);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"打开文件夹失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 获取文件大小（格式化字符串）
    /// </summary>
    public static string GetFileSizeString(string filePath)
    {
        if (!File.Exists(filePath))
            return "-";

        try
        {
            var fileInfo = new FileInfo(filePath);
            var bytes = fileInfo.Length;

            return bytes switch
            {
                < 1024 => $"{bytes} B",
                < 1024 * 1024 => $"{bytes / 1024.0:F1} KB",
                < 1024 * 1024 * 1024 => $"{bytes / (1024.0 * 1024.0):F1} MB",
                _ => $"{bytes / (1024.0 * 1024.0 * 1024.0):F2} GB"
            };
        }
        catch
        {
            return "-";
        }
    }

    /// <summary>
    /// 获取 PhotoItem 的总文件大小
    /// </summary>
    public static string GetTotalFileSizeString(PhotoItem photo)
    {
        long totalBytes = 0;

        if (!string.IsNullOrEmpty(photo.JpgPath) && File.Exists(photo.JpgPath))
        {
            totalBytes += new FileInfo(photo.JpgPath).Length;
        }

        if (!string.IsNullOrEmpty(photo.RawPath) && File.Exists(photo.RawPath))
        {
            totalBytes += new FileInfo(photo.RawPath).Length;
        }

        return totalBytes switch
        {
            < 1024 => $"{totalBytes} B",
            < 1024 * 1024 => $"{totalBytes / 1024.0:F1} KB",
            < 1024 * 1024 * 1024 => $"{totalBytes / (1024.0 * 1024.0):F1} MB",
            _ => $"{totalBytes / (1024.0 * 1024.0 * 1024.0):F2} GB"
        };
    }

    /// <summary>
    /// 使用 Windows API 将文件发送到回收站
    /// </summary>
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperation(ref SHFILEOPSTRUCT lpFileOp);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public int wFunc;
        public string pFrom;
        public string pTo;
        public short fFlags;
        public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        public string lpszProgressTitle;
    }

    /// <summary>
    /// 将文件发送到回收站
    /// </summary>
    private static bool SendToRecycleBin(string filePath)
    {
        try
        {
            var fileOp = new SHFILEOPSTRUCT
            {
                wFunc = FO_DELETE,
                pFrom = filePath + '\0' + '\0',
                fFlags = (short)(FOF_ALLOWUNDO | FOF_SILENT)
            };

            var result = SHFileOperation(ref fileOp);
            return result == 0 && !fileOp.fAnyOperationsAborted;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"发送到回收站失败: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 检查路径是否有效
    /// </summary>
    public static bool IsValidPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;

        try
        {
            // 检查路径字符是否合法
            var invalidChars = Path.GetInvalidPathChars();
            if (path.Any(c => invalidChars.Contains(c)))
                return false;

            // 尝试获取完整路径
            var fullPath = Path.GetFullPath(path);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 确保目录存在
    /// </summary>
    public static void EnsureDirectoryExists(string path)
    {
        if (!Directory.Exists(path))
        {
            Directory.CreateDirectory(path);
        }
    }
}
