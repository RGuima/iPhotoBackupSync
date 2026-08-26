using System.Diagnostics;

namespace iPhotoBackupSync.Core.Services;

/// <summary>
/// Forces iCloud to sync specific files right now instead of waiting for its own
/// schedule, using the same mechanism as the standalone "Cloud Sync Forcer" tool:
/// restart the iCloud client process(es) so they immediately rescan for pending
/// work, then pin each target file ("Always keep on this device" via
/// <c>attrib +P -U</c>), which forces a cloud-only placeholder to be
/// hydrated/downloaded -- or a locally-dirty file to be re-evaluated for upload --
/// right away.
/// </summary>
public sealed class CloudSyncForceService
{
    private static readonly string[] ICloudProcessNames = { "iCloudPhotos", "iCloudDrive", "iCloudServices" };

    public async Task ForceSyncAsync(IReadOnlyList<string> filePaths, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        foreach (var processName in ICloudProcessNames)
        {
            cancellationToken.ThrowIfCancellationRequested();

            Process[] running;
            try
            {
                running = Process.GetProcessesByName(processName);
            }
            catch
            {
                continue;
            }

            if (running.Length == 0) continue;

            string? exePath = null;
            try { exePath = running[0].MainModule?.FileName; } catch { /* access can be denied for some processes */ }

            foreach (var process in running)
            {
                try { process.Kill(); } catch { /* best effort */ }
                finally { process.Dispose(); }
            }
            progress?.Report($"Restarted {processName} to trigger a fresh scan.");

            await Task.Delay(800, cancellationToken);

            if (!string.IsNullOrEmpty(exePath) && File.Exists(exePath))
            {
                try { Process.Start(exePath); } catch { /* best effort */ }
            }
        }

        var count = 0;
        foreach (var path in filePaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await RunAttribAsync(path, cancellationToken);
            count++;
            progress?.Report($"Requested sync for {Path.GetFileName(path)} ({count}/{filePaths.Count})");
        }
    }

    private static async Task RunAttribAsync(string path, CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "attrib.exe",
            Arguments = $"+P -U \"{path}\"",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        using var process = Process.Start(psi);
        if (process is null) return;
        await process.WaitForExitAsync(cancellationToken);
    }
}
