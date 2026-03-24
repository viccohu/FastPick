using FastPick.Models;
using FastPick.Services;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Media.Imaging;

namespace FastPick.ViewModels;

/// <summary>
/// 主视图模型 - 管理应用状态和数据绑定
/// </summary>
public class MainViewModel : INotifyPropertyChanged
{
    private readonly ImageScanService _imageScanService;
    private readonly ThumbnailService _thumbnailService;
    private readonly JpgMetadataService _jpgMetadataService;
    private SettingsService _settingsService => SettingsService.Instance;
    private DispatcherQueue? _dispatcherQueue;

    // 增量加载配置
    private const int InitialBatchSize = 100;
    private const int IncrementalBatchSize = 500;
    private CancellationTokenSource? _incrementalLoadCts;

    // 图片列表
    public ObservableCollection<PhotoItem> PhotoItems { get; } = new();

    // 选中图片列表
    public ObservableCollection<PhotoItem> SelectedItems { get; } = new();

    // 预删除列表
    public ObservableCollection<PhotoItem> MarkedForDeletionItems { get; } = new();

    // 上次点击的项，用于 Shift+ 点击范围选择
    private PhotoItem? _lastClickedItem;
    public PhotoItem? LastClickedItem
    {
        get => _lastClickedItem;
        set
        {
            _lastClickedItem = value;
        }
    }

    // Shift 范围选择的锚点项（第一次 Shift 点击的项）
    private PhotoItem? _shiftAnchorItem;
    
    // 锚点点击时的选中状态快照（用于回退选择）
    private HashSet<int>? _anchorSelectionSnapshot;

    // 当前预览的图片
    private PhotoItem? _currentPreviewItem;
    public PhotoItem? CurrentPreviewItem
    {
        get => _currentPreviewItem;
        set
        {
            if (_currentPreviewItem != value)
            {
                _currentPreviewItem = value;
                OnPropertyChanged();
            }
        }
    }

    // 路径1
    private string _path1 = string.Empty;
    public string Path1
    {
        get => _path1;
        set
        {
            if (_path1 != value)
            {
                _path1 = value;
                OnPropertyChanged();
            }
        }
    }

    // 路径2
    private string _path2 = string.Empty;
    public string Path2
    {
        get => _path2;
        set
        {
            if (_path2 != value)
            {
                _path2 = value;
                OnPropertyChanged();
            }
        }
    }

    // 是否正在加载
    private bool _isLoading;
    public bool IsLoading
    {
        get => _isLoading;
        set
        {
            if (_isLoading != value)
            {
                _isLoading = value;
                OnPropertyChanged();
            }
        }
    }

    // 加载进度消息
    private string _loadingMessage = string.Empty;
    public string LoadingMessage
    {
        get => _loadingMessage;
        set
        {
            if (_loadingMessage != value)
            {
                _loadingMessage = value;
                OnPropertyChanged();
            }
        }
    }

    // 进度条是否可见
    private bool _isProgressVisible;
    public bool IsProgressVisible
    {
        get => _isProgressVisible;
        set
        {
            if (_isProgressVisible != value)
            {
                _isProgressVisible = value;
                OnPropertyChanged();
            }
        }
    }

    // 当前进度值
    private int _progressValue;
    public int ProgressValue
    {
        get => _progressValue;
        set
        {
            if (_progressValue != value)
            {
                _progressValue = value;
                OnPropertyChanged();
            }
        }
    }

    // 进度最大值
    private int _progressMaximum = 100;
    public int ProgressMaximum
    {
        get => _progressMaximum;
        set
        {
            if (_progressMaximum != value)
            {
                _progressMaximum = value;
                OnPropertyChanged();
            }
        }
    }

    // 进度说明文字
    private string _progressText = string.Empty;
    public string ProgressText
    {
        get => _progressText;
        set
        {
            if (_progressText != value)
            {
                _progressText = value;
                OnPropertyChanged();
            }
        }
    }

