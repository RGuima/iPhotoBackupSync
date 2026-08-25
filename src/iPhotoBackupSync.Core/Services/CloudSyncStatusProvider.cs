using System.Runtime.InteropServices;
using iPhotoBackupSync.Core.Models;

namespace iPhotoBackupSync.Core.Services;

/// <summary>
/// Reads the cloud placeholder state of a file via the Windows Cloud Filter API
/// (cldapi.dll). This is the same OS mechanism iCloud for Windows, OneDrive, and
/// Dropbox use to mark files as "cloud only", "in sync", or "locally modified".
///
/// The API only exposes a point-in-time placeholder state; it does not report a
/// live "percent complete" for an in-flight transfer. "Refreshing" below is a
/// best-effort heuristic (a placeholder that is partially hydrated and not yet
/// marked in-sync) rather than a guaranteed live indicator.
/// </summary>
public sealed class CloudSyncStatusProvider
{
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
                var state = NativeMethods.CfGetPlaceholderStateFromFindData(ref findData);
                return Interpret(findData.dwFileAttributes, state);
            }
            finally
            {
                NativeMethods.FindClose(findHandle);
            }
        }
        catch (DllNotFoundException)
        {
            // Not running on a Windows version with the Cloud Filter API (or it's missing).
            return SyncStatus.Error;
        }
        catch (EntryPointNotFoundException)
        {
            return SyncStatus.Error;
        }
        catch
        {
            return SyncStatus.Error;
        }
    }

    private const uint FILE_ATTRIBUTE_REPARSE_POINT = 0x400;

    private static SyncStatus Interpret(uint fileAttributes, CF_PLACEHOLDER_STATE state)
    {
        if (state.HasFlag(CF_PLACEHOLDER_STATE.INVALID))
        {
            return SyncStatus.Error;
        }

        if (state.HasFlag(CF_PLACEHOLDER_STATE.PLACEHOLDER))
        {
            if (state.HasFlag(CF_PLACEHOLDER_STATE.IN_SYNC))
            {
                return SyncStatus.Synced;
            }

            var partial = state.HasFlag(CF_PLACEHOLDER_STATE.PARTIAL) ||
                          state.HasFlag(CF_PLACEHOLDER_STATE.PARTIALLY_ON_DISK);
            return partial ? SyncStatus.Refreshing : SyncStatus.NotSynced;
        }

        if ((fileAttributes & FILE_ATTRIBUTE_REPARSE_POINT) != 0)
        {
            // Some other kind of reparse point (symlink, junction, ...) that isn't
            // a cloud placeholder we understand.
            return SyncStatus.Error;
        }

        // No placeholder markers at all. Cloud providers (iCloud, OneDrive, Dropbox)
        // mark a file that still needs to be uploaded with a "dirty" placeholder
        // immediately, so a plain, fully-hydrated file with no such marker inside a
        // cloud-managed library is, in practice, one that has already finished
        // uploading -- some providers drop the placeholder reparse point entirely
        // once a file is fully downloaded and confirmed in sync. Treating this case
        // as "not synced" (the previous behavior) made every already-backed-up photo
        // look unsynced, which is wrong far more often than treating it as synced is.
        return SyncStatus.Synced;
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

    [Flags]
    private enum CF_PLACEHOLDER_STATE : uint
    {
        NO_STATES = 0x0000,
        PLACEHOLDER = 0x0001,
        SYNC_ROOT = 0x0002,
        ESSENTIAL = 0x0004,
        ACTIVE = 0x0008,
        PARTIAL = 0x0010,
        PARTIALLY_ON_DISK = 0x0020,
        NO_CONTENT = 0x0040,
        IN_SYNC = 0x0080,
        INVALID = 0x0100
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

        [DllImport("cldapi.dll")]
        public static extern CF_PLACEHOLDER_STATE CfGetPlaceholderStateFromFindData(ref WIN32_FIND_DATA FindData);
    }
}
