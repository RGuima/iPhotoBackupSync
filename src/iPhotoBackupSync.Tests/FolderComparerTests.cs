using iPhotoBackupSync.Core.Models;
using iPhotoBackupSync.Core.Services;
using Xunit;

namespace iPhotoBackupSync.Tests;

public sealed class FolderComparerTests : IDisposable
{
    private readonly string _root;
    private readonly string _origin;
    private readonly string _destination;

    public FolderComparerTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "iPhotoBackupSync.Tests_" + Guid.NewGuid());
        _origin = Path.Combine(_root, "origin");
        _destination = Path.Combine(_root, "destination");
        Directory.CreateDirectory(_origin);
        Directory.CreateDirectory(_destination);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort cleanup */ }
    }

    private void WriteOrigin(string relativePath, string content = "x")
    {
        var full = Path.Combine(_origin, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    private void WriteDestination(string relativePath, string content = "x")
    {
        var full = Path.Combine(_destination, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    private static List<FileNode> FlattenFiles(FileNode node)
    {
        var result = new List<FileNode>();
        void Walk(FileNode n)
        {
            if (!n.IsDirectory) result.Add(n);
            foreach (var c in n.Children) Walk(c);
        }
        foreach (var c in node.Children) Walk(c);
        return result;
    }

    [Fact]
    public async Task FileOnlyInOrigin_IsReportedMissing()
    {
        WriteOrigin("root1.jpg");
        WriteOrigin("root2.jpg");
        WriteDestination("root2.jpg");

        var comparer = new FolderComparer();
        var result = await comparer.CompareAsync(_origin, _destination, progress: null, CancellationToken.None);

        var missing = FlattenFiles(result).Select(f => f.RelativePath).OrderBy(x => x).ToList();
        Assert.Equal(new[] { "root1.jpg" }, missing);
        Assert.Equal(1, result.MissingFileCount);
    }

    [Fact]
    public async Task IdenticalTopLevelFiles_ProduceNoResults()
    {
        WriteOrigin("a1.jpg");
        WriteDestination("a1.jpg");

        var comparer = new FolderComparer();
        var result = await comparer.CompareAsync(_origin, _destination, progress: null, CancellationToken.None);

        Assert.Empty(result.Children);
        Assert.Equal(0, result.MissingFileCount);
    }

    [Fact]
    public async Task FilesInSubfolders_AreIgnoredOnBothSides()
    {
        // Subfolders are never descended into on either side -- a separate tool that
        // reorganizes this same destination folder's contents into dated subfolders
        // shouldn't have its output compared against (or matched to) origin files.
        WriteOrigin("SubB/nested/deep.jpg");
        WriteOrigin("SubB/b1.jpg");
        WriteOrigin("top.jpg");
        WriteDestination("top.jpg");

        var comparer = new FolderComparer();
        var result = await comparer.CompareAsync(_origin, _destination, progress: null, CancellationToken.None);

        Assert.Empty(FlattenFiles(result));
        Assert.Equal(0, result.MissingFileCount);
    }

    [Fact]
    public async Task NonExistentDestination_TreatsEverythingAsMissing()
    {
        WriteOrigin("root1.jpg");
        Directory.Delete(_destination);

        var comparer = new FolderComparer();
        var result = await comparer.CompareAsync(_origin, _destination, progress: null, CancellationToken.None);

        Assert.Equal(1, result.MissingFileCount);
    }

    [Fact]
    public async Task FileRecordedInManifest_IsNotReportedMissing()
    {
        WriteOrigin("archived.jpg");
        WriteOrigin("still-missing.jpg");
        File.WriteAllText(
            Path.Combine(_destination, BackupManifestService.ManifestFileName),
            "# comment line, ignored\narchived.jpg\t123\t2024-01-01T00:00:00Z\n");

        var comparer = new FolderComparer();
        var result = await comparer.CompareAsync(_origin, _destination, progress: null, CancellationToken.None);

        var missing = FlattenFiles(result).Select(f => f.RelativePath).OrderBy(x => x).ToList();
        Assert.Equal(new[] { "still-missing.jpg" }, missing);
    }
}
