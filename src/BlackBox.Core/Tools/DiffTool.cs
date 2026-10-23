using System.Text;
using System.Text.Json.Nodes;
using BlackBox.Core.Diffs;

namespace BlackBox.Core.Tools;

/// <summary>
/// What changed, for the model to look through without reading whole files again or running git
/// by hand: the uncommitted changes of a repository, what a commit did, or how two files or
/// folders differ, as a list of the files with their line counts, then their hunks, in parts.
/// </summary>
public sealed class DiffTool : Tool
{
    /// <summary>Characters per call (about 3k tokens): a review reads on part by part, or narrows the path.</summary>
    internal const int PartSize = 12_000;

    /// <summary>Files listed by name in the summary at most.</summary>
    private const int MaxListed = 200;

    public override string Name => "diff";
    public override string Description =>
        "Show what changed, as a unified diff after a list of the files changed and their line counts: much cheaper than reading files again " +
        "or running git through run_shell. In a git repository it shows the uncommitted changes (staged and not) under path (the repository, or a folder or file in it); " +
        "against compares with a commit, branch or range instead (HEAD~3, main, main...feature), commit shows what one commit did (HEAD for the latest), " +
        "staged=true only what is staged. Files not tracked yet are listed, not shown. With other, it compares path with another file or folder " +
        "line by line, in a repository or not (Word, Excel, PowerPoint and PDF files by their text). Long diffs come in parts: pass start to read on, " +
        "or give the path of one file; summary=true gives only the list.";
    public override ToolRisk Risk => ToolRisk.ReadOnly;
    public override JsonObject Parameters => Schema(
        ("path", Str("A repository, or a folder or file in one; with other, the old file or folder (default: working directory)"), false),
        ("against", Str("Commit, branch, tag or range to compare with instead of HEAD"), false),
        ("commit", Str("Show what this one commit did (HEAD, a hash, a branch)"), false),
        ("staged", Bool("Only the changes staged for the next commit (default false)"), false),
        ("other", Str("A second file or folder to compare path with: how it differs from path"), false),
        ("context", Int("Unchanged lines shown around each change (default 3)"), false),
        ("summary", Bool("Only the list of changed files with their line counts (default false)"), false),
        ("start", Int("Character to start at, to read on in a long diff (default 0)"), false));

    public override string Describe(JsonObject args)
    {
        var path = GetString(args, "path") ?? ".";
        if (GetString(args, "other") is { Length: > 0 } other)
            return $"Compare {path} with {other}";
        if (GetString(args, "commit") is { Length: > 0 } commit)
            return $"Diff of commit {commit} in {path}";
        return $"Diff {path}" + (GetString(args, "against") is { Length: > 0 } against ? $" against {against}" : GetBool(args, "staged") ? " (staged)" : "");
    }

    public override async Task<ToolResult> ExecuteAsync(JsonObject args, ToolContext ctx, CancellationToken ct)
    {
        var path = ctx.ResolvePath(GetString(args, "path") is { Length: > 0 } p ? p : ".");
        var context = Math.Clamp((int)(GetNumber(args, "context") ?? 3), 0, 50);
        var start = Math.Max(0, (int)(GetNumber(args, "start") ?? 0));
        DiffResult diff;
        string where;
        try
        {
            if (GetString(args, "other") is { Length: > 0 } other)
            {
                diff = await FolderDiff.CompareAsync(path, ctx.ResolvePath(other), context, ct).ConfigureAwait(false);
                where = "";
            }
            else
            {
                diff = await GitDiff.ChangesAsync(new DiffRequest(path, Text(args, "against"), Text(args, "commit"), GetBool(args, "staged"), context), ct).ConfigureAwait(false);
                where = Path.TrimEndingDirectorySeparator(path) is var under && !string.Equals(under, diff.Root, StringComparison.OrdinalIgnoreCase)
                    ? $" in {diff.Root} (under {Path.GetRelativePath(diff.Root, under)})" : $" in {diff.Root}";
            }
        }
        catch (DiffException ex)
        {
            return ToolResult.Error(ex.NotRepository ? $"{ex.Message} To compare two files or folders, give the second one as other."
                : ex.Message == GitDiff.NoGit ? $"{ex.Message} With other, two files or folders can still be compared." : ex.Message);
        }
        var text = Format(diff, where, GetBool(args, "summary"));
        return new ToolResult(Part(text, start));
    }

