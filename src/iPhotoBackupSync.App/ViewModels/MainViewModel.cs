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
    private readonly CloudSyncForceService _cloudSyncForceService = new();
    private readonly CloudSyncStatusProvider _syncStatusProvider = new();
    private readonly BackupManifestService _manifestService = new();
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
    public IAsyncRelayCommand ForceSyncSelectedCommand { get; }
    public IAsyncRelayCommand RefreshSyncStatusCommand { get; }
    public IRelayCommand ExportCsvCommand { get; }
    public IRelayCommand<FileNodeViewModel> RevealInExplorerCommand { get; }
    public IRelayCommand<FileNodeViewModel> CopyPathCommand { get; }
    public IAsyncRelayCommand GenerateManifestCommand { get; }

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
        SortByColumnCommand = new RelayCommand<SortField>(SortByColumn, _ => !IsBusy);
        CopySelectedCommand = new AsyncRelayCommand(RunCopySelectedAsync, CanCopySelected);
        ForceSyncSelectedCommand = new AsyncRelayCommand(RunForceSyncSelectedAsync, CanForceSyncSelected);
        RefreshSyncStatusCommand = new AsyncRelayCommand(RunRefreshSyncStatusAsync, () => !IsBusy && HasResults);
        ExportCsvCommand = new RelayCommand(ExportCsv, () => HasResults);
        RevealInExplorerCommand = new RelayCommand<FileNodeViewModel>(RevealInExplorer);
        CopyPathCommand = new RelayCommand<FileNodeViewModel>(CopyPath);
        GenerateManifestCommand = new AsyncRelayCommand(RunGenerateManifestAsync, () => !IsBusy);

        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(IsBusy) or nameof(OriginPath))
            {
                CompareCommand.NotifyCanExecuteChanged();
                CancelCommand.NotifyCanExecuteChanged();
            }
            if (e.PropertyName is nameof(IsBusy))
            {
                GenerateManifestCommand.NotifyCanExecuteChanged();
            }
            if (e.PropertyName is nameof(IsBusy) or nameof(HasResults))
            {
                CopySelectedCommand.NotifyCanExecuteChanged();
                ForceSyncSelectedCommand.NotifyCanExecuteChanged();
                RefreshSyncStatusCommand.NotifyCanExecuteChanged();
                ExportCsvCommand.NotifyCanExecuteChanged();
                ExpandAllCommand.NotifyCanExecuteChanged();
                CollapseAllCommand.NotifyCanExecuteChanged();
                SelectAllCommand.NotifyCanExecuteChanged();
                ClearSelectionCommand.NotifyCanExecuteChanged();
                SortByColumnCommand.NotifyCanExecuteChanged();
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
                CopySelectedCommand.NotifyCanExecuteChanged();
                ForceSyncSelectedCommand.NotifyCanExecuteChanged();
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
            // The scan itself (directory enumeration, tree construction, sorting) is
            // CPU/IO-bound synchronous work under the hood despite the async signature
            // -- running it via Task.Run keeps it off the UI thread so the window stays
            // responsive. Only IProgress<T> callbacks (already marshaled back to the UI
            // thread since this Progress<T> was constructed here) and the final
            // ObservableCollection update touch the UI thread.
            var originPath = OriginPath;
            var destinationPath = DestinationPath;
            var (root, topLevelNodes) = await Task.Run(async () =>
            {
                var r = await _comparer.CompareAsync(originPath, destinationPath, progress, _cts.Token);
                var nodes = r.Children.Select(c => new FileNodeViewModel(c, null, 0, OnLeafSelectionChanged)).ToList();
                nodes = SortNodesRecursive(nodes);
                return (r, nodes);
            }, _cts.Token);

            _topLevelNodes = topLevelNodes;
            ResultsSummary = $"{root.MissingFileCount:N0} missing file(s), {FormatBytes(root.MissingSizeBytes)} total";
            HasResults = true;
            RefreshView();
            NotifySortGlyphsChanged();

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

    /// <summary>Resolves the current selection down to individual leaf files,
    /// regardless of whether they were selected directly or via a fully-checked
    /// parent folder -- needed to inspect each file's own sync status.</summary>
    private List<FileNodeViewModel> GetSelectedLeafFiles()
    {
        var result = new List<FileNodeViewModel>();
        void Walk(IEnumerable<FileNodeViewModel> nodes)
        {
            foreach (var node in nodes)
            {
                if (node.IsDirectory)
                {
                    if (node.IsSelected == false) continue;
                    Walk(node.Children);
                }
                else if (node.IsSelected == true)
                {
                    result.Add(node);
                }
            }
        }
        Walk(_topLevelNodes);
        return result;
    }

    private bool CanCopySelected()
    {
        if (IsBusy || !HasResults) return false;
        var files = GetSelectedLeafFiles();
        return files.Count > 0 && files.All(f => f.SyncStatus == SyncStatus.Synced);
    }

    private bool CanForceSyncSelected()
    {
        if (IsBusy || !HasResults) return false;
        return GetSelectedLeafFiles().Any(f => f.SyncStatus != SyncStatus.Synced);
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

        // Defense in depth: CanCopySelected already gates the command/button, but
        // re-check here too so a copy can never start against a file that isn't
        // fully synced yet -- backing up a placeholder/partial file would be worse
        // than not backing it up at all.
        var notSynced = GetSelectedLeafFiles().Where(f => f.SyncStatus != SyncStatus.Synced).ToList();
        if (notSynced.Count > 0)
        {
            StatusMessage = $"Copy blocked: {notSynced.Count:N0} of the selected file(s) aren't fully synced with iCloud yet. " +
                             "Use Force Sync (or wait), then Refresh Sync Status, before copying.";
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
            var nodesToCopy = selected.Select(vm => vm.Node).ToList();
            var copiedCount = selected.Count;

            // Run off the UI thread: opening/copying many files (especially over a
            // NAS) can add up even though each individual await yields, and this
            // keeps the window responsive throughout.
            await Task.Run(() => _actionService.CopyToDestinationAsync(
                nodesToCopy, OriginPath, DestinationPath, progress, _cts.Token), _cts.Token);

            StatusMessage = $"Copied {copiedCount:N0} selected item(s) to the destination. Refreshing...";
            await RunCompareAsync();
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

    private async Task RunForceSyncSelectedAsync()
    {
        var targets = GetSelectedLeafFiles().Where(f => f.SyncStatus != SyncStatus.Synced).ToList();
        if (targets.Count == 0)
        {
            StatusMessage = "All selected files are already synced.";
            return;
        }

        IsBusy = true;
        _cts = new CancellationTokenSource();
        var progress = new Progress<string>(msg => ProgressDetail = msg);

        try
        {
            var paths = targets.Select(f => f.Node.FullPath).ToList();
            await Task.Run(() => _cloudSyncForceService.ForceSyncAsync(paths, progress, _cts.Token), _cts.Token);

            // Re-check right away: this typically flips a file from "Not synced" to
            // "Refreshing" once iCloud picks up the request, confirming it actually
            // started rather than leaving stale status on screen.
            await Task.Run(() =>
            {
                foreach (var target in targets)
                {
                    target.Node.SyncStatus = _syncStatusProvider.GetStatus(target.Node.FullPath);
                }
            }, _cts.Token);

            foreach (var target in targets)
            {
                target.NotifySyncStatusChanged();
            }

            StatusMessage = $"Requested a fresh iCloud sync for {targets.Count:N0} file(s). " +
                             "Use Refresh Sync Status again in a moment to see when they're done.";
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Force sync canceled.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Force sync failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
            ProgressDetail = string.Empty;
            _cts = null;
            CopySelectedCommand.NotifyCanExecuteChanged();
            ForceSyncSelectedCommand.NotifyCanExecuteChanged();
        }
    }

    private async Task RunRefreshSyncStatusAsync()
    {
        var allFiles = new List<FileNodeViewModel>();
        void Walk(IEnumerable<FileNodeViewModel> nodes)
        {
            foreach (var node in nodes)
            {
                if (node.IsDirectory) Walk(node.Children);
                else allFiles.Add(node);
            }
        }
        Walk(_topLevelNodes);

        if (allFiles.Count == 0)
        {
            StatusMessage = "No missing files to check.";
            return;
        }

        IsBusy = true;
        _cts = new CancellationTokenSource();
        ProgressDetail = $"Checking sync status for {allFiles.Count:N0} file(s)...";

        try
        {
            await Task.Run(() =>
            {
                Parallel.ForEach(allFiles,
                    new ParallelOptions { CancellationToken = _cts.Token, MaxDegreeOfParallelism = 8 },
                    f => f.Node.SyncStatus = _syncStatusProvider.GetStatus(f.Node.FullPath));
            }, _cts.Token);

            foreach (var f in allFiles)
            {
                f.NotifySyncStatusChanged();
            }

            StatusMessage = "Sync status refreshed.";
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Refresh canceled.";
        }
        finally
        {
            IsBusy = false;
            ProgressDetail = string.Empty;
            _cts = null;
            CopySelectedCommand.NotifyCanExecuteChanged();
            ForceSyncSelectedCommand.NotifyCanExecuteChanged();
        }
    }

    private async Task RunGenerateManifestAsync()
    {
        var folder = BrowseForFolder(DestinationPath);
        if (folder is null) return;

        IsBusy = true;
        _cts = new CancellationTokenSource();
        var progress = new Progress<int>(count => ProgressDetail = $"Scanning existing files... {count:N0} found so far");

        try
        {
            var count = await Task.Run(
                () => _manifestService.GenerateManifestAsync(folder, progress, _cts.Token), _cts.Token);

            StatusMessage = count == 0
                ? $"No files found in \"{folder}\" -- no manifest was written."
                : $"Recorded {count:N0} file(s) already in \"{folder}\" as backed up ({BackupManifestService.ManifestFileName}). " +
                  "Compare will now treat those files as present even if they're later moved elsewhere.";
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Manifest generation canceled.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Manifest generation failed: {ex.Message}";
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