    /// <summary>
    /// 开始进度显示
    /// </summary>
    /// <param name="description">进度说明</param>
    /// <param name="maximum">进度最大值</param>
    public void StartProgress(string description, int maximum = 100)
    {
        if (_dispatcherQueue != null)
        {
            _dispatcherQueue.TryEnqueue(() =>
            {
                ProgressText = description;
                ProgressMaximum = maximum;
                ProgressValue = 0;
                IsProgressVisible = true;
            });
        }
        else
        {
            ProgressText = description;
            ProgressMaximum = maximum;
            ProgressValue = 0;
            IsProgressVisible = true;
        }
    }

    /// <summary>
    /// 更新进度值
    /// </summary>
    /// <param name="value">当前进度值</param>
    public void UpdateProgress(int value)
    {
        if (_dispatcherQueue != null)
        {
            _dispatcherQueue.TryEnqueue(() =>
            {
                ProgressValue = Math.Clamp(value, 0, ProgressMaximum);
            });
        }
        else
        {
            ProgressValue = Math.Clamp(value, 0, ProgressMaximum);
        }
    }

    /// <summary>
    /// 结束进度显示
    /// </summary>
    public void EndProgress()
    {
        if (_dispatcherQueue != null)
        {
            _dispatcherQueue.TryEnqueue(() =>
            {
                IsProgressVisible = false;
                ProgressValue = 0;
                ProgressText = string.Empty;
            });
        }
        else
        {
            IsProgressVisible = false;
            ProgressValue = 0;
            ProgressText = string.Empty;
        }
    }

    // 预删除数量
    public int MarkedForDeletionCount => MarkedForDeletionItems.Count;

    // 总图片数量
    public int TotalCount => PhotoItems.Count;

    // 选中数量
    public int SelectedCount => SelectedItems.Count;

