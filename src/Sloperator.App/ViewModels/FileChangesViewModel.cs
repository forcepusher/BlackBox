using System.Collections.Specialized;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Avalonia.Controls.Selection;
using Avalonia.Threading;
using Sloperator.Core.Diffs;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using static Sloperator.Core.Strings;

namespace Sloperator.App.ViewModels;

/// <summary>A file in the panel's list of changed files: a click there goes to it in the diff (Row, -1 while its section is folded).</summary>
public sealed partial class DiffFileItem(DiffFile file, int row, string fullPath) : ObservableObject
{
    public DiffFile File { get; } = file;
    public int Row { get; } = row;
    public string FullPath { get; } = fullPath;

    /// <summary>Some of its lines are picked: the list shows it grey, not as a whole file selected (the user, 2026-10-28).</summary>
    [ObservableProperty]
    private bool _hasPickedLines;

    public string Name => File.Display;
    public string Status => File.Status;
    public string Counts => File.Counts;

    /// <summary>Its row's tooltip: the full path and the status ("modified", "renamed"), which the one-line row leaves out.</summary>
    public string Tip => $"{FullPath}\n{Status}";

    /// <summary>The counts in three parts, so "+10" shows green and "-3" red: "+10", " -3" (with its space after an
    /// added count), and "binary" or "no lines" when there is neither.</summary>
    public string AddedCount => File.Binary || File.Added == 0 ? "" : $"+{File.Added}";
    public string RemovedCount => File.Binary || File.Removed == 0 ? "" : (AddedCount.Length > 0 ? " -" : "-") + File.Removed;
    public string OtherCount => AddedCount.Length + RemovedCount.Length == 0 ? File.Counts : "";
    public bool IsNew => File.Status == "added";
    public bool IsDeleted => File.Status == "deleted";

    /// <summary>Its section in a git repository; null outside one.</summary>
    public GitSection? Section => File.Git?.Section;

    /// <summary>"unstaged", "staged", "committed"; "" outside git.</summary>
    public string SectionWord => Section?.ToString().ToLowerInvariant() ?? "";

    /// <summary>A zero for each digit of the widest line number in its diff: an unseen copy sizes the number column to it.</summary>
    public string NumberSizer { get; } = new('0', file.Lines.Select(l => l.New ?? l.Old ?? 0).DefaultIfEmpty().Max().ToString(CultureInfo.InvariantCulture).Length);

    /// <summary>The same file in the same section (the diff in it may have changed).</summary>
    internal bool IsSame(DiffFileItem? other) => other is not null && Section == other.Section && string.Equals(FullPath, other.FullPath, StringComparison.OrdinalIgnoreCase);

    public override string ToString() => Name;
}

/// <summary>A foldout of the file list: Committed, Staged, Unstaged, or the files outside git (all the files, with no header, when none is in git).</summary>
public sealed partial class FileSection(FileChangesViewModel panel, string name, IReadOnlyList<DiffFileItem> files, bool expanded) : ObservableObject
{
    public string Name { get; } = name;
    public IReadOnlyList<DiffFileItem> Files { get; } = files;
    public bool HasHeader => Name.Length > 0;
    public string Count => Files.Count.ToString(CultureInfo.InvariantCulture);

    /// <summary>What UI Automation calls its list.</summary>
    public string ListName => HasHeader ? Name + " files" : "Changed files";

    /// <summary>Unfolded: its files show in the list, and their diffs in the diff.</summary>
    [ObservableProperty]
    private bool _isExpanded = expanded;

    partial void OnIsExpandedChanged(bool value) => panel.Folded(this);

    /// <summary>The files selected in this section's list (Ctrl+click adds one, Shift+click a run); none when the selected files are in another.</summary>
    public SelectionModel<DiffFileItem> Selection { get; } = MakeSelection(panel);

    private static SelectionModel<DiffFileItem> MakeSelection(FileChangesViewModel panel)
    {
        // No source of its own: the list gives it its items (set here, the list didn't take it, and showed nothing selected).
        var selection = new SelectionModel<DiffFileItem> { SingleSelect = false };
        selection.SelectionChanged += (sender, _) => panel.SectionPicked((SelectionModel<DiffFileItem>)sender!);
        return selection;
    }

    [RelayCommand]
    private void Toggle() => IsExpanded = !IsExpanded;
}

/// <summary>A row of the diff: a file's header, a hunk's @@ line, or a line of a file with its numbers on the old side and the new.</summary>
public sealed partial class DiffRow(DiffFileItem file, DiffLine? line) : ObservableObject
{
    public DiffFileItem File { get; } = file;

    /// <summary>Changed by the agent just now: lit up a moment, then it fades.</summary>
    [ObservableProperty]
    private bool _isFresh;

    /// <summary>Where it is among the diff's rows.</summary>
    public int Index { get; init; }

    /// <summary>The line; null for the file's header row.</summary>
    public DiffLine? Line { get; } = line;

    public bool IsHeader => Line is null;
    public bool IsHunk => Line?.Kind is DiffLineKind.Hunk or DiffLineKind.Note;
    public bool IsAdded => Line?.Kind == DiffLineKind.Added;
    public bool IsRemoved => Line?.Kind == DiffLineKind.Removed;

    /// <summary>A line of the file that can be picked: added, removed or kept, not a header.</summary>
    public bool IsLine => Line?.Kind is DiffLineKind.Added or DiffLineKind.Removed or DiffLineKind.Context;

    public string OldNumber => Line?.Old?.ToString(CultureInfo.InvariantCulture) ?? "";
    public string NewNumber => Line?.New?.ToString(CultureInfo.InvariantCulture) ?? "";

