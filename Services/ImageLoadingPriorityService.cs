using System.Collections.Concurrent;
using System.Diagnostics;

namespace FastPick.Services;

public enum ImageLoadingPriority
{
    Critical = 0,
    High = 1,
    Medium = 2,
    Low = 3,
    Background = 4
}

public class ImageLoadingPriorityService
{
    private class PriorityTask
    {
        public ImageLoadingPriority Priority { get; set; }
        public Func<Task> Task { get; set; }
        public CancellationToken CancellationToken { get; set; }
        public DateTime EnqueuedTime { get; set; }
    }

    private readonly ConcurrentDictionary<ImageLoadingPriority, ConcurrentQueue<PriorityTask>> _queues = new();
    private readonly SemaphoreSlim _workerSemaphore = new(0);
    private readonly SemaphoreSlim _queueLock = new(1, 1);
    private readonly CancellationTokenSource _workerCts = new();
    private readonly int _maxConcurrentTasks;
    private int _activeWorkers;
    private bool _isRunning;

    public ImageLoadingPriorityService(int maxConcurrentTasks = 3)
    {
        _maxConcurrentTasks = maxConcurrentTasks;
        StartWorkers();
    }

    private void StartWorkers()
    {
        if (_isRunning) return;
        _isRunning = true;

        for (int i = 0; i < _maxConcurrentTasks; i++)
        {
            _ = Task.Run(WorkerLoop, _workerCts.Token);
        }
    }

    public async Task EnqueueAsync(
        ImageLoadingPriority priority,
        Func<Task> task,
        CancellationToken cancellationToken = default)
    {
        await _queueLock.WaitAsync(cancellationToken);
        try
        {
            if (!_queues.ContainsKey(priority))
            {
                _queues[priority] = new ConcurrentQueue<PriorityTask>();
            }

            var priorityTask = new PriorityTask
            {
                Priority = priority,
                Task = task,
                CancellationToken = cancellationToken,
                EnqueuedTime = DateTime.Now
            };

            _queues[priority].Enqueue(priorityTask);
            Debug.WriteLine($"[优先级队列] 任务入队: 优先级={priority}, 队列大小={_queues[priority].Count}");

            _workerSemaphore.Release();
        }
        finally
        {
            _queueLock.Release();
        }
    }

    private async Task WorkerLoop()
    {
        Interlocked.Increment(ref _activeWorkers);
        Debug.WriteLine($"[优先级队列] 工作线程启动: 活跃线程={_activeWorkers}");

        try
        {
            while (!_workerCts.IsCancellationRequested)
            {
                await _workerSemaphore.WaitAsync(_workerCts.Token);

                PriorityTask? task = null;
                await _queueLock.WaitAsync(_workerCts.Token);
                try
                {
                    foreach (var priority in Enum.GetValues<ImageLoadingPriority>().OrderBy(p => p))
                    {
                        if (_queues.TryGetValue(priority, out var queue) && queue.TryDequeue(out var dequeuedTask))
                        {
                            task = dequeuedTask;
                            Debug.WriteLine($"[优先级队列] 任务出队: 优先级={priority}, 等待时间={(DateTime.Now - task.EnqueuedTime).TotalMilliseconds:F0}ms");
                            break;
                        }
                    }
                }
                finally
                {
                    _queueLock.Release();
                }

                if (task != null)
                {
                    try
                    {
                        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(task.CancellationToken, _workerCts.Token);
                        await task.Task();
                        Debug.WriteLine($"[优先级队列] 任务完成: 优先级={task.Priority}");
                    }
                    catch (OperationCanceledException)
                    {
                        Debug.WriteLine($"[优先级队列] 任务取消: 优先级={task.Priority}");
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[优先级队列] 任务失败: 优先级={task.Priority}, 错误={ex.Message}");
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            Debug.WriteLine($"[优先级队列] 工作线程取消");
        }
        finally
        {
            Interlocked.Decrement(ref _activeWorkers);
            Debug.WriteLine($"[优先级队列] 工作线程停止: 活跃线程={_activeWorkers}");
        }
    }

    public (int critical, int high, int medium, int low, int background) GetQueueStats()
    {
        int critical = 0, high = 0, medium = 0, low = 0, background = 0;

        if (_queues.TryGetValue(ImageLoadingPriority.Critical, out var q1)) critical = q1.Count;
        if (_queues.TryGetValue(ImageLoadingPriority.High, out var q2)) high = q2.Count;
        if (_queues.TryGetValue(ImageLoadingPriority.Medium, out var q3)) medium = q3.Count;
        if (_queues.TryGetValue(ImageLoadingPriority.Low, out var q4)) low = q4.Count;
        if (_queues.TryGetValue(ImageLoadingPriority.Background, out var q5)) background = q5.Count;

        return (critical, high, medium, low, background);
    }

    public void Clear()
    {
        _queueLock.Wait();
        try
        {
            foreach (var queue in _queues.Values)
            {
                while (queue.TryDequeue(out _)) { }
            }
            Debug.WriteLine($"[优先级队列] 清空所有队列");
        }
        finally
        {
            _queueLock.Release();
        }
    }

    public void Dispose()
    {
        _workerCts.Cancel();
        _workerSemaphore.Release(_maxConcurrentTasks);
        _workerCts.Dispose();
        _workerSemaphore.Dispose();
        _queueLock.Dispose();
    }
}
