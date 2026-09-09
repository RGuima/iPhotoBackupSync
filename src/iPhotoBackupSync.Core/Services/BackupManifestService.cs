using System.Globalization;
using System.Text;
using iPhotoBackupSync.Core.Models;

namespace iPhotoBackupSync.Core.Services;

/// <summary>
/// Manages a human-readable "backup manifest" file that can live at the root of a
/// destination folder, recording files that should be treated as already backed up
/// even though the actual bytes aren't (or are no longer) present in that folder --
/// e.g. because they were archived to offline media, a different drive, or cold
/// storage after the fact. A manifest entry is matched by <see cref="FileFingerprint"/>
/// (size + last-modified time), the same signal <see cref="FolderComparer"/> uses to
/// decide whether a file exists in the destination -- not by name, since iCloud can
/// reassign a name to different content over time (see <see cref="FileFingerprint"/>).
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

    /// <summary>
    /// Reads the manifest file at <paramref name="destinationRoot"/>, if one exists, and
    /// returns the content fingerprint (size + last-modified time) of every file it
    /// records. Returns an empty set if there is no manifest.
    /// </summary>
    public IReadOnlySet<FileFingerprint> ReadManifestFingerprints(string destinationRoot)
    {
        var manifestPath = Path.Combine(destinationRoot, ManifestFileName);
        var result = new HashSet<FileFingerprint>();
        foreach (var line in ParseBody(manifestPath))
        {
            if (line.Entry is { } entry) result.Add(entry.Fingerprint);
        }
        return result;
    }

    /// <summary>
    /// Scans every file directly inside <paramref name="folderRoot"/> -- top-level only,
    /// subfolders are never descended into -- and adds each one to the manifest file at its
    /// root, creating the file if it doesn't exist yet. This only ever adds or refreshes
    /// entries for files that are still physically present -- it never removes an existing
    /// entry, even one for a file no longer found in this scan (e.g. because it was archived
    /// elsewhere after being recorded), since that entry may be the only remaining record
    /// that the file was ever backed up.
    ///
    /// Existing entries are matched by <see cref="FileFingerprint"/> (size + last-modified
    /// time), not by name, and keep their original position in the file even when refreshed
    /// -- only the recorded name is updated, in case iCloud renamed the file since the last
    /// scan. Entries are never resorted; files new to this run are appended at the very end
    /// instead, preceded by a "# Added &lt;timestamp&gt;" comment marking that batch -- so
    /// the file reads as a rough history of when things were added, and a diff between two
    /// versions only ever shows a new block tacked on the end.
    ///
    /// Deliberately not recursive: this app always copies files straight into the
    /// destination root (it never mirrors origin subfolders), so any subfolder here belongs
    /// to something else entirely -- in practice, a separate file-organizer tool that sorts
    /// this same folder's contents into dated Photos/Videos/Documents subfolders. Recursing
    /// into those used to record every one of its reorganized copies as if it were a
    /// separate backed-up file, tripling up entries for the same photo and making the
    /// manifest grow without bound every time that tool ran.
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

        var body = ParseBody(manifestPath);

        // Map each existing entry's content fingerprint (size + last-modified time, not
        // name -- see FileFingerprint) to its position in `body`, so a file that's still
        // present gets its recorded name refreshed in place if iCloud has since renamed it;
        // new entries are the only ones that ever get appended. Two entries can't collide on
        // fingerprint here since ParseBody never returns duplicate fingerprints for distinct
        // files unless the destination genuinely has byte-identical duplicates, in which
        // case treating them as one backed-up item is correct.
        var existingIndex = new Dictionary<FileFingerprint, int>();
        for (var i = 0; i < body.Count; i++)
        {
            if (body[i].Entry is { } existing) existingIndex[existing.Fingerprint] = i;
        }
        var previousCount = existingIndex.Count;

        var scannedEntries = CollectTopLevelEntries(folderRoot, manifestPath, progress, cancellationToken);

        var newEntries = new List<ManifestEntry>();
        foreach (var entry in scannedEntries)
        {
            if (existingIndex.TryGetValue(entry.Fingerprint, out var index))
            {
                body[index] = BodyLine.Data(entry); // same content; refreshes the recorded name too
            }
            else
            {
                newEntries.Add(entry);
            }
        }

        if (newEntries.Count > 0)
        {
            newEntries.Sort((a, b) => string.Compare(a.RelativePath, b.RelativePath, StringComparison.OrdinalIgnoreCase));
            body.Add(BodyLine.Comment($"# Added {DateTime.UtcNow:O}"));
            body.AddRange(newEntries.Select(BodyLine.Data));
        }

        var totalCount = body.Count(l => l.Entry is not null);

        var sb = new StringBuilder();
        sb.AppendLine("# iPhotoBackupSync manifest -- files considered already backed up in this folder,");
        sb.AppendLine("# even though the file itself may not be present here (e.g. archived elsewhere).");
        sb.AppendLine("# Delete a line (or this whole file) to make iPhotoBackupSync treat that file as");
        sb.AppendLine("# missing again the next time you Compare. Regenerating this file only ever adds");
        sb.AppendLine("# or refreshes entries -- it never removes one for a file it doesn't currently see.");
        sb.AppendLine("# New entries are appended at the end after a \"# Added <timestamp>\" marker rather");
        sb.AppendLine("# than the whole file being resorted, so existing entries keep their position.");
        sb.AppendLine(CultureInfo.InvariantCulture, $"# Last generated {DateTime.UtcNow:O}");
        sb.AppendLine("# RelativePath\tSizeBytes\tLastModifiedUtc");
        foreach (var line in body)
        {
            if (line.CommentText is { } comment)
            {
                sb.Append(comment).Append('\n');
            }
            else if (line.Entry is { } entry)
            {
                sb.Append(entry.RelativePath).Append('\t')
                  .Append(entry.SizeBytes.ToString(CultureInfo.InvariantCulture)).Append('\t')
                  .Append(entry.LastModifiedUtc?.ToString("O", CultureInfo.InvariantCulture) ?? string.Empty)
                  .Append('\n');
            }
        }

        // Safety net: if a manifest is already on disk right now and it has more entries
        // than what we're about to write, refuse to overwrite it. Merging never removes an
        // entry by design, so the only way the new content could be smaller is if reading
        // and/or scanning the destination failed partway through this run -- e.g. a NAS
        // share was still waking up its disks when an unattended 3am scheduled run started,
        // making File.Exists/File.ReadLines see nothing at the moment they were checked.
        // Silently overwriting in that case would look identical to "this folder legitimately
        // lost half its files" from the outside; refuse instead, and leave the old file alone.
        var onDiskCount = ParseBody(manifestPath).Count(l => l.Entry is not null);
        if (onDiskCount > totalCount)
        {
            throw new IOException(
                $"Refusing to overwrite manifest at \"{manifestPath}\": it currently has {onDiskCount:N0} " +
                $"entries on disk, but this run only produced {totalCount:N0} after merging. The destination " +
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

        return new ManifestGenerationResult(totalCount, newEntries.Count, previousCount);
    }

    /// <summary>
    /// Reads every line of the manifest, in file order, as either a preserved comment (e.g.
    /// a previous run's "# Added &lt;timestamp&gt;" batch marker) or a parsed data entry --
    /// skipping only the fixed instructional header this method itself regenerates fresh on
    /// every write. Returns an empty list if there is no manifest yet.
    /// </summary>
    private static List<BodyLine> ParseBody(string manifestPath)
    {
        var body = new List<BodyLine>();
        if (!File.Exists(manifestPath)) return body;

        foreach (var line in File.ReadLines(manifestPath))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;

            var trimmed = line.TrimStart();
            if (trimmed.StartsWith('#'))
            {
                if (!IsGeneratedHeaderLine(trimmed)) body.Add(BodyLine.Comment(trimmed));
                continue;
            }

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

            body.Add(BodyLine.Data(new ManifestEntry(relativePath, size, modified)));
        }

        return body;
    }

    /// <summary>True for one of the fixed instructional/column-header lines this method
    /// writes fresh at the top of the file on every run -- these are never preserved
    /// verbatim, unlike a batch marker such as "# Added &lt;timestamp&gt;".</summary>
    private static bool IsGeneratedHeaderLine(string line) =>
        line is "# iPhotoBackupSync manifest -- files considered already backed up in this folder,"
             or "# even though the file itself may not be present here (e.g. archived elsewhere)."
             or "# Delete a line (or this whole file) to make iPhotoBackupSync treat that file as"
             or "# missing again the next time you Compare. Regenerating this file only ever adds"
             or "# or refreshes entries -- it never removes one for a file it doesn't currently see."
             or "# New entries are appended at the end after a \"# Added <timestamp>\" marker rather"
             or "# than the whole file being resorted, so existing entries keep their position."
             or "# RelativePath\tSizeBytes\tLastModifiedUtc"
             || line.StartsWith("# Last generated", StringComparison.Ordinal);

    private readonly record struct ManifestEntry(string RelativePath, long SizeBytes, DateTime? LastModifiedUtc)
    {
        public FileFingerprint Fingerprint => new(SizeBytes, LastModifiedUtc);
    }

    /// <summary>One line of the manifest body: either a preserved comment or a data entry,
    /// never both. Use <see cref="Comment"/>/<see cref="Data"/> to construct.</summary>
    private readonly record struct BodyLine(string? CommentText, ManifestEntry? Entry)
    {
        public static BodyLine Comment(string text) => new(text, null);
        public static BodyLine Data(ManifestEntry entry) => new(null, entry);
    }

    private static List<ManifestEntry> CollectTopLevelEntries(
        string root,
        string manifestPath,
        IProgress<int>? progress,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var entries = new List<ManifestEntry>();
        var scanned = 0;

        // EnumerateFileSystemInfos() is lazy: the actual directory listing happens while
        // iterating, not on this call, so a plain try/catch around the call above wouldn't
        // catch an error that occurs partway through a listing (possible on a NAS/network
        // share that hiccups mid-enumeration). Materialize the whole listing inside the try
        // instead, so any such failure is caught here rather than propagating out.
        List<FileSystemInfo> items;
        try
        {
            items = new DirectoryInfo(root).EnumerateFileSystemInfos().ToList();
        }
        catch (UnauthorizedAccessException)
        {
            return entries;
        }
        catch (IOException)
        {
            return entries;
        }

        foreach (var item in items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (item is not FileInfo fi) continue; // subfolders are never descended into
            if (string.Equals(fi.FullName, manifestPath, StringComparison.OrdinalIgnoreCase)) continue;

            entries.Add(new ManifestEntry(fi.Name, SafeLength(fi), SafeLastWriteUtc(fi)));
            progress?.Report(++scanned);
        }

        return entries;
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
