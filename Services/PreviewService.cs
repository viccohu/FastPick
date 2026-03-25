using FastPick.Models;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Dispatching;
using System.Diagnostics;

namespace FastPick.Services;

/// <summary>
/// 预览服务 - 核心服务层单例
/// </summary>
public class PreviewService : IDisposable
{
    private static readonly Lazy<PreviewService> _instance = new(() => new PreviewService());
    public static PreviewService Instance => _instance.Value;
    
    private readonly PreviewCacheManager _cacheManager;
    private readonly ProgressiveLoadManager _progressiveLoadManager;
    private readonly PreDecodeManager _preDecodeManager;
    private readonly ThumbnailService _thumbnailService;
    
    private DateTime _lastNavigationTime = DateTime.MinValue;
    private const int FastNavigationThresholdMs = 300;
    
    private DispatcherQueue? _dispatcherQueue;
    private bool _disposed = false;
    
    private PreviewService()
    {
        _thumbnailService = new ThumbnailService();
        _cacheManager = new PreviewCacheManager();
        _progressiveLoadManager = new ProgressiveLoadManager(_cacheManager, _thumbnailService);
        _preDecodeManager = new PreDecodeManager(_progressiveLoadManager);
        
        DebugService.WriteLine("[PreviewService] 服务实例已创建");
    }
    
    /// <summary>
    /// 初始化 DispatcherQueue（必须在 UI 线程调用）
    /// </summary>
    public void Initialize(DispatcherQueue dispatcherQueue)
    {
        _dispatcherQueue = dispatcherQueue;
        _thumbnailService.InitializeDispatcherQueue();
        _progressiveLoadManager.InitializeDispatcherQueue(dispatcherQueue);
        DebugService.WriteLine("[PreviewService] DispatcherQueue 已初始化");
    }
    
    /// <summary>
    /// 判断是否处于快速翻页模式
    /// </summary>
    private bool IsFastNavigationMode()
    {
        var now = DateTime.Now;
        var timeSinceLastNav = (now - _lastNavigationTime).TotalMilliseconds;
        var isFast = timeSinceLastNav < FastNavigationThresholdMs;
        _lastNavigationTime = now;
        
        DebugService.WriteLine($"[PreviewService] 导航间隔: {timeSinceLastNav:F0}ms, 快速模式: {isFast}");
        
        return isFast;
    }
    
