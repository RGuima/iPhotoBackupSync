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
        var count = await service.GenerateManifestAsync(_root, progress: null, CancellationToken.None);

        Assert.Equal(2, count);
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
    public void ReadManifestPaths_NoManifestFile_ReturnsEmpty()
    {
        var service = new BackupManifestService();
        Assert.Empty(service.ReadManifestPaths(_root));
    }
}
