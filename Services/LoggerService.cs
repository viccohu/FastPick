using System.Diagnostics;
using System.Text;

namespace FastPick.Services;

public enum LogLevel
{
    Verbose = 0,
    Info = 1,
    Warning = 2,
    Error = 3,
    Performance = 4
}

public static class LogCategory
{
    public const string Preview = "预览加载";
    public const string Thumbnail = "缩略图";
    public const string Hierarchical = "分级预取";
    public const string Preload = "智能预加载";
    public const string Cache = "缓存";
    public const string Memory = "内存管理";
    public const string ImageDecode = "图片解码";
    public const string RawProcessing = "RAW处理";
    public const string Performance = "性能监控";
    public const string Navigation = "导航";
}

public class LogEntry
{
    public DateTime Timestamp { get; set; }
    public LogLevel Level { get; set; }
    public string Category { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public long? DurationMs { get; set; }
}

public class LoggerService
{
    private static readonly Lazy<LoggerService> _instance = new(() => new LoggerService());
    public static LoggerService Instance => _instance.Value;

    private readonly List<LogEntry> _logBuffer = new();
    private readonly object _lock = new();
    private const int MaxBufferSize = 1000;

    private LogLevel _minimumLevel = LogLevel.Info;
    private bool _enablePerformanceLogging = true;
    private bool _enabled = true;

    public event EventHandler<LogEntry>? LogAdded;

    private LoggerService()
    {
    }

    public void Configure(LogLevel minimumLevel, bool enablePerformanceLogging)
    {
        lock (_lock)
        {
            _minimumLevel = minimumLevel;
            _enablePerformanceLogging = enablePerformanceLogging;
        }
    }

    public void Enable()
    {
        lock (_lock)
        {
            _enabled = true;
        }
    }

    public void Disable()
    {
        lock (_lock)
        {
            _enabled = false;
        }
    }

    public bool IsEnabled
    {
        get
        {
            lock (_lock)
            {
                return _enabled;
            }
        }
    }

    public void Log(LogLevel level, string category, string message, long? durationMs = null)
    {
        if (!_enabled)
            return;

        if (level == LogLevel.Performance && !_enablePerformanceLogging)
            return;

        if (level < _minimumLevel && level != LogLevel.Performance)
            return;

        var entry = new LogEntry
        {
            Timestamp = DateTime.Now,
            Level = level,
            Category = category,
            Message = message,
            DurationMs = durationMs
        };

        lock (_lock)
        {
            _logBuffer.Add(entry);
            while (_logBuffer.Count > MaxBufferSize)
            {
                _logBuffer.RemoveAt(0);
            }
        }

        WriteToDebug(entry);
        LogAdded?.Invoke(this, entry);
    }

    private void WriteToDebug(LogEntry entry)
    {
        var levelStr = GetLevelString(entry.Level);
        var sb = new StringBuilder();
        sb.Append($"[{entry.Timestamp:yyyy-MM-dd HH:mm:ss.fff}]");
        sb.Append($"[{levelStr}]");
        sb.Append($"[{entry.Category}]");
        sb.Append($" {entry.Message}");

        if (entry.DurationMs.HasValue)
        {
            sb.Append($" ({entry.DurationMs.Value}ms)");
        }

        Debug.WriteLine(sb.ToString());
    }

    private string GetLevelString(LogLevel level) => level switch
    {
        LogLevel.Verbose => "VERBOSE",
        LogLevel.Info => "INFO",
        LogLevel.Warning => "WARN",
        LogLevel.Error => "ERROR",
        LogLevel.Performance => "PERF",
        _ => "INFO"
    };

    public void Verbose(string category, string message)
        => Log(LogLevel.Verbose, category, message);

    public void Info(string category, string message)
        => Log(LogLevel.Info, category, message);

    public void Warning(string category, string message)
        => Log(LogLevel.Warning, category, message);

    public void Error(string category, string message)
        => Log(LogLevel.Error, category, message);

    public void Error(string category, string message, Exception ex)
        => Log(LogLevel.Error, category, $"{message}: {ex.Message}");

    public void Performance(string category, string message, long durationMs)
        => Log(LogLevel.Performance, category, message, durationMs);

    public IDisposable StartTimer(string category, string operationName)
    {
        return new PerformanceTimer(this, category, operationName);
    }

    public List<LogEntry> GetLogs(LogLevel? minLevel = null, string? category = null)
    {
        lock (_lock)
        {
            var query = _logBuffer.AsEnumerable();
            if (minLevel.HasValue)
                query = query.Where(l => l.Level >= minLevel.Value);
            if (!string.IsNullOrEmpty(category))
                query = query.Where(l => l.Category == category);
            return new List<LogEntry>(query);
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _logBuffer.Clear();
        }
    }

    public void PrintPerformanceSummary()
    {
        lock (_lock)
        {
            var perfLogs = _logBuffer.Where(l => l.Level == LogLevel.Performance && l.DurationMs.HasValue).ToList();
            if (perfLogs.Count == 0)
                return;

            var stats = perfLogs
                .GroupBy(l => l.Category)
                .Select(g => new
                {
                    Category = g.Key,
                    Avg = g.Average(l => l.DurationMs!.Value),
                    Min = g.Min(l => l.DurationMs!.Value),
                    Max = g.Max(l => l.DurationMs!.Value),
                    Count = g.Count()
                })
                .OrderByDescending(s => s.Count)
                .ToList();

            var sb = new StringBuilder();
            sb.AppendLine();
            sb.AppendLine("══════════════════════════════════════════════════════════════");
            sb.AppendLine("                    📊 性能统计汇总");
            sb.AppendLine("══════════════════════════════════════════════════════════════");
            sb.AppendLine($"{"分类".PadRight(18)} {"平均".PadRight(10)} {"范围".PadRight(15)} {"次数".PadRight(8)}");
            sb.AppendLine("──────────────────────────────────────────────────────────────");

            foreach (var stat in stats)
            {
                sb.AppendLine($"{stat.Category.PadRight(18)} {stat.Avg:F0}ms".PadRight(28) +
                    $" {stat.Min}-{stat.Max}ms".PadRight(15) +
                    $" {stat.Count}".PadRight(8));
            }

            sb.AppendLine("══════════════════════════════════════════════════════════════");
            Debug.WriteLine(sb.ToString());
        }
    }

    private class PerformanceTimer : IDisposable
    {
        private readonly LoggerService _logger;
        private readonly string _category;
        private readonly string _operationName;
        private readonly Stopwatch _stopwatch;
        private bool _disposed;

        public PerformanceTimer(LoggerService logger, string category, string operationName)
        {
            _logger = logger;
            _category = category;
            _operationName = operationName;
            _stopwatch = Stopwatch.StartNew();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _stopwatch.Stop();
            _logger.Performance(_category, _operationName, _stopwatch.ElapsedMilliseconds);
        }
    }
}
