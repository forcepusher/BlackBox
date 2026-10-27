using System.Collections.Specialized;
using System.Globalization;
using System.Text;
using Avalonia.Controls.Selection;
using Avalonia.Threading;
using Sloperator.Core.Diffs;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using static Sloperator.Core.Strings;

namespace Sloperator.App.ViewModels;

/// <summary>A file in the panel's list of changed files: a click there goes to it in the diff (Row, -1 while its section is folded).</summary>
public sealed record DiffFileItem(DiffFile File, int Row, string FullPath)
{
    public string Name => File.Display;
    public string Status => File.Status;
    public string Counts => File.Counts;
    public bool IsNew => File.Status == "added";
    public bool IsDeleted => File.Status == "deleted";

    /// <summary>Its section in a git repository; null outside one.</summary>
    public GitSection? Section => File.Git?.Section;

    /// <summary>"unstaged", "staged", "committed"; "" outside git.</summary>
    public string SectionWord => Section?.ToString().ToLowerInvariant() ?? "";

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

    /// <summary>The file selected in this section's list; null when the selected file is in another.</summary>
    [ObservableProperty]
    private DiffFileItem? _selected;

    partial void OnSelectedChanged(DiffFileItem? value) => panel.SectionPicked(value);

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
    public string Marker => Line?.Kind switch { DiffLineKind.Added => "+", DiffLineKind.Removed => "-", _ => "" };

    public string Text => Line is { } l ? l.Text : File.Name;

    /// <summary>What the header says after the name: "modified · +10 -3", "staged · modified · +2" in git.</summary>
    public string HeaderDetails => (File.Section is null ? "" : File.SectionWord + " · ") + $"{File.Status} · {File.Counts}";

    /// <summary>What screen readers and UI Automation call it: "48 + text", the file's name for a header.</summary>
    public override string ToString() => IsHeader ? $"{File.Name}, {HeaderDetails}" : $"{(NewNumber.Length > 0 ? NewNumber : OldNumber)} {Marker} {Text}".Replace("  ", " ");

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

    /// <summary>Set while the sections' selections follow <see cref="CurrentFile"/>.</summary>
    private bool _syncing;

    /// <summary>A file clicked in a section's list: the one selected (the other sections' lists let theirs go).</summary>
    internal void SectionPicked(DiffFileItem? file)
    {
        if (!_syncing && file is not null)
            CurrentFile = file;
    }

    partial void OnCurrentFileChanged(DiffFileItem? value)
    {
        ActionsChanged();
        _syncing = true;
        try
        {
            foreach (var section in Sections)
                section.Selected = section.Files.Contains(value) ? value : null;
        }
        finally
        {
            _syncing = false;
        }
    }

    /// <summary>
    /// The file selected in the list: with no lines picked, what a message is about (its whole change,
    /// as the box's card says). The first file until another is clicked, kept across a refresh.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Whole))]
    private DiffFileItem? _currentFile;

    /// <summary>Set while a refresh selects the file again, which goes nowhere in the diff: only a click there does.</summary>
    internal bool Reselecting { get; private set; }

    /// <summary>Over the chat's box while no line is picked: the selected file, as a pick shows ("notes.md", "whole file · +10 -3").</summary>
    public (string Title, string Details)? Whole => CurrentFile is { } file ? (Path.GetFileName(file.FullPath), $"whole file · {(file.Section is null ? "" : file.SectionWord + " · ")}{file.Status} · {file.Counts}") : null;

    public string WholeTip => "What you type is about this file's changes. Click or drag lines to ask about those.";

    /// <summary>The rows picked; their lines go into the chat's box.</summary>
    public SelectionModel<DiffRow> Selection { get; }

    /// <summary>How much changed ("3 files · +40 -12"); empty with nothing changed.</summary>
    [ObservableProperty]
    private string _summary = "";

    /// <summary>The folder the files are in, as the summary's tooltip.</summary>
    [ObservableProperty]
    private string? _folder;

    /// <summary>The line at the top: what went wrong, else how the panel is used.</summary>
    [ObservableProperty]
    private string _status = Hint;

    private const string Hint = "Pick lines to ask about them, stage or discard them; a @@ line picks its hunk, a file's name all of it.";

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
        var current = keepPicks ? CurrentFile : null;
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
            // Watching, the file changed is the one selected, as a click on it would.
            if (Watch)
                current = _latest[0].File;
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
                // The same file in the same section, else in another (staged since), else the first.
                CurrentFile = files.FirstOrDefault(f => f.IsSame(current))
                    ?? files.FirstOrDefault(f => current is not null && string.Equals(f.FullPath, current.FullPath, StringComparison.OrdinalIgnoreCase))
                    ?? files.FirstOrDefault();
                OnCurrentFileChanged(CurrentFile);
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
        var counts = diff.InGit ? "" : string.Join(" ", new[] { diff.Added > 0 ? $"+{diff.Added}" : null, diff.Removed > 0 ? $"-{diff.Removed}" : null }.OfType<string>());
        var count = files.Select(f => f.FullPath.ToUpperInvariant()).Distinct().Count();
        Summary = count == 0 ? "" : $"{count} {(count == 1 ? "file" : "files")}" + (counts.Length > 0 ? " · " + counts : "");
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

    /// <summary>Set by a scroll or pick by hand: watching stops until Watch is clicked again, its saved choice kept.</summary>
    private bool _watchStopped;

    /// <summary>
    /// The diff follows the agent: what it just changed is selected, scrolled to and lit up a
    /// moment. Kept in settings; turned on, it goes to the last change.
    /// </summary>
    public bool Watch
    {
        get => _chat.Panels.WatchChanges && !_watchStopped;
        set
        {
            if (value == Watch)
                return;
            _watchStopped = false;
            _chat.Panels.WatchChanges = value;
            OnPropertyChanged();
            _chat.SavePanels();
            if (value)
                GoToLatest();
        }
    }

