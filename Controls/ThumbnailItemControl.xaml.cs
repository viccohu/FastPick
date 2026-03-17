using FastPick.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace FastPick.Controls;

/// <summary>
/// 缩略图项控件
/// </summary>
public sealed partial class ThumbnailItemControl : UserControl
{
    public static readonly DependencyProperty PhotoItemProperty =
        DependencyProperty.Register(
            nameof(PhotoItem),
            typeof(PhotoItem),
            typeof(ThumbnailItemControl),
            new PropertyMetadata(null, OnPhotoItemChanged));

    public static readonly DependencyProperty ThumbnailSourceProperty =
        DependencyProperty.Register(
            nameof(ThumbnailSource),
            typeof(ImageSource),
            typeof(ThumbnailItemControl),
            new PropertyMetadata(null, OnThumbnailSourceChanged));

    public PhotoItem? PhotoItem
    {
        get => (PhotoItem?)GetValue(PhotoItemProperty);
        set => SetValue(PhotoItemProperty, value);
    }

    public ImageSource? ThumbnailSource
    {
        get => (ImageSource?)GetValue(ThumbnailSourceProperty);
        set => SetValue(ThumbnailSourceProperty, value);
    }

    public ThumbnailItemControl()
    {
        InitializeComponent();
        
        // 设置点击事件
        RootGrid.PointerPressed += OnPointerPressed;
        RootGrid.PointerEntered += OnPointerEntered;
        RootGrid.PointerExited += OnPointerExited;
    }

    private static void OnPhotoItemChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var control = (ThumbnailItemControl)d;
        control.UpdateUI();
    }

    private static void OnThumbnailSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var control = (ThumbnailItemControl)d;
        control.ThumbnailImage.Source = (ImageSource?)e.NewValue;
    }

    private void UpdateUI()
    {
        if (PhotoItem == null)
            return;

        // 文件名
        FileNameText.Text = PhotoItem.FileName;

        // 文件类型角标
        if (PhotoItem.HasJpg && PhotoItem.HasRaw)
        {
            FileTypeText.Text = "J+R";
        }
        else if (PhotoItem.HasRaw)
        {
            FileTypeText.Text = "RAW";
        }
        else
        {
            FileTypeText.Text = "JPG";
        }

        // 选中状态
        UpdateSelectionState();

        // 预删除状态
        UpdateDeleteState();

        // 评级显示
        UpdateRating();

        // 绑定属性变化事件
        PhotoItem.PropertyChanged += OnPhotoItemPropertyChanged;
    }

    private void OnPhotoItemPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(PhotoItem.IsSelected):
                UpdateSelectionState();
                break;
            case nameof(PhotoItem.IsMarkedForDeletion):
                UpdateDeleteState();
                break;
            case nameof(PhotoItem.Rating):
                UpdateRating();
                break;
        }
    }

    private void UpdateSelectionState()
    {
        if (PhotoItem?.IsSelected == true)
        {
            SelectionBorder.Visibility = Visibility.Visible;
            RootGrid.BorderBrush = (Brush)Application.Current.Resources["AccentBrush"];
        }
        else
        {
            SelectionBorder.Visibility = Visibility.Collapsed;
            RootGrid.BorderBrush = (Brush)Application.Current.Resources["BorderBrush"];
        }
    }

    private void UpdateDeleteState()
    {
        DeleteBadge.Visibility = PhotoItem?.IsMarkedForDeletion == true 
            ? Visibility.Visible 
            : Visibility.Collapsed;
    }

    private void UpdateRating()
    {
        if (PhotoItem?.Rating > 0)
        {
            RatingText.Visibility = Visibility.Visible;
            // 使用星星图标重复显示评级
            RatingText.Text = string.Join("", Enumerable.Repeat("\uE1CF", PhotoItem.Rating));
        }
        else
        {
            RatingText.Visibility = Visibility.Collapsed;
        }
    }

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        // 触发点击事件
        Clicked?.Invoke(this, PhotoItem);
    }

    private void OnPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        RootGrid.Background = (Brush)Application.Current.Resources["HoverBackgroundBrush"];
    }

    private void OnPointerExited(object sender, PointerRoutedEventArgs e)
    {
        RootGrid.Background = (Brush)Application.Current.Resources["ElevatedBackgroundBrush"];
    }

    /// <summary>
    /// 点击事件
    /// </summary>
    public event EventHandler<PhotoItem?>? Clicked;
}