    /// <summary>
    /// 加载预览 - 智能策略
    /// </summary>
    public async Task<BitmapImage?> LoadPreviewAsync(
        PhotoItem item, 
        Action<BitmapImage?>? onProgressUpdate = null,
        CancellationToken cancellationToken = default)
    {
        var fileName = Path.GetFileName(item.DisplayPath);
        var stopwatch = Stopwatch.StartNew();
        
        DebugService.WriteLine($"[PreviewService] ========== 开始加载预览: {fileName} ==========");
        
        var isFastNav = IsFastNavigationMode();
        
        // 取消之前的加载任务
        var ct = item.SafeCancelAndReplacePreviewCts();
        
        DebugService.WriteLine($"[PreviewService] 模式: {(isFastNav ? "快速翻页" : "正常浏览")}, 取消令牌已更新");
        
        try
        {
            BitmapImage? result = null;
            
            if (isFastNav)
            {
                // 快速翻页：只加载 L1 或 L2
                DebugService.WriteLine($"[PreviewService] 快速翻页模式 - 检查 L2 缓存");
                
                var cached = await _cacheManager.GetFromCacheAsync(
                    item.DisplayPath, 
                    PreviewQualityLevel.QuickPreview);
                
                if (cached != null)
                {
                    stopwatch.Stop();
                    DebugService.WriteLine($"[PreviewService] ✓ L2 缓存命中! 尺寸: {cached.PixelWidth}x{cached.PixelHeight}, 耗时: {stopwatch.ElapsedMilliseconds}ms");
                    onProgressUpdate?.Invoke(cached);
                    return cached;
                }
                
                DebugService.WriteLine($"[PreviewService] L2 缓存未命中，开始加载 L2");
                
                // 加载 L2，不加载 L3
                result = await _progressiveLoadManager.LoadLevelAsync(
                    item, 
                    PreviewQualityLevel.QuickPreview, 
                    ct);
                    
                stopwatch.Stop();
                DebugService.WriteLine($"[PreviewService] ✓ 快速模式加载完成, 结果: {(result != null ? $"{result.PixelWidth}x{result.PixelHeight}" : "null")}, 总耗时: {stopwatch.ElapsedMilliseconds}ms");
                onProgressUpdate?.Invoke(result);
            }
            else
            {
                // 正常浏览：三级渐进加载
                DebugService.WriteLine($"[PreviewService] 正常浏览模式 - 开始三级渐进加载");
                
                result = await LoadProgressiveAsync(item, PreviewQualityLevel.FullResolution, onProgressUpdate, ct);
                
                stopwatch.Stop();
                DebugService.WriteLine($"[PreviewService] ✓ 三级加载完成, 最终尺寸: {(result != null ? $"{result.PixelWidth}x{result.PixelHeight}" : "null")}, 总耗时: {stopwatch.ElapsedMilliseconds}ms");
            }
            
            DebugService.WriteLine($"[PreviewService] ========== 预览加载结束: {fileName} ==========");
            
            return result;
        }
        catch (OperationCanceledException)
        {
            stopwatch.Stop();
            DebugService.WriteLine($"[PreviewService] ⚠ 加载已取消: {fileName}, 耗时: {stopwatch.ElapsedMilliseconds}ms");
            return null;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            DebugService.WriteLine($"[PreviewService] ✗ 加载异常: {fileName}, 错误: {ex.Message}, 耗时: {stopwatch.ElapsedMilliseconds}ms");
            return null;
        }
    }
    
    /// <summary>
    /// 三级渐进加载
    /// </summary>
    public async Task<BitmapImage?> LoadProgressiveAsync(
        PhotoItem item,
        PreviewQualityLevel targetLevel,
        Action<BitmapImage?>? onProgressUpdate = null,
        CancellationToken cancellationToken = default)
    {
        var fileName = Path.GetFileName(item.DisplayPath);
        BitmapImage? result = null;
        var totalStopwatch = Stopwatch.StartNew();
        
        // L1: 缩略图
        var l1Stopwatch = Stopwatch.StartNew();
        DebugService.WriteLine($"[PreviewService] [L1-缩略图] 开始加载: {fileName}");
        
        var l1 = await _progressiveLoadManager.LoadLevelAsync(
            item, PreviewQualityLevel.Thumbnail, cancellationToken);
        
        l1Stopwatch.Stop();
        
        if (l1 != null)
        {
            result = l1;
            DebugService.WriteLine($"[PreviewService] [L1-缩略图] ✓ 加载成功! 尺寸: {l1.PixelWidth}x{l1.PixelHeight}, 耗时: {l1Stopwatch.ElapsedMilliseconds}ms");
            onProgressUpdate?.Invoke(result);
        }
        else
        {
            DebugService.WriteLine($"[PreviewService] [L1-缩略图] ✗ 加载失败, 耗时: {l1Stopwatch.ElapsedMilliseconds}ms");
        }
        
        if (targetLevel == PreviewQualityLevel.Thumbnail || cancellationToken.IsCancellationRequested)
        {
            totalStopwatch.Stop();
            DebugService.WriteLine($"[PreviewService] 三级加载提前返回 (目标级别: {targetLevel}, 取消: {cancellationToken.IsCancellationRequested})");
            return result;
        }
        
        // L2: 快速预览
        var l2Stopwatch = Stopwatch.StartNew();
        DebugService.WriteLine($"[PreviewService] [L2-快速预览] 开始加载: {fileName}");
        
        var l2 = await _progressiveLoadManager.LoadLevelAsync(
            item, PreviewQualityLevel.QuickPreview, cancellationToken);
        
        l2Stopwatch.Stop();
        
        if (l2 != null)
        {
            result = l2;
            DebugService.WriteLine($"[PreviewService] [L2-快速预览] ✓ 加载成功! 尺寸: {l2.PixelWidth}x{l2.PixelHeight}, 耗时: {l2Stopwatch.ElapsedMilliseconds}ms");
            onProgressUpdate?.Invoke(result);
        }
        else
        {
            DebugService.WriteLine($"[PreviewService] [L2-快速预览] ✗ 加载失败, 耗时: {l2Stopwatch.ElapsedMilliseconds}ms");
        }
        
        if (targetLevel == PreviewQualityLevel.QuickPreview || cancellationToken.IsCancellationRequested)
        {
            totalStopwatch.Stop();
            DebugService.WriteLine($"[PreviewService] 三级加载提前返回 (目标级别: {targetLevel}, 取消: {cancellationToken.IsCancellationRequested})");
            return result;
        }
        
        // L3: 全分辨率
        var l3Stopwatch = Stopwatch.StartNew();
        DebugService.WriteLine($"[PreviewService] [L3-全分辨率] 开始加载: {fileName}");
        
        var l3 = await _progressiveLoadManager.LoadLevelAsync(
            item, PreviewQualityLevel.FullResolution, cancellationToken);
        
        l3Stopwatch.Stop();
        
        if (l3 != null)
        {
            result = l3;
            DebugService.WriteLine($"[PreviewService] [L3-全分辨率] ✓ 加载成功! 尺寸: {l3.PixelWidth}x{l3.PixelHeight}, 耗时: {l3Stopwatch.ElapsedMilliseconds}ms");
            onProgressUpdate?.Invoke(result);
        }
        else
        {
            DebugService.WriteLine($"[PreviewService] [L3-全分辨率] ✗ 加载失败, 耗时: {l3Stopwatch.ElapsedMilliseconds}ms");
        }
        
        totalStopwatch.Stop();
        DebugService.WriteLine($"[PreviewService] 三级加载全部完成, 总耗时: {totalStopwatch.ElapsedMilliseconds}ms");
        
        return result;
    }
    
