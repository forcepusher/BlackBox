using System.Text.Json;
using System.Text.Json.Nodes;
using Sloperator.Core.Tools;

namespace Sloperator.Core.Diffs;

/// <summary>
/// The files the agent changed in a chat, each with what it held before the agent first changed it,
/// for the File changes panel: write_file and edit_file read their file before they run. What a
/// run_shell command changes isn't listed: a folder watched while it ran can't tell its writes from
/// other programs' (a Claude Code beside Sloperator rewrote .claude.json in the home folder during a
/// command's sleep, and it was listed as the model's). Each message of the user's starts a turn
/// (<see cref="NewTurn"/>), and a file keeps what it held before its first change in each turn, so
/// undoing a message puts the files back as they were before it (<see cref="RollBackAsync"/>).
/// </summary>
public sealed class FileChanges
{
    /// <summary>A file bigger than this is only said to have changed.</summary>
    private const long MaxBytes = 2_000_000;

    /// <summary>Files kept at most.</summary>
    private const int MaxFiles = 500;

    /// <summary>
    /// A file as it was or is: Exists false when it isn't there; Text null for one that isn't text or is too big.
    /// A Word, Excel, PowerPoint or PDF file, whose Text is what read_file shows, keeps its Bytes too, to be put back.
    /// </summary>
    private sealed record Content(bool Exists, string? Text, byte[]? Bytes = null)
    {
        public static readonly Content Absent = new(false, null);
    }

    /// <summary>What a file held before its first change in a turn; the first is what it held before the agent changed it at all.</summary>
    private sealed record Version(int Turn, Content Content);

    private readonly Lock _gate = new();
    private readonly Dictionary<string, List<Version>> _versions = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _order = [];
    private string? _store;

    /// <summary>The turn the changes made now belong to; 0 in a list kept before there were turns.</summary>
    private int _turn;

    private Content Before(string path) => _versions[path][0].Content;

    /// <summary>A message of the user's: the changes from now on are its turn's. Returns the turn's number, to roll back to.</summary>
    public int NewTurn()
    {
        lock (_gate)
            return ++_turn;
    }

    /// <summary>A chat opened again: its messages had turns up to <paramref name="turn"/>, which the list may not have kept (it is kept only once a file changes).</summary>
    public void TurnsUpTo(int turn)
    {
        lock (_gate)
            _turn = Math.Max(_turn, turn);
    }

    /// <summary>Raised on the agent's thread after a call that may have changed files.</summary>
    public event Action<FileChanges>? Changed;

    /// <summary>No file in the list yet: the agent changed none in the chat.</summary>
    public bool IsEmpty
    {
        get
        {
            lock (_gate)
                return _order.Count == 0;
        }
    }

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
            if (File.Exists(file) && JsonNode.Parse(File.ReadAllText(file)) is JsonObject json && json["files"] is JsonArray files)
            {
                changes._turn = json["turn"]?.GetValue<int>() ?? 0;
                foreach (var f in files.OfType<JsonObject>())
                {
                    // "known": false was a file a command changed before Sloperator read it, which may have been another program's.
                    if (f["path"]?.GetValue<string>() is not { Length: > 0 } path || changes._versions.ContainsKey(path) || f["known"]?.GetValue<bool>() == false)
                        continue;
                    changes._versions[path] = [Read(f), .. (f["later"] as JsonArray ?? []).OfType<JsonObject>().Select(Read)];
                    changes._order.Add(path);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or FormatException) { }
        return changes;

        // A list kept before there were turns has the first version alone, as turn 0.
        static Version Read(JsonObject v) => new(v["turn"]?.GetValue<int>() ?? 0, new Content(v["exists"]?.GetValue<bool>() ?? false, v["text"]?.GetValue<string>(),
            v["bytes"]?.GetValue<string>() is { } bytes ? Convert.FromBase64String(bytes) : null));
    }

