using System.Diagnostics;

namespace FastPick.Services;

public class MemoryOptimizationService
{
    private readonly long _memoryWarningThreshold;
    private readonly long _memoryCriticalThreshold;
    private readonly Timer? _memoryCheckTimer;
    private readonly ImageCacheService? _quickThumbCache;
    private readonly ImageCacheService? _highResCache;

    public event EventHandler? MemoryWarning;
    public event EventHandler? MemoryCritical;

    public MemoryOptimizationService(
        ImageCacheService? quickThumbCache = null,
        ImageCacheService? highResCache = null,
        long memoryWarningThresholdMb = 800,
        long memoryCriticalThresholdMb = 1200)
    {
        _quickThumbCache = quickThumbCache;
        _highResCache = highResCache;
        _memoryWarningThreshold = memoryWarningThresholdMb * 1024 * 1024;
        _memoryCriticalThreshold = memoryCriticalThresholdMb * 1024 * 1024;

        _memoryCheckTimer = new Timer(CheckMemoryUsage, null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));
    }

    private void CheckMemoryUsage(object? state)
    {
        try
        {
            var process = Process.GetCurrentProcess();
            var memoryUsed = process.WorkingSet64;

            LoggerService.Instance.Verbose(LogCategory.Memory, $"当前内存使用: {memoryUsed / 1024 / 1024:F0}MB");

            if (memoryUsed > _memoryCriticalThreshold)
            {
                LoggerService.Instance.Warning(LogCategory.Memory, "内存严重超限，执行深度清理");
                PerformDeepCleanup();
                MemoryCritical?.Invoke(this, EventArgs.Empty);
            }
            else if (memoryUsed > _memoryWarningThreshold)
            {
                LoggerService.Instance.Info(LogCategory.Memory, "内存超限，执行轻度清理");
                PerformLightCleanup();
                MemoryWarning?.Invoke(this, EventArgs.Empty);
            }
        }
        catch (Exception ex)
        {
            LoggerService.Instance.Error(LogCategory.Memory, "内存检查失败", ex);
        }
    }

    private void PerformLightCleanup()
    {
        try
        {
            LoggerService.Instance.Verbose(LogCategory.Memory, "轻度清理: 触发GC");

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }
        catch (Exception ex)
        {
            LoggerService.Instance.Error(LogCategory.Memory, "轻度清理失败", ex);
        }
    }

    private void PerformDeepCleanup()
    {
        try
        {
            LoggerService.Instance.Warning(LogCategory.Memory, "深度清理: 触发GC");

            GC.Collect(2, GCCollectionMode.Forced, true, true);
            GC.WaitForPendingFinalizers();
            GC.Collect(2, GCCollectionMode.Forced, true, true);
        }
        catch (Exception ex)
        {
            LoggerService.Instance.Error(LogCategory.Memory, "深度清理失败", ex);
        }
    }

    public void ForceCleanup()
    {
        PerformDeepCleanup();
    }

    public long GetCurrentMemoryUsage()
    {
        try
        {
            var process = Process.GetCurrentProcess();
            return process.WorkingSet64;
        }
        catch
        {
            return 0;
        }
    }

    public void Dispose()
    {
        _memoryCheckTimer?.Dispose();
    }
}
