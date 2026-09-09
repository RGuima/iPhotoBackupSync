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

    private void WriteOrigin(string relativePath, string content)
    {
        var full = Path.Combine(_origin, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    [Fact]
    public async Task CopyToDestination_ThenRecompare_ReportsNothingMissing()
    {
        // Compare is top-level only, so exercise it with top-level origin files -- the same
        // shape the real iCloud Photos origin folder actually has.
        WriteOrigin("root1.jpg", "a");
        WriteOrigin("root2.jpg", "b");
        WriteOrigin("root3.jpg", "c");

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
        WriteOrigin("a1.jpg", "b");
        WriteOrigin("a2.jpg", "c");

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
}
