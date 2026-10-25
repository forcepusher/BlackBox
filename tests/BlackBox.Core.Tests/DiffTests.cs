using System.Diagnostics;
using System.Text.Json.Nodes;
using BlackBox.Core.Agent;
using BlackBox.Core.Diffs;
using BlackBox.Core.Settings;
using BlackBox.Core.Tools;
using Xunit;

namespace BlackBox.Core.Tests;

// The diff tool and what the File changes panel reads: git's changes parsed, two files or folders compared
// without git, and the model's view of them, summary first and in parts.
public class DiffTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bb-diff-" + Guid.NewGuid().ToString("N")[..8]);

    public DiffTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        // git keeps its objects read-only, which Directory.Delete refuses.
        foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(_root, recursive: true);
    }

    private static ToolContext Ctx(string dir) => new() { Platform = new FakePlatform(), Settings = new AgentSettings(), WorkingDirectory = dir };

    private static void Git(string dir, params string[] args)
    {
        var psi = new ProcessStartInfo(GitDiff.Git!) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        psi.ArgumentList.Add("-C");
        psi.ArgumentList.Add(dir);
        foreach (var a in args)
            psi.ArgumentList.Add(a);
        foreach (var (k, v) in new[] { ("GIT_AUTHOR_NAME", "Tester"), ("GIT_AUTHOR_EMAIL", "t@example.com"), ("GIT_COMMITTER_NAME", "Tester"), ("GIT_COMMITTER_EMAIL", "t@example.com") })
            psi.Environment[k] = v;
        using var p = Process.Start(psi)!;
        var err = p.StandardError.ReadToEnd();
        p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        Assert.True(p.ExitCode == 0, $"git {string.Join(' ', args)}: {err}");
    }

    /// <summary>A repository with two commits: a.txt made, then changed and b.txt added.</summary>
    private string Repo()
    {
        var repo = Path.Combine(_root, "repo");
        Directory.CreateDirectory(Path.Combine(repo, "src"));
        Git(repo, "init", "-q", "-b", "main");
        Git(repo, "config", "core.autocrlf", "false");
        File.WriteAllText(Path.Combine(repo, "a.txt"), "one\ntwo\nthree\nfour\nfive\n");
        File.WriteAllText(Path.Combine(repo, "src", "gone.txt"), "bye\n");
        Git(repo, "add", "-A");
        Git(repo, "commit", "-q", "-m", "First");
        File.WriteAllText(Path.Combine(repo, "a.txt"), "one\nTWO\nthree\nfour\nfive\nsix\n");
        File.WriteAllText(Path.Combine(repo, "src", "b.txt"), "new file\n");
        Git(repo, "add", "-A");
        Git(repo, "commit", "-q", "-m", "Second: TWO and six\n\nWith a body.");
        return repo;
    }

    [Fact]
    public void Two_texts_differ_by_the_least_edits_in_hunks_with_context()
    {
        var hunks = LineDiff.Hunks(["a", "b", "c", "d", "e", "f", "g", "h", "i"], ["a", "B", "c", "d", "e", "f", "g", "h", "i", "j"], context: 1);
        Assert.Equal(["@@ -1,3 +1,3 @@", " a", "-b", "+B", " c", "@@ -9 +9,2 @@", " i", "+j"], hunks.Select(l => l.Unified));
        // Numbered on both sides: the added line is line 10 of the new text.
        Assert.Equal((null, 10), (hunks[^1].Old, hunks[^1].New));
        Assert.Empty(LineDiff.Hunks(["x"], ["x"], 3));
        // Everything new: one hunk from nothing.
        Assert.Equal(["@@ -0,0 +1,2 @@", "+x", "+y"], LineDiff.Hunks([], ["x", "y"], 3).Select(l => l.Unified));
        // Line endings don't make a difference.
        Assert.Equal(LineDiff.Lines("p\nq\n"), LineDiff.Lines("p\r\nq"));
    }

    [Fact]
    public void A_git_patch_is_read_file_by_file_with_numbers_renames_and_binaries()
    {
        const string patch = """
            diff --git a/my file.txt b/my file.txt
            index 1111111..2222222 100644
            --- a/my file.txt
            +++ b/my file.txt
            @@ -10,3 +10,4 @@ class Foo
             keep
            --- removed line that looks like a header
            +added
            +also
             end
            \ No newline at end of file
            diff --git a/old.cs b/new.cs
            similarity index 90%
            rename from old.cs
            rename to new.cs
            --- a/old.cs
            +++ b/new.cs
            @@ -1 +1 @@
            -x
            +y
            diff --git a/pic.png b/pic.png
            new file mode 100644
            Binary files /dev/null and b/pic.png differ
            diff --git a/gone.txt b/gone.txt
            deleted file mode 100644
            --- a/gone.txt
            +++ /dev/null
            @@ -1 +0,0 @@
            -bye
            diff --git "a/tab\there.txt" "b/tab\there.txt"
            --- "a/tab\there.txt"
            +++ "b/tab\there.txt"
            @@ -1 +1 @@
            -a
            +b
            """;
        var files = GitDiff.Parse(patch.ReplaceLineEndings("\r\n"));
        Assert.Equal(["my file.txt", "new.cs", "pic.png", "gone.txt", "tab\there.txt"], files.Select(f => f.Path));
        Assert.Equal(["modified", "renamed", "added", "deleted", "modified"], files.Select(f => f.Status));
        var first = files[0];
        Assert.Equal((2, 1), (first.Added, first.Removed));
        Assert.Equal("-- removed line that looks like a header", first.Lines[2].Text); // inside a hunk, not a header
        Assert.Equal((11, (int?)null), (first.Lines[2].Old, first.Lines[2].New));
        Assert.Equal(12, first.Lines[4].New);
        Assert.Equal((12, 13), (first.Lines[5].Old, first.Lines[5].New));
        Assert.Equal(DiffLineKind.Note, first.Lines[6].Kind);
        Assert.Equal("old.cs -> new.cs", files[1].Display);
        Assert.True(files[2].Binary);
        Assert.Equal("binary", files[2].Counts);
        Assert.Equal("-1", files[3].Counts);
    }

    [Fact]
    public async Task Uncommitted_changes_staged_ones_and_a_commit_come_from_git()
    {
        if (GitDiff.Git is null)
            return;
        var repo = Repo();
        File.WriteAllText(Path.Combine(repo, "a.txt"), "one\nTWO\nthree\nfour\nfive\nsix\nseven\n");
        File.WriteAllText(Path.Combine(repo, "notes.md"), "# Notes\nline\n");
        File.Delete(Path.Combine(repo, "src", "b.txt"));
        Git(repo, "add", "src/b.txt");

        var all = await GitDiff.ChangesAsync(new DiffRequest(repo), default);
        Assert.Equal("Uncommitted changes", all.Title);
        Assert.Equal(Path.GetFullPath(repo), all.Root, ignoreCase: true);
        Assert.Equal(["a.txt", "src/b.txt"], all.Files.Select(f => f.Path));
        Assert.Equal(["modified", "deleted"], all.Files.Select(f => f.Status));
        Assert.Equal(new UntrackedFile("notes.md", 2), Assert.Single(all.Untracked));
        Assert.True(all.NewIsOnDisk);
        Assert.Equal(7, all.Files[0].Lines.Single(l => l.Kind == DiffLineKind.Added).New);

        var staged = await GitDiff.ChangesAsync(new DiffRequest(repo, Staged: true), default);
        Assert.Equal("src/b.txt", Assert.Single(staged.Files).Path);
        Assert.False(staged.NewIsOnDisk);

        // A file's path narrows it to that file; a deleted one's too.
        Assert.Equal("src/b.txt", Assert.Single((await GitDiff.ChangesAsync(new DiffRequest(Path.Combine(repo, "src", "b.txt")), default)).Files).Path);

        var commit = await GitDiff.ChangesAsync(new DiffRequest(repo, Commit: "HEAD"), default);
        Assert.StartsWith("Commit ", commit.Title);
        Assert.Contains("by Tester", commit.About);
        Assert.Contains("Second: TWO and six\n\nWith a body.", commit.About);
        Assert.Equal(["a.txt", "src/b.txt"], commit.Files.Select(f => f.Path));
        Assert.Equal((2, 1), (commit.Files[0].Added, commit.Files[0].Removed));
        // The first commit has no parent: all of it is new.
        var first = await GitDiff.ChangesAsync(new DiffRequest(repo, Commit: "HEAD~1"), default);
        Assert.All(first.Files, f => Assert.Equal("added", f.Status));

        var since = await GitDiff.ChangesAsync(new DiffRequest(repo, Against: "HEAD~1"), default);
        Assert.Equal("Changes since HEAD~1", since.Title);
        Assert.Equal("a.txt", Assert.Single(since.Files).Path); // b.txt came after it and is gone again

        await Assert.ThrowsAsync<DiffException>(() => GitDiff.ChangesAsync(new DiffRequest(repo, Against: "--output=x.txt"), default));
        Assert.False(File.Exists(Path.Combine(repo, "x.txt")));
        var none = await Assert.ThrowsAsync<DiffException>(() => GitDiff.ChangesAsync(new DiffRequest(repo, Commit: "nosuch"), default));
        Assert.Contains("no commit \"nosuch\"", none.Message);
        var outside = await Assert.ThrowsAsync<DiffException>(() => GitDiff.ChangesAsync(new DiffRequest(_root), default));
        Assert.True(outside.NotRepository);

        // A repository with nothing committed yet: what is added is all new.
        var fresh = Path.Combine(_root, "fresh");
        Directory.CreateDirectory(fresh);
        Git(fresh, "init", "-q");
        File.WriteAllText(Path.Combine(fresh, "first.txt"), "hello\n");
        Git(fresh, "add", "first.txt");
        var added = Assert.Single((await GitDiff.ChangesAsync(new DiffRequest(fresh), default)).Files);
        Assert.Equal(("first.txt", "added", 1), (added.Path, added.Status, added.Added));
    }

    [Fact]
    public async Task The_tool_lists_the_files_first_and_gives_a_long_diff_in_parts()
    {
        if (GitDiff.Git is null)
            return;
        var repo = Repo();
        File.WriteAllText(Path.Combine(repo, "a.txt"), "one\nTWO\nthree\n4\nfive\nsix\n");
        File.WriteAllText(Path.Combine(repo, "big.txt"), "");
        Git(repo, "add", "big.txt");
        Git(repo, "commit", "-q", "-m", "Big");
        File.WriteAllText(Path.Combine(repo, "big.txt"), string.Concat(Enumerable.Range(1, 2_000).Select(i => $"line number {i} of the big file\n")));

        var tool = new DiffTool();
        var result = await tool.ExecuteAsync(new JsonObject { ["path"] = repo }, Ctx(_root), default);
        Assert.False(result.IsError, result.Text);
        var lines = result.Text.Split('\n');
        Assert.Equal($"Uncommitted changes in {Path.GetFullPath(repo)}: 2 files changed, 2001 lines added, 1 removed", lines[0]);
        Assert.Equal("  modified a.txt (+1 -1)", lines[1]);
        Assert.Equal("  modified big.txt (+2000)", lines[2]);
        Assert.Contains("\n=== a.txt (modified)\n@@ -1,6 +1,6 @@\n one\n TWO\n three\n-four\n+4\n", result.Text);
        Assert.True(result.Text.Length <= DiffTool.PartSize + 200);
        var more = System.Text.RegularExpressions.Regex.Match(result.Text, @"diff with start=(\d+) reads on");
        Assert.True(more.Success, result.Text[^300..]);

        var next = await tool.ExecuteAsync(new JsonObject { ["path"] = repo, ["start"] = int.Parse(more.Groups[1].Value) }, Ctx(_root), default);
        Assert.StartsWith("(characters ", next.Text);
        Assert.Contains("going on in big.txt (modified)", next.Text.Split('\n')[0]);
        Assert.StartsWith("+line number", next.Text.Split('\n')[1]);

        var summary = await tool.ExecuteAsync(new JsonObject { ["path"] = repo, ["summary"] = true }, Ctx(_root), default);
        Assert.Equal(3, summary.Text.Split('\n').Length);
        var commit = await tool.ExecuteAsync(new JsonObject { ["path"] = repo, ["commit"] = "HEAD~1" }, Ctx(_root), default);
        Assert.Contains("Second: TWO and six", commit.Text);

        var outside = await tool.ExecuteAsync(new JsonObject { ["path"] = _root }, Ctx(_root), default);
        Assert.True(outside.IsError);
        Assert.Contains("give the second one as other", outside.Text);
    }

    [Fact]
    public async Task Two_folders_are_compared_file_by_file_without_git()
    {
        var (before, after) = (Path.Combine(_root, "v1"), Path.Combine(_root, "v2"));
        foreach (var d in new[] { before, after })
            Directory.CreateDirectory(Path.Combine(d, "sub"));
        File.WriteAllText(Path.Combine(before, "same.txt"), "same\n");
        File.WriteAllText(Path.Combine(after, "same.txt"), "same\n");
        File.WriteAllText(Path.Combine(before, "sub", "changed.txt"), "a\nb\nc\n");
        File.WriteAllText(Path.Combine(after, "sub", "changed.txt"), "a\r\nB\r\nc\r\n");
        File.WriteAllText(Path.Combine(before, "old.txt"), "old\n");
        File.WriteAllText(Path.Combine(after, "new.txt"), "new\nnewer\n");
        File.WriteAllBytes(Path.Combine(before, "pic.bin"), [1, 0, 2]);
        File.WriteAllBytes(Path.Combine(after, "pic.bin"), [1, 0, 3]);

        var tool = new DiffTool();
        var result = await tool.ExecuteAsync(new JsonObject { ["path"] = before, ["other"] = after }, Ctx(_root), default);
        Assert.False(result.IsError, result.Text);
        var lines = result.Text.Split('\n');
        Assert.Equal($"How {after} differs from {before}: 4 files changed, 3 lines added, 2 removed", lines[0]);
        Assert.Equal(["  added new.txt (+2)", "  deleted old.txt (-1)", "  modified pic.bin (binary)", "  modified sub/changed.txt (+1 -1)"], lines[1..5]);
        Assert.Contains("=== sub/changed.txt (modified)\n@@ -1,3 +1,3 @@\n a\n-b\n+B\n c", result.Text);

        // Two files: only the encoding differs.
        File.WriteAllText(Path.Combine(_root, "x1.txt"), "same\n");
        File.WriteAllText(Path.Combine(_root, "x2.txt"), "same\r\n");
        var endings = await tool.ExecuteAsync(new JsonObject { ["path"] = Path.Combine(_root, "x1.txt"), ["other"] = Path.Combine(_root, "x2.txt") }, Ctx(_root), default);
        Assert.Contains("Only the encoding or the line endings differ.", endings.Text);
    }

    [Fact]
    public void The_model_has_the_diff_tool_and_its_usual_misnames()
    {
        var registry = ToolRegistry.CreateDefault(includeScreenTools: false);
        Assert.Equal(ToolRisk.ReadOnly, registry.Find("diff")!.Risk);
        Assert.IsType<DiffTool>(ToolCallRepair.ResolveTool("git_diff", registry));
        var args = ToolCallRepair.NormalizeArguments(new JsonObject { ["file"] = "a.txt", ["compare_to"] = "b.txt", ["sha"] = "HEAD" }, registry.Find("diff")!);
        Assert.Equal(("a.txt", "b.txt", "HEAD"), (args["path"]?.GetValue<string>(), args["other"]?.GetValue<string>(), args["commit"]?.GetValue<string>()));
        Assert.Contains("use diff", SystemPrompt.Build("Test OS", null, "C:\\", ""));
    }
}
