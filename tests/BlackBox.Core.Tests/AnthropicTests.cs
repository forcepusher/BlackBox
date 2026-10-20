using System.Net;
using System.Text.Json.Nodes;
using BlackBox.Core.Agent;
using BlackBox.Core.Llm;
using BlackBox.Core.Settings;
using BlackBox.Core.Tools;
using Xunit;
using static BlackBox.Core.Tests.ClaudeSse;

namespace BlackBox.Core.Tests;

/// <summary>Anthropic's API through the translator (AnthropicMessages), against <see cref="FakeAnthropic"/>.</summary>
public class AnthropicTests
{
    private const string Address = "https://api.anthropic.com/v1";

    private static (OpenAiChatClient Client, FakeAnthropic Api) Connect(string key = "sk-ant-test")
    {
        var api = new FakeAnthropic();
        return (new OpenAiChatClient(new HttpClient(api), Address, key), api);
    }

    private static ChatRequestOptions Opus => new() { Model = "claude-opus-5-5" };

    private static readonly ToolDefinition Click = new()
    {
        Name = "mouse_click",
        Description = "Click.",
        Parameters = JsonNode.Parse("""{"type":"object","properties":{"x":{"type":"number"},"y":{"type":"number"}}}""")!.AsObject(),
    };

    [Fact]
    public void Anthropic_addresses_are_told_apart()
    {
        Assert.True(OpenAiChatClient.IsAnthropic("https://api.anthropic.com"));
        Assert.True(OpenAiChatClient.IsAnthropic("api.anthropic.com/v1/messages"));
        Assert.False(OpenAiChatClient.IsAnthropic("https://openrouter.ai/api/v1"));
        Assert.True(OpenAiChatClient.IsAnthropic("https://gateway.ai.cloudflare.com/v1/acc/gw/anthropic"));
        Assert.False(OpenAiChatClient.IsAnthropic("https://anthropic-proxy.example.com/v1"));
        Assert.False(OpenAiChatClient.IsAnthropic(""));
        // Its websites lead to the API.
        Assert.Equal(Address, ServerAddress.Candidates("platform.claude.com")[0]);
        Assert.Equal(Address, ServerAddress.Candidates("https://console.anthropic.com/settings/keys")[0]);
    }

    [Fact]
    public void Claude_models_are_read_from_their_names()
    {
        var opus = ClaudeModel.FromName("claude-opus-5-5");
        Assert.True(opus.Adaptive);
        Assert.True(opus.Fallbacks);
        Assert.Equal(128_000, opus.MaxTokens);
        var haiku = ClaudeModel.FromName("claude-haiku-4-5-20251001");
        Assert.False(haiku.Adaptive);
        Assert.Equal(64_000, haiku.MaxTokens);
        Assert.False(ClaudeModel.FromName("claude-haiku-5-5").Fallbacks);
        Assert.True(ClaudeModel.FromName("claude-sonnet-5-5").Fallbacks);
        Assert.False(ClaudeModel.FromName("claude-sonnet-4-6").Fallbacks);
        Assert.True(ClaudeModel.FromName("claude-sonnet-4-6").Adaptive);
        Assert.Equal(("sonnet", 3.5), ClaudeModel.Parse("claude-3-5-sonnet-latest"));
    }

