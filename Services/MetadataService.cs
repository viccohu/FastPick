using System.Diagnostics;
using System.Xml.Linq;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;
using Windows.Foundation;

namespace FastPick.Services;

/// <summary>
/// 元数据服务
/// 处理图片评级的读取和写入（JPG 写入 Exif，RAW 写入 XMP）
/// </summary>
public static class MetadataService
{
    // XMP 命名空间
    private const string XmpNamespace = "http://ns.adobe.com/xap/1.0/";
    private const string XmpRatingNamespace = "http://ns.adobe.com/xap/1.0/";

    /// <summary>
    /// 照片元数据
    /// </summary>
    public class PhotoMetadata
    {
        public int Width { get; set; }
        public int Height { get; set; }
        public int Dpi { get; set; }
        public long FileSize { get; set; }
        public DateTime? DateTimeTaken { get; set; }
        public string? CameraModel { get; set; }
        public string? LensModel { get; set; }
        public int? ISO { get; set; }
        public double? FNumber { get; set; }
        public double? ExposureTime { get; set; }
        public int? Flash { get; set; }
        public string? ExposureBias { get; set; }
        public DateTime ModifiedDate { get; set; }
    }

    /// <summary>
    /// 读取照片元数据
    /// </summary>
    public static async Task<PhotoMetadata?> ReadMetadataAsync(string filePath)
    {
        if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
            return null;

        try
        {
            var fileInfo = new FileInfo(filePath);
            var metadata = new PhotoMetadata
            {
                FileSize = fileInfo.Length,
                ModifiedDate = fileInfo.LastWriteTime
            };

            var file = await StorageFile.GetFileFromPathAsync(filePath);
            using var stream = await file.OpenReadAsync();
            var decoder = await BitmapDecoder.CreateAsync(stream);

            metadata.Width = (int)decoder.OrientedPixelWidth;
            metadata.Height = (int)decoder.OrientedPixelHeight;
            metadata.Dpi = (int)Math.Max(decoder.DpiX, decoder.DpiY);

            var propertyNames = new[]
            {
                "System.Photo.DateTaken",
                "System.Photo.CameraModel",
                "System.Photo.LensModel",
                "System.Photo.ISOSpeed",
                "System.Photo.FNumber",
                "System.Photo.ExposureTime",
                "System.Photo.Flash",
                "System.Photo.ExposureBias"
            };

            try
            {
                var properties = await decoder.BitmapProperties.GetPropertiesAsync(propertyNames);

                if (properties.TryGetValue("System.Photo.DateTaken", out var dateProp) && dateProp.Value != null)
                {
                    if (dateProp.Value is DateTimeOffset dateOffset)
                    {
                        metadata.DateTimeTaken = dateOffset.DateTime;
                    }
                    else if (dateProp.Value is DateTime date)
                    {
                        metadata.DateTimeTaken = date;
                    }
                }

                if (properties.TryGetValue("System.Photo.CameraModel", out var modelProp) && modelProp.Value != null)
                {
                    metadata.CameraModel = modelProp.Value.ToString();
                }

                if (properties.TryGetValue("System.Photo.LensModel", out var lensProp) && lensProp.Value != null)
                {
                    metadata.LensModel = lensProp.Value.ToString();
                }

                if (properties.TryGetValue("System.Photo.ISOSpeed", out var isoProp) && isoProp.Value != null)
                {
                    if (isoProp.Value is ushort isoValue)
                    {
                        metadata.ISO = isoValue;
                    }
                    else if (double.TryParse(isoProp.Value.ToString(), out var isoDouble))
                    {
                        metadata.ISO = (int)isoDouble;
                    }
                }

                if (properties.TryGetValue("System.Photo.FNumber", out var fNumberProp) && fNumberProp.Value != null)
                {
                    if (fNumberProp.Value is double fNumber)
                    {
                        metadata.FNumber = fNumber;
                    }
                    else if (double.TryParse(fNumberProp.Value.ToString(), out var fNumberDouble))
                    {
                        metadata.FNumber = fNumberDouble;
                    }
                }

                if (properties.TryGetValue("System.Photo.ExposureTime", out var exposureProp) && exposureProp.Value != null)
                {
                    if (exposureProp.Value is double exposureTime)
                    {
                        metadata.ExposureTime = exposureTime;
                    }
                    else if (double.TryParse(exposureProp.Value.ToString(), out var exposureDouble))
                    {
                        metadata.ExposureTime = exposureDouble;
                    }
                }

                if (properties.TryGetValue("System.Photo.Flash", out var flashProp) && flashProp.Value != null)
                {
                    if (flashProp.Value is ushort flashValue)
                    {
                        metadata.Flash = flashValue;
                    }
                    else if (int.TryParse(flashProp.Value.ToString(), out var flashInt))
                    {
                        metadata.Flash = flashInt;
                    }
                }

                if (properties.TryGetValue("System.Photo.ExposureBias", out var biasProp) && biasProp.Value != null)
                {
                    metadata.ExposureBias = biasProp.Value.ToString();
                }
            }
            catch
            {
            }

            return metadata;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"读取元数据失败: {filePath}, 错误: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 读取文件评级
    /// </summary>
    /// <param name="filePath">文件路径</param>
    /// <returns>评级 0-5，读取失败返回 0</returns>
    public static async Task<int> ReadRatingAsync(string filePath)
    {
        if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
            return 0;

        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        
        try
        {
            if (IsJpg(ext))
            {
                return await ReadJpgRatingAsync(filePath);
            }
            else if (IsRaw(ext))
            {
                return await ReadRawRatingAsync(filePath);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"读取评级失败: {filePath}, 错误: {ex.Message}");
        }

        return 0;
    }

    /// <summary>
    /// 写入文件评级
    /// </summary>
    /// <param name="filePath">文件路径</param>
    /// <param name="rating">评级 0-5</param>
    /// <returns>是否成功</returns>
    public static async Task<bool> WriteRatingAsync(string filePath, int rating)
    {
        if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
            return false;

        // 限制评级范围
        rating = Math.Clamp(rating, 0, 5);

        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        
        try
        {
            if (IsJpg(ext))
            {
                return await WriteJpgRatingAsync(filePath, rating);
            }
            else if (IsRaw(ext))
            {
                return await WriteRawRatingAsync(filePath, rating);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"写入评级失败: {filePath}, 错误: {ex.Message}");
        }

        return false;
    }

    /// <summary>
    /// 读取 JPG 的 Exif 评级
    /// </summary>
    private static async Task<int> ReadJpgRatingAsync(string filePath)
    {
        var file = await StorageFile.GetFileFromPathAsync(filePath);
        using var stream = await file.OpenReadAsync();
        var decoder = await BitmapDecoder.CreateAsync(stream);
        
        var properties = await decoder.BitmapProperties.GetPropertiesAsync(new[] { "System.Rating" });
        
        if (properties.TryGetValue("System.Rating", out var ratingProperty) && ratingProperty.Value != null)
        {
            // Windows 评级值转换：0=0星, 1=1星, 25=2星, 50=3星, 75=4星, 99=5星
            var windowsRating = Convert.ToUInt32(ratingProperty.Value);
            return ConvertWindowsRatingToStars(windowsRating);
        }

        return 0;
    }

    /// <summary>
    /// 写入 JPG 的 Exif 评级
    /// </summary>
    private static async Task<bool> WriteJpgRatingAsync(string filePath, int rating)
    {
        var file = await StorageFile.GetFileFromPathAsync(filePath);
        
        // 读取原图
        using var inputStream = await file.OpenReadAsync();
        var decoder = await BitmapDecoder.CreateAsync(inputStream);
        
        // 创建临时文件
        var tempFile = await ApplicationData.Current.TemporaryFolder.CreateFileAsync(
            $"temp_{Guid.NewGuid()}.jpg", CreationCollisionOption.ReplaceExisting);
        
        try
        {
            // 编码并写入新评级
            using var outputStream = await tempFile.OpenAsync(FileAccessMode.ReadWrite);
            var encoder = await BitmapEncoder.CreateForTranscodingAsync(outputStream, decoder);
            
            // 转换星级到 Windows 评级值
            var windowsRating = ConvertStarsToWindowsRating(rating);
            
            // 设置 Exif 属性
            var propertySet = new BitmapPropertySet();
            var ratingValue = new BitmapTypedValue(windowsRating, Windows.Foundation.PropertyType.UInt32);
            propertySet.Add("System.Rating", ratingValue);
            
            encoder.BitmapProperties.SetPropertiesAsync(propertySet).AsTask().Wait();
            await encoder.FlushAsync();
            
            // 替换原文件
            inputStream.Dispose();
            
            await tempFile.CopyAndReplaceAsync(file);
            
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"写入 JPG 评级失败: {ex.Message}");
            return false;
        }
        finally
        {
            // 清理临时文件
            try
            {
                await tempFile.DeleteAsync();
            }
            catch { }
        }
    }

    /// <summary>
    /// 读取 RAW 的评级（优先从 RatingStoreService，兼容 XMP）
    /// </summary>
    private static async Task<int> ReadRawRatingAsync(string rawPath)
    {
        // 优先从 RatingStoreService 读取
        var rating = RatingStoreService.Instance.GetRating(rawPath);
        if (rating > 0)
        {
            return rating;
        }

        // 兼容：从 XMP 文件读取
        var xmpPath = GetXmpPath(rawPath);
        
        if (!File.Exists(xmpPath))
            return 0;

        try
        {
            var xmpContent = await File.ReadAllTextAsync(xmpPath);
            var xmpRating = ParseXmpRating(xmpContent);
            
            // 如果 XMP 中有评级，同步到 RatingStoreService
            if (xmpRating > 0)
            {
                RatingStoreService.Instance.SetRating(rawPath, xmpRating);
            }
            
            return xmpRating;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"读取 RAW XMP 评级失败: {ex.Message}");
            return 0;
        }
    }

