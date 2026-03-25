using Microsoft.UI.Xaml.Media.Imaging;
using System.Threading;

namespace FastPick.Models;

/// <summary>
/// 预览加载状态
/// </summary>
public enum PreviewLoadState
{
    NotLoaded = 0,
    Loading = 1,
    Loaded = 2,
    Failed = 3
}

/// <summary>
/// 预览质量级别
/// </summary>
public enum PreviewQualityLevel
{
    Thumbnail = 1,      // L1 - 复用 ThumbnailService
    QuickPreview = 2,   // L2 - 快速大图
    FullResolution = 3  // L3 - 原图
}

/// <summary>
/// 预览图像 - 线程安全模型
/// </summary>
public class PreviewImage
{
    private readonly object _lock = new();
    
    // L2: 快速大图
    private BitmapImage? _quickPreview;
    public BitmapImage? QuickPreview
    {
        get { lock (_lock) return _quickPreview; }
        set { lock (_lock) _quickPreview = value; }
    }
    
    // L3: 原图
    private BitmapImage? _fullResolution;
    public BitmapImage? FullResolution
    {
        get { lock (_lock) return _fullResolution; }
        set { lock (_lock) _fullResolution = value; }
    }
    
    // 加载状态 - 使用 Interlocked 保证原子性
    private int _quickPreviewState = (int)PreviewLoadState.NotLoaded;
    private int _fullResolutionState = (int)PreviewLoadState.NotLoaded;
    
    public PreviewLoadState QuickPreviewState
    {
        get => (PreviewLoadState)Interlocked.CompareExchange(ref _quickPreviewState, 0, 0);
        set => Interlocked.Exchange(ref _quickPreviewState, (int)value);
    }
    
    public PreviewLoadState FullResolutionState
    {
        get => (PreviewLoadState)Interlocked.CompareExchange(ref _fullResolutionState, 0, 0);
        set => Interlocked.Exchange(ref _fullResolutionState, (int)value);
    }
    
    /// <summary>
    /// 尝试开始加载，返回是否成功获取锁
    /// </summary>
    public bool TryStartLoading(PreviewQualityLevel level)
    {
        return level switch
        {
            PreviewQualityLevel.QuickPreview => 
                Interlocked.CompareExchange(ref _quickPreviewState, 
                    (int)PreviewLoadState.Loading, 
                    (int)PreviewLoadState.NotLoaded) == (int)PreviewLoadState.NotLoaded,
            PreviewQualityLevel.FullResolution => 
                Interlocked.CompareExchange(ref _fullResolutionState, 
                    (int)PreviewLoadState.Loading, 
                    (int)PreviewLoadState.NotLoaded) == (int)PreviewLoadState.NotLoaded,
            _ => false
        };
    }
    
    /// <summary>
    /// 标记加载完成
    /// </summary>
    public void MarkLoaded(PreviewQualityLevel level, bool success)
    {
        var state = success ? PreviewLoadState.Loaded : PreviewLoadState.Failed;
        if (level == PreviewQualityLevel.QuickPreview)
            QuickPreviewState = state;
        else if (level == PreviewQualityLevel.FullResolution)
            FullResolutionState = state;
    }
    
    /// <summary>
    /// 获取当前可用的最高质量图像
    /// </summary>
    public BitmapImage? GetBestAvailableImage()
    {
        lock (_lock)
        {
            return _fullResolution ?? _quickPreview;
        }
    }
    
    /// <summary>
    /// 重置状态
    /// </summary>
    public void Reset()
    {
        lock (_lock)
        {
            _quickPreview = null;
            _fullResolution = null;
        }
        QuickPreviewState = PreviewLoadState.NotLoaded;
        FullResolutionState = PreviewLoadState.NotLoaded;
    }
}

/// <summary>
/// 任务优先级常量
/// </summary>
public static class TaskPriority
{
    public const int CurrentPreview = 100;      // P0: 当前显示
    public const int PredecodeForward = 80;     // P1: 预解码下一张
    public const int PredecodeBackward = 70;    // P1: 预解码上一张
    public const int BackgroundThumbnail = 50;  // P2: 后台缩略图
}
