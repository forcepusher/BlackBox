using System.ComponentModel;
using System.Text;
using Avalonia.Threading;
using AvaloniaEdit.Document;
using Sloperator.Core.Projects;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Sloperator.App.ViewModels;

/// <summary>A file edited in the Code editor panel and not saved yet: the file as it was read, and its text as typed.</summary>
public sealed class CodeDraft(CodeFile file, TextDocument document)
{
    public CodeFile File { get; } = file;
    public TextDocument Document { get; } = document;
}

/// <summary>
/// The Code editor panel, one of the chat window's side panels on the left of the chat: a folder's
/// files in a tree, and one of them open to read and edit (Ctrl+S saves it), as a bare VS Code. The
/// folder is the one the user opened in the chat (Open folder, or one dropped on the panel), else the
/// project the agent changed files in. Opening a file puts nothing in the chat's box: only lines the
/// user selects go there, and the message sent from there carries them for the model.
/// </summary>
public sealed partial class CodeEditorViewModel : ObservableObject, IFilePanel
{
    private readonly MainWindowViewModel _chat;

    /// <summary>The chat's folder read again a moment after its changes or another chat came.</summary>
    private readonly DispatcherTimer _followTimer;

    /// <summary>What the disk said changed, read once a burst of it is over (a build writes hundreds of files).</summary>
    private readonly DispatcherTimer _diskTimer;

    private FileSystemWatcher? _watcher;
    private readonly Lock _gate = new();

    /// <summary>Folders the disk said changed since the last read, "*" for all of them; under <see cref="_gate"/>.</summary>
    private readonly HashSet<string> _changedFolders = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The open file changed on disk since the last read; under <see cref="_gate"/>.</summary>
    private bool _openFileChanged;

    /// <summary>A note to the window's thread is on its way; under <see cref="_gate"/>.</summary>
    private bool _diskPosted;

    private CancellationTokenSource? _opening;

    /// <summary>Lines of a pick the model is given at most, in characters (as the File inspector's).</summary>
    private const int MaxPickedChars = 20_000;