    // 筛选状态
    private FilterState _filterState = new();
    public FilterState FilterState
    {
        get => _filterState;
        set
        {
            if (_filterState != value)
            {
                _filterState = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasActiveFilter));
            }
        }
    }
    
    public bool HasActiveFilter => _filterState.HasActiveFilter || _isFilteringDeleted;
    
    private bool _isFilteringDeleted;
    public bool IsFilteringDeleted
    {
        get => _isFilteringDeleted;
        set
        {
            if (_isFilteringDeleted != value)
            {
                _isFilteringDeleted = value;
                OnPropertyChanged();
                ApplyFilter();
            }
        }
    }
    
    public ObservableCollection<PhotoItem> FilteredPhotoItems { get; } = new();
    
    public int FilteredCount => FilteredPhotoItems.Count;

    public MainViewModel()
    {
        _imageScanService = new ImageScanService();
        _thumbnailService = new ThumbnailService();
        _jpgMetadataService = new JpgMetadataService();
    }

    /// <summary>
    /// 初始化缩略图服务的 DispatcherQueue（必须在 UI 线程上调用）
    /// </summary>
    public void InitializeThumbnailServiceDispatcherQueue()
    {
        _thumbnailService.InitializeDispatcherQueue();
    }

    /// <summary>
    /// 初始化 MainViewModel 的 DispatcherQueue（必须在 UI 线程上调用）
    /// </summary>
    public void InitializeDispatcherQueue()
    {
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
    }

    /// <summary>
    /// 更新缩略图视区信息，用于后台解码优先级排序
    /// </summary>
    /// <param name="startIndex">视区开始索引</param>
    /// <param name="endIndex">视区结束索引</param>
    public void UpdateThumbnailViewportInfo(int startIndex, int endIndex)
    {
        _thumbnailService.UpdateViewportInfo(startIndex, endIndex);
    }

    /// <summary>
    /// 预加载缩略图
    /// </summary>
    /// <param name="photoItems">要预加载的照片项</param>
    /// <returns></returns>
    public async Task PreloadThumbnailsAsync(IEnumerable<PhotoItem> photoItems)
    {
        await _thumbnailService.PreloadThumbnailsAsync(photoItems);
    }

    /// <summary>
    /// 加载图片
    /// </summary>
    public async Task LoadPhotosAsync(IProgress<(int current, int total, string message)>? progress = null, CancellationToken cancellationToken = default)
    {
        if (!_imageScanService.IsPathValid(Path1))
        {
            DebugService.WriteLine($"[LoadPhotos] 路径无效，Path1: {Path1}");
            return;
        }

        IsLoading = true;
        LoadingMessage = "正在扫描图片...";
        DebugService.WriteLine("[LoadPhotos] 开始加载图片");

        try
        {
            // 取消之前的增量加载任务
            _incrementalLoadCts?.Cancel();
            _incrementalLoadCts?.Dispose();
            _incrementalLoadCts = null;

            // 清空现有数据
            PhotoItems.Clear();
            SelectedItems.Clear();
            MarkedForDeletionItems.Clear();
            await _thumbnailService.ClearCacheAsync();
            
            // 显式调用 GC 清理内存
            GC.Collect();
            GC.WaitForPendingFinalizers();

            // 阶段 A：快速扫描 - 仅获取文件名
            progress?.Report((10, 100, "快速扫描文件..."));
            var quickItems = await _imageScanService.ScanFilesQuickAsync(Path1, Path2, cancellationToken);

            if (quickItems.Count == 0)
            {
                ApplyFilter();
                OnPropertyChanged(nameof(TotalCount));
                return;
            }

            // 立即显示前 100 个
            progress?.Report((30, 100, $"显示前 {Math.Min(InitialBatchSize, quickItems.Count)} 张..."));
            var initialBatch = quickItems.Take(InitialBatchSize).ToList();

            // 为初始批次填充基础信息
            foreach (var item in initialBatch)
            {
                await _imageScanService.PopulateBasicInfoAsync(item);
                item.Rating = await ReadRatingFromMetadataAsync(item);
                PhotoItems.Add(item);
            }
            ApplyFilter();
            OnPropertyChanged(nameof(TotalCount));

            // 创建新的取消令牌用于增量加载
            _incrementalLoadCts = new CancellationTokenSource();
            var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _incrementalLoadCts.Token);

            // 启动后台增量加载
            _ = LoadIncrementalAsync(quickItems.Skip(InitialBatchSize).ToList(), progress, linkedCts.Token);
        }
        finally
        {
            IsLoading = false;
            LoadingMessage = string.Empty;
        }
    }

    /// <summary>
    /// 后台增量加载剩余图片
    /// </summary>
    private async Task LoadIncrementalAsync(
        List<PhotoItem> remainingItems,
        IProgress<(int current, int total, string message)>? progress,
        CancellationToken token)
    {
        try
        {
            int totalProcessed = InitialBatchSize;
            int totalCount = totalProcessed + remainingItems.Count;
            int batchCounter = 0;

            for (int i = 0; i < remainingItems.Count; i += IncrementalBatchSize)
            {
                token.ThrowIfCancellationRequested();

                var batch = remainingItems.Skip(i).Take(IncrementalBatchSize).ToList();

                // 阶段 B：填充基础信息和评级
                foreach (var item in batch)
                {
                    await _imageScanService.PopulateBasicInfoAsync(item);
                    item.Rating = await ReadRatingFromMetadataAsync(item);
                }

                // 添加到集合
                foreach (var item in batch)
                {
                    PhotoItems.Add(item);
                }

                batchCounter++;
                // 每 2 批次才调用一次 ApplyFilter，减少 UI 线程阻塞
                if (batchCounter % 2 == 0)
                {
                    ApplyFilter();
                }

                totalProcessed += batch.Count;
                progress?.Report((30 + (totalProcessed * 70 / totalCount), 100, 
                    $"已加载 {totalProcessed}/{totalCount}..."));

                OnPropertyChanged(nameof(TotalCount));

                // 给 UI 线程喘息机会
                await Task.Delay(50, token);
            }

            // 最后确保调用一次 ApplyFilter
            ApplyFilter();

            // 阶段 C：后台读取完整元数据
            _ = LoadMetadataAsync(token);

            // 阶段 D：后台持续解码缩略图
            if (_settingsService.EnableBackgroundThumbnailDecoding)
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var items = PhotoItems.ToList();
                        var itemsWithoutThumbnail = items.Where(item => item.Thumbnail == null).Count();
                        if (itemsWithoutThumbnail > 0)
                        {
                            StartProgress("正在加载缩略图", 1);
                            await _thumbnailService.StartBackgroundDecodingAsync(items, null, token);
                            EndProgress();
                        }
                        DebugService.WriteLine("启动后台缩略图解码任务");
                    }
                    catch (Exception ex)
                    {
                        DebugService.WriteLine($"[后台解码] 出错: {ex.Message}");
                        DebugService.WriteLine($"[后台解码] 堆栈跟踪: {ex.StackTrace}");
                        EndProgress();
                    }
                }, token);
            }

            progress?.Report((100, 100, $"加载完成，共 {totalCount} 张照片"));
        }
        catch (OperationCanceledException)
        {
            DebugService.WriteLine("[增量加载] 任务已取消");
        }
        catch (Exception ex)
        {
            DebugService.WriteLine($"[增量加载] 出错: {ex.Message}");
        }
    }

    /// <summary>
    /// 后台读取完整元数据
    /// </summary>
    private async Task LoadMetadataAsync(CancellationToken token)
    {
        try
        {
            foreach (var item in PhotoItems)
            {
                token.ThrowIfCancellationRequested();

                await _imageScanService.PopulateFullMetadataAsync(item);

                // 每 50 个给 UI 线程机会
                if (PhotoItems.IndexOf(item) % 50 == 0)
                    await Task.Delay(10, token);
            }
        }
        catch (OperationCanceledException)
        {
            DebugService.WriteLine("[元数据加载] 任务已取消");
        }
    }

    /// <summary>
    /// 设置图片评级
    /// </summary>
    public async Task SetRatingAsync(PhotoItem item, int rating)
    {
        if (item == null || rating < 0 || rating > 5)
            return;

        item.Rating = rating;

        // 写入元数据
        await WriteRatingToMetadataAsync(item, rating);
    }

    /// <summary>
    /// 写入评级到元数据
    /// </summary>
    private async Task WriteRatingToMetadataAsync(PhotoItem item, int rating)
    {
        // JPG: 写入 Exif
        if (item.HasJpg)
        {
            await _jpgMetadataService.WriteRatingAsync(item.JpgPath, rating);
        }

        // RAW: 写入 RatingStoreService
        if (item.HasRaw && item.RawPath != null)
        {
            RatingStoreService.Instance.SetRating(item.RawPath, rating);
        }
    }

    /// <summary>
    /// 从元数据读取评级
    /// </summary>
    public async Task<int> ReadRatingFromMetadataAsync(PhotoItem item)
    {
        int rating = 0;

        // 优先从 JPG 读取
        if (item.HasJpg)
        {
            rating = await _jpgMetadataService.ReadRatingAsync(item.JpgPath);
        }

        // 如果没有 JPG 或有 RAW，从 RatingStoreService 读取
        if (rating == 0 && item.HasRaw && item.RawPath != null)
        {
            rating = RatingStoreService.Instance.GetRating(item.RawPath);
        }

        return rating;
    }

    /// <summary>
    /// 批量设置评级（并行优化）
    /// </summary>
    public async Task SetRatingForSelectedAsync(int rating)
    {
        if (rating < 0 || rating > 5)
            return;

        var selectedItems = SelectedItems.ToList();
        if (selectedItems.Count == 0)
            return;

        // 第一步：立即更新所有图片的 Rating 属性（UI 立即响应）
        foreach (var item in selectedItems)
        {
            item.Rating = rating;
        }

        // 第二步：并行写入元数据（后台操作）
        var tasks = selectedItems.Select(item => WriteRatingToMetadataAsync(item, rating));
        await Task.WhenAll(tasks);
    }

    /// <summary>
    /// 切换预删除标记
    /// </summary>
    public void ToggleMarkForDeletion(PhotoItem item)
    {
        if (item == null)
            return;

        item.IsMarkedForDeletion = !item.IsMarkedForDeletion;

        if (item.IsMarkedForDeletion)
        {
            if (!MarkedForDeletionItems.Contains(item))
                MarkedForDeletionItems.Add(item);
        }
        else
        {
            MarkedForDeletionItems.Remove(item);
        }

        OnPropertyChanged(nameof(MarkedForDeletionCount));
    }

    /// <summary>
    /// 批量切换预删除标记
    /// </summary>
    public void ToggleMarkForDeletionForSelected()
    {
        foreach (var item in SelectedItems.ToList())
        {
            ToggleMarkForDeletion(item);
        }
    }

    /// <summary>
    /// 清空预删除列表
    /// </summary>
    public void ClearMarkedForDeletion()
    {
        foreach (var item in MarkedForDeletionItems)
        {
            item.IsMarkedForDeletion = false;
        }
        MarkedForDeletionItems.Clear();
        OnPropertyChanged(nameof(MarkedForDeletionCount));
    }

    /// <summary>
    /// 执行删除
    /// </summary>
    public async Task ExecuteDeletionAsync(DeleteOptionEnum option)
    {
        var itemsToDelete = MarkedForDeletionItems.ToList();
        if (itemsToDelete.Count == 0)
            return;

        StartProgress("正在删除", itemsToDelete.Count);
        int deletedCount = 0;

        try
        {
            foreach (var item in itemsToDelete)
            {
                try
                {
                    // 删除文件
                    if (option == DeleteOptionEnum.Both || option == DeleteOptionEnum.JpgOnly)
                    {
                        if (item.HasJpg)
                            await DeleteFileToRecycleBinAsync(item.JpgPath);
                    }

                    if (option == DeleteOptionEnum.Both || option == DeleteOptionEnum.RawOnly)
                    {
                        if (item.HasRaw && item.RawPath != null)
                            await DeleteFileToRecycleBinAsync(item.RawPath);
                    }

                    // 从列表移除
                    PhotoItems.Remove(item);
                    MarkedForDeletionItems.Remove(item);
                    SelectedItems.Remove(item);
                }
                catch (Exception ex)
                {
                    DebugService.WriteLine($"删除文件失败: {ex.Message}");
                }

                deletedCount++;
                UpdateProgress(deletedCount);
            }

            OnPropertyChanged(nameof(TotalCount));
            OnPropertyChanged(nameof(MarkedForDeletionCount));
            
            // 刷新筛选列表
            ApplyFilter();
        }
        finally
        {
            EndProgress();
        }
    }

    /// <summary>
    /// 删除文件到回收站
    /// </summary>
    private async Task DeleteFileToRecycleBinAsync(string filePath)
    {
        var permanentlyDelete = !_settingsService.DeleteToRecycleBin;
        
        await Task.Run(() =>
        {
            try
            {
                if (File.Exists(filePath))
                {
                    FileOperationService.MoveToRecycleBin(filePath, permanentlyDelete);
                }
            }
            catch (Exception ex)
            {
                DebugService.WriteLine($"删除文件失败: {filePath}, {ex.Message}");
                throw;
            }
        });
    }

    /// <summary>
    /// 选择/取消选择图片
    /// </summary>
    public void SelectItem(PhotoItem item, bool isSelected)
    {
        if (item == null)
            return;

        item.IsSelected = isSelected;

        if (isSelected)
        {
            if (!SelectedItems.Contains(item))
                SelectedItems.Add(item);
        }
        else
        {
            SelectedItems.Remove(item);
        }

        OnPropertyChanged(nameof(SelectedCount));

        // 更新当前预览
        if (isSelected && SelectedItems.Count == 1)
        {
            CurrentPreviewItem = item;
        }
    }

    /// <summary>
    /// 普通点击选择（设置锚点）
    /// </summary>
    public void SelectWithAnchor(PhotoItem item)
    {
        if (item == null)
            return;

        // 如果点击的是已选中的项，不取消其他选中
        if (item.IsSelected)
        {
            // 只更新当前预览项，保持所有选中
            CurrentPreviewItem = item;
            return;
        }

        // 如果点击的是未选中的项，清除其他选中
        DeselectAll();
        
        // 选中当前项
        SelectItem(item, true);
        
        // 设置锚点和快照（普通点击时设置）
        _shiftAnchorItem = item;
        _anchorSelectionSnapshot = new HashSet<int>();
        
        // 记录上次点击的项
        LastClickedItem = item;
    }

    /// <summary>
    /// Ctrl+点击切换选择（不改变锚点）
    /// </summary>
    public void ToggleSelection(PhotoItem item)
    {
        if (item == null)
            return;

        SelectItem(item, !item.IsSelected);
        
        // 记录上次点击的项
        LastClickedItem = item;
    }

    /// <summary>
    /// 范围选择（Shift+ 点击）- 支持往前多选和往后回退
    /// </summary>
    public void SelectRange(PhotoItem clickedItem)
    {
        if (clickedItem == null)
            return;

        var clickedIndex = PhotoItems.IndexOf(clickedItem);
        if (clickedIndex < 0)
            return;

        // 如果有锚点，使用锚点 + 快照进行范围选择
        if (_shiftAnchorItem != null && _anchorSelectionSnapshot != null)
        {
            var anchorIndex = PhotoItems.IndexOf(_shiftAnchorItem);
            if (anchorIndex < 0)
            {
                // 锚点失效，回退到普通选择
                SelectWithAnchor(clickedItem);
                return;
            }

            // 先恢复到锚点时的选择状态
            RestoreSelectionSnapshot(_anchorSelectionSnapshot);

            // 计算新的范围（从锚点到当前点击位置）
            var min = Math.Min(anchorIndex, clickedIndex);
            var max = Math.Max(anchorIndex, clickedIndex);

            // 选中范围内的所有项
            for (int i = min; i <= max; i++)
            {
                var item = PhotoItems[i];
                item.IsSelected = true;
                if (!SelectedItems.Contains(item))
                {
                    SelectedItems.Add(item);
                }
            }
            
            // 记录上次点击的项
            LastClickedItem = clickedItem;
        }
        else
        {
            // 没有锚点，回退到普通选择
            SelectWithAnchor(clickedItem);
        }

        OnPropertyChanged(nameof(SelectedCount));
    }

    /// <summary>
    /// 恢复选择状态快照
    /// </summary>
    private void RestoreSelectionSnapshot(HashSet<int> snapshot)
    {
        // 清除当前所有选中
        foreach (var item in SelectedItems)
        {
            item.IsSelected = false;
        }
        SelectedItems.Clear();

        // 恢复快照中的选中项
        foreach (var index in snapshot)
        {
            if (index >= 0 && index < PhotoItems.Count)
            {
                var item = PhotoItems[index];
                item.IsSelected = true;
                SelectedItems.Add(item);
            }
        }
    }

    /// <summary>
    /// 清除 Shift 选择锚点（在非 Shift 操作后调用）
    /// </summary>
    public void ClearShiftAnchor()
    {
        _shiftAnchorItem = null;
        _anchorSelectionSnapshot = null;
    }

    /// <summary>
    /// 全选（仅选中当前筛选视图中的图片）
    /// </summary>
    public void SelectAll()
    {
        SelectedItems.Clear();
        foreach (var item in FilteredPhotoItems)
        {
            item.IsSelected = true;
            SelectedItems.Add(item);
        }
        OnPropertyChanged(nameof(SelectedCount));
    }

    /// <summary>
    /// 取消全选
    /// </summary>
    public void DeselectAll()
    {
        foreach (var item in SelectedItems)
        {
            item.IsSelected = false;
        }
        SelectedItems.Clear();
        OnPropertyChanged(nameof(SelectedCount));
    }

    /// <summary>
    /// 导航到上一张图片
    /// </summary>
    public void NavigatePrevious()
    {
        if (PhotoItems.Count == 0)
            return;

        // 多选模式：在选中项之间切换
        if (SelectedItems.Count > 1)
        {
            if (CurrentPreviewItem == null)
            {
                CurrentPreviewItem = SelectedItems[0];
                return;
            }

            var currentIndex = SelectedItems.IndexOf(CurrentPreviewItem);
            if (currentIndex > 0)
            {
                CurrentPreviewItem = SelectedItems[currentIndex - 1];
            }
            return;
        }

        // 单选/无选模式：在所有图片之间切换
        if (CurrentPreviewItem == null)
        {
            var firstItem = PhotoItems[0];
            CurrentPreviewItem = firstItem;
            // 同步选中状态并更新锚点
            DeselectAll();
            SelectItem(firstItem, true);
            UpdateAnchorForNavigation(firstItem);
            return;
        }

        var index = PhotoItems.IndexOf(CurrentPreviewItem);
        if (index > 0)
        {
            var newItem = PhotoItems[index - 1];
            CurrentPreviewItem = newItem;
            // 同步选中状态（单选模式）并更新锚点
            DeselectAll();
            SelectItem(newItem, true);
            UpdateAnchorForNavigation(newItem);
        }
    }

    /// <summary>
    /// 导航到下一张图片
    /// </summary>
    public void NavigateNext()
    {
        if (PhotoItems.Count == 0)
            return;

        // 多选模式：在选中项之间切换
        if (SelectedItems.Count > 1)
        {
            if (CurrentPreviewItem == null)
            {
                CurrentPreviewItem = SelectedItems[0];
                return;
            }

            var currentIndex = SelectedItems.IndexOf(CurrentPreviewItem);
            if (currentIndex >= 0 && currentIndex < SelectedItems.Count - 1)
            {
                CurrentPreviewItem = SelectedItems[currentIndex + 1];
            }
            return;
        }

        // 单选/无选模式：在所有图片之间切换
        if (CurrentPreviewItem == null)
        {
            var firstItem = PhotoItems[0];
            CurrentPreviewItem = firstItem;
            // 同步选中状态并更新锚点
            DeselectAll();
            SelectItem(firstItem, true);
            UpdateAnchorForNavigation(firstItem);
            return;
        }

        var index = PhotoItems.IndexOf(CurrentPreviewItem);
        if (index >= 0 && index < PhotoItems.Count - 1)
        {
            var newItem = PhotoItems[index + 1];
            CurrentPreviewItem = newItem;
            // 同步选中状态（单选模式）并更新锚点
            DeselectAll();
            SelectItem(newItem, true);
            UpdateAnchorForNavigation(newItem);
        }
    }

    /// <summary>
    /// 更新导航锚点（用于 Shift 范围选择）
    /// </summary>
    private void UpdateAnchorForNavigation(PhotoItem item)
    {
        _shiftAnchorItem = item;
        _anchorSelectionSnapshot = new HashSet<int>();
        LastClickedItem = item;
    }

    /// <summary>
    /// 获取缩略图
    /// </summary>
    public async Task<object?> GetThumbnailAsync(PhotoItem item, System.Threading.CancellationToken cancellationToken = default)
    {
        return await _thumbnailService.GetThumbnailAsync(item, cancellationToken);
    }

    /// <summary>
    /// 获取缓存的缩略图（优先从内存缓存和本地缓存加载）
    /// </summary>
    /// <param name="photoItem">照片项</param>
    /// <returns>缓存的缩略图，如果不存在则返回 null</returns>
    public async Task<BitmapImage?> GetCachedThumbnailAsync(PhotoItem photoItem)
    {
        return await _thumbnailService.GetCachedThumbnailAsync(photoItem);
    }



    public event PropertyChangedEventHandler? PropertyChanged;

    protected virtual void OnPropertyChanged([CallerMemberName] string propertyName = "")
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    #region 筛选功能

    public void SetFileTypeFilter(FileTypeFilter fileType)
    {
        _filterState.FileType = fileType;
        ApplyFilter();
    }

    public void SetRatingCondition(RatingFilterCondition condition)
    {
        _filterState.RatingCondition = condition;
        ApplyFilter();
    }

    public void SetRatingValue(int value)
    {
        _filterState.RatingValue = value;
        ApplyFilter();
    }

    public void ClearFilter()
    {
        _filterState.Clear();
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        var filteredList = HasActiveFilter 
            ? PhotoItems.Where(item => MatchesFilter(item) && !string.IsNullOrEmpty(item.DisplayPath)).ToList()
            : PhotoItems.Where(item => !string.IsNullOrEmpty(item.DisplayPath)).ToList();
        
        FilteredPhotoItems.Clear();
        foreach (var item in filteredList)
        {
            FilteredPhotoItems.Add(item);
        }
        
        OnPropertyChanged(nameof(FilterState));
        OnPropertyChanged(nameof(HasActiveFilter));
        OnPropertyChanged(nameof(FilteredCount));
    }

    private bool MatchesFilter(PhotoItem item)
    {
        // 预删除筛选
        if (_isFilteringDeleted && !item.IsMarkedForDeletion)
            return false;
        
        bool fileTypeMatch = _filterState.FileType switch
        {
            FileTypeFilter.All => true,
            FileTypeFilter.Both => item.HasJpg && item.HasRaw,
            FileTypeFilter.JpgOnly => item.HasJpg && !item.HasRaw,
            FileTypeFilter.RawOnly => item.HasRaw && !item.HasJpg,
            _ => true
        };

        if (!fileTypeMatch) return false;

        bool ratingMatch = _filterState.RatingCondition switch
        {
            RatingFilterCondition.All => true,
            RatingFilterCondition.HasRating => item.Rating > 0,
            RatingFilterCondition.NoRating => item.Rating == 0,
            RatingFilterCondition.Equals => item.Rating == _filterState.RatingValue,
            RatingFilterCondition.LessOrEqual => item.Rating <= _filterState.RatingValue,
            RatingFilterCondition.GreaterOrEqual => item.Rating >= _filterState.RatingValue,
            _ => true
        };

        return ratingMatch;
    }
    
    public void ClearDeletedFilter()
    {
        IsFilteringDeleted = false;
    }

    public void DebugFilter()
    {
        DebugService.WriteLine($"=== 筛选调试 ===");
        DebugService.WriteLine($"筛选类型: {_filterState.FileType}");
        DebugService.WriteLine($"星级条件: {_filterState.RatingCondition}, 值: {_filterState.RatingValue}");
        DebugService.WriteLine($"总图片数: {PhotoItems.Count}");
        
        int both = PhotoItems.Count(p => p.HasJpg && p.HasRaw);
        int jpgOnly = PhotoItems.Count(p => p.HasJpg && !p.HasRaw);
        int rawOnly = PhotoItems.Count(p => p.HasRaw && !p.HasJpg);
        DebugService.WriteLine($"双文件: {both}, 仅JPG: {jpgOnly}, 仅RAW: {rawOnly}");
        DebugService.WriteLine($"筛选后数量: {FilteredPhotoItems.Count}");
    }

    #endregion
}
