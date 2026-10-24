using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using BlackBox.Core.Tools;

namespace BlackBox.Core.Diffs;

/// <summary>
/// The files the agent changed in a chat, each with what it held before the agent first changed it,
/// for the Code changes panel. write_file and edit_file read their file before they run; a run_shell
/// command has its folder watched while it runs, and what it changed that BlackBox hadn't read
/// before is taken as git has it when the file is in a repository. A file a command changed outside
/// git is shown from that command on, as what it held before is not known.
/// </summary>
public sealed class FileChanges
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
                return Directory.Exists(cwd) ? new Call(this, new Watch(cwd)) : null;
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

    /// <summary>The folder a command runs in, watched while it runs: the paths it created, and those it changed or deleted.</summary>
    public sealed class Watch : IDisposable
    {
        private readonly FileSystemWatcher? _watcher;

        /// <summary>Each path, and whether it was there before the command (its first event says).</summary>
        internal ConcurrentDictionary<string, bool> Seen { get; } = new(StringComparer.OrdinalIgnoreCase);

        internal string Folder { get; }

        internal Watch(string folder)
        {
            Folder = Path.GetFullPath(folder);
            try
            {
                _watcher = new FileSystemWatcher(Folder)
                {
                    IncludeSubdirectories = true,
                    InternalBufferSize = 64 * 1024,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
                };
                _watcher.Created += (_, e) => Add(e.FullPath, existed: false);
                _watcher.Changed += (_, e) => Add(e.FullPath, existed: true);
                _watcher.Deleted += (_, e) => Add(e.FullPath, existed: true);
                _watcher.Renamed += (_, e) =>
                {
                    Add(e.OldFullPath, existed: true);
                    Add(e.FullPath, existed: false);
                };
                _watcher.EnableRaisingEvents = true;
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or PlatformNotSupportedException or UnauthorizedAccessException)
            {
                _watcher = null;
            }
        }

        private void Add(string path, bool existed)
        {
            if (Seen.Count < MaxSeen && !IsNoise(Folder, path))
                Seen.TryAdd(path, existed);
        }

        public void Dispose() => _watcher?.Dispose();
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