    /// <summary>
    /// 更新预解码窗口
    /// </summary>
    public async Task UpdatePredecodeWindowAsync(int newIndex, IReadOnlyList<PhotoItem> photoList)
    {
        DebugService.WriteLine($"[PreviewService] 更新预解码窗口: 当前索引={newIndex}, 列表总数={photoList.Count}");
        await _preDecodeManager.UpdatePredecodeWindowAsync(newIndex, photoList);
    }
    
    /// <summary>
    /// 取消所有预解码任务
    /// </summary>
    public void CancelAllPredecodeTasks()
    {
        DebugService.WriteLine($"[PreviewService] 取消所有预解码任务");
        _preDecodeManager.CancelAllTasks();
    }
    
    /// <summary>
    /// 获取缓存统计
    /// </summary>
    public async Task<(int L2Count, int L3Count, long L2Size, long L3Size)> GetCacheStatsAsync()
    {
        var stats = await _cacheManager.GetCacheStatsAsync();
        DebugService.WriteLine($"[PreviewService] 缓存统计: L2={stats.L2Count}张/{stats.L2Size/1024/1024}MB, L3={stats.L3Count}张/{stats.L3Size/1024/1024}MB");
        return stats;
    }
    
    /// <summary>
    /// 清理缓存
    /// </summary>
    public async Task ClearCacheAsync()
    {
        DebugService.WriteLine($"[PreviewService] 清理所有缓存");
        await _cacheManager.ClearCacheAsync();
    }
    
    /// <summary>
    /// 获取缩略图服务（用于缩略图网格）
    /// </summary>
    public ThumbnailService GetThumbnailService() => _thumbnailService;
    
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        DebugService.WriteLine($"[PreviewService] 服务正在释放资源");
        _preDecodeManager.CancelAllTasks();
        _ = _cacheManager.ClearCacheAsync();
    }
}
