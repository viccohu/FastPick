namespace FastPick.Services;

public record PathPreviewResult
{
    public bool IsValid { get; init; }
    public int JpgCount { get; init; }
    public int RawCount { get; init; }
    public int MatchedPairs { get; init; }
    public string? ErrorMessage { get; init; }
    public bool IsScanning { get; init; }
}

public class PathPreviewService
{
    private static readonly string[] JpgExtensions = { ".jpg", ".jpeg", ".jpe", ".jfif" };
    private static readonly string[] RawExtensions = { 
        ".raw", ".cr2", ".cr3", ".nef", ".arw", ".dng", ".orf", ".rw2", ".pef", ".srw"
    };

    private readonly Dictionary<string, PathPreviewResult> _cache = new(StringComparer.OrdinalIgnoreCase);

    public async Task<PathPreviewResult> ScanPathAsync(string path, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return new PathPreviewResult { IsValid = false, ErrorMessage = null };
        }

        if (!Directory.Exists(path))
        {
            return new PathPreviewResult { IsValid = false, ErrorMessage = "路径不存在" };
        }

        if (_cache.TryGetValue(path, out var cached))
        {
            return cached;
        }

        try
        {
            return await Task.Run(() =>
            {
                var jpgFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var rawFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                try
                {
                    var searchOption = SearchOption.TopDirectoryOnly;
                    
                    foreach (var ext in JpgExtensions)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var files = Directory.GetFiles(path, $"*{ext}", searchOption);
                        foreach (var file in files)
                        {
                            jpgFiles.Add(System.IO.Path.GetFileNameWithoutExtension(file));
                        }
                    }

                    foreach (var ext in RawExtensions)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var files = Directory.GetFiles(path, $"*{ext}", searchOption);
                        foreach (var file in files)
                        {
                            rawFiles.Add(System.IO.Path.GetFileNameWithoutExtension(file));
                        }
                    }
                }
                catch (UnauthorizedAccessException)
                {
                    return new PathPreviewResult { IsValid = false, ErrorMessage = "无访问权限" };
                }
                catch (OperationCanceledException)
                {
                    return new PathPreviewResult { IsValid = false, IsScanning = true };
                }

                var matchedPairs = jpgFiles.Intersect(rawFiles, StringComparer.OrdinalIgnoreCase).Count();

                var result = new PathPreviewResult
                {
                    IsValid = true,
                    JpgCount = jpgFiles.Count,
                    RawCount = rawFiles.Count,
                    MatchedPairs = matchedPairs
                };

                _cache[path] = result;
                return result;
            }, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return new PathPreviewResult { IsValid = false, IsScanning = true };
        }
        catch (Exception ex)
        {
            return new PathPreviewResult { IsValid = false, ErrorMessage = ex.Message };
        }
    }

    public void ClearCache()
    {
        _cache.Clear();
    }

    public void ClearCache(string path)
    {
        _cache.Remove(path);
    }
}
