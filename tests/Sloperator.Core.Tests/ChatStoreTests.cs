using Sloperator.Core.Chats;
using Sloperator.Core.Llm;
using Xunit;

namespace Sloperator.Core.Tests;

/// <summary>Saved chats in Data/chats: what the History dropdown lists, and what is kept to carry on with a chat.</summary>
public class ChatStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bb-chats-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    /// <summary>Claude's signed thinking goes on with a chat opened again (see <see cref="ChatMessage.AnthropicContent"/>).</summary>
    [Fact]
    public void Claudes_signed_thinking_is_saved_with_the_chat()
    {
        var store = new ChatStore(_dir);
        var blocks = new System.Text.Json.Nodes.JsonArray(
            new System.Text.Json.Nodes.JsonObject { ["type"] = "thinking", ["thinking"] = "Press win+r first.", ["signature"] = "c2lnbmVk" },
            new System.Text.Json.Nodes.JsonObject { ["type"] = "tool_use", ["id"] = "c1", ["name"] = "press_keys", ["input"] = new System.Text.Json.Nodes.JsonObject { ["keys"] = "win+r" } });
        var reply = new ChatMessage { Role = ChatRole.Assistant, ToolCalls = [new ToolCall { Id = "c1", Name = "press_keys", Arguments = "{\"keys\":\"win+r\"}" }], AnthropicContent = blocks };
        var chat = new SavedChat { Id = ChatStore.NewId(), Title = "Open Notepad", Updated = DateTime.UtcNow, History = ChatStore.ForSaving([ChatMessage.User("Open Notepad"), reply, ChatMessage.ToolResult("c1", "ok")]) };
        store.Save(chat);

        var loaded = store.Load(chat.Id)!.History[1].AnthropicContent!;
        Assert.Equal(blocks.ToJsonString(), loaded.ToJsonString());
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
    public void Times_read_back_and_a_chat_saved_before_them_has_none()
    {
        var store = new ChatStore(_dir);
        var at = new DateTime(2026, 10, 16, 20, 46, 34, DateTimeKind.Utc);
        store.Save(new SavedChat
        {
            Id = "timed",
            Title = "Open Notepad",
            Entries =
            [
                new ChatEntry { Kind = ChatEntryKind.User, Text = "Open Notepad", At = at },
                new ChatEntry
                {
                    Kind = ChatEntryKind.Assistant, Text = "Done.", At = at.AddSeconds(1), Sent = at.AddSeconds(3), FirstWords = at.AddSeconds(20),
                    Ended = at.AddSeconds(42), RunTook = TimeSpan.FromSeconds(43.5),
                },
            ],
        });
        File.WriteAllText(Path.Combine(_dir, "old.json"), """{"id":"old","title":"Old","entries":[{"kind":"User","text":"Hi"}]}""");

        var reply = store.Load("timed")!.Entries[1];
        Assert.Equal(at, store.Load("timed")!.Entries[0].At);
        Assert.Equal((at.AddSeconds(1), at.AddSeconds(3), at.AddSeconds(20), at.AddSeconds(42)), (reply.At, reply.Sent, reply.FirstWords, reply.Ended));
        Assert.Equal(TimeSpan.FromSeconds(43.5), reply.RunTook);
        var old = Assert.Single(store.Load("old")!.Entries);
        Assert.Null(old.At);
        Assert.Null(old.RunTook);
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
    public void Pictures_are_kept_beside_the_chat_in_order_each_written_once()
    {
        var store = new ChatStore(_dir);
        var shot = ContentPart.FromImage([1, 2, 3], "image/jpeg");
        var zoom = ContentPart.FromImage([4, 5], "image/png");
        var history = new List<ChatMessage>
        {
            ChatMessage.User("Look at the screen"),
            new() { Role = ChatRole.User, Parts = [ContentPart.FromText("Screenshot after the last action:"), shot] },
        };
        var pictures = new ChatPictures();

        var first = new Dictionary<string, byte[]>();
        var saved = ChatStore.ForSaving(history, pictures, first);
        Assert.Equal("0001.jpg", saved[1].Parts![1].File);
        Assert.Null(saved[1].Parts![1].ImageBytes);
        Assert.Equal(["0001.jpg"], first.Keys);
        store.Save(new SavedChat { Id = "c1", Title = "t", History = saved, NewPictures = first });

        history.Add(new() { Role = ChatRole.Tool, ToolCallId = "z", Parts = [ContentPart.FromText("Zoomed in:"), zoom] });
        var second = new Dictionary<string, byte[]>();
        saved = ChatStore.ForSaving(history, pictures, second);
        Assert.Equal("0001.jpg", saved[1].Parts![1].File); // the same name in every version
        Assert.Equal("0002.png", saved[2].Parts![1].File);
        Assert.Equal(["0002.png"], second.Keys);
        store.Save(new SavedChat { Id = "c1", Title = "t", History = saved, NewPictures = second });

        Assert.Equal([1, 2, 3], File.ReadAllBytes(Path.Combine(store.PicturesOf("c1"), "0001.jpg")));
        Assert.Equal([4, 5], File.ReadAllBytes(Path.Combine(store.PicturesOf("c1"), "0002.png")));
        Assert.DoesNotContain(store.List(), h => h.Id != "c1"); // the folder isn't listed as a chat
        Assert.Equal(3, store.NextPictureNumber("c1")); // carried on, the chat's pictures go after these

        // Opened again, on another computer: the conversation has its pictures back where they were.
        var elsewhere = new ChatStore(_dir + "-copy");
        Directory.CreateDirectory(elsewhere.PicturesOf("c1"));
        File.Copy(Path.Combine(_dir, "c1.json"), Path.Combine(elsewhere.Folder, "c1.json"));
        foreach (var picture in Directory.GetFiles(store.PicturesOf("c1")))
            File.Copy(picture, Path.Combine(elsewhere.PicturesOf("c1"), Path.GetFileName(picture)));
        var loaded = elsewhere.Load("c1")!.History;
        Assert.Equal("Screenshot after the last action:", loaded[1].Parts![0].Text);
        Assert.Equal([1, 2, 3], loaded[1].Parts![1].ImageBytes);
        Assert.Equal("image/png", loaded[2].Parts![1].ImageMime);
        Assert.Equal([4, 5], loaded[2].Parts![1].ImageBytes);
        // Saved again, it names the same files and writes none.
        var again = new Dictionary<string, byte[]>();
        Assert.Equal("0002.png", ChatStore.ForSaving(loaded, new ChatPictures(3), again)[2].Parts![1].File);
        Assert.Empty(again);
        // A picture gone from the folder becomes a note, and the chat still opens.
        File.Delete(Path.Combine(elsewhere.PicturesOf("c1"), "0001.jpg"));
        Assert.Contains("missing", elsewhere.Load("c1")!.History[1].Parts![1].Text);
        Directory.Delete(elsewhere.Folder, recursive: true);

        store.Delete("c1");
        Assert.False(Directory.Exists(store.PicturesOf("c1")));
    }

    [Fact]
    public void A_chat_version_replaced_before_it_was_written_hands_on_its_pictures()
    {
        var store = new ChatStore(_dir);
        var saver = new ChatSaver(store);
        for (var i = 1; i <= 20; i++)
            saver.Save(new SavedChat { Id = "c1", Title = "t", NewPictures = new() { [$"{i:D4}.jpg"] = [(byte)i] } });
        Assert.True(saver.Flush(TimeSpan.FromSeconds(10)));

        Assert.Equal(20, Directory.GetFiles(store.PicturesOf("c1")).Length);
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
    public void A_chat_is_saved_even_while_another_program_holds_the_file_for_a_moment()
    {
        var store = new ChatStore(_dir);
        store.Save(new SavedChat { Id = "c", Title = "First" });
        // An antivirus scanner or indexer that has the file open blocks replacing it.
        var held = new FileStream(Path.Combine(_dir, "c.json"), FileMode.Open, FileAccess.Read, FileShare.Read);
        // Let go on a thread of its own: a pool task waited behind the rest of the suite's work past
        // the save's half second of tries, and the test failed now and then in a full run.
        var release = new Thread(() => { Thread.Sleep(120); held.Dispose(); });
        release.Start();

        store.Save(new SavedChat { Id = "c", Title = "Second" });

        release.Join();
        Assert.Equal("Second", store.Load("c")!.Title);
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
    }

    [Fact]
    public async Task Two_saves_of_one_file_at_once_both_get_through_whole()
    {
        // The agent (mcp_servers) and the Settings dialog can write mcp.json at the same moment;
        // sharing one temporary file, one write could throw or the other's be lost.
        var path = Path.Combine(_dir, "mcp.json");
        var texts = Enumerable.Range(0, 8).Select(i => new string((char)('a' + i), 50_000)).ToList();

        await Task.WhenAll(texts.Select(t => Task.Run(() => Sloperator.Core.Settings.AtomicFile.Write(path, t), TestContext.Current.CancellationToken)));

        Assert.Contains(File.ReadAllText(path), texts);
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
    }

    [Fact]
    public void Chats_written_in_the_background_end_as_their_last_version()
    {
        // At every step a long run's chat, megabytes, went onto the disk on the window's thread.
        var store = new ChatStore(_dir);
        var saver = new ChatSaver(store);
        for (var i = 1; i <= 50; i++)
        {
            saver.Save(new SavedChat { Id = "a", Title = $"A {i}", Entries = [new ChatEntry { Text = new string('x', 100_000) }] });
            saver.Save(new SavedChat { Id = "b", Title = $"B {i}" });
        }
        Assert.True(saver.Flush(TimeSpan.FromSeconds(30)));
        Assert.Equal("A 50", store.Load("a")!.Title);
        Assert.Equal("B 50", store.Load("b")!.Title);
        Assert.True(saver.Flush(TimeSpan.Zero)); // nothing left to write

        // A chat being deleted isn't written again after it is gone.
        saver.Forget("a");
        store.Delete("a");
        Assert.True(saver.Flush(TimeSpan.FromSeconds(5)));
        Assert.Null(store.Load("a"));
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
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
