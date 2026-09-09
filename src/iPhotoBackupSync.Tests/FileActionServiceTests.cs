using iPhotoBackupSync.Core.Services;
using Xunit;

namespace iPhotoBackupSync.Tests;

public sealed class FileActionServiceTests : IDisposable
{
    private readonly string _root;
    private readonly string _origin;
    private readonly string _destination;

    public FileActionServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "iPhotoBackupSync.ActionTests_" + Guid.NewGuid());
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

    [Fact]
    public async Task CopyToDestination_ThenRecompare_ReportsNothingMissing()
    {
        // Compare is top-level only, so exercise it with top-level origin files -- the same
        // shape the real iCloud Photos origin folder actually has. Distinct content/dates
        // per file so fingerprint matching can't accidentally conflate them.
        WriteOrigin("root1.jpg", "content-a", new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        WriteOrigin("root2.jpg", "content-bb", new DateTime(2020, 2, 2, 0, 0, 0, DateTimeKind.Utc));
        WriteOrigin("root3.jpg", "content-ccc", new DateTime(2020, 3, 3, 0, 0, 0, DateTimeKind.Utc));

        var comparer = new FolderComparer();
        var firstPass = await comparer.CompareAsync(_origin, _destination, progress: null, CancellationToken.None);
        Assert.Equal(3, firstPass.MissingFileCount);

        var actionService = new FileActionService();
        await actionService.CopyToDestinationAsync(
            firstPass.Children, _origin, _destination, progress: null, CancellationToken.None);

        // Origin files must be untouched.
        Assert.True(File.Exists(Path.Combine(_origin, "root1.jpg")));
        Assert.True(File.Exists(Path.Combine(_origin, "root2.jpg")));
        Assert.True(File.Exists(Path.Combine(_origin, "root3.jpg")));

        // Destination must now have all three, directly at its root.
        Assert.True(File.Exists(Path.Combine(_destination, "root1.jpg")));
        Assert.True(File.Exists(Path.Combine(_destination, "root2.jpg")));
        Assert.True(File.Exists(Path.Combine(_destination, "root3.jpg")));

        var secondPass = await comparer.CompareAsync(_origin, _destination, progress: null, CancellationToken.None);
        Assert.Equal(0, secondPass.MissingFileCount);
        Assert.Empty(secondPass.Children);
    }

    [Fact]
    public async Task CopyToDestination_CopyingSingleFile_LeavesRestStillMissing()
    {
        WriteOrigin("a1.jpg", "content-one", new DateTime(2021, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        WriteOrigin("a2.jpg", "content-two", new DateTime(2021, 2, 2, 0, 0, 0, DateTimeKind.Utc));

        var comparer = new FolderComparer();
        var firstPass = await comparer.CompareAsync(_origin, _destination, progress: null, CancellationToken.None);
        var a1 = firstPass.Children.Single(n => n.Name == "a1.jpg");

        var actionService = new FileActionService();
        await actionService.CopyToDestinationAsync(
            new[] { a1 }, _origin, _destination, progress: null, CancellationToken.None);

        var secondPass = await comparer.CompareAsync(_origin, _destination, progress: null, CancellationToken.None);
        Assert.Equal(1, secondPass.MissingFileCount);
        var remaining = Assert.Single(secondPass.Children);
        Assert.Equal("a2.jpg", remaining.Name);
    }

    [Fact]
    public async Task CopyToDestination_NameAlreadyTakenByDifferentContent_UsesAlternateName()
    {
        // A destination file can share an origin file's *name* while being different
        // content (e.g. iCloud reused a name for a new photo while the old one moved to a
        // "(1)" suffix elsewhere) -- Copy must never destroy that existing destination file.
        WriteOrigin("IMG_1596.HEIC", "new-content", new DateTime(2026, 9, 7, 0, 0, 0, DateTimeKind.Utc));
        var destPath = Path.Combine(_destination, "IMG_1596.HEIC");
        File.WriteAllText(destPath, "old-content-already-backed-up");
        File.SetLastWriteTimeUtc(destPath, new DateTime(2024, 11, 9, 0, 0, 0, DateTimeKind.Utc));

        var comparer = new FolderComparer();
        var firstPass = await comparer.CompareAsync(_origin, _destination, progress: null, CancellationToken.None);
        var missing = Assert.Single(firstPass.Children);

        var actionService = new FileActionService();
        await actionService.CopyToDestinationAsync(
            new[] { missing }, _origin, _destination, progress: null, CancellationToken.None);

        // The pre-existing destination file must survive untouched...
        Assert.Equal("old-content-already-backed-up", File.ReadAllText(destPath));
        // ...and the new content must have been written under an alternate name instead.
        var altPath = Path.Combine(_destination, "IMG_1596 (1).HEIC");
        Assert.True(File.Exists(altPath));
        Assert.Equal("new-content", File.ReadAllText(altPath));
    }
}
