namespace Sloperator.Core.Diffs;

public enum DiffLineKind
{
    /// <summary>A hunk's "@@ -10,6 +10,7 @@" line, where it starts in each side.</summary>
    Hunk,
    Context,
    Added,
    Removed,
    /// <summary>"\ No newline at end of file".</summary>
    Note,
}

/// <summary>A line of a diff: its number on the old side and on the new (null where it isn't on one), and its text without the +/- in front.</summary>
public readonly record struct DiffLine(DiffLineKind Kind, int? Old, int? New, string Text)
{
    /// <summary>The longest line shown whole: a minified file's single line came whole into a diff.</summary>
    internal const int MaxLine = 1_000;

    internal static string Cut(string line) =>
        line.Length <= MaxLine ? line : line[..MaxLine] + $" [... {line.Length - MaxLine:N0} more characters ...]";

    /// <summary>As a unified diff writes it: the @@ line, or the text after " ", "+", "-" or "\".</summary>
    public string Unified => Kind switch
    {
        DiffLineKind.Hunk => Text,
        DiffLineKind.Added => "+" + Text,
        DiffLineKind.Removed => "-" + Text,
        DiffLineKind.Note => "\\" + Text,
        _ => " " + Text,
    };
}

/// <summary>
/// A file that changed: its path (relative to <see cref="DiffResult.Root"/>, with forward slashes),
/// what happened to it ("modified", "added", "deleted", "renamed", "copied"), and its hunks.
/// </summary>
public sealed record DiffFile(string Path, string Status, IReadOnlyList<DiffLine> Lines)
{
    /// <summary>Where it was before, when it was renamed or copied.</summary>
    public string? OldPath { get; init; }

    public int Added { get; init; }
    public int Removed { get; init; }

    /// <summary>A file that isn't text: whether it changed is all that is known.</summary>
    public bool Binary { get; init; }

    /// <summary>In the File changes panel, the file's section in its git repository (and how to stage it); null outside one.</summary>
    public GitFile? Git { get; init; }

    /// <summary>"src/a.cs", or "old.cs -> new.cs" for a rename.</summary>
    public string Display => OldPath is null ? Path : $"{OldPath} -> {Path}";

    /// <summary>"+10 -3", "+30", "binary".</summary>
    public string Counts => Binary ? "binary" : string.Join(" ", new[] { Added > 0 ? $"+{Added}" : null, Removed > 0 ? $"-{Removed}" : null }.Where(c => c is not null)) is { Length: > 0 } c ? c : "no lines";
}

/// <summary>A file in the working tree that git doesn't track yet: its path, and its line count (null for a binary file or one too big to count).</summary>
public sealed record UntrackedFile(string Path, long? Lines);

/// <summary>
/// What changed: in a git repository (<see cref="GitDiff"/>), or between two files or folders
/// (<see cref="FolderDiff"/>). <see cref="Root"/> is what file paths are relative to.
/// </summary>
/// <param name="Title">What was compared: "Uncommitted changes", "Commit 1a2b3c4", "Changes since main".</param>
public sealed record DiffResult(string Root, string Title, IReadOnlyList<DiffFile> Files)
{
    /// <summary>Files git doesn't track yet, which a diff doesn't show (only for changes on disk).</summary>
    public IReadOnlyList<UntrackedFile> Untracked { get; init; } = [];

    /// <summary>A commit's hash, author, date and message, when one commit is shown.</summary>
    public string? About { get; init; }

    /// <summary>The diff was longer than could be read: the files after where it was cut are missing.</summary>
    public bool Truncated { get; init; }

    /// <summary>
    /// Whether the new side is the files as they are on disk now, so its line numbers are where to
    /// change them: not for a commit, the staged changes, or a range of commits.
    /// </summary>
    public bool NewIsOnDisk { get; init; }

    /// <summary>In the File changes panel, whether any file the agent changed is in a git repository: its files are then split into sections (<see cref="DiffFile.Git"/>).</summary>
    public bool InGit { get; init; }

    public int Added => Files.Sum(f => f.Added);
    public int Removed => Files.Sum(f => f.Removed);

    /// <summary>The full path of a file of it.</summary>
    public string FullPath(DiffFile file) => System.IO.Path.GetFullPath(System.IO.Path.Combine(Root, file.Path));
}

public sealed class DiffException(string message, bool notRepository = false) : Exception(message)
{
    /// <summary>The path isn't in a git repository (as opposed to git failing there).</summary>
    public bool NotRepository { get; } = notRepository;
}
