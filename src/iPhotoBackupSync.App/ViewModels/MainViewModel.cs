using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using iPhotoBackupSync.Core.Models;
using iPhotoBackupSync.Core.Services;

namespace iPhotoBackupSync.App.ViewModels;

public sealed partial class MainViewModel : ObservableObject
{
    private readonly FolderComparer _comparer = new();
    private readonly FileActionService _actionService = new();
    private CancellationTokenSource? _cts;
    private List<FileNodeViewModel> _topLevelNodes = new();

    public ObservableCollection<FileNodeViewModel> FlattenedItems { get; } = new();

    [ObservableProperty]
    private string _originPath = string.Empty;

    [ObservableProperty]
    private string _destinationPath = string.Empty;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _statusMessage = "Choose an origin (e.g. your iCloud Photos folder) and a destination (backup) folder, then Compare.";

    [ObservableProperty]
    private string _progressDetail = string.Empty;

    [ObservableProperty]
    private string _resultsSummary = string.Empty;

    [ObservableProperty]
    private bool _hasResults;

    public IRelayCommand BrowseOriginCommand { get; }
    public IRelayCommand BrowseDestinationCommand { get; }
    public IAsyncRelayCommand CompareCommand { get; }
    public IRelayCommand CancelCommand { get; }
    public IRelayCommand<FileNodeViewModel> ToggleExpandCommand { get; }
    public IRelayCommand ExpandAllCommand { get; }
    public IRelayCommand CollapseAllCommand { get; }
    public IAsyncRelayCommand CopySelectedCommand { get; }
    public IRelayCommand ExportCsvCommand { get; }
    public IRelayCommand<FileNodeViewModel> RevealInExplorerCommand { get; }
    public IRelayCommand<FileNodeViewModel> CopyPathCommand { get; }

