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

    /// <summary>
    /// Flattens a chapter tree to navigable nodes only, sorted by (PageIndex, Depth, SourceOrder)
    /// so a binary search for the last entry with PageIndex &lt;= page yields the deepest/later node.
    /// </summary>
    public static List<ChapterItem> FlattenNavigable(List<ChapterItem> roots)
    {
        var list = new List<ChapterItem>();
        var stack = new Stack<ChapterItem>();
        for (int i = roots.Count - 1; i >= 0; i--)
            stack.Push(roots[i]);

        while (stack.Count > 0)
        {
            var node = stack.Pop();
            if (node.PageIndex.HasValue)
                list.Add(node);
            for (int i = node.Children.Count - 1; i >= 0; i--)
                stack.Push(node.Children[i]);
        }

        list.Sort((a, b) =>
        {
            int byPage = a.PageIndex!.Value.CompareTo(b.PageIndex!.Value);
            if (byPage != 0) return byPage;
            int byDepth = a.Depth.CompareTo(b.Depth);
            return byDepth != 0 ? byDepth : a.SourceOrder.CompareTo(b.SourceOrder);
        });
        return list;
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
