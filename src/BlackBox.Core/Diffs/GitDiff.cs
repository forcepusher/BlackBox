using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace BlackBox.Core.Diffs;

/// <summary>What to compare in a git repository: what is uncommitted by default.</summary>
/// <param name="Path">A repository, or a folder or file in one: the diff is only of what is under it.</param>
/// <param name="Against">A commit, branch, tag or range ("HEAD~3", "main", "main...feature") to compare with instead of HEAD.</param>
/// <param name="Commit">Show what this one commit did instead (against its first parent, for a merge).</param>
/// <param name="Staged">Only what is staged for the next commit.</param>
/// <param name="Context">Unchanged lines around each change.</param>
public sealed record DiffRequest(string Path, string? Against = null, string? Commit = null, bool Staged = false, int Context = 3);

/// <summary>
/// Changes in a git repository, read from the git on this PC (on the PATH, or where Git for
/// Windows, GitHub Desktop or Sourcetree put theirs) and parsed into <see cref="DiffResult"/>:
/// for the diff tool and the File changes panel. Only reads: no command here changes the repository
/// (the panel's Stage runs its own through <see cref="RunAsync"/>, in <see cref="GitChanges"/>).
/// </summary>
public static partial class GitDiff
{
    /// <summary>Characters of patch read at most; the rest is left out (<see cref="DiffResult.Truncated"/>).</summary>
    internal const int MaxChars = 4_000_000;

    private static readonly TimeSpan GitTime = TimeSpan.FromSeconds(60);

    public const string NoGit = "git isn't installed on this PC, so changes in a repository can't be shown.";

    private static string? _git;
    private static bool _looked;

    /// <summary>The git program, or null when there is none.</summary>
    public static string? Git
    {
        get
        {
            if (!_looked)
            {
                _git = Find();
                _looked = true;
            }
            return _git;
        }
    }

