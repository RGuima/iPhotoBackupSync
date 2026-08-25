using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using iPhotoBackupSync.App.Services;
using iPhotoBackupSync.Core.Models;
using iPhotoBackupSync.Core.Services;

namespace iPhotoBackupSync.App.ViewModels;

public sealed partial class MainViewModel : ObservableObject
{
    private readonly FolderComparer _comparer = new();
    private readonly FileActionService _actionService = new();
    private CancellationTokenSource? _cts;
    private List<FileNodeViewModel> _topLevelNodes = new();
    private List<SortCriterion> _sortCriteria = new() { new SortCriterion(SortField.Name, false) };

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

    [ObservableProperty]
    private string _filterText = string.Empty;

    [ObservableProperty]
    private DateTime? _filterDateFrom;

    [ObservableProperty]
    private DateTime? _filterDateTo;

    [ObservableProperty]
    private bool _filterSyncRefreshing;

    [ObservableProperty]
    private bool _filterSyncSynced;

    [ObservableProperty]
    private bool _filterSyncNotSynced;

    [ObservableProperty]
    private bool _filterSyncError;

    [ObservableProperty]
    private int _selectedCount;

    [ObservableProperty]
    private long _selectedSizeBytes;

    public string SelectionSummary => SelectedCount == 0
        ? "No items selected"
        : $"{SelectedCount:N0} selected ({FormatBytes(SelectedSizeBytes)})";

    public string NameSortGlyph => GetSortGlyph(SortField.Name);
    public string SizeSortGlyph => GetSortGlyph(SortField.Size);
    public string LastModifiedSortGlyph => GetSortGlyph(SortField.LastModified);
    public string SyncStatusSortGlyph => GetSortGlyph(SortField.SyncStatus);

