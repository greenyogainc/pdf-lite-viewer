using System.ComponentModel;

namespace PdfLiteViewer;

/// <summary>One node in the chapter tree, mapped 1:1 from a PDF outline/bookmark node.
/// <see cref="PageIndex"/> is set only for internal destinations of the current document;
/// container, URI, external-file, and embedded-file nodes stay in the tree but do not navigate.
/// Children is a plain list — the outline is immutable after load (avoids ObservableCollection
/// overhead on large bookmarks trees).</summary>
public sealed class ChapterItem : INotifyPropertyChanged
{
    private bool _isExpanded;
    private bool _isSelected;

    public string Title { get; init; } = "";

    /// <summary>0-based target page, or null when this node has no navigable in-document destination.</summary>
    public int? PageIndex { get; init; }

    public List<ChapterItem> Children { get; } = new();

    public ChapterItem? Parent { get; set; }

    /// <summary>Outline depth: 0 for roots, +1 per nesting level.</summary>
    public int Depth { get; init; }

    /// <summary>Global pre-order sequence number, for deterministic current-chapter tie-breaking.</summary>
    public int SourceOrder { get; init; }

    public bool IsNavigable => PageIndex.HasValue;

    public bool IsExpanded
    {
        get => _isExpanded;
        set { _isExpanded = value; OnChanged(nameof(IsExpanded)); }
    }

    public bool IsSelected
    {
        get => _isSelected;
        set { _isSelected = value; OnChanged(nameof(IsSelected)); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Screen readers / UI Automation announce the node by this name.</summary>
    public override string ToString() => Title;

    private void OnChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