    /// <summary>The one number shown: the line's in the new file, a removed line's in the old (greyed out).</summary>
    public string Number => NewNumber.Length > 0 ? NewNumber : OldNumber;
    public string Marker => Line?.Kind switch { DiffLineKind.Added => "+", DiffLineKind.Removed => "-", _ => "" };

    public string Text => Line is { } l ? l.Text : File.Name;

    /// <summary>A line with a number and a marker: not a file's header nor a hunk's divider.</summary>
    public bool IsCode => !IsHeader && !IsHunk;

    /// <summary>What a hunk's divider says: "Lines 96-104" of the new side for "@@ -96,8 +96,9 @@", the old side's
    /// when nothing is left of it ("Removed lines 1-140"), and git's function context after it. A note as it is.</summary>
    public string Divider
    {
        get
        {
            if (Line is not { Kind: DiffLineKind.Hunk } l || HunkRange().Match(l.Text) is not { Success: true } m)
                return Text;
            static int Num(Group g, int none) => g.Success ? int.Parse(g.Value, CultureInfo.InvariantCulture) : none;
            var (oldStart, oldCount, newStart, newCount) = (Num(m.Groups[1], 0), Num(m.Groups[2], 1), Num(m.Groups[3], 0), Num(m.Groups[4], 1));
            var (start, count, lines) = newCount > 0 ? (newStart, newCount, "Line") : (oldStart, oldCount, "Removed line");
            lines += count == 1 ? $" {start}" : $"s {start}-{start + count - 1}";
            var context = m.Groups[5].Value.Trim();
            return context.Length == 0 ? lines : $"{lines} · {context}";
        }
    }

    [GeneratedRegex(@"^@@ -(\d+)(?:,(\d+))? \+(\d+)(?:,(\d+))? @@(.*)$")]
    private static partial Regex HunkRange();

    /// <summary>What the header says after the name: "modified · +10 -3", "staged · modified · +2" in git.</summary>
    public string HeaderDetails => HeaderStatus + File.Counts;

    /// <summary>The header's details before the counts, which are shown in their colours: "staged · modified · ".</summary>
    public string HeaderStatus => (File.Section is null ? "" : File.SectionWord + " · ") + $"{File.Status} · ";

    /// <summary>What screen readers and UI Automation call it: "48 + text", the file's name for a header.</summary>
    public override string ToString() => IsHeader ? $"{File.Name}, {HeaderDetails}" : IsHunk ? Divider : $"{Number} {Marker} {Text}".Replace("  ", " ");

    /// <summary>Says which row this is again after a refresh, to keep it picked.</summary>
    internal (string Path, GitSection? Section, DiffLineKind Kind, int? Old, int? New, string Text) Key => (File.FullPath.ToUpperInvariant(), File.Section, Line?.Kind ?? DiffLineKind.Hunk, Line?.Old, Line?.New, Line?.Text ?? "");
}

/// <summary>
/// The File changes panel, a tool of the chat window's toolkit on the left of the chat: the files the
/// agent changed in this chat (write_file, edit_file, and what its commands changed), each against
/// what it held before the agent first changed it, updated as the agent works. Lines picked in it
/// (click, drag, Shift+click, Ctrl+click) go into the chat's box as the files they are in, and the message
/// sent from there carries them for the model: the user asks about them, has them explained,
/// reviewed or changed (the user's request, 2026-10-24).
/// </summary>
public sealed partial class FileChangesViewModel : ObservableObject, IToolkit
{
    private readonly MainWindowViewModel _chat;
    private readonly DispatcherTimer _refreshTimer;
    private CancellationTokenSource? _loading;
    private bool _picking;

    /// <summary>Rows shown at most: a diff of a whole generated folder would make the list slow to scroll.</summary>
    private const int MaxRows = 30_000;

    /// <summary>Lines of a pick the model is given at most, with the count of the rest.</summary>
    private const int MaxPickedLines = 300;

