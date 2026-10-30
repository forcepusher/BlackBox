using Sloperator.Core.Tools;

namespace Sloperator.Core.Diffs;

/// <summary>
/// Two files, or two folders file by file, compared line by line without git: how the second
/// differs from the first. Word, Excel, PowerPoint and PDF files are compared by the text read_file
/// shows of them.
/// </summary>
public static class FolderDiff
{
    /// <summary>Files of a folder compared at most.</summary>
    private const int MaxFiles = 5_000;

    /// <summary>A file bigger than this is only said to differ.</summary>
    private const long MaxBytes = 20_000_000;

    /// <exception cref="DiffException">A path doesn't exist, or one is a file and the other a folder.</exception>
    public static async Task<DiffResult> CompareAsync(string before, string after, int context, CancellationToken ct)
    {
        before = Path.GetFullPath(before);
        after = Path.GetFullPath(after);
        foreach (var p in new[] { before, after })
            if (!File.Exists(p) && !Directory.Exists(p))
                throw new DiffException($"{p} doesn't exist.");
        if (File.Exists(before) != File.Exists(after))
            throw new DiffException("One of the two is a file and the other a folder: compare two files or two folders.");

        if (File.Exists(before))
        {
            var file = await CompareFileAsync(before, after, after, context, ct).ConfigureAwait(false);
            if (file is not null && !string.Equals(Path.GetFileName(before), Path.GetFileName(after), StringComparison.OrdinalIgnoreCase))
                file = file with { OldPath = before };
            return new DiffResult("", $"How {after} differs from {before}", file is null ? [] : [file]);
        }

        var (beforeFiles, cutA) = Files(before);
        var (afterFiles, cutB) = Files(after);
        var files = new List<DiffFile>();
        foreach (var name in beforeFiles.Keys.Union(afterFiles.Keys, StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase))
        {
            ct.ThrowIfCancellationRequested();
            var a = beforeFiles.GetValueOrDefault(name);
            var b = afterFiles.GetValueOrDefault(name);
            if (await CompareFileAsync(a, b, name.Replace('\\', '/'), context, ct).ConfigureAwait(false) is { } file)
                files.Add(file);
        }
        return new DiffResult(after, $"How {after} differs from {before}", files) { Truncated = cutA || cutB };
    }

    /// <summary>The files under a folder by their path in it, but for git's own.</summary>
    private static (Dictionary<string, string> Files, bool Cut) Files(string folder)
    {
        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.System };
        foreach (var path in Directory.EnumerateFiles(folder, "*", options))
        {
            var relative = Path.GetRelativePath(folder, path);
            if (relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Contains(".git", StringComparer.OrdinalIgnoreCase))
                continue;
            if (files.Count == MaxFiles)
                return (files, true);
            files[relative] = path;
        }
        return (files, false);
    }

    /// <summary>One file against another (either may be missing: added, deleted); null when they are the same.</summary>
    private static async Task<DiffFile?> CompareFileAsync(string? before, string? after, string name, int context, CancellationToken ct)
    {
        if (before is not null && after is not null && await SameBytesAsync(before, after, ct).ConfigureAwait(false))
            return null;
        string[]? a = before is null ? [] : await LinesAsync(before, ct).ConfigureAwait(false);
        string[]? b = after is null ? [] : await LinesAsync(after, ct).ConfigureAwait(false);
        var status = before is null ? "added" : after is null ? "deleted" : "modified";
        if (a is null || b is null)
            return new DiffFile(name, status, []) { Binary = true };
        var lines = LineDiff.Hunks(a, b, context);
        if (lines.Count == 0)
            return new DiffFile(name, status, [new DiffLine(DiffLineKind.Note, null, null, "Only the encoding or the line endings differ.")]);
        return new DiffFile(name, status, lines)
        {
            Added = lines.Count(l => l.Kind == DiffLineKind.Added),
            Removed = lines.Count(l => l.Kind == DiffLineKind.Removed),
        };
    }

    /// <summary>A file's lines; null when it isn't text, or is too big.</summary>
    private static async Task<string[]?> LinesAsync(string path, CancellationToken ct)
    {
        try
        {
            if (new FileInfo(path).Length > MaxBytes)
                return null;
            if (DocumentText.Handles(path))
                return LineDiff.Lines(await Task.Run(() => DocumentText.Read(path, ct), ct).ConfigureAwait(false));
            var head = new byte[8000];
            int read;
            await using (var stream = TextFiles.OpenRead(path))
                read = await stream.ReadAtLeastAsync(head, head.Length, throwOnEndOfStream: false, ct).ConfigureAwait(false);
            if (TextFiles.IsBinary(head.AsSpan(0, read)))
                return null;
            var (text, _) = await TextFiles.ReadAllAsync(path, ct).ConfigureAwait(false);
            return LineDiff.Lines(text);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return null;
        }
    }

    private static async Task<bool> SameBytesAsync(string a, string b, CancellationToken ct)
    {
        try
        {
            if (new FileInfo(a).Length != new FileInfo(b).Length)
                return false;
            await using var x = TextFiles.OpenRead(a);
            await using var y = TextFiles.OpenRead(b);
            var (bx, by) = (new byte[64 * 1024], new byte[64 * 1024]);
            while (true)
            {
                var n = await x.ReadAtLeastAsync(bx, bx.Length, throwOnEndOfStream: false, ct).ConfigureAwait(false);
                var m = await y.ReadAtLeastAsync(by, by.Length, throwOnEndOfStream: false, ct).ConfigureAwait(false);
                if (n != m || !bx.AsSpan(0, n).SequenceEqual(by.AsSpan(0, m)))
                    return false;
                if (n == 0)
                    return true;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
