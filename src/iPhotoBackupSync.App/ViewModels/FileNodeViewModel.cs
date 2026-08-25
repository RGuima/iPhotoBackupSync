using CommunityToolkit.Mvvm.ComponentModel;
using iPhotoBackupSync.Core.Models;

namespace iPhotoBackupSync.App.ViewModels;

public sealed partial class FileNodeViewModel : ObservableObject
{
    public FileNode Node { get; }
    public FileNodeViewModel? Parent { get; }
    public int Depth { get; }
    public List<FileNodeViewModel> Children { get; } = new();

    public FileNodeViewModel(FileNode node, FileNodeViewModel? parent, int depth)
    {
        Node = node;
        Parent = parent;
        Depth = depth;

        foreach (var child in node.Children)
        {
            Children.Add(new FileNodeViewModel(child, this, depth + 1));
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

    [ObservableProperty]
    private bool _isExpanded;

    private bool? _isSelected;
    public bool? IsSelected
    {
        get => _isSelected;
        set => SetSelected(value, propagateToChildren: true, propagateToParent: true);
    }

    public void SetSelected(bool? value, bool propagateToChildren, bool propagateToParent)
    {
        if (_isSelected == value) return;
        _isSelected = value;
        OnPropertyChanged(nameof(IsSelected));

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
