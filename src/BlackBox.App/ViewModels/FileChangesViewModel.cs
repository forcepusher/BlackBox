using System.Collections.Specialized;
using System.Globalization;
using System.Text;
using Avalonia.Controls.Selection;
using Avalonia.Threading;
using BlackBox.Core.Diffs;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using static BlackBox.Core.Strings;

namespace BlackBox.App.ViewModels;

/// <summary>A file in the panel's list of changed files: a click there goes to it in the diff.</summary>
public sealed record DiffFileItem(DiffFile File, int Row, string FullPath)
{
    public string Name => File.Display;
    public string Status => File.Status;
    public string Counts => File.Counts;
    public bool IsNew => File.Status == "added";
    public bool IsDeleted => File.Status == "deleted";

    public override string ToString() => Name;
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

    /// <summary>What the header says after the name: "modified · +10 -3".</summary>
    public string HeaderDetails => $"{File.Status} · {File.Counts}";

    /// <summary>What screen readers and UI Automation call it: "48 + text", the file's name for a header.</summary>
    public override string ToString() => IsHeader ? $"{File.Name}, {HeaderDetails}" : $"{(NewNumber.Length > 0 ? NewNumber : OldNumber)} {Marker} {Text}".Replace("  ", " ");

    /// <summary>Says which row this is again after a refresh, to keep it picked.</summary>
    internal (string Path, DiffLineKind Kind, int? Old, int? New, string Text) Key => (File.FullPath, Line?.Kind ?? DiffLineKind.Hunk, Line?.Old, Line?.New, Line?.Text ?? "");
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
        Selection.SelectionChanged += (_, _) => OnPicked();
        // Calls that come one after another (a file written in parts) are read once.
        _refreshTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, (_, _) =>
        {
            _refreshTimer!.Stop();
            _ = LoadAsync();
        });
        _fade = new DispatcherTimer(TimeSpan.FromMilliseconds(1500), DispatcherPriority.Background, (_, _) =>
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
    public (string Title, string Details)? Whole => CurrentFile is { } file ? (Path.GetFileName(file.FullPath), $"whole file · {file.Status} · {file.Counts}") : null;

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

    private const string Hint = "Click or drag lines to put them in your message; Shift+click picks a range, Ctrl+click adds more.";

    /// <summary>Reads the files again, keeping what was picked where the same lines are still there.</summary>
    private async Task LoadAsync()
    {
        _loading?.Cancel();
        var loading = _loading = new CancellationTokenSource();
        var changes = _chat.Changes;
        DiffResult diff;
        try
        {
            diff = await Task.Run(changes.Diff, loading.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        if (!ReferenceEquals(loading, _loading))
            return;
        Show(diff, keepPicks: ReferenceEquals(changes, _shownChanges));
        _shownChanges = changes;
    }

    /// <summary>Whose list is shown: picks are kept across a refresh of the same chat's, not into another chat's.</summary>
    private FileChanges? _shownChanges;

    private void Show(DiffResult diff, bool keepPicks)
    {
        var picked = keepPicks ? Selection.SelectedItems.OfType<DiffRow>().Select(r => r.Key).ToHashSet() : [];
        var current = keepPicks ? CurrentFile?.FullPath : null;
        var before = keepPicks ? _diff : null;
        _diff = diff;
        Status = Hint;
        var rows = new List<DiffRow>();
        var files = new List<DiffFileItem>();
        foreach (var file in diff.Files)
        {
            if (rows.Count >= MaxRows)
            {
                Status = $"Too much changed to show it all: the diff stops in {files[^1].Name}.";
                break;
            }
            var item = new DiffFileItem(file, rows.Count, diff.FullPath(file));
            files.Add(item);
            rows.Add(new DiffRow(item, null) { Index = rows.Count });
            foreach (var line in file.Lines.Take(MaxRows - rows.Count))
                rows.Add(new DiffRow(item, line) { Index = rows.Count });
        }
        // Another chat's list, or the first read, has no "just now": nothing lights up.
        _fade.Stop();
        _latest = before is null ? [] : Fresh(before, rows);
        foreach (var row in _latest)
            row.IsFresh = true;
        if (_latest.Count > 0)
        {
            _fade.Start();
            // Watching, the file changed is the one selected, as a click on it would.
            if (Watch)
                current = _latest[0].File.FullPath;
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
                CurrentFile = files.FirstOrDefault(f => string.Equals(f.FullPath, current, StringComparison.OrdinalIgnoreCase)) ?? files.FirstOrDefault();
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
        var counts = string.Join(" ", new[] { diff.Added > 0 ? $"+{diff.Added}" : null, diff.Removed > 0 ? $"-{diff.Removed}" : null }.OfType<string>());
        Summary = diff.Files.Count == 0 ? "" : $"{diff.Files.Count} {(diff.Files.Count == 1 ? "file" : "files")}" + (counts.Length > 0 ? " · " + counts : "");
        if (Watch && _latest.Count > 0)
            ShowRow?.Invoke(_latest[0].Index);
    }

    /// <summary>
    /// The added and removed lines in <paramref name="rows"/> that <paramref name="before"/> didn't
    /// have, file by file: what the agent changed since the last read. Counted by their text, as the
    /// numbers of every line below an edit move.
    /// </summary>
    public static List<DiffRow> Fresh(DiffResult before, IReadOnlyList<DiffRow> rows)
    {
        var had = new Dictionary<(string, DiffLineKind, string), int>();
        foreach (var file in before.Files)
        {
            var path = before.FullPath(file).ToUpperInvariant();
            foreach (var line in file.Lines.Where(l => l.Kind is DiffLineKind.Added or DiffLineKind.Removed))
                had[(path, line.Kind, line.Text)] = had.GetValueOrDefault((path, line.Kind, line.Text)) + 1;
        }
        var fresh = new List<DiffRow>();
        foreach (var row in rows.Where(r => r.IsAdded || r.IsRemoved))
        {
            var key = (row.File.FullPath.ToUpperInvariant(), row.Line!.Value.Kind, row.Line.Value.Text);
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
    private void GoTo(DiffFileItem file) => GoToRow?.Invoke(file.Row);

    // ---- what is picked, into the chat's box -----------------------------------------------------------

    /// <summary>The picked lines into the box, as the files they are in ("Lines 12-18 · DiffTool.cs": the lines first, as a long name is cut short there).</summary>
    private void OnPicked()
    {
        if (_picking)
            return;
        _picking = true;
        try
        {
            // A file's header and a hunk's @@ line are no lines of it: a click there picks nothing.
            foreach (var row in Selection.SelectedItems.OfType<DiffRow>().Where(r => !r.IsLine).ToList())
                Selection.Deselect(row.Index);
            // Lines picked by hand stay in sight: the agent's next edit doesn't scroll them away.
            if (!_refreshing && Selection.Count > 0)
                StopWatching();
            _chat.Pick(Picks().Select(p => (p.File.FullPath, $"{Capital(Lines(p.Rows))} · {Path.GetFileName(p.File.FullPath)}")).ToList());
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
        var context = new StringBuilder("[The File changes panel is open beside the chat, showing the files you changed in this chat, each against what it held before your first change to it: ")
            .Append(string.Join(", ", diff.Files.Take(30).Select(f => $"{diff.FullPath(f)} ({f.Status}, {f.Counts})")))
            .Append(diff.Files.Count > 30 ? $" and {diff.Files.Count - 30} more. " : ". ");
        if (picks.Count == 0)
        {
            if (CurrentFile is not { } current)
            {
                context.Append("What they say is about these changes unless they say otherwise.]");
                return (via, context.ToString());
            }
            // No lines picked: the file selected in the list, whole, as its card in the box said.
            context.Append($"They have {current.FullPath} ({current.Status}, {current.Counts}) selected in it: what they say is about the change to that file unless they say otherwise. Its diff:\n");
            var lines = current.File.Lines;
            foreach (var line in lines.Take(MaxPickedLines))
                AppendLine(context, line);
            if (lines.Count > MaxPickedLines)
                context.Append($"[... {lines.Count - MaxPickedLines} more lines of its diff]\n");
            context.Append("(+ added, - removed, the rest unchanged; numbers are lines of the file as it is now.)]");
            return ($"File changes · {Path.GetFileName(current.FullPath)}", context.ToString());
        }
        context.Append(picks.Count == 1 ? "They selected these lines in it" : "They selected lines in it").Append(", which is what they refer to:");
        var shown = 0;
        foreach (var (file, rows) in picks)
        {
            context.Append($"\n{file.FullPath} ({file.Status}), {Lines(rows)}:\n");
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
        context.Append("(+ added, - removed, the rest unchanged; numbers are lines of the file as it is now.)]");
        return (via, context.ToString());
    }

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
