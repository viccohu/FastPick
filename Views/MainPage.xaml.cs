using FastPick.Controls;
using FastPick.Models;
using FastPick.Services;
using FastPick.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Windowing;
using Windows.Graphics;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;

namespace FastPick.Views
{
    /// <summary>
    /// FastPick 主页面 - 五段式布局界面
    /// </summary>
    public partial class MainPage : Page
    {
        private Border _currentDrawer = null;
        private Window _window;
        private AppWindow _appWindow;
        private MainViewModel _viewModel;
        private KeyboardService _keyboardService;
        private CancellationTokenSource? _previewLoadCts;

        private double _zoomScale = 1.0;
        private double _minZoom = 0.1;
        private double _maxZoom = 8.0;  // 从 5.0 改为 8.0，支持 800% 缩放
        
        // 100% 吸附效果相关
        private bool _justSnappedTo100Percent = false;
        private int _snapStayCounter = 0;
        private const double SnapThreshold = 0.05;  // 5% 的容差范围（增强吸附力度）
        private const int SnapStayCount = 4;  // 需要几次滚轮事件才离开 100%（增强停留效果）
        
        private bool _isDragging = false;
        private Windows.Foundation.Point _lastDragPoint;
        private double _translateX = 0;
        private double _translateY = 0;
        
        private double _rotation = 0;
        private bool _flipHorizontal = false;
        private bool _flipVertical = false;
        
        private DispatcherTimer? _toastTimer;
        private DispatcherTimer? _zoomIndicatorTimer;

        // 缩放动画
        private DispatcherTimer? _zoomAnimationTimer;
        private double _targetZoomScale = 1.0;
        private double _startZoomScale = 1.0;
        private double _zoomAnimationProgress = 0;
        private const double ZoomAnimationDuration = 150; // 动画持续时间（毫秒）
        private const double ZoomAnimationInterval = 16; // 约 60fps

        // 抽屉栏锁定状态
        private bool _isDrawerBarLocked = false;

        // 截流标志位
        private bool _isPath1PickerOpen = false;
        private bool _isPath2PickerOpen = false;
        private bool _isExportPathPickerOpen = false;
        private bool _isLoadingPhotos = false;
        private bool _isExporting = false;
        private bool _isUpdatingZoomComboBox = false;

        // 路径预览服务
        private PathPreviewService _pathPreviewService = new();
        private DispatcherTimer? _path1PreviewTimer;
        private DispatcherTimer? _path2PreviewTimer;
        private CancellationTokenSource? _path1PreviewCts;
        private CancellationTokenSource? _path2PreviewCts;

        // 高分辨率预览加载
        private PreviewImageService _previewImageService = new();
        private CancellationTokenSource? _highResLoadCts;
        private PhotoItem? _currentHighResItem;
        private bool _isLoadingHighRes = false;
        private DispatcherTimer? _highResDebounceTimer;
        private const double HighResolutionZoomThreshold = 1.0;

        // 双缓冲预览
        private Image _frontImage;  // 当前显示的 Image
        private Image _backImage;   // 后台加载的 Image

        // 设置服务
        private Services.SettingsService _settingsService => Services.SettingsService.Instance;

        public MainPage()
        {
            this.InitializeComponent();
            _window = App.Window;
            
            // 订阅设置变更事件
            _settingsService.FolderNameChanged += SettingsService_FolderNameChanged;
            
            // 初始化 ViewModel
            _viewModel = new MainViewModel();
            _viewModel.PropertyChanged += ViewModel_PropertyChanged;
            
            // 监听 SelectedItems 集合变化
            _viewModel.SelectedItems.CollectionChanged += SelectedItems_CollectionChanged;
            
            // 初始化键盘服务
            _keyboardService = new KeyboardService(_viewModel);
            _keyboardService.Register(_window);
            
            // 初始化窗口设置
            InitializeWindow();
            
            // 设置标题栏拖拽
            SetupTitleBarDrag();
            
            // 绑定缩略图列表
            SetupThumbnailList();
            
            // 绑定数据
            this.DataContext = _viewModel;
            
            // 设置预览区裁剪
            PreviewContainer.SizeChanged += PreviewContainer_SizeChanged;
            
            // 初始化删除模式按钮样式
            UpdateDeleteModeButtonStyles();

            // 初始化路径预览防抖计时器
            _path1PreviewTimer = new DispatcherTimer();
            _path1PreviewTimer.Interval = TimeSpan.FromMilliseconds(500);
            _path1PreviewTimer.Tick += Path1PreviewTimer_Tick;

            _path2PreviewTimer = new DispatcherTimer();
            _path2PreviewTimer.Interval = TimeSpan.FromMilliseconds(500);
            _path2PreviewTimer.Tick += Path2PreviewTimer_Tick;

            // 初始化双缓冲预览
            _frontImage = PreviewImageFront;
            _backImage = PreviewImageBack;

            // 初始化缩放指示器自动隐藏定时器
            _zoomIndicatorTimer = new DispatcherTimer();
            _zoomIndicatorTimer.Interval = TimeSpan.FromSeconds(2);
            _zoomIndicatorTimer.Tick += ZoomIndicatorTimer_Tick;

            // 在 Page.Loaded 事件中加载保存的路径（确保所有控件都已初始化）
            this.Loaded += MainPage_Loaded;
            this.Unloaded += MainPage_Unloaded;
        }

        private void MainPage_Loaded(object sender, RoutedEventArgs e)
        {
            LoadSavedPaths();
        }

        private void MainPage_Unloaded(object sender, RoutedEventArgs e)
        {
            SaveCurrentPaths();
        }

        private void PreviewContainer_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (PreviewClip != null)
            {
                PreviewClip.Rect = new Windows.Foundation.Rect(0, 0, e.NewSize.Width, e.NewSize.Height);
            }
        }

        #region 数据绑定

        private PhotoItem? _currentPreviewItemForBinding;

        /// <summary>
        /// SelectedItems 集合变化事件
        /// </summary>
        private void SelectedItems_CollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        {
            // 集合变化时，更新所有可见缩略图的选中状态
            UpdateAllThumbnailSelectionVisuals();
        }

