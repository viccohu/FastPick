using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

namespace FastPick.Services;

public class RatingStoreService
{
    private static readonly Lazy<RatingStoreService> _instance = new(() => new RatingStoreService());
    public static RatingStoreService Instance => _instance.Value;

    private class RatingEntry
    {
        public string FileName { get; set; } = string.Empty;
        public int Rating { get; set; }
        public DateTime LastModified { get; set; }
        public long FileSize { get; set; }
    }

    private class RatingDatabase
    {
        public int Version { get; set; } = 1;
        public Dictionary<string, RatingEntry> Ratings { get; set; } = new();
    }

    private static string DatabasePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "FastPick",
        "ratings.json");

    private RatingDatabase _database = new();
    private readonly Dictionary<string, string> _fileIdCache = new();
    private bool _isLoaded = false;
    private readonly object _lock = new();

    private RatingStoreService() { }

    private void EnsureLoaded()
    {
        if (_isLoaded) return;
        
        lock (_lock)
        {
            if (_isLoaded) return;
            Load();
            _isLoaded = true;
        }
    }

    private void Load()
    {
        try
        {
            if (File.Exists(DatabasePath))
            {
                var json = File.ReadAllText(DatabasePath);
                _database = JsonSerializer.Deserialize<RatingDatabase>(json) ?? new RatingDatabase();
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[RatingStoreService] 加载数据库失败: {ex.Message}");
            _database = new RatingDatabase();
        }
    }

    private void Save()
    {
        try
        {
            var directory = Path.GetDirectoryName(DatabasePath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var json = JsonSerializer.Serialize(_database, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(DatabasePath, json);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[RatingStoreService] 保存数据库失败: {ex.Message}");
        }
    }

    public string ComputeFileId(string filePath)
    {
        var cacheKey = filePath.ToLowerInvariant();
        if (_fileIdCache.TryGetValue(cacheKey, out var cachedId))
        {
            return cachedId;
        }

        try
        {
            var fileInfo = new FileInfo(filePath);
            var fileSize = fileInfo.Length;

            using var stream = File.OpenRead(filePath);
            using var sha256 = SHA256.Create();

            var headSize = (int)Math.Min(65536, fileSize);
            var headBuffer = new byte[headSize];
            stream.Read(headBuffer, 0, headSize);
            sha256.TransformBlock(headBuffer, 0, headSize, null, 0);

            if (fileSize > 65536)
            {
                stream.Seek(-Math.Min(65536, fileSize - 65536), SeekOrigin.End);
                var tailSize = (int)Math.Min(65536, fileSize - 65536);
                var tailBuffer = new byte[tailSize];
                stream.Read(tailBuffer, 0, tailSize);
                sha256.TransformBlock(tailBuffer, 0, tailSize, null, 0);
            }

            var sizeBytes = BitConverter.GetBytes(fileSize);
            sha256.TransformFinalBlock(sizeBytes, 0, sizeBytes.Length);

            var hash = Convert.ToHexString(sha256.Hash!);
            _fileIdCache[cacheKey] = hash;
            return hash;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[RatingStoreService] 计算文件ID失败: {filePath}, {ex.Message}");
            return string.Empty;
        }
    }

    public int GetRating(string filePath)
    {
        EnsureLoaded();
        
        var fileId = ComputeFileId(filePath);
        if (string.IsNullOrEmpty(fileId)) return 0;

        return GetRatingByFileId(fileId);
    }

    public int GetRatingByFileId(string fileId)
    {
        EnsureLoaded();

        if (_database.Ratings.TryGetValue(fileId, out var entry))
        {
            return entry.Rating;
        }

        return 0;
    }

    public void SetRating(string filePath, int rating)
    {
        EnsureLoaded();

        rating = Math.Clamp(rating, 0, 5);
        var fileId = ComputeFileId(filePath);
        if (string.IsNullOrEmpty(fileId)) return;

        var fileInfo = new FileInfo(filePath);
        var fileName = fileInfo.Name;

        SetRatingByFileId(fileId, fileName, rating, fileInfo.Length);
    }

    public void SetRatingByFileId(string fileId, string fileName, int rating, long fileSize)
    {
        EnsureLoaded();

        rating = Math.Clamp(rating, 0, 5);

        _database.Ratings[fileId] = new RatingEntry
        {
            FileName = fileName,
            Rating = rating,
            LastModified = DateTime.Now,
            FileSize = fileSize
        };

        Save();
    }

    public Dictionary<string, int> GetRatingsBatch(IEnumerable<string> filePaths)
    {
        EnsureLoaded();

        var result = new Dictionary<string, int>();
        foreach (var filePath in filePaths)
        {
            result[filePath] = GetRating(filePath);
        }
        return result;
    }

    public void SetRatingsBatch(Dictionary<string, int> ratings)
    {
        EnsureLoaded();

        foreach (var kvp in ratings)
        {
            SetRating(kvp.Key, kvp.Value);
        }
    }

    public void TransferRating(string sourcePath, string targetPath)
    {
        EnsureLoaded();

        var sourceId = ComputeFileId(sourcePath);
        if (string.IsNullOrEmpty(sourceId)) return;

        if (_database.Ratings.TryGetValue(sourceId, out var entry))
        {
            SetRating(targetPath, entry.Rating);
        }
    }

    public void RemoveRating(string filePath)
    {
        EnsureLoaded();

        var fileId = ComputeFileId(filePath);
        if (string.IsNullOrEmpty(fileId)) return;

        if (_database.Ratings.Remove(fileId))
        {
            Save();
        }
    }

    public void CleanupInvalidEntries()
    {
        EnsureLoaded();

        var keysToRemove = new List<string>();
        foreach (var kvp in _database.Ratings)
        {
            // 保留条目，因为文件可能暂时不存在
            // 可以根据需要添加更复杂的清理逻辑
        }

        if (keysToRemove.Count > 0)
        {
            foreach (var key in keysToRemove)
            {
                _database.Ratings.Remove(key);
            }
            Save();
        }
    }

    public void ClearCache()
    {
        _fileIdCache.Clear();
    }
}