    /// <summary>The user scrolled the diff or picked something: it stays where they took it.</summary>
    internal void StopWatching()
    {
        if (!Watch)
            return;
        _watchStopped = true;
        OnPropertyChanged(nameof(Watch));
    }

    /// <summary>The lines the agent changed last, lit up while they are fresh; empty before it changed one with the panel open.</summary>
    private List<DiffRow> _latest = [];

    private readonly DispatcherTimer _fade;

    /// <summary>How long a change stays lit before it fades (over 2 s, Border.fresh in the panel): 1.5 s and 1 s were gone before the eye got there (the user, 2026-10-27).</summary>
    private static readonly TimeSpan LitFor = TimeSpan.FromSeconds(4);

    /// <summary>Set while a refresh picks the same lines again: no pick by hand, so watching goes on.</summary>
    private bool _refreshing;

    /// <summary>Watch turned on: its file selected, the last change in sight and lit up again.</summary>
    private void GoToLatest()
    {
        if (_latest.Count == 0 || _latest[0].Index >= Rows.Count || !ReferenceEquals(Rows[_latest[0].Index], _latest[0]))
            return;
        Reselecting = true;
        try { CurrentFile = _latest[0].File; }
        finally { Reselecting = false; }
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
        if (Selection.Count > 0)
            return Picks()
                .Select(p => (p.File, (IReadOnlyCollection<(int? Old, int? New)>?)p.Rows.Where(r => r.IsAdded || r.IsRemoved)
                    .Select(r => r.IsAdded ? ((int?)null, r.Line!.Value.New) : (r.Line!.Value.Old, (int?)null)).ToList()))
                .Where(p => p.Item2!.Count > 0).ToList();
        return CurrentFile is { Row: >= 0 } current ? [(current, null)] : [];
    }

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
            if (targets.Count > 1)
                return $"{targets.Sum(t => t.Lines?.Count ?? 0)} lines in {targets.Select(t => t.File.FullPath.ToUpperInvariant()).Distinct().Count()} files";
            var (file, lines) = targets[0];
            var where = file.Section is null ? "" : " · " + file.SectionWord;
            if (lines is null)
                return Path.GetFileName(file.FullPath) + where;
            var rows = Picks().First(p => p.File == file).Rows.Where(r => r.IsAdded || r.IsRemoved).ToList();
            return $"{Capital(Lines(rows))} · {Path.GetFileName(file.FullPath)}{where}";
        }
    }

    /// <summary>"the picked lines", else the selected file's name, for the tooltips.</summary>
    private string What => Selection.Count > 0 ? "the picked lines" : CurrentFile is { } file ? Path.GetFileName(file.FullPath) : "the file";

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
            if (targets[0].Lines is null && targets[0].File.IsNew)
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

    private static void ToRecycleBin(string path) =>
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
            // A click on a hunk's @@ line picks its lines, on a file's name all the file's (Ctrl held: as
            // well as what was picked). A header or @@ line is no line itself: it doesn't stay picked.
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
            // Lines picked by hand stay in sight: the agent's next edit doesn't scroll them away.
            if (!_refreshing && Selection.Count > 0)
                StopWatching();
            // A file picked in two sections (staged and unstaged) is one card in the box.
            _chat.Pick(Picks().GroupBy(p => p.File.FullPath, StringComparer.OrdinalIgnoreCase)
                .Select(g => (g.Key, $"{Capital(Lines(g.SelectMany(p => p.Rows).ToList()))} · {Path.GetFileName(g.Key)}")).ToList());
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
        if (picks.Count == 0)
        {
            if (CurrentFile is not { } current)
            {
                context.Append("What they say is about these changes unless they say otherwise.]");
                return (via, context.ToString());
            }
            // No lines picked: the file selected in the list, whole, as its card in the box said.
            context.Append($"They have {current.FullPath} ({Where(current)}{current.Status}, {current.Counts}) selected in it: what they say is about the change to that file unless they say otherwise. Its diff:\n");
            var lines = current.File.Lines;
            foreach (var line in lines.Take(MaxPickedLines))
                AppendLine(context, line);
            if (lines.Count > MaxPickedLines)
                context.Append($"[... {lines.Count - MaxPickedLines} more lines of its diff]\n");
            context.Append(Numbers);
            return ($"File changes · {Path.GetFileName(current.FullPath)}", context.ToString());
        }
        context.Append(picks.Count == 1 ? "They selected these lines in it" : "They selected lines in it").Append(", which is what they refer to:");
        var shown = 0;
        foreach (var (file, rows) in picks)
        {
            context.Append($"\n{file.FullPath} ({Where(file)}{file.Status}), {Lines(rows)}:\n");
            foreach (var row in rows)
            {
                if (shown++ == MaxPickedLines)
                {
                    context.Append($"[... {picks.Sum(p => p.Rows.Count) - MaxPickedLines} more selected lines]\n");
                    break;
                }
                AppendLine(context, row.Line!.Value);
            }
            if (shown > MaxPickedLines)
                break;
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


    /// <summary>The message went with the picked lines: they are no longer picked.</summary>
    public void Sent()
    {
        _picking = true;
        try { Selection.Clear(); }
        finally { _picking = false; }
    }

    /// <summary>A click away from the lines, or Esc: none is picked, and their files leave the box. False when none was.</summary>
    public bool Unpick()
    {
        if (Selection.Count == 0)
            return false;
        Selection.Clear();
        return true;
    }

    /// <summary>A file taken off the box (its ×): its lines are no longer picked here either.</summary>
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
