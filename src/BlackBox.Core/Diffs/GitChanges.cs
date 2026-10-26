using System.Text;

namespace BlackBox.Core.Diffs;

/// <summary>Where a change to a file in a git repository is: committed, staged, or only on disk.</summary>
public enum GitSection
{
    /// <summary>In the last commit (HEAD), against what the file held before the agent changed it.</summary>
    Committed,

    /// <summary>In the index, against the last commit.</summary>
    Staged,

    /// <summary>On disk, against the index (a file git doesn't track yet, whole).</summary>
    Unstaged,
}

/// <summary>A run of changed lines between two texts, 0-based: <c>OldCount</c> lines from <c>OldStart</c> became <c>NewCount</c> from <c>NewStart</c>.</summary>
public readonly record struct ChangeBlock(int OldStart, int OldCount, int NewStart, int NewCount);

/// <summary>A file's diff in one of the File changes panel's git sections, as its Stage and Unstage buttons need it.</summary>
public sealed class GitFile(string root, string path, GitSection section, bool splittable)
{
    /// <summary>The repository's top folder.</summary>
    public string Root { get; } = root;

    /// <summary>The file's path in the repository, with forward slashes.</summary>
    public string Path { get; } = path;

    public GitSection Section { get; } = section;

    /// <summary>Its hunks can be staged or unstaged one by one: UTF-8 text, not a Word or PDF file read as text, no merge conflict.</summary>
    public bool Splittable { get; } = splittable;
}

/// <summary>
/// Git's side of the File changes panel: what a changed file holds in the last commit and the
/// index, for its Committed, Staged and Unstaged sections, and staging or unstaging a file or some
/// of its lines, which writes the repository's index (and nothing else: no commit, no change on disk).
/// </summary>
public static class GitChanges
{
    /// <summary>A file as git has it: its status letters (git status --porcelain: index, then work tree) and its bytes in the index and the last commit (null where it isn't).</summary>
    internal sealed record Read(string Root, string Path, char X, char Y, byte[]? Index, byte[]? Head)
    {
        /// <summary>A merge conflict, whose stages git keeps apart: only git add can settle it.</summary>
        public bool Conflict => X == 'U' || Y == 'U' || X == Y && X is 'A' or 'D';
    }

    private static readonly UTF8Encoding Strict = new(false, throwOnInvalidBytes: true);

