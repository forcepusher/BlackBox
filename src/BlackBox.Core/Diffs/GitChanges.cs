using System.Text;

namespace BlackBox.Core.Diffs;

/// <summary>Which of the agent's changes the File changes panel shows.</summary>
public enum ChangesShown
{
    /// <summary>Each file against what it held before the agent first changed it.</summary>
    All,

    /// <summary>Each file in a git repository against its last commit (HEAD): what isn't committed yet.</summary>
    NotCommitted,

    /// <summary>Each file in a git repository against what is staged (the index): what isn't staged yet.</summary>
    NotStaged,
}

/// <summary>A run of changed lines between two texts, 0-based: <c>OldCount</c> lines from <c>OldStart</c> became <c>NewCount</c> from <c>NewStart</c>.</summary>
public readonly record struct ChangeBlock(int OldStart, int OldCount, int NewStart, int NewCount);

/// <summary>
/// A changed file in a git repository, as the File changes panel's Stage buttons need it: whether
/// some of it isn't staged, and where (<see cref="Blocks"/>, from what is staged to the file on disk).
/// </summary>
public sealed class GitFile(string root, string path, bool unstaged, IReadOnlyList<ChangeBlock>? blocks)
{
    /// <summary>The repository's top folder.</summary>
    public string Root { get; } = root;

    /// <summary>The file's path in the repository, with forward slashes.</summary>
    public string Path { get; } = path;

    /// <summary>The file on disk differs from what is staged, or git doesn't track it yet: Stage has something to do.</summary>
    public bool Unstaged { get; } = unstaged;

    /// <summary>
    /// What isn't staged, run by run, in lines of the file on disk; null when parts of it can't be
    /// staged on their own (not UTF-8 text, a Word or PDF file read as text, a merge conflict).
    /// </summary>
    public IReadOnlyList<ChangeBlock>? Blocks { get; } = blocks;

    /// <summary>
    /// Whether any of what isn't staged is at the lines of the file on disk <paramref name="spans"/>
    /// names: (from, to), 1-based, both included; (n, n - 1) is the place between lines n - 1 and n,
    /// where lines were removed.
    /// </summary>
    public bool Touches(IEnumerable<(int From, int To)> spans) => Blocks is { } blocks && spans.Any(s => blocks.Any(b => GitChanges.Touch(s, b)));
}

/// <summary>
/// Git's side of the File changes panel: what a changed file holds in the last commit and the
/// index, for its Not committed and Not staged views, and staging a file or some of its lines,
/// which writes the repository's index (and nothing else: no commit, no change on disk).
/// </summary>
public static class GitChanges
{
    /// <summary>A file as git has it: its status letters (git status --porcelain: index, then work tree) and its bytes in the index and the last commit (null where it isn't).</summary>
    internal sealed record Read(string Root, string Path, char X, char Y, byte[]? Index, byte[]? Head);

    private static readonly UTF8Encoding Strict = new(false, throwOnInvalidBytes: true);

