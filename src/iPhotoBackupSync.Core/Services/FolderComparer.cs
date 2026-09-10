using iPhotoBackupSync.Core.Models;

namespace iPhotoBackupSync.Core.Services;

/// <summary>
/// Compares the files directly inside an origin folder against the files directly inside
/// a destination folder and produces the list of files that exist in the origin but not
/// the destination. Matching is by <see cref="FileFingerprint"/> -- size plus name with any
/// iCloud duplicate suffix stripped -- rather than the literal name (survives iCloud
/// renaming a file, e.g. "IMG_1596.HEIC" becoming "IMG_1596(1).HEIC") or last-modified time
/// (found to drift by months or years independent of a file's actual content on a real
/// iCloud Photos library; see <see cref="FileFingerprint"/>). File contents themselves are
/// still never read/hashed, which keeps this fast on very large libraries.
///
/// Deliberately top-level only: subfolders on either side are never descended into. This
/// app always copies files straight into the destination root, and the origin (an iCloud
/// Photos folder) is flat too -- any subfolder that shows up at the destination belongs to
/// something else entirely (in practice, a separate tool that sorts this same folder's
/// contents into dated Photos/Videos/Documents subfolders after the fact). Comparing
/// against those would be wasteful (that tool's output can run into the tens of thousands
/// of files) for no benefit.
/// </summary>
public sealed class FolderComparer
{
    private readonly CloudSyncStatusProvider _syncStatusProvider = new();
    private readonly BackupManifestService _manifestService = new();

    /// <summary>Max concurrent cloud-sync-status lookups for files found to be missing.</summary>
    public int MaxDegreeOfParallelism { get; init; } = Math.Clamp(Environment.ProcessorCount * 4, 4, 16);

    public async Task<FileNode> CompareAsync(
        string originRoot,
        string destinationRoot,
        IProgress<CompareProgress>? progress,
        CancellationToken cancellationToken)
    {
        originRoot = Path.GetFullPath(originRoot);
        destinationRoot = Path.GetFullPath(destinationRoot);

        long filesScanned = 0;
        long missingFound = 0;

        void ReportProgress(ComparePhase phase, long dirsScanned, string? currentPath)
        {
            progress?.Report(new CompareProgress(phase, dirsScanned, filesScanned, missingFound, currentPath));
        }

        // Phase 1: index the destination root's top-level files by content fingerprint.
        var destinationIndex = new HashSet<FileFingerprint>();
        if (Directory.Exists(destinationRoot))
        {
            foreach (var fi in EnumerateTopLevelFiles(destinationRoot))
            {
                destinationIndex.Add(FileFingerprint.FromFileName(fi.Name, SafeLength(fi)));
            }
        }
        ReportProgress(ComparePhase.IndexingDestination, 1, destinationRoot);

        // Files recorded in a backup manifest at the destination root count as already
        // backed up even if the bytes aren't actually there -- e.g. archived elsewhere.
        foreach (var fingerprint in _manifestService.ReadManifestFingerprints(destinationRoot))
        {
            destinationIndex.Add(fingerprint);
        }

        // Phase 2: walk the origin root's top-level files only.
        cancellationToken.ThrowIfCancellationRequested();
        var rootNode = new FileNode
        {
            Name = Path.GetFileName(originRoot.TrimEnd(Path.DirectorySeparatorChar)) is { Length: > 0 } n ? n : originRoot,
            FullPath = originRoot,
            RelativePath = string.Empty,
            IsDirectory = true,
            IsMissing = !Directory.Exists(destinationRoot)
        };

        if (Directory.Exists(originRoot))
        {
            foreach (var fi in EnumerateTopLevelFiles(originRoot))
            {
                cancellationToken.ThrowIfCancellationRequested();
                filesScanned++;

                var size = SafeLength(fi);
                if (!destinationIndex.Contains(FileFingerprint.FromFileName(fi.Name, size)))
                {
                    missingFound++;
                    rootNode.Children.Add(new FileNode
                    {
                        Name = fi.Name,
                        FullPath = fi.FullName,
                        RelativePath = fi.Name,
                        IsDirectory = false,
                        IsMissing = true,
                        SizeBytes = size,
                        LastModifiedUtc = SafeLastWriteUtc(fi)
                    });
                }

                ReportProgress(ComparePhase.ScanningOrigin, 1, originRoot);
            }
        }

        rootNode.Children.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));

        // Phase 3: compute cloud sync status only for the (much smaller) set of missing files.
        long statusChecked = 0;
        await Parallel.ForEachAsync(
            rootNode.Children,
            new ParallelOptions { MaxDegreeOfParallelism = MaxDegreeOfParallelism, CancellationToken = cancellationToken },
            (node, ct) =>
            {
                node.SyncStatus = _syncStatusProvider.GetStatus(node.FullPath);
                Interlocked.Increment(ref statusChecked);
                ReportProgress(ComparePhase.CheckingSyncStatus, 1, node.FullPath);
                return ValueTask.CompletedTask;
            });

        RollUpMissingTotals(rootNode);
        ReportProgress(ComparePhase.Done, 1, null);
        return rootNode;
    }

    private static List<FileInfo> EnumerateTopLevelFiles(string dir)
    {
        // EnumerateFileSystemInfos()/GetFiles() are lazy: the actual directory listing
        // happens while iterating, not on this call, so materialize inside the try so a
        // failure partway through a listing (possible on a NAS/network share) is caught
        // here instead of propagating out.
        try
        {
            return new DirectoryInfo(dir).EnumerateFiles().ToList();
        }
        catch (UnauthorizedAccessException)
        {
            return new List<FileInfo>();
        }
        catch (IOException)
        {
            return new List<FileInfo>();
        }
    }

    private static long SafeLength(FileInfo fi)
    {
        try { return fi.Length; } catch { return 0; }
    }

    private static DateTime? SafeLastWriteUtc(FileInfo fi)
    {
        try { return fi.LastWriteTimeUtc; } catch { return null; }
    }

    private static void RollUpMissingTotals(FileNode node)
    {
        if (!node.IsDirectory)
        {
            if (node.IsMissing)
            {
                node.MissingFileCount = 1;
                node.MissingSizeBytes = node.SizeBytes;
            }
            return;
        }

        foreach (var child in node.Children)
        {
            RollUpMissingTotals(child);
            node.MissingFileCount += child.MissingFileCount;
            node.MissingSizeBytes += child.MissingSizeBytes;
        }
    }
}