    /// <summary>Of these files, those git sees (tracked, or untracked and not ignored), with what it has of them.</summary>
    internal static async Task<Dictionary<string, Read>> ReadAsync(IReadOnlyList<string> paths, CancellationToken ct)
    {
        var read = new Dictionary<string, Read>(StringComparer.OrdinalIgnoreCase);
        var roots = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var byRoot = new Dictionary<string, List<(string Full, string Relative)>>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var full in paths)
            {
                var dir = System.IO.Path.GetDirectoryName(full) ?? "";
                if (!roots.TryGetValue(dir, out var root))
                    roots[dir] = root = roots.Count < 50 ? await GitDiff.RootOfAsync(dir, ct).ConfigureAwait(false) : null;
                if (root is null)
                    continue;
                var relative = System.IO.Path.GetRelativePath(root, full).Replace('\\', '/');
                if (relative.StartsWith("../", StringComparison.Ordinal) || System.IO.Path.IsPathRooted(relative) || relative.Contains('\n'))
                    continue;
                if (!byRoot.TryGetValue(root, out var list))
                    byRoot[root] = list = [];
                list.Add((full, relative));
            }
            foreach (var (root, files) in byRoot)
            {
                var status = await StatusAsync(root, files.Select(f => f.Relative).ToList(), ct).ConfigureAwait(false);
                var blobs = await BlobsAsync(root, files.SelectMany(f => new[] { ":" + f.Relative, "HEAD:" + f.Relative }).ToList(), ct).ConfigureAwait(false);
                foreach (var (full, relative) in files)
                {
                    var (x, y) = status.GetValueOrDefault(relative, (' ', ' '));
                    var index = blobs.GetValueOrDefault(":" + relative);
                    var head = blobs.GetValueOrDefault("HEAD:" + relative);
                    // Neither tracked nor listed as untracked: git ignores it.
                    if (x == ' ' && y == ' ' && index is null && head is null)
                        continue;
                    read[full] = new Read(root, relative, x, y, index, head);
                }
            }
        }
        catch (DiffException) { } // no git on this PC, or it failed: no file is in git as far as the panel knows
        return read;
    }

    /// <summary>The status letters of each path that isn't as committed: "M " staged, " M" not, "??" untracked.</summary>
    private static async Task<Dictionary<string, (char X, char Y)>> StatusAsync(string root, List<string> relative, CancellationToken ct)
    {
        var status = new Dictionary<string, (char, char)>(StringComparer.Ordinal);
        var output = await GitDiff.RunAsync(root, ["status", "--porcelain=v1", "-z", "--untracked-files=all", "--ignored=no", "--no-renames", "--", .. relative], ct, 400_000).ConfigureAwait(false);
        if (output.Exit != 0)
            throw new DiffException(output.Err.Trim());
        foreach (var entry in output.Out.Split('\0', StringSplitOptions.RemoveEmptyEntries))
            if (entry.Length > 3)
                status[entry[3..]] = (entry[0], entry[1]);
        return status;
    }

    /// <summary>The bytes of each of these (":path" in the index, "HEAD:path" in the last commit) that git has; read in one go by cat-file --batch.</summary>
    private static async Task<Dictionary<string, byte[]>> BlobsAsync(string root, List<string> names, CancellationToken ct)
    {
        var blobs = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var input = Encoding.UTF8.GetBytes(string.Concat(names.Select(n => n + "\n")));
        var (exit, output, _) = await GitDiff.RunBytesAsync(root, ["cat-file", "--batch"], input, ct, 200_000_000).ConfigureAwait(false);
        if (exit != 0 || output is null)
            return blobs;
        // "<id> blob <size>\n<bytes>\n" for each, or "<name> missing\n".
        var at = 0;
        foreach (var name in names)
        {
            var end = Array.IndexOf(output, (byte)'\n', at);
            if (end < 0)
                break;
            var header = Encoding.UTF8.GetString(output, at, end - at).Split(' ');
            at = end + 1;
            if (header.Length == 3 && long.TryParse(header[2], out var size) && at + size <= output.Length)
            {
                if (header[1] == "blob")
                    blobs[name] = output.AsSpan(at, (int)size).ToArray();
                at += (int)size + 1;
            }
        }
        return blobs;
    }

    /// <summary>The file's bytes; empty for one that isn't there, null for one too big or unreadable.</summary>
    internal static byte[]? OnDisk(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return !info.Exists ? [] : info.Length > 2_000_000 ? null : File.ReadAllBytes(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// UTF-8 text cut into lines that keep their line breaks, at the breaks string.ReplaceLineEndings
    /// knows (so line numbers are the diff's), its byte order mark kept on the first; null for
    /// bytes that aren't UTF-8 text.
    /// </summary>
    internal static List<string>? Lines(byte[] bytes)
    {
        if (Array.IndexOf(bytes, (byte)0) >= 0)
            return null;
        string text;
        try
        {
            text = Strict.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return null;
        }
        return Split(text);
    }

    /// <summary>A text cut into lines that keep their line breaks, at the breaks string.ReplaceLineEndings knows, so the lines are the diff's.</summary>
    internal static List<string> Split(string text)
    {
        var lines = new List<string>();
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c is not ('\r' or '\n' or '\f' or '\u0085' or '\u2028' or '\u2029'))
                continue;
            if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
                i++;
            lines.Add(text[start..(i + 1)]);
            start = i + 1;
        }
        if (start < text.Length)
            lines.Add(text[start..]);
        return lines;
    }

    /// <summary>A line without its line break, as the diff compares it.</summary>
    internal static string Key(string line) => line.TrimEnd('\r', '\n', '\f', '\u0085', '\u2028', '\u2029');

    /// <summary>
    /// Whether a run of changes is at the span's lines of its new side, or right beside a place between
    /// lines: a span is (from, to), 1-based, both included; (n, n - 1) is the place between lines
    /// n - 1 and n, where lines were removed.
    /// </summary>
    internal static bool Touch((int From, int To) span, ChangeBlock block)
    {
        // Lines n at 2n, the place between lines n - 1 and n at 2n - 1: touching is being at most one apart.
        var (lo, hi) = span.To >= span.From ? (2 * span.From, 2 * span.To) : (2 * span.From - 1, 2 * span.From - 1);
        var first = block.NewStart + 1;
        var (blo, bhi) = block.NewCount > 0 ? (2 * first, 2 * (block.NewStart + block.NewCount)) : (2 * first - 1, 2 * first - 1);
        return lo <= bhi + 1 && blo <= hi + 1;
    }

    // ---- staging and unstaging ------------------------------------------------------------------------

    /// <summary>
    /// Stages the file (<paramref name="spans"/> null), as git add does, or only the changes at those
    /// lines of the file on disk (<see cref="Touch"/>), as an Unstaged hunk's Stage button asks.
    /// Null when done, else why not.
    /// </summary>
    public static async Task<string?> StageAsync(string fullPath, IReadOnlyList<(int From, int To)>? spans, CancellationToken ct)
    {
        try
        {
            if (await PlaceAsync(fullPath, ct).ConfigureAwait(false) is not ({ } root, { } relative))
                return "This file isn't in a git repository.";
            if (spans is null || !File.Exists(fullPath))
                return await AddAsync(root, relative, ct).ConfigureAwait(false);

            var indexBytes = (await BlobsAsync(root, [":" + relative], ct).ConfigureAwait(false)).GetValueOrDefault(":" + relative);
            if (Lines(indexBytes ?? []) is not { } index || OnDisk(fullPath) is not { } bytes || Lines(bytes) is not { } now)
                return "Only the whole file can be staged: it isn't UTF-8 text.";
            var picked = LineDiff.Blocks(index.Select(Key).ToArray(), now.Select(Key).ToArray()).Where(b => spans.Any(s => Touch(s, b))).ToList();
            if (picked.Count == 0)
                return "Nothing here is left to stage.";
            var staged = Apply(index, now, picked);
            // All of it, the same as git add: which also keeps a new file's mode as git sees it.
            if (staged == string.Concat(now))
                return await AddAsync(root, relative, ct).ConfigureAwait(false);
            // As git add would store these bytes: its filters for the path (line endings) applied.
            return await WriteIndexAsync(root, relative, staged, filters: true, indexBytes is not null, ct).ConfigureAwait(false);
        }
        catch (DiffException ex)
        {
            return ex.Message;
        }
    }

    /// <summary>
    /// Unstages the file (<paramref name="spans"/> null), as git restore --staged does, or only the
    /// staged changes at those lines of the staged file, as a Staged hunk's Unstage button asks. The
    /// file on disk stays as it is. Null when done, else why not.
    /// </summary>
    public static async Task<string?> UnstageAsync(string fullPath, IReadOnlyList<(int From, int To)>? spans, CancellationToken ct)
    {
        try
        {
            if (await PlaceAsync(fullPath, ct).ConfigureAwait(false) is not ({ } root, { } relative))
                return "This file isn't in a git repository.";
            var blobs = await BlobsAsync(root, [":" + relative, "HEAD:" + relative], ct).ConfigureAwait(false);
            var (indexBytes, headBytes) = (blobs.GetValueOrDefault(":" + relative), blobs.GetValueOrDefault("HEAD:" + relative));
            if (spans is not null && indexBytes is not null)
            {
                if (Lines(indexBytes) is not { } index || Lines(headBytes ?? []) is not { } head)
                    return "Only the whole file can be unstaged: it isn't UTF-8 text.";
                var blocks = LineDiff.Blocks(head.Select(Key).ToArray(), index.Select(Key).ToArray());
                var kept = blocks.Where(b => !spans.Any(s => Touch(s, b))).ToList();
                if (kept.Count == blocks.Count)
                    return "Nothing here is staged.";
                if (kept.Count > 0 || headBytes is not null)
                    // Already as git stores it: no filters again.
                    return await WriteIndexAsync(root, relative, Apply(head, index, kept), filters: false, hadIndex: true, ct).ConfigureAwait(false);
            }
            // The whole file: as last committed in the index, or out of it when it is new.
            var restore = headBytes is not null
                ? await GitDiff.RunAsync(root, ["restore", "--staged", "--", relative], ct, 10_000).ConfigureAwait(false)
                : await GitDiff.RunAsync(root, ["rm", "--cached", "-q", "--", relative], ct, 10_000).ConfigureAwait(false);
            return restore.Exit == 0 ? null : FirstLine(restore.Err, "git couldn't unstage the file.");
        }
        catch (DiffException ex)
        {
            return ex.Message;
        }
    }

    /// <summary>The repository the file is in and its path there; null outside one.</summary>
    private static async Task<(string Root, string Relative)?> PlaceAsync(string fullPath, CancellationToken ct)
    {
        var root = await GitDiff.RootOfAsync(System.IO.Path.GetDirectoryName(fullPath) ?? fullPath, ct).ConfigureAwait(false);
        return root is null ? null : (root, System.IO.Path.GetRelativePath(root, fullPath).Replace('\\', '/'));
    }

    /// <summary>Puts <paramref name="text"/> in the index as the file, with the mode it has there.</summary>
    private static async Task<string?> WriteIndexAsync(string root, string relative, string text, bool filters, bool hadIndex, CancellationToken ct)
    {
        var hash = await GitDiff.RunBytesAsync(root, ["hash-object", "-w", filters ? "--path=" + relative : "--no-filters", "--stdin"], Encoding.UTF8.GetBytes(text), ct, 1_000).ConfigureAwait(false);
        var id = hash.Out is null ? "" : Encoding.UTF8.GetString(hash.Out).Trim();
        if (hash.Exit != 0 || id.Length == 0)
            return FirstLine(hash.Err, "git couldn't store the lines.");
        var listed = await GitDiff.RunAsync(root, ["ls-files", "-s", "--", relative], ct, 10_000).ConfigureAwait(false);
        var mode = hadIndex && listed.Out.Split(' ')[0] is { Length: 6 } m ? m : "100644";
        var update = await GitDiff.RunAsync(root, ["update-index", "--add", "--cacheinfo", $"{mode},{id},{relative}"], ct, 10_000).ConfigureAwait(false);
        return update.Exit == 0 ? null : FirstLine(update.Err, "git couldn't update the index.");
    }

    private static async Task<string?> AddAsync(string root, string relative, CancellationToken ct)
    {
        // A file deleted on disk is staged as deleted, as git add does since git 2.0.
        var add = await GitDiff.RunAsync(root, ["add", "--", relative], ct, 10_000).ConfigureAwait(false);
        return add.Exit == 0 ? null : FirstLine(add.Err, "git add failed.");
    }

    /// <summary>The old side's lines with the picked runs taken from the new side, the rest as they were.</summary>
    internal static string Apply(List<string> old, List<string> now, IReadOnlyList<ChangeBlock> picked)
    {
        var lines = new List<string>();
        var at = 0;
        foreach (var block in picked.OrderBy(b => b.OldStart))
        {
            lines.AddRange(old.Skip(at).Take(block.OldStart - at));
            lines.AddRange(now.Skip(block.NewStart).Take(block.NewCount));
            at = block.OldStart + block.OldCount;
        }
        lines.AddRange(old.Skip(at));
        return Join(lines, Break(now) ?? Break(old) ?? "\n");
    }

    /// <summary>The lines one after another, a line without a break that is no longer last given <paramref name="lineBreak"/>.</summary>
    internal static string Join(IReadOnlyList<string> lines, string lineBreak)
    {
        var text = new StringBuilder();
        for (var i = 0; i < lines.Count; i++)
        {
            text.Append(lines[i]);
            if (i < lines.Count - 1 && Key(lines[i]).Length == lines[i].Length)
                text.Append(lineBreak);
        }
        return text.ToString();
    }

    /// <summary>The line break the first line ends with.</summary>
    internal static string? Break(List<string> lines) =>
        lines.FirstOrDefault(l => Key(l).Length < l.Length) is { } line ? line[Key(line).Length..] : null;

    /// <summary>The file on disk put back as it is staged; the index stays.</summary>
    internal static async Task<string?> RestoreAsync(string root, string relative, CancellationToken ct)
    {
        var restore = await GitDiff.RunAsync(root, ["restore", "--worktree", "--", relative], ct, 10_000).ConfigureAwait(false);
        return restore.Exit == 0 ? null : FirstLine(restore.Err, "git couldn't put the file back.");
    }

    private static string FirstLine(string text, string otherwise) =>
        text.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) is { } line ? line.Replace("fatal: ", "").Replace("error: ", "") : otherwise;
}
