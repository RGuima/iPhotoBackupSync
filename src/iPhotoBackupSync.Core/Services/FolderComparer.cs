using System.Collections.Concurrent;
using iPhotoBackupSync.Core.Models;

namespace iPhotoBackupSync.Core.Services;

/// <summary>
/// Compares an origin folder tree against a destination folder tree by relative path
/// (name + structure only -- file contents are never read, which keeps this fast even
/// for very large photo libraries) and produces a pruned tree of everything that exists
/// in the origin but not the destination.
///
/// Designed for tens of thousands of files, possibly over a NAS: directory listings are
/// performed with bounded parallelism so network latency is hidden behind concurrency
/// instead of being paid serially per folder.
/// </summary>
public sealed class FolderComparer
{
    private readonly CloudSyncStatusProvider _syncStatusProvider = new();

    /// <summary>Max concurrent directory-listing operations. Kept modest by default so a
    /// NAS isn't hammered; local disks would tolerate a higher number too.</summary>
    public int MaxDegreeOfParallelism { get; init; } = Math.Clamp(Environment.ProcessorCount * 4, 4, 16);

    public async Task<FileNode> CompareAsync(
        string originRoot,
        string destinationRoot,
        IProgress<CompareProgress>? progress,
        CancellationToken cancellationToken)
    {
        originRoot = Path.GetFullPath(originRoot);
        destinationRoot = Path.GetFullPath(destinationRoot);

        long dirsScanned = 0;
        long filesScanned = 0;
        long missingFound = 0;
        var lastReport = DateTime.MinValue;
        var reportLock = new object();

        void ReportProgress(ComparePhase phase, string? currentPath)
        {
            if (progress is null) return;
            lock (reportLock)
            {
                var now = DateTime.UtcNow;
                if (now - lastReport < TimeSpan.FromMilliseconds(80) && phase != ComparePhase.Done) return;
                lastReport = now;
            }
            progress.Report(new CompareProgress(phase,
                Interlocked.Read(ref dirsScanned),
                Interlocked.Read(ref filesScanned),
                Interlocked.Read(ref missingFound),
                currentPath));
        }

        // Phase 1: index every relative path that exists under the destination root.
        var destinationIndex = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
        if (Directory.Exists(destinationRoot))
        {
            using var indexGate = new SemaphoreSlim(MaxDegreeOfParallelism);
            await IndexDirectoryAsync(destinationRoot, destinationRoot, destinationIndex, indexGate,
                () => { Interlocked.Increment(ref dirsScanned); ReportProgress(ComparePhase.IndexingDestination, destinationRoot); },
                cancellationToken);
        }

        // Phase 2: walk the origin tree, keeping only nodes that are missing from the
        // destination index or that contain descendants that are.
        dirsScanned = 0;
        using var scanGate = new SemaphoreSlim(MaxDegreeOfParallelism);
        var rootNode = new FileNode
        {
            Name = Path.GetFileName(originRoot.TrimEnd(Path.DirectorySeparatorChar)) is { Length: > 0 } n ? n : originRoot,
            FullPath = originRoot,
            RelativePath = string.Empty,
            IsDirectory = true,
            IsMissing = !Directory.Exists(destinationRoot)
        };

        await ScanDirectoryAsync(
            originRoot, originRoot, destinationRoot, destinationIndex, rootNode, scanGate,
            onFile: () => { Interlocked.Increment(ref filesScanned); ReportProgress(ComparePhase.ScanningOrigin, originRoot); },
            onDir: () => { Interlocked.Increment(ref dirsScanned); ReportProgress(ComparePhase.ScanningOrigin, originRoot); },
            onMissing: () => { Interlocked.Increment(ref missingFound); },
            cancellationToken);

        // Phase 3: compute cloud sync status only for the (much smaller) set of missing files.
        var missingFiles = new List<FileNode>();
        CollectMissingFiles(rootNode, missingFiles);

        long statusChecked = 0;
        await Parallel.ForEachAsync(
            missingFiles,
            new ParallelOptions { MaxDegreeOfParallelism = MaxDegreeOfParallelism, CancellationToken = cancellationToken },
            (node, ct) =>
            {
                node.SyncStatus = _syncStatusProvider.GetStatus(node.FullPath);
                Interlocked.Increment(ref statusChecked);
                ReportProgress(ComparePhase.CheckingSyncStatus, node.FullPath);
                return ValueTask.CompletedTask;
            });

        RollUpMissingTotals(rootNode);
        ReportProgress(ComparePhase.Done, null);
        return rootNode;
    }