    public IRelayCommand BrowseOriginCommand { get; }
    public IRelayCommand BrowseDestinationCommand { get; }
    public IAsyncRelayCommand CompareCommand { get; }
    public IRelayCommand CancelCommand { get; }
    public IRelayCommand<FileNodeViewModel> ToggleExpandCommand { get; }
    public IRelayCommand ExpandAllCommand { get; }
    public IRelayCommand CollapseAllCommand { get; }
    public IRelayCommand SelectAllCommand { get; }
    public IRelayCommand ClearSelectionCommand { get; }
    public IRelayCommand ClearFiltersCommand { get; }
    public IRelayCommand<SortField> SortByColumnCommand { get; }
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
        ExpandAllCommand = new RelayCommand(() => SetAllExpanded(true), () => HasResults);
        CollapseAllCommand = new RelayCommand(() => SetAllExpanded(false), () => HasResults);
        SelectAllCommand = new RelayCommand(() => SetSelectionRecursive(_topLevelNodes, true), () => HasResults);
        ClearSelectionCommand = new RelayCommand(() => SetSelectionRecursive(_topLevelNodes, false), () => HasResults);
        ClearFiltersCommand = new RelayCommand(() =>
        {
            FilterText = string.Empty;
            FilterDateFrom = null;
            FilterDateTo = null;
            FilterSyncRefreshing = false;
            FilterSyncSynced = false;
            FilterSyncNotSynced = false;
            FilterSyncError = false;
        });
        SortByColumnCommand = new RelayCommand<SortField>(SortByColumn);
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
                ExpandAllCommand.NotifyCanExecuteChanged();
                CollapseAllCommand.NotifyCanExecuteChanged();
                SelectAllCommand.NotifyCanExecuteChanged();
                ClearSelectionCommand.NotifyCanExecuteChanged();
            }
            if (e.PropertyName is nameof(FilterText) or nameof(FilterDateFrom) or nameof(FilterDateTo)
                or nameof(FilterSyncRefreshing) or nameof(FilterSyncSynced) or nameof(FilterSyncNotSynced) or nameof(FilterSyncError))
            {
                OnPropertyChanged(nameof(IsFilterActive));
                RefreshView();
            }
            if (e.PropertyName is nameof(SelectedCount) or nameof(SelectedSizeBytes))
            {
                OnPropertyChanged(nameof(SelectionSummary));
            }
        };

        var settings = AppSettingsService.Load();
        if (!string.IsNullOrWhiteSpace(settings.OriginPath)) OriginPath = settings.OriginPath;
        if (!string.IsNullOrWhiteSpace(settings.DestinationPath)) DestinationPath = settings.DestinationPath;
    }

    public void SaveSettings()
    {
        AppSettingsService.Save(new AppSettings { OriginPath = OriginPath, DestinationPath = DestinationPath });
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
        SelectedCount = 0;
        SelectedSizeBytes = 0;
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
            SaveSettings();
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
        _topLevelNodes = root.Children.Select(c => new FileNodeViewModel(c, null, 0, OnLeafSelectionChanged)).ToList();
        ApplySort();

        ResultsSummary = $"{root.MissingFileCount:N0} missing file(s), {FormatBytes(root.MissingSizeBytes)} total";
        HasResults = true;

        RefreshView();
        NotifySortGlyphsChanged();
    }

    // --- Expand / collapse -------------------------------------------------

    private void ToggleExpand(FileNodeViewModel? node)
    {
        if (node is null || !node.HasChildren || IsFilterActive) return;

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
        void SetFlags(IEnumerable<FileNodeViewModel> nodes)
        {
            foreach (var node in nodes)
            {
                node.IsExpanded = expanded && node.HasChildren;
                SetFlags(node.Children);
            }
        }
        SetFlags(_topLevelNodes);
        RefreshView();
    }

    // --- Selection -----------------------------------------------------------

    private void OnLeafSelectionChanged(FileNodeViewModel node, bool? oldValue, bool? newValue)
    {
        if (oldValue == true)
        {
            SelectedCount--;
            SelectedSizeBytes -= node.Node.SizeBytes;
        }
        if (newValue == true)
        {
            SelectedCount++;
            SelectedSizeBytes += node.Node.SizeBytes;
        }
    }

    private void SetSelectionRecursive(IEnumerable<FileNodeViewModel> nodes, bool value)
    {
        foreach (var node in nodes)
        {
            if (node.IsDirectory)
            {
                SetSelectionRecursive(node.Children, value);
            }
            else if (!IsFilterActive || NodeMatchesFilter(node))
            {
                node.SetSelected(value, propagateToChildren: false, propagateToParent: true);
            }
        }
    }

    // --- Filtering -------------------------------------------------------

    private bool AnySyncFilterActive => FilterSyncRefreshing || FilterSyncSynced || FilterSyncNotSynced || FilterSyncError;

    public bool IsFilterActive => !string.IsNullOrWhiteSpace(FilterText) || FilterDateFrom.HasValue || FilterDateTo.HasValue || AnySyncFilterActive;

    private bool SyncStatusMatches(SyncStatus status)
    {
        if (!AnySyncFilterActive) return true;
        return status switch
        {
            SyncStatus.Refreshing => FilterSyncRefreshing,
            SyncStatus.Synced => FilterSyncSynced,
            SyncStatus.NotSynced => FilterSyncNotSynced,
            SyncStatus.Error => FilterSyncError,
            _ => true
        };
    }

    private bool NodeMatchesFilter(FileNodeViewModel node)
    {
        var nameOk = string.IsNullOrWhiteSpace(FilterText) ||
                     node.Name.Contains(FilterText, StringComparison.OrdinalIgnoreCase);
        if (!nameOk) return false;

        if (!SyncStatusMatches(node.SyncStatus)) return false;

        if (FilterDateFrom.HasValue || FilterDateTo.HasValue)
        {
            var lastModified = node.Node.LastModifiedUtc?.ToLocalTime().Date;
            if (lastModified is null) return false;
            if (FilterDateFrom.HasValue && lastModified < FilterDateFrom.Value.Date) return false;
            if (FilterDateTo.HasValue && lastModified > FilterDateTo.Value.Date) return false;
        }
        return true;
    }

    private bool BuildFilteredList(IEnumerable<FileNodeViewModel> nodes, List<FileNodeViewModel> output)
    {
        var anyMatch = false;
        foreach (var node in nodes)
        {
            if (node.IsDirectory)
            {
                var folderNameMatches = !string.IsNullOrWhiteSpace(FilterText) &&
                                         node.Name.Contains(FilterText, StringComparison.OrdinalIgnoreCase);

                if (folderNameMatches && !FilterDateFrom.HasValue && !FilterDateTo.HasValue && !AnySyncFilterActive)
                {
                    // The folder itself matches by name and there's no other constraint:
                    // show it and everything inside without filtering further.
                    output.Add(node);
                    AddAllDescendants(node, output);
                    anyMatch = true;
                    continue;
                }

                var childOutput = new List<FileNodeViewModel>();
                if (BuildFilteredList(node.Children, childOutput))
                {
                    output.Add(node);
                    output.AddRange(childOutput);
                    anyMatch = true;
                }
            }
            else if (NodeMatchesFilter(node))
            {
                output.Add(node);
                anyMatch = true;
            }
        }
        return anyMatch;
    }

    private static void AddAllDescendants(FileNodeViewModel node, List<FileNodeViewModel> output)
    {
        foreach (var child in node.Children)
        {
            output.Add(child);
            if (child.IsDirectory) AddAllDescendants(child, output);
        }
    }

    // --- Sorting -----------------------------------------------------------

    private void SortByColumn(SortField field)
    {
        var additive = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        var existing = _sortCriteria.FirstOrDefault(c => c.Field == field);

        if (!additive)
        {
            if (_sortCriteria.Count == 1 && existing != null)
            {
                existing.Descending = !existing.Descending;
            }
            else
            {
                _sortCriteria = new List<SortCriterion> { new(field, existing?.Descending ?? false) };
            }
        }
        else if (existing != null)
        {
            existing.Descending = !existing.Descending;
        }
        else
        {
            _sortCriteria.Add(new SortCriterion(field, false));
        }

        ApplySort();
        RefreshView();
        NotifySortGlyphsChanged();
    }

    private void NotifySortGlyphsChanged()
    {
        OnPropertyChanged(nameof(NameSortGlyph));
        OnPropertyChanged(nameof(SizeSortGlyph));
        OnPropertyChanged(nameof(LastModifiedSortGlyph));
        OnPropertyChanged(nameof(SyncStatusSortGlyph));
    }

    private string GetSortGlyph(SortField field)
    {
        var index = _sortCriteria.FindIndex(c => c.Field == field);
        if (index < 0) return string.Empty;
        var arrow = _sortCriteria[index].Descending ? "▼" : "▲";
        return _sortCriteria.Count > 1 ? $"{arrow}{index + 1}" : arrow;
    }

    private void ApplySort()
    {
        if (_topLevelNodes.Count == 0) return;
        _topLevelNodes = SortNodesRecursive(_topLevelNodes);
    }

    private List<FileNodeViewModel> SortNodesRecursive(List<FileNodeViewModel> nodes)
    {
        var sorted = SortNodes(nodes);
        foreach (var node in sorted)
        {
            if (node.Children.Count > 0)
            {
                var sortedChildren = SortNodesRecursive(node.Children);
                node.Children.Clear();
                node.Children.AddRange(sortedChildren);
            }
        }
        return sorted;
    }

    private List<FileNodeViewModel> SortNodes(List<FileNodeViewModel> nodes)
    {
        var query = nodes.OrderBy(n => n.IsDirectory ? 0 : 1);
        foreach (var criterion in _sortCriteria)
        {
            query = ApplyCriterion(query, criterion);
        }
        return query.ToList();
    }

    private static IOrderedEnumerable<FileNodeViewModel> ApplyCriterion(IOrderedEnumerable<FileNodeViewModel> query, SortCriterion c)
    {
        return c.Field switch
        {
            SortField.Size => c.Descending
                ? query.ThenByDescending(n => n.IsDirectory ? n.Node.MissingSizeBytes : n.Node.SizeBytes)
                : query.ThenBy(n => n.IsDirectory ? n.Node.MissingSizeBytes : n.Node.SizeBytes),
            SortField.LastModified => c.Descending
                ? query.ThenByDescending(n => n.Node.LastModifiedUtc ?? DateTime.MinValue)
                : query.ThenBy(n => n.Node.LastModifiedUtc ?? DateTime.MinValue),
            SortField.SyncStatus => c.Descending
                ? query.ThenByDescending(n => (int)n.SyncStatus)
                : query.ThenBy(n => (int)n.SyncStatus),
            _ => c.Descending
                ? query.ThenByDescending(n => n.Name, StringComparer.OrdinalIgnoreCase)
                : query.ThenBy(n => n.Name, StringComparer.OrdinalIgnoreCase)
        };
    }

    // --- View rebuilding ---------------------------------------------------

    private void RefreshView()
    {
        if (IsFilterActive)
        {
            var output = new List<FileNodeViewModel>();
            BuildFilteredList(_topLevelNodes, output);
            FlattenedItems.Clear();
            foreach (var n in output) FlattenedItems.Add(n);
        }
        else
        {
            RebuildNormalView();
        }
    }

    private void RebuildNormalView()
    {
        FlattenedItems.Clear();
        void Walk(IEnumerable<FileNodeViewModel> nodes)
        {
            foreach (var node in nodes)
            {
                FlattenedItems.Add(node);
                if (node.IsExpanded) Walk(node.Children);
            }
        }
        Walk(_topLevelNodes);
    }

    // --- Actions -------------------------------------------------------------

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
