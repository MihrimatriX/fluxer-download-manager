using Avalonia;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Fluxer.App.ViewModels;

/// A folder or file of a torrent. A folder's check box is indeterminate while only some of its files are selected.
/// Row template: App.axaml.
public sealed partial class TorrentNode : ObservableObject
{
    private readonly TorrentTree _tree;
    private string _size;

    internal TorrentNode(TorrentTree tree, TorrentNode? parent, string name, int index, long length, bool selected)
    {
        _tree = tree;
        Parent = parent;
        Name = name;
        Index = index;
        Length = length;
        Depth = parent is null ? 0 : parent.Depth + 1;
        _isSelected = selected;
        _size = _meta = Loc.Bytes(length);
    }

    public TorrentNode? Parent { get; }
    public List<TorrentNode> Children { get; } = [];
    public string Name { get; }
    /// File index inside the torrent; -1 for folders.
    public int Index { get; }
    public bool IsFolder => Index < 0;
    public long Length { get; private set; }
    public int Depth { get; }
    public Thickness Indent => new(Depth * 18, 0, 0, 0);
    public string Chevron => IsExpanded ? "" : "";

    [ObservableProperty] private bool? _isSelected;
    [ObservableProperty] private bool _isExpanded;
    /// "12 MB" or, once progress is known, "12 MB · 40 %".
    [ObservableProperty] private string _meta;

    internal long Done;

    partial void OnIsSelectedChanged(bool? value) => _tree.Toggled(this);
    partial void OnIsExpandedChanged(bool value)
    {
        OnPropertyChanged(nameof(Chevron));
        _tree.Rebuild();
    }

    [RelayCommand] private void ToggleExpand() => IsExpanded = !IsExpanded;

    internal void SetPercent(double fraction) => Meta = $"{_size} · {Loc.Percent(fraction)}";

    /// Folders are created before their children (which need them as Parent); size and state follow once those exist.
    internal void Finish()
    {
        Length = Children.Sum(c => c.Length);
        _size = Meta = Loc.Bytes(Length);
        IsSelected = TorrentTree.Combine(Children); // the tree is still building, so no Toggled reaction
    }
}

/// Torrent files as a folder tree, shown as a flat list of the rows that are open. The list virtualizes and
/// replaces itself in one reset per expand/collapse, so a 10 000-file torrent costs only its top level
/// until a folder is opened (a TreeView would realize every child of an opened folder).
public sealed class TorrentTree : ObservableObject
{
    private readonly List<TorrentNode> _roots;
    private readonly Dictionary<int, TorrentNode> _files = [];
    private readonly Action _changed;
    private bool _busy = true;

    /// `changed` runs once per user click, after the folder/parent check boxes settled.
    public TorrentTree(IEnumerable<(int Index, string Path, long Length, bool Selected)> files, Action changed)
    {
        _changed = changed;
        _roots = Build(null, [.. files.Select(f => (f.Path.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries), f))], 0);
        Open(_roots);
        _busy = false;
        Rebuild();
    }

    public IReadOnlyList<TorrentNode> Rows { get; private set; } = [];
    public int FileCount => _files.Count;
    public long SelectedBytes => _files.Values.Where(f => f.IsSelected == true).Sum(f => f.Length);
    public HashSet<int> Deselected => [.. _files.Values.Where(f => f.IsSelected != true).Select(f => f.Index)];

    /// Groups by the path part at `depth`: folders first, then files, each by name.
    private List<TorrentNode> Build(TorrentNode? parent, List<(string[] Parts, (int Index, string Path, long Length, bool Selected) File)> items, int depth)
    {
        var nodes = new List<TorrentNode>();
        foreach (var g in items.Where(i => i.Parts.Length > depth + 1).GroupBy(i => i.Parts[depth]).OrderBy(g => g.Key, StringComparer.CurrentCultureIgnoreCase))
        {
            var folder = new TorrentNode(this, parent, g.Key, -1, 0, true);
            folder.Children.AddRange(Build(folder, [.. g], depth + 1));
            folder.Finish();
            nodes.Add(folder);
        }
        foreach (var (parts, f) in items.Where(i => i.Parts.Length <= depth + 1).OrderBy(i => i.Parts.LastOrDefault(), StringComparer.CurrentCultureIgnoreCase))
        {
            var node = new TorrentNode(this, parent, parts.LastOrDefault() ?? f.Path, f.Index, f.Length, f.Selected);
            _files[f.Index] = node;
            nodes.Add(node);
        }
        return nodes;
    }

    private void Open(List<TorrentNode> level)
    {
        foreach (var n in level)
            if (n.IsFolder && ShouldStartOpen(n, level))
            {
                n.IsExpanded = true;
                Open(n.Children);
            }
    }

    /// Which folders are already open when the list first shows. `level` is the list the folder sits in.
    private static bool ShouldStartOpen(TorrentNode folder, IReadOnlyList<TorrentNode> level)
    {
        // A lone folder (the usual "Name/" root, or Name/Season/ chains) opens itself; a huge one stays shut.
        return level.Count == 1 && folder.Children.Count <= 500;
    }

    internal static bool? Combine(List<TorrentNode> children) =>
        children.All(c => c.IsSelected == true) ? true : children.All(c => c.IsSelected == false) ? false : null;

    internal void Rebuild()
    {
        if (_busy) return;
        var rows = new List<TorrentNode>();
        void Add(List<TorrentNode> level)
        {
            foreach (var n in level)
            {
                rows.Add(n);
                if (n.IsExpanded) Add(n.Children);
            }
        }
        Add(_roots);
        Rows = rows;
        OnPropertyChanged(nameof(Rows));
    }

    /// A click on a folder applies to everything under it; then every folder above recomputes its state.
    internal void Toggled(TorrentNode node)
    {
        if (_busy) return;
        _busy = true;
        if (node.IsFolder)
        {
            var value = node.IsSelected ?? false; // a click on an indeterminate box unticks
            node.IsSelected = value;
            SetAll(node.Children, value);
        }
        Settle(node);
        _busy = false;
        _changed();
    }

    private static void SetAll(List<TorrentNode> level, bool value)
    {
        foreach (var n in level)
        {
            n.IsSelected = value;
            SetAll(n.Children, value);
        }
    }

    private static void Settle(TorrentNode node)
    {
        for (var p = node.Parent; p is not null; p = p.Parent) p.IsSelected = Combine(p.Children);
    }

    /// Ticks one file back without raising `changed` (used to keep at least one file selected).
    public void Select(int index)
    {
        if (!_files.TryGetValue(index, out var f)) return;
        _busy = true;
        f.IsSelected = true;
        Settle(f);
        _busy = false;
    }

    /// Per-file progress (0–100) by torrent file index; folders show their size-weighted total.
    public void SetPercents(IEnumerable<(int Index, double Percent)> files)
    {
        foreach (var (i, p) in files)
            if (_files.TryGetValue(i, out var f)) f.Done = (long)(f.Length * p / 100);
        foreach (var n in _roots) Sum(n);

        static long Sum(TorrentNode n)
        {
            if (n.IsFolder) n.Done = n.Children.Sum(Sum);
            n.SetPercent(n.Length == 0 ? 1 : n.Done / (double)n.Length);
            return n.Done;
        }
    }
}