        /// <summary>
        /// ViewModel 属性变化事件
        /// </summary>
        private void ViewModel_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            switch (e.PropertyName)
            {
                case nameof(MainViewModel.CurrentPreviewItem):
                    OnCurrentPreviewItemChanged();
                    break;
                case nameof(MainViewModel.MarkedForDeletionCount):
                    UpdateDeleteCount();
                    break;
                case nameof(MainViewModel.IsLoading):
                    UpdateLoadingState();
                    break;
                case nameof(MainViewModel.LoadingMessage):
                    UpdateLoadingMessage();
                    break;
            }
        }

        /// <summary>
        /// 当前预览项变化时
        /// </summary>
        private void OnCurrentPreviewItemChanged()
        {
            if (_currentPreviewItemForBinding != null)
            {
                _currentPreviewItemForBinding.PropertyChanged -= CurrentPreviewItem_PropertyChanged;
            }

            UpdatePreview();

            _currentPreviewItemForBinding = _viewModel.CurrentPreviewItem;
            if (_currentPreviewItemForBinding != null)
            {
                _currentPreviewItemForBinding.PropertyChanged += CurrentPreviewItem_PropertyChanged;
                
                ScrollThumbnailIntoView(_currentPreviewItemForBinding);
                
                UpdateNavigationInfo();
            }
        }

        private void UpdateNavigationInfo()
        {
            if (CurrentIndexRun == null || TotalCountRun == null) return;
            
            var currentIndex = 0;
            if (_viewModel.CurrentPreviewItem != null)
            {
                currentIndex = _viewModel.FilteredPhotoItems.IndexOf(_viewModel.CurrentPreviewItem) + 1;
            }
            
            CurrentIndexRun.Text = currentIndex.ToString();
            TotalCountRun.Text = _viewModel.FilteredCount.ToString();
        }

        private void ScrollThumbnailIntoView(PhotoItem item)
        {
            if (ThumbnailRepeater == null) return;
            
            var index = _viewModel.FilteredPhotoItems.IndexOf(item);
            if (index < 0) return;
            
            // 获取对应的元素
            var element = ThumbnailRepeater.TryGetElement(index);
            if (element != null)
            {
                // 使用元素的 StartBringIntoView 方法
                element.StartBringIntoView();
            }
        }

        /// <summary>
        /// 当前预览项属性变化事件
        /// </summary>
        private void CurrentPreviewItem_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(PhotoItem.Rating))
            {
                var item = _viewModel.CurrentPreviewItem;
                if (item != null)
                {
                    // 评级已在 UpdateFileInfo 中更新
                }
            }
        }

        /// <summary>
        /// 评级变化事件（缩略图区和预览区共用）
        /// </summary>
        private async void RatingControl_Changed(object sender, int rating)
        {
            if (sender is Controls.RatingControl control && control.DataContext is PhotoItem item)
            {
                // 调用 ViewModel 写入元数据并触发 PropertyChanged（自动刷新 UI）
                await _viewModel.SetRatingAsync(item, rating);
                
                // 如果修改的是当前预览项，更新文件信息显示
                if (item == _viewModel.CurrentPreviewItem)
                {
                    UpdateFileInfo(item);
                }
            }
        }

        /// <summary>
        /// 设置缩略图列表
        /// </summary>
        private void SetupThumbnailList()
        {
            var itemsSourceBinding = new Microsoft.UI.Xaml.Data.Binding
            {
                Source = _viewModel,
                Path = new PropertyPath("FilteredPhotoItems"),
                Mode = Microsoft.UI.Xaml.Data.BindingMode.OneWay
            };
            ThumbnailRepeater.SetBinding(ItemsRepeater.ItemsSourceProperty, itemsSourceBinding);
            
            ThumbnailRepeater.ElementPrepared += ThumbnailRepeater_ElementPrepared;
            ThumbnailRepeater.ElementClearing += ThumbnailRepeater_ElementClearing;
        }

        /// <summary>
        /// 缩略图区滚轮事件 - 将垂直滚动转换为水平滚动
        /// </summary>
        private void ThumbnailScrollViewer_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
        {
            var scrollViewer = sender as ScrollViewer;
            if (scrollViewer == null) return;

            var pointer = e.GetCurrentPoint(scrollViewer);
            var delta = pointer.Properties.MouseWheelDelta;

            // 将滚轮增量应用到水平滚动
            scrollViewer.ChangeView(
                scrollViewer.HorizontalOffset - delta,
                null,
                null,
                true
            );

            e.Handled = true;
        }

        /// <summary>
        /// ItemsRepeater 元素准备事件 - 虚拟滚动核心
        /// </summary>
        private async void ThumbnailRepeater_ElementPrepared(ItemsRepeater sender, ItemsRepeaterElementPreparedEventArgs args)
        {
            if (args.Element is Grid thumbnailGrid)
            {
                var photoItem = _viewModel.FilteredPhotoItems[args.Index];
                
                // 设置数据上下文
                thumbnailGrid.DataContext = photoItem;
                
                // 绑定点击事件（只绑定一次）
                thumbnailGrid.PointerPressed -= ThumbnailGrid_PointerPressed;
                thumbnailGrid.PointerPressed += ThumbnailGrid_PointerPressed;
                
                // 订阅 IsSelected 属性变化事件
                photoItem.PropertyChanged -= OnPhotoItemPropertyChanged;
                photoItem.PropertyChanged += OnPhotoItemPropertyChanged;
                
                // 更新选中状态显示
                UpdateSelectionVisual(thumbnailGrid, photoItem.IsSelected);
                
                // 优先检查缓存，如果有缓存直接设置，否则异步加载
                if (photoItem.Thumbnail is Microsoft.UI.Xaml.Media.Imaging.BitmapImage cachedBitmap)
                {
                    var image = thumbnailGrid.FindName("ThumbnailImage") as Image;
                    if (image != null)
                    {
                        image.Source = cachedBitmap;
                    }
                }
                else
                {
                    // 没有缓存时才异步加载
                    await LoadThumbnailToImageAsync(thumbnailGrid, photoItem);
                }
            }
        }

        private void ThumbnailGrid_PointerPressed(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            var thumbnailGrid = sender as Grid;
            var photoItem = thumbnailGrid.DataContext as PhotoItem;
            if (photoItem == null) return;

            var ctrlPressed = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control)
                .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
            var shiftPressed = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Shift)
                .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
            
            _keyboardService.HandleThumbnailClick(photoItem, ctrlPressed, shiftPressed);
            e.Handled = true;
        }

        private void OnPhotoItemPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(PhotoItem.IsSelected))
            {
                for (int i = 0; i < _viewModel.FilteredPhotoItems.Count; i++)
                {
                    var element = ThumbnailRepeater.TryGetElement(i);
                    if (element is Grid thumbnailGrid && thumbnailGrid.DataContext is PhotoItem item)
                    {
                        if (item == sender)
                        {
                            UpdateSelectionVisual(thumbnailGrid, item.IsSelected);
                            break;
                        }
                    }
                }
            }
        }

        /// <summary>
        /// 加载缩略图并直接设置到 Image 控件
        /// </summary>
        private async Task LoadThumbnailToImageAsync(Grid thumbnailGrid, PhotoItem photoItem)
        {
            try
            {
                var image = thumbnailGrid.FindName("ThumbnailImage") as Image;
                if (image == null) return;
                
                // 先检查是否已有缓存
                if (photoItem.Thumbnail is Microsoft.UI.Xaml.Media.Imaging.BitmapImage cachedBitmap)
                {
                    image.Source = cachedBitmap;
                    return;
                }
                
                // 异步加载缩略图
                var thumbnail = await _viewModel.GetThumbnailAsync(photoItem);
                if (thumbnail is Microsoft.UI.Xaml.Media.Imaging.BitmapImage bitmap)
                {
                    // 缓存到 PhotoItem
                    photoItem.Thumbnail = bitmap;
                    
                    // 直接设置到 Image 控件
                    image.Source = bitmap;
                }
            }
            catch (Exception ex)
            {

            }
        }

        /// <summary>
        /// ItemsRepeater 元素清理事件 - 释放资源
        /// </summary>
        private void ThumbnailRepeater_ElementClearing(ItemsRepeater sender, ItemsRepeaterElementClearingEventArgs args)
        {
            if (args.Element is Grid thumbnailGrid)
            {
                // 只清空 Image 控件的 Source，保留 PhotoItem.Thumbnail 缓存
                var image = thumbnailGrid.FindName("ThumbnailImage") as Image;
                if (image != null)
                {
                    image.Source = null;
                }
                
                // 取消订阅 PropertyChanged 事件
                if (thumbnailGrid.DataContext is PhotoItem photoItem)
                {
                    photoItem.PropertyChanged -= OnPhotoItemPropertyChanged;
                }
                
                // 清理数据上下文
                thumbnailGrid.DataContext = null;
            }
        }

        /// <summary>
        /// 更新缩略图选中状态视觉
        /// </summary>
        private void UpdateSelectionVisual(Grid thumbnailGrid, bool isSelected)
        {
            var selectionBorder = thumbnailGrid.FindName("SelectionBorder") as Border;
            if (selectionBorder != null)
            {
                selectionBorder.Visibility = isSelected ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        /// <summary>
        /// 更新所有可见缩略图的选中状态
        /// </summary>
        private void UpdateAllThumbnailSelectionVisuals()
        {
            if (ThumbnailRepeater == null) return;
            
            // 只更新实际生成的元素（虚拟滚动优化）
            // 遍历所有已实现的元素，性能更好
            var count = ThumbnailRepeater.ItemsSourceView.Count;
            for (int i = 0; i < count; i++)
            {
                var element = ThumbnailRepeater.TryGetElement(i);
                if (element is Grid thumbnailGrid && thumbnailGrid.DataContext is PhotoItem photoItem)
                {
                    UpdateSelectionVisual(thumbnailGrid, photoItem.IsSelected);
                }
            }
        }

        /// <summary>
        /// 更新预览区
        /// </summary>
        private void UpdatePreview()
        {
            ResetZoom();
            
            var item = _viewModel.CurrentPreviewItem;
            if (item == null)
            {
                // 清空双缓冲
                if (_frontImage.Source is BitmapImage oldBitmap)
                {
                    oldBitmap.UriSource = null;
                }
                _frontImage.Source = null;
                _backImage.Source = null;
                
                PreviewInfoPanel.Visibility = Visibility.Collapsed;
                PreviewInfoPanel.DataContext = null;
                ClearFileInfo();
                return;
            }

            LoadPreviewImage(item);
            
            PreviewInfoPanel.Visibility = Visibility.Visible;
            PreviewInfoPanel.DataContext = item;
            PreviewFileNameText.Text = item.FileName;
            
            UpdateFileInfo(item);
        }

        private void ClearFileInfo()
        {
            FileNameTextBlock.Text = "-";
            FileYearTextBlock.Text = "-年";
            FileMonthTextBlock.Text = "-月";
            FileDayTextBlock.Text = "-日";
            FileTimeTextBlock.Text = "-";
            FileDimensionsTextBlock.Text = "- x -";
            FileSizeTextBlock.Text = "- MB";
            FileDpiTextBlock.Text = "- dpi";
            FileCameraTextBlock.Text = "-";
            FileLensTextBlock.Text = "-";
            FileISOTextBlock.Text = "-";
            FileApertureTextBlock.Text = "-";
            FileShutterTextBlock.Text = "-";
            FileFlashTextBlock.Text = "-";
            JpgPathPanel.Visibility = Visibility.Collapsed;
            RawPathPanel.Visibility = Visibility.Collapsed;
        }

        private void UpdateFileInfo(PhotoItem item)
        {
            FileNameTextBlock.Text = item.FileName;
            
            var date = item.DateTimeTaken ?? item.ModifiedDate;
            FileYearTextBlock.Text = $"{date.Year}年";
            FileMonthTextBlock.Text = $"{date.Month}月";
            FileDayTextBlock.Text = $"{date.Day}日";
            FileTimeTextBlock.Text = date.ToString("HH:mm:ss");
            
            FileDimensionsTextBlock.Text = item.Width > 0 ? $"{item.Width} x {item.Height}" : "-";
            FileSizeTextBlock.Text = FormatFileSize(item.FileSize);
            FileDpiTextBlock.Text = item.Dpi > 0 ? $"{item.Dpi} dpi" : "- dpi";
            
            // 设备信息
            FileCameraTextBlock.Text = item.CameraModel ?? "-";
            FileLensTextBlock.Text = item.LensModel ?? "-";
            
            // 拍摄参数
            FileISOTextBlock.Text = item.ISO.HasValue ? $"ISO {item.ISO}" : "-";
            FileApertureTextBlock.Text = item.FNumber.HasValue ? $"f/{item.FNumber:0.##}" : "-";
            FileShutterTextBlock.Text = FormatShutterSpeed(item.ExposureTime);
            FileFlashTextBlock.Text = FormatFlashStatus(item.Flash);
            
            JpgPathPanel.Visibility = item.HasJpg ? Visibility.Visible : Visibility.Collapsed;
            if (item.HasJpg)
            {
                JpgPathTextBlock.Text = item.JpgPath;
            }
            
            RawPathPanel.Visibility = item.HasRaw ? Visibility.Visible : Visibility.Collapsed;
            if (item.HasRaw)
            {
                RawPathTextBlock.Text = item.RawPath;
            }
        }

        /// <summary>
        /// 格式化快门速度
        /// </summary>
        private string FormatShutterSpeed(double? exposureTime)
        {
            if (!exposureTime.HasValue)
                return "-";
            
            var time = exposureTime.Value;
            if (time >= 1)
            {
                return $"{time:0.#}s";
            }
            else
            {
                var denominator = (int)Math.Round(1 / time);
                return $"1/{denominator}s";
            }
        }

        /// <summary>
        /// 格式化闪光灯状态
        /// </summary>
        private string FormatFlashStatus(int? flash)
        {
            if (!flash.HasValue)
                return "-";
            
            return flash.Value switch
            {
                0 => "关闭",
                1 => "开启",
                5 => "开启",
                7 => "开启",
                9 => "开启",
                13 => "开启",
                15 => "开启",
                16 => "关闭",
                24 => "自动",
                25 => "自动",
                29 => "自动",
                31 => "自动",
                32 => "无",
                65 => "红眼",
                69 => "红眼",
                71 => "红眼",
                73 => "红眼",
                77 => "红眼",
                79 => "红眼",
                _ => flash.Value.ToString()
            };
        }

        private string GetRatingStars(int rating)
        {
            if (rating <= 0) return "无评级";
            return new string('★', rating) + new string('☆', 5 - rating);
        }

        /// <summary>
        /// 加载预览图（双缓冲优化）
        /// </summary>
        private async void LoadPreviewImage(PhotoItem item)
        {
            // 取消之前的加载任务
            if (_previewLoadCts != null)
            {
                _previewLoadCts.Cancel();
                _previewLoadCts.Dispose();
            }
            _previewLoadCts = new CancellationTokenSource();
            var token = _previewLoadCts.Token;

            // 取消高分辨率加载
            CancelHighResolutionLoading();

            try
            {
                // 使用 PreviewImageService 加载预览图
                var bitmap = await _viewModel.LoadPreviewAsync(item);
                
                // 检查是否被取消或当前项已改变
                if (token.IsCancellationRequested || _viewModel.CurrentPreviewItem != item)
                {
                    return;
                }

                if (bitmap != null)
                {
                    // 设置到后台缓冲
                    _backImage.Source = bitmap;
                    
                    // 交换前后缓冲
                    SwapPreviewBuffers();
                }
            }
            catch (OperationCanceledException)
            {
                // 正常取消，忽略
            }
            catch (Exception ex)
            {

            }
        }

        /// <summary>
        /// 交换前后缓冲
        /// </summary>
        private void SwapPreviewBuffers()
        {
            // 交换前后缓冲引用
            var temp = _frontImage;
            _frontImage = _backImage;
            _backImage = temp;
            
            // 更新可见性
            _frontImage.Visibility = Visibility.Visible;
            _frontImage.Opacity = 1;
            _backImage.Visibility = Visibility.Collapsed;
            _backImage.Opacity = 0;
            
            // 延迟清空后台缓冲，避免立即释放导致闪烁
            _ = Task.Run(async () =>
            {
                await Task.Delay(100);
                this.DispatcherQueue.TryEnqueue(() =>
                {
                    if (_backImage.Source is BitmapImage oldBitmap)
                    {
                        oldBitmap.UriSource = null;
                        _backImage.Source = null;
                    }
                });
            });
        }

        /// <summary>
        /// 加载多选预览图（前5张）
        /// </summary>
        private async void LoadMultiPreviewImages()
        {
            try
            {
                // 清空当前预览
                if (_frontImage.Source is BitmapImage oldBitmap)
                {
                    oldBitmap.UriSource = null;
                }
                _frontImage.Source = null;
                
                // TODO: 实现多选预览平铺显示
                // 暂时只显示第一张
                if (_viewModel.SelectedItems.Count > 0)
                {
                    var firstItem = _viewModel.SelectedItems[0];
                    var bitmap = await _viewModel.LoadPreviewAsync(firstItem);
                    if (bitmap != null)
                    {
                        _frontImage.Source = bitmap;
                    }
                }
            }
            catch (Exception ex)
            {

            }
        }

        /// <summary>
        /// 更新预删除数量
        /// </summary>
        private void UpdateDeleteCount()
        {
            DeleteCountRun.Text = _viewModel.MarkedForDeletionCount.ToString();
        }

        /// <summary>
        /// 更新加载状态
        /// </summary>
        private void UpdateLoadingState()
        {
            LoadingRing.IsActive = _viewModel.IsLoading;
        }

        /// <summary>
        /// 更新加载消息
        /// </summary>
        private void UpdateLoadingMessage()
        {
            // 可以在这里显示加载进度消息
        }

        /// <summary>
        /// 格式化文件大小
        /// </summary>
        private string FormatFileSize(long bytes)
        {
            if (bytes < 1024) return $"{bytes} B";
            if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
            if (bytes < 1024 * 1024 * 1024) return $"{bytes / (1024.0 * 1024):F1} MB";
            return $"{bytes / (1024.0 * 1024 * 1024):F2} GB";
        }

        /// <summary>
        /// 获取文件类型文本
        /// </summary>
        private string GetFileTypeText(PhotoItem item)
        {
            if (item.HasJpg && item.HasRaw) return "JPG + RAW";
            if (item.HasRaw) return "RAW";
            return "JPG";
        }

        #endregion

        #region 窗口初始化

        /// <summary>
        /// 初始化窗口设置（无边框）
        /// </summary>
        private void InitializeWindow()
        {
            if (_window == null) return;

            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(_window);
            var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
            _appWindow = AppWindow.GetFromWindowId(windowId);

            if (_appWindow != null)
            {
                _appWindow.SetPresenter(AppWindowPresenterKind.Overlapped);
                
                var titleBar = _appWindow.TitleBar;
                titleBar.ExtendsContentIntoTitleBar = true;
                titleBar.PreferredHeightOption = TitleBarHeightOption.Standard;
                
                titleBar.BackgroundColor = Microsoft.UI.Colors.Transparent;
                titleBar.ForegroundColor = Microsoft.UI.Colors.White;
                titleBar.InactiveBackgroundColor = Microsoft.UI.Colors.Transparent;
                titleBar.InactiveForegroundColor = Microsoft.UI.ColorHelper.FromArgb(0xFF, 0xB0, 0xB0, 0xB0);
                
                titleBar.ButtonBackgroundColor = Microsoft.UI.Colors.Transparent;
                titleBar.ButtonForegroundColor = Microsoft.UI.Colors.White;
                titleBar.ButtonHoverBackgroundColor = Microsoft.UI.ColorHelper.FromArgb(0xFF, 0x38, 0x38, 0x38);
                titleBar.ButtonHoverForegroundColor = Microsoft.UI.Colors.White;
                titleBar.ButtonPressedBackgroundColor = Microsoft.UI.ColorHelper.FromArgb(0xFF, 0x44, 0x44, 0x44);
                titleBar.ButtonPressedForegroundColor = Microsoft.UI.Colors.White;
                titleBar.ButtonInactiveBackgroundColor = Microsoft.UI.Colors.Transparent;
                titleBar.ButtonInactiveForegroundColor = Microsoft.UI.ColorHelper.FromArgb(0xFF, 0x66, 0x66, 0x66);
            }
        }

        /// <summary>
        /// 设置标题栏拖拽功能
        /// </summary>
        private void SetupTitleBarDrag()
        {
            if (_appWindow == null) return;
            
            _window.SizeChanged += (s, e) => UpdateTitleBarDragRectangles();
            TitleBarGrid.SizeChanged += (s, e) => UpdateTitleBarDragRectangles();
            TitleBarGrid.Loaded += (s, e) => UpdateTitleBarDragRectangles();
        }

        /// <summary>
        /// 更新标题栏拖拽区域
        /// </summary>
        private void UpdateTitleBarDragRectangles()
        {
            if (_appWindow == null || TitleBarGrid == null) return;

            var scale = _window.Content.XamlRoot.RasterizationScale;
            var titleBarHeight = TitleBarGrid.ActualHeight;

            var leftButtonsWidth = GetLeftButtonsWidth();
            var rightAreaWidth = GetRightNonDragWidth();

            var dragRects = new System.Collections.Generic.List<Windows.Graphics.RectInt32>();

            var centerStart = (int)(leftButtonsWidth * scale);
            var centerEnd = (int)((TitleBarGrid.ActualWidth - rightAreaWidth) * scale);
            if (centerEnd > centerStart)
            {
                var centerRect = new Windows.Graphics.RectInt32(centerStart, 0, centerEnd - centerStart, (int)(titleBarHeight * scale));
                dragRects.Add(centerRect);
            }

            _appWindow.TitleBar.SetDragRectangles(dragRects.ToArray());
        }

        private double GetLeftButtonsWidth()
        {
            double width = 0;
            if (TitleBarButtonsPanel != null)
            {
                var transform = TitleBarButtonsPanel.TransformToVisual(TitleBarGrid);
                var bounds = transform.TransformBounds(new Windows.Foundation.Rect(0, 0, TitleBarButtonsPanel.ActualWidth, TitleBarButtonsPanel.ActualHeight));
                width = bounds.Right + 8;
            }
            return width;
        }

        private double GetRightNonDragWidth()
        {
            return 180;
        }

        #endregion

        #region 抽屉管理

        /// <summary>
        /// 切换抽屉显示状态（带动画）
        /// </summary>
        private void ToggleDrawer(Border drawerToShow)
        {
            if (drawerToShow == null)
            {
                if (!_isDrawerBarLocked)
                {
                    CloseAllDrawers();
                }
                return;
            }

            // 锁定状态下的逻辑
            if (_isDrawerBarLocked)
            {
                // 如果点击的是当前已打开的抽屉，不做任何操作
                if (_currentDrawer == drawerToShow)
                {
                    return;
                }

                // 锁定状态下：关闭当前抽屉，打开目标抽屉
                if (_currentDrawer != null && _currentDrawer.Visibility == Visibility.Visible)
                {
                    AnimateDrawerClose(_currentDrawer);
                }

                drawerToShow.Visibility = Visibility.Visible;
                AnimateDrawerOpen(drawerToShow);
                _currentDrawer = drawerToShow;
                return;
            }

            // 未锁定状态下的原有逻辑
            // 如果点击的是当前已打开的抽屉，则关闭它
            if (_currentDrawer == drawerToShow && drawerToShow.Visibility == Visibility.Visible)
            {
                AnimateDrawerClose(drawerToShow);
                _currentDrawer = null;
                return;
            }

            // 关闭当前抽屉（带动画）
            if (_currentDrawer != null && _currentDrawer.Visibility == Visibility.Visible)
            {
                AnimateDrawerClose(_currentDrawer);
            }

            // 显示指定的抽屉（带动画）
            if (drawerToShow != null)
            {
                drawerToShow.Visibility = Visibility.Visible;
                AnimateDrawerOpen(drawerToShow);
                _currentDrawer = drawerToShow;
            }
            else
            {
                _currentDrawer = null;
            }
        }

        /// <summary>
        /// 打开抽屉动画
        /// </summary>
        private void AnimateDrawerOpen(Border drawer)
        {
            if (drawer == null) return;
            
            var transform = drawer.RenderTransform as TranslateTransform;
            if (transform == null) return;
            
            // 判断是顶部抽屉还是右侧抽屉
            bool isRightDrawer = drawer.Name == "FileInfoDrawer";
            string property = isRightDrawer ? "X" : "Y";
            double fromValue = isRightDrawer ? 50 : -20;
            
            // 滑动动画
            var slideAnimation = new DoubleAnimation
            {
                From = fromValue,
                To = 0,
                Duration = new Duration(TimeSpan.FromMilliseconds(200)),
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            };
            
            // 淡入动画
            var fadeAnimation = new DoubleAnimation
            {
                From = 0,
                To = 1,
                Duration = new Duration(TimeSpan.FromMilliseconds(150))
            };
            
            Storyboard.SetTarget(slideAnimation, transform);
            Storyboard.SetTargetProperty(slideAnimation, property);
            Storyboard.SetTarget(fadeAnimation, drawer);
            Storyboard.SetTargetProperty(fadeAnimation, "Opacity");
            
            var storyboard = new Storyboard();
            storyboard.Children.Add(slideAnimation);
            storyboard.Children.Add(fadeAnimation);
            storyboard.Begin();
        }

        /// <summary>
        /// 关闭抽屉动画
        /// </summary>
        private void AnimateDrawerClose(Border drawer)
        {
            if (drawer == null || drawer.Visibility != Visibility.Visible) return;
            
            var transform = drawer.RenderTransform as TranslateTransform;
            if (transform == null) return;
            
            // 判断是顶部抽屉还是右侧抽屉
            bool isRightDrawer = drawer.Name == "FileInfoDrawer";
            string property = isRightDrawer ? "X" : "Y";
            double toValue = isRightDrawer ? 50 : -20;
            
            // 滑动动画
            var slideAnimation = new DoubleAnimation
            {
                From = 0,
                To = toValue,
                Duration = new Duration(TimeSpan.FromMilliseconds(200)),
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn }
            };
            
            // 淡出动画
            var fadeAnimation = new DoubleAnimation
            {
                From = 1,
                To = 0,
                Duration = new Duration(TimeSpan.FromMilliseconds(150))
            };
            
            Storyboard.SetTarget(slideAnimation, transform);
            Storyboard.SetTargetProperty(slideAnimation, property);
            Storyboard.SetTarget(fadeAnimation, drawer);
            Storyboard.SetTargetProperty(fadeAnimation, "Opacity");
            
            var storyboard = new Storyboard();
            storyboard.Children.Add(slideAnimation);
            storyboard.Children.Add(fadeAnimation);
            storyboard.Completed += (s, e) =>
            {
                drawer.Visibility = Visibility.Collapsed;
            };
            storyboard.Begin();
        }

        /// <summary>
        /// 关闭所有抽屉（锁定状态下不关闭）
        /// </summary>
        private void CloseAllDrawers()
        {
            if (_isDrawerBarLocked)
                return;

            AnimateDrawerClose(PathDrawer);
            AnimateDrawerClose(ExportDrawer);
            AnimateDrawerClose(DeleteDrawer);
            AnimateDrawerClose(FilterDrawer);
            _currentDrawer = null;
        }

        /// <summary>
        /// 切换抽屉栏锁定状态
        /// </summary>
        private void ToggleDrawerLock(Border drawer)
        {
            _isDrawerBarLocked = !_isDrawerBarLocked;
            UpdateAllPinButtons();
        }

        /// <summary>
        /// 更新所有固定按钮的视觉状态
        /// </summary>
        private void UpdateAllPinButtons()
        {
            var iconFills = new[]
            {
                PinPathDrawerIconFill,
                PinExportDrawerIconFill,
                PinDeleteDrawerIconFill,
                PinFilterDrawerIconFill
            };

            var pinButtons = new[]
            {
                PinPathDrawerButton,
                PinExportDrawerButton,
                PinDeleteDrawerButton,
                PinFilterDrawerButton
            };

            foreach (var iconFill in iconFills)
            {
                if (iconFill != null)
                {
                    iconFill.Visibility = _isDrawerBarLocked ? Visibility.Visible : Visibility.Collapsed;
                }
            }

            foreach (var pinButton in pinButtons)
            {
                if (pinButton != null)
                {
                    ToolTipService.SetToolTip(pinButton, _isDrawerBarLocked ? "取消固定" : "固定");
                }
            }
        }

        /// <summary>
        /// 切换右侧文件信息抽屉（带动画）
        /// </summary>
        private void ToggleFileInfoDrawer()
        {
            if (FileInfoDrawer.Visibility == Visibility.Visible)
            {
                AnimateDrawerClose(FileInfoDrawer);
                Task.Delay(200).ContinueWith(_ =>
                {
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        FileInfoDrawer.Visibility = Visibility.Collapsed;
                        PreviewContainer.Margin = new Thickness(0);
                        ThumbnailScrollViewer.Margin = new Thickness(0);
                    });
                });
            }
            else
            {
                FileInfoDrawer.Visibility = Visibility.Visible;
                AnimateDrawerOpen(FileInfoDrawer);
                PreviewContainer.Margin = new Thickness(0, 0, 320, 0);
                ThumbnailScrollViewer.Margin = new Thickness(0, 0, 320, 0);
            }
        }
        
        private void CloseFileInfoButton_Click(object sender, RoutedEventArgs e)
        {
            AnimateDrawerClose(FileInfoDrawer);
            Task.Delay(200).ContinueWith(_ =>
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    FileInfoDrawer.Visibility = Visibility.Collapsed;
                    PreviewContainer.Margin = new Thickness(0);
                    ThumbnailScrollViewer.Margin = new Thickness(0);
                });
            });
        }

        /// <summary>
        /// 页面点击事件 - 点击空白区域关闭抽屉
        /// </summary>
        private void Page_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            // 锁定状态下不关闭抽屉
            if (_isDrawerBarLocked)
                return;

            // 获取点击位置
            var point = e.GetCurrentPoint(this).Position;
            
            // 检查点击是否在抽屉区域外
            bool clickedOutsideDrawers = true;
            
            // 检查顶部抽屉
            if (_currentDrawer != null && _currentDrawer.Visibility == Visibility.Visible)
            {
                var drawerBounds = GetElementBounds(_currentDrawer);
                if (drawerBounds.Contains(point))
                {
                    clickedOutsideDrawers = false;
                }
            }
            
            // 文件信息抽屉不参与点击空白关闭逻辑
            
            // 检查标题栏按钮区域（点击按钮时不关闭抽屉）
            var titleBarBounds = GetElementBounds(TitleBarButtonsPanel);
            if (titleBarBounds.Contains(point))
            {
                clickedOutsideDrawers = false;
            }
            
            // 如果点击在抽屉外，关闭顶部抽屉（不包括文件信息抽屉）
            if (clickedOutsideDrawers)
            {
                CloseAllDrawers();
            }
        }

        /// <summary>
        /// 获取元素在页面中的边界
        /// </summary>
        private Windows.Foundation.Rect GetElementBounds(FrameworkElement element)
        {
            if (element == null) return new Windows.Foundation.Rect(0, 0, 0, 0);
            
            var transform = element.TransformToVisual(this);
            var position = transform.TransformPoint(new Windows.Foundation.Point(0, 0));
            return new Windows.Foundation.Rect(position.X, position.Y, element.ActualWidth, element.ActualHeight);
        }

        #endregion

        #region 标题栏按钮事件

        private void PathButton_Click(object sender, RoutedEventArgs e)
        {
            ToggleDrawer(PathDrawer);
        }

        private void SettingsService_FolderNameChanged(object? sender, Services.SettingsService.FolderNameChangedEventArgs e)
        {
            if (e.JpgFolderName != null)
            {
                JpgFolderTextBox.Text = e.JpgFolderName;
            }
            if (e.RawFolderName != null)
            {
                RawFolderTextBox.Text = e.RawFolderName;
            }
        }

        private void ExportButton_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(JpgFolderTextBox.Text))
                JpgFolderTextBox.Text = _settingsService.JpgFolderName;
            if (string.IsNullOrEmpty(RawFolderTextBox.Text))
                RawFolderTextBox.Text = _settingsService.RawFolderName;
            ToggleDrawer(ExportDrawer);
        }

        private void DeleteButton_Click(object sender, RoutedEventArgs e)
        {
            bool wasVisible = DeleteDrawer.Visibility == Visibility.Visible;
            ToggleDrawer(DeleteDrawer);
            
            // 折叠删除抽屉时清除预删除筛选
            if (wasVisible && DeleteDrawer.Visibility == Visibility.Collapsed)
            {
                _viewModel?.ClearDeletedFilter();
                UpdateFilterDeletedButtonState();
            }
        }

        private void FilterButton_Click(object sender, RoutedEventArgs e)
        {
            ToggleDrawer(FilterDrawer);
        }

        private void SettingsButton_Click(object sender, RoutedEventArgs e)
        {
            var slideTransition = new Microsoft.UI.Xaml.Media.Animation.SlideNavigationTransitionInfo
            {
                Effect = Microsoft.UI.Xaml.Media.Animation.SlideNavigationTransitionEffect.FromRight
            };
            this.Frame.Navigate(typeof(SettingsPage), null, slideTransition);
        }

        private void PinPathDrawerButton_Click(object sender, RoutedEventArgs e)
        {
            ToggleDrawerLock(PathDrawer);
        }

        private void PinExportDrawerButton_Click(object sender, RoutedEventArgs e)
        {
            ToggleDrawerLock(ExportDrawer);
        }

        private void PinDeleteDrawerButton_Click(object sender, RoutedEventArgs e)
        {
            ToggleDrawerLock(DeleteDrawer);
        }

        private void PinFilterDrawerButton_Click(object sender, RoutedEventArgs e)
        {
            ToggleDrawerLock(FilterDrawer);
        }

        #endregion

        #region 窗口控制按钮事件

        private void MinimizeButton_Click(object sender, RoutedEventArgs e)
        {
            if (_appWindow != null)
            {
                var presenter = _appWindow.Presenter as OverlappedPresenter;
                presenter?.Minimize();
            }
        }

        private void MaximizeButton_Click(object sender, RoutedEventArgs e)
        {
            if (_appWindow != null)
            {
                var presenter = _appWindow.Presenter as OverlappedPresenter;
                if (presenter != null)
                {
                    if (presenter.State == OverlappedPresenterState.Maximized)
                    {
                        presenter.Restore();
                    }
                    else
                    {
                        presenter.Maximize();
                    }
                }
            }
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            _window?.Close();
        }

        #endregion

        #region 路径抽屉事件

        private async void SelectPath1Button_Click(object sender, RoutedEventArgs e)
        {
            if (_isPath1PickerOpen) return;
            _isPath1PickerOpen = true;

            try
            {
                var folderPicker = new Windows.Storage.Pickers.FolderPicker();
                folderPicker.SettingsIdentifier = "Path1Picker";
                folderPicker.SuggestedStartLocation = GetSuggestedStartLocation(Path1TextBox.Text);
                folderPicker.FileTypeFilter.Add("*");

                WinRT.Interop.InitializeWithWindow.Initialize(folderPicker, 
                    WinRT.Interop.WindowNative.GetWindowHandle(_window));

                var folder = await folderPicker.PickSingleFolderAsync();
                if (folder != null)
                {
                    Path1TextBox.Text = folder.Path;
                    _viewModel.Path1 = folder.Path;
                    _settingsService.Path1 = folder.Path;
                }
            }
            finally
            {
                _isPath1PickerOpen = false;
            }
        }

        private async void SelectPath2Button_Click(object sender, RoutedEventArgs e)
        {
            if (_isPath2PickerOpen) return;
            _isPath2PickerOpen = true;

            try
            {
                var folderPicker = new Windows.Storage.Pickers.FolderPicker();
                folderPicker.SettingsIdentifier = "Path2Picker";
                folderPicker.SuggestedStartLocation = GetSuggestedStartLocation(Path2TextBox.Text);
                folderPicker.FileTypeFilter.Add("*");

                WinRT.Interop.InitializeWithWindow.Initialize(folderPicker,
                    WinRT.Interop.WindowNative.GetWindowHandle(_window));

                var folder = await folderPicker.PickSingleFolderAsync();
                if (folder != null)
                {
                    Path2TextBox.Text = folder.Path;
                    _viewModel.Path2 = folder.Path;
                    _settingsService.Path2 = folder.Path;
                }
            }
            finally
            {
                _isPath2PickerOpen = false;
            }
        }

        private async void LoadButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isLoadingPhotos) return;
            _isLoadingPhotos = true;

            try
            {
                _viewModel.Path1 = Path1TextBox.Text;
                _viewModel.Path2 = Path2TextBox.Text;
                
                await _viewModel.LoadPhotosAsync(null);
            }
            finally
            {
                _isLoadingPhotos = false;
            }
        }

        #region 路径预览

        private void Path1TextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            _path1PreviewTimer?.Stop();
            _path1PreviewTimer?.Start();
        }

        private void Path2TextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            _path2PreviewTimer?.Stop();
            _path2PreviewTimer?.Start();
        }

        private async void Path1PreviewTimer_Tick(object? sender, object e)
        {
            _path1PreviewTimer?.Stop();
            await ScanPath1Async();
        }

        private async void Path2PreviewTimer_Tick(object? sender, object e)
        {
            _path2PreviewTimer?.Stop();
            await ScanPath2Async();
        }

        private async Task ScanPath1Async()
        {
            var path = Path1TextBox.Text;

            if (string.IsNullOrWhiteSpace(path))
            {
                Path1PreviewPanel.Visibility = Visibility.Collapsed;
                return;
            }

            _path1PreviewCts?.Cancel();
            _path1PreviewCts = new CancellationTokenSource();

            Path1PreviewPanel.Visibility = Visibility.Visible;
            Path1ScanRing.IsActive = true;
            Path1PreviewText.Text = "正在扫描...";

            try
            {
                var result = await _pathPreviewService.ScanPathAsync(path, _path1PreviewCts.Token);
                UpdatePathPreviewUI(Path1PreviewPanel, Path1ScanRing, Path1PreviewText, result);
            }
            catch (OperationCanceledException)
            {
            }
        }

        private async Task ScanPath2Async()
        {
            var path = Path2TextBox.Text;

            if (string.IsNullOrWhiteSpace(path))
            {
                Path2PreviewPanel.Visibility = Visibility.Collapsed;
                return;
            }

            _path2PreviewCts?.Cancel();
            _path2PreviewCts = new CancellationTokenSource();

            Path2PreviewPanel.Visibility = Visibility.Visible;
            Path2ScanRing.IsActive = true;
            Path2PreviewText.Text = "正在扫描...";

            try
            {
                var result = await _pathPreviewService.ScanPathAsync(path, _path2PreviewCts.Token);
                UpdatePathPreviewUI(Path2PreviewPanel, Path2ScanRing, Path2PreviewText, result);
            }
            catch (OperationCanceledException)
            {
            }
        }

        private void UpdatePathPreviewUI(StackPanel panel, ProgressRing ring, TextBlock text, PathPreviewResult result)
        {
            ring.IsActive = false;

            if (!result.IsValid)
            {
                if (!string.IsNullOrEmpty(result.ErrorMessage))
                {
                    text.Text = $"⚠ {result.ErrorMessage}";
                    text.Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["WarningBrush"];
                }
                else
                {
                    panel.Visibility = Visibility.Collapsed;
                }
                return;
            }

            text.Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextSecondaryBrush"];

            if (result.JpgCount == 0 && result.RawCount == 0)
            {
                text.Text = "⚠ 未找到图片";
                text.Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["WarningBrush"];
            }
            else
            {
                var parts = new List<string>();
                if (result.JpgCount > 0) parts.Add($"{result.JpgCount} JPG");
                if (result.RawCount > 0) parts.Add($"{result.RawCount} RAW");
                if (result.MatchedPairs > 0) parts.Add($"{result.MatchedPairs} 对匹配");

                text.Text = "✓ " + string.Join(", ", parts);
                text.Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["AccentBrush"];
            }
        }

        #endregion

        #endregion

        #region 导出抽屉事件

        private async void BrowseExportPathButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isExportPathPickerOpen) return;
            _isExportPathPickerOpen = true;

            try
            {
                var folderPicker = new Windows.Storage.Pickers.FolderPicker();
                folderPicker.SettingsIdentifier = "ExportPathPicker";
                folderPicker.SuggestedStartLocation = GetSuggestedStartLocation(ExportPathTextBox.Text);
                folderPicker.FileTypeFilter.Add("*");

                WinRT.Interop.InitializeWithWindow.Initialize(folderPicker,
                    WinRT.Interop.WindowNative.GetWindowHandle(_window));

                var folder = await folderPicker.PickSingleFolderAsync();
                if (folder != null)
                {
                    ExportPathTextBox.Text = folder.Path;
                    _settingsService.ExportPath = folder.Path;
                }
            }
            finally
            {
                _isExportPathPickerOpen = false;
            }
        }

        private async void ExecuteExportButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isExporting) return;
            
            if (_viewModel.PhotoItems.Count == 0)
            {
                ShowToast("没有可导出的图片");
                return;
            }

            var exportPath = ExportPathTextBox.Text;
            if (string.IsNullOrEmpty(exportPath))
            {
                ShowToast("请选择导出路径");
                return;
            }

            _isExporting = true;

            try
            {
                var ratingIndex = ExportRatingComboBox.SelectedIndex;
                var minRatingForRaw = 5 - ratingIndex;

                var options = new Services.FileOperationService.ExportOptions
                {
                    ExportOption = Models.ExportOptionEnum.Both,
                    JpgFolderName = JpgFolderTextBox.Text ?? _settingsService.JpgFolderName,
                    RawFolderName = RawFolderTextBox.Text ?? _settingsService.RawFolderName,
                    MinRating = minRatingForRaw
                };

                _viewModel.IsLoading = true;
                _viewModel.LoadingMessage = "正在导出图片...";

                var (jpgCount, rawCount) = await Services.FileOperationService.ExportPhotosAsync(
                    _viewModel.PhotoItems.ToList(),
                    exportPath,
                    options,
                    null,
                    default
                );

                _viewModel.IsLoading = false;
                ShowToast($"导出完成：JPG {jpgCount} 张，RAW {rawCount} 张");
            }
            catch (Exception ex)
            {
                _viewModel.IsLoading = false;
                ShowToast($"导出失败：{ex.Message}");

            }
            finally
            {
                _isExporting = false;
            }
        }

        #endregion

        #region 删除抽屉事件

        private DeleteOptionEnum _deleteMode = DeleteOptionEnum.Both;

        private void FilterDeletedButton_Click(object sender, RoutedEventArgs e)
        {
            if (_viewModel == null) return;
            
            _viewModel.IsFilteringDeleted = !_viewModel.IsFilteringDeleted;
            UpdateFilterDeletedButtonState();
            UpdateFilterButtonState();
        }

        private void DeleteModeButton_Click(object sender, RoutedEventArgs e)
        {
            var button = sender as Button;
            var mode = button.Tag.ToString();
            
            _deleteMode = mode switch
            {
                "Both" => DeleteOptionEnum.Both,
                "JpgOnly" => DeleteOptionEnum.JpgOnly,
                "RawOnly" => DeleteOptionEnum.RawOnly,
                _ => DeleteOptionEnum.Both
            };
            
            UpdateDeleteModeButtonStyles();
        }

        private void UpdateFilterDeletedButtonState()
        {
            if (_viewModel == null) return;
            
            if (_viewModel.IsFilteringDeleted)
            {
                FilterDeletedButton.Style = Application.Current.Resources["TitleBarButtonHighlightStyle"] as Style;
            }
            else
            {
                FilterDeletedButton.Style = Application.Current.Resources["TitleBarButtonStyle"] as Style;
            }
        }

        private void UpdateDeleteModeButtonStyles()
        {
            var buttons = new[] { DeleteBothButton, DeleteJpgOnlyButton, DeleteRawOnlyButton };
            var modes = new[] { DeleteOptionEnum.Both, DeleteOptionEnum.JpgOnly, DeleteOptionEnum.RawOnly };
            
            for (int i = 0; i < buttons.Length; i++)
            {
                if (_deleteMode == modes[i])
                {
                    buttons[i].Style = Application.Current.Resources["TitleBarButtonHighlightStyle"] as Style;
                }
                else
                {
                    buttons[i].Style = Application.Current.Resources["TitleBarButtonStyle"] as Style;
                }
            }
        }

        private async void ExecuteDeleteButton_Click(object sender, RoutedEventArgs e)
        {
            if (_viewModel.MarkedForDeletionCount == 0)
            {
                ShowToast("没有标记删除的照片");
                return;
            }

            var deleteOption = _deleteMode;

            var optionText = deleteOption switch
            {
                DeleteOptionEnum.Both => "JPG + RAW",
                DeleteOptionEnum.JpgOnly => "仅 JPG",
                DeleteOptionEnum.RawOnly => "仅 RAW",
                _ => ""
            };

            var confirmDialog = new Microsoft.UI.Xaml.Controls.ContentDialog
            {
                Title = "确认删除",
                Content = $"确定要删除 {_viewModel.MarkedForDeletionCount} 张照片吗？\n\n删除选项: {optionText}\n\n文件将移至回收站。",
                PrimaryButtonText = "删除",
                CloseButtonText = "取消",
                DefaultButton = Microsoft.UI.Xaml.Controls.ContentDialogButton.Close,
                XamlRoot = this.XamlRoot
            };

            var result = await confirmDialog.ShowAsync();

            if (result == Microsoft.UI.Xaml.Controls.ContentDialogResult.Primary)
            {
                var deletedCount = _viewModel.MarkedForDeletionCount;
                await _viewModel.ExecuteDeletionAsync(deleteOption);
                ShowToast($"已删除 {deletedCount} 张照片");
            }
        }

        #endregion

        #region 筛选抽屉事件

        private void FilterFileType_Click(object sender, RoutedEventArgs e)
        {
            if (_viewModel == null) return;
            
            var button = sender as Button;
            var fileType = (FileTypeFilter)Enum.Parse(typeof(FileTypeFilter), button.Tag.ToString());
            _viewModel.SetFileTypeFilter(fileType);
            _viewModel.DebugFilter();
            UpdateFileTypeButtonStyles();
            UpdateFilterButtonState();
        }

        private void FilterRatingConditionComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_viewModel == null || FilterRatingConditionComboBox == null) return;
            
            var condition = (RatingFilterCondition)FilterRatingConditionComboBox.SelectedIndex;
            
            // 仅在选中等于、小于等于、大于等于时启用 RatingControl
            bool needRatingValue = condition == RatingFilterCondition.Equals ||
                                   condition == RatingFilterCondition.LessOrEqual ||
                                   condition == RatingFilterCondition.GreaterOrEqual;
            
            FilterRatingControl.IsEnabled = needRatingValue;
            
            // 如果切换到不需要星级的条件，清除星级值
            if (!needRatingValue)
            {
                FilterRatingControl.Rating = 0;
                _viewModel.SetRatingValue(0);
            }
            
            _viewModel.SetRatingCondition(condition);
            UpdateFilterButtonState();
        }

        private void FilterRatingControl_RatingChanged(object sender, int rating)
        {
            if (_viewModel == null) return;
            
            _viewModel.SetRatingValue(rating);
            UpdateFilterButtonState();
        }

        private void ClearFilterButton_Click(object sender, RoutedEventArgs e)
        {
            ClearAllFilters();
        }

        private void FilterClearButton_Click(object sender, RoutedEventArgs e)
        {
            ClearAllFilters();
        }

        private void ClearAllFilters()
        {
            _viewModel?.ClearFilter();
            ResetFilterUI();
            UpdateFilterButtonState();
        }

        private void UpdateFileTypeButtonStyles()
        {
            if (_viewModel == null) return;
            
            var buttons = new[] { FilterAllButton, FilterBothButton, FilterJpgButton, FilterRawButton };
            var types = new[] { FileTypeFilter.All, FileTypeFilter.Both, FileTypeFilter.JpgOnly, FileTypeFilter.RawOnly };
            
            for (int i = 0; i < buttons.Length; i++)
            {
                if (_viewModel.FilterState.FileType == types[i])
                {
                    buttons[i].Style = Application.Current.Resources["TitleBarButtonHighlightStyle"] as Style;
                }
                else
                {
                    buttons[i].Style = Application.Current.Resources["TitleBarButtonStyle"] as Style;
                }
            }
        }

        private void UpdateFilterButtonState()
        {
            if (_viewModel == null) return;
            
            if (_viewModel.HasActiveFilter)
            {
                FilterButton.Style = Application.Current.Resources["IconButtonHighlightStyle"] as Style;
                FilterClearButton.Visibility = Visibility.Visible;
            }
            else
            {
                FilterButton.Style = Application.Current.Resources["IconButtonStyle"] as Style;
                FilterClearButton.Visibility = Visibility.Collapsed;
            }
        }

        private void ResetFilterUI()
        {
            FilterRatingConditionComboBox.SelectedIndex = 0;
            FilterRatingControl.Rating = 0;
            FilterRatingControl.IsEnabled = false;
            _viewModel?.ClearDeletedFilter();
            UpdateFileTypeButtonStyles();
            UpdateFilterDeletedButtonState();
        }

        #endregion

        #region 底部工具栏事件

        private void FileInfoButton_Click(object sender, RoutedEventArgs e)
        {
            ToggleFileInfoDrawer();
        }

        #region 文件路径操作

        private void JpgPathBorder_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            var item = _viewModel?.CurrentPreviewItem;
            if (item?.HasJpg == true)
            {
                OpenInExplorer(item.JpgPath);
            }
        }

        private void RawPathBorder_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            var item = _viewModel?.CurrentPreviewItem;
            if (item?.HasRaw == true)
            {
                OpenInExplorer(item.RawPath);
            }
        }

        private void CopyJpgPathButton_Click(object sender, RoutedEventArgs e)
        {
            var item = _viewModel?.CurrentPreviewItem;
            if (item?.HasJpg == true)
            {
                CopyToClipboard(item.JpgPath);
                ShowToast("JPG 路径已复制");
            }
        }

        private void CopyRawPathButton_Click(object sender, RoutedEventArgs e)
        {
            var item = _viewModel?.CurrentPreviewItem;
            if (item?.HasRaw == true)
            {
                CopyToClipboard(item.RawPath);
                ShowToast("RAW 路径已复制");
            }
        }

        private void OpenInExplorer(string? path)
        {
            if (string.IsNullOrEmpty(path) || !System.IO.File.Exists(path)) return;
            
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"/select, \"{path}\"",
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                ShowToast($"无法打开资源管理器: {ex.Message}");
            }
        }

        private void CopyToClipboard(string text)
        {
            try
            {
                var dataPackage = new Windows.ApplicationModel.DataTransfer.DataPackage();
                dataPackage.SetText(text);
                Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(dataPackage);
            }
            catch { }
        }

        #endregion

        private void FitToWindowButton_Click(object sender, RoutedEventArgs e)
        {
            ResetZoom();
        }

        private void ActualSizeButton_Click(object sender, RoutedEventArgs e)
        {
            var item = _viewModel.CurrentPreviewItem;
            if (item == null) return;
            
            // 放大到原图 100%（1:1）
            var originalScale = CalculateOriginalScale(item);
            SetZoom(originalScale);
            CheckAndLoadHighResolution();
        }

        private void ZoomComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // 如果是程序化更新，跳过缩放逻辑
            if (_isUpdatingZoomComboBox) return;
            
            if (ZoomComboBox == null || ZoomComboBox.SelectedIndex < 0) return;
            if (_viewModel == null) return;
            
            var item = _viewModel.CurrentPreviewItem;
            if (item == null || item.Width <= 0 || item.Height <= 0) return;
            
            // 原图比例值
            var originalScaleValues = new[] { 0.25, 0.5, 0.75, 1.0, 1.5, 2.0, 4.0, 8.0 };
            var index = ZoomComboBox.SelectedIndex;
            
            if (index >= 0 && index < originalScaleValues.Length)
            {
                // 获取 fitScale
                var fitScale = CalculateFitToScreenScale(item);
                if (fitScale <= 0) return;
                
                // 将原图比例转换为 Fit 比例
                // 原图比例 = _zoomScale * fitScale * dpiScale
                // 所以 _zoomScale = 原图比例 / (fitScale * dpiScale)
                var dpiScale = GetDpiScale();
                var originalScale = originalScaleValues[index];
                var fitScaleValue = originalScale / (fitScale * dpiScale);
                
                SetZoom(fitScaleValue);
                CheckAndLoadHighResolution();
            }
        }

        private void ZoomComboBox_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
        {
            // 禁止所有键盘输入，防止用户编辑
            e.Handled = true;
        }

        private void ZoomComboBox_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            // 只在点击下拉箭头时才打开下拉列表
            // 不禁止点击，因为用户需要通过下拉框选择选项
        }

        private void OpenInExplorerButton_Click(object sender, RoutedEventArgs e)
        {
            if (_viewModel.CurrentPreviewItem == null) return;
            
            var path = _viewModel.CurrentPreviewItem.DisplayPath;
            if (!string.IsNullOrEmpty(path) && System.IO.File.Exists(path))
            {
                var folderPath = System.IO.Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(folderPath))
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = folderPath,
                        UseShellExecute = true,
                        Verb = "open"
                    });
                }
            }
        }

        private void RotateLeftButton_Click(object sender, RoutedEventArgs e)
        {
            RotateLeft();
        }

        private void RotateRightButton_Click(object sender, RoutedEventArgs e)
        {
            RotateRight();
        }

        private void FlipHorizontalButton_Click(object sender, RoutedEventArgs e)
        {
            FlipHorizontal();
        }

        private void FlipVerticalButton_Click(object sender, RoutedEventArgs e)
        {
            FlipVertical();
        }

        private async void BottomRatingControl_RatingChanged(object sender, int rating)
        {
            if (_viewModel.CurrentPreviewItem != null)
            {
                await _viewModel.SetRatingAsync(_viewModel.CurrentPreviewItem, rating);
            }
        }

        #endregion

        #region 辅助方法

        /// <summary>
        /// 显示 Toast 提示（轻量级渐入渐出效果）
        /// </summary>
        private void ShowToast(string message, int durationMs = 2000)
        {
            try
            {
                if (ToastContainer == null || ToastText == null)
                {

                    return;
                }

                // 停止之前的计时器
                if (_toastTimer != null)
                {
                    _toastTimer.Stop();
                }

                // 设置消息
                ToastText.Text = message;
                ToastContainer.Visibility = Visibility.Visible;

                // 创建渐入动画
                var fadeIn = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimation
                {
                    From = 0,
                    To = 1,
                    Duration = new Duration(TimeSpan.FromMilliseconds(200))
                };
                Storyboard.SetTarget(fadeIn, ToastContainer);
                Storyboard.SetTargetProperty(fadeIn, "Opacity");
                
                var fadeInStoryboard = new Storyboard();
                fadeInStoryboard.Children.Add(fadeIn);
                fadeInStoryboard.Begin();

                // 设置自动隐藏计时器
                _toastTimer = new DispatcherTimer();
                _toastTimer.Interval = TimeSpan.FromMilliseconds(durationMs);
                _toastTimer.Tick += (s, e) =>
                {
                    _toastTimer.Stop();
                    
                    // 创建渐出动画
                    var fadeOut = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimation
                    {
                        From = 1,
                        To = 0,
                        Duration = new Duration(TimeSpan.FromMilliseconds(200))
                    };
                    Storyboard.SetTarget(fadeOut, ToastContainer);
                    Storyboard.SetTargetProperty(fadeOut, "Opacity");
                    
                    var fadeOutStoryboard = new Storyboard();
                    fadeOutStoryboard.Children.Add(fadeOut);
                    fadeOutStoryboard.Completed += (ss, ee) =>
                    {
                        ToastContainer.Visibility = Visibility.Collapsed;
                    };
                    fadeOutStoryboard.Begin();
                };
                _toastTimer.Start();
            }
            catch (Exception ex)
            {

            }
        }

        #endregion

        #region 图片缩放和拖动

        private void PreviewContainer_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
        {
            var pointer = e.GetCurrentPoint(PreviewContainer);
            var delta = pointer.Properties.MouseWheelDelta;
            
            if (delta != 0)
            {
                // 等比缩放因子（每次缩放约 10%）
                double zoomFactor = delta > 0 ? 1.1 : 0.909;
                
                var (minZoom, maxZoom) = GetZoomLimitsForOriginalScale();
                
                // 获取原图 100% 对应的 _zoomScale
                double originalScaleForFit = 1.0;
                var item = _viewModel?.CurrentPreviewItem;
                if (item != null && item.Width > 0 && item.Height > 0)
                {
                    originalScaleForFit = CalculateOriginalScale(item);
                }
                
                var oldScale = _zoomScale;
                var newScale = oldScale * zoomFactor;
                
                // 100% 吸附和停留逻辑
                
                if (_justSnappedTo100Percent)
                {
                    // 刚刚吸附到 100%，需要继续滚动才离开
                    _snapStayCounter++;
                    if (_snapStayCounter < SnapStayCount)
                    {
                        // 还没到离开的次数，保持在 100%
                        newScale = originalScaleForFit;
                    }
                    else
                    {
                        // 达到离开次数，重置状态
                        _justSnappedTo100Percent = false;
                        _snapStayCounter = 0;
                    }
                }
                else
                {
                    // 检查是否跨越 100% 阈值
                    bool wasBelow100 = oldScale < originalScaleForFit * (1.0 - SnapThreshold);
                    bool wasAbove100 = oldScale > originalScaleForFit * (1.0 + SnapThreshold);
                    bool willBeAbove100 = newScale > originalScaleForFit * (1.0 + SnapThreshold);
                    bool willBeBelow100 = newScale < originalScaleForFit * (1.0 - SnapThreshold);
                    
                    // 检查是否在 100% 附近（对于高像素图片更宽松的判断）
                    bool isNear100 = Math.Abs(oldScale - originalScaleForFit) < originalScaleForFit * SnapThreshold * 1.5;
                    
                    if ((wasBelow100 && willBeAbove100) || (wasAbove100 && willBeBelow100) || isNear100)
                    {
                        // 跨越 100% 阈值或在 100% 附近，吸附到 100%
                        newScale = originalScaleForFit;
                        _justSnappedTo100Percent = true;
                        _snapStayCounter = 0;
                    }
                }
                
                _zoomScale = Math.Min(maxZoom, Math.Max(minZoom, newScale));
                
                // 缩放时保持当前偏移比例，不跳变
                var scaleRatio = _zoomScale / oldScale;
                _translateX *= scaleRatio;
                _translateY *= scaleRatio;
                
                // 应用边界限制
                ClampTranslation();
                
                ApplyZoomTransform();
                UpdateZoomIndicator();
                CheckAndLoadHighResolution();
                e.Handled = true;
            }
        }

        private bool CanPanImage()
        {
            if (_frontImage == null || _frontImage.Source == null || PreviewContainer == null)
                return false;
            
            var (imageWidth, imageHeight) = GetRotatedImageSize();
            var containerWidth = PreviewContainer.ActualWidth;
            var containerHeight = PreviewContainer.ActualHeight;
            
            return imageWidth > containerWidth || imageHeight > containerHeight;
        }

        private bool CanPanHorizontal()
        {
            if (_frontImage == null || PreviewContainer == null) return false;
            var (imageWidth, _) = GetRotatedImageSize();
            return imageWidth > PreviewContainer.ActualWidth;
        }

        private bool CanPanVertical()
        {
            if (_frontImage == null || PreviewContainer == null) return false;
            var (_, imageHeight) = GetRotatedImageSize();
            return imageHeight > PreviewContainer.ActualHeight;
        }

        private (double width, double height) GetRotatedImageSize()
        {
            if (_frontImage == null)
                return (0, 0);
            
            var actualWidth = _frontImage.ActualWidth * _zoomScale;
            var actualHeight = _frontImage.ActualHeight * _zoomScale;
            
            // 当旋转 90 度或 270 度时，宽高交换
            if (_rotation == 90 || _rotation == 270)
            {
                return (actualHeight, actualWidth);
            }
            return (actualWidth, actualHeight);
        }

        private void PreviewContainer_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            if (CanPanImage() || _isDragging)
            {
                var pointer = e.GetCurrentPoint(PreviewContainer);
                if (pointer.Properties.IsLeftButtonPressed)
                {
                    _isDragging = true;
                    _lastDragPoint = pointer.Position;
                    PreviewContainer.CapturePointer(e.Pointer);
                    ProtectedCursor = Microsoft.UI.Input.InputSystemCursor.Create(Microsoft.UI.Input.InputSystemCursorShape.SizeAll);
                }
            }
        }

        private void PreviewContainer_PointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (_isDragging)
            {
                var pointer = e.GetCurrentPoint(PreviewContainer);
                var currentPoint = pointer.Position;
                
                var deltaX = currentPoint.X - _lastDragPoint.X;
                var deltaY = currentPoint.Y - _lastDragPoint.Y;
                
                // 检查是否允许水平拖拽
                if (CanPanHorizontal())
                {
                    _translateX += deltaX;
                }
                
                // 检查是否允许垂直拖拽
                if (CanPanVertical())
                {
                    _translateY += deltaY;
                }
                
                // 应用边界限制
                ClampTranslation();
                
                _lastDragPoint = currentPoint;
                ApplyZoomTransform();
            }
        }

        private void PreviewContainer_PointerReleased(object sender, PointerRoutedEventArgs e)
        {
            if (_isDragging)
            {
                _isDragging = false;
                PreviewContainer.ReleasePointerCapture(e.Pointer);
                ProtectedCursor = null;
            }
        }

        private void PreviewContainer_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
        {
            ResetZoom();
        }

        /// <summary>
        /// 计算并限制图片平移边界
        /// </summary>
        private void ClampTranslation()
        {
            if (_frontImage == null || _frontImage.Source == null || PreviewContainer == null) return;
            
            var containerWidth = PreviewContainer.ActualWidth;
            var containerHeight = PreviewContainer.ActualHeight;
            
            if (containerWidth <= 0 || containerHeight <= 0) return;
            
            var (imageWidth, imageHeight) = GetRotatedImageSize();
            
            // 水平方向
            if (imageWidth <= containerWidth)
            {
                // 图片窄于容器，强制居中
                _translateX = 0;
            }
            else
            {
                // 图片宽于容器，限制边界
                var maxTranslateX = imageWidth / 2 - containerWidth / 2;
                _translateX = Math.Max(-maxTranslateX, Math.Min(maxTranslateX, _translateX));
            }
            
            // 垂直方向
            if (imageHeight <= containerHeight)
            {
                // 图片矮于容器，强制居中
                _translateY = 0;
            }
            else
            {
                // 图片高于容器，限制边界
                var maxTranslateY = imageHeight / 2 - containerHeight / 2;
                _translateY = Math.Max(-maxTranslateY, Math.Min(maxTranslateY, _translateY));
            }
        }

        private void ApplyZoomTransform()
        {
            if (PreviewScaleTransform == null || PreviewRotateTransform == null || PreviewTranslateTransform == null) return;
            
            PreviewScaleTransform.ScaleX = _zoomScale * (_flipHorizontal ? -1 : 1);
            PreviewScaleTransform.ScaleY = _zoomScale * (_flipVertical ? -1 : 1);
            PreviewRotateTransform.Angle = _rotation;
            PreviewTranslateTransform.X = _translateX;
            PreviewTranslateTransform.Y = _translateY;
        }
        
        private void ApplyZoomTransformWithAnimation()
        {
            if (PreviewScaleTransform == null || PreviewRotateTransform == null || PreviewTranslateTransform == null) return;
            
            PreviewScaleTransform.ScaleX = _zoomScale * (_flipHorizontal ? -1 : 1);
            PreviewScaleTransform.ScaleY = _zoomScale * (_flipVertical ? -1 : 1);
            PreviewRotateTransform.Angle = _rotation;
            PreviewTranslateTransform.X = _translateX;
            PreviewTranslateTransform.Y = _translateY;
        }

        private void UpdateZoomIndicator()
        {
            if (ZoomIndicator == null || ZoomTextBlock == null) return;
            
            int percentage;
            
            try
            {
                var item = _viewModel.CurrentPreviewItem;
                
                if (item != null && item.Width > 0 && item.Height > 0)
                {
                    // 计算 fitScale
                    var fitScale = CalculateFitToScreenScale(item);
                    var dpiScale = GetDpiScale();
                    
                    // 输出图片相关尺寸信息
                    var previewWidth = _frontImage?.ActualWidth ?? 0;
                    var previewHeight = _frontImage?.ActualHeight ?? 0;
                    var displayWidth = item.Width * _zoomScale * fitScale * dpiScale;
                    var displayHeight = item.Height * _zoomScale * fitScale * dpiScale;
                    
                    System.Diagnostics.Debug.WriteLine($"[图片尺寸] 原图: {item.Width}x{item.Height}, 预览: {previewWidth:F0}x{previewHeight:F0}, 显示: {displayWidth:F0}x{displayHeight:F0}, DPI: {dpiScale:F2}");
                    
                    // 输出解码尺寸（如果有图片源）
                    if (_frontImage?.Source is BitmapImage bitmapImage)
                    {
                        var pixelWidth = bitmapImage.PixelWidth;
                        var pixelHeight = bitmapImage.PixelHeight;
                        System.Diagnostics.Debug.WriteLine($"[解码尺寸]: {pixelWidth}x{pixelHeight}");
                    }
                    
                    // 有图片，显示相对于原图的比例
                    // _zoomScale = 1.0 表示 Fit 屏幕
                    // 原图比例 = _zoomScale * fitScale * dpiScale
                    
                    // 安全检查，避免除以 0 或 NaN
                    if (fitScale > 0 && !double.IsNaN(fitScale) && !double.IsInfinity(fitScale))
                    {
                        var originalPercentage = (_zoomScale * fitScale * dpiScale) * 100;
                        
                        // 安全检查，确保百分比在合理范围内
                        if (!double.IsNaN(originalPercentage) && !double.IsInfinity(originalPercentage))
                        {
                            percentage = (int)Math.Round(originalPercentage);
                        }
                        else
                        {
                            // 如果计算失败，回退到原来的逻辑
                            percentage = (int)Math.Round(_zoomScale * 100);
                        }
                    }
                    else
                    {
                        // 如果 fitScale 无效，回退到原来的逻辑
                        percentage = (int)Math.Round(_zoomScale * 100);
                    }
                }
                else
                {
                    // 没有图片，回退到原来的逻辑（基于 Fit 比例）
                    percentage = (int)Math.Round(_zoomScale * 100);
                }
            }
            catch
            {
                // 发生任何异常，回退到原来的逻辑
                percentage = (int)Math.Round(_zoomScale * 100);
            }
            
            // 确保百分比在合理范围内
            percentage = Math.Max(0, Math.Min(1000, percentage));
            
            ZoomTextBlock.Text = $"{percentage}%";
            bool shouldShow = percentage != 100;
            
            if (shouldShow)
            {
                ZoomIndicator.Visibility = Visibility.Visible;
                _zoomIndicatorTimer?.Stop();
                _zoomIndicatorTimer?.Start();
            }
            else
            {
                ZoomIndicator.Visibility = Visibility.Collapsed;
                _zoomIndicatorTimer?.Stop();
            }
            
            // 更新缩放下拉框显示文本（程序化更新，不触发缩放）
            if (ZoomComboBox != null)
            {
                try
                {
                    _isUpdatingZoomComboBox = true;
                    ZoomComboBox.Text = $"{percentage}%";
                    _isUpdatingZoomComboBox = false;
                }
                catch
                {
                    // 下拉框更新失败，不影响其他功能
                }
            }
        }

        private void ZoomIndicatorTimer_Tick(object? sender, object e)
        {
            _zoomIndicatorTimer?.Stop();
            if (ZoomIndicator != null)
            {
                ZoomIndicator.Visibility = Visibility.Collapsed;
            }
        }

        private void ResetZoom()
        {
            _zoomScale = 1.0;
            _translateX = 0;
            _translateY = 0;
            _rotation = 0;
            _flipHorizontal = false;
            _flipVertical = false;
            ApplyZoomTransformWithAnimation();
            UpdateZoomIndicator();
        }

        private void SetZoom(double scale)
        {
            var (minZoom, maxZoom) = GetZoomLimitsForOriginalScale();
            _zoomScale = Math.Max(minZoom, Math.Min(maxZoom, scale));
            ApplyZoomTransformWithAnimation();
            UpdateZoomIndicator();
        }
        
        private void RotateLeft()
        {
            _rotation -= 90;
            NormalizeRotation();
            ApplyZoomTransformWithAnimation();
        }
        
        private void RotateRight()
        {
            _rotation += 90;
            NormalizeRotation();
            ApplyZoomTransformWithAnimation();
        }
        
        private void NormalizeRotation()
        {
            _rotation = _rotation % 360;
            if (_rotation < 0) _rotation += 360;
        }
        
        private void FlipHorizontal()
        {
            _flipHorizontal = !_flipHorizontal;
            ApplyZoomTransformWithAnimation();
        }
        
        private void FlipVertical()
        {
            _flipVertical = !_flipVertical;
            ApplyZoomTransformWithAnimation();
        }

        #region 缩放动画

        private void AnimateZoomTo(double targetScale)
        {
            _targetZoomScale = Math.Max(_minZoom, Math.Min(_maxZoom, targetScale));
            _startZoomScale = _zoomScale;
            _zoomAnimationProgress = 0;
            
            if (_zoomAnimationTimer == null)
            {
                _zoomAnimationTimer = new DispatcherTimer();
                _zoomAnimationTimer.Interval = TimeSpan.FromMilliseconds(ZoomAnimationInterval);
                _zoomAnimationTimer.Tick += ZoomAnimationTimer_Tick;
            }
            
            _zoomAnimationTimer.Start();
        }

        private void ZoomAnimationTimer_Tick(object? sender, object e)
        {
            _zoomAnimationProgress += ZoomAnimationInterval / ZoomAnimationDuration;
            
            if (_zoomAnimationProgress >= 1.0)
            {
                _zoomAnimationProgress = 1.0;
                _zoomAnimationTimer.Stop();
            }
            
            // 使用线性插值，平滑过渡
            _zoomScale = _startZoomScale + (_targetZoomScale - _startZoomScale) * _zoomAnimationProgress;
            
            ApplyZoomTransform();
            UpdateZoomIndicator();
        }

        #endregion

        #region 缩放计算辅助方法

        private double GetDpiScale()
        {
            if (_window?.Content?.XamlRoot != null)
            {
                return _window.Content.XamlRoot.RasterizationScale;
            }
            return 1.0;
        }

        private double CalculateFitToScreenScale(PhotoItem item)
        {
            if (item.Width <= 0 || item.Height <= 0) return 1.0;
            if (PreviewContainer == null) return 1.0;
            
            var containerWidth = PreviewContainer.ActualWidth;
            var containerHeight = PreviewContainer.ActualHeight;
            
            if (containerWidth <= 0 || containerHeight <= 0) return 1.0;
            
            var scaleX = containerWidth / item.Width;
            var scaleY = containerHeight / item.Height;
            
            return Math.Min(scaleX, scaleY);
        }

        private double CalculateOriginalScale(PhotoItem item)
        {
            // 原图 100% = 图片以原始物理像素大小显示
            // Fit 状态下 _zoomScale = 1.0 表示图片适应容器
            // 需要除以 DPI 缩放比例进行补偿，抵消系统缩放
            var fitScale = CalculateFitToScreenScale(item);
            if (fitScale <= 0) return 1.0;
            
            var dpiScale = GetDpiScale();
            return (1.0 / fitScale) / dpiScale;
        }

        private (double minZoom, double maxZoom) GetZoomLimitsForOriginalScale()
        {
            var item = _viewModel?.CurrentPreviewItem;
            if (item == null || item.Width <= 0 || item.Height <= 0)
            {
                return (_minZoom, _maxZoom);
            }
            
            var fitScale = CalculateFitToScreenScale(item);
            var dpiScale = GetDpiScale();
            
            if (fitScale <= 0)
            {
                return (_minZoom, _maxZoom);
            }
            
            // _minZoom 和 _maxZoom 是相对于原图的比例
            // 转换为相对于 Fit 状态的 _zoomScale
            // 原图比例 = _zoomScale * fitScale * dpiScale
            // 所以 _zoomScale = 原图比例 / (fitScale * dpiScale)
            var minZoomForFit = _minZoom / (fitScale * dpiScale);
            var maxZoomForFit = _maxZoom / (fitScale * dpiScale);
            
            return (minZoomForFit, maxZoomForFit);
        }

        #endregion

        #region 高分辨率预览加载

        private bool ShouldLoadHighResolution()
        {
            if (!_settingsService.EnableRawHighResDecode) return false;
            
            var item = _viewModel.CurrentPreviewItem;
            if (item == null) return false;
            if (!item.HasRaw && !item.HasJpg) return false;
            
            // 计算原图 100% 的缩放比例
            var originalScale = CalculateOriginalScale(item);
            
            // 当缩放超过原图 100% 的 50% 时加载高分辨率
            if (_zoomScale >= originalScale * 0.5) return true;
            
            if (_isLoadingHighRes && _currentHighResItem == item) return false;
            
            return true;
        }

        private void CheckAndLoadHighResolution()
        {
            if (!ShouldLoadHighResolution()) return;

            if (_highResDebounceTimer == null)
            {
                _highResDebounceTimer = new DispatcherTimer();
                _highResDebounceTimer.Interval = TimeSpan.FromMilliseconds(300);
                _highResDebounceTimer.Tick += async (s, e) =>
                {
                    _highResDebounceTimer.Stop();
                    await LoadHighResolutionAsync();
                };
            }

            _highResDebounceTimer.Stop();
            _highResDebounceTimer.Start();
        }

        private async Task LoadHighResolutionAsync()
        {
            var item = _viewModel.CurrentPreviewItem;
            if (item == null) return;
            if (!item.HasRaw && !item.HasJpg) return;
            if (!ShouldLoadHighResolution()) return;

            _highResLoadCts?.Cancel();
            _highResLoadCts?.Dispose();
            _highResLoadCts = new CancellationTokenSource();

            _isLoadingHighRes = true;
            _currentHighResItem = item;

            try
            {
                // 使用原图尺寸作为目标尺寸
                var targetWidth = (int)item.Width;
                var targetHeight = (int)item.Height;

                BitmapImage? highResBitmap = null;

                if (item.HasRaw && item.RawPath != null)
                {
                    highResBitmap = await _previewImageService.LoadRawFullResolutionAsync(
                        item.RawPath,
                        targetWidth,
                        targetHeight,
                        _highResLoadCts.Token);
                }
                else if (item.HasJpg && !string.IsNullOrEmpty(item.JpgPath))
                {
                    highResBitmap = await _previewImageService.LoadJpgFullResolutionAsync(
                        item.JpgPath,
                        targetWidth,
                        targetHeight,
                        _highResLoadCts.Token);
                }

                if (_highResLoadCts.Token.IsCancellationRequested ||
                    _viewModel.CurrentPreviewItem != item)
                {
                    return;
                }

                if (highResBitmap != null)
                {
                    // 设置到后台缓冲
                    _backImage.Source = highResBitmap;
                    
                    // 交换前后缓冲
                    SwapPreviewBuffers();
                }
            }
            catch (OperationCanceledException)
            {
                // 正常取消
            }
            finally
            {
                _isLoadingHighRes = false;
            }
        }

        private void CancelHighResolutionLoading()
        {
            _highResLoadCts?.Cancel();
            _highResDebounceTimer?.Stop();
            _isLoadingHighRes = false;
            _currentHighResItem = null;
        }

        #endregion

        #region 文件夹选择器辅助方法

        private Windows.Storage.Pickers.PickerLocationId GetSuggestedStartLocation(string currentPath)
        {
            if (string.IsNullOrEmpty(currentPath))
            {
                return Windows.Storage.Pickers.PickerLocationId.PicturesLibrary;
            }

            var lowerPath = currentPath.ToLowerInvariant();

            if (lowerPath.Contains("desktop"))
                return Windows.Storage.Pickers.PickerLocationId.Desktop;
            if (lowerPath.Contains("download"))
                return Windows.Storage.Pickers.PickerLocationId.Downloads;
            if (lowerPath.Contains("document"))
                return Windows.Storage.Pickers.PickerLocationId.DocumentsLibrary;
            if (lowerPath.Contains("picture") || lowerPath.Contains("photo"))
                return Windows.Storage.Pickers.PickerLocationId.PicturesLibrary;
            if (lowerPath.Contains("music"))
                return Windows.Storage.Pickers.PickerLocationId.MusicLibrary;
            if (lowerPath.Contains("video"))
                return Windows.Storage.Pickers.PickerLocationId.VideosLibrary;

            return Windows.Storage.Pickers.PickerLocationId.PicturesLibrary;
        }

        #endregion

        #region 路径持久化

        public void SaveCurrentPaths()
        {
            try
            {
                var path1 = Path1TextBox.Text;
                var path2 = Path2TextBox.Text;
                var exportPath = ExportPathTextBox.Text;
                

                
                _settingsService.SaveAllPaths(path1, path2, exportPath);
            }
            catch (Exception ex)
            {

            }
        }

        private void LoadSavedPaths()
        {
            if (!_settingsService.AutoLoadLastPath)
                return;

            try
            {
                var path1 = _settingsService.Path1;
                var path2 = _settingsService.Path2;
                var exportPath = _settingsService.ExportPath;
                


                if (!string.IsNullOrEmpty(path1))
                {
                    Path1TextBox.Text = path1;
                    _viewModel.Path1 = path1;
                    _path1PreviewTimer?.Start();
                }

                if (!string.IsNullOrEmpty(path2))
                {
                    Path2TextBox.Text = path2;
                    _viewModel.Path2 = path2;
                    _path2PreviewTimer?.Start();
                }

                if (!string.IsNullOrEmpty(exportPath))
                {
                    ExportPathTextBox.Text = exportPath;
                }
            }
            catch (Exception ex)
            {

            }
        }

        #endregion

        #endregion

    }
}
