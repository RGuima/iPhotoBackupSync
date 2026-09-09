using iPhotoBackupSync.Core.Models;
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

    private static void WriteFile(string path, string content, DateTime modifiedUtc)
    {
        File.WriteAllText(path, content);
        File.SetLastWriteTimeUtc(path, modifiedUtc);
    }

    [Fact]
    public async Task GenerateManifest_RecordsTopLevelFilesOnly()
    {
        var modified = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        WriteFile(Path.Combine(_root, "a.jpg"), "x", modified);
        Directory.CreateDirectory(Path.Combine(_root, "Sub"));
        WriteFile(Path.Combine(_root, "Sub", "b.jpg"), "yy", modified);

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

        var recorded = service.ReadManifestFingerprints(_root);
        Assert.Contains(new FileFingerprint(1, modified), recorded); // "x"
        Assert.DoesNotContain(new FileFingerprint(2, modified), recorded); // "yy", in Sub
    }

    [Fact]
    public async Task GenerateManifest_DoesNotRecordItself()
    {
        var service = new BackupManifestService();
        await service.GenerateManifestAsync(_root, progress: null, CancellationToken.None);
        await service.GenerateManifestAsync(_root, progress: null, CancellationToken.None);

        Assert.Empty(service.ReadManifestFingerprints(_root));
    }

    [Fact]
    public async Task GenerateManifest_RerunAfterFileArchivedElsewhere_KeepsItsEntry()
    {
        var service = new BackupManifestService();
        var modifiedA = new DateTime(2020, 5, 1, 0, 0, 0, DateTimeKind.Utc);
        var pathA = Path.Combine(_root, "a.jpg");
        WriteFile(pathA, "x", modifiedA);

        var first = await service.GenerateManifestAsync(_root, progress: null, CancellationToken.None);
        Assert.Equal(1, first.TotalEntries);

        // Simulate the file being moved off to an archive drive, plus a new file arriving.
        File.Delete(pathA);
        var modifiedB = new DateTime(2021, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        WriteFile(Path.Combine(_root, "b.jpg"), "yy", modifiedB);

        var second = await service.GenerateManifestAsync(_root, progress: null, CancellationToken.None);

        Assert.Equal(1, second.PreviousEntries);
        Assert.Equal(1, second.NewEntries);
        Assert.Equal(2, second.TotalEntries);

        var recorded = service.ReadManifestFingerprints(_root);
        Assert.Contains(new FileFingerprint(1, modifiedA), recorded); // never removed, even gone from disk
        Assert.Contains(new FileFingerprint(2, modifiedB), recorded);
    }

    [Fact]
    public void ReadManifestFingerprints_NoManifestFile_ReturnsEmpty()
    {
        var service = new BackupManifestService();
        Assert.Empty(service.ReadManifestFingerprints(_root));
    }

    [Fact]
    public async Task GenerateManifest_NewEntries_AppendedAfterAddedMarker_ExistingEntryKeepsPosition()
    {
        var service = new BackupManifestService();
        var modifiedZ = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        WriteFile(Path.Combine(_root, "z.jpg"), "first", modifiedZ);
        await service.GenerateManifestAsync(_root, progress: null, CancellationToken.None);

        // Alphabetically before z.jpg -- a plain resort would put this first, but the new
        // format never resorts existing entries; new ones only ever get appended at the end.
        var modifiedA = new DateTime(2019, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        WriteFile(Path.Combine(_root, "a.jpg"), "second-content", modifiedA);
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

    [Fact]
    public async Task GenerateManifest_FileRenamedWithSameContent_UpdatesRecordedNameInPlace()
    {
        // Simulates iCloud renaming a file while its bytes/date stay the same -- the
        // manifest should follow the content (fingerprint), not the name, and treat this
        // as the same entry rather than a new one.
        var service = new BackupManifestService();
        var modified = new DateTime(2022, 3, 3, 0, 0, 0, DateTimeKind.Utc);
        var oldPath = Path.Combine(_root, "IMG_1596.HEIC");
        WriteFile(oldPath, "same-bytes", modified);
        var first = await service.GenerateManifestAsync(_root, progress: null, CancellationToken.None);
        Assert.Equal(1, first.NewEntries);

        File.Move(oldPath, Path.Combine(_root, "IMG_1596(1).HEIC"));
        var second = await service.GenerateManifestAsync(_root, progress: null, CancellationToken.None);

        Assert.Equal(1, second.PreviousEntries);
        Assert.Equal(0, second.NewEntries); // same fingerprint -- refreshed in place, not a new entry
        Assert.Equal(1, second.TotalEntries);

        var lines = File.ReadAllLines(Path.Combine(_root, BackupManifestService.ManifestFileName));
        Assert.Contains(lines, l => l.StartsWith("IMG_1596(1).HEIC\t", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, l => l.StartsWith("IMG_1596.HEIC\t", StringComparison.Ordinal));
    }
}
