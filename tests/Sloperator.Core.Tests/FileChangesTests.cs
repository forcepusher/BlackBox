using System.Text.Json.Nodes;
using Sloperator.Core.Agent;
using Sloperator.Core.Diffs;
using Sloperator.Core.Llm;
using Sloperator.Core.Settings;
using Sloperator.Core.Tools;
using Xunit;

namespace Sloperator.Core.Tests;

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
    public async Task Files_in_Sloperators_own_Data_folder_are_not_listed()
    {
        // The agent's scratch notes are no change of the user's (the user, 2026-10-27).
        var data = Path.Combine(_root, "Data");
        var changes = new FileChanges();
        var ctx = new ToolContext
        {
            Platform = new FakePlatform(), Settings = new AgentSettings(), WorkingDirectory = _root, Changes = changes,
            ScratchDirectory = Path.Combine(data, "scratch"), ToolsDirectory = Path.Combine(data, "tools"),
        };
        Directory.CreateDirectory(ctx.ScratchDirectory!);
        var made = Path.Combine(_root, "made.txt");

        await Run(new WriteFileTool(), new JsonObject { ["path"] = Path.Combine(data, "scratch", "notes.md"), ["content"] = "plan\n" }, ctx);
        await Run(new WriteFileTool(), new JsonObject { ["path"] = Path.Combine(data, "mcp.json"), ["content"] = "{}\n" }, ctx);
        Assert.True(changes.IsEmpty);
        await Run(new WriteFileTool(), new JsonObject { ["path"] = made, ["content"] = "a\n" }, ctx);

        Assert.False(changes.IsEmpty);
        Assert.Equal([made], changes.Diff().Files.Select(f => changes.Diff().FullPath(f)));
    }

    [Fact]
    public async Task What_a_command_changes_is_not_listed()
    {
        // A watched folder can't tell a command's writes from other programs': a Claude Code beside
        // Sloperator rewrote .claude.json in the home folder during a command's sleep, and it was listed.
        var settings = Path.Combine(_root, ".claude.json");
        File.WriteAllText(settings, "{\"at\": 1}\n");
        var changes = new FileChanges();
        var ctx = Ctx(changes);
        var outside = Task.Run(async () =>
        {
            await Task.Delay(300);
            File.WriteAllText(settings, "{\"at\": 2}\n");
        });

        var shell = new ShellTool();
        var args = new JsonObject { ["command"] = "echo named> notes.txt & ping -n 2 127.0.0.1 >nul" };
        Assert.Null(changes.Starting(shell, args, ctx));
        var result = await Run(shell, args, ctx);
        await outside;

        Assert.False(result.IsError, result.Text);
        Assert.True(File.Exists(Path.Combine(_root, "notes.txt")));
        Assert.Empty(changes.Diff().Files);
    }

    [Fact]
    public void A_list_kept_before_drops_the_files_commands_added()
    {
        var store = Path.Combine(_root, "old.json");
        var written = Path.Combine(_root, "written.txt");
        File.WriteAllText(written, "after\n");
        File.WriteAllText(store, new JsonObject
        {
            ["files"] = new JsonArray(
                new JsonObject { ["path"] = Path.Combine(_root, ".claude.json"), ["exists"] = true, ["text"] = "{}", ["known"] = false },
                new JsonObject { ["path"] = written, ["exists"] = true, ["text"] = "before\n", ["known"] = true }),
        }.ToJsonString());

        Assert.Equal(["written.txt"], FileChanges.Load(store).Diff().Files.Select(f => f.Path));
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
    }
}
