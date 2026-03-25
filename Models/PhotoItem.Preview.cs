using System.Threading;

namespace FastPick.Models;

/// <summary>
/// PhotoItem 预览扩展 - 线程安全
/// </summary>
public partial class PhotoItem
{
    // 预览缓存 - 延迟初始化，线程安全
    private PreviewImage? _previewCache;
    private readonly object _previewCacheLock = new();
    
    /// <summary>
    /// 预览缓存（L2/L3）
    /// </summary>
    public PreviewImage PreviewCache
    {
        get
        {
            if (_previewCache == null)
            {
                lock (_previewCacheLock)
                {
                    _previewCache ??= new PreviewImage();
                }
            }
            return _previewCache;
        }
    }
    
    // 预览加载取消令牌 - 使用 Interlocked 保证原子性
    private CancellationTokenSource? _previewLoadCts;
    private readonly object _ctsLock = new();
    
    /// <summary>
    /// 预览加载取消令牌源
    /// </summary>
    public CancellationTokenSource? PreviewLoadCts
    {
        get { lock (_ctsLock) return _previewLoadCts; }
        set { lock (_ctsLock) _previewLoadCts = value; }
    }
    
    /// <summary>
    /// 安全地取消并替换令牌
    /// </summary>
    public CancellationToken SafeCancelAndReplacePreviewCts()
    {
        lock (_ctsLock)
        {
            _previewLoadCts?.Cancel();
            _previewLoadCts?.Dispose();
            _previewLoadCts = new CancellationTokenSource();
            return _previewLoadCts.Token;
        }
    }
    
    /// <summary>
    /// 清理预览资源
    /// </summary>
    public void ClearPreviewCache()
    {
        lock (_previewCacheLock)
        {
            _previewCache?.Reset();
        }
        
        lock (_ctsLock)
        {
            _previewLoadCts?.Cancel();
            _previewLoadCts?.Dispose();
            _previewLoadCts = null;
        }
    }
}