    private static async Task IndexDirectoryAsync(
        string root,
        string currentDir,
        ConcurrentDictionary<string, byte> index,
        SemaphoreSlim gate,
        Action onDirectoryScanned,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        IEnumerable<string> entries;
        try
        {
            entries = Directory.EnumerateFileSystemEntries(currentDir);
        }
        catch (UnauthorizedAccessException)
        {
            return;
        }
        catch (IOException)
        {
            return;
        }

        onDirectoryScanned();

        var subDirTasks = new List<Task>();
        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(root, entry);
            index[relative] = 0;

            if (Directory.Exists(entry))
            {
                subDirTasks.Add(RunBoundedAsync(gate, () =>
                    IndexDirectoryAsync(root, entry, index, gate, onDirectoryScanned, cancellationToken)));
            }
        }

        await Task.WhenAll(subDirTasks);
    }

    private static async Task ScanDirectoryAsync(
        string root,
        string currentDir,
        string destinationRoot,
        ConcurrentDictionary<string, byte> destinationIndex,
        FileNode parentNode,
        SemaphoreSlim gate,
        Action onFile,
        Action onDir,
        Action onMissing,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        IEnumerable<FileSystemInfo> entries;
        try
        {
            entries = new DirectoryInfo(currentDir).EnumerateFileSystemInfos();
        }
        catch (UnauthorizedAccessException)
        {
            return;
        }
        catch (IOException)
        {
            return;
        }

        var childNodes = new ConcurrentBag<FileNode>();
        var subDirTasks = new List<Task>();

        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(root, entry.FullName);
            var existsInDestination = destinationIndex.ContainsKey(relative);
            var isDirectory = entry is DirectoryInfo;

            if (isDirectory)
            {
                onDir();
                var dirNode = new FileNode
                {
                    Name = entry.Name,
                    FullPath = entry.FullName,
                    RelativePath = relative,
                    IsDirectory = true,
                    IsMissing = !existsInDestination
                };

                if (!existsInDestination) onMissing();

                subDirTasks.Add(RunBoundedAsync(gate, async () =>
                {
                    await ScanDirectoryAsync(root, entry.FullName, destinationRoot, destinationIndex,
                        dirNode, gate, onFile, onDir, onMissing, cancellationToken);

                    // Only keep this directory in the result tree if it is itself missing
                    // or it has at least one missing descendant.
                    if (dirNode.IsMissing || dirNode.Children.Count > 0)
                    {
                        childNodes.Add(dirNode);
                    }
                }));
            }
            else
            {
                onFile();
                if (!existsInDestination)
                {
                    onMissing();
                    var fi = (FileInfo)entry;
                    childNodes.Add(new FileNode
                    {
                        Name = entry.Name,
                        FullPath = entry.FullName,
                        RelativePath = relative,
                        IsDirectory = false,
                        IsMissing = true,
                        SizeBytes = SafeLength(fi),
                        LastModifiedUtc = SafeLastWriteUtc(fi)
                    });
                }
            }
        }

        await Task.WhenAll(subDirTasks);

        foreach (var child in childNodes.OrderBy(c => !c.IsDirectory).ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase))
        {
            parentNode.Children.Add(child);
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

    private static async Task RunBoundedAsync(SemaphoreSlim gate, Func<Task> work)
    {
        await gate.WaitAsync();
        try
        {
            await work();
        }
        finally
        {
            gate.Release();
        }
    }

    private static void CollectMissingFiles(FileNode node, List<FileNode> result)
    {
        if (!node.IsDirectory && node.IsMissing)
        {
            result.Add(node);
        }

        foreach (var child in node.Children)
        {
            CollectMissingFiles(child, result);
        }
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
