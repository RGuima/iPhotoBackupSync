using iPhotoBackupSync.Core.Models;

namespace iPhotoBackupSync.Core.Services;

/// <summary>
/// Headless equivalent of the UI's Compare -> Force Sync -> wait -> Copy -> Generate
/// Manifest workflow, for running iPhoto Backup Sync unattended from the command line.
/// Files are force-synced one at a time (the same way <see cref="CloudSyncForceService"/>
/// already requests iCloud syncs sequentially rather than concurrently). Sync status is
/// then polled repeatedly, copying each batch of newly-synced files to the destination
/// as soon as it finishes -- rather than waiting for every missing file to finish
/// syncing before copying anything -- until every file has been copied or
/// <see cref="MaxWaitForSync"/> elapses, whichever comes first. The destination's
/// backup manifest is always regenerated at the end, exactly as the UI's "Generate
/// Manifest for Folder..." action would -- an unattended run must never skip that step
/// just because there's no one there to click the button.
/// </summary>
public sealed class CliBackupRunner
{
    private readonly FolderComparer _comparer = new();
    private readonly CloudSyncForceService _forceService = new();
    private readonly CloudSyncStatusProvider _statusProvider = new();
    private readonly FileActionService _actionService = new();
    private readonly BackupManifestService _manifestService = new();

    /// <summary>How long to keep waiting for iCloud to finish syncing before giving up
    /// and copying whatever has become available in the meantime.</summary>
    public TimeSpan MaxWaitForSync { get; init; } = TimeSpan.FromHours(1);

    /// <summary>How often to re-check sync status while waiting.</summary>
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(30);

    public async Task<int> RunAsync(
        string originRoot,
        string destinationRoot,
        IProgress<string> progress,
        CancellationToken cancellationToken)
    {
        originRoot = Path.GetFullPath(originRoot);
        destinationRoot = Path.GetFullPath(destinationRoot);

        if (!Directory.Exists(originRoot))
        {
            progress.Report($"Origin folder does not exist: {originRoot}");
            return 1;
        }

        progress.Report("Comparing origin and destination...");
        var root = await _comparer.CompareAsync(originRoot, destinationRoot, progress: null, cancellationToken);

        var missingFiles = new List<FileNode>();
        CollectMissingFiles(root, missingFiles);
        progress.Report($"{missingFiles.Count:N0} file(s) missing from the destination.");

        if (missingFiles.Count > 0)
        {
            var needsSync = missingFiles.Where(f => f.SyncStatus != SyncStatus.Synced).ToList();
            if (needsSync.Count > 0)
            {
                progress.Report($"Requesting iCloud sync for {needsSync.Count:N0} file(s) not yet synced...");
                await _forceService.ForceSyncAsync(needsSync.Select(f => f.FullPath).ToList(), progress, cancellationToken);
            }

            // Copy files as soon as each one finishes syncing rather than waiting for the
            // whole batch -- on a large library, plenty of missing files are often already
            // synced immediately (nothing to wait on at all), and others finish syncing at
            // different times while this loop is still waiting on the rest.
            var pending = new List<FileNode>(missingFiles);
            var copiedTotal = 0;
            var deadline = DateTime.UtcNow + MaxWaitForSync;

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                foreach (var file in pending)
                {
                    file.SyncStatus = _statusProvider.GetStatus(file.FullPath);
                }

                var readyNow = pending.Where(f => f.SyncStatus == SyncStatus.Synced).ToList();
                if (readyNow.Count > 0)
                {
                    if (!Directory.Exists(destinationRoot))
                    {
                        Directory.CreateDirectory(destinationRoot);
                    }

                    progress.Report($"{readyNow.Count:N0} file(s) just finished syncing -- copying them now...");
                    var copyProgress = new RelayProgress<(int copied, int total, string currentPath)>(
                        p => progress.Report($"Copied {p.copied:N0}/{p.total:N0}: {p.currentPath}"));
                    await _actionService.CopyToDestinationAsync(readyNow, originRoot, destinationRoot, copyProgress, cancellationToken);

                    copiedTotal += readyNow.Count;
                    pending.RemoveAll(f => f.SyncStatus == SyncStatus.Synced);
                }

                if (pending.Count == 0)
                {
                    progress.Report("All missing files have been synced and copied.");
                    break;
                }

                if (DateTime.UtcNow >= deadline)
                {
                    progress.Report($"Gave up waiting after {MaxWaitForSync.TotalMinutes:N0} minute(s) -- " +
                                     $"{pending.Count:N0} file(s) still aren't synced; they'll be picked up on the next run.");
                    break;
                }

                progress.Report($"{pending.Count:N0} file(s) still syncing; checking again in {PollInterval.TotalSeconds:N0}s...");
                await Task.Delay(PollInterval, cancellationToken);
            }

            progress.Report(copiedTotal == 0
                ? "No files were synced in time -- nothing copied."
                : $"Copied {copiedTotal:N0} file(s) total to \"{destinationRoot}\".");
        }

        progress.Report("Updating the backup manifest for the destination folder...");
        var manifestResult = await _manifestService.GenerateManifestAsync(destinationRoot, progress: null, cancellationToken);
        progress.Report($"Manifest updated: {manifestResult.TotalEntries:N0} total entries " +
                         $"({manifestResult.NewEntries:N0} new).");

        return 0;
    }

    private static void CollectMissingFiles(FileNode node, List<FileNode> result)
    {
        if (!node.IsDirectory)
        {
            if (node.IsMissing) result.Add(node);
            return;
        }

        foreach (var child in node.Children)
        {
            CollectMissingFiles(child, result);
        }
    }

    private sealed class RelayProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