    [Fact]
    public async Task Stream_gives_text_thinking_calls_and_the_whole_prompt_size()
    {
        var sse = Message("tool_use", new Thinking("The Save button is at the top."), new Said("Saving now."), new Call("toolu_01", "mouse_click", """{"x": 10, "y": 20}"""));
        var deltas = new List<StreamDelta>();
        var reply = await AnthropicMessages.ReadStreamAsync(new StringReader(sse), deltas.Add, CancellationToken.None);

        Assert.Equal("Saving now.", reply.Content);
        Assert.Equal("The Save button is at the top.", reply.Reasoning);
        Assert.Equal("tool_calls", reply.FinishReason);
        var call = Assert.Single(reply.ToolCalls!);
        Assert.Equal(("toolu_01", "mouse_click"), (call.Id, call.Name));
        Assert.Equal(10, JsonNode.Parse(call.Arguments)!["x"]!.GetValue<int>());
        // input 40 + cache written 300 + cache read 0
        Assert.Equal(340, reply.PromptTokens);
        Assert.Equal(25, reply.CompletionTokens);
        // The blocks as they came, the signature with the thinking.
        var blocks = reply.AnthropicContent!;
        Assert.Equal(["thinking", "text", "tool_use"], blocks.Select(b => (string)b!["type"]!));
        Assert.Equal(FakeAnthropic.Sign("The Save button is at the top."), (string)blocks[0]!["signature"]!);
        Assert.Equal(10, blocks[2]!["input"]!["x"]!.GetValue<int>());
        Assert.Contains(deltas, d => d.Channel == StreamChannel.Reasoning);
        Assert.Equal("Saving now.", string.Concat(deltas.Where(d => d.Channel == StreamChannel.Content).Select(d => d.Text)));
    }

    [Fact]
    public async Task Stream_cut_off_is_a_drop_and_an_overload_is_sent_again()
    {
        var sse = Message("end_turn", new Said("Half"));
        await Assert.ThrowsAsync<IOException>(() => AnthropicMessages.ReadStreamAsync(new StringReader(sse[..sse.IndexOf("message_delta", StringComparison.Ordinal)]), null, CancellationToken.None));

        var overloaded = "event: error\ndata: {\"type\":\"error\",\"error\":{\"type\":\"overloaded_error\",\"message\":\"Overloaded\"}}\n\n";
        var ex = await Assert.ThrowsAsync<ConnectionLostException>(() => AnthropicMessages.ReadStreamAsync(new StringReader(overloaded), null, CancellationToken.None));
        Assert.True(ex.ServerFailed);
    }

    [Fact]
    public async Task A_refusal_says_why_and_ends_the_request()
    {
        var sse = Message("refusal", new JsonObject { ["type"] = "refusal", ["category"] = "cyber", ["explanation"] = "This looks like an attack." }, new Said("I'll"));
        var ex = await Assert.ThrowsAsync<LlmException>(() => AnthropicMessages.ReadStreamAsync(new StringReader(sse), null, CancellationToken.None));
        Assert.Contains("declined", ex.Message);
        Assert.Contains("cyber", ex.Message);
    }

    [Fact]
    public async Task After_a_fallback_mid_reply_only_the_text_before_it_goes_back()
    {
        var sse = Message("tool_use", new Thinking("Declined model's thought."), new Said("Let me"), new Call("toolu_a", "mouse_click", "{}"),
            new Fallback("claude-opus-5-5", "claude-opus-4-8"), new Said(" check."), new Call("toolu_b", "mouse_click", """{"x":1}"""));
        var reply = await AnthropicMessages.ReadStreamAsync(new StringReader(sse), null, CancellationToken.None);
        Assert.Equal(["text", "text", "tool_use"], reply.AnthropicContent!.Select(b => (string)b!["type"]!));
        Assert.Equal("toolu_b", Assert.Single(reply.ToolCalls!).Id);
    }

