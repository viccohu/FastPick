using System.Diagnostics;

namespace FastPick.Services;

public class PerformanceMetrics
{
    public string OperationName { get; set; } = string.Empty;
    public long DurationMs { get; set; }
    public DateTime Timestamp { get; set; }
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }
}

public class PerformanceMonitorService
{
    private readonly List<PerformanceMetrics> _metrics = new();
    private readonly object _lock = new();
    private const int MaxMetricsCount = 1000;

    public event EventHandler<PerformanceMetrics>? MetricRecorded;

    public IDisposable StartTimer(string operationName)
    {
        return new PerformanceTimer(this, operationName);
    }

    public void RecordMetric(string operationName, long durationMs, bool success = true, string? errorMessage = null)
    {
        var metric = new PerformanceMetrics
        {
            OperationName = operationName,
            DurationMs = durationMs,
            Timestamp = DateTime.Now,
            Success = success,
            ErrorMessage = errorMessage
        };

        lock (_lock)
        {
            _metrics.Add(metric);

            while (_metrics.Count > MaxMetricsCount)
            {
                _metrics.RemoveAt(0);
            }
        }

        LoggerService.Instance.Performance(LogCategory.Performance, operationName, durationMs);

        MetricRecorded?.Invoke(this, metric);
    }

    public List<PerformanceMetrics> GetMetrics(string? operationName = null)
    {
        lock (_lock)
        {
            if (string.IsNullOrEmpty(operationName))
            {
                return new List<PerformanceMetrics>(_metrics);
            }
            return new List<PerformanceMetrics>(_metrics.Where(m => m.OperationName == operationName));
        }
    }

    public (double avgMs, double minMs, double maxMs, int count) GetStatistics(string operationName)
    {
        lock (_lock)
        {
            var filtered = _metrics.Where(m => m.OperationName == operationName && m.Success).ToList();
            if (filtered.Count == 0)
                return (0, 0, 0, 0);

            var durations = filtered.Select(m => m.DurationMs).ToList();
            return (
                durations.Average(),
                durations.Min(),
                durations.Max(),
                durations.Count
            );
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _metrics.Clear();
        }
        LoggerService.Instance.Info(LogCategory.Performance, "已清空所有指标");
    }

    private class PerformanceTimer : IDisposable
    {
        private readonly PerformanceMonitorService _service;
        private readonly string _operationName;
        private readonly Stopwatch _stopwatch;
        private bool _disposed;

        public PerformanceTimer(PerformanceMonitorService service, string operationName)
        {
            _service = service;
            _operationName = operationName;
            _stopwatch = Stopwatch.StartNew();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _stopwatch.Stop();
            _service.RecordMetric(_operationName, _stopwatch.ElapsedMilliseconds);
        }
    }
}