    public MainViewModel()
    {
        BrowseOriginCommand = new RelayCommand(() => OriginPath = BrowseForFolder(OriginPath) ?? OriginPath);
        BrowseDestinationCommand = new RelayCommand(() => DestinationPath = BrowseForFolder(DestinationPath) ?? DestinationPath);
        CompareCommand = new AsyncRelayCommand(RunCompareAsync, () => !IsBusy && Directory.Exists(OriginPath));
        CancelCommand = new RelayCommand(() => _cts?.Cancel(), () => IsBusy);
        ToggleExpandCommand = new RelayCommand<FileNodeViewModel>(ToggleExpand);
        ExpandAllCommand = new RelayCommand(() => SetAllExpanded(true));
        CollapseAllCommand = new RelayCommand(() => SetAllExpanded(false));
        CopySelectedCommand = new AsyncRelayCommand(RunCopySelectedAsync, () => !IsBusy && HasResults);
        ExportCsvCommand = new RelayCommand(ExportCsv, () => HasResults);
        RevealInExplorerCommand = new RelayCommand<FileNodeViewModel>(RevealInExplorer);
        CopyPathCommand = new RelayCommand<FileNodeViewModel>(CopyPath);

        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(IsBusy) or nameof(OriginPath))
            {
                CompareCommand.NotifyCanExecuteChanged();
                CancelCommand.NotifyCanExecuteChanged();
            }
            if (e.PropertyName is nameof(IsBusy) or nameof(HasResults))
            {
                CopySelectedCommand.NotifyCanExecuteChanged();
                ExportCsvCommand.NotifyCanExecuteChanged();
            }
        };
    }

    private static string? BrowseForFolder(string startPath)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Select a folder (local drive or NAS/UNC path)",
            Multiselect = false
        };
        if (Directory.Exists(startPath))
        {
            dialog.InitialDirectory = startPath;
        }
        return dialog.ShowDialog() == true ? dialog.FolderName : null;
    }

    private async Task RunCompareAsync()
    {
        if (!Directory.Exists(OriginPath))
        {
            StatusMessage = "Origin folder does not exist.";
            return;
        }

        IsBusy = true;
        HasResults = false;
        FlattenedItems.Clear();
        _topLevelNodes.Clear();
        StatusMessage = "Comparing...";
        _cts = new CancellationTokenSource();

        var progress = new Progress<CompareProgress>(p =>
        {
            ProgressDetail = p.Phase switch
            {
                ComparePhase.IndexingDestination => $"Indexing destination... {p.DirectoriesScanned:N0} folders",
                ComparePhase.ScanningOrigin => $"Scanning origin... {p.FilesScanned:N0} files, {p.DirectoriesScanned:N0} folders, {p.MissingFilesFound:N0} missing so far",
                ComparePhase.CheckingSyncStatus => $"Checking iCloud sync status for missing files...",
                ComparePhase.Done => "Done.",
                _ => string.Empty
            };
        });

        try
        {
            var root = await _comparer.CompareAsync(OriginPath, DestinationPath, progress, _cts.Token);
            PopulateResults(root);
            StatusMessage = root.MissingFileCount == 0
                ? "No differences found: everything in the origin folder already exists in the destination."
                : "Comparison complete.";
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Comparison canceled.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Comparison failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
            ProgressDetail = string.Empty;
            _cts = null;
        }
    }

    private void PopulateResults(FileNode root)
    {
        _topLevelNodes = root.Children.Select(c => new FileNodeViewModel(c, null, 0)).ToList();

        FlattenedItems.Clear();
        foreach (var node in _topLevelNodes)
        {
            FlattenedItems.Add(node);
        }

        ResultsSummary = $"{root.MissingFileCount:N0} missing file(s), {FormatBytes(root.MissingSizeBytes)} total";
        HasResults = true;
    }

    private void ToggleExpand(FileNodeViewModel? node)
    {
        if (node is null || !node.HasChildren) return;

        if (node.IsExpanded)
        {
            Collapse(node);
        }
        else
        {
            Expand(node);
        }
    }

    private void Expand(FileNodeViewModel node)
    {
        node.IsExpanded = true;
        var index = FlattenedItems.IndexOf(node);
        if (index < 0) return;

        int insertAt = index + 1;
        InsertVisibleDescendants(node, ref insertAt);
    }

    private void InsertVisibleDescendants(FileNodeViewModel node, ref int insertAt)
    {
        foreach (var child in node.Children)
        {
            FlattenedItems.Insert(insertAt++, child);
            if (child.IsExpanded)
            {
                InsertVisibleDescendants(child, ref insertAt);
            }
        }
    }

    private void Collapse(FileNodeViewModel node)
    {
        node.IsExpanded = false;
        var index = FlattenedItems.IndexOf(node);
        if (index < 0) return;

        int removeStart = index + 1;
        int count = 0;
        while (removeStart + count < FlattenedItems.Count && FlattenedItems[removeStart + count].Depth > node.Depth)
        {
            count++;
        }
        for (int i = 0; i < count; i++)
        {
            FlattenedItems.RemoveAt(removeStart);
        }
    }

    private void SetAllExpanded(bool expanded)
    {
        FlattenedItems.Clear();
        void Rebuild(IEnumerable<FileNodeViewModel> nodes)
        {
            foreach (var node in nodes)
            {
                node.IsExpanded = expanded && node.HasChildren;
                FlattenedItems.Add(node);
                if (node.IsExpanded)
                {
                    Rebuild(node.Children);
                }
            }
        }
        Rebuild(_topLevelNodes);
    }

    private async Task RunCopySelectedAsync()
    {
        var selected = GetTopSelectedNodes();
        if (selected.Count == 0)
        {
            StatusMessage = "Select at least one file or folder to copy.";
            return;
        }

        if (!Directory.Exists(DestinationPath))
        {
            var result = System.Windows.MessageBox.Show(
                $"Destination folder does not exist yet:\n{DestinationPath}\n\nCreate it?",
                "Create destination folder", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Question);
            if (result != System.Windows.MessageBoxResult.Yes) return;
            Directory.CreateDirectory(DestinationPath);
        }

        IsBusy = true;
        _cts = new CancellationTokenSource();
        var progress = new Progress<(int copied, int total, string currentPath)>(p =>
        {
            ProgressDetail = $"Copying {p.copied:N0} / {p.total:N0}: {p.currentPath}";
        });

        try
        {
            await _actionService.CopyToDestinationAsync(
                selected.Select(vm => vm.Node), OriginPath, DestinationPath, progress, _cts.Token);
            StatusMessage = $"Copied {selected.Count:N0} selected item(s) to the destination. Re-run Compare to refresh the list.";
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Copy canceled.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Copy failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
            ProgressDetail = string.Empty;
            _cts = null;
        }
    }

    private List<FileNodeViewModel> GetTopSelectedNodes()
    {
        var result = new List<FileNodeViewModel>();
        void Walk(IEnumerable<FileNodeViewModel> nodes)
        {
            foreach (var node in nodes)
            {
                if (node.IsSelected == true)
                {
                    result.Add(node);
                }
                else
                {
                    Walk(node.Children);
                }
            }
        }
        Walk(_topLevelNodes);
        return result;
    }

    private void ExportCsv()
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "CSV files (*.csv)|*.csv",
            FileName = "missing-files.csv"
        };
        if (dialog.ShowDialog() != true) return;

        var allNodes = new List<FileNode>();
        void Walk(IEnumerable<FileNodeViewModel> nodes)
        {
            foreach (var node in nodes)
            {
                allNodes.Add(node.Node);
                Walk(node.Children);
            }
        }
        Walk(_topLevelNodes);

        try
        {
            _actionService.ExportToCsv(allNodes, dialog.FileName);
            StatusMessage = $"Exported to {dialog.FileName}";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Export failed: {ex.Message}";
        }
    }

    private static void RevealInExplorer(FileNodeViewModel? node)
    {
        if (node is null) return;
        try
        {
            Process.Start("explorer.exe", $"/select,\"{node.Node.FullPath}\"");
        }
        catch
        {
            // Best effort only.
        }
    }

    private static void CopyPath(FileNodeViewModel? node)
    {
        if (node is null) return;
        try
        {
            System.Windows.Clipboard.SetText(node.Node.FullPath);
        }
        catch
        {
            // Clipboard access can transiently fail; not critical.
        }
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double size = bytes;
        int unit = 0;
        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }
        return unit == 0 ? $"{size:N0} {units[unit]}" : $"{size:N1} {units[unit]}";
    }
}
