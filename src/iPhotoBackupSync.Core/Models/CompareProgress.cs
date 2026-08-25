namespace iPhotoBackupSync.Core.Models;

public enum ComparePhase
{
    IndexingDestination,
    ScanningOrigin,
    CheckingSyncStatus,
    Done
}

public sealed record CompareProgress(
    ComparePhase Phase,
    long DirectoriesScanned,
    long FilesScanned,
    long MissingFilesFound,
    string? CurrentPath);
