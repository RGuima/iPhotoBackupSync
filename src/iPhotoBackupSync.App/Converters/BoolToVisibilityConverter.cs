using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace iPhotoBackupSync.App.Converters;

/// <summary>
/// Bool-to-Visibility. Pass ConverterParameter="Invert" to flip, or "Collapse" (default)
/// vs "Hidden" via a second parameter token separated by ';' e.g. "Invert;Hidden".
/// </summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var flag = value is bool b && b;
        var tokens = (parameter as string)?.Split(';') ?? Array.Empty<string>();
        if (tokens.Contains("Invert")) flag = !flag;
        var collapsedVisibility = tokens.Contains("Hidden") ? Visibility.Hidden : Visibility.Collapsed;
        return flag ? Visibility.Visible : collapsedVisibility;
    }

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
