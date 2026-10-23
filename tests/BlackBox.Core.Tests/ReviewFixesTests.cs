using System.Text.Json.Nodes;
using BlackBox.Core.Film;
using BlackBox.Core.Llm;
using Xunit;

namespace BlackBox.Core.Tests;

/// <summary>What reviews of the work since 1.0.0 found wrong (2026-10-23), each kept from coming back.</summary>
public class ReviewFixesTests
{
    private static readonly ClaudeModel Haiku = ClaudeModel.FromName("claude-haiku-4-5");

    private static JsonArray Content(JsonObject request) =>
        new(request["messages"]!.AsArray().SelectMany(m => m!["content"]!.AsArray()).Select(b => b!.DeepClone()).ToArray());

    [Fact]
    public void Past_a_hundred_pictures_the_oldest_are_left_out_fifty_at_a_time()
    {
        var picture = ContentPart.FromImage(new byte[] { 0xFF, 0xD8, 0xFF, 0xD9 }, "image/jpeg");
        var history = new List<ChatMessage> { ChatMessage.User("Play the game") };
        for (var i = 0; i < 120; i++)
        {
            history.Add(new ChatMessage { Role = ChatRole.Assistant, ToolCalls = [new ToolCall { Id = $"c{i}", Name = "press_keys", Arguments = """{"keys":"up"}""" }] });
            history.Add(ChatMessage.ToolResult($"c{i}", "Pressed."));
            history.Add(new ChatMessage { Role = ChatRole.User, Parts = [ContentPart.FromText("Screenshot after the last action:"), picture] });
        }
        var request = AnthropicMessages.BuildRequest(history, [], new ChatRequestOptions { Model = Haiku.Id }, Haiku, new AnthropicMessages.Wire(), []);

        var blocks = Content(request);
        Assert.Equal(70, blocks.Count(b => (string)b!["type"]! == "image")); // 120 less 50
        Assert.Equal(50, blocks.Count(b => b!["text"]?.GetValue<string>().Contains("left out: a request takes 100") == true));
    }

    [Fact]
    public void Thinking_cut_off_before_its_signature_is_not_sent_back()
    {
        var reply = new ChatMessage
        {
            Role = ChatRole.Assistant,
            Content = "",
            AnthropicContent = [new JsonObject { ["type"] = "thinking", ["thinking"] = "Let me think about the whole" }],
        };
        var request = AnthropicMessages.BuildRequest([ChatMessage.User("Hi"), reply, ChatMessage.User("Go on")], [], new ChatRequestOptions { Model = Haiku.Id }, Haiku, new AnthropicMessages.Wire(), []);

        Assert.DoesNotContain(Content(request), b => (string)b!["type"]! == "thinking");
    }

    [Fact]
    public void Ids_given_in_a_change_stay_and_new_ones_come_after_them()
    {
        var board = Storyboard.Parse("""
            {"frames": [{"id": "f1", "prompt": "a lighthouse"}, {"prompt": "a fox"}, {"id": "f2", "prompt": "the keeper"}, {"id": "s1", "prompt": "named like a shot"}],
             "shots": [{"id": "s1", "first": "f2"}, {"id": "s2", "first": "f1"}]}
            """);
        Assert.Equal("the keeper", board.Frame("f2")!.Prompt);
        Assert.Equal("a fox", board.Frames[1].Prompt);
        Assert.Equal("f3", board.Frames[1].Id);
        // The shot keeps its id; the frame that took it is the one renamed.
        Assert.Equal(["s1", "s2"], board.Shots.Select(s => s.Id));
        Assert.NotEqual("s1", board.Frames[3].Id);
        Assert.Equal("f2", board.Shot("s1")!.First);
    }

    [Theory]
    [InlineData("Con", "Con_")]
    [InlineData("nul.mp4", "nul.mp4_")]
    [InlineData("Console", "Console")]
    [InlineData("COM1", "COM1_")]
    public void Film_names_are_never_a_devices(string name, string safe) =>
        Assert.Equal(safe, Storyboard.Safe(name));

    [Fact]
    public void A_song_that_still_covers_the_film_stays_made_when_the_film_gets_shorter()
    {
        var folder = Directory.CreateTempSubdirectory("bb-song-").FullName;
        try
        {
            var board = Storyboard.Parse("""
                {"title": "Long", "shots": [{"id": "s1", "seconds": 30}, {"id": "s2", "seconds": 30}, {"id": "s3", "seconds": 10}],
                 "sound": {"source": "song", "tags": "piano", "seed": 7}}
                """);
            board.Folder = folder;
            Directory.CreateDirectory(Path.Combine(folder, "sound"));
            File.WriteAllText(board.SongFile, "a song");
            File.WriteAllText(board.SongMeta, new JsonObject { ["made"] = new JsonObject { ["stamp"] = FilmMaker.SongStamp(board) } }.ToJsonString());
            Assert.True(FilmMaker.SongIsCurrent(board));

            board.Shot("s3")!.Seconds = 9; // a second shorter: the song still covers it
            Assert.True(FilmMaker.SongIsCurrent(board));
            board.Shot("s3")!.Seconds = 20; // longer than the song
            Assert.False(FilmMaker.SongIsCurrent(board));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}
