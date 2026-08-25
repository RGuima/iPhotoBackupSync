using System.Runtime.InteropServices;
using iPhotoBackupSync.Core.Models;

namespace iPhotoBackupSync.Core.Services;

/// <summary>
/// Reads the cloud sync state of a file. Two independent OS mechanisms are checked:
///
/// 1. The Windows Cloud Filter API (cldapi.dll), used by OneDrive and some other
///    providers, which marks a file as a reparse-point-based placeholder with an
///    explicit in-sync / partially-hydrated / dirty state.
/// 2. The lighter-weight cloud-file *attribute* bits (FILE_ATTRIBUTE_PINNED /
///    UNPINNED / RECALL_ON_DATA_ACCESS / RECALL_ON_OPEN), which don't require a
///    reparse point at all. Sampling a real "iCloud Photos" folder on Windows
///    showed every single file returning CF_PLACEHOLDER_STATE_NO_STATES (i.e. not
///    a Cloud Filter placeholder at all) -- iCloud for Windows relies entirely on
///    this second, attribute-only mechanism instead. FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS
///    means the file's content is not fully present on disk and would need to be
///    fetched from the cloud to be opened, i.e. it is still in the download queue.
///
/// Neither mechanism reports a live "percent complete" for an in-flight transfer,
/// so "Refreshing" here is a best-effort "not fully available locally yet" signal
/// rather than a guaranteed indicator of an active transfer.
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
    private const uint FILE_ATTRIBUTE_RECALL_ON_OPEN = 0x40000;
    private const uint FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS = 0x400000;

    private static SyncStatus Interpret(uint fileAttributes, CF_PLACEHOLDER_STATE state)
    {
        if (state.HasFlag(CF_PLACEHOLDER_STATE.INVALID))
        {
            return SyncStatus.Error;
        }

        if (state.HasFlag(CF_PLACEHOLDER_STATE.PLACEHOLDER))
        {
            // Local content completeness takes priority over the metadata-only
            // IN_SYNC bit: a file that hasn't finished downloading is still "in
            // the queue", not "synced", regardless of whether its metadata has
            // already been confirmed to match the server.
            var notFullyOnDisk = state.HasFlag(CF_PLACEHOLDER_STATE.NO_CONTENT) ||
                                 state.HasFlag(CF_PLACEHOLDER_STATE.PARTIAL) ||
                                 state.HasFlag(CF_PLACEHOLDER_STATE.PARTIALLY_ON_DISK);
            if (notFullyOnDisk)
            {
                return SyncStatus.Refreshing;
            }

            return state.HasFlag(CF_PLACEHOLDER_STATE.IN_SYNC) ? SyncStatus.Synced : SyncStatus.NotSynced;
        }

        // Not a Cloud Filter reparse-point placeholder. iCloud for Windows marks
        // files this way instead: FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS / RECALL_ON_OPEN
        // mean the file's content isn't fully present locally and would have to be
        // fetched from the cloud to be opened -- i.e. it's still downloading / queued.
        if ((fileAttributes & (FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS | FILE_ATTRIBUTE_RECALL_ON_OPEN)) != 0)
        {
            return SyncStatus.Refreshing;
        }

        if ((fileAttributes & FILE_ATTRIBUTE_REPARSE_POINT) != 0)
        {
            // Some other kind of reparse point (symlink, junction, ...) that isn't
            // a cloud placeholder we understand.
            return SyncStatus.Error;
        }

        // No placeholder or recall markers at all. Cloud providers mark a file
        // that still needs to be uploaded, or isn't fully downloaded, immediately
        // -- so a plain file with no such marker inside a cloud-managed library
        // is, in practice, one that has already finished syncing.
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
