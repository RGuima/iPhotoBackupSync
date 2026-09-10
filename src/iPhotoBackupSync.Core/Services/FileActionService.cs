using System.Globalization;
using System.Text;
using iPhotoBackupSync.Core.Models;

namespace iPhotoBackupSync.Core.Services;

/// <summary>
/// Actions that can be applied to selected "missing" nodes. Kept intentionally small
/// for now (copy-to-destination is the obvious first action for a backup-gap tool);
/// more actions can be added here later without touching the UI's selection logic.
/// </summary>
public sealed class FileActionService
{
    /// <summary>
    /// Copies every selected file (recursing into selected directories) to the
    /// destination root, preserving the path relative to the origin root. Never
    /// deletes or modifies anything in the origin.
    ///
    /// Since <see cref="FolderComparer"/> now matches files by content
    /// (<see cref="FileFingerprint"/>) rather than by name, a file that's "missing" can
    /// still share its name with something already sitting at the destination under a
    /// name iCloud has since reused for different content. Never blindly overwrite that:
    /// if the destination name is taken by a file with a different fingerprint, this picks
    /// an alternate name instead, the same way Windows Explorer would.
    /// </summary>
    public async Task CopyToDestinationAsync(
        IEnumerable<FileNode> selectedNodes,
        string originRoot,
        string destinationRoot,
        IProgress<(int copied, int total, string currentPath)>? progress,
        CancellationToken cancellationToken)
    {
        var files = new List<FileNode>();
        foreach (var node in selectedNodes)
        {
            CollectFiles(node, files);
        }
        files = files.DistinctBy(f => f.FullPath).ToList();

        int copied = 0;
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var destPath = Path.Combine(destinationRoot, file.RelativePath);
            var destDir = Path.GetDirectoryName(destPath);
            if (!string.IsNullOrEmpty(destDir))
            {
                Directory.CreateDirectory(destDir);
            }

            var sourceInfo = new FileInfo(file.FullPath);
            destPath = ResolveSafeDestinationPath(destPath, SafeLength(sourceInfo));

            await using (var source = File.OpenRead(file.FullPath))
            await using (var dest = File.Create(destPath))
            {
                await source.CopyToAsync(dest, cancellationToken);
            }
            File.SetLastWriteTimeUtc(destPath, sourceInfo.LastWriteTimeUtc);

            copied++;
            progress?.Report((copied, files.Count, Path.GetRelativePath(destinationRoot, destPath)));
        }
    }

    /// <summary>Returns <paramref name="destPath"/> unchanged if nothing is there yet, or if
    /// what's there already has the same size (genuinely the same file, safe to overwrite in
    /// place -- the name is identical by construction here, so size is the only remaining
    /// signal). Otherwise a different file already occupies that name, so this returns the
    /// first "name (1)", "name (2)", ... variant that's free.</summary>
    private static string ResolveSafeDestinationPath(string destPath, long sourceSize)
    {
        if (!File.Exists(destPath)) return destPath;
        if (SafeLength(new FileInfo(destPath)) == sourceSize) return destPath;

        var dir = Path.GetDirectoryName(destPath) ?? string.Empty;
        var baseName = Path.GetFileNameWithoutExtension(destPath);
        var ext = Path.GetExtension(destPath);
        var counter = 1;
        string candidate;
        do
        {
            candidate = Path.Combine(dir, $"{baseName} ({counter}){ext}");
            counter++;
        } while (File.Exists(candidate));
        return candidate;
    }

    private static long SafeLength(FileInfo fi)
    {
        try { return fi.Length; } catch { return 0; }
    }

    public void ExportToCsv(IEnumerable<FileNode> nodes, string csvPath)
    {
        var sb = new StringBuilder();
        sb.AppendLine("RelativePath,Type,SizeBytes,LastModifiedUtc,SyncStatus");
        foreach (var node in nodes.Where(n => !n.IsDirectory && n.IsMissing))
        {
            sb.Append(CsvEscape(node.RelativePath)).Append(',')
              .Append("File").Append(',')
              .Append(node.SizeBytes.ToString(CultureInfo.InvariantCulture)).Append(',')
              .Append(node.LastModifiedUtc?.ToString("O") ?? "").Append(',')
              .Append(node.SyncStatus)
              .Append('\n');
        }
        File.WriteAllText(csvPath, sb.ToString(), Encoding.UTF8);
    }

    private static string CsvEscape(string value)
    {
        if (value.Contains(',') || value.Contains('"') || value.Contains('\n'))
        {
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        }
        return value;
    }

    private static void CollectFiles(FileNode node, List<FileNode> result)
    {
        if (!node.IsDirectory)
        {
            if (node.IsMissing) result.Add(node);
            return;
        }

        foreach (var child in node.Children)
        {
            CollectFiles(child, result);
        }
    }
}