    private void Save()
    {
        string? file;
        string json;
        lock (_gate)
        {
            if ((file = _store) is null || _order.Count == 0)
                return;
            // The first version where lists kept before turns had it, the later ones under it.
            json = new JsonObject
            {
                ["turn"] = _turn,
                ["files"] = new JsonArray(_order.Select(p =>
                {
                    var file = Write(_versions[p][0]);
                    file.Insert(0, "path", p);
                    if (_versions[p].Count > 1)
                        file["later"] = new JsonArray(_versions[p].Skip(1).Select(v => (JsonNode)Write(v)).ToArray());
                    return (JsonNode)file;
                }).ToArray()),
            }.ToJsonString();
        }
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            Settings.AtomicFile.Write(file, json);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }

        static JsonObject Write(Version v) => new()
        {
            ["exists"] = v.Content.Exists,
            ["text"] = v.Content.Text,
            ["bytes"] = v.Content.Bytes is { } bytes ? Convert.ToBase64String(bytes) : null,
            ["turn"] = v.Turn,
        };
    }

    // ---- around a tool call -------------------------------------------------------------------------

    /// <summary>A write_file or edit_file call about to run: the file it writes is read first; null for any other call, or one on another host.</summary>
    public Call? Starting(Tool tool, JsonObject args, ToolContext ctx)
    {
        if (RemoteHost.Of(args) is not null || tool is not (WriteFileTool or EditFileTool))
            return null;
        try
        {
            if (args["path"] is JsonValue v && v.TryGetValue<string>(out var path) && path.Trim().Length > 0)
            {
                var full = Path.GetFullPath(ctx.ResolvePath(path.Trim()));
                if (InOwnData(full, ctx))
                    return null;
                Remember(full);
                return new Call(this);
            }
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or IOException or UnauthorizedAccessException) { }
        return null;
    }

    /// <summary>
    /// Whether a path is in Sloperator's own Data folder (the one holding its scratch and tools): the
    /// agent's notes and downloads there are no change of the user's (the user, 2026-10-27).
    /// </summary>
    private static bool InOwnData(string path, ToolContext ctx) =>
        new[] { ctx.ScratchDirectory, ctx.ToolsDirectory }
            .OfType<string>().Select(d => Path.GetDirectoryName(Path.GetFullPath(d).TrimEnd(Path.DirectorySeparatorChar))).OfType<string>()
            .Any(data => path.StartsWith(data + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));

    /// <summary>A call that may change a file, until it has ended.</summary>
    public sealed class Call(FileChanges changes)
    {
        /// <summary>The list is kept; then the panel reads it again.</summary>
        public Task EndAsync()
        {
            try
            {
                changes.Save();
                changes.Changed?.Invoke(changes);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // What the panel shows is never worth failing the call over.
            }
            return Task.CompletedTask;
        }
    }

    /// <summary>A file the agent is about to change: what it holds now, unless that is known already for this turn.</summary>
    private void Remember(string path)
    {
        path = Path.GetFullPath(path);
        int turn;
        lock (_gate)
        {
            turn = _turn;
            if (_versions.TryGetValue(path, out var known) ? known[^1].Turn == turn : _order.Count >= MaxFiles)
                return;
        }
        var now = Read(path, keepDocument: true);
        lock (_gate)
        {
            if (!_versions.TryGetValue(path, out var versions))
            {
                _versions[path] = [new Version(turn, now)];
                _order.Add(path);
            }
            else if (versions[^1].Turn != turn)
                versions.Add(new Version(turn, now));
        }
    }

    /// <summary>What <see cref="RollBackAsync"/> did: the files put back as they were, those deleted, and those it couldn't put back, with why.</summary>
    public sealed record RollBack(IReadOnlyList<string> Restored, IReadOnlyList<string> Deleted, IReadOnlyList<(string Path, string Why)> Failed);

    /// <summary>
    /// Undoes the changes made from <paramref name="turn"/> on: each file the agent changed since is
    /// put back as it was before its first change in that turn or a later one, a file it made goes to
    /// <paramref name="delete"/>, and what the list kept of those turns goes. A file that wasn't text,
    /// was too big, or can't be written is left as it is, and said in <see cref="RollBack.Failed"/>.
    /// </summary>
    public async Task<RollBack> RollBackAsync(int turn, Action<string> delete, CancellationToken ct)
    {
        List<(string Path, Content Then)> back = [];
        lock (_gate)
        {
            foreach (var path in _order.ToList())
            {
                var versions = _versions[path];
                if (versions.FindIndex(v => v.Turn >= turn) is not (>= 0 and var first))
                    continue;
                back.Add((path, versions[first].Content));
                versions.RemoveRange(first, versions.Count - first);
                if (versions.Count == 0)
                {
                    _versions.Remove(path);
                    _order.Remove(path);
                }
            }
        }
        List<string> restored = [], deleted = [];
        List<(string, string)> failed = [];
        foreach (var (path, then) in back)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (!then.Exists)
                {
                    if (File.Exists(path))
                    {
                        delete(path);
                        deleted.Add(path);
                    }
                    continue;
                }
                if (then.Bytes is { } bytes)
                    await File.WriteAllBytesAsync(path, bytes, ct).ConfigureAwait(false);
                else if (then.Text is { } text && !DocumentText.Handles(path))
                {
                    if (File.Exists(path) && Read(path).Text == text)
                        continue; // as it was already
                    await TextFiles.WriteAllAsync(path, text, File.Exists(path) ? TextFiles.Detect(path) : TextFiles.ForNewFile(path), ct).ConfigureAwait(false);
                }
                else
                {
                    failed.Add((path, "Sloperator didn't keep what it held: it isn't text, or was too big."));
                    continue;
                }
                restored.Add(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                failed.Add((path, ex.Message));
            }
        }
        Save();
        try { Changed?.Invoke(this); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { }
        return new RollBack(restored, deleted, failed);
    }

    /// <summary>A file's text as the diff compares it: Word, Excel, PowerPoint and PDF files by the text read_file shows (and with <paramref name="keepDocument"/>, their bytes).</summary>
    private static Content Read(string path, bool keepDocument = false)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
                return Content.Absent;
            if (info.Length > MaxBytes)
                return new Content(true, null);
            if (DocumentText.Handles(path))
                return new Content(true, DocumentText.Read(path), keepDocument ? File.ReadAllBytes(path) : null);
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
            files = _order.Select(p => (p, Before(p))).ToList();
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
            if (lines.Count == 0)
                continue;
            var file = new DiffFile(path, status, lines)
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
                before = _versions.TryGetValue(path, out var versions) ? versions[0].Content : null;
            var (index, head, now) = (FromGit(read.Index, path), FromGit(read.Head, path), Read(path));
            var disk = GitChanges.OnDisk(path);
            var utf8 = (Index: read.Index is null || GitChanges.Lines(read.Index) is not null, Head: read.Head is null || GitChanges.Lines(read.Head) is not null,
                Disk: disk is not null && GitChanges.Lines(disk) is not null);
            var splittable = !read.Conflict && !DocumentText.Handles(path);
            if (before is not null)
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
                content = _versions.TryGetValue(path, out var versions) ? versions[0].Content : null;
            if (content is null)
                return "Sloperator has nothing of this file from before.";
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
        }
        try
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
                    return "Sloperator didn't keep what this file held before: it isn't text.";
                await TextFiles.WriteAllAsync(path, before, File.Exists(path) ? TextFiles.Detect(path) : TextFiles.ForNewFile(path), ct).ConfigureAwait(false);
                return null;
            }
            var now = Read(path);
            if (DocumentText.Handles(path) || now.Exists && now.Text is null || existed && before is null)
                return "Only the whole file can be put back: it isn't text.";
            var old = GitChanges.Split(before ?? "");
            var current = GitChanges.Split(now.Text ?? "");
            var lineBreak = GitChanges.Break(current) ?? GitChanges.Break(old) ?? "\n";
            var kept = GitChanges.Mix(old, current, lines.ToHashSet(), apply: false, lineBreak);
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
