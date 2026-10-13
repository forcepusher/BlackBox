using BlackBox.Core.Chats;
using BlackBox.Core.Llm;
using Xunit;

namespace BlackBox.Core.Tests;

/// <summary>Saved chats in Data/chats: what the History dropdown lists, and what is kept to carry on with a chat.</summary>
public class ChatStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bb-chats-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void A_saved_chat_reads_back_as_it_was()
    {
        var store = new ChatStore(_dir);
        var chat = new SavedChat
        {
            Id = ChatStore.NewId(),
            Title = "Open Notepad",
            Updated = DateTime.UtcNow,
            Entries =
            [
                new ChatEntry { Kind = ChatEntryKind.User, Text = "Open Notepad" },
                new ChatEntry { Kind = ChatEntryKind.Assistant, Text = "Done.", Reasoning = "Press win+r." },
                new ChatEntry { Kind = ChatEntryKind.Tool, CallId = "c1", Name = "press_keys", Summary = "win+r", State = "Done", Text = "ok" },
                new ChatEntry { Kind = ChatEntryKind.Notice, Text = "Stopped.", IsError = true },
            ],
            History =
            [
                ChatMessage.User("Open Notepad"),
                new ChatMessage { Role = ChatRole.Assistant, ToolCalls = [new ToolCall { Id = "c1", Name = "press_keys", Arguments = "{\"keys\":\"win+r\"}" }] },
                ChatMessage.ToolResult("c1", "ok"),
            ],
        };
        store.Save(chat);

        var loaded = store.Load(chat.Id)!;
        Assert.Equal("Open Notepad", loaded.Title);
        Assert.Equal([ChatEntryKind.User, ChatEntryKind.Assistant, ChatEntryKind.Tool, ChatEntryKind.Notice], loaded.Entries.Select(e => e.Kind));
        Assert.Equal("Press win+r.", loaded.Entries[1].Reasoning);
        Assert.Equal("press_keys", loaded.Entries[2].Name);
        Assert.True(loaded.Entries[3].IsError);
        Assert.Equal("{\"keys\":\"win+r\"}", loaded.History[1].ToolCalls![0].Arguments);
        Assert.Equal("c1", loaded.History[2].ToolCallId);
    }

    [Fact]
    public void The_list_has_the_latest_chat_first_and_skips_broken_files()
    {
        var store = new ChatStore(_dir);
        store.Save(new SavedChat { Id = "a", Title = "Older", Updated = DateTime.UtcNow.AddHours(-2) });
        store.Save(new SavedChat { Id = "b", Title = "Newer", Updated = DateTime.UtcNow });
        File.WriteAllText(Path.Combine(_dir, "broken.json"), "{ not json");

        Assert.Equal(["Newer", "Older"], store.List().Select(h => h.Title));

        store.Delete("b");
        Assert.Equal(["Older"], store.List().Select(h => h.Title));
        Assert.Null(store.Load("b"));
    }

    [Fact]
    public void Screenshots_are_not_saved_and_a_stopped_tool_call_gets_an_answer()
    {
        var history = new List<ChatMessage>
        {
            ChatMessage.User("Look at the screen"),
            new() { Role = ChatRole.User, Parts = [ContentPart.FromText("Screenshot after the last action:"), ContentPart.FromImage([1, 2, 3], "image/png")] },
            new() { Role = ChatRole.Assistant, ToolCalls = [new ToolCall { Id = "c9", Name = "screenshot", Arguments = "{}" }] },
        };

        var saved = ChatStore.ForSaving(history);

        Assert.Null(saved[1].Parts);
        Assert.StartsWith("Screenshot after the last action:", saved[1].Content);
        Assert.Contains("not kept", saved[1].Content);
        Assert.Equal(ChatRole.Tool, saved[^1].Role);
        Assert.Equal("c9", saved[^1].ToolCallId);
        Assert.Equal(3, history.Count); // the live conversation is left as it is
    }

    [Fact]
    public void The_list_reads_only_the_start_of_each_file()
    {
        var store = new ChatStore(_dir);
        var huge = new string('r', 200_000);
        var history = Enumerable.Range(0, 50).Select(i => new ChatMessage { Role = ChatRole.Assistant, Content = "step", Reasoning = huge }).ToList();
        var updated = new DateTime(2026, 10, 6, 12, 30, 0, DateTimeKind.Utc);
        store.Save(new SavedChat { Id = "long-run", Title = "Make a game", Updated = updated, History = history });
        Assert.True(new FileInfo(Path.Combine(_dir, "long-run.json")).Length > 10_000_000);

        var header = Assert.Single(store.List());
        Assert.Equal(("long-run", "Make a game", updated), (header.Id, header.Title, header.Updated.ToUniversalTime()));
    }

    [Fact]
    public async Task A_chat_is_saved_even_while_another_program_holds_the_file_for_a_moment()
    {
        var store = new ChatStore(_dir);
        store.Save(new SavedChat { Id = "c", Title = "First" });
        // An antivirus scanner or indexer that has the file open blocks replacing it.
        var held = new FileStream(Path.Combine(_dir, "c.json"), FileMode.Open, FileAccess.Read, FileShare.Read);
        var release = Task.Run(async () => { await Task.Delay(120); held.Dispose(); }, TestContext.Current.CancellationToken);

        store.Save(new SavedChat { Id = "c", Title = "Second" });

        await release;
        Assert.Equal("Second", store.Load("c")!.Title);
        Assert.False(File.Exists(Path.Combine(_dir, "c.json.tmp")));
    }

    [Theory]
    [InlineData("  Open Notepad  ", "Open Notepad")]
    [InlineData("First line\nsecond line", "First line")]
    public void The_title_is_the_first_line_of_the_first_message(string message, string title) =>
        Assert.Equal(title, ChatStore.TitleFrom(message));

    [Fact]
    public void A_long_first_line_is_cut_short()
    {
        var title = ChatStore.TitleFrom(new string('x', 200));
        Assert.Equal(78, title.Length);
        Assert.EndsWith("…", title);
    }
}