    /// <param name="chat">The chat the panel is a tool of: the files its agent changes, and where messages about the picked lines go.</param>
    public FileChangesViewModel(MainWindowViewModel chat)
    {
        _chat = chat;
        Selection = new SelectionModel<DiffRow> { SingleSelect = false };
        Selection.SelectionChanged += (_, e) => OnPicked(e.SelectedItems.OfType<DiffRow>().ToList());
        // Calls that come one after another (a file written in parts) are read once.
        _refreshTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, (_, _) =>
        {
            _refreshTimer!.Stop();
            _ = LoadAsync();
        });
        _lightFirstRead = chat.OpeningOnFirstChange;
        _fade = new DispatcherTimer(LitFor, DispatcherPriority.Background, (_, _) =>
        {
            _fade!.Stop();
            foreach (var row in _latest)
                row.IsFresh = false;
        });
        _chat.Attachments.CollectionChanged += OnBoxChanged;
        _chat.ChangesUpdated += OnChangesUpdated;
        _ = LoadAsync();
    }

    private void OnChangesUpdated()
    {
        _refreshTimer.Stop();
        _refreshTimer.Start();
    }

    // ---- the diff -------------------------------------------------------------------------------

    /// <summary>The diff shown now; null before one is read.</summary>
    private DiffResult? _diff;

    /// <summary>Every row of the diff, the files one after another.</summary>
    [ObservableProperty]
    private IReadOnlyList<DiffRow> _rows = [];

    /// <summary>The changed files, for the list beside the diff.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFiles))]
    private IReadOnlyList<DiffFileItem> _files = [];

    public bool HasFiles => Files.Count > 0;

    /// <summary>The file list's foldouts: Committed, Staged, Unstaged and Outside git, those with files; one with no header when no file is in git.</summary>
    [ObservableProperty]
    private IReadOnlyList<FileSection> _sections = [];

    /// <summary>The sections folded by the user, by name: kept across refreshes while the panel is open.</summary>
    private readonly HashSet<string> _folded = [];

    /// <summary>A section folded or unfolded: the diff shows its files' rows, or not.</summary>
    internal void Folded(FileSection section)
    {
        if (_syncing)
            return;
        if (section.IsExpanded)
            _folded.Remove(section.Name);
        else
            _folded.Add(section.Name);
        if (_diff is not null)
            Show(_diff, keepPicks: true, fresh: false);
    }

    /// <summary>Set while the sections' selections follow <see cref="SelectedFiles"/>.</summary>
    private bool _syncing;

    /// <summary>
    /// Files clicked in a section's list, as Sourcetree's lists take them (the user, 2026-10-28): a
    /// click selects that file alone, Ctrl+click adds or takes one off, Shift+click a run, and the
    /// files are in one section at a time (a click in another lets go of this one's). A click on the
    /// only file selected lets it go (<see cref="LetGoOf"/>). Lines and files are never picked at once
    /// (the user, 2026-10-28: with lines picked, the file they were in could be selected or not,
    /// which said nothing): with lines picked a click only goes to the file, so lines in another
    /// are Ctrl+clicked after.
    /// </summary>
    internal void SectionPicked(SelectionModel<DiffFileItem> changed)
    {
        if (_syncing || Sections.FirstOrDefault(s => s.Selection == changed) is not { } section)
            return;
        var files = changed.SelectedItems.OfType<DiffFileItem>().ToList();
        if (files.Count > 0)
            SelectFiles(files);
        else if (SelectedFiles.Count > 0 && SelectedFiles.All(section.Files.Contains))
            SelectFiles([]);
    }

    /// <summary>A press on the only file selected, with no key held: it is let go, as a second click on it does.</summary>
    internal bool IsOnlySelected(DiffFileItem file) => SelectedFiles is [var only] && only == file;

    internal void LetGoOf(DiffFileItem file)
    {
        if (SelectedFiles is [var only] && only == file)
            SelectFiles([]);
    }

    private void SelectFiles(IReadOnlyList<DiffFileItem> files)
    {
        SelectedFiles = files;
        ActionsChanged();
        SyncSections();
        OnPicked();
    }

    /// <summary>The sections' lists select <see cref="SelectedFiles"/>, each theirs if there, else none.</summary>
    private void SyncSections()
    {
        _syncing = true;
        try
        {
            // Only what differs: a list cleared and selected again in its own click showed nothing selected.
            foreach (var section in Sections)
                for (var i = 0; i < section.Files.Count; i++)
                {
                    var want = SelectedFiles.Contains(section.Files[i]);
                    if (want != section.Selection.IsSelected(i))
                    {
                        if (want)
                            section.Selection.Select(i);
                        else
                            section.Selection.Deselect(i);
                    }
                }
        }
        finally
        {
            _syncing = false;
        }
    }

    /// <summary>
    /// The files selected in the list: with no lines picked, picks of their whole changes in the box
    /// ("Whole file · notes.md"). None until one is clicked, kept across a refresh.
    /// </summary>
    private IReadOnlyList<DiffFileItem> SelectedFiles { get; set; } = [];

    /// <summary>Set while a refresh selects the file again, which goes nowhere in the diff: only a click there does.</summary>
    internal bool Reselecting { get; private set; }

    /// <summary>The rows picked; their lines go into the chat's box.</summary>
    public SelectionModel<DiffRow> Selection { get; }

    /// <summary>How much changed ("3 files · +40 -12"); empty with nothing changed. Shown as its files
    /// ("3 files · ") and its counts, "+40" green and " -12" red.</summary>
    [ObservableProperty]
    private string _summary = "";

    [ObservableProperty]
    private string _summaryFiles = "";

    [ObservableProperty]
    private string _summaryAdded = "";

    [ObservableProperty]
    private string _summaryRemoved = "";

    /// <summary>The folder the files are in, as the summary's tooltip.</summary>
    [ObservableProperty]
    private string? _folder;

    /// <summary>The line at the top: what went wrong, else how the panel is used.</summary>
    [ObservableProperty]
    private string _status = Hint;

    private const string Hint = "Pick lines or files to ask about them, stage or discard them.";

    /// <summary>Reads the files again, keeping what was picked where the same lines are still there.</summary>
    /// <param name="byUser">Read after the user staged, unstaged or discarded: what moved isn't the agent's change "just now".</param>
    private async Task LoadAsync(bool byUser = false)
    {
        _loading?.Cancel();
        var loading = _loading = new CancellationTokenSource();
        var changes = _chat.Changes;
        DiffResult diff;
        try
        {
            diff = await Task.Run(() => changes.DiffAsync(loading.Token), loading.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        if (!ReferenceEquals(loading, _loading))
            return;
        var same = ReferenceEquals(changes, _shownChanges);
        // Opened by the agent's first change, the first read is that change: all of it is new.
        var first = _lightFirstRead && _shownChanges is null;
        _lightFirstRead = false;
        Show(diff, keepPicks: same, fresh: (same && !byUser) || first);
        _shownChanges = changes;
    }

    /// <summary>The panel opened on its own at the agent's first change in the chat: its first read lights what it shows.</summary>
    private bool _lightFirstRead;

    /// <summary>Whose list is shown: picks are kept across a refresh of the same chat's, not into another chat's.</summary>
    private FileChanges? _shownChanges;

    private void Show(DiffResult diff, bool keepPicks, bool fresh)
    {
        var picked = keepPicks ? Selection.SelectedItems.OfType<DiffRow>().Select(r => r.Key).ToHashSet() : [];
        var current = keepPicks ? SelectedFiles : [];
        var before = fresh ? _diff : null;
        _diff = diff;
        Status = Hint;
        var rows = new List<DiffRow>();
        var files = new List<DiffFileItem>();
        // In a repository, a foldout each for where the changes are; else the files with no header.
        var names = diff.InGit ? new[] { "Committed", "Staged", "Unstaged", "Outside git" } : [""];
        var bySection = names.ToDictionary(n => n, _ => new List<DiffFileItem>());
        foreach (var file in diff.Files)
        {
            var name = !diff.InGit ? "" : file.Git?.Section.ToString() ?? "Outside git";
            var shown = !_folded.Contains(name);
            if (shown && rows.Count >= MaxRows)
            {
                Status = $"Too much changed to show it all: the diff stops in {files.Last(f => f.Row >= 0).Name}.";
                shown = false;
            }
            var item = new DiffFileItem(file, shown ? rows.Count : -1, diff.FullPath(file));
            files.Add(item);
            bySection[name].Add(item);
            if (!shown)
                continue;
            rows.Add(new DiffRow(item, null) { Index = rows.Count });
            foreach (var line in file.Lines.Take(MaxRows - rows.Count))
                rows.Add(new DiffRow(item, line) { Index = rows.Count });
        }
        // Another chat's list, or the first read (unless the panel opened on the agent's first change),
        // has no "just now": nothing lights up.
        var now = fresh ? Fresh(before, rows) : [];
        if (now.Count > 0)
        {
            _fade.Stop();
            _latest = now;
            foreach (var row in _latest)
                row.IsFresh = true;
            _fade.Start();
        }
        else if (keepPicks)
        {
            // A read that found nothing new (the second of two calls in a row) leaves the last change as
            // it was, still lit if it was, its fade going on: before, the new rows went out unlit at once.
            var lit = _latest.Where(r => r.IsFresh).Select(r => r.Key).ToHashSet();
            var last = _latest.Select(r => r.Key).ToHashSet();
            _latest = rows.Where(r => last.Contains(r.Key)).ToList();
            foreach (var row in _latest)
                row.IsFresh = lit.Contains(row.Key);
            if (lit.Count == 0)
                _fade.Stop();
        }
        else
        {
            _fade.Stop();
            _latest = [];
        }
        _picking = true;
        try
        {
            Selection.Source = null;
            Rows = rows;
            Reselecting = true;
            try
            {
                Files = files;
                _syncing = true;
                try
                {
                    Sections = names.Where(n => bySection[n].Count > 0).Select(n => new FileSection(this, n, bySection[n], !_folded.Contains(n))).ToList();
                }
                finally
                {
                    _syncing = false;
                }
                // Each the same file in the same section, else in another (staged since); none unless clicked.
                SelectedFiles = current.Select(c => files.FirstOrDefault(f => f.IsSame(c))
                        ?? files.FirstOrDefault(f => string.Equals(f.FullPath, c.FullPath, StringComparison.OrdinalIgnoreCase)))
                    .OfType<DiffFileItem>().Distinct().ToList();
                ActionsChanged();
                SyncSections();
            }
            finally
            {
                Reselecting = false;
            }
            Selection.Source = rows;
            if (picked.Count > 0)
                for (var i = 0; i < rows.Count; i++)
                    if (picked.Contains(rows[i].Key))
                        Selection.Select(i);
        }
        finally
        {
            _picking = false;
        }
        _refreshing = true;
        try { OnPicked(); }
        finally { _refreshing = false; }
        Folder = diff.Root.Length > 0 ? diff.Root : null;
        // "2 files · +14", as each file's counts leave out what is none.
        // In git a file may be in two sections, so their counts add up to nothing: only the files are counted.
        var count = files.Select(f => f.FullPath.ToUpperInvariant()).Distinct().Count();
        SummaryAdded = count == 0 || diff.InGit || diff.Added == 0 ? "" : $"+{diff.Added}";
        SummaryRemoved = count == 0 || diff.InGit || diff.Removed == 0 ? "" : (SummaryAdded.Length > 0 ? " -" : "-") + diff.Removed;
        var counts = SummaryAdded + SummaryRemoved;
        SummaryFiles = count == 0 ? "" : $"{count} {(count == 1 ? "file" : "files")}" + (counts.Length > 0 ? " · " : "");
        Summary = SummaryFiles + counts;
        ActionsChanged();
        if (Watch && now.Count > 0)
            ShowRow?.Invoke(_latest[0].Index);
    }

    /// <summary>
    /// The added and removed lines in <paramref name="rows"/> that <paramref name="before"/> didn't
    /// have, file by file: what the agent changed since the last read. Counted by their text, as the
    /// numbers of every line below an edit move.
    /// </summary>
    public static List<DiffRow> Fresh(DiffResult? before, IReadOnlyList<DiffRow> rows)
    {
        var had = new Dictionary<(string, GitSection?, DiffLineKind, string), int>();
        foreach (var file in before?.Files ?? [])
        {
            var path = before!.FullPath(file).ToUpperInvariant();
            foreach (var line in file.Lines.Where(l => l.Kind is DiffLineKind.Added or DiffLineKind.Removed))
                had[(path, file.Git?.Section, line.Kind, line.Text)] = had.GetValueOrDefault((path, file.Git?.Section, line.Kind, line.Text)) + 1;
        }
        var fresh = new List<DiffRow>();
        foreach (var row in rows.Where(r => r.IsAdded || r.IsRemoved))
        {
            var key = (row.File.FullPath.ToUpperInvariant(), row.File.Section, row.Line!.Value.Kind, row.Line.Value.Text);
            if (had.GetValueOrDefault(key) is > 0 and var n)
                had[key] = n - 1;
            else
                fresh.Add(row);
        }
        return fresh;
    }

    // ---- how the diff shows: Wrap and Watch ------------------------------------------------------

    /// <summary>Long lines wrap inside the panel, under the code, rather than run off its right edge; kept in settings.</summary>
    public bool Wrap
    {
        get => _chat.Panels.WrapDiff;
        set
        {
            if (value == Wrap)
                return;
            _chat.Panels.WrapDiff = value;
            OnPropertyChanged();
            _chat.SavePanels();
        }
    }

    /// <summary>
    /// The diff follows the agent: what it just changed is selected, scrolled to and lit up a
    /// moment. Kept in settings and changed only by its toggle: scrolled or picked by hand, the diff stays
    /// where the user took it until the agent's next change. Turned on, it goes to the last change.
    /// </summary>
    public bool Watch
    {
        get => _chat.Panels.WatchChanges;
        set
        {
            if (value == Watch)
                return;
            _chat.Panels.WatchChanges = value;
            OnPropertyChanged();
            _chat.SavePanels();
            if (value)
                GoToLatest();
        }
    }

    /// <summary>The lines the agent changed last, lit up while they are fresh; empty before it changed one with the panel open.</summary>
    private List<DiffRow> _latest = [];

    private readonly DispatcherTimer _fade;

    /// <summary>How long a change stays lit before it fades (over 2 s, Border.fresh in the panel): 1.5 s and 1 s were gone before the eye got there (the user, 2026-10-27).</summary>
    private static readonly TimeSpan LitFor = TimeSpan.FromSeconds(4);

    /// <summary>Set while a refresh picks the same lines again: no click by hand, so a hunk isn't picked whole.</summary>
    private bool _refreshing;

    /// <summary>Watch turned on: the last change in sight and lit up again (its file isn't selected: only a click selects one).</summary>
    private void GoToLatest()
    {
        if (_latest.Count == 0 || _latest[0].Index >= Rows.Count || !ReferenceEquals(Rows[_latest[0].Index], _latest[0]))
            return;
        foreach (var row in _latest)
            row.IsFresh = true;
        _fade.Stop();
        _fade.Start();
        ShowRow?.Invoke(_latest[0].Index);
    }

    /// <summary>Watching: the panel brings this row, the agent's last change, into sight.</summary>
    public event Action<int>? ShowRow;

    /// <summary>A file in the list was clicked: the panel shows its header at the top of the diff.</summary>
    public event Action<int>? GoToRow;

    [RelayCommand]
    private void GoTo(DiffFileItem file)
    {
        if (file.Row >= 0)
            GoToRow?.Invoke(file.Row);
    }

    // ---- git: staging, unstaging, discarding ------------------------------------------------------------

    /// <summary>Where the diff would be, with nothing to show.</summary>
    public string EmptyText => "Files the agent changes in this chat show here as it works.";

    /// <summary>
    /// What the action bar's Stage, Unstage and Discard act on: the picked lines, file by file (their
    /// added and removed lines: a picked unchanged line changes nothing), else the selected file
    /// whole (Lines null), unless its section is folded.
    /// </summary>
    private List<(DiffFileItem File, IReadOnlyCollection<(int? Old, int? New)>? Lines)> Targets()
    {
        var picks = Picks();
        return picks
            .Select(p => (p.File, (IReadOnlyCollection<(int? Old, int? New)>?)p.Rows.Where(r => r.IsAdded || r.IsRemoved)
                .Select(r => r.IsAdded ? ((int?)null, r.Line!.Value.New) : (r.Line!.Value.Old, (int?)null)).ToList()))
            .Where(p => p.Item2!.Count > 0)
            .Concat(WholeFiles().Where(f => f.Row >= 0).Select(f => (f, (IReadOnlyCollection<(int? Old, int? New)>?)null)))
            .ToList();
    }

    /// <summary>
    /// The files selected whole: those selected in the list with none of their lines picked. A file whose
    /// lines are picked is about those, and shows grey in the list; with its lines let go, it is whole again.
    /// </summary>
    private List<DiffFileItem> WholeFiles() => SelectedFiles.Where(f => !f.HasPickedLines).ToList();

    /// <summary>The action bar's actions.</summary>
    private enum GitAct { Stage, Unstage, Discard }

    /// <summary>Which files each action takes: Stage the unstaged, Unstage the staged, Discard the unstaged and those outside git.</summary>
    private static bool Takes(GitAct act, DiffFileItem file) => act switch
    {
        GitAct.Stage => file.Section == GitSection.Unstaged,
        GitAct.Unstage => file.Section == GitSection.Staged,
        _ => file.Section is null or GitSection.Unstaged,
    };

    private List<(DiffFileItem File, IReadOnlyCollection<(int? Old, int? New)>? Lines)> Targets(GitAct act) => Targets().Where(t => Takes(act, t.File)).ToList();

    /// <summary>Stage and Unstage show only where a file is in git.</summary>
    public bool ShowGit => _diff?.InGit == true;

    public bool CanStage => Targets(GitAct.Stage).Count > 0;
    public bool CanUnstage => Targets(GitAct.Unstage).Count > 0;
    public bool CanDiscard => Targets(GitAct.Discard).Count > 0;

    /// <summary>What the actions act on, beside them: "Lines 3-4 · notes.txt · unstaged", "12 lines in 3 files", "notes.txt · staged".</summary>
    public string Target
    {
        get
        {
            var targets = Targets();
            if (targets.Count == 0)
                return Selection.Count > 0 ? "No changed line picked" : "";
            if (targets.Count > 1 && targets.All(t => t.Lines is null))
                return $"{targets.Select(t => t.File.FullPath.ToUpperInvariant()).Distinct().Count()} files" + (targets[0].File.Section is { } s && targets.All(t => t.File.Section == s) ? " · " + targets[0].File.SectionWord : "");
            if (targets.Count > 1)
            {
                // "3 lines in 2 files", and the files whole beside them: "3 lines in 2 files · 1 whole file".
                var lineCount = targets.Sum(t => t.Lines?.Count ?? 0);
                var lineFiles = targets.Where(t => t.Lines is not null).Select(t => t.File.FullPath.ToUpperInvariant()).Distinct().Count();
                var wholeFiles = targets.Where(t => t.Lines is null).Select(t => t.File.FullPath.ToUpperInvariant()).Distinct().Count();
                var parts = new List<string>();
                if (lineFiles > 0)
                    parts.Add($"{lineCount} {(lineCount == 1 ? "line" : "lines")} in {lineFiles} {(lineFiles == 1 ? "file" : "files")}");
                if (wholeFiles > 0)
                    parts.Add($"{wholeFiles} whole {(wholeFiles == 1 ? "file" : "files")}");
                return string.Join(" · ", parts);
            }
            var (file, lines) = targets[0];
            var where = file.Section is null ? "" : " · " + file.SectionWord;
            if (lines is null)
                return Path.GetFileName(file.FullPath) + where;
            var rows = Picks().First(p => p.File == file).Rows.Where(r => r.IsAdded || r.IsRemoved).ToList();
            return $"{Capital(Lines(rows))} · {Path.GetFileName(file.FullPath)}{where}";
        }
    }

    /// <summary>"the picked lines", else the selected file's name, for the tooltips.</summary>
    private string What => (Selection.Count > 0, WholeFiles()) switch
    {
        (true, []) => "the picked lines",
        (true, _) => "what is picked",
        (false, [var file]) => Path.GetFileName(file.FullPath),
        (false, []) => "the file",
        (false, var files) => $"{files.Count} files",
    };

    public string StageTip => CanStage ? $"Stage {What} in git" : "Pick unstaged lines, or select an unstaged file, to stage them";

    public string UnstageTip => CanUnstage ? $"Unstage {What}: the file on disk stays as it is" : "Pick staged lines, or select a staged file, to unstage them";

    /// <summary>What Discard puts back, or why it can't.</summary>
    public string DiscardTip
    {
        get
        {
            var targets = Targets(GitAct.Discard);
            if (targets.Count == 0)
                return Targets().Any(t => t.File.Section == GitSection.Committed) ? "Committed changes can't be discarded here"
                    : Targets().Count > 0 ? "Only changes not staged yet can be discarded: unstage these first"
                    : "Pick unstaged lines, or select an unstaged file, to discard them";
            if (targets is [{ Lines: null, File.IsNew: true }])
                return $"Move {Path.GetFileName(targets[0].File.FullPath)} to the Recycle Bin";
            var how = targets.All(t => t.File.Section == GitSection.Unstaged) ? " as staged" : targets.All(t => t.File.Section is null) ? " as before the agent's changes" : "";
            return $"Put {What} back{how}";
        }
    }

    /// <summary>The action bar says again what it acts on and what each button does.</summary>
    private void ActionsChanged()
    {
        foreach (var name in (string[])[nameof(ShowGit), nameof(CanStage), nameof(CanUnstage), nameof(CanDiscard), nameof(Target), nameof(StageTip), nameof(UnstageTip), nameof(DiscardTip)])
            OnPropertyChanged(name);
    }

    [RelayCommand]
    private Task Stage() => Act(GitAct.Stage);

    [RelayCommand]
    private Task Unstage() => Act(GitAct.Unstage);

    /// <summary>
    /// Discard: unstaged changes put back as staged, a file outside git as before the agent changed
    /// it; a file that wasn't there goes to the Recycle Bin.
    /// </summary>
    [RelayCommand]
    private Task Discard() => Act(GitAct.Discard);

    /// <summary>Stages, unstages or discards what is picked, else the selected file; then reads the files again, the picks let go.</summary>
    private async Task Act(GitAct act)
    {
        var changes = _chat.Changes;
        var errors = new List<string>();
        foreach (var (file, lines) in Targets(act))
        {
            var error = await Task.Run(() => act switch
            {
                GitAct.Stage => GitChanges.StageAsync(file.FullPath, lines, CancellationToken.None),
                GitAct.Unstage => GitChanges.UnstageAsync(file.FullPath, lines, CancellationToken.None),
                _ => changes.DiscardAsync(file.Section, file.FullPath, lines, ToRecycleBin, CancellationToken.None),
            });
            if (error is not null)
                errors.Add($"{Path.GetFileName(file.FullPath)}: {error}");
        }
        Unpick();
        await LoadAsync(byUser: true);
        if (errors.Count > 0)
            Status = act switch { GitAct.Stage => "Not staged: ", GitAct.Unstage => "Not unstaged: ", _ => "Not discarded: " } + string.Join(" ", errors);
    }

    internal static void ToRecycleBin(string path) =>
        Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(path, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);

    // ---- what is picked, into the chat's box -----------------------------------------------------------

    /// <summary>The picked lines into the box, as the files they are in ("Lines 12-18 · DiffTool.cs": the lines first, as a long name is cut short there).</summary>
    private void OnPicked(IReadOnlyList<DiffRow>? added = null)
    {
        if (_picking)
            return;
        _picking = true;
        try
        {
            // A click on a hunk's divider picks its lines, on a file's name all the file's (Ctrl held: as
            // well as what was picked). A header or divider is no line itself: it doesn't stay picked.
            if (!_refreshing && added is [{ } clicked] && (clicked.IsHeader || clicked.Line?.Kind == DiffLineKind.Hunk))
            {
                var end = clicked.Index + 1;
                while (end < Rows.Count && !Rows[end].IsHeader && (clicked.IsHeader || Rows[end].Line?.Kind != DiffLineKind.Hunk))
                    end++;
                if (end > clicked.Index + 1)
                    Selection.SelectRange(clicked.Index + 1, end - 1);
            }
            foreach (var row in Selection.SelectedItems.OfType<DiffRow>().Where(r => !r.IsLine).ToList())
                Selection.Deselect(row.Index);
            // A file picked in two sections (staged and unstaged) is one card in the box: its lines,
            // and the files selected in the list whose lines aren't picked, whole.
            var picks = Picks();
            foreach (var file in Files)
                file.HasPickedLines = picks.Any(p => p.File == file);
            var lines = picks.GroupBy(p => p.File.FullPath, StringComparer.OrdinalIgnoreCase)
                .Select(g => (g.Key, $"{Capital(Lines(g.SelectMany(p => p.Rows).ToList()))} · {Path.GetFileName(g.Key)}")).ToList();
            _chat.Pick(lines.Concat(WholeFiles().Select(f => f.FullPath).Distinct(StringComparer.OrdinalIgnoreCase)
                    .Where(p => !lines.Any(l => string.Equals(l.Key, p, StringComparison.OrdinalIgnoreCase)))
                    .Select(p => (p, $"Whole file · {Path.GetFileName(p)}")))
                .ToList());
            ActionsChanged();
        }
        finally
        {
            _picking = false;
        }
    }

    /// <summary>
    /// A drag over the diff from row <paramref name="from"/> to <paramref name="to"/>: every row between
    /// is picked, as a Shift+click there would, with <paramref name="kept"/> (what was picked before a
    /// Ctrl+drag) picked too. Called again as the drag goes on, so the pick follows it.
    /// </summary>
    internal void PickDrag(int from, int to, IReadOnlyList<int> kept)
    {
        using (Selection.BatchUpdate())
        {
            Selection.Clear();
            foreach (var row in kept)
                Selection.Select(row);
            Selection.SelectRange(Math.Min(from, to), Math.Max(from, to));
            // Shift+click after the drag goes on from where it started.
            Selection.AnchorIndex = from;
        }
    }

    /// <summary>The picked lines, file by file in the diff's order.</summary>
    private List<(DiffFileItem File, List<DiffRow> Rows)> Picks() =>
        Selection.SelectedItems.OfType<DiffRow>().Where(r => r.IsLine)
            .GroupBy(r => r.File)
            .OrderBy(g => g.Key.Row)
            .Select(g => (g.Key, g.OrderBy(r => r.Index).ToList()))
            .ToList();

    /// <summary>"lines 12-18, 30", by their numbers in the new file; "removed lines 5-7" when only removed ones are picked.</summary>
    internal static string Lines(IReadOnlyList<DiffRow> rows)
    {
        var numbers = rows.Where(r => r.Line?.New is not null).Select(r => r.Line!.Value.New!.Value).ToList();
        var removed = numbers.Count == 0;
        if (removed)
            numbers = rows.Select(r => r.Line?.Old ?? 0).Where(n => n > 0).ToList();
        var ranges = Ranges(numbers);
        return (removed ? "removed " : "") + (numbers.Count == 1 ? "line " : "lines ") + ranges;
    }

    /// <summary>"12-18, 30" for 12, 13 … 18 and 30.</summary>
    private static string Ranges(List<int> numbers)
    {
        var sorted = numbers.Distinct().Order().ToList();
        var parts = new List<string>();
        for (var i = 0; i < sorted.Count;)
        {
            var j = i;
            while (j + 1 < sorted.Count && sorted[j + 1] == sorted[j] + 1)
                j++;
            parts.Add(j == i ? $"{sorted[i]}" : $"{sorted[i]}-{sorted[j]}");
            i = j + 1;
        }
        return string.Join(", ", parts);
    }

    /// <summary>
    /// What a message sent while the panel is open carries: over its bubble, the lines picked; for
    /// the model, what the panel shows, and the picked lines with their numbers and text. Neutral
    /// about what is to be done with them: the user may ask about them, have them explained or
    /// reviewed, or changed. Nothing while the agent has changed no file in the chat.
    /// </summary>
    public (string Via, string Context)? About(IReadOnlyList<string> files)
    {
        if (_diff is not { Files.Count: > 0 } diff)
            return null;
        var picks = Picks().Where(p => files.Contains(p.File.FullPath, StringComparer.OrdinalIgnoreCase)).ToList();
        var via = "File changes" + (picks.Count == 0 ? "" : " · " + string.Join(", ", picks.Select(p => $"{Path.GetFileName(p.File.FullPath)} {Lines(p.Rows)}")));
        var context = new StringBuilder(diff.InGit
                ? "[The File changes panel is open beside the chat, showing the files you changed in this chat by where their changes are in git: committed (against what the file held before your first change), staged (against the last commit) and unstaged (on disk, against what is staged); files outside git against what they held before your first change: "
                : "[The File changes panel is open beside the chat, showing the files you changed in this chat, each against what it held before your first change to it: ")
            .Append(string.Join(", ", diff.Files.Take(30).Select(f => $"{diff.FullPath(f)} ({(f.Git is { } g ? g.Section.ToString().ToLowerInvariant() + ", " : "")}{f.Status}, {f.Counts})")))
            .Append(diff.Files.Count > 30 ? $" and {diff.Files.Count - 30} more. " : ". ");
        // The files selected in the list whole (as their cards in the box said), and the lines picked.
        var whole = WholeFiles().Where(f => files.Contains(f.FullPath, StringComparer.OrdinalIgnoreCase)).ToList();
        if (picks.Count == 0 && whole.Count == 0)
        {
            context.Append("What they say is about these changes unless they say otherwise.]");
            return (via, context.ToString());
        }
        via = "File changes · " + string.Join(", ", picks.Select(p => $"{Path.GetFileName(p.File.FullPath)} {Lines(p.Rows)}")
            .Concat(whole.Select(f => Path.GetFileName(f.FullPath)).Distinct()));
        context.Append("What they say is about what they selected in it unless they say otherwise.");
        var left = MaxPickedLines;
        if (whole.Count > 0)
        {
            context.Append(whole.Count == 1 ? " This file, whole:" : " These files, whole:");
            foreach (var file in whole)
            {
                context.Append($"\n{file.FullPath} ({Where(file)}{file.Status}, {file.Counts}):\n");
                var lines = file.File.Lines;
                foreach (var line in lines.Take(Math.Max(left, 0)))
                    AppendLine(context, line);
                if (lines.Count > left)
                    context.Append($"[... {lines.Count - Math.Max(left, 0)} more lines of its diff]\n");
                left -= lines.Count;
            }
        }
        if (picks.Count > 0)
        {
            context.Append(whole.Count > 0 ? "\nAnd these lines:" : " These lines:");
            var total = picks.Sum(p => p.Rows.Count);
            var shown = 0;
            foreach (var (file, rows) in picks)
            {
                context.Append($"\n{file.FullPath} ({Where(file)}{file.Status}), {Lines(rows)}:\n");
                foreach (var row in rows)
                {
                    if (shown++ >= Math.Max(left, 0))
                    {
                        context.Append($"[... {total - shown + 1} more selected lines]\n");
                        break;
                    }
                    AppendLine(context, row.Line!.Value);
                }
                if (shown > Math.Max(left, 0))
                    break;
            }
        }
        context.Append(Numbers);
        return (via, context.ToString());
    }

    /// <summary>"staged, " before a file's status in git, so the model knows which diff it is.</summary>
    private static string Where(DiffFileItem file) => file.Section is null ? "" : file.SectionWord + ", ";

    private string Numbers => _diff?.InGit == true
        ? "(+ added, - removed, the rest unchanged; numbers are lines of the new side: the file as it is now for unstaged changes and files outside git, as staged for staged ones, as committed for committed ones.)]"
        : "(+ added, - removed, the rest unchanged; numbers are lines of the file as it is now.)]";

    /// <summary>A line of the diff for the model: "+ 12	text", "- (was 5)	text"; a hunk's @@ line as it is.</summary>
    private static void AppendLine(StringBuilder context, DiffLine line)
    {
        if (line.Kind is DiffLineKind.Hunk or DiffLineKind.Note)
        {
            context.Append(line.Text).Append('\n');
            return;
        }
        var number = line.Kind == DiffLineKind.Removed ? $"(was {line.Old})" : $"{line.New}";
        context.Append(line.Kind switch { DiffLineKind.Added => "+", DiffLineKind.Removed => "-", _ => " " })
            .Append(' ').Append(number).Append('\t').Append(line.Text).Append('\n');
    }

    /// <summary>The message went with the picked lines, or the selected files: they are no longer picked.</summary>
    public void Sent()
    {
        _picking = true;
        try
        {
            Selection.Clear();
            SelectFiles([]);
        }
        finally { _picking = false; }
    }

    /// <summary>A click away from the lines, or Esc: no line or file is picked, and their files leave the box. False when none was.</summary>
    public bool Unpick()
    {
        if (Selection.Count == 0 && SelectedFiles.Count == 0)
            return false;
        _picking = true;
        try { SelectFiles([]); }
        finally { _picking = false; }
        Selection.Clear();
        OnPicked();
        return true;
    }

    /// <summary>A file taken off the box (sent): its lines, or it whole, are no longer picked here either.</summary>
    private void OnBoxChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_picking || e.OldItems is null)
            return;
        var gone = e.OldItems.OfType<AttachmentItem>().Where(a => a.Picked).Select(a => a.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (gone.Count == 0 || _chat.Attachments.Any(a => a.Picked && gone.Contains(a.Path)))
            return;
        _picking = true;
        try
        {
            for (var i = 0; i < Rows.Count; i++)
                if (Selection.IsSelected(i) && gone.Contains(Rows[i].File.FullPath))
                    Selection.Deselect(i);
            if (SelectedFiles.Any(f => gone.Contains(f.FullPath)))
                SelectFiles(SelectedFiles.Where(f => !gone.Contains(f.FullPath)).ToList());
        }
        finally
        {
            _picking = false;
        }
    }

    // Dropped files go with the next message.
    public bool Drop(IReadOnlyList<string> paths) => false;

    public void Dispose()
    {
        _chat.Attachments.CollectionChanged -= OnBoxChanged;
        _chat.ChangesUpdated -= OnChangesUpdated;
        _refreshTimer.Stop();
        _fade.Stop();
        _loading?.Cancel();
    }
}
