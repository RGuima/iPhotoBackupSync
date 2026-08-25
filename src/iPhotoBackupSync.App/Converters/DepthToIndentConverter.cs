using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace iPhotoBackupSync.App.Converters;

public sealed class DepthToIndentConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var depth = value is int i ? i : 0;
        return new Thickness(depth * 18, 0, 0, 0);
    }

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
