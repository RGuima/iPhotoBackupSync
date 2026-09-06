using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using iPhotoBackupSync.Core.Services;

namespace iPhotoBackupSync.App;

public partial class App : System.Windows.Application
{
    /// <summary>
    /// Command-line mode: "iPhotoBackupSync.exe &lt;origin&gt; &lt;destination&gt;" runs the full
    /// Compare -> Force Sync -> wait -> Copy -> Generate Manifest workflow headlessly and
    /// exits, instead of showing the normal desktop UI (which stays the default when the
    /// app is launched with no arguments).
    /// </summary>
    protected override void OnStartup(StartupEventArgs e)
    {
        if (e.Args.Length >= 2)
        {
            Environment.Exit(RunCli(e.Args[0], e.Args[1]));
            return;
        }

        if (e.Args.Length == 1)
        {
            AttachOrAllocConsole();
            Console.WriteLine("Usage: iPhotoBackupSync.exe <origin folder> <destination folder>");
            Console.WriteLine("(Run with no arguments to launch the normal desktop UI instead.)");
            Environment.Exit(1);
            return;
        }

        base.OnStartup(e);
    }

    private static int RunCli(string origin, string destination)
    {
        AttachOrAllocConsole();
        Console.WriteLine("iPhoto Backup Sync -- command-line mode");
        Console.WriteLine($"Origin:      {origin}");
        Console.WriteLine($"Destination: {destination}");
        Console.WriteLine();

        using var cts = new CancellationTokenSource();
        void OnCancelKeyPress(object? sender, ConsoleCancelEventArgs ev)
        {
            ev.Cancel = true;
            Console.WriteLine("Cancelling...");
            cts.Cancel();
        }
        Console.CancelKeyPress += OnCancelKeyPress;

        try
        {
            var runner = new CliBackupRunner();
            var progress = new ConsoleLineProgress();

            // Run on a thread-pool thread (no captured WPF Dispatcher SynchronizationContext)
            // so blocking on it here -- before the dispatcher message loop has even started
            // pumping -- can't deadlock against any awaited continuation inside the runner.
            return Task.Run(() => runner.RunAsync(origin, destination, progress, cts.Token), cts.Token)
                .GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine("Canceled.");
            return 130;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed: {ex.Message}");
            return 1;
        }
        finally
        {
            Console.CancelKeyPress -= OnCancelKeyPress;
        }
    }

    private sealed class ConsoleLineProgress : IProgress<string>
    {
        public void Report(string value) => Console.WriteLine(value);
    }

    /// <summary>
    /// The app is a WinExe (GUI subsystem) so double-clicking it never flashes a console
    /// window -- but that also means a console launched from cmd/PowerShell has nothing
    /// attached to print to. Attach to the parent console if there is one (the normal
    /// case when run from a terminal); fall back to allocating a fresh console window
    /// otherwise (e.g. launched from Task Scheduler).
    /// </summary>
    private static void AttachOrAllocConsole()
    {
        if (!NativeMethods.AttachConsole(NativeMethods.AttachParentProcess))
        {
            NativeMethods.AllocConsole();
        }
        Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
        Console.SetError(new StreamWriter(Console.OpenStandardError()) { AutoFlush = true });
    }

    private static class NativeMethods
    {
        public const int AttachParentProcess = -1;

        [DllImport("kernel32.dll")]
        public static extern bool AttachConsole(int dwProcessId);

        [DllImport("kernel32.dll")]
        public static extern bool AllocConsole();
    }
}
