namespace iPhotoBackupSync.Core.Models;

/// <summary>
/// Cloud sync status of a file, as reported by the Windows Cloud Filter API
/// (the same mechanism iCloud for Windows, OneDrive, and Dropbox use for
/// placeholder/on-demand files).
/// </summary>
public enum SyncStatus
{
    /// <summary>Status has not been checked yet.</summary>
    Unknown,

    /// <summary>The file is currently being uploaded/downloaded/hydrated.</summary>
    Refreshing,

    /// <summary>The file is fully synced with iCloud (safe: a cloud copy exists).</summary>
    Synced,

    /// <summary>The file has local changes not yet uploaded, or is not tracked by the cloud provider.</summary>
    NotSynced,

    /// <summary>The sync status could not be determined or the provider reported an error.</summary>
    Error
}
