using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml;
using System;
using FastPick.Models;

namespace FastPick.Converters;

public class FileTypeToColorConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is FileTypeEnum fileType)
        {
            return fileType switch
            {
                FileTypeEnum.Both => Application.Current.Resources["BothBrush"] as SolidColorBrush,
                FileTypeEnum.RawOnly => Application.Current.Resources["RawBrush"] as SolidColorBrush,
                FileTypeEnum.JpgOnly => Application.Current.Resources["JpgBrush"] as SolidColorBrush,
                _ => Application.Current.Resources["JpgBrush"] as SolidColorBrush
            };
        }
        return Application.Current.Resources["JpgBrush"] as SolidColorBrush;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        throw new NotImplementedException();
    }
}
