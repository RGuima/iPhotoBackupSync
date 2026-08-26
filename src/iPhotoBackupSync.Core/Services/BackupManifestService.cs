using System.Collections.Concurrent;
using System.Globalization;
using System.Text;

namespace iPhotoBackupSync.Core.Services;

/// <summary>
/// Manages a human-readable "backup manifest" file that can live at the root of a
/// destination folder, recording files that should be treated as already backed up
/// even though the actual bytes aren't (or are no longer) present in that folder --
/// e.g. because they were archived to offline media, a different drive, or cold
/// storage after the fact. A manifest entry is matched by relative path, the same
/// signal <see cref="FolderComparer"/> already uses to decide whether a file exists
/// in the destination (path/structure only, not content).
/// </summary>
public sealed class BackupManifestService
{
    public const string ManifestFileName = "iPhotoBackupSync.manifest.txt";

    /// <summary>Max concurrent directory-listing operations when generating a manifest.</summary>
    public int MaxDegreeOfParallelism { get; init; } = Math.Clamp(Environment.ProcessorCount * 4, 4, 16);

    /// <summary>
    /// Reads the manifest file at <paramref name="destinationRoot"/>, if one exists, and
    /// returns every relative path it records plus every ancestor folder of each path --
    /// so a folder that only exists because manifest entries live under it isn't itself
    /// reported as missing. Returns an empty set if there is no manifest.
    /// </summary>
    public IReadOnlySet<string> ReadManifestPaths(string destinationRoot)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var manifestPath = Path.Combine(destinationRoot, ManifestFileName);
        if (!File.Exists(manifestPath)) return result;

        foreach (var line in File.ReadLines(manifestPath))
        {
            if (string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith('#')) continue;

            var relativePath = line.Split('\t')[0].Trim();
            if (relativePath.Length == 0) continue;

            AddWithAncestors(result, relativePath);
        }

        return result;
    }

    private static void AddWithAncestors(HashSet<string> set, string relativePath)
    {
        set.Add(relativePath);
        var dir = Path.GetDirectoryName(relativePath);
        while (!string.IsNullOrEmpty(dir))
        {
            set.Add(dir);
            dir = Path.GetDirectoryName(dir);
        }
    }

    /// <summary>
    /// Scans every file currently in <paramref name="folderRoot"/> (recursively) and
    /// (re)writes the manifest file at its root to record all of them. Use this to mark
    /// an entire folder's existing contents as "already backed up" -- for example, a
    /// batch of files that were manually moved to an archive drive.
    /// </summary>
    public async Task<int> GenerateManifestAsync(
        string folderRoot,
        IProgress<int>? progress,
        CancellationToken cancellationToken)
    {
        folderRoot = Path.GetFullPath(folderRoot);
        var manifestPath = Path.Combine(folderRoot, ManifestFileName);

        var entries = new ConcurrentBag<ManifestEntry>();
        long scanned = 0;
        using var gate = new SemaphoreSlim(MaxDegreeOfParallelism);

        await CollectEntriesAsync(folderRoot, folderRoot, manifestPath, entries, gate,
            () => progress?.Report((int)Interlocked.Increment(ref scanned)),
            cancellationToken);

        var sorted = entries.OrderBy(e => e.RelativePath, StringComparer.OrdinalIgnoreCase).ToList();

        var sb = new StringBuilder();
        sb.AppendLine("# iPhotoBackupSync manifest -- files considered already backed up in this folder,");
        sb.AppendLine("# even though the file itself may not be present here (e.g. archived elsewhere).");
        sb.AppendLine("# Delete a line (or this whole file) to make iPhotoBackupSync treat that file as");
        sb.AppendLine("# missing again the next time you Compare.");
        sb.AppendLine(CultureInfo.InvariantCulture, $"# Generated {DateTime.UtcNow:O}");
        sb.AppendLine("# RelativePath\tSizeBytes\tLastModifiedUtc");
        foreach (var entry in sorted)
        {
            sb.Append(entry.RelativePath).Append('\t')
              .Append(entry.SizeBytes.ToString(CultureInfo.InvariantCulture)).Append('\t')
              .Append(entry.LastModifiedUtc?.ToString("O", CultureInfo.InvariantCulture) ?? string.Empty)
              .Append('\n');
        }

        await File.WriteAllTextAsync(manifestPath, sb.ToString(), Encoding.UTF8, cancellationToken);
        return sorted.Count;
    }

    private readonly record struct ManifestEntry(string RelativePath, long SizeBytes, DateTime? LastModifiedUtc);

    private static async Task CollectEntriesAsync(
        string root,
        string currentDir,
        string manifestPath,
        ConcurrentBag<ManifestEntry> entries,
        SemaphoreSlim gate,
        Action onFile,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        IEnumerable<FileSystemInfo> items;
        try
        {
            items = new DirectoryInfo(currentDir).EnumerateFileSystemInfos();
        }
        catch (UnauthorizedAccessException)
        {
            return;
        }
        catch (IOException)
        {
            return;
        }

        var subDirTasks = new List<Task>();
        foreach (var item in items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (item is DirectoryInfo)
            {
                subDirTasks.Add(RunBoundedAsync(gate, () =>
                    CollectEntriesAsync(root, item.FullName, manifestPath, entries, gate, onFile, cancellationToken)));
            }
            else if (item is FileInfo fi)
            {
                if (string.Equals(fi.FullName, manifestPath, StringComparison.OrdinalIgnoreCase)) continue;

                var relative = Path.GetRelativePath(root, fi.FullName);
                entries.Add(new ManifestEntry(relative, SafeLength(fi), SafeLastWriteUtc(fi)));
                onFile();
            }
        }

        await Task.WhenAll(subDirTasks);
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

    private static long SafeLength(FileInfo fi)
    {
        try { return fi.Length; } catch { return 0; }
    }

    private static DateTime? SafeLastWriteUtc(FileInfo fi)
    {
        try { return fi.LastWriteTimeUtc; } catch { return null; }
    }
}
