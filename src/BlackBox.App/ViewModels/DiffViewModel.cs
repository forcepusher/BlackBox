using System.Collections.Specialized;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia.Controls.Selection;
using Avalonia.Threading;
using BlackBox.Core.Diffs;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BlackBox.App.ViewModels;

/// <summary>What the Diff panel compares in its repository.</summary>
public sealed record DiffMode(string Name, string Tip, bool TakesRef = false)
{
    public override string ToString() => Name;
}

/// <summary>A file in the panel's list of changed files: a click there goes to it in the diff.</summary>
public sealed record DiffFileItem(DiffFile File, int Row)
{
    public string Name => File.Display;
    public string Status => File.Status;
    public string Counts => File.Counts;
    public bool IsNew => File.Status is "added" or "not tracked yet";
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
    public bool IsHunk => Line?.Kind == DiffLineKind.Hunk;
    public bool IsAdded => Line?.Kind == DiffLineKind.Added;
    public bool IsRemoved => Line?.Kind == DiffLineKind.Removed;

    /// <summary>A line of the file that can be picked: added, removed or kept, not a header.</summary>
    public bool IsLine => Line?.Kind is DiffLineKind.Added or DiffLineKind.Removed or DiffLineKind.Context;

    public string OldNumber => Line?.Old?.ToString(CultureInfo.InvariantCulture) ?? "";
    public string NewNumber => Line?.New?.ToString(CultureInfo.InvariantCulture) ?? "";
    public string Marker => Line?.Kind switch { DiffLineKind.Added => "+", DiffLineKind.Removed => "-", _ => "" };

    public string Text => Line is { } l ? l.Kind == DiffLineKind.Note ? "\\ " + l.Text : l.Text : File.Name;

    /// <summary>What the header says after the name: "modified · +10 -3".</summary>
    public string HeaderDetails => $"{File.Status} · {File.Counts}";

    /// <summary>What screen readers and UI Automation call it: "48 + text", the file's name for a header.</summary>
    public override string ToString() => IsHeader ? $"{File.Name}, {HeaderDetails}" : $"{(NewNumber.Length > 0 ? NewNumber : OldNumber)} {Marker} {Text}".Replace("  ", " ");

    /// <summary>Says which row this is again after a refresh, to keep it picked.</summary>
    internal (string Path, DiffLineKind Kind, int? Old, int? New, string Text) Key => (File.File.Path, Line?.Kind ?? DiffLineKind.Hunk, Line?.Old, Line?.New, Line?.Text ?? "");
}

/// <summary>
/// The Diff panel, a tool of the chat window's toolkit on the left of the chat: the changes in a git
/// repository (what is uncommitted, staged, the last commit, a commit, or since a branch), file by
/// file. Lines picked in it (click, Shift+click, Ctrl+click) go into the chat's box as the files they
/// are in, and the message sent from there carries them for the model: the user asks about them,
/// has them explained, reviewed or changed (the user's request, 2026-10-24). It shows what the
/// agent changes as it changes it.
/// </summary>
public sealed partial class DiffViewModel : ObservableObject, IToolkit
{
    private readonly MainWindowViewModel _chat;
    private readonly string _stateFile;
    private readonly DispatcherTimer _refreshTimer;
    private FileSystemWatcher? _watcher;
    private CancellationTokenSource? _loading;
    private bool _picking;

    /// <summary>Rows shown at most: a diff of a whole generated folder would make the list slow to scroll.</summary>
    private const int MaxRows = 30_000;

    /// <summary>Lines of a pick the model is given at most, with the count of the rest.</summary>
    private const int MaxPickedLines = 300;

    public IReadOnlyList<DiffMode> Modes { get; } =
    [
        new("Uncommitted changes", "What changed since the last commit, staged or not, and files not tracked yet"),
        new("Staged changes", "What is staged for the next commit"),
        new("Last commit", "What the latest commit did"),
        new("A commit", "What one commit did: type its hash or a branch", TakesRef: true),
        new("Since a commit or branch", "What changed since a commit, branch or tag, up to the files as they are now", TakesRef: true),
    ];