    [Fact]
    public async Task Request_has_cached_system_prompt_tools_thinking_and_fallbacks()
    {
        var (client, api) = Connect();
        await client.CompleteAsync([ChatMessage.System("You are BlackBox."), ChatMessage.User("Open Notepad.")], [Click], Opus, null, CancellationToken.None);

        Assert.Empty(api.Violations);
        var sent = api.Requests.Last(r => r.Path == "/v1/messages");
        Assert.Equal("sk-ant-test", sent.Headers["x-api-key"]);
        Assert.Equal("2023-06-01", sent.Headers["anthropic-version"]);
        Assert.Contains(AnthropicBetas.Binding, sent.Headers["anthropic-beta"]);
        Assert.Contains(AnthropicBetas.Fallback, sent.Headers["anthropic-beta"]);
        Assert.False(sent.Headers.ContainsKey("authorization"));
        var body = sent.Body!;
        Assert.Equal("You are BlackBox.", (string)body["system"]![0]!["text"]!);
        Assert.NotNull(body["system"]![0]!["cache_control"]);
        Assert.Equal("mouse_click", (string)body["tools"]![0]!["name"]!);
        Assert.NotNull(body["tools"]![0]!["input_schema"]);
        Assert.Equal("adaptive", (string)body["thinking"]!["type"]!);
        Assert.Equal("summarized", (string)body["thinking"]!["display"]!);
        Assert.Equal("drop_block", (string)body["thinking"]!["block_binding"]!["prefix_mismatch_behavior"]!);
        Assert.Equal("default", (string)body["fallbacks"]!);
        Assert.Equal(64_000, (int)body["max_tokens"]!);
        Assert.True((bool)body["stream"]!);
        Assert.Null(body["output_config"]);
    }

    [Fact]
    public async Task A_quick_look_asks_for_low_effort_and_room_to_think()
    {
        var (client, api) = Connect();
        await client.CompleteAsync([ChatMessage.User("Where is Save?")], [], new ChatRequestOptions { Model = "claude-opus-5-5", MaxTokens = 4096, QuickAnswer = true }, null, CancellationToken.None);
        var body = api.Requests.Last().Body!;
        Assert.Equal("low", (string)body["output_config"]!["effort"]!);
        Assert.Equal(64_000, (int)body["max_tokens"]!);
    }

    [Fact]
    public async Task A_model_without_adaptive_thinking_gets_none_and_its_own_reply_limit()
    {
        var (client, api) = Connect();
        await client.CompleteAsync([ChatMessage.User("Hi")], [], new ChatRequestOptions { Model = "claude-haiku-4-5-20251001", MaxTokens = 4096 }, null, CancellationToken.None);
        Assert.Empty(api.Violations);
        var body = api.Requests.Last().Body!;
        Assert.Null(body["thinking"]);
        Assert.Null(body["fallbacks"]);
        Assert.Equal(4096, (int)body["max_tokens"]!);
    }

    [Fact]
    public async Task Tool_results_screenshots_and_earlier_replies_make_valid_turns()
    {
        var (client, api) = Connect();
        var first = new ChatMessage
        {
            Role = ChatRole.Assistant, Content = "Clicking.", Reasoning = "Qwen's thought",
            ToolCalls = [new ToolCall { Id = "call:1", Name = "mouse_click", Arguments = """{"x":5,"y":6}""" }, new ToolCall { Id = "call_2", Name = "mouse_click", Arguments = "not json" }],
        };
        List<ChatMessage> history =
        [
            ChatMessage.System("You are BlackBox."),
            ChatMessage.User("Click twice."),
            first,
            ChatMessage.ToolResult("call:1", "Clicked."),
            // call_2's result is missing (a stop), and a stray result whose call is gone.
            ChatMessage.ToolResult("call_gone", "Old output."),
            new ChatMessage { Role = ChatRole.User, Parts = [ContentPart.FromText("Screenshot after the last action:"), ContentPart.FromImage(Jpeg(64, 32), "image/jpeg")] },
            new ChatMessage { Role = ChatRole.Assistant, Content = "" },
            ChatMessage.User("[BlackBox] Continue with the task."),
        ];
        await client.CompleteAsync(history, [Click], Opus, null, CancellationToken.None);

        Assert.Empty(api.Violations);
        var messages = (JsonArray)api.Requests.Last().Body!["messages"]!;
        Assert.Equal(["user", "assistant", "user"], messages.Select(m => (string)m!["role"]!));
        var reply = (JsonArray)messages[1]!["content"]!;
        // Another model's reasoning isn't sent as Claude's thinking; ids get Claude's characters.
        Assert.Equal(["text", "tool_use", "tool_use"], reply.Select(b => (string)b!["type"]!));
        Assert.Equal("call_1", (string)reply[1]!["id"]!);
        Assert.Equal(new JsonObject(), reply[2]!["input"], JsonNode.DeepEquals);
        var results = (JsonArray)messages[2]!["content"]!;
        Assert.Equal(["tool_result", "tool_result", "text", "text", "image", "text"], results.Select(b => (string)b!["type"]!));
        Assert.Equal("Not run.", (string)results[1]!["content"]!);
        Assert.Contains("Old output.", (string)results[2]!["text"]!);
        Assert.NotNull(results[^1]!["cache_control"]);
    }

