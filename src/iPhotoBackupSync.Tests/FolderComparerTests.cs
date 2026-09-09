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

    private void WriteOrigin(string relativePath, string content, DateTime modifiedUtc)
    {
        var full = Path.Combine(_origin, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        File.SetLastWriteTimeUtc(full, modifiedUtc);
    }

    private void WriteDestination(string relativePath, string content, DateTime modifiedUtc)
    {
        var full = Path.Combine(_destination, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        File.SetLastWriteTimeUtc(full, modifiedUtc);
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
        var t = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        WriteOrigin("root1.jpg", "unique-1", t);
        WriteOrigin("root2.jpg", "shared", t);
        WriteDestination("root2.jpg", "shared", t);

        var comparer = new FolderComparer();
        var result = await comparer.CompareAsync(_origin, _destination, progress: null, CancellationToken.None);

        var missing = FlattenFiles(result).Select(f => f.RelativePath).OrderBy(x => x).ToList();
        Assert.Equal(new[] { "root1.jpg" }, missing);
        Assert.Equal(1, result.MissingFileCount);
    }

    [Fact]
    public async Task IdenticalTopLevelFiles_ProduceNoResults()
    {
        var t = new DateTime(2024, 2, 2, 0, 0, 0, DateTimeKind.Utc);
        WriteOrigin("a1.jpg", "content", t);
        WriteDestination("a1.jpg", "content", t);

        var comparer = new FolderComparer();
        var result = await comparer.CompareAsync(_origin, _destination, progress: null, CancellationToken.None);

        Assert.Empty(result.Children);
        Assert.Equal(0, result.MissingFileCount);
    }

    [Fact]
    public async Task MatchIsByContentNotName_RenamedFileAtDestinationIsNotReportedMissing()
    {
        // iCloud renames files as part of its own sync/conflict resolution over time -- a
        // file already backed up under one name shouldn't show up as missing just because
        // the origin's copy now has a different name, as long as size + last-modified time
        // still match what's at the destination.
        var t = new DateTime(2019, 5, 5, 0, 0, 0, DateTimeKind.Utc);
        WriteOrigin("IMG_1596.HEIC", "same-bytes", t);
        WriteDestination("IMG_1596(1).HEIC", "same-bytes", t); // same content, different name

        var comparer = new FolderComparer();
        var result = await comparer.CompareAsync(_origin, _destination, progress: null, CancellationToken.None);

        Assert.Empty(FlattenFiles(result));
        Assert.Equal(0, result.MissingFileCount);
    }

    [Fact]
    public async Task SameNameDifferentContent_IsStillReportedMissing()
    {
        // The exact bug this was built to fix: iCloud reused the plain name "IMG_1596.HEIC"
        // for a new, different photo while the original file became "IMG_1596(1).HEIC".
        // A destination file sharing just the *name* with a different fingerprint must not
        // suppress detection of the real, currently-unbacked-up file.
        WriteOrigin("IMG_1596.HEIC", "new-bigger-content", new DateTime(2026, 9, 7, 0, 0, 0, DateTimeKind.Utc));
        WriteDestination("IMG_1596.HEIC", "old-content", new DateTime(2024, 11, 9, 0, 0, 0, DateTimeKind.Utc));

        var comparer = new FolderComparer();
        var result = await comparer.CompareAsync(_origin, _destination, progress: null, CancellationToken.None);

        var missing = FlattenFiles(result).Select(f => f.RelativePath).ToList();
        Assert.Equal(new[] { "IMG_1596.HEIC" }, missing);
        Assert.Equal(1, result.MissingFileCount);
    }

    [Fact]
    public async Task FilesInSubfolders_AreIgnoredOnBothSides()
    {
        // Subfolders are never descended into on either side -- a separate tool that
        // reorganizes this same destination folder's contents into dated subfolders
        // shouldn't have its output compared against (or matched to) origin files.
        var t = new DateTime(2021, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        WriteOrigin("SubB/nested/deep.jpg", "deep", t);
        WriteOrigin("SubB/b1.jpg", "b1", t);
        WriteOrigin("top.jpg", "top", t);
        WriteDestination("top.jpg", "top", t);

        var comparer = new FolderComparer();
        var result = await comparer.CompareAsync(_origin, _destination, progress: null, CancellationToken.None);

        Assert.Empty(FlattenFiles(result));
        Assert.Equal(0, result.MissingFileCount);
    }

    [Fact]
    public async Task NonExistentDestination_TreatsEverythingAsMissing()
    {
        WriteOrigin("root1.jpg", "x", new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        Directory.Delete(_destination);

        var comparer = new FolderComparer();
        var result = await comparer.CompareAsync(_origin, _destination, progress: null, CancellationToken.None);

        Assert.Equal(1, result.MissingFileCount);
    }

    [Fact]
    public async Task FileRecordedInManifest_IsNotReportedMissing()
    {
        var archivedTime = new DateTime(2018, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var missingTime = new DateTime(2018, 2, 2, 0, 0, 0, DateTimeKind.Utc);
        WriteOrigin("archived.jpg", "archived-content", archivedTime); // 16 bytes
        WriteOrigin("still-missing.jpg", "still-missing-content", missingTime);
        File.WriteAllText(
            Path.Combine(_destination, BackupManifestService.ManifestFileName),
            $"# comment line, ignored\narchived.jpg\t16\t{archivedTime:O}\n");

        var comparer = new FolderComparer();
        var result = await comparer.CompareAsync(_origin, _destination, progress: null, CancellationToken.None);

        var missing = FlattenFiles(result).Select(f => f.RelativePath).OrderBy(x => x).ToList();
        Assert.Equal(new[] { "still-missing.jpg" }, missing);
    }
}
