using System.Diagnostics;
using System.Text.Json.Nodes;
using BlackBox.Core.Agent;
using BlackBox.Core.Diffs;
using BlackBox.Core.Llm;
using BlackBox.Core.Settings;
using BlackBox.Core.Tools;
using Xunit;

namespace BlackBox.Core.Tests;

// What the File changes panel shows: the files the agent changed in a chat, each against what it held
// before the first change, from write_file and edit_file and from what a command changed.
public class FileChangesTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bb-changes-" + Guid.NewGuid().ToString("N")[..8]);

    public FileChangesTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        // git keeps its objects read-only, which Directory.Delete refuses.
        foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(_root, recursive: true);
    }

    private ToolContext Ctx(FileChanges changes) => new() { Platform = new FakePlatform(), Settings = new AgentSettings(), WorkingDirectory = _root, Changes = changes };

    /// <summary>Runs a tool as the agent does: the file it writes read first, the folder a command runs in watched.</summary>
    private static async Task<ToolResult> Run(Tool tool, JsonObject args, ToolContext ctx)
    {
        var call = ctx.Changes!.Starting(tool, args, ctx);
        try { return await tool.ExecuteAsync(args, ctx, CancellationToken.None); }
        finally { if (call is not null) await call.EndAsync(); }
    }

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

    [Fact]
    public async Task Written_and_edited_files_show_against_what_they_held_before_the_first_change()
    {
        var changes = new FileChanges();
        var ctx = Ctx(changes);
        var old = Path.Combine(_root, "src", "old.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(old)!);
        File.WriteAllText(old, "one\ntwo\nthree\n");
        var made = Path.Combine(_root, "made.txt");

        await Run(new WriteFileTool(), new JsonObject { ["path"] = made, ["content"] = "a\nb\n" }, ctx);
        await Run(new EditFileTool(), new JsonObject { ["path"] = old, ["old_text"] = "two", ["new_text"] = "TWO" }, ctx);
        await Run(new EditFileTool(), new JsonObject { ["path"] = old, ["old_text"] = "three", ["new_text"] = "THREE" }, ctx);

        var diff = changes.Diff();
        Assert.Equal(_root, diff.Root);
        Assert.Equal(["made.txt", "src/old.txt"], diff.Files.Select(f => f.Path));
        Assert.Equal("added", diff.Files[0].Status);
        Assert.Equal(2, diff.Files[0].Added);
        var edited = diff.Files[1];
        Assert.Equal(("modified", 2, 2), (edited.Status, edited.Added, edited.Removed));
        Assert.Contains(new DiffLine(DiffLineKind.Removed, 2, null, "two"), edited.Lines);
        Assert.Contains(new DiffLine(DiffLineKind.Added, null, 3, "THREE"), edited.Lines);
        Assert.True(diff.NewIsOnDisk);
        Assert.Equal(made, diff.FullPath(diff.Files[0]));

        // Put back as it was, a file leaves the list; deleted, it shows as deleted.
        await Run(new EditFileTool(), new JsonObject { ["path"] = old, ["old_text"] = "TWO\nTHREE", ["new_text"] = "two\nthree" }, ctx);
        File.Delete(made);
        Assert.Empty(changes.Diff().Files); // and made, then deleted again: nothing to show
    }

    [Fact]
    public async Task What_a_command_changes_shows_and_its_builds_and_ignored_files_do_not()
    {
        var repo = Path.Combine(_root, "repo");
        Directory.CreateDirectory(repo);
        Git(repo, "init", "-q", "-b", "main");
        Git(repo, "config", "core.autocrlf", "false");
        File.WriteAllText(Path.Combine(repo, "kept.txt"), "one\ntwo\n");
        File.WriteAllText(Path.Combine(repo, ".gitignore"), "*.log\n");
        Git(repo, "add", "-A");
        Git(repo, "commit", "-q", "-m", "First");
        var changes = new FileChanges();
        var ctx = Ctx(changes);

        var result = await Run(new ShellTool(), new JsonObject
        {
            ["command"] = "echo three>> kept.txt & echo new> new.txt & echo noise> run.log & mkdir bin & echo built> bin\\out.txt",
            ["working_directory"] = repo,
        }, ctx);

        Assert.False(result.IsError, result.Text);
        var diff = changes.Diff();
        Assert.Equal(["kept.txt", "new.txt"], diff.Files.Select(f => f.Path).Order());
        var kept = diff.Files.Single(f => f.Path == "kept.txt");
        // What git had of it, not a note that it isn't known.
        Assert.Equal(("modified", 1, 0), (kept.Status, kept.Added, kept.Removed));
        Assert.DoesNotContain(kept.Lines, l => l.Kind == DiffLineKind.Note);
        Assert.Equal("added", diff.Files.Single(f => f.Path == "new.txt").Status);
    }

    [Fact]
    public async Task A_file_a_command_changed_outside_git_shows_from_that_command_on()
    {
        var file = Path.Combine(_root, "notes.txt");
        File.WriteAllText(file, "one\r\n");
        var changes = new FileChanges();
        var ctx = Ctx(changes);

        await Run(new ShellTool(), new JsonObject { ["command"] = "echo two>> notes.txt" }, ctx);
        var first = Assert.Single(changes.Diff().Files);
        Assert.Equal([new DiffLine(DiffLineKind.Note, null, null, FileChanges.NotKnown)], first.Lines);

        await Run(new EditFileTool(), new JsonObject { ["path"] = file, ["old_text"] = "two", ["new_text"] = "TWO" }, ctx);
        var then = Assert.Single(changes.Diff().Files);
        Assert.Equal((1, 1), (then.Added, then.Removed));
    }

    [Fact]
    public async Task What_another_program_writes_in_the_home_folder_during_a_command_is_not_the_commands()
    {
        // A command with no folder runs in the home folder, where other programs write all the time:
        // a Claude Code beside BlackBox rewrote .claude.json there during a command's sleep.
        var home = Path.Combine(_root, "home");
        var project = Path.Combine(_root, "project");
        Directory.CreateDirectory(Path.Combine(home, ".claude", "backups"));
        Directory.CreateDirectory(project);
        var settings = Path.Combine(home, ".claude.json");
        File.WriteAllText(settings, "{\"at\": 1}\n");
        var changes = new FileChanges { Home = home };
        var ctx = new ToolContext { Platform = new FakePlatform(), Settings = new AgentSettings(), WorkingDirectory = home, Changes = changes };

        var outside = Task.Run(async () =>
        {
            await Task.Delay(400);
            File.WriteAllText(settings, "{\"at\": 2}\n");
            File.WriteAllText(Path.Combine(home, ".claude", "backups", ".claude.json.backup.1"), "{}\n");
            File.WriteAllText(Path.Combine(home, "other.txt"), "not the command's\n");
        });
        var result = await Run(new ShellTool(), new JsonObject
        {
            ["command"] = $"echo named> notes.txt & cd /d {project} & echo built> out.txt & ping -n 3 127.0.0.1 >nul",
        }, ctx);
        await outside;

        Assert.False(result.IsError, result.Text);
        var diff = changes.Diff();
        Assert.Equal([Path.Combine(home, "notes.txt"), Path.Combine(project, "out.txt")], diff.Files.Select(diff.FullPath).Order());
    }

    [Fact]
    public async Task Another_programs_write_in_the_commands_own_folder_still_counts()
    {
        // The same outside write, but the command runs in a folder of its own: there it counts, as
        // nothing can tell who wrote. This is why the home folder is no longer watched whole.
        var work = Path.Combine(_root, "work");
        Directory.CreateDirectory(work);
        var changes = new FileChanges { Home = Path.Combine(_root, "home") };
        var ctx = new ToolContext { Platform = new FakePlatform(), Settings = new AgentSettings(), WorkingDirectory = work, Changes = changes };
        var outside = Task.Run(async () =>
        {
            await Task.Delay(400);
            File.WriteAllText(Path.Combine(work, "other.txt"), "x\n");
        });
        await Run(new ShellTool(), new JsonObject { ["command"] = "ping -n 3 127.0.0.1 >nul" }, ctx);
        await outside;
        Assert.Equal("other.txt", Assert.Single(changes.Diff().Files).Path);
    }

    [Fact]
    public void Shared_folders_and_the_paths_a_command_names()
    {
        var home = Path.Combine(_root, "home");
        Assert.True(FileChanges.Scope.IsShared(home, home));
        Assert.True(FileChanges.Scope.IsShared(_root, home)); // the folder the home folder is in
        Assert.True(FileChanges.Scope.IsShared(Path.GetPathRoot(_root)!, home));
        Assert.True(FileChanges.Scope.IsShared(Path.GetTempPath(), home));
        Assert.False(FileChanges.Scope.IsShared(Path.Combine(home, "Documents"), home));
        Assert.False(FileChanges.Scope.IsShared(Path.Combine(_root, "project"), home));

        Assert.Equal(["a b.txt", "x.txt", "C:\\y.cs", Path.Combine(home, "z.md"), home + "\\q"],
            FileChanges.Scope.Words("Set-Content \"a b.txt\" 1; Get-Item -Path:x.txt | Copy-Item C:\\y.cs; type ~\\z.md > $HOME\\q", home)
                .Where(w => w.Contains('.') || w.Contains('\\')));
        Assert.Contains(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + "\\c.txt", FileChanges.Scope.Words("echo 1 > $env:USERPROFILE\\c.txt", home));

        Directory.CreateDirectory(home);
        Directory.CreateDirectory(Path.Combine(_root, "project"));
        Directory.CreateDirectory(Path.Combine(_root, "src"));
        var scope = FileChanges.Scope.Of($"cd {_root}\\project; echo 1 > notes.txt; dir {_root}\\src\\*.cs", home, home);
        Assert.True(scope.Allows(Path.Combine(home, "notes.txt")));
        Assert.True(scope.Allows(Path.Combine(_root, "project", "a", "b.cs")));
        Assert.True(scope.Allows(Path.Combine(_root, "src", "c.cs")));
        Assert.False(scope.Allows(Path.Combine(home, ".claude.json")));
        Assert.False(scope.Allows(Path.Combine(_root, "other.txt")));

        // A folder it names in the home folder's AppData counts, though AppData is noise seen from the home folder.
        var deep = Path.Combine(home, "AppData", "Local", "proj");
        Directory.CreateDirectory(deep);
        var inHome = FileChanges.Scope.Of($"Set-Location '{deep}'; 'x' | Set-Content out.txt", home, home);
        Assert.True(inHome.Allows(Path.Combine(deep, "out.txt")));
        Assert.False(inHome.Allows(Path.Combine(deep, "bin", "out.dll")));
        Assert.False(inHome.Allows(Path.Combine(home, "AppData", "Local", "other.txt")));
    }

    [Fact]
    public async Task The_list_is_kept_for_the_chat_and_read_back()
    {
        var store = Path.Combine(_root, "chats", "changes", "chat1.json");
        var changes = new FileChanges();
        var file = Path.Combine(_root, "a.txt");
        File.WriteAllText(file, "before\n");
        await Run(new WriteFileTool(), new JsonObject { ["path"] = file, ["content"] = "after\n" }, Ctx(changes));
        Assert.False(File.Exists(store)); // no chat to keep it for yet
        changes.KeepIn(store);

        var back = FileChanges.Load(store).Diff();

        var only = Assert.Single(back.Files);
        Assert.Equal([new DiffLine(DiffLineKind.Removed, 1, null, "before"), new DiffLine(DiffLineKind.Added, null, 1, "after")], only.Lines.Skip(1));
        Assert.Empty(FileChanges.Load(Path.Combine(_root, "none.json")).Diff().Files);
    }

    [Fact]
    public async Task The_agent_reads_a_file_before_writing_it_and_says_so_after()
    {
        var file = Path.Combine(_root, "x.txt");
        File.WriteAllText(file, "old\n");
        var changes = new FileChanges();
        var told = 0;
        changes.Changed += _ => told++;
        var args = new JsonObject { ["path"] = file.Replace('\\', '/'), ["content"] = "new\n" }.ToJsonString();
        var server = new ScriptedServer(Sse.ToolCall("c1", "write_file", args), Sse.Text("Done."));
        var session = new AgentSession(new OpenAiChatClient(new HttpClient(server), "http://test", null), new ChatRequestOptions(),
            ToolRegistry.CreateDefault(true), Ctx(changes), "sys");

        await session.RunAsync("go", new RecordingObserver(), CancellationToken.None);

        Assert.Equal(1, told);
        Assert.Equal(("x.txt", 1, 1), (changes.Diff().Files.Single().Path, changes.Diff().Added, changes.Diff().Removed));
    }

    [Fact]
    public void Paths_share_their_deepest_folder()
    {
        Assert.Equal(@"C:\a", FileChanges.CommonFolder([@"C:\a\b\x.cs", @"C:\a\c.cs"]));
        Assert.Equal(@"C:\a\b", FileChanges.CommonFolder([@"C:\a\b\x.cs"]));
        Assert.Equal(@"C:\", FileChanges.CommonFolder([@"C:\a\x.cs", @"C:\ab\y.cs"]));
        Assert.Equal("", FileChanges.CommonFolder([@"C:\a\x.cs", @"D:\a\y.cs"]));
        Assert.True(FileChanges.IsNoise(@"C:\p", @"C:\p\src\bin\Debug\a.dll"));
        Assert.False(FileChanges.IsNoise(@"C:\p", @"C:\p\src\a.cs"));
        Assert.False(FileChanges.IsNoise(@"C:\p", @"C:\p\bin")); // the folder itself: a file named bin is no build
    }
}
