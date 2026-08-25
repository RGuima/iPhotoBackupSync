namespace iPhotoBackupSync.App.ViewModels;

/// <summary>One level of a (possibly multi-column) sort.</summary>
public sealed class SortCriterion
{
    public SortField Field { get; }
    public bool Descending { get; set; }

    public SortCriterion(SortField field, bool descending)
    {
        Field = field;
        Descending = descending;
    }
}
