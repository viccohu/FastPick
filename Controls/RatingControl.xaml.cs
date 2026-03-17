using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using System;
using System.ComponentModel;

namespace FastPick.Controls;

public sealed partial class RatingControl : UserControl
{
    private int _hoverRating;
    private TextBlock[]? _stars;
    private bool _isLoaded;
    private Models.PhotoItem? _boundItem;
    
    // 是否使用删除状态颜色（仅缩略图中使用）
    public static readonly DependencyProperty UseDeletionColorProperty =
        DependencyProperty.Register(
            nameof(UseDeletionColor),
            typeof(bool),
            typeof(RatingControl),
            new PropertyMetadata(false));

    public bool UseDeletionColor
    {
        get => (bool)GetValue(UseDeletionColorProperty);
        set => SetValue(UseDeletionColorProperty, value);
    }

    // 是否为筛选模式（不自动绑定到当前选中图片）
    public static readonly DependencyProperty IsFilterModeProperty =
        DependencyProperty.Register(
            nameof(IsFilterMode),
            typeof(bool),
            typeof(RatingControl),
            new PropertyMetadata(false));

    public bool IsFilterMode
    {
        get => (bool)GetValue(IsFilterModeProperty);
        set => SetValue(IsFilterModeProperty, value);
    }

    public static readonly DependencyProperty RatingProperty =
        DependencyProperty.Register(
            nameof(Rating),
            typeof(int),
            typeof(RatingControl),
            new PropertyMetadata(0, OnRatingChanged));

    public int Rating
    {
        get => (int)GetValue(RatingProperty);
        set => SetValue(RatingProperty, value);
    }

    public event EventHandler<int>? RatingChanged;

    public RatingControl()
    {
        this.InitializeComponent();
        Loaded += RatingControl_Loaded;
        DataContextChanged += RatingControl_DataContextChanged;
        Unloaded += RatingControl_Unloaded;
    }

    private void RatingControl_Loaded(object sender, RoutedEventArgs e)
    {
        _isLoaded = true;
        _stars = new[] { Star1, Star2, Star3, Star4, Star5 };
        UpdateStarsDisplay();
    }

    private void RatingControl_Unloaded(object sender, RoutedEventArgs e)
    {
        UnsubscribeFromItem();
    }

    private void RatingControl_DataContextChanged(FrameworkElement sender, DataContextChangedEventArgs args)
    {
        if (IsFilterMode)
        {
            return;
        }
        
        UnsubscribeFromItem();
        
        if (DataContext is Models.PhotoItem photoItem)
        {
            SubscribeToItem(photoItem);
        }
        else if (DataContext is ViewModels.MainViewModel viewModel)
        {
            viewModel.PropertyChanged += ViewModel_PropertyChanged;
            if (viewModel.CurrentPreviewItem != null)
            {
                SubscribeToItem(viewModel.CurrentPreviewItem);
            }
        }
        
        UpdateStarsDisplay();
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ViewModels.MainViewModel.CurrentPreviewItem))
        {
            UnsubscribeFromItem();
        
            var viewModel = DataContext as ViewModels.MainViewModel;
            if (viewModel?.CurrentPreviewItem != null)
            {
                SubscribeToItem(viewModel.CurrentPreviewItem);
            }
        }
    }

    private void SubscribeToItem(Models.PhotoItem item)
    {
        if (_boundItem != null)
        {
            _boundItem.PropertyChanged -= BoundItem_PropertyChanged;
        }
        _boundItem = item;
        _boundItem.PropertyChanged += BoundItem_PropertyChanged;
        Rating = item.Rating;
    }

    private void UnsubscribeFromItem()
    {
        if (_boundItem != null)
        {
            _boundItem.PropertyChanged -= BoundItem_PropertyChanged;
            _boundItem = null;
        }
        
        if (DataContext is ViewModels.MainViewModel viewModel)
        {
            viewModel.PropertyChanged -= ViewModel_PropertyChanged;
        }
    }

    private void BoundItem_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_boundItem != null)
        {
            if (e.PropertyName == nameof(Models.PhotoItem.Rating))
            {
                Rating = _boundItem.Rating;
            }
            else if (e.PropertyName == nameof(Models.PhotoItem.IsMarkedForDeletion))
            {
                UpdateStarsDisplay();
            }
        }
    }

    private static void OnRatingChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is RatingControl control && control._isLoaded)
        {
            control.UpdateStarsDisplay();
        }
    }

    private void Star_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (_stars == null) return;
        var star = sender as TextBlock;
        if (star == null) return;

        int index = Array.IndexOf(_stars, star);
        if (index >= 0)
        {
            _hoverRating = index + 1;
            UpdateStarsDisplay(_hoverRating);
        }
    }

    private void Star_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        _hoverRating = 0;
        UpdateStarsDisplay();
    }

    private void Star_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_stars == null) return;
        var star = sender as TextBlock;
        if (star == null) return;

        int index = Array.IndexOf(_stars, star);
        if (index >= 0)
        {
            int newRating = index + 1;
            
            if (Rating == newRating)
            {
                Rating = 0;
            }
            else
            {
                Rating = newRating;
            }
            
            UpdateStarsDisplay();
            RatingChanged?.Invoke(this, Rating);
        }
    }

    private void UpdateStarsDisplay(int? hoverRating = null)
    {
        if (_stars == null || !_isLoaded) return;

        int displayRating = hoverRating ?? Rating;
        
        // Segoe Fluent Icons: 空心星 e734, 实心星 e735
        const string EmptyStar = "\ue734";
        const string FilledStar = "\ue735";
        
        // 仅当 UseDeletionColor 为 true 且项目被标记为删除时，才使用删除状态颜色
        bool shouldUseDeletionColor = UseDeletionColor && (_boundItem?.IsMarkedForDeletion ?? false);
        var filledBrush = shouldUseDeletionColor 
            ? (Brush)Application.Current.Resources["DeletionTextBrush"]
            : (Brush)Application.Current.Resources["AccentBrush"];
        
        for (int i = 0; i < _stars.Length; i++)
        {
            if (_stars[i] != null)
            {
                bool isFilled = i < displayRating;
                _stars[i].Text = isFilled ? FilledStar : EmptyStar;
                _stars[i].Foreground = isFilled 
                    ? filledBrush
                    : (Brush)Application.Current.Resources["TextSecondaryBrush"];
            }
        }
    }
}
