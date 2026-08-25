namespace iPhotoBackupSync.Core.Models;

/// <summary>
/// A node in the "missing from destination" tree: either a file that exists in the
/// origin folder but not the destination, or a directory that contains such files.
/// Directories that exist identically on both sides are never materialized as nodes.
/// </summary>
public sealed class FileNode
{
    public required string Name { get; init; }

    /// <summary>Full path on disk (origin side).</summary>
    public required string FullPath { get; init; }

    /// <summary>Path relative to the origin root, used for display and as a stable key.</summary>
    public required string RelativePath { get; init; }

    public required bool IsDirectory { get; init; }

    /// <summary>True when this exact path does not exist on the destination side.
    /// False for a directory that exists on both sides but is included only because
    /// it contains missing descendants.</summary>
    public required bool IsMissing { get; init; }

    public long SizeBytes { get; set; }

    public DateTime? LastModifiedUtc { get; set; }

    /// <summary>Only meaningful for missing files.</summary>
    public SyncStatus SyncStatus { get; set; } = SyncStatus.Unknown;

    public List<FileNode> Children { get; } = new();

    /// <summary>Sum of sizes of all missing files at or below this node.</summary>
    public long MissingSizeBytes { get; set; }

    /// <summary>Count of missing files (not directories) at or below this node.</summary>
    public int MissingFileCount { get; set; }
}
