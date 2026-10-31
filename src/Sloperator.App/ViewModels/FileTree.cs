using System.Collections.ObjectModel;
using Avalonia;
using Sloperator.Core.Projects;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Sloperator.App.ViewModels;

/// <summary>A row of the Code editor panel's tree: a folder, unfolded or not, or a file, as deep as it is in the folder shown.</summary>
public sealed partial class FileTreeRow(string name, string path, bool isFolder, int depth) : ObservableObject
{
    public string Name { get; } = name;
    public string Path { get; } = path;
    public bool IsFolder { get; } = isFolder;
    public int Depth { get; } = depth;

    /// <summary>12 in for each folder it is in.</summary>
    public Thickness Indent => new(Depth * 12, 0, 0, 0);

    [ObservableProperty]
    private bool _isExpanded;

    /// <summary>Edited in the panel and not saved yet.</summary>
    [ObservableProperty]
    private bool _isUnsaved;

    /// <summary>A folder's rows, read when it is first unfolded and again when the disk says it changed; null before.</summary>
    internal List<FileTreeRow>? Children { get; set; }

    public override string ToString() => Name;
}

/// <summary>
/// The Code editor panel's tree of a folder, as one list of rows (a folder's own rows under it while
/// it is unfolded), so it scrolls and selects as File changes' list does. A folder's files are read
/// when it is first unfolded and kept, folded or not, and read again when the disk says it changed.
/// </summary>
/// <param name="isUnsaved">Whether a file has edits not saved yet, for its row's mark.</param>
public sealed class FileTree(Func<string, bool> isUnsaved)
{
    /// <summary>The folder shown; null with none.</summary>
    public string? Folder { get; private set; }

    /// <summary>Every row shown: the folder's files and folders, and those of the folders unfolded under them.</summary>
    public ObservableCollection<FileTreeRow> Rows { get; } = [];

    /// <summary>The folder's own rows.</summary>
    private List<FileTreeRow> _top = [];

    /// <summary>Another folder, folded; none with null.</summary>
    public void Show(string? folder)
    {
        Folder = folder;
        _top = folder is null ? [] : Read(folder, 0);
        Sync();
    }

    /// <summary>A folder unfolded (its files read the first time) or folded.</summary>
    public void Expand(FileTreeRow row, bool expand)
    {
        if (!row.IsFolder || row.IsExpanded == expand)
            return;
        if (expand)
            row.Children ??= Read(row.Path, row.Depth + 1);
        row.IsExpanded = expand;
        Sync();
    }

    /// <summary>The folder a row is in; null for the folder's own rows.</summary>
    public FileTreeRow? Parent(FileTreeRow row)
    {
        for (var i = Rows.IndexOf(row) - 1; i >= 0; i--)
            if (Rows[i].Depth < row.Depth)
                return Rows[i];
        return null;
    }

    /// <summary>The row shown for a path; null when it isn't (in a folded folder, or outside the folder).</summary>
    public FileTreeRow? Find(string path) => Rows.FirstOrDefault(r => string.Equals(r.Path, path, StringComparison.OrdinalIgnoreCase));

    /// <summary>A file's row, the folders it is in unfolded down to it; null when it isn't in the folder.</summary>
    public FileTreeRow? Reveal(string path)
    {
        if (Folder is not { } root || !path.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            return null;
        var at = root;
        foreach (var part in Path.GetRelativePath(root, path).Split(Path.DirectorySeparatorChar)[..^1])
        {
            at = Path.Combine(at, part);
            if (Find(at) is not { IsFolder: true } folder)
                return null;
            Expand(folder, true);
        }
        return Find(path);
    }

    /// <summary>
    /// The folders the disk said changed read again ("*": all of them), those read before, unfolded or
    /// not: rows that are still there are kept as they were (unfolded, with their own rows), new ones
    /// added, gone ones taken out.
    /// </summary>
    public void Refresh(IReadOnlyCollection<string> folders)
    {
        if (Folder is not { } root || folders.Count == 0)
            return;
        var all = folders.Contains("*");
        bool Changed(string path) => all || folders.Any(f => string.Equals(f, path, StringComparison.OrdinalIgnoreCase));
        if (Changed(root))
            _top = Merge(_top, Read(root, 0));
        foreach (var row in Loaded(_top).ToList())
            if (Changed(row.Path))
                row.Children = Merge(row.Children!, Read(row.Path, row.Depth + 1));
        Sync();

        static IEnumerable<FileTreeRow> Loaded(List<FileTreeRow> rows) =>
            rows.Where(r => r.Children is not null).SelectMany(r => Loaded(r.Children!).Prepend(r));
    }

    /// <summary>A folder's rows, folders first, from the disk.</summary>
    private List<FileTreeRow> Read(string folder, int depth) =>
        FolderListing.List(folder).Select(e => new FileTreeRow(e.Name, e.Path, e.IsFolder, depth) { IsUnsaved = !e.IsFolder && isUnsaved(e.Path) }).ToList();

    /// <summary>A folder's rows as read again: those it had are kept, the new ones added.</summary>
    private static List<FileTreeRow> Merge(List<FileTreeRow> had, List<FileTreeRow> now)
    {
        var old = new Dictionary<(string, bool), FileTreeRow>();
        foreach (var row in had)
            old.TryAdd((row.Name, row.IsFolder), row);
        return now.Select(r => old.GetValueOrDefault((r.Name, r.IsFolder)) ?? r).ToList();
    }

    /// <summary>
    /// <see cref="Rows"/> made the tree as it is now by adding and taking out only the rows that differ,
    /// so the list stays scrolled where it was and its selection stays. Both are in the tree's order, so
    /// a row of the list not wanted where a wanted one should be is gone, and a wanted one not there yet
    /// is new.
    /// </summary>
    private void Sync()
    {
        var wanted = new List<FileTreeRow>();
        Walk(_top);
        var keep = wanted.ToHashSet();
        var i = 0;
        foreach (var row in wanted)
        {
            while (i < Rows.Count && Rows[i] != row && !keep.Contains(Rows[i]))
                Rows.RemoveAt(i);
            if (i < Rows.Count && Rows[i] == row)
            {
                i++;
                continue;
            }
            Rows.Insert(i++, row);
        }
        while (Rows.Count > i)
            Rows.RemoveAt(Rows.Count - 1);

        void Walk(List<FileTreeRow> rows)
        {
            foreach (var row in rows)
            {
                wanted.Add(row);
                if (row is { IsExpanded: true, Children: { } children })
                    Walk(children);
            }
        }
    }
}