    /// <param name="chat">The chat the panel is a tool of, where messages about the picked lines go.</param>
    /// <param name="dataDir">BlackBox's Data folder, where the folders opened in it are remembered (diff.json).</param>
    public DiffViewModel(MainWindowViewModel chat, string dataDir)
    {
        _chat = chat;
        _stateFile = Path.Combine(dataDir, "diff.json");
        _mode = Modes[0];
        Selection = new SelectionModel<DiffRow> { SingleSelect = false };
        Selection.SelectionChanged += (_, _) => OnPicked();
        _refreshTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(1200), DispatcherPriority.Background, (_, _) =>
        {
            _refreshTimer!.Stop();
            _ = LoadAsync(keepPicks: true);
        });
        _chat.Attachments.CollectionChanged += OnBoxChanged;
        Recent = LoadRecent();
        if (Recent.FirstOrDefault(Directory.Exists) is { } last)
            Open(last);
        else
            _ = OpenFromChatAsync();
    }

    // ---- the repository and what is compared ------------------------------------------------------

    /// <summary>The repository shown (its top folder); null before one is opened.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FolderName), nameof(HasFolder), nameof(EmptyText))]
    private string? _folder;

    public bool HasFolder => Folder is not null;

    /// <summary>On the button at the top that opens the folders' menu.</summary>
    public string FolderName => Folder is null ? "Choose a folder" : Path.GetFileName(Path.TrimEndingDirectorySeparator(Folder)) is { Length: > 0 } name ? name : Folder;

    /// <summary>Folders opened in the panel before, the latest first.</summary>
    public IReadOnlyList<string> Recent { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TakesRef))]
    private DiffMode _mode;

    // With no commit typed yet for it, the panel says to type one, and the diff of another mode goes.
    partial void OnModeChanged(DiffMode value) => _ = LoadAsync(keepPicks: false);

    public bool TakesRef => Mode.TakesRef;

    /// <summary>The commit or branch for "A commit" and "Since a commit or branch".</summary>
    [ObservableProperty]
    private string _ref = "";

    /// <summary>The commit or branch typed in: Enter in its box shows it.</summary>
    [RelayCommand]
    private Task ApplyRef() => LoadAsync(keepPicks: false);

    [RelayCommand]
    private Task Refresh() => LoadAsync(keepPicks: true);

    /// <summary>Opens the repository a folder (or a file) is in; one that is in none says so.</summary>
    public void Open(string folder) => _ = OpenAsync(folder);

    private async Task OpenAsync(string folder)
    {
        string? root;
        try
        {
            root = await Task.Run(() => GitDiff.RootOfAsync(folder, CancellationToken.None));
        }
        catch (DiffException ex)
        {
            Status = ex.Message;
            return;
        }
        if (root is null)
        {
            Status = $"{folder} is not in a git repository.";
            return;
        }
        Folder = root;
        Remember(root);
        Watch();
        await LoadAsync(keepPicks: false);
    }

    /// <summary>With no folder opened before: the repository the chat's tool calls worked in last, if one.</summary>
    private async Task OpenFromChatAsync()
    {
        foreach (var dir in ChatFolders())
        {
            try
            {
                if (await Task.Run(() => GitDiff.RootOfAsync(dir, CancellationToken.None)) is { } root)
                {
                    if (Folder is null)
                        Open(root);
                    return;
                }
            }
            catch (DiffException ex)
            {
                Status = ex.Message;
                return;
            }
        }
        Status = "Choose a folder with a git repository at the top.";
    }

    /// <summary>The folders the chat's tool calls named, the latest first.</summary>
    internal List<string> ChatFolders()
    {
        var folders = new List<string>();
        foreach (var call in _chat.Items.OfType<ToolCallItem>().Reverse().Take(200))
        {
            JsonObject? args;
            try { args = JsonNode.Parse(call.Arguments.Length > 0 ? call.Arguments : "{}") as JsonObject; }
            catch (JsonException) { continue; }
            if (args is null || args["host"] is not null)
                continue;
            foreach (var key in new[] { "path", "working_directory" })
                if (args[key] is JsonValue v && v.TryGetValue<string>(out var path) && path.Trim().Length > 0)
                {
                    try
                    {
                        var full = Path.GetFullPath(Environment.ExpandEnvironmentVariables(path.Trim()));
                        var dir = Directory.Exists(full) ? full : Path.GetDirectoryName(full);
                        if (dir is not null && Directory.Exists(dir) && !folders.Contains(dir, StringComparer.OrdinalIgnoreCase))
                            folders.Add(dir);
                    }
                    catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { }
                }
            if (folders.Count >= 12)
                break;
        }
        return folders;
    }

    // ---- the diff -------------------------------------------------------------------------------

    /// <summary>The diff shown now; null before one is read.</summary>
    private DiffResult? _diff;

    /// <summary>Every row of the diff, the files one after another.</summary>
    [ObservableProperty]
    private IReadOnlyList<DiffRow> _rows = [];

    /// <summary>The changed files, for the list beside the diff.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFiles), nameof(EmptyText))]
    private IReadOnlyList<DiffFileItem> _files = [];

    public bool HasFiles => Files.Count > 0;

    /// <summary>What the panel says where the diff would be when it has none.</summary>
    public string EmptyText => Folder is null ? "Choose a folder with a git repository at the top: its changes show here." : _diff is null ? "" : "No changes here.";

    /// <summary>The rows picked; their lines go into the chat's box.</summary>
    public SelectionModel<DiffRow> Selection { get; }

    /// <summary>What was compared and how much changed ("Uncommitted changes · 3 files · +40 -12"), or what went wrong.</summary>
    [ObservableProperty]
    private string _summary = "";

    /// <summary>A commit's hash, author, date and message, over the diff when one commit is shown.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCommitNote))]
    private string? _commitNote;

    public bool HasCommitNote => CommitNote is not null;

    /// <summary>The line under the top bar: what the panel did or what went wrong, else how it is used.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusOrHint))]
    private string _status = "";

    public string StatusOrHint => Status.Length > 0 ? Status : "Click lines to put them in your message; Shift+click picks a range, Ctrl+click adds more.";

    /// <summary>Reads the diff again, keeping what was picked where the same lines are still there.</summary>
    private async Task LoadAsync(bool keepPicks)
    {
        if (Folder is not { } folder)
            return;
        if (Mode.TakesRef && Ref.Trim().Length == 0)
        {
            _loading?.Cancel();
            Show(null, keepPicks: false);
            Status = Mode == Modes[3] ? "Type a commit's hash or a branch, then press Enter." : "Type a commit, branch or tag to compare with, then press Enter.";
            return;
        }
        _loading?.Cancel();
        var loading = _loading = new CancellationTokenSource();
        var request = Mode.Name switch
        {
            "Staged changes" => new DiffRequest(folder, Staged: true),
            "Last commit" => new DiffRequest(folder, Commit: "HEAD"),
            "A commit" => new DiffRequest(folder, Commit: Ref.Trim()),
            "Since a commit or branch" => new DiffRequest(folder, Against: Ref.Trim()),
            _ => new DiffRequest(folder),
        };
        DiffResult diff;
        try
        {
            diff = await Task.Run(async () =>
            {
                var read = await GitDiff.ChangesAsync(request, loading.Token);
                // Files not tracked yet as all new: what the agent just made is among them.
                var added = read.Untracked.Select(u => GitDiff.AsAdded(read.Root, u)).OfType<DiffFile>().ToList();
                return added.Count == 0 ? read : read with { Files = [.. read.Files, .. added], Untracked = read.Untracked.Where(u => added.All(a => a.Path != u.Path)).ToList() };
            }, loading.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (DiffException ex)
        {
            if (!ReferenceEquals(loading, _loading))
                return;
            Show(null, keepPicks: false);
            Status = ex.Message;
            return;
        }
        if (!ReferenceEquals(loading, _loading))
            return;
        Status = "";
        Show(diff, keepPicks);
    }

    private void Show(DiffResult? diff, bool keepPicks)
    {
        var picked = keepPicks ? Selection.SelectedItems.OfType<DiffRow>().Select(r => r.Key).ToHashSet() : [];
        _diff = diff;
        var rows = new List<DiffRow>();
        var files = new List<DiffFileItem>();
        foreach (var file in diff?.Files ?? [])
        {
            if (rows.Count >= MaxRows)
            {
                Status = $"The diff is too long to show whole: it stops in {files[^1].Name}.";
                break;
            }
            var item = new DiffFileItem(file, rows.Count);
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
        CommitNote = diff?.About;
        Summary = diff is null ? "" : diff.Files.Count == 0
            ? $"{diff.Title} · no changes" + (diff.Untracked.Count > 0 ? $" · {diff.Untracked.Count} files not tracked yet" : "")
            : $"{diff.Title} · {diff.Files.Count} {(diff.Files.Count == 1 ? "file" : "files")} · +{diff.Added} -{diff.Removed}";
        if (diff?.Truncated == true)
            Status = "The diff is too long to read whole: the last files are left out.";
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
            _chat.Pick(Picks().Select(p => (p.FullPath, $"{Capital(Lines(p.Rows))} · {Path.GetFileName(p.File.File.Path)}")).ToList());
        }
        finally
        {
            _picking = false;
        }
    }

    /// <summary>The picked lines, file by file in the diff's order.</summary>
    private List<(DiffFileItem File, string FullPath, List<DiffRow> Rows)> Picks()
    {
        if (_diff is not { } diff)
            return [];
        return Selection.SelectedItems.OfType<DiffRow>().Where(r => r.IsLine)
            .GroupBy(r => r.File)
            .OrderBy(g => g.Key.Row)
            .Select(g => (g.Key, diff.FullPath(g.Key.File), g.OrderBy(r => r.Index).ToList()))
            .ToList();
    }

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
    /// What a message sent while the panel is open carries: over its bubble, the repository and the
    /// lines picked; for the model, what the panel shows, and the picked lines with their numbers
    /// and text. Neutral about what is to be done with them: the user may ask about them, have them
    /// explained or reviewed, or changed.
    /// </summary>
    public (string Via, string Context)? About(IReadOnlyList<string> files)
    {
        if (_diff is not { } diff || Folder is not { } folder)
            return null;
        var picks = Picks().Where(p => files.Contains(p.FullPath, StringComparer.OrdinalIgnoreCase)).ToList();
        var via = $"Code diff · {FolderName}" + (picks.Count == 0 ? "" : " · " + string.Join(", ", picks.Select(p => $"{Path.GetFileName(p.File.File.Path)} {Lines(p.Rows)}")));
        var context = new StringBuilder($"[The Code diff panel is open beside the chat, showing {Lower(diff.Title)} in {folder} ({DiffCall()} shows the same). ");
        if (picks.Count == 0)
        {
            context.Append("What they say is about these changes unless they say otherwise.]");
            return (via, context.ToString());
        }
        context.Append(picks.Count == 1 ? "They selected these lines in it" : "They selected lines in it")
            .Append(diff.NewIsOnDisk ? "" : $" (numbered as in {Lower(diff.Title)}: the files on disk may differ now)")
            .Append(", which is what they refer to:");
        var shown = 0;
        foreach (var (file, fullPath, rows) in picks)
        {
            context.Append($"\n{fullPath} ({file.Status}), {Lines(rows)}:\n");
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
        context.Append(diff.NewIsOnDisk ? "(+ added, - removed, the rest unchanged; numbers are lines of the file as it is now.)]" : "(+ added, - removed, the rest unchanged.)]");
        return (via, context.ToString());
    }

    /// <summary>The diff tool call that shows what the panel shows.</summary>
    private string DiffCall() => Mode.Name switch
    {
        "Staged changes" => $"diff path=\"{Folder}\" staged=true",
        "Last commit" => $"diff path=\"{Folder}\" commit=\"HEAD\"",
        "A commit" => $"diff path=\"{Folder}\" commit=\"{Ref.Trim()}\"",
        "Since a commit or branch" => $"diff path=\"{Folder}\" against=\"{Ref.Trim()}\"",
        _ => $"diff path=\"{Folder}\"",
    };

    private static string Capital(string text) => text.Length > 0 ? char.ToUpperInvariant(text[0]) + text[1..] : text;

    private static string Lower(string title) => title.Length > 0 ? char.ToLowerInvariant(title[0]) + title[1..] : title;

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
        if (_picking || e.OldItems is null || _diff is not { } diff)
            return;
        var gone = e.OldItems.OfType<AttachmentItem>().Where(a => a.Picked).Select(a => a.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (gone.Count == 0 || _chat.Attachments.Any(a => a.Picked && gone.Contains(a.Path)))
            return;
        _picking = true;
        try
        {
            for (var i = 0; i < Rows.Count; i++)
                if (Selection.IsSelected(i) && gone.Contains(diff.FullPath(Rows[i].File.File)))
                    Selection.Deselect(i);
        }
        finally
        {
            _picking = false;
        }
    }

    public bool Drop(IReadOnlyList<string> paths)
    {
        // A folder dropped on the panel is opened in it.
        if (paths is [var one] && Directory.Exists(one))
        {
            Open(one);
            return true;
        }
        return false;
    }

    // ---- watching the repository ------------------------------------------------------------------

    /// <summary>What changes in the repository shows here soon after: the agent's edits as it makes them.</summary>
    private void Watch()
    {
        _watcher?.Dispose();
        _watcher = null;
        if (Folder is not { } folder)
            return;
        try
        {
            _watcher = new FileSystemWatcher(folder) { IncludeSubdirectories = true, NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size };
            _watcher.Changed += OnFileChanged;
            _watcher.Created += OnFileChanged;
            _watcher.Deleted += OnFileChanged;
            _watcher.Renamed += OnFileChanged;
            _watcher.EnableRaisingEvents = true;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or PlatformNotSupportedException)
        {
            _watcher = null;
        }
    }

    private void OnFileChanged(object sender, FileSystemEventArgs e)
    {
        // git's own objects and logs change with every look it takes; its index and HEAD say what is staged and committed.
        var path = e.FullPath.Replace('\\', '/');
        if (path.Contains("/.git/objects/", StringComparison.OrdinalIgnoreCase) || path.Contains("/.git/logs/", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".lock", StringComparison.OrdinalIgnoreCase))
            return;
        Dispatcher.UIThread.Post(() =>
        {
            _refreshTimer.Stop();
            _refreshTimer.Start();
        });
    }

    // ---- folders opened before ---------------------------------------------------------------------

    private List<string> LoadRecent()
    {
        try
        {
            if (File.Exists(_stateFile) && JsonNode.Parse(File.ReadAllText(_stateFile))?["recent"] is JsonArray list)
                return list.Select(n => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null).OfType<string>().ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
        return [];
    }

    private void Remember(string folder)
    {
        var recent = Recent.Where(r => !string.Equals(r, folder, StringComparison.OrdinalIgnoreCase)).Prepend(folder).Take(8).ToList();
        Recent = recent;
        OnPropertyChanged(nameof(Recent));
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_stateFile)!);
            File.WriteAllText(_stateFile, new JsonObject { ["recent"] = new JsonArray(recent.Select(r => (JsonNode)r).ToArray()) }.ToJsonString());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    public void Dispose()
    {
        _chat.Attachments.CollectionChanged -= OnBoxChanged;
        _refreshTimer.Stop();
        _loading?.Cancel();
        _watcher?.Dispose();
        _watcher = null;
    }
}
