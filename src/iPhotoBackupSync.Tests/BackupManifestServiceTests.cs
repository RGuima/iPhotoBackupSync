using iPhotoBackupSync.Core.Services;
using Xunit;

namespace iPhotoBackupSync.Tests;

public sealed class BackupManifestServiceTests : IDisposable
{
    private readonly string _root;

    public BackupManifestServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "iPhotoBackupSync.ManifestTests_" + Guid.NewGuid());
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort cleanup */ }
    }

    [Fact]
    public async Task GenerateManifest_RecordsEveryExistingFile()
    {
        File.WriteAllText(Path.Combine(_root, "a.jpg"), "x");
        Directory.CreateDirectory(Path.Combine(_root, "Sub"));
        File.WriteAllText(Path.Combine(_root, "Sub", "b.jpg"), "yy");

        var service = new BackupManifestService();
        var result = await service.GenerateManifestAsync(_root, progress: null, CancellationToken.None);

        Assert.Equal(2, result.TotalEntries);
        Assert.Equal(2, result.NewEntries);
        Assert.Equal(0, result.PreviousEntries);
        var manifestPath = Path.Combine(_root, BackupManifestService.ManifestFileName);
        Assert.True(File.Exists(manifestPath));

        var recorded = service.ReadManifestPaths(_root);
        Assert.Contains("a.jpg", recorded);
        Assert.Contains(Path.Combine("Sub", "b.jpg"), recorded);
        Assert.Contains("Sub", recorded); // ancestor folder implied present too
    }

    [Fact]
    public async Task GenerateManifest_DoesNotRecordItself()
    {
        var service = new BackupManifestService();
        await service.GenerateManifestAsync(_root, progress: null, CancellationToken.None);
        await service.GenerateManifestAsync(_root, progress: null, CancellationToken.None);

        var recorded = service.ReadManifestPaths(_root);
        Assert.DoesNotContain(BackupManifestService.ManifestFileName, recorded);
    }

    [Fact]
    public async Task GenerateManifest_RerunAfterFileArchivedElsewhere_KeepsItsEntry()
    {
        var service = new BackupManifestService();
        var pathA = Path.Combine(_root, "a.jpg");
        File.WriteAllText(pathA, "x");

        var first = await service.GenerateManifestAsync(_root, progress: null, CancellationToken.None);
        Assert.Equal(1, first.TotalEntries);

        // Simulate the file being moved off to an archive drive, plus a new file arriving.
        File.Delete(pathA);
        File.WriteAllText(Path.Combine(_root, "b.jpg"), "yy");

        var second = await service.GenerateManifestAsync(_root, progress: null, CancellationToken.None);

        Assert.Equal(1, second.PreviousEntries);
        Assert.Equal(1, second.NewEntries);
        Assert.Equal(2, second.TotalEntries);

        var recorded = service.ReadManifestPaths(_root);
        Assert.Contains("a.jpg", recorded); // never removed, even though it's gone from disk
        Assert.Contains("b.jpg", recorded);
    }

    [Fact]
    public void ReadManifestPaths_NoManifestFile_ReturnsEmpty()
    {
        var service = new BackupManifestService();
        Assert.Empty(service.ReadManifestPaths(_root));
    }
}