    [Fact]
    public async Task Many_pictures_are_made_to_fit_2000_pixels()
    {
        var (client, api) = Connect();
        var big = Jpeg(2560, 1080);
        var history = new List<ChatMessage> { ChatMessage.User("Look.") };
        for (var i = 0; i < 21; i++)
        {
            history.Add(new ChatMessage { Role = ChatRole.Assistant, Content = $"Step {i}." });
            history.Add(new ChatMessage { Role = ChatRole.User, Parts = [ContentPart.FromText("Screen:"), ContentPart.FromImage(big, "image/jpeg")] });
        }
        await client.CompleteAsync(history, [], Opus, null, CancellationToken.None);
        Assert.Empty(api.Violations);

        // Up to 20 they go as they are.
        var (client2, api2) = Connect();
        await client2.CompleteAsync(history.Take(21).ToList(), [], Opus, null, CancellationToken.None);
        Assert.Empty(api2.Violations);
        var image = api2.Requests.Last().Body!["messages"]!.AsArray().SelectMany(m => m!["content"]!.AsArray()).First(b => (string)b!["type"]! == "image");
        Assert.Equal(Convert.ToBase64String(big), (string)image!["source"]!["data"]!);
    }

    [Fact]
    public async Task Errors_say_what_is_wrong()
    {
        var (client, api) = Connect();
        Task Send() => client.CompleteAsync([ChatMessage.User("Hi")], [], Opus, null, CancellationToken.None);

        api.Failures.Enqueue((HttpStatusCode.BadRequest, FakeAnthropic.Error("invalid_request_error", "prompt is too long: 215430 tokens > 200000 maximum").ToJsonString()));
        var overflow = await Assert.ThrowsAsync<ContextOverflowException>(Send);
        Assert.Equal(200_000, overflow.ContextLimit);

        api.Failures.Enqueue(((HttpStatusCode)529, FakeAnthropic.Error("overloaded_error", "Overloaded").ToJsonString()));
        Assert.True((await Assert.ThrowsAsync<ConnectionLostException>(Send)).ServerFailed);

        api.Failures.Enqueue((HttpStatusCode.TooManyRequests, FakeAnthropic.Error("rate_limit_error", "Number of request tokens has exceeded your per-minute rate limit").ToJsonString()));
        await Assert.ThrowsAsync<RateLimitedException>(Send);

        // A spend limit isn't waited out: asking again fails until it is raised.
        api.Failures.Enqueue((HttpStatusCode.TooManyRequests, """{"type":"error","error":{"type":"rate_limit_error","message":"You have reached your API usage limits: your organization has crossed its monthly API usage threshold.","details":{"error_code":"enforced_spend_limit_reached"}}}"""));
        Assert.Contains("spend limit", (await Assert.ThrowsAsync<KeyRefusedException>(Send)).Message);

        api.Failures.Enqueue((HttpStatusCode.BadRequest, FakeAnthropic.Error("invalid_request_error", "Your credit balance is too low to access the Anthropic API. Please go to Plans & Billing to upgrade or purchase credits.").ToJsonString()));
        Assert.Contains("credits", (await Assert.ThrowsAsync<KeyRefusedException>(Send)).Message);

        var (wrongKey, _) = Connect("sk-ant-wrong");
        var refused = await Assert.ThrowsAsync<KeyRefusedException>(() => wrongKey.CompleteAsync([ChatMessage.User("Hi")], [], Opus, null, CancellationToken.None));
        Assert.Contains("invalid x-api-key", refused.Message);
    }

