using System.Diagnostics;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.FileProperties;

namespace FastPick.Services;

/// <summary>
/// JPG 元数据服务 - 使用 WIC 读写 Exif Rating 标签
/// </summary>
public class JpgMetadataService
{
    /// <summary>
    /// 读取 JPG 评级（使用 Windows 属性系统）
    /// </summary>
    /// <param name="filePath">JPG 文件路径</param>
    /// <returns>评级 (0-5)，读取失败返回 0</returns>
    public async Task<int> ReadRatingAsync(string filePath)
    {
        if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
            return 0;

        try
        {
            var storageFile = await StorageFile.GetFileFromPathAsync(filePath);
            
            // 使用 Windows 属性系统读取 Rating
            var properties = await storageFile.Properties.RetrievePropertiesAsync(new[] { "System.Rating" });
            
            if (properties.TryGetValue("System.Rating", out var ratingObj) && ratingObj is uint ratingValue)
            {
                // Windows Rating 值: 0=未评级, 1=1星, 25=2星, 50=3星, 75=4星, 99=5星
                return ConvertWindowsRatingToStars(ratingValue);
            }
            
            return 0;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"读取 JPG 元数据失败: {filePath}, {ex.Message}");
            return 0;
        }
    }
    
    /// <summary>
    /// 将 Windows Rating 值转换为星级 (0-5)
    /// </summary>
    private int ConvertWindowsRatingToStars(uint windowsRating)
    {
        return windowsRating switch
        {
            0 => 0,
            1 => 1,
            25 => 2,
            50 => 3,
            75 => 4,
            99 => 5,
            _ when windowsRating > 75 => 5,
            _ when windowsRating > 50 => 4,
            _ when windowsRating > 25 => 3,
            _ when windowsRating > 1 => 2,
            _ when windowsRating > 0 => 1,
            _ => 0
        };
    }
    
    /// <summary>
    /// 将星级转换为 Windows Rating 值
    /// </summary>
    private uint ConvertStarsToWindowsRating(int stars)
    {
        return stars switch
        {
            1 => 1,
            2 => 25,
            3 => 50,
            4 => 75,
            5 => 99,
            _ => 0
        };
    }
    
    /// <summary>
    /// 写入 JPG 评级
    /// </summary>
    /// <param name="filePath">JPG 文件路径</param>
    /// <param name="rating">评级 (0-5)</param>
    /// <returns>是否成功</returns>
    public async Task<bool> WriteRatingAsync(string filePath, int rating)
    {
        rating = Math.Clamp(rating, 0, 5);
        
        try
        {
            var storageFile = await StorageFile.GetFileFromPathAsync(filePath);
            
            // 使用 Windows 属性系统写入 Rating
            var windowsRating = ConvertStarsToWindowsRating(rating);
            var propertiesToSave = new Dictionary<string, object>
            {
                ["System.Rating"] = windowsRating
            };
            
            await storageFile.Properties.SavePropertiesAsync(propertiesToSave);
            
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"写入 JPG 元数据失败: {filePath}, {ex.Message}");
            return false;
        }
    }
    
    /// <summary>
    /// 批量写入 JPG 评级
    /// </summary>
    /// <param name="filePaths">JPG 文件路径列表</param>
    /// <param name="rating">评级 (0-5)</param>
    /// <returns>成功写入的文件数</returns>
    public async Task<int> WriteRatingBatchAsync(List<string> filePaths, int rating)
    {
        int successCount = 0;
        
        foreach (var filePath in filePaths)
        {
            if (await WriteRatingAsync(filePath, rating))
            {
                successCount++;
            }
            
            // 小延迟避免阻塞
            await Task.Delay(10);
        }
        
        return successCount;
    }
}