    /// <summary>Of these files, those git sees (tracked, or untracked and not ignored), with what it has of them; <paramref name="withHead"/> reads their last commit too.</summary>
    internal static async Task<Dictionary<string, Read>> ReadAsync(IReadOnlyList<string> paths, bool withHead, CancellationToken ct)
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
                var blobs = await BlobsAsync(root, files.SelectMany(f => withHead ? [":" + f.Relative, "HEAD:" + f.Relative] : new[] { ":" + f.Relative }).ToList(), ct).ConfigureAwait(false);
                foreach (var (full, relative) in files)
                {
                    var (x, y) = status.GetValueOrDefault(relative, (' ', ' '));
                    var index = blobs.GetValueOrDefault(":" + relative);
                    var head = withHead ? blobs.GetValueOrDefault("HEAD:" + relative) : null;
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

    /// <summary>The file in git's view for the Stage buttons; Blocks from <paramref name="read"/>'s index to the file on disk.</summary>
    internal static GitFile FileOf(string fullPath, Read read)
    {
        var unstaged = read.Y != ' ';
        IReadOnlyList<ChangeBlock>? blocks = null;
        if (unstaged && read.X != 'U' && read.Y != 'U' && !(read.X == read.Y && read.X is 'A' or 'D') && !Tools.DocumentText.Handles(fullPath)
            && Lines(read.Index ?? []) is { } index && Lines(OnDisk(fullPath) ?? []) is { } now)
            blocks = LineDiff.Blocks(index.Select(Key).ToArray(), now.Select(Key).ToArray());
        return new GitFile(read.Root, read.Path, unstaged, blocks);
    }

    /// <summary>The file's bytes; empty for one that isn't there, null for one too big or unreadable.</summary>
    private static byte[]? OnDisk(string path)
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

    /// <summary>Whether a run of changes is at the span's lines, or right beside a place between lines (see <see cref="GitFile.Touches"/>).</summary>
    internal static bool Touch((int From, int To) span, ChangeBlock block)
    {
        // Lines n at 2n, the place between lines n - 1 and n at 2n - 1: touching is being at most one apart.
        var (lo, hi) = span.To >= span.From ? (2 * span.From, 2 * span.To) : (2 * span.From - 1, 2 * span.From - 1);
        var first = block.NewStart + 1;
        var (blo, bhi) = block.NewCount > 0 ? (2 * first, 2 * (block.NewStart + block.NewCount)) : (2 * first - 1, 2 * first - 1);
        return lo <= bhi + 1 && blo <= hi + 1;
    }

    // ---- staging ----------------------------------------------------------------------------------------

    /// <summary>
    /// Stages the file (<paramref name="spans"/> null), as git add does, or only what isn't staged
    /// yet at those lines of the file on disk (<see cref="GitFile.Touches"/>), as the hunk's Stage
    /// button asks. Null when done, else why not.
    /// </summary>
    public static async Task<string?> StageAsync(string fullPath, IReadOnlyList<(int From, int To)>? spans, CancellationToken ct)
    {
        try
        {
            var root = await GitDiff.RootOfAsync(System.IO.Path.GetDirectoryName(fullPath) ?? fullPath, ct).ConfigureAwait(false);
            if (root is null)
                return "This file isn't in a git repository.";
            var relative = System.IO.Path.GetRelativePath(root, fullPath).Replace('\\', '/');
            if (spans is null || !File.Exists(fullPath))
                return await AddAsync(root, relative, ct).ConfigureAwait(false);

            var blobs = await BlobsAsync(root, [":" + relative], ct).ConfigureAwait(false);
            var indexBytes = blobs.GetValueOrDefault(":" + relative);
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
            var hash = await GitDiff.RunBytesAsync(root, ["hash-object", "-w", "--path=" + relative, "--stdin"], Encoding.UTF8.GetBytes(staged), ct, 1_000).ConfigureAwait(false);
            var id = hash.Out is null ? "" : Encoding.UTF8.GetString(hash.Out).Trim();
            if (hash.Exit != 0 || id.Length == 0)
                return FirstLine(hash.Err, "git couldn't store the lines.");
            var listed = await GitDiff.RunAsync(root, ["ls-files", "-s", "--", relative], ct, 10_000).ConfigureAwait(false);
            var mode = indexBytes is not null && listed.Out.Split(' ')[0] is { Length: 6 } m ? m : "100644";
            var update = await GitDiff.RunAsync(root, ["update-index", "--add", "--cacheinfo", $"{mode},{id},{relative}"], ct, 10_000).ConfigureAwait(false);
            return update.Exit == 0 ? null : FirstLine(update.Err, "git couldn't stage the lines.");
        }
        catch (DiffException ex)
        {
            return ex.Message;
        }
    }

    private static async Task<string?> AddAsync(string root, string relative, CancellationToken ct)
    {
        // A file deleted on disk is staged as deleted, as git add does since git 2.0.
        var add = await GitDiff.RunAsync(root, ["add", "--", relative], ct, 10_000).ConfigureAwait(false);
        return add.Exit == 0 ? null : FirstLine(add.Err, "git add failed.");
    }

    /// <summary>The index's lines with the picked runs taken from the file on disk, the rest as staged.</summary>
    internal static string Apply(List<string> index, List<string> now, IReadOnlyList<ChangeBlock> picked)
    {
        var lines = new List<string>();
        var at = 0;
        foreach (var block in picked.OrderBy(b => b.OldStart))
        {
            lines.AddRange(index.Skip(at).Take(block.OldStart - at));
            lines.AddRange(now.Skip(block.NewStart).Take(block.NewCount));
            at = block.OldStart + block.OldCount;
        }
        lines.AddRange(index.Skip(at));
        return Join(lines, Break(now) ?? Break(index) ?? "\n");
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

    /// <summary>The file on disk put back as it is staged, or as it was in the last commit (<paramref name="fromHead"/>); what is staged stays.</summary>
    internal static async Task<string?> RestoreAsync(string root, string relative, bool fromHead, CancellationToken ct)
    {
        var restore = await GitDiff.RunAsync(root, ["restore", "--worktree", .. fromHead ? new[] { "--source=HEAD" } : [], "--", relative], ct, 10_000).ConfigureAwait(false);
        return restore.Exit == 0 ? null : FirstLine(restore.Err, "git couldn't put the file back.");
    }
    private static string FirstLine(string text, string otherwise) =>
        text.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) is { } line ? line.Replace("fatal: ", "").Replace("error: ", "") : otherwise;
}