    [Fact]
    public async Task Parts_the_API_does_not_take_are_left_out_and_the_request_sent_again()
    {
        var (client, api) = Connect();
        api.KnownBetas.Clear();
        await client.CompleteAsync([ChatMessage.User("Hi")], [], new ChatRequestOptions { Model = "claude-opus-5-5" }, null, CancellationToken.None);
        var sent = api.Requests.Where(r => r.Path == "/v1/messages").ToList();
        Assert.Equal(2, sent.Count);
        Assert.False(sent[1].Headers.ContainsKey("anthropic-beta"));
        Assert.Null(sent[1].Body!["fallbacks"]);
        Assert.Null(sent[1].Body!["thinking"]!["block_binding"]);
    }

    [Fact]
    public async Task Models_are_listed_page_by_page_with_their_context_and_pictures()
    {
        var (client, api) = Connect();
        api.PageSize = 2;
        var models = await client.ListModelEntriesAsync(CancellationToken.None);
        Assert.Equal(4, models.Count);
        var opus = models.First(m => (string)m["id"]! == "claude-opus-5-5");
        Assert.Equal(1_000_000, (int)opus["context_length"]!);
        Assert.Contains("image", opus["architecture"]!["input_modalities"]!.AsArray().Select(v => (string)v!));
    }

    [Fact]
    public async Task Probe_connects_picks_Opus_and_reads_its_context()
    {
        var api = new FakeAnthropic();
        var http = new HttpClient(api);
        var server = new ServerSettings { BaseUrl = "api.anthropic.com", ApiKey = "sk-ant-test" };
        var probe = await ServerProbe.CheckAsync(http, server, CancellationToken.None);
        Assert.True(probe.Reachable, probe.Message);
        Assert.Equal(Address, probe.BaseUrl);
        Assert.Equal("claude-opus-5-5", probe.Model);
        Assert.True(probe.AutoPicked);
        Assert.Equal(1_000_000, probe.LoadedContext);
        Assert.True(probe.ContextHosted);
        Assert.True(probe.Vision);
        // Nothing is asked of LM Studio's or Ollama's addresses there.
        Assert.All(api.Requests, r => Assert.StartsWith("/v1/models", r.Path));

        var none = await ServerProbe.CheckAsync(http, new ServerSettings { BaseUrl = "api.anthropic.com" }, CancellationToken.None);
        Assert.True(none.KeyRefused);
        var elsewhere = await ServerProbe.CheckAsync(new HttpClient(new FakeAnthropic()), new ServerSettings { BaseUrl = "sk-ant-api03-abcdefghijklmnopqrstuvwxyz0123" }, CancellationToken.None);
        Assert.Contains("api.anthropic.com", elsewhere.Message);
    }

