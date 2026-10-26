using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using BlackBox.Core.Diffs;
using BlackBox.Core.Settings;
using BlackBox.Core.Tools;
using Xunit;

namespace BlackBox.Core.Tests;

// File changes' git side: the Not committed and Not staged views, staging a file or a hunk, and
// Discard putting picked lines or a whole file back as they were in the view shown.
public class GitChangesTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bb-gitchanges-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly string _repo;
    private readonly FileChanges _changes = new();

    public GitChangesTests()
    {
        _repo = Path.Combine(_root, "repo");
        Directory.CreateDirectory(_repo);
        Git("init", "-q", "-b", "main");
        Git("config", "core.autocrlf", "false");
    }

    public void Dispose()
    {
        foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(_root, recursive: true);
    }

    private string Git(params string[] args)
    {
        var psi = new ProcessStartInfo(GitDiff.Git!) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true, StandardOutputEncoding = new UTF8Encoding(false) };
        psi.ArgumentList.Add("-C");
        psi.ArgumentList.Add(_repo);
        foreach (var a in args)
            psi.ArgumentList.Add(a);
        foreach (var (k, v) in new[] { ("GIT_AUTHOR_NAME", "Tester"), ("GIT_AUTHOR_EMAIL", "t@example.com"), ("GIT_COMMITTER_NAME", "Tester"), ("GIT_COMMITTER_EMAIL", "t@example.com") })
            psi.Environment[k] = v;
        using var p = Process.Start(psi)!;
        var err = p.StandardError.ReadToEndAsync();
        var output = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        Assert.True(p.ExitCode == 0, $"git {string.Join(' ', args)}: {err.Result}");
        return output;
    }

    private static string Numbered(int count, string lineBreak = "\n") => string.Concat(Enumerable.Range(1, count).Select(i => $"line {i}{lineBreak}"));

    /// <summary>The agent writes the file, as write_file does: it is read first.</summary>
    private async Task Write(string path, string content)
    {
        var ctx = new ToolContext { Platform = new FakePlatform(), Settings = new AgentSettings(), WorkingDirectory = _repo, Changes = _changes };
        var args = new JsonObject { ["path"] = path, ["content"] = content };
        var call = _changes.Starting(new WriteFileTool(), args, ctx);
        var result = await new WriteFileTool().ExecuteAsync(args, ctx, CancellationToken.None);
        Assert.False(result.IsError, result.Text);
        await call!.EndAsync();
    }

    /// <summary>The files of one section of the panel (null: outside git).</summary>
    private async Task<List<DiffFile>> In(GitSection? section) =>
        (await _changes.DiffAsync(CancellationToken.None)).Files.Where(f => f.Git?.Section == section).ToList();
    /// <summary>The changes of each hunk, as the panel's Stage button gives them.</summary>
    private static List<List<(int, int)>> HunkSpans(DiffFile file)
    {
        var spans = new List<List<(int, int)>>();
        for (var i = 0; i < file.Lines.Count; i++)
        {
            if (file.Lines[i].Kind != DiffLineKind.Hunk)
                continue;
            var hunk = new List<(int, int)>();
            for (var j = i + 1; j < file.Lines.Count && file.Lines[j].Kind != DiffLineKind.Hunk; j++)
                if (file.Lines[j].Kind == DiffLineKind.Added)
                    hunk.Add((file.Lines[j].New!.Value, file.Lines[j].New!.Value));
                else if (file.Lines[j].Kind == DiffLineKind.Removed && file.Lines[j - 1].Kind != DiffLineKind.Removed)
                    hunk.Add(file.Lines.Skip(j).FirstOrDefault(l => l.New is not null) is { New: { } n } ? (n, n - 1) : (1, 0));
            spans.Add(hunk);
        }
        return spans;
    }

    [Fact]
    public async Task A_hunk_is_staged_and_unstaged_alone_and_the_sections_follow()
    {
        var path = Path.Combine(_repo, "a.txt");
        File.WriteAllText(path, Numbered(30));
        Git("add", "-A");
        Git("commit", "-q", "-m", "First");

        await Write(path, Numbered(30).Replace("line 2\n", "line two\n").Replace("line 25\n", "line 25\nline 25b\n"));

        var unstaged = Assert.Single(await In(GitSection.Unstaged));
        Assert.Empty(await In(GitSection.Staged));
        Assert.Empty(await In(GitSection.Committed));
        Assert.True(unstaged.Git!.Splittable);
        var hunks = HunkSpans(unstaged);
        Assert.Equal(2, hunks.Count);

        Assert.Null(await GitChanges.StageAsync(path, hunks[0], CancellationToken.None));

        Assert.Equal(Numbered(30).Replace("line 2\n", "line two\n"), Git("show", ":a.txt"));
        var staged = Assert.Single(await In(GitSection.Staged));
        Assert.Equal((1, 1), (staged.Added, staged.Removed));
        unstaged = Assert.Single(await In(GitSection.Unstaged));
        Assert.Equal((1, 0), (unstaged.Added, unstaged.Removed));
        Assert.Contains(new DiffLine(DiffLineKind.Added, null, 26, "line 25b"), unstaged.Lines);

        // Both staged, then the first unstaged again: the second stays staged, the file on disk as it is.
        Assert.Null(await GitChanges.StageAsync(path, null, CancellationToken.None));
        Assert.Empty(await In(GitSection.Unstaged));
        Assert.Null(await GitChanges.UnstageAsync(path, HunkSpans(Assert.Single(await In(GitSection.Staged)))[0], CancellationToken.None));
        Assert.Equal(Numbered(30).Replace("line 25\n", "line 25\nline 25b\n"), Git("show", ":a.txt"));
        Assert.Equal(Numbered(30).Replace("line 2\n", "line two\n").Replace("line 25\n", "line 25\nline 25b\n"), File.ReadAllText(path));
        Assert.Contains(new DiffLine(DiffLineKind.Added, null, 2, "line two"), Assert.Single(await In(GitSection.Unstaged)).Lines);

        // Committed, the agent's change shows there, against what the file held before it.
        Assert.Null(await GitChanges.StageAsync(path, null, CancellationToken.None));
        Git("commit", "-q", "-m", "Second");
        var committed = Assert.Single(await In(GitSection.Committed));
        Assert.Equal((2, 1), (committed.Added, committed.Removed));
        Assert.Empty(await In(GitSection.Staged));
        Assert.Empty(await In(GitSection.Unstaged));
    }

    [Fact]
    public async Task Staging_lines_keeps_the_files_line_breaks_and_its_last_line()
    {
        var path = Path.Combine(_repo, "crlf.txt");
        File.WriteAllText(path, "one\r\ntwo\r\nthree");
        Git("add", "-A");
        Git("commit", "-q", "-m", "First");

        await Write(path, "ONE\r\ntwo\r\nthree\r\nfour\r\n");
        var file = Assert.Single(await In(GitSection.Unstaged));
        // One hunk; stage only the change at the end, by its lines.
        Assert.Null(await GitChanges.StageAsync(path, [(3, 4)], CancellationToken.None));

        Assert.Equal("one\r\ntwo\r\nthree\r\nfour\r\n", Git("show", ":crlf.txt"));
        Assert.Single(file.Lines, l => l.Kind == DiffLineKind.Hunk);
    }

    [Fact]
    public async Task A_new_file_moves_between_unstaged_and_staged_and_one_outside_git_has_no_section()
    {
        Git("commit", "-q", "--allow-empty", "-m", "First");
        var made = Path.Combine(_repo, "new.txt");
        var outside = Path.Combine(_root, "outside.txt");
        await Write(made, "a\nb\n");
        await Write(outside, "x\n");

        var diff = await _changes.DiffAsync(CancellationToken.None);
        Assert.True(diff.InGit);
        Assert.Equal([GitSection.Unstaged, null], diff.Files.Select(f => f.Git?.Section));
        var unstaged = Assert.Single(await In(GitSection.Unstaged));
        Assert.Equal(("added", 2), (unstaged.Status, unstaged.Added));

        Assert.Null(await GitChanges.StageAsync(made, HunkSpans(unstaged)[0], CancellationToken.None));
        Assert.Empty(await In(GitSection.Unstaged));
        Assert.Equal("added", Assert.Single(await In(GitSection.Staged)).Status);

        Assert.Null(await GitChanges.UnstageAsync(made, null, CancellationToken.None));
        Assert.Empty(await In(GitSection.Staged));
        Assert.Single(await In(GitSection.Unstaged));
        Assert.True(File.Exists(made));
    }

    [Fact]
    public async Task Discard_puts_back_unstaged_changes_as_staged_and_files_outside_git_as_before()
    {
        var path = Path.Combine(_repo, "d.txt");
        File.WriteAllText(path, "one\ntwo\nthree\n");
        Git("add", "-A");
        Git("commit", "-q", "-m", "First");
        await Write(path, "one\nTWO\nthree\nfour\n");
        var deleted = new List<string>();

        // Unstaged lines: the removed "two" back, the added "four" gone; the added "TWO" stays.
        Assert.Null(await _changes.DiscardAsync(GitSection.Unstaged, path, [(2, null), (null, 4)], deleted.Add, CancellationToken.None));
        Assert.Equal("one\ntwo\nTWO\nthree\n", File.ReadAllText(path));
        // Staged changes aren't discarded.
        Assert.NotNull(await _changes.DiscardAsync(GitSection.Staged, path, null, deleted.Add, CancellationToken.None));
        // The whole file: back as staged.
        Assert.Null(await _changes.DiscardAsync(GitSection.Unstaged, path, null, deleted.Add, CancellationToken.None));
        Assert.Equal("one\ntwo\nthree\n", File.ReadAllText(path));

        // Outside git, as before the agent's change; a file the agent made goes to the delete it is given.
        var outside = Path.Combine(_root, "o.txt");
        File.WriteAllText(outside, "a\n");
        await Write(outside, "a\nb\n");
        Assert.Null(await _changes.DiscardAsync(null, outside, [(null, 2)], deleted.Add, CancellationToken.None));
        Assert.Equal("a\n", File.ReadAllText(outside));
        var made = Path.Combine(_repo, "made.txt");
        await Write(made, "new\n");
        Assert.Null(await _changes.DiscardAsync(GitSection.Unstaged, made, null, deleted.Add, CancellationToken.None));
        Assert.Equal([made], deleted);
    }
    [Fact]
    public void Lines_keep_their_breaks_and_a_last_line_without_one_gets_one_when_it_is_no_longer_last()
    {
        Assert.Equal(["a\r\n", "b\n", "c"], GitChanges.Split("a\r\nb\nc"));
        Assert.Equal("a\nb\n", GitChanges.Join(["a", "b\n"], "\n"));
        var index = GitChanges.Split("x");
        var now = GitChanges.Split("x\r\ny\r\n");
        Assert.Equal("x\r\ny\r\n", GitChanges.Apply(index, now, [new ChangeBlock(1, 0, 1, 1)]));
        // A place between lines touches the lines on both sides of it, and nothing further.
        Assert.True(GitChanges.Touch((5, 4), new ChangeBlock(3, 1, 3, 1)));
        Assert.True(GitChanges.Touch((5, 4), new ChangeBlock(4, 1, 4, 1)));
        Assert.False(GitChanges.Touch((5, 4), new ChangeBlock(5, 1, 5, 1)));
        Assert.False(GitChanges.Touch((2, 2), new ChangeBlock(2, 0, 3, 1)));
    }
}
