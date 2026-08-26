using CommunityToolkit.Mvvm.ComponentModel;
using iPhotoBackupSync.Core.Models;

namespace iPhotoBackupSync.App.ViewModels;

public sealed partial class FileNodeViewModel : ObservableObject
{
    public FileNode Node { get; }
    public FileNodeViewModel? Parent { get; }
    public int Depth { get; }
    public List<FileNodeViewModel> Children { get; } = new();

    private readonly Action<FileNodeViewModel, bool?, bool?>? _onLeafSelectionChanged;

    public FileNodeViewModel(FileNode node, FileNodeViewModel? parent, int depth,
        Action<FileNodeViewModel, bool?, bool?>? onLeafSelectionChanged = null)
    {
        Node = node;
        Parent = parent;
        Depth = depth;
        _onLeafSelectionChanged = onLeafSelectionChanged;

        foreach (var child in node.Children)
        {
            Children.Add(new FileNodeViewModel(child, this, depth + 1, onLeafSelectionChanged));
        }
    }

    public bool IsDirectory => Node.IsDirectory;
    public string Name => Node.Name;
    public string RelativePath => Node.RelativePath;
    public bool HasChildren => Children.Count > 0;

    public string SizeDisplay => Node.IsDirectory
        ? FormatBytes(Node.MissingSizeBytes) + " missing"
        : FormatBytes(Node.SizeBytes);

    public string LastModifiedDisplay => Node.LastModifiedUtc?.ToLocalTime().ToString("g") ?? string.Empty;

    public string MissingSummary => Node.IsDirectory
        ? $"{Node.MissingFileCount:N0} missing file(s)"
        : string.Empty;

    public SyncStatus SyncStatus => Node.SyncStatus;

    /// <summary>Raises change notification for <see cref="SyncStatus"/> after
    /// <see cref="Node"/>'s SyncStatus has been updated out-of-band (e.g. by a
    /// force-sync or manual status refresh), since it isn't itself observable.</summary>
    public void NotifySyncStatusChanged() => OnPropertyChanged(nameof(SyncStatus));

    [ObservableProperty]
    private bool _isExpanded;

    // Starts unchecked (false), not null/indeterminate -- null is reserved for a
    // directory whose children are only partially selected. Defaulting a fresh
    // node to null would make a tri-state CheckBox's *first* click land on
    // "unchecked" (the automation/click cycle is indeterminate -> unchecked ->
    // checked), forcing a confusing second click just to actually select it.
    private bool? _isSelected = false;
    public bool? IsSelected
    {
        get => _isSelected;
        set => SetSelected(value, propagateToChildren: true, propagateToParent: true);
    }

    public void SetSelected(bool? value, bool propagateToChildren, bool propagateToParent)
    {
        if (_isSelected == value) return;
        var oldValue = _isSelected;
        _isSelected = value;
        OnPropertyChanged(nameof(IsSelected));

        if (!IsDirectory)
        {
            _onLeafSelectionChanged?.Invoke(this, oldValue, value);
        }

        if (propagateToChildren && value.HasValue)
        {
            foreach (var child in Children)
            {
                child.SetSelected(value, true, false);
            }
        }

        if (propagateToParent)
        {
            Parent?.UpdateSelectionFromChildren();
        }
    }

    private void UpdateSelectionFromChildren()
    {
        if (Children.Count == 0) return;

        bool? consensus = Children[0].IsSelected;
        foreach (var child in Children)
        {
            if (child.IsSelected != consensus)
            {
                consensus = null;
                break;
            }
        }

        if (_isSelected != consensus)
        {
            _isSelected = consensus;
            OnPropertyChanged(nameof(IsSelected));
        }

        Parent?.UpdateSelectionFromChildren();
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