    /// <summary>
    /// A run of several steps: every request is valid, each reply's thinking goes back unchanged
    /// (nothing dropped), and from the second step on the cache serves what came before.
    /// </summary>
    [Fact]
    public async Task Agent_run_sends_its_thinking_back_and_reads_the_cache()
    {
        var api = new FakeAnthropic();
        api.Reply = (body, n) => n switch
        {
            0 => Message("tool_use", new Thinking("First I need to see the screen."), new Call("toolu_1", "screenshot", "{}")),
            1 => Message("tool_use", new Thinking("Notepad is closed; I'll wait a moment."), new Said("Waiting."), new Call("toolu_2", "wait", """{"seconds":1}""")),
            2 => Message("tool_use", new Thinking("Two things at once."), new Call("toolu_3", "wait", """{"seconds":1}"""), new Call("toolu_4", "screenshot", "{}")),
            _ => Text("Done."),
        };
        var client = new OpenAiChatClient(new HttpClient(api), Address, "sk-ant-test");
        var ctx = new ToolContext { Platform = new FakePlatform(), Settings = new AgentSettings(), WorkingDirectory = Path.GetTempPath() };
        var session = new AgentSession(client, Opus, ToolRegistry.CreateDefault(true), ctx, "You are BlackBox, test prompt.", contextTokens: 200_000);
        var observer = new RecordingObserver();
        await session.RunAsync("Open Notepad.", observer, CancellationToken.None);

        Assert.Empty(api.Violations);
        Assert.Equal(0, api.DroppedThinking);
        Assert.Equal(4, api.CacheReads.Count);
        Assert.Equal(0, api.CacheReads[0]);
        Assert.All(api.CacheReads.Skip(1), read => Assert.True(read > 0));
        // Thinking came back in the order it was written, signed as it was.
        var last = (JsonArray)api.Requests.Last(r => r.Path == "/v1/messages").Body!["messages"]!;
        var thoughts = last.SelectMany(m => m!["content"]!.AsArray()).Where(b => (string)b!["type"]! == "thinking").Select(b => (string)b!["thinking"]!).ToList();
        Assert.Equal(["First I need to see the screen.", "Notepad is closed; I'll wait a moment.", "Two things at once."], thoughts);
        Assert.Equal("Done.", session.History.Last().Content);

        // The chat saved and opened again (History): its screenshots and thinking aren't kept, and
        // carrying on with it is a valid conversation.
        var reopened = Chats.ChatStore.ForSaving(session.History);
        Assert.All(reopened, m => Assert.Null(m.AnthropicContent));
        var again = new AgentSession(client, Opus, ToolRegistry.CreateDefault(true), ctx, "You are BlackBox, test prompt.", reopened, 200_000);
        await again.RunAsync("Now close it.", observer, CancellationToken.None);
        Assert.Empty(api.Violations);
        Assert.Equal("Done.", again.History.Last().Content);
    }

    /// <summary>
    /// A context reset: the handoff note is asked for on the cached history, thinking and all, and
    /// the conversation after it (the user's messages, the note, the screen) carries no earlier
    /// thinking, so nothing is refused or dropped.
    /// </summary>
    [Fact]
    public async Task A_context_reset_starts_clean_and_the_note_reads_the_cache()
    {
        var api = new FakeAnthropic();
        api.Reply = (body, n) => n switch
        {
            0 => Message(15_000, "tool_use", null, new Thinking("Look first."), new Call("toolu_1", "screenshot", "{}")),
            1 => Message("end_turn", new Thinking("Summing up."), new Said("Handoff: took a screenshot of the desktop; next, say done.")),
            _ => Text("Done."),
        };
        var client = new OpenAiChatClient(new HttpClient(api), Address, "sk-ant-test");
        var ctx = new ToolContext { Platform = new FakePlatform(), Settings = new AgentSettings(), WorkingDirectory = Path.GetTempPath() };
        var session = new AgentSession(client, Opus, ToolRegistry.CreateDefault(true), ctx, "You are BlackBox, test prompt.", contextTokens: 20_000);
        var observer = new RecordingObserver();
        await session.RunAsync("Look at the screen.", observer, CancellationToken.None);

        Assert.Empty(api.Violations);
        Assert.Equal(0, api.DroppedThinking);
        var sent = api.Requests.Where(r => r.Path == "/v1/messages").Select(r => r.Body!).ToList();
        Assert.Equal(3, sent.Count);
        Assert.Contains(sent[1]["messages"]!.AsArray().SelectMany(m => m!["content"]!.AsArray()), b => (string?)b!["text"] == AgentSession.HandoffRequest);
        Assert.True(api.CacheReads[1] > 0); // the note was written on the cached history
        var after = (JsonArray)sent[2]["messages"]!;
        Assert.Single(after); // the user's message, the note and the screen, as one user turn
        Assert.DoesNotContain(after.SelectMany(m => m!["content"]!.AsArray()), b => (string)b!["type"]! is "thinking" or "tool_use");
        Assert.Equal("Done.", session.History.Last().Content);
    }

