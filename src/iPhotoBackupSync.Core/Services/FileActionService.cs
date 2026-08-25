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

            await using (var source = File.OpenRead(file.FullPath))
            await using (var dest = File.Create(destPath))
            {
                await source.CopyToAsync(dest, cancellationToken);
            }
            File.SetLastWriteTimeUtc(destPath, File.GetLastWriteTimeUtc(file.FullPath));

            copied++;
            progress?.Report((copied, files.Count, file.RelativePath));
        }
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
