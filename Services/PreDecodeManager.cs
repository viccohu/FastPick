using FastPick.Models;
using System.Collections.Concurrent;

namespace FastPick.Services;

/// <summary>
/// 预解码管理器 - 滑动窗口预加载
/// </summary>
public class PreDecodeManager
{
    private readonly ProgressiveLoadManager _progressiveLoadManager;
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _predecodeTasks = new();
    private readonly object _windowLock = new();
    private int _currentFocusIndex = -1;
    private const int WindowSize = 2;
    
    public PreDecodeManager(ProgressiveLoadManager progressiveLoadManager)
    {
        _progressiveLoadManager = progressiveLoadManager;
    }
    
    /// <summary>
    /// 更新预解码窗口，自动取消超出范围的任务
    /// </summary>
    public async Task UpdatePredecodeWindowAsync(
        int newIndex, 
        IReadOnlyList<PhotoItem> photoList)
    {
        List<Task> tasksToAwait;
        
        lock (_windowLock)
        {
            var oldStart = _currentFocusIndex >= 0 ? Math.Max(0, _currentFocusIndex - WindowSize) : -1;
            var oldEnd = _currentFocusIndex >= 0 ? Math.Min(photoList.Count - 1, _currentFocusIndex + WindowSize) : -1;
            var newStart = Math.Max(0, newIndex - WindowSize);
            var newEnd = Math.Min(photoList.Count - 1, newIndex + WindowSize);
            
            // 取消超出新窗口的任务
            if (oldStart >= 0 && oldEnd >= 0)
            {
                for (int i = oldStart; i <= oldEnd; i++)
                {
                    if (i < newStart || i > newEnd)
                    {
                        if (i >= 0 && i < photoList.Count)
                            CancelPredecodeTask(photoList[i]);
                    }
                }
            }
            
            // 启动新窗口内的任务
            tasksToAwait = new List<Task>();
            for (int i = newStart; i <= newEnd; i++)
            {
                if (i == newIndex) continue; // 跳过当前项（由主加载处理）
                if (i < 0 || i >= photoList.Count) continue;
                
                var task = StartPredecodeTaskAsync(photoList[i], i > newIndex);
                if (task != null) tasksToAwait.Add(task);
            }
            
            _currentFocusIndex = newIndex;
        }
        
        // 不等待预解码完成，让它们在后台运行
        _ = Task.WhenAll(tasksToAwait).ContinueWith(_ => { }, TaskScheduler.Default);
    }
    
    /// <summary>
    /// 取消指定项的预解码任务
    /// </summary>
    public void CancelPredecodeTask(PhotoItem item)
    {
        var key = item.DisplayPath;
        if (_predecodeTasks.TryRemove(key, out var cts))
        {
            cts.Cancel();
            cts.Dispose();
        }
    }
    
    /// <summary>
    /// 取消所有预解码任务
    /// </summary>
    public void CancelAllTasks()
    {
        foreach (var kvp in _predecodeTasks)
        {
            kvp.Value.Cancel();
            kvp.Value.Dispose();
        }
        _predecodeTasks.Clear();
    }
    
    private Task? StartPredecodeTaskAsync(PhotoItem item, bool isForward)
    {
        var key = item.DisplayPath;
        
        // 已有任务在运行
        if (_predecodeTasks.ContainsKey(key)) return null;
        
        // 检查是否已有缓存
        if (item.PreviewCache.QuickPreview != null)
            return null;
        
        var cts = new CancellationTokenSource();
        if (!_predecodeTasks.TryAdd(key, cts)) 
        {
            cts.Dispose();
            return null;
        }
        
        var priority = isForward ? TaskPriority.PredecodeForward : TaskPriority.PredecodeBackward;
        
        return PriorityTaskScheduler.Instance.ScheduleAsync(async ct =>
        {
            try
            {
                await _progressiveLoadManager.LoadLevelAsync(
                    item, 
                    PreviewQualityLevel.QuickPreview, 
                    cts.Token);
            }
            catch (OperationCanceledException)
            {
                // 正常取消
            }
            finally
            {
                _predecodeTasks.TryRemove(key, out var removed);
                removed?.Dispose();
            }
        }, priority, cts.Token);
    }
    
    /// <summary>
    /// 获取预解码统计信息
    /// </summary>
    public int GetActiveTaskCount()
    {
        return _predecodeTasks.Count;
    }
}