    /// <summary>A history changed under earlier thinking (an MCP server added mid-run) has that thinking dropped, not refused.</summary>
    [Fact]
    public async Task Thinking_after_a_changed_history_is_dropped_not_refused()
    {
        var (client, api) = Connect();
        api.Reply = (_, n) => n == 0 ? Message("end_turn", new Thinking("A plan."), new Said("Hello.")) : Text("Again.");
        var history = new List<ChatMessage> { ChatMessage.System("Prompt one."), ChatMessage.User("Hi") };
        history.Add(await client.CompleteAsync(history, [], Opus, null, CancellationToken.None));
        history.Add(ChatMessage.User("And now?"));
        history[0] = ChatMessage.System("Prompt two, with a new server.");
        await client.CompleteAsync(history, [], Opus, null, CancellationToken.None);
        Assert.Empty(api.Violations);
        Assert.Equal(1, api.DroppedThinking);

        // Where the API has no drop_block, the thinking is left out and the request sent again.
        var (old, oldApi) = Connect();
        oldApi.KnownBetas.Clear();
        oldApi.Reply = api.Reply;
        history = [ChatMessage.System("Prompt one."), ChatMessage.User("Hi")];
        history.Add(await old.CompleteAsync(history, [], Opus, null, CancellationToken.None));
        history.Add(ChatMessage.User("And now?"));
        history[0] = ChatMessage.System("Prompt two.");
        var reply = await old.CompleteAsync(history, [], Opus, null, CancellationToken.None);
        Assert.Equal("Again.", reply.Content);
        var last = oldApi.Requests.Last(r => r.Path == "/v1/messages").Body!["messages"]!.AsArray();
        Assert.DoesNotContain(last.SelectMany(m => m!["content"]!.AsArray()), b => (string)b!["type"]! == "thinking");
    }

    [Fact]
    public void Screenshots_for_Claude_fit_what_it_sees_so_clicks_land()
    {
        var platform = new FakePlatform { ScreenBounds = new(0, 0, 2560, 1080) };
        var ctx = new ToolContext { Platform = platform, Settings = new AgentSettings(), WorkingDirectory = Path.GetTempPath(), MaxScreenshotSide = ClaudeModel.FromName("claude-opus-5-5").ScreenshotSide };
        var space = ctx.GetScreenSpace();
        Assert.True(space.ImageWidth <= 2000, $"{space.ImageWidth}");
        Assert.Equal((2559, 1079), space.ToScreen(space.ImageWidth, space.ImageHeight));
        Assert.Equal(1280, ClaudeModel.FromName("claude-sonnet-4-6").ScreenshotSide);
        // Other servers get the screen as it is.
        var native = new ToolContext { Platform = platform, Settings = new AgentSettings(), WorkingDirectory = Path.GetTempPath() }.GetScreenSpace();
        Assert.Equal(2560, native.ImageWidth);
    }

    private static byte[] Jpeg(int width, int height)
    {
        using var bitmap = new SkiaSharp.SKBitmap(width, height);
        using var canvas = new SkiaSharp.SKCanvas(bitmap);
        canvas.Clear(SkiaSharp.SKColors.SteelBlue);
        using var image = SkiaSharp.SKImage.FromBitmap(bitmap);
        return image.Encode(SkiaSharp.SKEncodedImageFormat.Jpeg, 80).ToArray();
    }
}
