using System.Globalization;
using System.Windows.Data;
using iPhotoBackupSync.Core.Models;

namespace iPhotoBackupSync.App.Converters;

public sealed class SyncStatusToTextConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not SyncStatus status) return string.Empty;
        return status switch
        {
            SyncStatus.Synced => "Synced with cloud",
            SyncStatus.Refreshing => "Refreshing...",
            SyncStatus.NotSynced => "Not synced yet",
            SyncStatus.Error => "Sync status unknown / error",
            _ => "Unknown"
        };
    }

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
