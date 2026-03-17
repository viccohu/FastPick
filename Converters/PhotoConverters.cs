using FastPick.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;

namespace FastPick.Converters;

/// <summary>
/// 布尔到可见性转换器
/// </summary>
public class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is bool boolValue)
        {
            return boolValue ? Visibility.Visible : Visibility.Collapsed;
        }
        return Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        if (value is Visibility visibility)
        {
            return visibility == Visibility.Visible;
        }
        return false;
    }
}

/// <summary>
/// 文件类型到字符串转换器
/// </summary>
public class FileTypeToStringConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is FileTypeEnum fileType)
        {
            return fileType switch
            {
                FileTypeEnum.Both => "J+R",
                FileTypeEnum.RawOnly => "RAW",
                FileTypeEnum.JpgOnly => "JPG",
                _ => "JPG"
            };
        }
        return "JPG";
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        throw new NotImplementedException();
    }
}

/// <summary>
/// 评级到星星字符串转换器
/// </summary>
public class RatingToStarsConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is int rating)
        {
            return new string('★', rating) + new string('☆', 5 - rating);
        }
        return "☆☆☆☆☆";
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        throw new NotImplementedException();
    }
}

/// <summary>
/// 评级到可见性转换器（已弃用，保留兼容）
/// </summary>
public class RatingToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        return Visibility.Visible;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        throw new NotImplementedException();
    }
}

public class BoolToDeletionBackgroundConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is bool isMarkedForDeletion && isMarkedForDeletion)
        {
            return Application.Current.Resources["DeletionBackgroundBrush"] as SolidColorBrush 
                ?? new SolidColorBrush(Microsoft.UI.Colors.Red);
        }
        return Application.Current.Resources["SurfaceBackgroundBrush"] as SolidColorBrush 
            ?? new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(0xFF, 0x1E, 0x1E, 0x1E));
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        throw new NotImplementedException();
    }
}