    /// <param name="chat">The chat the panel is beside: its folder, and where messages about the selected lines go.</param>
    public CodeEditorViewModel(MainWindowViewModel chat)
    {
        _chat = chat;
        Tree = new FileTree(path => chat.CodeDrafts.ContainsKey(path));
        _followTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, (_, _) =>
        {
            _followTimer!.Stop();
            _ = FollowChatAsync();
        });
        _diskTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, (_, _) =>
        {
            _diskTimer!.Stop();
            _ = OnDiskChangedAsync();
        });
        _watch = chat.Panels.WatchCode;
        _wrap = chat.Panels.WrapCode;
        // A change made before the panel opened isn't followed: only the agent's next one.
        (_followedList, _followedNumber) = (chat.Changes, chat.Changes.LastChange?.Number ?? 0);
        _chat.ChangesUpdated += OnChangesUpdated;
        _ = FollowChatAsync();
    }

    // ---- the folder -----------------------------------------------------------------------------

    /// <summary>The folder whose files the tree shows; null with none.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFolder), nameof(Status), nameof(EmptyText))]
    private string? _folder;

    public bool HasFolder => Folder is not null;

    /// <summary>What the panel's bar says: the folder, else how the panel is used.</summary>
    public string Status => Folder ?? "Open a folder, then select lines to ask about them.";

    /// <summary>The folder's files and folders, and those of the folders unfolded.</summary>
    public FileTree Tree { get; }

    /// <summary>The row of the file open, or of the folder last clicked.</summary>
    [ObservableProperty]
    private FileTreeRow? _selectedRow;

    private void OnChangesUpdated()
    {
        _followTimer.Stop();
        _followTimer.Start();
    }

    /// <summary>
    /// The chat's folder: the one the user opened in it, else the project the agent changed files in
    /// (none while it changed none). Read again when the agent changes a file and when another chat comes.
    /// </summary>
    private async Task FollowChatAsync()
    {
        var changes = _chat.Changes;
        var project = _chat.EditorFolder is null ? await Task.Run(changes.Project) : null;
        if (_closed)
            return;
        // The user may have opened one meanwhile.
        var folder = _chat.EditorFolder ?? project;
        if (folder is not null && !Directory.Exists(folder))
            folder = null;
        if (!string.Equals(folder, Folder, StringComparison.OrdinalIgnoreCase))
            ShowFolder(folder);
        await FollowAgentAsync(again: false);
    }

    // ---- Watch: following the agent's edits -----------------------------------------------------

    /// <summary>Wrap (the user, 2026-10-31, as File changes has it): long lines wrap inside the panel. Kept in settings.</summary>
    [ObservableProperty]
    private bool _wrap;

    partial void OnWrapChanged(bool value)
    {
        _chat.Panels.WrapCode = value;
        _chat.SavePanels();
    }

    /// <summary>Watch (the user, 2026-10-31, as File changes has it): the file the agent edits opens at the lines it changed, lit a moment. Kept in settings.</summary>
    [ObservableProperty]
    private bool _watch;

    partial void OnWatchChanged(bool value)
    {
        _chat.Panels.WatchCode = value;
        _chat.SavePanels();
        // Turned on, it goes to the agent's last change and lights it again.
        if (value)
            _ = FollowAgentAsync(again: true);
    }

    /// <summary>The chat's list of changes, and the number of its last call followed (see <see cref="Core.Diffs.FileChanges.LastChange"/>).</summary>
    private Core.Diffs.FileChanges _followedList;

    private int _followedNumber;

    /// <summary>Raised to light lines the agent changed (first to last, numbered from 1) and bring the first into view.</summary>
    internal event Action<IReadOnlyList<(int From, int To)>>? Lit;

    /// <summary>
    /// Watching: the file of the agent's new write_file or edit_file call (with <paramref name="again"/>,
    /// its last one) opens, scrolled to what the chat changed in it, lit. The open file's new text is lit
    /// as it is read (<see cref="ReadOpenFileAgainAsync"/>). Only a text file in the folder shown: one
    /// elsewhere would take the tree from the folder the user chose.
    /// </summary>
    private async Task FollowAgentAsync(bool again)
    {
        var changes = _chat.Changes;
        if (!ReferenceEquals(changes, _followedList))
            (_followedList, _followedNumber) = (changes, 0);
        if (changes.LastChange is not { } last || !again && last.Number == _followedNumber)
            return;
        _followedNumber = last.Number;
        var path = last.Path;
        if (!Watch || Folder is not { } folder || !IsIn(path, folder) || !await Task.Run(() => File.Exists(path) && Core.Documents.FileInspector.IsText(path)) || _closed)
            return;
        if (string.Equals(path, OpenPath, StringComparison.OrdinalIgnoreCase))
        {
            if (again)
                await LightChatChangesAsync(path);
            else
                await ReadOpenFileAgainAsync();
            return;
        }
        await OpenAsync(path);
        if (string.Equals(path, OpenPath, StringComparison.OrdinalIgnoreCase))
            await LightChatChangesAsync(path);
    }

    /// <summary>The lines the agent added or changed in the file in this chat, lit: what File changes shows of it.</summary>
    private async Task LightChatChangesAsync(string path)
    {
        var changes = _chat.Changes;
        var diff = await Task.Run(changes.Diff);
        if (!string.Equals(path, OpenPath, StringComparison.OrdinalIgnoreCase)
            || diff.Files.FirstOrDefault(f => string.Equals(diff.FullPath(f), path, StringComparison.OrdinalIgnoreCase)) is not { } file)
            return;
        if (Runs(file.Lines.Where(l => l.Kind == Core.Diffs.DiffLineKind.Added).Select(l => l.New ?? 0).Where(n => n > 0)) is { Count: > 0 } runs)
            Lit?.Invoke(runs);
    }

    /// <summary>Line numbers as runs of consecutive ones: 4, 5, 6, 9 as 4-6 and 9-9.</summary>
    public static List<(int From, int To)> Runs(IEnumerable<int> lines)
    {
        var runs = new List<(int From, int To)>();
        foreach (var line in lines.Distinct().Order())
        {
            if (runs.Count > 0 && runs[^1].To == line - 1)
                runs[^1] = (runs[^1].From, line);
            else
                runs.Add((line, line));
        }
        return runs;
    }

    /// <summary>Open folder, or a folder dropped on the panel: the chat's folder from now on, kept with it.</summary>
    internal void OpenFolder(string folder)
    {
        folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        _chat.EditorFolder = folder;
        if (!string.Equals(folder, Folder, StringComparison.OrdinalIgnoreCase))
            ShowFolder(folder);
    }

    /// <summary>The folder's tree, watched for files added, deleted and renamed; with another folder, a file open outside it closes.</summary>
    private void ShowFolder(string? folder)
    {
        _watcher?.Dispose();
        _watcher = null;
        lock (_gate)
            _changedFolders.Clear();
        Folder = folder;
        Tree.Show(folder);
        SelectedRow = null;
        if (OpenPath is { } open && (folder is null || !IsIn(open, folder)))
            CloseFile();
        if (folder is null)
            return;
        try
        {
            var watcher = new FileSystemWatcher(folder)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
                InternalBufferSize = 64 * 1024,
            };
            watcher.Created += OnDisk;
            watcher.Deleted += OnDisk;
            watcher.Renamed += OnDisk;
            watcher.Changed += OnDisk;
            // Too much at once for its buffer: every folder shown is read again.
            watcher.Error += (_, _) => Noted(["*"], false);
            watcher.EnableRaisingEvents = true;
            _watcher = watcher;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
        }
    }

    /// <summary>A click on a folder: unfolded (its files read the first time) or folded.</summary>
    internal void Toggle(FileTreeRow row) => Expand(row, !row.IsExpanded);

    internal void Expand(FileTreeRow row, bool expand) => Tree.Expand(row, expand);

    // ---- what changed on disk -------------------------------------------------------------------

    /// <summary>On the watcher's thread: notes the folder the change was in (or the open file's change), and the window's thread reads it all once it is over.</summary>
    private void OnDisk(object? sender, FileSystemEventArgs e)
    {
        if (sender != _watcher)
            return;
        var open = OpenPath;
        var old = (e as RenamedEventArgs)?.OldFullPath;
        var isOpen = open is not null && (string.Equals(e.FullPath, open, StringComparison.OrdinalIgnoreCase) || string.Equals(old, open, StringComparison.OrdinalIgnoreCase));
        // A file written changes only its own text: the tree is as it was.
        List<string> folders = e.ChangeType == WatcherChangeTypes.Changed ? [] : [Path.GetDirectoryName(e.FullPath) ?? "", .. old is null ? [] : new[] { Path.GetDirectoryName(old) ?? "" }];
        if (folders.Count > 0 || isOpen)
            Noted(folders, isOpen);
    }

    private void Noted(IEnumerable<string> folders, bool openFile)
    {
        lock (_gate)
        {
            _changedFolders.UnionWith(folders);
            _openFileChanged |= openFile;
            if (_diskPosted)
                return;
            _diskPosted = true;
        }
        Dispatcher.UIThread.Post(() =>
        {
            lock (_gate)
                _diskPosted = false;
            if (_closed)
                return;
            _diskTimer.Stop();
            _diskTimer.Start();
        });
    }

    /// <summary>The folders that changed on disk read again (those read before, unfolded or not), and the open file if it changed.</summary>
    private async Task OnDiskChangedAsync()
    {
        List<string> folders;
        bool openFile;
        lock (_gate)
        {
            folders = [.. _changedFolders];
            _changedFolders.Clear();
            openFile = _openFileChanged;
            _openFileChanged = false;
        }
        Tree.Refresh(folders);
        if (SelectedRow is { } selected && !Tree.Rows.Contains(selected))
            SelectedRow = null;
        if (openFile)
            await ReadOpenFileAgainAsync();
    }

    // ---- the file open --------------------------------------------------------------------------

    /// <summary>The text of the file open, as typed; null with none.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFile), nameof(OpenPath), nameof(EmptyText), nameof(FileStatus))]
    private TextDocument? _document;

    private CodeFile? _file;

    public bool HasFile => Document is not null;

    /// <summary>The path of the file open; null with none.</summary>
    public string? OpenPath => Document is null ? null : _file?.Path;

    /// <summary>Why the file clicked last isn't open ("Not a text file."); null when it is.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EmptyText))]
    private string? _fileProblem;

    /// <summary>What the space of the file says while none is open.</summary>
    public string EmptyText => FileProblem ?? (HasFolder ? "Pick a file to open it here." : "Open a folder to browse and edit its files, or drop one here.");

    /// <summary>Edited since it was read or saved.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FileStatus))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    private bool _isUnsaved;

    /// <summary>Changed on disk (by the agent, another program) while it had edits here not saved: Save keeps those, Reload takes the disk's.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FileStatus))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand), nameof(ReloadCommand))]
    private bool _changedOnDisk;

    /// <summary>Saving failed: why.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FileStatus))]
    private string? _saveProblem;

    /// <summary>What the file's bar says: its path in the folder, and whether it is saved.</summary>
    public string FileStatus
    {
        get
        {
            if (OpenPath is not { } path)
                return "";
            var where = Folder is { } root && IsIn(path, root) ? Path.GetRelativePath(root, path) : path;
            if (SaveProblem is { } problem)
                return $"{where} · not saved: {problem}";
            if (EncodingProblem is { } encoding)
                return $"{where} · {encoding}";
            if (ChangedOnDisk)
                return File.Exists(path) ? $"{where} · changed on disk too: Save keeps yours, Reload takes the file's" : $"{where} · deleted on disk: Save writes it again";
            return IsUnsaved ? $"{where} · not saved" : where;
        }
    }

    // ---- encoding and line breaks ----------------------------------------------------------------

    /// <summary>The language the file is highlighted as (one of <see cref="CodeLanguages.Names"/>); null for plain text.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LanguageName))]
    private string? _language;

    /// <summary>On its button in the file's bar: "C#", "Plain text".</summary>
    public string LanguageName => Document is null ? "Language" : Language ?? CodeLanguages.PlainText;

    /// <summary>Whether the language menu's entry is the one shown; null for Automatic, ticked while nothing was picked for the kind.</summary>
    internal bool IsLanguage(string? name) => _file is { } file && (name is null
        ? !_chat.Panels.CodeLanguages.ContainsKey(CodeLanguages.Key(file.Path))
        : _chat.Panels.CodeLanguages.TryGetValue(CodeLanguages.Key(file.Path), out var picked) && picked == name);

    /// <summary>
    /// Another language picked for the file (a "Plain text" too), kept in settings for every file of its kind
    /// (the user, 2026-10-31: "manually select highlighting if the file format is weird"); null, Automatic:
    /// the one its extension says again.
    /// </summary>
    internal void PickLanguage(string? name)
    {
        if (_file is not { } file)
            return;
        var key = CodeLanguages.Key(file.Path);
        if (name is null)
            _chat.Panels.CodeLanguages.Remove(key);
        else
            _chat.Panels.CodeLanguages[key] = name;
        _chat.SavePanels();
        Language = CodeLanguages.For(file.Path, _chat.Panels.CodeLanguages);
    }

    /// <summary>
    /// The encoding the file is written in on Save, on its button in the file's bar: its own when opened
    /// (the user, 2026-10-31: "use currently used line endings and encoding"), another when picked.
    /// </summary>
    public string EncodingName => _file is { } file && Document is not null ? CodeFile.NameOf(file.Encoding) : "Encoding";

    /// <summary>The line breaks its lines end in ("CRLF", "LF"), "Mixed" when not all the same.</summary>
    public string LineBreakName => Document is null ? "Line breaks" : _mixedBreaks ? "Mixed" : CodeFile.NameOfBreak(_lineBreak);

    private string _lineBreak = "\r\n";
    private bool _mixedBreaks;

    /// <summary>Another encoding that can't write the text: why it wasn't taken.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FileStatus))]
    private string? _encodingProblem;

    internal bool IsEncoding(System.Text.Encoding encoding) => _file is { } file && CodeFile.Same(file.Encoding, encoding);

    internal bool IsLineBreak(string lineBreak) => !_mixedBreaks && _lineBreak == lineBreak;

    /// <summary>
    /// Another encoding picked: the whole file is written in it on the next Save, as Visual Studio's
    /// Advanced Save Options and VS Code's Save with Encoding do; till then it counts as an edit not
    /// saved. Refused, with why, when the text has a character it can't write (a "✓" in Windows-1252).
    /// </summary>
    internal void PickEncoding(System.Text.Encoding encoding)
    {
        if (_file is not { } file || Document is not { } document)
            return;
        if (CodeFile.Unwritable(document.Text, encoding) is { } bad)
        {
            EncodingProblem = $"{CodeFile.NameOf(encoding)} can't write \"{bad.Char}\" (line {bad.Line}): kept {CodeFile.NameOf(file.Encoding)}";
            return;
        }
        EncodingProblem = null;
        file.Encoding = encoding;
        UpdateUnsaved();
        OnPropertyChanged(nameof(EncodingName));
    }

    /// <summary>Other line breaks picked: every line of the file ends in them, an edit (undone by Ctrl+Z) that Save writes.</summary>
    internal void PickLineBreak(string lineBreak)
    {
        if (Document is not { } document)
            return;
        var text = CodeFile.WithLineBreaks(document.Text, lineBreak);
        if (text != document.Text)
            Replace(document, text);
        ReadLineBreaks();
    }

    /// <summary>Which line breaks the text has now: read once typing rests (a 5 MB file isn't read at each key).</summary>
    private void ReadLineBreaks()
    {
        _breaksTimer?.Stop();
        if (Document is { } document)
            (_lineBreak, _mixedBreaks) = CodeFile.LineBreakOf(document.Text);
        OnPropertyChanged(nameof(LineBreakName));
    }

    private DispatcherTimer? _breaksTimer;

    private void OnTextChanged(object? sender, EventArgs e)
    {
        _breaksTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(300), DispatcherPriority.Background, (_, _) => ReadLineBreaks());
        _breaksTimer.Stop();
        _breaksTimer.Start();
    }

    /// <summary>A file clicked in the tree, or dropped on the panel: open in the editor, with its edits not saved yet if it has some.</summary>
    internal async Task OpenAsync(string path)
    {
        path = Path.GetFullPath(path);
        if (string.Equals(path, OpenPath, StringComparison.OrdinalIgnoreCase))
            return;
        _opening?.Cancel();
        var opening = _opening = new CancellationTokenSource();
        Leave();
        SelectRow(path);
        if (_chat.CodeDrafts.TryGetValue(path, out var draft))
        {
            Show(draft.File, draft.Document);
            return;
        }
        var (file, why) = await Task.Run(() => CodeFile.ReadAsync(path, opening.Token));
        if (opening.IsCancellationRequested || _closed)
            return;
        if (file is null)
        {
            FileProblem = why;
            return;
        }
        Show(file, new TextDocument(file.Text));
    }

    private void Show(CodeFile file, TextDocument document)
    {
        _file = file;
        FileProblem = null;
        SaveProblem = null;
        ChangedOnDisk = false;
        EncodingProblem = null;
        document.UndoStack.PropertyChanged += OnUndoChanged;
        document.TextChanged += OnTextChanged;
        // Before the text: the editor highlights it as it shows it.
        Language = CodeLanguages.For(file.Path, _chat.Panels.CodeLanguages);
        Document = document;
        OnPropertyChanged(nameof(LanguageName));
        ReadLineBreaks();
        UpdateUnsaved();
        OnPropertyChanged(nameof(EncodingName));
    }

    /// <summary>The file open goes from the editor; edited and not saved, it is kept as a draft, to come back with its edits.</summary>
    private void Leave()
    {
        Unpick();
        if (Document is { } document)
        {
            document.UndoStack.PropertyChanged -= OnUndoChanged;
            document.TextChanged -= OnTextChanged;
        }
        _breaksTimer?.Stop();
        _file = null;
        Document = null;
        FileProblem = null;
        IsUnsaved = false;
        ChangedOnDisk = false;
        SaveProblem = null;
        EncodingProblem = null;
        Language = null;
        OnPropertyChanged(nameof(EncodingName));
        OnPropertyChanged(nameof(LineBreakName));
        OnPropertyChanged(nameof(LanguageName));
    }

    private void CloseFile()
    {
        _opening?.Cancel();
        Leave();
        SelectedRow = null;
    }

    /// <summary>The file's row selected, the folders it is in unfolded down to it.</summary>
    private void SelectRow(string path) => SelectedRow = Tree.Reveal(path);

    /// <summary>Typed in, or undone back to what it was read or saved as: the draft kept or let go, its row's mark with it.</summary>
    private void OnUndoChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(UndoStack.IsOriginalFile) || _file is not { } file || Document is not { } document)
            return;
        UpdateUnsaved();
    }

    /// <summary>Edited (or another encoding picked) since it was read or saved: the draft kept or let go, its row's mark with it.</summary>
    private void UpdateUnsaved()
    {
        if (_file is not { } file || Document is not { } document)
            return;
        IsUnsaved = !document.UndoStack.IsOriginalFile || file.EncodingChanged;
        if (IsUnsaved)
            _chat.CodeDrafts[file.Path] = new CodeDraft(file, document);
        else
            _chat.CodeDrafts.Remove(file.Path);
        if (Tree.Find(file.Path) is { } row)
            row.IsUnsaved = IsUnsaved;
    }

    private bool CanSave() => IsUnsaved || ChangedOnDisk;

    /// <summary>Save (Ctrl+S): the text over the file, in the encoding it was read in.</summary>
    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync()
    {
        if (_file is not { } file || Document is not { } document)
            return;
        var text = document.Text;
        try
        {
            await file.SaveAsync(text, CancellationToken.None);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SaveProblem = ex.Message;
            return;
        }
        SaveProblem = null;
        ChangedOnDisk = false;
        // Typed on while it was written: that is still to save.
        if (Document == document && document.Text == text)
            document.UndoStack.MarkAsOriginalFile();
        // Saved in another encoding, with no edit to mark: the file is as shown now.
        UpdateUnsaved();
        OnPropertyChanged(nameof(EncodingName));
    }

    /// <summary>Reload: the file as it is on disk, in place of the edits not saved.</summary>
    [RelayCommand(CanExecute = nameof(ChangedOnDisk))]
    private async Task ReloadAsync()
    {
        if (_file is not { } file || Document is not { } document)
            return;
        // The encoding picked goes with the edits: the file's own comes back.
        file.Encoding = file.SavedEncoding;
        if (await file.ReadAgainAsync(CancellationToken.None) is not { } text || Document != document)
            return;
        Replace(document, text);
        document.UndoStack.MarkAsOriginalFile();
        ChangedOnDisk = false;
        UpdateUnsaved();
        OnPropertyChanged(nameof(EncodingName));
    }

    /// <summary>
    /// The open file changed on disk (the agent edited it): with no edits here it shows what the disk has,
    /// the caret and the view where they were; with edits, the bar says so and Save or Reload decides.
    /// </summary>
    private async Task ReadOpenFileAgainAsync()
    {
        if (_file is not { } file || Document is not { } document)
            return;
        var text = await file.ReadAgainAsync(CancellationToken.None);
        if (Document != document)
            return;
        // Written again, it may be in another encoding, which it is saved in from now on.
        OnPropertyChanged(nameof(EncodingName));
        UpdateUnsaved();
        if (text is null)
        {
            // Deleted: kept here, for Save to write again.
            ChangedOnDisk = !File.Exists(file.Path);
            return;
        }
        if (text == document.Text)
        {
            ChangedOnDisk = false;
            return;
        }
        if (IsUnsaved)
        {
            ChangedOnDisk = true;
            return;
        }
        var (start, length) = Replace(document, text);
        document.UndoStack.MarkAsOriginalFile();
        // Watching, the new lines are lit (a deletion: the line where it was).
        if (Watch)
        {
            var from = document.GetLineByOffset(start);
            var end = document.GetLineByOffset(start + length);
            Lit?.Invoke([(from.LineNumber, end.LineNumber > from.LineNumber && end.Offset == start + length ? end.LineNumber - 1 : end.LineNumber)]);
        }
    }

    /// <summary>Only what differs replaced, so the caret, the selection and the view before it stay. Returns where the new text went in, and how long it is.</summary>
    public static (int Start, int Length) Replace(TextDocument document, string text)
    {
        var old = document.Text;
        var start = 0;
        while (start < old.Length && start < text.Length && old[start] == text[start])
            start++;
        var end = 0;
        while (end < old.Length - start && end < text.Length - start && old[^(end + 1)] == text[^(end + 1)])
            end++;
        document.Replace(start, old.Length - start - end, text.Substring(start, text.Length - start - end));
        return (start, text.Length - start - end);
    }

    // ---- what is selected, into the chat's box ---------------------------------------------------

    /// <summary>The lines selected in the editor: the first and last line's numbers, the text selected, and whether it is the whole file.</summary>
    private (int From, int To, string Text, bool Whole)? _pick;

    /// <summary>Set while the editor lets go of its selection after a message went: that isn't the user's.</summary>
    private bool _sending;

    /// <summary>The editor's selection, which goes into the box as its lines ("Lines 12-18 · Program.cs").</summary>
    internal void Selected(int from, int to, string text, bool whole)
    {
        if (_file is not { } file || _sending)
            return;
        _pick = (from, to, text, whole);
        _chat.Pick([(file.Path, Caption(_pick.Value, Path.GetFileName(file.Path)))]);
    }

    /// <summary>Nothing selected (a click, Esc, typing over it, another file): the lines leave the box. False when none were in it.</summary>
    internal bool Unpick()
    {
        if (_pick is null || _sending)
            return false;
        _pick = null;
        _chat.Pick([]);
        return true;
    }

    /// <summary>Raised when the editor is to let go of its selection: the message went with it.</summary>
    internal event Action? LetGoOfSelection;

    /// <summary>"Lines 12-18 · Program.cs", "Line 4 · Program.cs", "Whole file · Program.cs".</summary>
    public static string Caption((int From, int To, string Text, bool Whole) pick, string name) =>
        (pick.Whole ? "Whole file" : pick.From == pick.To ? $"Line {pick.From}" : $"Lines {pick.From}-{pick.To}") + " · " + name;

    /// <summary>
    /// What a message sent with the selected lines carries: over its bubble the file and lines; for
    /// the model, the file's path and the lines with their numbers. Nothing with no lines selected:
    /// a file only open is no context the user chose (the user, 2026-10-28).
    /// </summary>
    public (string Via, string Context)? About(IReadOnlyList<string> files)
    {
        if (_pick is not { } pick || _file is not { } file || Document is not { } document || !files.Contains(file.Path, StringComparer.OrdinalIgnoreCase))
            return null;
        var lines = Enumerable.Range(pick.From, pick.To - pick.From + 1).Where(n => n <= document.LineCount)
            .Select(n => document.GetText(document.GetLineByNumber(n))).ToList();
        return Told(pick, file.Path, IsUnsaved, lines);
    }

    /// <summary>
    /// What the model is told: the lines the selection is in, whole and numbered (a selection that
    /// starts or ends inside a line still means those lines); a part of one line, also that part.
    /// </summary>
    public static (string Via, string Context) Told((int From, int To, string Text, bool Whole) pick, string path, bool unsaved, IReadOnlyList<string> lines)
    {
        var where = pick.Whole ? "the whole file" : pick.From == pick.To ? $"line {pick.From}" : $"lines {pick.From}-{pick.To}";
        var via = $"Code editor · {Path.GetFileName(path)}" + (pick.Whole ? "" : " " + where);
        var context = new StringBuilder($"[The Code editor panel is open beside the chat, with {path} open");
        if (unsaved)
            context.Append(", edited there and not saved yet: the file on disk is as it was before those edits");
        context.Append($". What they say is about what they selected in it unless they say otherwise, {where}");
        context.Append(pick.Whole ? ":\n" : " (numbers are lines of the file as the panel shows it):\n");
        for (var i = 0; i < lines.Count; i++)
        {
            var line = $"{pick.From + i}\t{lines[i]}\n";
            if (context.Length + line.Length > MaxPickedChars)
            {
                context.Append($"[... {lines.Count - i} more selected lines]\n");
                break;
            }
            context.Append(line);
        }
        if (lines is [var only] && pick.Text.Length > 0 && pick.Text.Trim() != only.Trim())
            context.Append($"In that line they selected: {pick.Text}\n");
        context.Append(']');
        return (via, context.ToString());
    }

    /// <summary>The message went with the selected lines: they are no longer selected.</summary>
    public void Sent()
    {
        _pick = null;
        _sending = true;
        try { LetGoOfSelection?.Invoke(); }
        finally { _sending = false; }
    }

    /// <summary>A folder dropped on the panel is opened; a file, shown in the editor. False for anything else: it goes with the next message.</summary>
    public bool Drop(IReadOnlyList<string> paths)
    {
        if (paths.FirstOrDefault(Directory.Exists) is { } folder)
        {
            OpenFolder(folder);
            return true;
        }
        if (paths.FirstOrDefault(File.Exists) is not { } file)
            return false;
        Show(file);
        return true;
    }

    // Explicit: a property named Path would hide System.IO.Path in the class.
    string? IFilePanel.Path => OpenPath;

    void IFilePanel.Show(string path) => Show(path);

    /// <summary>
    /// A file to show (Open in Code editor on a file's right-click menu, one dropped on the panel): open,
    /// its row shown in the tree. Outside the folder shown, the folder becomes its project (its git
    /// repository, solution or package, else its own folder), as if opened with Open folder.
    /// </summary>
    private void Show(string path)
    {
        path = Path.GetFullPath(path);
        if (Folder is not { } folder || !IsIn(path, folder))
            OpenFolder(ProjectRoot.Find(path)?.Root ?? Path.GetDirectoryName(path)!);
        _ = OpenAsync(path);
    }

    private static bool IsIn(string path, string folder) =>
        path.StartsWith(Path.EndsInDirectorySeparator(folder) ? folder : folder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private bool _closed;

    public void Dispose()
    {
        _closed = true;
        _chat.ChangesUpdated -= OnChangesUpdated;
        _followTimer.Stop();
        _diskTimer.Stop();
        _watcher?.Dispose();
        _watcher = null;
        _opening?.Cancel();
        _breaksTimer?.Stop();
        if (Document is { } document)
        {
            document.UndoStack.PropertyChanged -= OnUndoChanged;
            document.TextChanged -= OnTextChanged;
        }
    }
}
