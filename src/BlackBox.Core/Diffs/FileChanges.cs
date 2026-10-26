using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using BlackBox.Core.Tools;

namespace BlackBox.Core.Diffs;

/// <summary>
/// The files the agent changed in a chat, each with what it held before the agent first changed it,
/// for the File changes panel. write_file and edit_file read their file before they run; a run_shell
/// command has its folder and the paths it names watched while it runs (<see cref="Scope"/>), and
/// what it changed that BlackBox hadn't read before is taken as git has it when the file is in a
/// repository. A file a command changed outside git is shown from that command on, as what it held
/// before is not known.
/// </summary>
public sealed partial class FileChanges
{
    /// <summary>A file bigger than this is only said to have changed.</summary>
    private const long MaxBytes = 2_000_000;

    /// <summary>Files kept at most: a command that unpacks an archive would list thousands.</summary>
    private const int MaxFiles = 500;

    /// <summary>Paths a command changed that are looked at, at most: one run in the home folder sees every cache.</summary>
    private const int MaxSeen = 2_000;

    /// <summary>Folders whose files a command changes as a side effect: builds, packages, caches, git's own.</summary>
    private static readonly HashSet<string> Noise = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".vs", ".idea", "bin", "obj", "node_modules", "__pycache__", ".venv", "venv", "packages", ".gradle",
        ".pytest_cache", ".mypy_cache", ".cache", "AppData", "Library", "Temp", "Logs",
    };

    /// <summary>
    /// A file as it was or is: Exists false when it isn't there; Text null for one that isn't text
    /// or is too big. Known false when a command changed it before BlackBox read it: Text is then
    /// what it held right after that command.
    /// </summary>
    private sealed record Content(bool Exists, string? Text, bool Known = true)
    {
        public static readonly Content Absent = new(false, null);
    }

    private readonly Lock _gate = new();
    private readonly Dictionary<string, Content> _before = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _order = [];
    private string? _store;

    /// <summary>Raised on the agent's thread after a call that may have changed files.</summary>
    public event Action<FileChanges>? Changed;

    /// <summary>Keeps the list in <paramref name="file"/> from now on (the chat's, once it has one), and writes it there now.</summary>
    public void KeepIn(string file)
    {
        lock (_gate)
        {
            if (_store is not null)
                return;
            _store = file;
        }
        Save();
    }

    /// <summary>The list kept in <paramref name="file"/>; an empty one, kept there, when there is none.</summary>
    public static FileChanges Load(string file)
    {
        var changes = new FileChanges { _store = file };
        try
        {
            if (File.Exists(file) && JsonNode.Parse(File.ReadAllText(file))?["files"] is JsonArray files)
                foreach (var f in files.OfType<JsonObject>())
                {
                    if (f["path"]?.GetValue<string>() is not { Length: > 0 } path || changes._before.ContainsKey(path))
                        continue;
                    changes._before[path] = new Content(f["exists"]?.GetValue<bool>() ?? false, f["text"]?.GetValue<string>(), f["known"]?.GetValue<bool>() ?? true);
                    changes._order.Add(path);
                }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or FormatException) { }
        return changes;
    }

    private void Save()
    {
        string? file;
        string json;
        lock (_gate)
        {
            if ((file = _store) is null || _order.Count == 0)
                return;
            json = new JsonObject
            {
                ["files"] = new JsonArray(_order.Select(p => (JsonNode)new JsonObject
                {
                    ["path"] = p,
                    ["exists"] = _before[p].Exists,
                    ["text"] = _before[p].Text,
                    ["known"] = _before[p].Known,
                }).ToArray()),
            }.ToJsonString();
        }
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            Settings.AtomicFile.Write(file, json);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    // ---- around a tool call -------------------------------------------------------------------------

    /// <summary>
    /// A call about to run: the file it writes is read first, or the folder a command runs in is
    /// watched; null for a call that changes no file on this PC.
    /// </summary>
    public Call? Starting(Tool tool, JsonObject args, ToolContext ctx)
    {
        if (RemoteHost.Of(args) is not null)
            return null;
        try
        {
            if (tool is WriteFileTool or EditFileTool && args["path"] is JsonValue v && v.TryGetValue<string>(out var path) && path.Trim().Length > 0)
            {
                Remember(ctx.ResolvePath(path.Trim()));
                return new Call(this, null);
            }
            if (tool is ShellTool)
            {
                var cwd = args["working_directory"] is JsonValue w && w.TryGetValue<string>(out var dir) && dir.Trim().Length > 0 ? ctx.ResolvePath(dir.Trim()) : ctx.WorkingDirectory;
                var command = args["command"] is JsonValue c && c.TryGetValue<string>(out var text) ? text : "";
                return Directory.Exists(cwd) ? new Call(this, new Watch(Scope.Of(command, cwd, Home))) : null;
            }
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or IOException or UnauthorizedAccessException) { }
        return null;
    }

    /// <summary>A call that may change files, until it has ended.</summary>
    public sealed class Call(FileChanges changes, Watch? watch)
    {
        /// <summary>What a command changed joins the list; then the panel reads it again.</summary>
        public async Task EndAsync()
        {
            try
            {
                if (watch is not null)
                {
                    // The last events arrive a moment after the command is done.
                    await Task.Delay(150).ConfigureAwait(false);
                    watch.Dispose();
                    await changes.TakeAsync(watch).ConfigureAwait(false);
                }
                changes.Save();
                changes.Changed?.Invoke(changes);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // What the panel shows is never worth failing the call over.
                watch?.Dispose();
            }
        }
    }

    /// <summary>A file the agent is about to change: what it holds now, unless that is known already.</summary>
    private void Remember(string path)
    {
        path = Path.GetFullPath(path);
        lock (_gate)
            if (_before.ContainsKey(path) || _order.Count >= MaxFiles)
                return;
        var now = Read(path);
        lock (_gate)
        {
            if (_before.TryAdd(path, now))
                _order.Add(path);
        }
    }

    /// <summary>The home folder, where a command runs unless it is told otherwise; a test's own folder in tests.</summary>
    internal string Home { get; init; } = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>
    /// What of the changes made while a command runs is the command's own. Other programs write files
    /// at any moment, and a watched folder doesn't say who wrote: a Claude Code beside BlackBox rewrote
    /// .claude.json in the home folder during a command's sleep, and the panel listed it as the model's.
    /// So a change counts below the folder the command runs in or a folder it names, unless that folder
    /// is one every program writes in (the home folder or one above it, a drive, Windows, Program Files,
    /// ProgramData, Temp); there only the files the command names count.
    /// </summary>
    internal sealed class Scope
    {
        /// <summary>Folders below which every change counts.</summary>
        public List<string> Folders { get; } = [];

        /// <summary>Files the command names, which count wherever they are.</summary>
        public HashSet<string> Files { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>The folders watched, and whether their subfolders are.</summary>
        public List<(string Folder, bool Deep)> Watched { get; } = [];

        /// <summary>A path below a folder it may change files in, but not in a build's or cache's folder there (<see cref="Noise"/>); elsewhere a file it names.</summary>
        public bool Allows(string path) =>
            Folders.Where(f => IsInside(path, f)).MaxBy(f => f.Length) is { } folder ? !IsNoise(folder, path) : Files.Contains(path);

        public static Scope Of(string command, string cwd, string home)
        {
            var scope = new Scope();
            cwd = Path.GetFullPath(cwd);
            if (!IsShared(cwd, home))
                scope.Folders.Add(cwd);
            foreach (var word in Words(command, home).Take(300))
            {
                string full;
                try
                {
                    full = Path.GetFullPath(Path.Combine(cwd, word));
                    // A wildcard names the folder it is in.
                    if (full.IndexOfAny(['*', '?']) is var star and >= 0)
                        full = Path.GetDirectoryName(full[..star]) ?? "";
                }
                catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { continue; }
                if (full.Length == 0)
                    continue;
                if (Directory.Exists(full))
                {
                    if (!IsShared(full, home) && !scope.Folders.Any(f => Same(f, full) || IsInside(full, f)))
                        scope.Folders.Add(full);
                }
                else
                    scope.Files.Add(full);
            }
            // The folder it runs in is watched whole, as a file it names may be anywhere below; then
            // each folder it names, and the folder of each file it names elsewhere.
            scope.Watched.Add((cwd, true));
            foreach (var folder in scope.Folders.Where(f => !Covered(f)).ToList())
                scope.Watched.Add((folder, true));
            foreach (var parent in scope.Files.Select(Path.GetDirectoryName).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToList())
                if (scope.Watched.Count < 16 && !Covered(parent) && Directory.Exists(parent))
                    scope.Watched.Add((parent, false));
            return scope;

            bool Covered(string folder) => scope.Watched.Any(w => Same(w.Folder, folder) || w.Deep && IsInside(folder, w.Folder));
        }

        /// <summary>The words of a command that may be paths: quoted ones whole, the rest split at spaces and shell punctuation, with ~ and environment variables filled in.</summary>
        internal static IEnumerable<string> Words(string command, string home)
        {
            var words = Quoted().Matches(command).Select(m => m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value)
                .Concat(Quoted().Replace(command, " ").Split([' ', '\t', '\r', '\n', ';', '|', '&', '(', ')', '{', '}', '<', '>', ',', '='], StringSplitOptions.RemoveEmptyEntries));
            foreach (var raw in words)
            {
                var word = raw.Trim().Trim('`');
                // -Path:C:\x names C:\x; other switches name nothing.
                if (word.StartsWith('-'))
                    word = word.IndexOf(':') is var colon and > 0 ? word[(colon + 1)..] : "";
                word = EnvVariable().Replace(Environment.ExpandEnvironmentVariables(word), m =>
                    m.Groups[1].Success ? Environment.GetEnvironmentVariable(m.Groups[2].Value) ?? m.Value
                    : m.Groups[2].Value.Equals("HOME", StringComparison.OrdinalIgnoreCase) ? home : m.Value);
                if (word == "~" || word.StartsWith("~/", StringComparison.Ordinal) || word.StartsWith("~\\", StringComparison.Ordinal))
                    word = home + word[1..];
                if (word.Length > 0 && !word.Contains("://", StringComparison.Ordinal) && word.IndexOfAny(Path.GetInvalidPathChars()) < 0)
                    yield return word;
            }
        }

        /// <summary>A folder every program writes in: the home folder or one it is in, a drive, or the system's own.</summary>
        internal static bool IsShared(string folder, string home)
        {
            folder = Path.GetFullPath(folder);
            if (Same(folder, Path.GetPathRoot(folder) ?? "") || Same(folder, home) || IsInside(home, folder))
                return true;
            return new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                Path.GetTempPath(),
            }.Any(s => s.Length > 0 && Same(folder, s));
        }

        private static bool Same(string a, string b) =>
            string.Equals(Path.TrimEndingDirectorySeparator(a), Path.TrimEndingDirectorySeparator(b), StringComparison.OrdinalIgnoreCase);

        /// <summary>A path below the folder (not the folder itself).</summary>
        private static bool IsInside(string path, string folder) =>
            path.StartsWith(Path.EndsInDirectorySeparator(folder) ? folder : folder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"""([^""]*)""|'([^']*)'")]
    private static partial System.Text.RegularExpressions.Regex Quoted();

    // $env:NAME, ${env:NAME}, $HOME
    [System.Text.RegularExpressions.GeneratedRegex(@"\$\{?(env:)?(\w+)\}?", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex EnvVariable();

    /// <summary>The folders a command may change files in, watched while it runs: the paths it created, and those it changed or deleted, that are its own (<see cref="Scope"/>).</summary>
    public sealed class Watch : IDisposable
    {
        private readonly List<FileSystemWatcher> _watchers = [];
        private readonly Scope _scope;

        /// <summary>Each path, and whether it was there before the command (its first event says).</summary>
        internal ConcurrentDictionary<string, bool> Seen { get; } = new(StringComparer.OrdinalIgnoreCase);

        internal Watch(Scope scope)
        {
            _scope = scope;
            foreach (var (folder, deep) in scope.Watched)
                try
                {
                    var watcher = new FileSystemWatcher(folder)
                    {
                        IncludeSubdirectories = deep,
                        InternalBufferSize = 64 * 1024,
                        NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
                    };
                    watcher.Created += (_, e) => Add(e.FullPath, existed: false);
                    watcher.Changed += (_, e) => Add(e.FullPath, existed: true);
                    watcher.Deleted += (_, e) => Add(e.FullPath, existed: true);
                    watcher.Renamed += (_, e) =>
                    {
                        Add(e.OldFullPath, existed: true);
                        Add(e.FullPath, existed: false);
                    };
                    watcher.EnableRaisingEvents = true;
                    _watchers.Add(watcher);
                }
                catch (Exception ex) when (ex is ArgumentException or IOException or PlatformNotSupportedException or UnauthorizedAccessException) { }
        }

        private void Add(string path, bool existed)
        {
            if (Seen.Count < MaxSeen && _scope.Allows(path))
                Seen.TryAdd(path, existed);
        }

        public void Dispose()
        {
            foreach (var watcher in _watchers)
                watcher.Dispose();
        }
    }

    /// <summary>A path in a folder of <see cref="Noise"/>, below where the command ran.</summary>
    internal static bool IsNoise(string folder, string path)
    {
        var relative = Path.GetRelativePath(folder, path);
        var parts = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return parts.Length > 1 && parts[..^1].Any(Noise.Contains) || path.EndsWith(".lock", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>What a command changed, into the list: text files only, and not those git ignores.</summary>
    private async Task TakeAsync(Watch watch)
    {
        List<(string Path, bool Existed)> seen;
        lock (_gate)
            seen = watch.Seen.Where(s => !_before.ContainsKey(s.Key) && !Directory.Exists(s.Key)).Select(s => (Path.GetFullPath(s.Key), s.Value)).ToList();
        if (seen.Count == 0)
            return;
        var fromGit = await FromGitAsync(seen.Select(s => s.Path).ToList()).ConfigureAwait(false);
        foreach (var (path, existed) in seen)
        {
            if (fromGit.Ignored.Contains(path))
                continue;
            var now = Read(path);
            Content before;
            if (!existed)
                before = Content.Absent;
            else if (fromGit.Tracked.TryGetValue(path, out var text))
                before = new Content(true, text);
            else
                // Changed, but BlackBox never read it: shown from here on. One gone is shown not at all.
                before = now with { Known = false };
            // A command's binary files (a build's, a download) aren't code to look at.
            if (!now.Exists && !before.Exists || now.Exists && now.Text is null || before.Exists && before.Text is null || !now.Exists && !before.Known)
                continue;
            lock (_gate)
            {
                if (_order.Count >= MaxFiles)
                    break;
                if (_before.TryAdd(path, before))
                    _order.Add(path);
            }
        }
    }

    /// <summary>Of the paths: those git ignores, and what git has of those it tracks (as staged, else committed).</summary>
    private static async Task<(HashSet<string> Ignored, Dictionary<string, string> Tracked)> FromGitAsync(List<string> paths)
    {
        var ignored = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var tracked = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var roots = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var group in paths.GroupBy(p => Path.GetDirectoryName(p) ?? "", StringComparer.OrdinalIgnoreCase).Take(20))
            {
                if (!roots.TryGetValue(group.Key, out var root))
                    roots[group.Key] = root = await GitDiff.RootOfAsync(group.Key, CancellationToken.None).ConfigureAwait(false);
                if (root is null)
                    continue;
                var relative = group.Select(p => Path.GetRelativePath(root, p).Replace('\\', '/')).Where(r => !r.StartsWith("../", StringComparison.Ordinal)).Take(200).ToList();
                if (relative.Count == 0)
                    continue;
                // Not check-ignore: it refuses the literal paths every git call here is made with.
                var ignore = await GitDiff.RunAsync(root, ["ls-files", "-z", "--others", "--ignored", "--exclude-standard", "--", .. relative], CancellationToken.None, 200_000).ConfigureAwait(false);
                foreach (var r in ignore.Out.Split('\0', StringSplitOptions.RemoveEmptyEntries))
                    ignored.Add(Path.GetFullPath(Path.Combine(root, r)));
                var listed = await GitDiff.RunAsync(root, ["ls-files", "-z", "--", .. relative], CancellationToken.None, 200_000).ConfigureAwait(false);
                foreach (var r in listed.Out.Split('\0', StringSplitOptions.RemoveEmptyEntries))
                {
                    var shown = await GitDiff.RunAsync(root, ["show", ":" + r], CancellationToken.None, (int)MaxBytes).ConfigureAwait(false);
                    if (shown.Exit == 0 && !shown.Cut && !shown.Out.Contains('\0'))
                        tracked[Path.GetFullPath(Path.Combine(root, r))] = shown.Out;
                }
            }
        }
        catch (DiffException) { } // no git on this PC, or it failed: what isn't known shows from the command on
        return (ignored, tracked);
    }

    /// <summary>A file's text as the diff compares it: Word, Excel, PowerPoint and PDF files by the text read_file shows.</summary>
    private static Content Read(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
                return Content.Absent;
            if (info.Length > MaxBytes)
                return new Content(true, null);
            if (DocumentText.Handles(path))
                return new Content(true, DocumentText.Read(path));
            var head = new byte[8000];
            int read;
            using (var stream = TextFiles.OpenRead(path))
                read = stream.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
            if (FolderDiff.IsBinary(head.AsSpan(0, read)))
                return new Content(true, null);
            using var reader = TextFiles.OpenText(path);
            return new Content(true, reader.ReadToEnd());
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return new Content(File.Exists(path), null);
        }
    }

    // ---- the diff -------------------------------------------------------------------------------------

    public const string Title = "Changes in this chat";

    /// <summary>Each file as it is now against what it held before the agent changed it, in the order they were first changed; those back as they were are left out.</summary>
    public DiffResult Diff()
    {
        List<(string Path, Content Before)> files;
        lock (_gate)
            files = _order.Select(p => (p, _before[p])).ToList();
        var shown = new List<(string Path, DiffFile File)>();
        foreach (var (path, before) in files)
        {
            var now = Read(path);
            if (!before.Exists && !now.Exists)
                continue;
            var status = !before.Exists ? "added" : !now.Exists ? "deleted" : "modified";
            if (before.Exists && before.Text is null || now.Exists && now.Text is null)
            {
                shown.Add((path, new DiffFile(path, status, []) { Binary = true }));
                continue;
            }
            var lines = LineDiff.Hunks(before.Text is null ? [] : LineDiff.Lines(before.Text), now.Text is null ? [] : LineDiff.Lines(now.Text), 3);
            if (lines.Count == 0 && before.Known)
                continue;
            var file = new DiffFile(path, status, before.Known ? lines : [new DiffLine(DiffLineKind.Note, null, null, NotKnown), .. lines])
            {
                Added = lines.Count(l => l.Kind == DiffLineKind.Added),
                Removed = lines.Count(l => l.Kind == DiffLineKind.Removed),
            };
            shown.Add((path, file));
        }
        var root = CommonFolder(shown.Select(s => s.Path).ToList());
        return new DiffResult(root, Title, shown.Select(s => s.File with { Path = root.Length == 0 ? s.Path : Path.GetRelativePath(root, s.Path).Replace('\\', '/') }).ToList())
        {
            NewIsOnDisk = true,
        };
    }

    /// <summary>
    /// What the File changes panel shows: <see cref="Diff"/>, with each file in a git repository
    /// split by where its changes are: what of the agent's change is committed (against what the
    /// file held before it), what is staged (against the last commit) and what is only on disk
    /// (against what is staged), a file of its own in each with <see cref="DiffFile.Git"/> saying
    /// which; in that order, then the files outside git as they are.
    /// </summary>
    public async Task<DiffResult> DiffAsync(CancellationToken ct)
    {
        var all = Diff();
        var paths = all.Files.Select(all.FullPath).ToList();
        var git = paths.Count == 0 ? [] : await GitChanges.ReadAsync(paths, ct).ConfigureAwait(false);
        if (git.Count == 0)
            return all;
        List<(string Path, DiffFile File)>[] sections = [[], [], []];
        var outside = new List<(string Path, DiffFile File)>();
        for (var i = 0; i < paths.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var path = paths[i];
            if (!git.TryGetValue(path, out var read))
            {
                outside.Add((path, all.Files[i]));
                continue;
            }
            Content? before;
            lock (_gate)
                _before.TryGetValue(path, out before);
            var (index, head, now) = (FromGit(read.Index, path), FromGit(read.Head, path), Read(path));
            var disk = GitChanges.OnDisk(path);
            var utf8 = (Index: read.Index is null || GitChanges.Lines(read.Index) is not null, Head: read.Head is null || GitChanges.Lines(read.Head) is not null,
                Disk: disk is not null && GitChanges.Lines(disk) is not null);
            var splittable = !read.Conflict && !DocumentText.Handles(path);
            if (before is { Known: true })
                Add(GitSection.Committed, before, head, false);
            if (read.X != ' ')
                Add(GitSection.Staged, head, index, splittable && utf8.Head && utf8.Index);
            if (read.Y != ' ')
                Add(GitSection.Unstaged, index, now, splittable && utf8.Index && utf8.Disk);

            void Add(GitSection section, Content from, Content to, bool split)
            {
                if (!from.Exists && !to.Exists)
                    return;
                var status = !from.Exists ? "added" : !to.Exists ? "deleted" : "modified";
                var gitFile = new GitFile(read.Root, read.Path, section, split);
                if (from.Exists && from.Text is null || to.Exists && to.Text is null)
                {
                    // Not text: git's letters say it changed, but what was committed since can't be told.
                    if (section != GitSection.Committed || status != "modified")
                        sections[(int)section].Add((path, new DiffFile(path, status, []) { Binary = true, Git = gitFile }));
                    return;
                }
                var lines = LineDiff.Hunks(from.Text is null ? [] : LineDiff.Lines(from.Text), to.Text is null ? [] : LineDiff.Lines(to.Text), 3);
                if (lines.Count == 0)
                    return; // only its line endings or mode differ
                sections[(int)section].Add((path, new DiffFile(path, status, lines)
                {
                    Added = lines.Count(l => l.Kind == DiffLineKind.Added),
                    Removed = lines.Count(l => l.Kind == DiffLineKind.Removed),
                    Git = gitFile,
                }));
            }
        }
        var files = sections.SelectMany(s => s).Concat(outside).ToList();
        var root = CommonFolder(files.Select(s => s.Path).Distinct(StringComparer.OrdinalIgnoreCase).ToList());
        return new DiffResult(root, Title, files.Select(s => s.File with { Path = root.Length == 0 ? s.Path : Path.GetRelativePath(root, s.Path).Replace('\\', '/') }).ToList())
        {
            NewIsOnDisk = true,
            InGit = true,
        };

        // What git has of the file, as read_file would read it: absent, not text, or its text.
        static Content FromGit(byte[]? bytes, string path) =>
            bytes is null ? Content.Absent
            : FolderDiff.IsBinary(bytes.AsSpan(0, Math.Min(bytes.Length, 8000))) || DocumentText.Handles(path) ? new Content(true, null)
            : new Content(true, Decode(bytes));
    }

    /// <summary>
    /// Puts a file's changes back: in the Unstaged section as they are staged, outside git (null) as
    /// they were before the agent changed them. <paramref name="lines"/> are the diff's changed lines
    /// to put back, an added one by its number in the file now (Old null), a removed one by its number
    /// before (New null); null puts back the whole file, and one that wasn't there before goes to
    /// <paramref name="delete"/>. Staged and committed changes aren't put back. Null when done, else why not.
    /// </summary>
    public async Task<string?> DiscardAsync(GitSection? section, string path, IReadOnlyCollection<(int? Old, int? New)>? lines, Action<string> delete, CancellationToken ct)
    {
        path = Path.GetFullPath(path);
        bool existed;
        string? before;
        if (section is null)
        {
            Content? content;
            lock (_gate)
                _before.TryGetValue(path, out content);
            if (content is null)
                return "BlackBox has nothing of this file from before.";
            (existed, before) = (content.Exists, content.Text);
        }
        else if (section != GitSection.Unstaged)
            return "Only changes not staged yet can be discarded: unstage these first.";
        else
        {
            if ((await GitChanges.ReadAsync([path], ct).ConfigureAwait(false)).GetValueOrDefault(path) is not { } read)
                return "This file isn't in a git repository.";
            if (lines is null && read.Index is not null)
                return await GitChanges.RestoreAsync(read.Root, read.Path, ct).ConfigureAwait(false);
            (existed, before) = (read.Index is not null, read.Index is null || FolderDiff.IsBinary(read.Index.AsSpan(0, Math.Min(read.Index.Length, 8000))) ? null : Decode(read.Index));
        }        try
        {
            if (lines is null)
            {
                if (!existed)
                {
                    if (File.Exists(path))
                        delete(path);
                    return null;
                }
                if (before is null || DocumentText.Handles(path))
                    return "BlackBox didn't keep what this file held before: it isn't text.";
                await TextFiles.WriteAllAsync(path, before, File.Exists(path) ? TextFiles.Detect(path) : TextFiles.ForNewFile(path), ct).ConfigureAwait(false);
                return null;
            }
            var now = Read(path);
            if (DocumentText.Handles(path) || now.Exists && now.Text is null || existed && before is null)
                return "Only the whole file can be put back: it isn't text.";
            var old = GitChanges.Split(before ?? "");
            var current = GitChanges.Split(now.Text ?? "");
            var lineBreak = GitChanges.Break(current) ?? GitChanges.Break(old) ?? "\n";
            var picked = lines.ToHashSet();
            var kept = new List<string>();
            var at = 0;
            foreach (var block in LineDiff.Blocks(old.Select(GitChanges.Key).ToArray(), current.Select(GitChanges.Key).ToArray()))
            {
                kept.AddRange(current.Skip(at).Take(block.NewStart - at));
                // Removed lines put back come first, as the diff shows them above the added ones.
                for (var i = block.OldStart; i < block.OldStart + block.OldCount; i++)
                    if (picked.Contains((i + 1, null)))
                        kept.Add(GitChanges.Key(old[i]) + (GitChanges.Key(old[i]).Length < old[i].Length ? lineBreak : ""));
                for (var i = block.NewStart; i < block.NewStart + block.NewCount; i++)
                    if (!picked.Contains((null, i + 1)))
                        kept.Add(current[i]);
                at = block.NewStart + block.NewCount;
            }
            kept.AddRange(current.Skip(at));
            await TextFiles.WriteAllAsync(path, GitChanges.Join(kept, lineBreak), now.Exists ? TextFiles.Detect(path) : TextFiles.ForNewFile(path), ct).ConfigureAwait(false);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ex.Message;
        }
    }

    /// <summary>A file's bytes from git as read_file reads the file: in its own encoding.</summary>
    private static string Decode(byte[] bytes)
    {
        using var reader = new StreamReader(new MemoryStream(bytes), TextFiles.Detect(bytes, whole: true), detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    /// <summary>The note over a file a command changed before BlackBox read it.</summary>
    public const string NotKnown = "A command changed this file before BlackBox read it: changes show from then on.";

    /// <summary>The deepest folder all the paths are in; "" when they are on different drives.</summary>
    internal static string CommonFolder(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0)
            return "";
        var common = Path.GetDirectoryName(paths[0]) ?? "";
        foreach (var path in paths.Skip(1))
            while (common.Length > 0 && !path.StartsWith(Inside(common), StringComparison.OrdinalIgnoreCase))
                common = Path.GetDirectoryName(common) ?? "";
        return common;

        // "C:\a\" for C:\a, "C:\" for the drive.
        static string Inside(string folder) => Path.EndsInDirectorySeparator(folder) ? folder : folder + Path.DirectorySeparatorChar;
    }
}