    private static string? Text(JsonObject args, string key) => GetString(args, key) is { } s && s.Trim().Length > 0 ? s.Trim() : null;

    /// <summary>The list of files with their counts, then each file's hunks (under "=== path").</summary>
    internal static string Format(DiffResult diff, string where, bool summaryOnly)
    {
        var sb = new StringBuilder();
        sb.Append(diff.Title).Append(where).Append(':');
        if (diff.About is { } about)
            sb.Append('\n').Append(about).Append('\n');
        if (diff.Files.Count == 0)
            sb.Append(" no changes.\n");
        else
            sb.Append($" {diff.Files.Count} {(diff.Files.Count == 1 ? "file" : "files")} changed, {diff.Added} lines added, {diff.Removed} removed\n");
        foreach (var file in diff.Files.Take(MaxListed))
            sb.Append($"  {file.Status} {file.Display} ({file.Counts})\n");
        if (diff.Files.Count > MaxListed)
            sb.Append($"  [... {diff.Files.Count - MaxListed} more files; give a folder as path to see its part]\n");
        if (diff.Untracked.Count > 0)
        {
            sb.Append($"Not tracked by git yet, so not in the diff ({diff.Untracked.Count}{(diff.Untracked.Count >= 500 ? "+" : "")}; read_file shows them):\n");
            foreach (var file in diff.Untracked.Take(MaxListed / 4))
                sb.Append($"  {file.Path} ({(file.Lines is { } n ? $"{n} lines" : "binary or big")})\n");
            if (diff.Untracked.Count > MaxListed / 4)
                sb.Append($"  [... {diff.Untracked.Count - MaxListed / 4} more]\n");
        }
        if (diff.Truncated)
            sb.Append("[The diff is too long to read whole: the last files are missing. Give a folder or file as path.]\n");
        if (summaryOnly || diff.Files.Count == 0)
            return sb.ToString().TrimEnd();

        foreach (var file in diff.Files)
        {
            sb.Append($"\n=== {file.Display} ({file.Status}{(file.Binary ? ", binary" : "")})\n");
            foreach (var line in file.Lines)
                sb.Append(line.Unified).Append('\n');
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// The text from <paramref name="start"/>, at most <see cref="PartSize"/> characters, ending at a
    /// file's start where one is in the second half of the part, else at a line break; a later part
    /// says which file it goes on in.
    /// </summary>
    internal static string Part(string text, int start)
    {
        if (start == 0 && text.Length <= PartSize)
            return text;
        if (start >= text.Length)
            return $"The diff has only {text.Length} characters; start={start} is past its end.";
        if (start > 0)
        {
            var lineStart = text.LastIndexOf('\n', start - 1) + 1;
            if (start - lineStart < 1_000)
                start = lineStart;
        }
        var end = Math.Min(text.Length, start + PartSize);
        if (end < text.Length)
        {
            var file = text.LastIndexOf("\n=== ", end - 1, end - start, StringComparison.Ordinal);
            var lineEnd = text.LastIndexOf('\n', end - 1, end - start);
            // Past the line break, so the next part starts on a line of its own.
            end = file > start + PartSize / 2 ? file + 1 : lineEnd > start ? lineEnd + 1 : end;
        }

        var sb = new StringBuilder();
        if (start > 0)
        {
            sb.Append($"(characters {start}-{end} of the diff's {text.Length}");
            var file = text.LastIndexOf("\n=== ", start, StringComparison.Ordinal);
            if (file >= 0 && !text.AsSpan(start).StartsWith("=== "))
                sb.Append(", going on in ").Append(text.AsSpan(file + 5, (text.IndexOf('\n', file + 1) is var e and >= 0 ? e : text.Length) - file - 5));
            sb.Append(")\n");
        }
        sb.Append(text, start, end - start);
        if (end < text.Length)
            sb.Append($"\n\n[{text.Length - end} more characters: diff with start={end} reads on, or give one file's path]");
        return sb.ToString().TrimEnd();
    }
}