    private static string? Find()
    {
        var exe = OperatingSystem.IsWindows() ? "git.exe" : "git";
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(dir.Trim().Trim('"'), exe);
                if (File.Exists(candidate))
                    return candidate;
            }
            catch (ArgumentException) { }
        }
        if (!OperatingSystem.IsWindows())
            return null;
        // Installed without being put on the PATH, or only as part of a git program.
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var places = new List<string>
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"Git\cmd\git.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), @"Git\cmd\git.exe"),
            Path.Combine(local, @"Programs\Git\cmd\git.exe"),
            Path.Combine(local, @"Atlassian\SourceTree\git_local\cmd\git.exe"),
            Path.Combine(local, @"Atlassian\SourceTree\git_local\bin\git.exe"),
        };
        try
        {
            var desktop = Path.Combine(local, "GitHubDesktop");
            if (Directory.Exists(desktop))
                places.AddRange(Directory.GetDirectories(desktop, "app-*").OrderDescending().Select(d => Path.Combine(d, @"resources\app\git\cmd\git.exe")));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return places.FirstOrDefault(File.Exists);
    }

    /// <summary>The top folder of the repository <paramref name="path"/> is in; null when it is in none.</summary>
    /// <exception cref="DiffException">There is no git, or it failed.</exception>
    public static async Task<string?> RootOfAsync(string path, CancellationToken ct)
    {
        if (ExistingFolder(Path.GetFullPath(path)) is not { } dir)
            return null;
        var top = await RunAsync(dir, ["rev-parse", "--show-toplevel"], ct, 10_000).ConfigureAwait(false);
        return top.Exit == 0 && top.Out.Trim().Length > 0 ? Path.GetFullPath(top.Out.Trim()) : null;
    }

    /// <summary>The changes <paramref name="request"/> asks for.</summary>
    /// <exception cref="DiffException">The path isn't in a repository, a commit or branch doesn't exist, or git failed.</exception>
    public static async Task<DiffResult> ChangesAsync(DiffRequest request, CancellationToken ct)
    {
        var full = Path.GetFullPath(request.Path);
        var dir = ExistingFolder(full) ?? throw new DiffException($"{full} doesn't exist.");
        var top = await RunAsync(dir, ["rev-parse", "--show-toplevel"], ct, 10_000).ConfigureAwait(false);
        if (top.Exit != 0 || top.Out.Trim().Length == 0)
            throw top.Err.Contains("not a git repository", StringComparison.OrdinalIgnoreCase)
                ? new DiffException($"{dir} is not in a git repository.", notRepository: true)
                : new DiffException(FirstLine(top.Err, $"git failed in {dir}."));
        var root = Path.GetFullPath(top.Out.Trim());
        var spec = Spec(root, full);
        // A value starting with - would be read as an option: --output=file writes a file.
        foreach (var rev in new[] { request.Against, request.Commit })
            if (rev is not null && (rev.TrimStart().StartsWith('-') || rev.Contains('\n')))
                throw new DiffException($"\"{rev}\" is not a commit, branch or range.");

        List<string> args = ["diff", "--no-color", "--no-ext-diff", "--no-textconv", "-M", $"-U{Math.Clamp(request.Context, 0, 50)}", "--src-prefix=a/", "--dst-prefix=b/"];
        string title;
        string? about = null;
        var onDisk = false;
        if (request.Commit is { } commitName)
        {
            var parents = await RunAsync(root, ["rev-list", "--parents", "-n", "1", commitName.Trim() + "^{commit}", "--"], ct, 10_000).ConfigureAwait(false);
            var ids = parents.Out.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parents.Exit != 0 || ids.Length == 0)
                throw new DiffException($"There is no commit \"{commitName}\" in {root}.");
            var log = await RunAsync(root, ["log", "-1", "--no-color", "--date=short", "--format=%h by %an, %ad%n%B", ids[0], "--"], ct, 8_000).ConfigureAwait(false);
            about = log.Out.Trim();
            if (about.Length > 1_500)
                about = about[..1_500] + " [...]";
            if (ids.Length > 2)
                about += "\n(A merge: shown against its first parent, so all it brought in.)";
            title = "Commit " + (about.Split(' ', 2)[0] is { Length: > 0 } shortId ? shortId : ids[0][..Math.Min(7, ids[0].Length)]);
            if (ids.Length > 1)
                args.AddRange([ids[1], ids[0]]);
            else
                args = ["show", "--format=", .. args[1..], ids[0]]; // the first commit: all of it is new
        }
        else
        {
            if (request.Staged)
                args.Add("--cached");
            if (request.Against is { } against)
                args.Add(against.Trim());
            else if (!request.Staged)
                // Against HEAD: on its own, git diff shows only what isn't staged yet. A repository
                // with no commit yet is compared with nothing.
                args.Add(await HasHeadAsync(root, ct).ConfigureAwait(false) ? "HEAD" : await EmptyTreeAsync(root, ct).ConfigureAwait(false));
            var range = request.Against?.Contains("..", StringComparison.Ordinal) == true;
            title = request.Staged ? "Staged changes" : request.Against is null ? "Uncommitted changes" : range ? $"Changes {request.Against.Trim()}" : $"Changes since {request.Against.Trim()}";
            onDisk = !request.Staged && !range;
        }
        args.Add("--");
        if (spec is not null)
            args.Add(spec);

        var patch = await RunAsync(root, args, ct).ConfigureAwait(false);
        if (patch.Exit != 0)
            throw new DiffException(FirstLine(patch.Err, $"git diff failed in {root}."));
        return new DiffResult(root, title, Parse(patch.Out))
        {
            About = about,
            Truncated = patch.Cut,
            NewIsOnDisk = onDisk,
            Untracked = onDisk ? await UntrackedAsync(root, spec, ct).ConfigureAwait(false) : [],
        };
    }

    private static async Task<bool> HasHeadAsync(string root, CancellationToken ct) =>
        (await RunAsync(root, ["rev-parse", "--verify", "--quiet", "HEAD^{commit}", "--"], ct, 1_000).ConfigureAwait(false)).Exit == 0;

    /// <summary>The id of the empty tree, as the repository's hash names it (SHA-1 or SHA-256).</summary>
    private static async Task<string> EmptyTreeAsync(string root, CancellationToken ct)
    {
        var tree = await RunAsync(root, ["hash-object", "-t", "tree", "--stdin"], ct, 1_000).ConfigureAwait(false);
        return tree.Exit == 0 && tree.Out.Trim().Length > 0 ? tree.Out.Trim() : "4b825dc642cb6eb9a060e54bf8d69288fbee4904";
    }

    /// <summary>The path as git reads it from the top of the repository; null for all of it.</summary>
    private static string? Spec(string root, string full)
    {
        var relative = Path.GetRelativePath(root, full);
        if (relative == ".")
            return null;
        // Another spelling of the same folder (a mapped drive, a link): git takes the full path.
        return relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative) ? full.Replace('\\', '/') : relative.Replace('\\', '/');
    }

    /// <summary>The path if it is a folder, the folder of a file, or the nearest folder above that exists (a deleted file's).</summary>
    private static string? ExistingFolder(string full)
    {
        if (Directory.Exists(full))
            return full;
        for (var dir = Path.GetDirectoryName(full); dir is not null; dir = Path.GetDirectoryName(dir))
            if (Directory.Exists(dir))
                return dir;
        return null;
    }

    private static string FirstLine(string text, string otherwise) =>
        text.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) is { } line ? line.Replace("fatal: ", "") : otherwise;

    private static async Task<IReadOnlyList<UntrackedFile>> UntrackedAsync(string root, string? spec, CancellationToken ct)
    {
        List<string> args = ["ls-files", "--others", "--exclude-standard", "-z", "--"];
        if (spec is not null)
            args.Add(spec);
        var listed = await RunAsync(root, args, ct, 200_000).ConfigureAwait(false);
        if (listed.Exit != 0)
            return [];
        return listed.Out.Split('\0', StringSplitOptions.RemoveEmptyEntries).Take(500).Select(p => new UntrackedFile(p, CountLines(Path.Combine(root, p)))).ToList();
    }

    /// <summary>A text file's lines; null for a binary one, or one over 2 MB.</summary>
    internal static long? CountLines(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > 2_000_000)
                return null;
            var bytes = File.ReadAllBytes(path);
            if (FolderDiff.IsBinary(bytes))
                return null;
            long lines = bytes.Count(b => b == (byte)'\n');
            return bytes.Length > 0 && bytes[^1] != (byte)'\n' ? lines + 1 : lines;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    internal sealed record Output(int Exit, string Out, string Err, bool Cut);

    /// <summary>Runs git in <paramref name="dir"/>, reading at most <paramref name="max"/> characters of what it prints.</summary>
    internal static async Task<Output> RunAsync(string dir, IEnumerable<string> args, CancellationToken ct, int max = MaxChars)
    {
        var psi = StartInfo(dir, args);
        psi.StandardOutputEncoding = new UTF8Encoding(false);
        using (var process = Start(psi))
        {
            process.StandardInput.Close();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(GitTime);
            var err = process.StandardError.ReadToEndAsync(timeout.Token);
            var output = new StringBuilder();
            var buffer = new char[16 * 1024];
            var cut = false;
            try
            {
                int n;
                while ((n = await process.StandardOutput.ReadAsync(buffer.AsMemory(), timeout.Token).ConfigureAwait(false)) > 0)
                {
                    if (output.Length + n > max)
                    {
                        output.Append(buffer, 0, max - output.Length);
                        cut = true;
                        Kill(process);
                        break;
                    }
                    output.Append(buffer, 0, n);
                }
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
                var errText = await err.ConfigureAwait(false);
                return new Output(cut ? 0 : process.ExitCode, output.ToString(), errText, cut);
            }
            catch (OperationCanceledException)
            {
                Kill(process);
                ct.ThrowIfCancellationRequested();
                throw new DiffException($"git took over {GitTime.TotalSeconds:0} seconds in {dir}.");
            }
        }
    }

    /// <summary>
    /// Runs git in <paramref name="dir"/> with <paramref name="input"/> on its standard input, reading
    /// what it prints as bytes, as they are: a file's content from the index keeps its byte order mark
    /// and line endings. Null when it printed more than <paramref name="max"/> bytes.
    /// </summary>
    internal static async Task<(int Exit, byte[]? Out, string Err)> RunBytesAsync(string dir, IEnumerable<string> args, byte[]? input, CancellationToken ct, int max = MaxChars)
    {
        using var process = Start(StartInfo(dir, args));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(GitTime);
        try
        {
            // Written while its output is read: either could fill a pipe and wait for the other.
            var write = Task.Run(async () =>
            {
                try
                {
                    if (input is not null)
                        await process.StandardInput.BaseStream.WriteAsync(input, timeout.Token).ConfigureAwait(false);
                }
                catch (IOException) { } // git stopped reading: what it says tells why
                finally
                {
                    process.StandardInput.Close();
                }
            }, timeout.Token);
            var err = process.StandardError.ReadToEndAsync(timeout.Token);
            using var output = new MemoryStream();
            var buffer = new byte[64 * 1024];
            int n;
            while ((n = await process.StandardOutput.BaseStream.ReadAsync(buffer, timeout.Token).ConfigureAwait(false)) > 0)
            {
                if (output.Length + n > max)
                {
                    Kill(process);
                    return (0, null, "");
                }
                output.Write(buffer, 0, n);
            }
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            await write.ConfigureAwait(false);
            return (process.ExitCode, output.ToArray(), await err.ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            Kill(process);
            ct.ThrowIfCancellationRequested();
            throw new DiffException($"git took over {GitTime.TotalSeconds:0} seconds in {dir}.");
        }
    }

    private static ProcessStartInfo StartInfo(string dir, IEnumerable<string> args)
    {
        var git = Git ?? throw new DiffException(NoGit);
        var psi = new ProcessStartInfo(git)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardErrorEncoding = new UTF8Encoding(false),
        };
        // Paths as they are (not "\303\251"), no colours, and paths taken literally (a [ or * in one).
        foreach (var a in (string[])["-C", dir, "-c", "core.quotepath=off", "-c", "color.ui=never", "--no-pager", "--literal-pathspecs"])
            psi.ArgumentList.Add(a);
        foreach (var a in args)
            psi.ArgumentList.Add(a);
        // No lock on the index for git to refresh it while reading, and never a password prompt.
        psi.Environment["GIT_OPTIONAL_LOCKS"] = "0";
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        return psi;
    }

    private static Process Start(ProcessStartInfo psi)
    {
        try
        {
            return Process.Start(psi) ?? throw new DiffException(NoGit);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            throw new DiffException(NoGit);
        }
    }

    private static void Kill(Process process)
    {
        try { process.Kill(entireProcessTree: true); }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
    }

    // ---- reading a patch ----------------------------------------------------------------------------

    /// <summary>The files of a patch git printed, with their hunks; its header lines (index, mode) are left out.</summary>
    internal static List<DiffFile> Parse(string patch)
    {
        var files = new List<DiffFile>();
        string? path = null, oldName = null, newName = null, renamedFrom = null, status = null;
        var binary = false;
        int added = 0, removed = 0, oldNo = 0, newNo = 0;
        List<DiffLine> lines = [];
        var inHunk = false;

        void Flush()
        {
            if (path is null)
                return;
            var name = status == "deleted" ? oldName ?? path : newName ?? path;
            files.Add(new DiffFile(name, status ?? "modified", lines)
            {
                OldPath = status is "renamed" or "copied" ? renamedFrom ?? oldName : null,
                Added = added,
                Removed = removed,
                Binary = binary,
            });
        }

        foreach (var raw in patch.Split('\n'))
        {
            var line = raw.EndsWith('\r') ? raw[..^1] : raw;
            if (line.StartsWith("diff --git ", StringComparison.Ordinal))
            {
                Flush();
                path = FromDiffLine(line["diff --git ".Length..]);
                oldName = newName = renamedFrom = status = null;
                binary = inHunk = false;
                added = removed = 0;
                lines = [];
                continue;
            }
            if (path is null)
                continue;
            if (!inHunk)
            {
                if (line.StartsWith("new file mode", StringComparison.Ordinal)) status = "added";
                else if (line.StartsWith("deleted file mode", StringComparison.Ordinal)) status = "deleted";
                else if (line.StartsWith("rename from ", StringComparison.Ordinal)) (status, renamedFrom) = ("renamed", Unquote(line[12..]));
                else if (line.StartsWith("rename to ", StringComparison.Ordinal)) newName = Unquote(line[10..]);
                else if (line.StartsWith("copy from ", StringComparison.Ordinal)) (status, renamedFrom) = ("copied", Unquote(line[10..]));
                else if (line.StartsWith("copy to ", StringComparison.Ordinal)) newName = Unquote(line[8..]);
                else if (line.StartsWith("Binary files ", StringComparison.Ordinal)) binary = true;
                else if (line.StartsWith("--- ", StringComparison.Ordinal)) oldName = Side(line[4..], "a/") ?? oldName;
                else if (line.StartsWith("+++ ", StringComparison.Ordinal)) newName = Side(line[4..], "b/") ?? newName;
                if (!line.StartsWith("@@", StringComparison.Ordinal))
                    continue;
            }
            if (line.StartsWith("@@", StringComparison.Ordinal) && HunkHeader().Match(line) is { Success: true } m)
            {
                inHunk = true;
                oldNo = int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
                newNo = int.Parse(m.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture);
                lines.Add(new DiffLine(DiffLineKind.Hunk, null, null, DiffLine.Cut(line)));
                continue;
            }
            if (line.Length == 0)
                continue; // only after the last line
            var text = DiffLine.Cut(line[1..]);
            switch (line[0])
            {
                case '+':
                    lines.Add(new DiffLine(DiffLineKind.Added, null, newNo++, text));
                    added++;
                    break;
                case '-':
                    lines.Add(new DiffLine(DiffLineKind.Removed, oldNo++, null, text));
                    removed++;
                    break;
                case ' ':
                    lines.Add(new DiffLine(DiffLineKind.Context, oldNo++, newNo++, text));
                    break;
                case '\\':
                    lines.Add(new DiffLine(DiffLineKind.Note, null, null, text.Trim()));
                    break;
            }
        }
        Flush();
        return files;
    }

    [GeneratedRegex(@"^@@ -(\d+)(?:,\d+)? \+(\d+)(?:,\d+)? @@")]
    private static partial Regex HunkHeader();

    /// <summary>A name of "--- a/x" or "+++ b/x" without its prefix; null for /dev/null.</summary>
    private static string? Side(string name, string prefix)
    {
        name = Unquote(name.TrimEnd('\t'));
        if (name == "/dev/null")
            return null;
        return name.StartsWith(prefix, StringComparison.Ordinal) ? name[prefix.Length..] : name;
    }

    /// <summary>
    /// The path of "diff --git a/x b/x": both halves are the same file but for a rename, whose
    /// names come on lines of their own. A path with a space in it can't be split at a space.
    /// </summary>
    private static string FromDiffLine(string rest)
    {
        if (rest.StartsWith('"'))
        {
            var end = rest.IndexOf("\" ", 1, StringComparison.Ordinal);
            var first = Unquote(end > 0 ? rest[..(end + 1)] : rest);
            return first.StartsWith("a/", StringComparison.Ordinal) ? first[2..] : first;
        }
        if (rest.Length % 2 == 1 && rest.StartsWith("a/", StringComparison.Ordinal))
        {
            var half = (rest.Length - 1) / 2;
            var name = rest[2..half];
            if (rest[(half + 1)..] == "b/" + name)
                return name;
        }
        var split = rest.IndexOf(" b/", StringComparison.Ordinal);
        var a = split > 0 ? rest[..split] : rest;
        return a.StartsWith("a/", StringComparison.Ordinal) ? a[2..] : a;
    }

    /// <summary>A path as git writes one with odd characters in it: "a/tab\there.txt", in quotes, with C escapes.</summary>
    internal static string Unquote(string name)
    {
        if (name.Length < 2 || name[0] != '"' || name[^1] != '"')
            return name;
        var bytes = new List<byte>();
        for (var i = 1; i < name.Length - 1; i++)
        {
            var c = name[i];
            if (c != '\\' || i + 1 >= name.Length - 1)
            {
                bytes.AddRange(Encoding.UTF8.GetBytes(c.ToString()));
                continue;
            }
            var next = name[++i];
            if (next is >= '0' and <= '7' && i + 2 < name.Length - 1)
            {
                bytes.Add(Convert.ToByte(name.Substring(i, 3), 8));
                i += 2;
                continue;
            }
            bytes.Add(next switch { 'n' => (byte)'\n', 't' => (byte)'\t', 'r' => (byte)'\r', 'a' => 7, 'b' => 8, 'f' => 12, 'v' => 11, _ => (byte)next });
        }
        return Encoding.UTF8.GetString(bytes.ToArray());
    }
}