    /// <summary>
    /// 写入 RAW 的评级（同时写入 RatingStoreService 和 XMP）
    /// </summary>
    private static async Task<bool> WriteRawRatingAsync(string rawPath, int rating)
    {
        // 写入 RatingStoreService
        RatingStoreService.Instance.SetRating(rawPath, rating);
        
        // 同时写入 XMP 文件（兼容其他软件）
        var xmpPath = GetXmpPath(rawPath);
        
        try
        {
            string xmpContent;
            
            if (File.Exists(xmpPath))
            {
                xmpContent = await File.ReadAllTextAsync(xmpPath);
                xmpContent = UpdateXmpRating(xmpContent, rating);
            }
            else
            {
                xmpContent = CreateXmpWithRating(rating);
            }
            
            await File.WriteAllTextAsync(xmpPath, xmpContent);
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"写入 RAW XMP 评级失败: {ex.Message}");
            return true; // RatingStoreService 已成功保存
        }
    }

    /// <summary>
    /// 获取 XMP 文件路径
    /// </summary>
    private static string GetXmpPath(string rawPath)
    {
        return Path.ChangeExtension(rawPath, ".xmp");
    }

    /// <summary>
    /// 从 XMP 内容解析评级
    /// </summary>
    private static int ParseXmpRating(string xmpContent)
    {
        try
        {
            var doc = XDocument.Parse(xmpContent);
            var xmpMeta = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "xmpmeta");
            
            if (xmpMeta != null)
            {
                var rdf = xmpMeta.Descendants().FirstOrDefault(e => e.Name.LocalName == "RDF");
                if (rdf != null)
                {
                    var description = rdf.Descendants().FirstOrDefault(e => e.Name.LocalName == "Description");
                    if (description != null)
                    {
                        var ratingAttr = description.Attribute(XName.Get("Rating", XmpRatingNamespace));
                        if (ratingAttr != null && int.TryParse(ratingAttr.Value, out var rating))
                        {
                            return Math.Clamp(rating, 0, 5);
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"解析 XMP 评级失败: {ex.Message}");
        }

        return 0;
    }

    /// <summary>
    /// 更新 XMP 内容中的评级
    /// </summary>
    private static string UpdateXmpRating(string xmpContent, int rating)
    {
        try
        {
            var doc = XDocument.Parse(xmpContent);
            var description = doc.Descendants()
                .FirstOrDefault(e => e.Name.LocalName == "Description");
            
            if (description != null)
            {
                // 更新或添加 Rating 属性
                var ratingName = XName.Get("Rating", XmpRatingNamespace);
                var existingAttr = description.Attribute(ratingName);
                
                if (existingAttr != null)
                {
                    existingAttr.Value = rating.ToString();
                }
                else
                {
                    description.SetAttributeValue(ratingName, rating.ToString());
                }
                
                return doc.ToString();
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"更新 XMP 评级失败: {ex.Message}");
        }

        // 如果解析失败，创建新的 XMP
        return CreateXmpWithRating(rating);
    }

    /// <summary>
    /// 创建包含评级的 XMP 内容
    /// </summary>
    private static string CreateXmpWithRating(int rating)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
        sb.AppendLine("<x:xmpmeta xmlns:x=\"adobe:ns:meta/\" x:xmptk=\"FastPick 1.0\">");
        sb.AppendLine("    <rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\">");
        sb.AppendLine("        <rdf:Description rdf:about=\"\"");
        sb.AppendLine("            xmlns:xmp=\"http://ns.adobe.com/xap/1.0/\"");
        sb.AppendLine($"            xmp:Rating=\"{rating}\"/>");
        sb.AppendLine("    </rdf:RDF>");
        sb.AppendLine("</x:xmpmeta>");
        return sb.ToString();
    }

    /// <summary>
    /// 判断是否为 JPG 文件
    /// </summary>
    private static bool IsJpg(string ext)
    {
        return ext is ".jpg" or ".jpeg";
    }

    /// <summary>
    /// 判断是否为 RAW 文件
    /// </summary>
    private static bool IsRaw(string ext)
    {
        return ext is ".cr2" or ".cr3" or ".nef" or ".arw" or ".raf" or ".orf" or ".rw2" or ".dng";
    }

    /// <summary>
    /// 将 Windows 评级值转换为星级
    /// </summary>
    private static int ConvertWindowsRatingToStars(uint windowsRating)
    {
        return windowsRating switch
        {
            0 => 0,
            1 => 1,
            25 => 2,
            50 => 3,
            75 => 4,
            99 => 5,
            _ => 0
        };
    }

    /// <summary>
    /// 将星级转换为 Windows 评级值
    /// </summary>
    private static uint ConvertStarsToWindowsRating(int stars)
    {
        return stars switch
        {
            0 => 0,
            1 => 1,
            2 => 25,
            3 => 50,
            4 => 75,
            5 => 99,
            _ => 0
        };
    }
}