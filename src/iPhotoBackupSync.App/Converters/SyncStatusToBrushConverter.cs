using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using iPhotoBackupSync.Core.Models;

namespace iPhotoBackupSync.App.Converters;

public sealed class SyncStatusToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not SyncStatus status) return Brushes.Transparent;

        var key = status switch
        {
            SyncStatus.Synced => "SuccessBrush",
            SyncStatus.Refreshing => "AccentBrush",
            SyncStatus.NotSynced => "WarningBrush",
            SyncStatus.Error => "ErrorBrush",
            _ => "MutedBrush"
        };

        return Application.Current.TryFindResource(key) as Brush ?? Brushes.Gray;
    }

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
