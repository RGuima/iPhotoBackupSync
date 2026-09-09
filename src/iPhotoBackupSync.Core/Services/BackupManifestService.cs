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
/// <summary>
/// Result of a manifest generation pass: <paramref name="TotalEntries"/> is the full
/// entry count after merging, <paramref name="NewEntries"/> is how many of those were
/// added in this pass, and <paramref name="PreviousEntries"/> is how many existed
/// before this pass ran (all of which are still present -- none are ever removed).
/// </summary>
public readonly record struct ManifestGenerationResult(int TotalEntries, int NewEntries, int PreviousEntries);

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
    /// Scans every file currently in <paramref name="folderRoot"/> (recursively) and adds
    /// each one to the manifest file at its root, creating the file if it doesn't exist
    /// yet. This only ever adds or refreshes entries for files that are still physically
    /// present -- it never removes an existing entry, even one for a file no longer found
    /// in this scan (e.g. because it was archived elsewhere after being recorded), since
    /// that entry may be the only remaining record that the file was ever backed up.
    /// </summary>
    public async Task<ManifestGenerationResult> GenerateManifestAsync(
        string folderRoot,
        IProgress<int>? progress,
        CancellationToken cancellationToken)
    {
        folderRoot = Path.GetFullPath(folderRoot);
        var manifestPath = Path.Combine(folderRoot, ManifestFileName);

        // Fail fast instead of silently treating an unreachable destination (e.g. a NAS
        // share that's off, asleep, or unmapped when an unattended run starts) as an empty
        // folder -- that would read 0 previous entries AND scan 0 files, so the safety net
        // below (which compares counts) wouldn't see anything wrong, and would write a
        // near-empty manifest over one that may have tens of thousands of real entries.
        if (!Directory.Exists(folderRoot))
        {
            throw new DirectoryNotFoundException(
                $"Destination folder is not reachable, refusing to touch its manifest: {folderRoot}");
        }

        var merged = new Dictionary<string, ManifestEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var existing in ReadRawEntries(manifestPath))
        {
            merged[existing.RelativePath] = existing;
        }
        var previousCount = merged.Count;

        var scannedEntries = new ConcurrentBag<ManifestEntry>();
        long scanned = 0;
        using var gate = new SemaphoreSlim(MaxDegreeOfParallelism);

        await CollectEntriesAsync(folderRoot, folderRoot, manifestPath, scannedEntries, gate,
            () => progress?.Report((int)Interlocked.Increment(ref scanned)),
            cancellationToken);

        // Add newly found files and refresh size/date for ones already recorded that are
        // still present; entries for files not seen in this scan are left untouched.
        var newCount = 0;
        foreach (var entry in scannedEntries)
        {
            if (!merged.ContainsKey(entry.RelativePath)) newCount++;
            merged[entry.RelativePath] = entry;
        }

        var sorted = merged.Values.OrderBy(e => e.RelativePath, StringComparer.OrdinalIgnoreCase).ToList();

        var sb = new StringBuilder();
        sb.AppendLine("# iPhotoBackupSync manifest -- files considered already backed up in this folder,");
        sb.AppendLine("# even though the file itself may not be present here (e.g. archived elsewhere).");
        sb.AppendLine("# Delete a line (or this whole file) to make iPhotoBackupSync treat that file as");
        sb.AppendLine("# missing again the next time you Compare. Regenerating this file only ever adds");
        sb.AppendLine("# or refreshes entries -- it never removes one for a file it doesn't currently see.");
        sb.AppendLine(CultureInfo.InvariantCulture, $"# Last generated {DateTime.UtcNow:O}");
        sb.AppendLine("# RelativePath\tSizeBytes\tLastModifiedUtc");
        foreach (var entry in sorted)
        {
            sb.Append(entry.RelativePath).Append('\t')
              .Append(entry.SizeBytes.ToString(CultureInfo.InvariantCulture)).Append('\t')
              .Append(entry.LastModifiedUtc?.ToString("O", CultureInfo.InvariantCulture) ?? string.Empty)
              .Append('\n');
        }

        // Safety net: if a manifest is already on disk right now and it has more entries
        // than what we're about to write, refuse to overwrite it. Merging never removes an
        // entry by design, so the only way the new content could be smaller is if reading
        // and/or scanning the destination failed partway through this run -- e.g. a NAS
        // share was still waking up its disks when an unattended 3am scheduled run started,
        // making File.Exists/File.ReadLines see nothing at the moment they were checked.
        // Silently overwriting in that case would look identical to "this folder legitimately
        // lost half its files" from the outside; refuse instead, and leave the old file alone.
        var onDiskCount = ReadRawEntries(manifestPath).Count;
        if (onDiskCount > sorted.Count)
        {
            throw new IOException(
                $"Refusing to overwrite manifest at \"{manifestPath}\": it currently has {onDiskCount:N0} " +
                $"entries on disk, but this run only produced {sorted.Count:N0} after merging. The destination " +
                "was likely temporarily unreachable during part of this run (e.g. a NAS share still waking " +
                "up). Nothing was written -- try again once the destination is reliably reachable.");
        }

        // Windows' CreateFile refuses to overwrite an existing file that already has the
        // Hidden (or System) attribute unless the write explicitly re-specifies it, and
        // File.WriteAllTextAsync doesn't -- so without clearing it first, every write after
        // the first one here would throw UnauthorizedAccessException once the file is hidden.
        if (File.Exists(manifestPath))
        {
            try { File.SetAttributes(manifestPath, FileAttributes.Normal); } catch { /* best-effort */ }
        }

        await File.WriteAllTextAsync(manifestPath, sb.ToString(), Encoding.UTF8, cancellationToken);

        // Mark the manifest hidden so NAS/media-import tools that auto-sort a folder's
        // contents by file type (which, on at least one real NAS, physically relocated this
        // exact file into a dated "Documents" archive because it isn't a photo/video --
        // wiping out the destination's manifest history the next time this ran) are far less
        // likely to enumerate it in the first place. WriteAllTextAsync above always creates a
        // fresh file, so this has to be reapplied on every write, not just the first one.
        try
        {
            File.SetAttributes(manifestPath, File.GetAttributes(manifestPath) | FileAttributes.Hidden);
        }
        catch
        {
            // Best-effort: some filesystems/SMB configurations don't support setting
            // attributes. The manifest still works fine without it, just more discoverable.
        }

        return new ManifestGenerationResult(sorted.Count, newCount, previousCount);
    }

    private static List<ManifestEntry> ReadRawEntries(string manifestPath)
    {
        var result = new List<ManifestEntry>();
        if (!File.Exists(manifestPath)) return result;

        foreach (var line in File.ReadLines(manifestPath))
        {
            if (string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith('#')) continue;

            var parts = line.Split('\t');
            var relativePath = parts[0].Trim();
            if (relativePath.Length == 0) continue;

            long size = 0;
            if (parts.Length > 1) long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out size);

            DateTime? modified = null;
            if (parts.Length > 2 && DateTime.TryParse(parts[2], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed))
            {
                modified = parsed;
            }

            result.Add(new ManifestEntry(relativePath, size, modified));
        }

        return result;
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

        // EnumerateFileSystemInfos() is lazy: the actual directory listing happens while
        // iterating, not on this call, so a plain try/catch around the call above would
        // never see an error that occurs partway through a listing (common on a NAS/network
        // share that hiccups mid-enumeration). Materialize the whole listing inside the try
        // instead, so any such failure is caught here and only skips this one folder,
        // instead of propagating up and aborting the entire scan.
        List<FileSystemInfo> items;
        try
        {
            items = new DirectoryInfo(currentDir).EnumerateFileSystemInfos().ToList();
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
