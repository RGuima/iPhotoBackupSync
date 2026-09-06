using System.Runtime.InteropServices;
using iPhotoBackupSync.Core.Models;

namespace iPhotoBackupSync.Core.Services;

/// <summary>
/// Reads the iCloud sync state of a file. This intentionally checks only the mechanism
/// iCloud for Windows itself uses, not the Windows Cloud Filter API's reparse-point
/// placeholders (used by OneDrive and some other providers) -- sampling a real
/// 52,000+ file "iCloud Photos" folder showed every file reporting
/// CF_PLACEHOLDER_STATE_NO_STATES, i.e. iCloud for Windows never creates that kind of
/// placeholder at all. Instead it marks files with the lighter-weight cloud-file
/// *attribute* bits: a file with FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS (or
/// RECALL_ON_OPEN) isn't fully present on disk yet and would have to be fetched from
/// iCloud to be opened, so it's reported as Refreshing (still downloading/queued); a
/// file with neither bit set is reported as Synced.
///
/// Because only this attribute-bit signal is checked, a file that happens to also sit
/// in a folder managed by a different cloud provider (e.g. OneDrive) is judged purely
/// on iCloud's own markers -- this app has no reason to care what OneDrive thinks of
/// that file, so its reparse-point placeholder state is never consulted.
/// </summary>
public sealed class CloudSyncStatusProvider
{
    private const uint FILE_ATTRIBUTE_RECALL_ON_OPEN = 0x40000;
    private const uint FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS = 0x400000;

    public SyncStatus GetStatus(string fullPath)
    {
        try
        {
            var findHandle = NativeMethods.FindFirstFileW(GetSearchPath(fullPath), out var findData);
            if (findHandle == NativeMethods.InvalidHandle)
            {
                // File vanished, permission denied, or path too long even with \\?\ prefix.
                return SyncStatus.Error;
            }

            try
            {
                return Interpret(findData.dwFileAttributes);
            }
            finally
            {
                NativeMethods.FindClose(findHandle);
            }
        }
        catch
        {
            return SyncStatus.Error;
        }
    }

    private static SyncStatus Interpret(uint fileAttributes)
    {
        return (fileAttributes & (FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS | FILE_ATTRIBUTE_RECALL_ON_OPEN)) != 0
            ? SyncStatus.Refreshing
            : SyncStatus.Synced;
    }

    private static string GetSearchPath(string fullPath)
    {
        // Enable long-path support for FindFirstFileW regardless of app-wide long path opt-in.
        if (fullPath.StartsWith(@"\\?\", StringComparison.Ordinal) ||
            fullPath.StartsWith(@"\\.\", StringComparison.Ordinal))
        {
            return fullPath;
        }

        if (fullPath.StartsWith(@"\\", StringComparison.Ordinal))
        {
            // UNC path: \\server\share\... -> \\?\UNC\server\share\...
            return @"\\?\UNC\" + fullPath.Substring(2);
        }

        return @"\\?\" + fullPath;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WIN32_FIND_DATA
    {
        public uint dwFileAttributes;
        public uint ftCreationTime_dwLowDateTime;
        public uint ftCreationTime_dwHighDateTime;
        public uint ftLastAccessTime_dwLowDateTime;
        public uint ftLastAccessTime_dwHighDateTime;
        public uint ftLastWriteTime_dwLowDateTime;
        public uint ftLastWriteTime_dwHighDateTime;
        public uint nFileSizeHigh;
        public uint nFileSizeLow;
        public uint dwReserved0;
        public uint dwReserved1;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string cFileName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 14)]
        public string cAlternateFileName;
    }

    private static class NativeMethods
    {
        public static readonly IntPtr InvalidHandle = new(-1);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "FindFirstFileW")]
        public static extern IntPtr FindFirstFileW(string lpFileName, out WIN32_FIND_DATA lpFindFileData);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool FindClose(IntPtr hFindFile);
    }
}
