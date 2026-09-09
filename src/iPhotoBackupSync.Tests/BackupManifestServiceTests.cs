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
    public async Task GenerateManifest_RecordsTopLevelFilesOnly()
    {
        File.WriteAllText(Path.Combine(_root, "a.jpg"), "x");
        Directory.CreateDirectory(Path.Combine(_root, "Sub"));
        File.WriteAllText(Path.Combine(_root, "Sub", "b.jpg"), "yy");

        var service = new BackupManifestService();
        var result = await service.GenerateManifestAsync(_root, progress: null, CancellationToken.None);

        // Subfolders are never descended into -- a separate tool that sorts this same
        // destination folder's contents into dated subfolders shouldn't have every one of
        // its reorganized copies recorded as if it were a distinct backed-up file.
        Assert.Equal(1, result.TotalEntries);
        Assert.Equal(1, result.NewEntries);
        Assert.Equal(0, result.PreviousEntries);
        var manifestPath = Path.Combine(_root, BackupManifestService.ManifestFileName);
        Assert.True(File.Exists(manifestPath));

        var recorded = service.ReadManifestPaths(_root);
        Assert.Contains("a.jpg", recorded);
        Assert.DoesNotContain(Path.Combine("Sub", "b.jpg"), recorded);
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

    [Fact]
    public async Task GenerateManifest_NewEntries_AppendedAfterAddedMarker_ExistingEntryKeepsPosition()
    {
        var service = new BackupManifestService();
        File.WriteAllText(Path.Combine(_root, "z.jpg"), "first");
        await service.GenerateManifestAsync(_root, progress: null, CancellationToken.None);

        // Alphabetically before z.jpg -- a plain resort would put this first, but the new
        // format never resorts existing entries; new ones only ever get appended at the end.
        File.WriteAllText(Path.Combine(_root, "a.jpg"), "second");
        var result = await service.GenerateManifestAsync(_root, progress: null, CancellationToken.None);

        Assert.Equal(1, result.PreviousEntries);
        Assert.Equal(1, result.NewEntries);
        Assert.Equal(2, result.TotalEntries);

        var lines = File.ReadAllLines(Path.Combine(_root, BackupManifestService.ManifestFileName));
        var zIndex = Array.FindIndex(lines, l => l.StartsWith("z.jpg\t", StringComparison.Ordinal));
        // The first generation call above also appended z.jpg after its own "# Added"
        // marker, so there are two markers in the file now -- this run's is the last one.
        var addedMarkerIndex = Array.FindLastIndex(lines, l => l.StartsWith("# Added ", StringComparison.Ordinal));
        var aIndex = Array.FindIndex(lines, l => l.StartsWith("a.jpg\t", StringComparison.Ordinal));

        Assert.True(zIndex >= 0 && addedMarkerIndex >= 0 && aIndex >= 0);
        Assert.True(zIndex < addedMarkerIndex, "existing entry should stay before the new batch's marker");
        Assert.True(addedMarkerIndex < aIndex, "new entry should come after its batch's marker");
    }
}
