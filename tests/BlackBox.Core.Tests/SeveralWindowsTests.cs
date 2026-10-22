using System.Text.Json.Nodes;
using BlackBox.Core.Chats;
using BlackBox.Core.Platform;
using BlackBox.Core.Settings;
using BlackBox.Core.Tools;
using Xunit;

namespace BlackBox.Core.Tests;

/// <summary>
/// Several BlackBox windows at once, each a process of its own sharing one Data folder: the
/// settings each one changes, the chats each one has open, and the mouse and keyboard.
/// </summary>
public class SeveralWindowsTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("bb-windows-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
    }

    [Fact]
    public void Each_window_saves_only_what_it_changed()
    {
        var paths = new AppPaths(_root);
        paths.EnsureCreated();
        var first = new SettingsStore(paths);
        first.Save(new AppSettings { Server = { BaseUrl = "http://localhost:1234/v1", Model = "qwen" } });

        // Two windows open with the same settings.
        var (one, two) = (new SettingsStore(paths), new SettingsStore(paths));
        var inOne = one.Load();
        var inTwo = two.Load();

        // The first picks another model; the second, which still has the old one, saves where it was put.
        inOne.Server.Model = "gemma";
        one.Save(inOne);
        inTwo.Window = new WindowPlacement { X = 10, Y = 20, Width = 900, Height = 700 };
        inTwo.Agent.CustomInstructions = "Be brief.";
        two.Save(inTwo);

        var saved = new SettingsStore(paths).Load();
        Assert.Equal("gemma", saved.Server.Model);
        Assert.Equal("http://localhost:1234/v1", saved.Server.BaseUrl);
        Assert.Equal(10, saved.Window?.X);
        Assert.Equal("Be brief.", saved.Agent.CustomInstructions);

        // Changed back in the first window: what it changes again is written again.
        inOne.Server.Model = "qwen";
        one.Save(inOne);
        Assert.Equal("qwen", new SettingsStore(paths).Load().Server.Model);
        Assert.Equal("Be brief.", new SettingsStore(paths).Load().Agent.CustomInstructions);
    }

    [Fact]
    public void A_key_one_window_removed_goes_and_one_it_never_had_stays()
    {
        var before = JsonNode.Parse("""{"a":1,"b":{"x":1,"y":2},"gone":true}""")!.AsObject();
        var mine = JsonNode.Parse("""{"a":1,"b":{"x":5,"y":2}}""")!.AsObject();
        var theirs = JsonNode.Parse("""{"a":2,"b":{"x":1,"y":3},"gone":true,"new":"kept"}""")!.AsObject();

        var merged = SettingsStore.Merge(before, mine, theirs);

        Assert.True(JsonNode.DeepEquals(JsonNode.Parse("""{"a":2,"b":{"x":5,"y":3},"new":"kept"}"""), merged), merged.ToJsonString());
    }

    [Fact]
    public void A_chat_open_in_one_window_cannot_be_claimed_by_another()
    {
        var store = new ChatStore(Path.Combine(_root, "chats"));
        using (var claim = store.Claim("chat-1"))
        {
            Assert.NotNull(claim);
            Assert.Null(store.Claim("chat-1"));
            // This window's own claim isn't "elsewhere".
            Assert.Empty(store.OpenElsewhere());
        }
        // Let go of: free again, and the claim file is gone with it.
        using (var again = store.Claim("chat-1"))
            Assert.NotNull(again);
        Assert.Empty(Directory.GetFiles(store.Folder, "*.open"));
    }

    [Fact]
    public void A_chat_another_process_holds_is_open_elsewhere()
    {
        var store = new ChatStore(Path.Combine(_root, "chats"));
        Directory.CreateDirectory(store.Folder);
        // Held the way another BlackBox holds it, with its process id in it.
        var file = Path.Combine(store.Folder, "chat-2.open");
        using (var other = new FileStream(file, FileMode.Create, FileAccess.ReadWrite, FileShare.Read, 4096, FileOptions.DeleteOnClose))
        {
            other.Write("4242"u8);
            other.Flush();
            Assert.Equal(4242, Assert.Contains("chat-2", store.OpenElsewhere()));
            Assert.Null(store.Claim("chat-2"));
        }
        Assert.Empty(store.OpenElsewhere());
    }

    [Fact]
    public void A_claim_file_nobody_holds_is_no_claim()
    {
        // Left behind where it couldn't be deleted on close.
        var store = new ChatStore(Path.Combine(_root, "chats"));
        Directory.CreateDirectory(store.Folder);
        File.WriteAllText(Path.Combine(store.Folder, "chat-3.open"), "4242");

        Assert.Empty(store.OpenElsewhere());
        using var claim = store.Claim("chat-3");
        Assert.NotNull(claim);
    }

    [Fact]
    public void A_chat_is_let_go_of_once_its_last_version_is_written()
    {
        var store = new ChatStore(Path.Combine(_root, "chats"));
        var saver = new ChatSaver(store);
        var claim = store.Claim("chat-4")!;
        saver.Save(new SavedChat { Id = "chat-4", Title = "Last", Updated = DateTime.UtcNow });

        saver.Release(claim);
        Assert.True(saver.Flush(TimeSpan.FromSeconds(10)));

        // Another window opening it now finds the version written last.
        using var other = store.Claim("chat-4");
        Assert.NotNull(other);
        Assert.Equal("Last", store.Load("chat-4")?.Title);
    }

    [Fact]
    public async Task An_action_waits_for_its_turn_and_looks_again_when_another_window_had_it()
    {
        var platform = new FakePlatform { DesktopTurn = new Turn(true) };
        var ctx = new ToolContext { Platform = platform, Settings = new AgentSettings(), WorkingDirectory = Path.GetTempPath() };

        var result = await new PressKeysTool().ExecuteAsync(new JsonObject { ["keys"] = "enter" }, ctx, CancellationToken.None);

        Assert.Empty(platform.Actions); // nothing pressed on a screen the model hasn't seen
        Assert.StartsWith("Nothing was done: another BlackBox window", result.Text);
        Assert.NotNull(result.Image);
    }

    [Fact]
    public async Task An_action_whose_turn_never_came_does_nothing_and_says_why()
    {
        var platform = new FakePlatform { DesktopTurn = new Turn(null) };
        var ctx = new ToolContext { Platform = platform, Settings = new AgentSettings(), WorkingDirectory = Path.GetTempPath() };

        var result = await new MouseClickTool().ExecuteAsync(new JsonObject { ["x"] = 10, ["y"] = 10 }, ctx, CancellationToken.None);

        Assert.Empty(platform.Actions);
        Assert.Contains("Do what doesn't need them meanwhile", result.Text);
    }

    [Fact]
    public async Task The_turn_is_let_go_of_when_the_task_ends()
    {
        var turn = new Turn(false);
        var platform = new FakePlatform { DesktopTurn = turn };
        var ctx = new ToolContext { Platform = platform, Settings = new AgentSettings { ScreenshotAfterActions = false }, WorkingDirectory = Path.GetTempPath() };

        await new PressKeysTool().ExecuteAsync(new JsonObject { ["keys"] = "enter" }, ctx, CancellationToken.None);
        Assert.NotEmpty(platform.Actions);
        Assert.Equal(1, turn.Done); // the idle time counts from the end of the action
        Assert.Equal(0, turn.Released);

        await ctx.FinishRunAsync();
        Assert.Equal(1, turn.Released);
    }

    private sealed class Turn(bool? answer) : IDesktopTurn
    {
        public int Released { get; private set; }
        public int Done { get; private set; }
        public Task<bool?> TakeAsync(TimeSpan maxWait, Action<TimeSpan>? waiting, CancellationToken ct) => Task.FromResult(answer);
        void IDesktopTurn.Done() => Done++;
        public void Release() => Released++;
    }
}
