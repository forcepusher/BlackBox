using System.Collections.Specialized;
using System.Globalization;
using System.Text;
using Avalonia.Controls.Selection;
using Avalonia.Threading;
using BlackBox.Core.Diffs;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

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
public sealed class DiffRow(DiffFileItem file, DiffLine? line)
{
    public DiffFileItem File { get; } = file;

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
/// The Code changes panel, a tool of the chat window's toolkit on the left of the chat: the files the
/// agent changed in this chat (write_file, edit_file, and what its commands changed), each against
/// what it held before the agent first changed it, updated as the agent works. Lines picked in it
/// (click, Shift+click, Ctrl+click) go into the chat's box as the files they are in, and the message
/// sent from there carries them for the model: the user asks about them, has them explained,
/// reviewed or changed (the user's request, 2026-10-24).
/// </summary>
public sealed partial class DiffViewModel : ObservableObject, IToolkit
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
    public DiffViewModel(MainWindowViewModel chat)
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

    private const string Hint = "Click lines to put them in your message; Shift+click picks a range, Ctrl+click adds more.";

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
        _picking = true;
        try
        {
            Selection.Source = null;
            Rows = rows;
            Files = files;
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
        OnPicked();
        Folder = diff.Root.Length > 0 ? diff.Root : null;
        // "2 files · +14", as each file's counts leave out what is none.
        var counts = string.Join(" ", new[] { diff.Added > 0 ? $"+{diff.Added}" : null, diff.Removed > 0 ? $"-{diff.Removed}" : null }.OfType<string>());
        Summary = diff.Files.Count == 0 ? "" : $"{diff.Files.Count} {(diff.Files.Count == 1 ? "file" : "files")}" + (counts.Length > 0 ? " · " + counts : "");
    }

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
            _chat.Pick(Picks().Select(p => (p.File.FullPath, $"{Capital(Lines(p.Rows))} · {Path.GetFileName(p.File.FullPath)}")).ToList());
        }
        finally
        {
            _picking = false;
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
        var via = "Code changes" + (picks.Count == 0 ? "" : " · " + string.Join(", ", picks.Select(p => $"{Path.GetFileName(p.File.FullPath)} {Lines(p.Rows)}")));
        var context = new StringBuilder("[The Code changes panel is open beside the chat, showing the files you changed in this chat, each against what it held before your first change to it: ")
            .Append(string.Join(", ", diff.Files.Take(30).Select(f => $"{diff.FullPath(f)} ({f.Status}, {f.Counts})")))
            .Append(diff.Files.Count > 30 ? $" and {diff.Files.Count - 30} more. " : ". ");
        if (picks.Count == 0)
        {
            context.Append("What they say is about these changes unless they say otherwise.]");
            return (via, context.ToString());
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
                var line = row.Line!.Value;
                var number = line.Kind == DiffLineKind.Removed ? $"(was {line.Old})" : $"{line.New}";
                context.Append(line.Kind switch { DiffLineKind.Added => "+", DiffLineKind.Removed => "-", _ => " " })
                    .Append(' ').Append(number).Append('\t').Append(line.Text).Append('\n');
            }
            if (shown > MaxPickedLines)
                break;
        }
        context.Append("(+ added, - removed, the rest unchanged; numbers are lines of the file as it is now.)]");
        return (via, context.ToString());
    }

    private static string Capital(string text) => text.Length > 0 ? char.ToUpperInvariant(text[0]) + text[1..] : text;

    /// <summary>The message went with the picked lines: they are no longer picked.</summary>
    public void Sent()
    {
        _picking = true;
        try { Selection.Clear(); }
        finally { _picking = false; }
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
        _loading?.Cancel();
    }
}
