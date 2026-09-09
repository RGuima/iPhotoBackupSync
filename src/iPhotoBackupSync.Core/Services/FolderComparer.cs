using iPhotoBackupSync.Core.Models;

namespace iPhotoBackupSync.Core.Services;

/// <summary>
/// Compares the files directly inside an origin folder against the files directly inside
/// a destination folder (name only -- file contents are never read, which keeps this fast
/// even for very large photo libraries) and produces the list of files that exist in the
/// origin but not the destination.
///
/// Deliberately top-level only: subfolders on either side are never descended into. This
/// app always copies files straight into the destination root, and the origin (an iCloud
/// Photos folder) is flat too -- any subfolder that shows up at the destination belongs to
/// something else entirely (in practice, a separate tool that sorts this same folder's
/// contents into dated Photos/Videos/Documents subfolders after the fact). Comparing
/// against those would be both pointless (renamed copies never match an origin filename
/// anyway) and wasteful (that tool's output can run into the tens of thousands of files).
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

        // Phase 1: index the destination root's top-level file names.
        var destinationIndex = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (Directory.Exists(destinationRoot))
        {
            foreach (var name in EnumerateTopLevelFileNames(destinationRoot))
            {
                destinationIndex.Add(name);
            }
        }
        ReportProgress(ComparePhase.IndexingDestination, 1, destinationRoot);

        // Files recorded in a backup manifest at the destination root count as already
        // backed up even if the bytes aren't actually there -- e.g. archived elsewhere.
        // The manifest itself is top-level-only now too, so every entry here is a bare
        // file name already comparable against an origin file name directly.
        foreach (var manifestPath in _manifestService.ReadManifestPaths(destinationRoot))
        {
            destinationIndex.Add(manifestPath);
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

                if (!destinationIndex.Contains(fi.Name))
                {
                    missingFound++;
                    rootNode.Children.Add(new FileNode
                    {
                        Name = fi.Name,
                        FullPath = fi.FullName,
                        RelativePath = fi.Name,
                        IsDirectory = false,
                        IsMissing = true,
                        SizeBytes = SafeLength(fi),
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

    private static IEnumerable<string> EnumerateTopLevelFileNames(string dir) =>
        EnumerateTopLevelFiles(dir).Select(fi => fi.Name);

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
