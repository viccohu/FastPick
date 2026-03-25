using System.Collections.Concurrent;

namespace FastPick.Services;

/// <summary>
/// 优先级任务调度器 - 全局单例
/// </summary>
public sealed class PriorityTaskScheduler : IDisposable
{
    private static readonly Lazy<PriorityTaskScheduler> _instance = new(() => new PriorityTaskScheduler());
    public static PriorityTaskScheduler Instance => _instance.Value;
    
    private readonly PriorityQueue<ScheduledTask, int> _taskQueue = new();
    private readonly SemaphoreSlim _concurrentLoader = new(3, 3);
    private readonly object _queueLock = new();
    private readonly CancellationTokenSource _shutdownCts = new();
    private bool _disposed = false;
    
    private PriorityTaskScheduler()
    {
        _ = ProcessQueueAsync(_shutdownCts.Token);
    }
    
    private class ScheduledTask
    {
        public Func<CancellationToken, Task> Action { get; }
        public CancellationToken CancellationToken { get; }
        public TaskCompletionSource<bool> CompletionSource { get; }
        public int Priority { get; }
        
        public ScheduledTask(Func<CancellationToken, Task> action, CancellationToken ct, int priority)
        {
            Action = action;
            CancellationToken = ct;
            CompletionSource = new TaskCompletionSource<bool>();
            Priority = priority;
        }
    }
    
    /// <summary>
    /// 调度任务
    /// </summary>
    public Task ScheduleAsync(Func<CancellationToken, Task> action, int priority, CancellationToken ct = default)
    {
        var task = new ScheduledTask(action, ct, priority);
        
        lock (_queueLock)
        {
            _taskQueue.Enqueue(task, -priority); // 负数使高优先级先出队
        }
        
        return task.CompletionSource.Task;
    }
    
    /// <summary>
    /// 取消低于指定优先级的所有任务
    /// </summary>
    public void CancelTasksBelowPriority(int minPriority)
    {
        lock (_queueLock)
        {
            var temp = new List<ScheduledTask>();
            while (_taskQueue.Count > 0)
            {
                _taskQueue.TryDequeue(out var task, out var negPriority);
                var priority = -negPriority;
                if (task != null && priority >= minPriority)
                {
                    temp.Add(task);
                }
                else if (task != null)
                {
                    task.CompletionSource.TrySetCanceled();
                }
            }
            
            foreach (var task in temp)
            {
                _taskQueue.Enqueue(task, -task.Priority);
            }
        }
    }
    
    private async Task ProcessQueueAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && !_disposed)
        {
            ScheduledTask? task = null;
            
            lock (_queueLock)
            {
                if (_taskQueue.Count > 0)
                {
                    _taskQueue.TryDequeue(out var t, out _);
                    task = t;
                }
            }
            
            if (task == null)
            {
                await Task.Delay(50, ct);
                continue;
            }
            
            if (task.CancellationToken.IsCancellationRequested)
            {
                task.CompletionSource.TrySetCanceled();
                continue;
            }
            
            await _concurrentLoader.WaitAsync(ct);
            try
            {
                await task.Action(task.CancellationToken);
                task.CompletionSource.TrySetResult(true);
            }
            catch (OperationCanceledException)
            {
                task.CompletionSource.TrySetCanceled();
            }
            catch (Exception ex)
            {
                task.CompletionSource.TrySetException(ex);
            }
            finally
            {
                _concurrentLoader.Release();
            }
        }
    }
    
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _shutdownCts.Cancel();
        _shutdownCts.Dispose();
        _concurrentLoader.Dispose();
    }
}
