using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using BlackBox.Core.Agent;
using BlackBox.Core.Llm;
using BlackBox.Core.Platform;
using BlackBox.Core.Settings;
using BlackBox.Core.Tools;
using Xunit;

namespace BlackBox.Core.Tests;

public class AgentSessionTests
{
    [Fact]
    public async Task Runs_tools_until_the_model_answers()
    {
        var dir = Directory.CreateTempSubdirectory("bb-test-").FullName;
        File.WriteAllText(Path.Combine(dir, "notes.txt"), "hello");

        var server = new ScriptedServer(
            Sse.ToolCall("call_a", "list_directory", "{}"),
            Sse.Text("There is one file: notes.txt."));
        var (session, observer) = CreateSession(server, dir, new FakePlatform());

        await session.RunAsync("What is in the folder?", observer, CancellationToken.None);

        Assert.Equal(AgentStopReason.Completed, observer.StopReason);
        Assert.Equal(["list_directory"], observer.ToolsRun);
        Assert.Contains("notes.txt", observer.Results[0].Text);
        Assert.Equal(4, session.History.Count); // user, assistant(tool call), tool, assistant
        Assert.Equal(2, server.Requests.Count);

        // The second request carries the tool result back to the model.
        var messages = server.Requests[1]["messages"]!.AsArray();
        Assert.Equal("system", messages[0]!["role"]!.GetValue<string>());
        Assert.Equal("tool", messages[3]!["role"]!.GetValue<string>());
        Assert.Equal("call_a", messages[3]!["tool_call_id"]!.GetValue<string>());
    }

    [Fact]
    public async Task Bad_arguments_are_reported_to_the_model_instead_of_crashing()
    {
        var server = new ScriptedServer(
            Sse.ToolCall("c1", "read_file", "{not json"),
            Sse.Text("Sorry."));
        var (session, observer) = CreateSession(server, Path.GetTempPath(), new FakePlatform());

        await session.RunAsync("read it", observer, CancellationToken.None);

        Assert.Equal(AgentStopReason.Completed, observer.StopReason);
        Assert.True(observer.Results[0].IsError);
        Assert.Contains("not valid JSON", session.History[2].Content);
    }

    [Fact]
    public async Task Screenshots_are_sent_as_user_images_and_the_history_only_grows()
    {
        var server = new ScriptedServer(
            Sse.ToolCall("s1", "screenshot", "{}"),
            Sse.ToolCall("s2", "screenshot", "{}"),
            Sse.ToolCall("s3", "screenshot", "{}"),
            Sse.ToolCall("s4", "screenshot", "{}"),
            Sse.ToolCall("s5", "screenshot", "{}"),
            Sse.ToolCall("s6", "screenshot", "{}"),
            Sse.Text("Done."));
        var (session, observer) = CreateSession(server, Path.GetTempPath(), new FakePlatform());

        await session.RunAsync("look", observer, CancellationToken.None);

        Assert.Equal(AgentStopReason.Completed, observer.StopReason);
        Assert.Equal(6, Images(server.Requests[^1]));
        // Every request starts with the previous one unchanged, so the server's cache always holds.
        for (var i = 1; i < server.Requests.Count; i++)
        {
            var before = server.Requests[i - 1]["messages"]!.AsArray();
            var after = server.Requests[i]["messages"]!.AsArray();
            for (var m = 0; m < before.Count; m++)
                Assert.Equal(before[m]!.ToJsonString(), after[m]!.ToJsonString());
        }
    }

    private static int Images(JsonObject request) => request["messages"]!.AsArray()
        .SelectMany(m => m!["content"] is JsonArray parts ? parts : [])
        .Count(p => p!["type"]!.GetValue<string>() == "image_url");

    [Fact]
    public async Task Risky_tools_wait_for_approval_and_denial_is_reported()
    {
        var server = new ScriptedServer(
            Sse.ToolCall("w1", "write_file", """{"path":"x.txt","content":"hi"}"""),
            Sse.Text("Okay, I won't."));
        var dir = Directory.CreateTempSubdirectory("bb-test-").FullName;
        var (session, observer) = CreateSession(server, dir, new FakePlatform());
        observer.Approve = false;

        await session.RunAsync("write", observer, CancellationToken.None);

        Assert.Equal(1, observer.ApprovalRequests);
        Assert.False(File.Exists(Path.Combine(dir, "x.txt")));
        Assert.Contains("declined", session.History[2].Content);
    }

    [Fact]
    public async Task Cancelling_mid_tool_leaves_a_valid_history()
    {
        var server = new ScriptedServer(Sse.ToolCall("t1", "wait", """{"seconds":10}"""));
        var (session, observer) = CreateSession(server, Path.GetTempPath(), new FakePlatform());
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));

        await session.RunAsync("wait", observer, cts.Token);

        Assert.Equal(AgentStopReason.Cancelled, observer.StopReason);
        Assert.Equal(ChatRole.Tool, session.History[^1].Role);
        Assert.Equal("t1", session.History[^1].ToolCallId);
    }

    [Fact]
    public async Task Earlier_reasoning_is_sent_back_for_preserved_thinking()
    {
        var thought = new JsonObject { ["choices"] = new JsonArray(new JsonObject { ["delta"] = new JsonObject { ["reasoning_content"] = "I should list files." } }) };
        var server = new ScriptedServer($"data: {thought.ToJsonString()}\n\n" + Sse.ToolCall("c1", "list_directory", "{}"), Sse.Text("Done."));
        var ctx = new ToolContext { Platform = new FakePlatform(), Settings = new AgentSettings(), WorkingDirectory = Path.GetTempPath() };
        var session = new AgentSession(new OpenAiChatClient(new HttpClient(server), "http://test", null),
            new ChatRequestOptions { SendReasoning = true }, ToolRegistry.CreateDefault(true), ctx, "sys");

        await session.RunAsync("go", new RecordingObserver(), CancellationToken.None);

        var assistant = server.Requests[1]["messages"]!.AsArray().First(m => m!["role"]!.GetValue<string>() == "assistant")!;
        Assert.Equal("I should list files.", assistant["reasoning_content"]!.GetValue<string>());
    }

    [Fact]
    public async Task Context_overflow_from_the_server_resets_the_context_and_retries()
    {
        var overflow = """{"error":"The number of tokens to keep from the initial prompt is greater than the context length. Try to load the model with a larger context length"}""";
        var server = new ScriptedServer(Sse.Text("NOTE: answered four old questions."), Sse.Text("Short answer."));
        server.Failures.Enqueue((System.Net.HttpStatusCode.BadRequest, overflow));
        var ctx = new ToolContext { Platform = new FakePlatform(), Settings = new AgentSettings(), WorkingDirectory = Path.GetTempPath() };
        var session = new AgentSession(new OpenAiChatClient(new HttpClient(server), "http://test", null),
            new ChatRequestOptions { SendReasoning = true }, ToolRegistry.CreateDefault(true), ctx, "sys");
        for (var i = 0; i < 4; i++)
        {
            session.History.Add(ChatMessage.User("old question " + i));
            session.History.Add(new ChatMessage { Role = ChatRole.Assistant, Content = "old answer", Reasoning = new string('r', 8000) });
        }
        var observer = new RecordingObserver();

        await session.RunAsync("new question", observer, CancellationToken.None);

        Assert.Equal(AgentStopReason.Completed, observer.StopReason);
        Assert.Equal(3, server.Requests.Count); // rejected, handoff note, retry
        Assert.True(JsonSize(server.Requests[2]) < JsonSize(server.Requests[0]) / 4);
        Assert.Equal("Short answer.", session.History[^1].Content);

        static int JsonSize(JsonObject o) => o.ToJsonString().Length;
    }

    [Fact]
    public async Task A_wrong_assumed_context_size_is_corrected_from_the_servers_error()
    {
        var tooLong = """{"error":{"message":"This model's maximum context length is 8192 tokens. However, your messages resulted in 9100 tokens."}}""";
        var server = new ScriptedServer(Sse.Text("NOTE: nothing done yet."), Sse.Text("Short answer."));
        server.Failures.Enqueue((System.Net.HttpStatusCode.BadRequest, tooLong));
        var (session, observer) = CreateSession(server, Path.GetTempPath(), new FakePlatform(), contextTokens: 100_000);

        await session.RunAsync("question", observer, CancellationToken.None);

        Assert.Equal(AgentStopReason.Completed, observer.StopReason);
        Assert.Equal(8192, session.ContextTokens);
        Assert.Contains(observer.Notices, n => n.Contains("8k tokens, not 100k"));
    }

    [Fact]
    public async Task Fields_a_strict_server_rejects_are_dropped_once()
    {
        var server = new ScriptedServer(Sse.Text("ok"), Sse.Text("ok again"));
        server.Failures.Enqueue((System.Net.HttpStatusCode.BadRequest, """{"error":{"message":"Unrecognized request argument supplied: top_k"}}"""));
        var client = new OpenAiChatClient(new HttpClient(server), "http://test", null);
        var options = new ChatRequestOptions { Extra = new JsonObject { ["top_k"] = 40 } };

        await client.CompleteAsync([ChatMessage.User("hi")], [], options, null, CancellationToken.None);
        await client.CompleteAsync([ChatMessage.User("hi")], [], options, null, CancellationToken.None);

        Assert.Equal(3, server.Requests.Count);
        Assert.NotNull(server.Requests[0]["top_k"]);
        Assert.Null(server.Requests[1]["top_k"]);
        Assert.Null(server.Requests[2]["top_k"]); // remembered for later requests
    }

    [Fact]
    public void Middle_steps_are_dropped_last_keeping_the_task_and_recent_steps()
    {
        var ctx = new ToolContext { Platform = new FakePlatform(), Settings = new AgentSettings(), WorkingDirectory = Path.GetTempPath() };
        var session = new AgentSession(new OpenAiChatClient(new HttpClient(new ScriptedServer()), "http://test", null),
            new ChatRequestOptions(), ToolRegistry.CreateDefault(true), ctx, "sys");
        session.History.Add(ChatMessage.User("THE TASK"));
        for (var i = 0; i < 20; i++)
        {
            session.History.Add(new ChatMessage { Role = ChatRole.Assistant, Content = "", ToolCalls = [new ToolCall { Id = $"c{i}", Name = "wait", Arguments = "{}" }] });
            session.History.Add(ChatMessage.ToolResult($"c{i}", new string('x', 300)));
        }

        // About a third of the steps have to go, whatever the tool schemas weigh.
        session.FitContext(session.EstimateTokens() - 700);

        Assert.Equal("THE TASK", session.History[0].Content);
        Assert.StartsWith("[Earlier steps were removed", session.History[1].Content);
        Assert.Equal("c19", session.History[^1].ToolCallId);
        // Every remaining tool result still follows the call it answers.
        var callIds = session.History.Where(m => m.HasToolCalls).SelectMany(m => m.ToolCalls!).Select(c => c.Id).ToHashSet();
        Assert.All(session.History.Where(m => m.Role == ChatRole.Tool), m => Assert.Contains(m.ToolCallId!, callIds));
    }

    [Fact]
    public void Loop_detector_warns_on_repeats_and_unchanged_screens_but_not_on_watching()
    {
        var loops = new LoopDetector();
        var click = new ToolCall { Id = "c", Name = "mouse_click", Arguments = """{"x":1,"y":2}""" };
        var same = new ToolResult("clicked", [1, 2, 3]);

        Assert.Null(loops.Check(click, same));
        Assert.Null(loops.Check(click, same));
        var third = loops.Check(click, same);
        Assert.Contains("3 times", third);
        Assert.Contains("screen has not changed", loops.Check(click, same));

        // Watching the screen for a long time is fine.
        var watcher = new LoopDetector();
        var shot = new ToolCall { Id = "s", Name = "screenshot", Arguments = "{}" };
        for (var i = 0; i < 50; i++)
            Assert.Null(watcher.Check(shot, same));
    }

    [Fact]
    public async Task Dropped_connections_are_retried_with_the_same_request()
    {
        var server = new ScriptedServer(Sse.Text("Made it.")) { Drops = 2 };
        var ctx = new ToolContext { Platform = new FakePlatform(), Settings = new AgentSettings(), WorkingDirectory = Path.GetTempPath() };
        var session = new AgentSession(new OpenAiChatClient(new HttpClient(server), "http://test", null),
            new ChatRequestOptions(), ToolRegistry.CreateDefault(true), ctx, "sys");
        var observer = new RecordingObserver();

        await session.RunAsync("go", observer, CancellationToken.None);

        Assert.Equal(AgentStopReason.Completed, observer.StopReason);
        Assert.Equal(3, server.Requests.Count);
        Assert.Equal(server.Requests[0].ToJsonString(), server.Requests[2].ToJsonString()); // identical, so the server cache resumes
        Assert.Equal(2, observer.Notices.Count(n => n.Contains("Reconnecting")));
    }

    [Fact]
    public async Task A_tool_call_lost_to_a_drop_makes_the_model_write_in_parts()
    {
        // LM Studio sends a call's name first and its arguments only when the call is complete;
        // a proxy that cuts silent connections kills it in between, every time it is resent.
        var cut = Sse.ToolCall("c1", "write_file", "").Replace("data: [DONE]\n\n", "");
        var server = new ScriptedServer(cut, cut, cut, Sse.Text("Done."));
        var ctx = new ToolContext { Platform = new FakePlatform(), Settings = new AgentSettings(), WorkingDirectory = Path.GetTempPath() };
        var session = new AgentSession(new OpenAiChatClient(new HttpClient(server), "http://test", null),
            new ChatRequestOptions(), ToolRegistry.CreateDefault(true), ctx, "sys");
        var observer = new RecordingObserver();

        await session.RunAsync("write a game", observer, CancellationToken.None);

        Assert.Equal(AgentStopReason.Completed, observer.StopReason);
        Assert.Equal(4, server.Requests.Count);
        static string LastContent(JsonObject req) => ((JsonArray)req["messages"]!)[^1]!["content"]!.ToString();
        Assert.Equal(AgentSession.LongToolCallNote, LastContent(server.Requests[1]));
        // Still too long: one firmer note, then plain resends.
        Assert.Equal(AgentSession.StillTooLongNote, LastContent(server.Requests[2]));
        Assert.Equal(server.Requests[2].ToJsonString(), server.Requests[3].ToJsonString());
        Assert.Single(session.History, m => m.Content == AgentSession.LongToolCallNote);
        Assert.Single(session.History, m => m.Content == AgentSession.StillTooLongNote);
    }

    [Fact]
    public async Task Thinking_that_repeats_itself_is_cut_off_and_the_model_told_to_look_again()
    {
        // From a real run: the model misread the board and wrote this paragraph about 30 times.
        const string loop =
            "Hmm, wait a moment. Could it be that the key press didn't register as Up, and instead... the board changed from " +
            "[2,2,4,0] to [2,4,2,2]? What kind of move would produce that? Actually, wait. Maybe I misread the previous board. " +
            "Let me re-verify: in the previous screenshot (score 2084), column 3 was 2, 2, 4, empty. Yes, I'm confident about that.\n";
        var chunks = new StringBuilder();
        for (var i = 0; i < 30; i++)
            foreach (var word in loop.Split(' '))
                chunks.Append("data: ").Append(new JsonObject { ["choices"] = new JsonArray(new JsonObject { ["delta"] = new JsonObject { ["reasoning_content"] = word + " " } }) }.ToJsonString()).Append("\n\n");
        chunks.Append("data: [DONE]\n\n");
        var server = new ScriptedServer(chunks.ToString(), Sse.Text("Done."));
        var (session, observer) = CreateSession(server, Path.GetTempPath(), new FakePlatform());

        await session.RunAsync("play", observer, CancellationToken.None);

        Assert.Equal(AgentStopReason.Completed, observer.StopReason);
        Assert.Contains(observer.Notices, n => n.Contains("repeating"));
        Assert.Equal(2, server.Requests.Count);
        Assert.Equal(AgentSession.RepetitionNote, server.Requests[1]["messages"]!.AsArray()[^1]!["content"]!.ToString());
        Assert.DoesNotContain(session.History, m => m.Reasoning?.Contains("misread") == true);
    }

    [Fact]
    public void Repeated_board_printouts_are_not_a_loop()
    {
        var board = string.Concat(Enumerable.Repeat("8 2 8 2\n16 8 16 4\n8 32 256 2\n4 16 2 2\n", 20));
        Assert.False(RepetitionGuard.IsRepeating(board));
        Assert.False(RepetitionGuard.IsRepeating("Left merged the 2s. " + string.Concat(Enumerable.Range(0, 50).Select(i => $"Move {i}: score {i * 4}, new tile at ({i % 4},{i / 4 % 4}). "))));
    }

    /// <summary>A long run: the task, 60 steps of tool output and a final screenshot.</summary>
    private static (AgentSession, RecordingObserver) LongSession(ScriptedServer server, int contextTokens)
    {
        var ctx = new ToolContext { Platform = new FakePlatform(), Settings = new AgentSettings(), WorkingDirectory = Path.GetTempPath() };
        var session = new AgentSession(new OpenAiChatClient(new HttpClient(server), "http://test", null),
            new ChatRequestOptions(), ToolRegistry.CreateDefault(true), ctx, "sys", contextTokens: contextTokens);
        session.History.Add(new ChatMessage { Role = ChatRole.User, Content = "THE TASK", FromUser = true });
        for (var i = 0; i < 60; i++)
        {
            session.History.Add(new ChatMessage { Role = ChatRole.Assistant, Content = "", ToolCalls = [new ToolCall { Id = $"c{i}", Name = "wait", Arguments = "{}" }] });
            session.History.Add(ChatMessage.ToolResult($"c{i}", new string('x', 2000)));
        }
        session.History.Add(new ChatMessage { Role = ChatRole.User, Parts = [ContentPart.FromText("Screenshot after the last action:"), ContentPart.FromImage([7, 7, 7], "image/jpeg")] });
        return (session, new RecordingObserver());
    }

    private static string LastText(JsonObject request) => request["messages"]!.AsArray()[^1]!["content"] is JsonValue v ? v.ToString() : "";

    [Fact]
    public async Task A_full_context_is_reset_to_the_users_messages_a_handoff_note_and_the_screen()
    {
        var server = new ScriptedServer
        {
            Responder = req => LastText(req) == AgentSession.HandoffRequest
                ? Sse.Text("NOTE: opened Notepad, saved C:\\notes.txt. Next: close Notepad.")
                : Sse.Text("Done."),
        };
        var (session, observer) = LongSession(server, contextTokens: 40_000);
        var fullHistory = session.History.Count;

        await session.RunAsync("and now finish", observer, CancellationToken.None);

        Assert.Equal(AgentStopReason.Completed, observer.StopReason);
        Assert.Equal(2, server.Requests.Count);
        // The note is asked for on the whole history, with the same tools, so the server's cache holds.
        var handoff = server.Requests[0];
        Assert.Equal(fullHistory + 3, handoff["messages"]!.AsArray().Count); // system + history + "and now finish" + request
        Assert.NotNull(handoff["tools"]);
        // The next step starts from the user's messages, the note and the latest screenshot only.
        var messages = server.Requests[1]["messages"]!.AsArray();
        Assert.Equal(5, messages.Count);
        Assert.Equal("THE TASK", messages[1]!["content"]!.ToString());
        Assert.Equal("and now finish", messages[2]!["content"]!.ToString());
        Assert.StartsWith(AgentSession.HandoffHeader, messages[3]!["content"]!.ToString());
        Assert.Contains("notes.txt", messages[3]!["content"]!.ToString());
        Assert.Equal(1, Images(server.Requests[1]));
        Assert.Contains(observer.Notices, n => n.StartsWith("Context reset"));
    }

    [Fact]
    public async Task The_servers_token_count_triggers_the_reset_even_when_the_estimate_is_small()
    {
        // A short history the estimate considers tiny, but the server says the prompt was 90k tokens.
        static string WithUsage(string sse, int prompt) => sse.Replace("data: [DONE]",
            "data: " + new JsonObject { ["choices"] = new JsonArray(), ["usage"] = new JsonObject { ["prompt_tokens"] = prompt, ["completion_tokens"] = 50 } }.ToJsonString() + "\n\ndata: [DONE]");
        var server = new ScriptedServer(
            WithUsage(Sse.ToolCall("w1", "wait", """{"seconds":0}"""), 90_000),
            Sse.Text("NOTE: waited once."),
            Sse.Text("Done."));
        var ctx = new ToolContext { Platform = new FakePlatform(), Settings = new AgentSettings(), WorkingDirectory = Path.GetTempPath() };
        var session = new AgentSession(new OpenAiChatClient(new HttpClient(server), "http://test", null),
            new ChatRequestOptions(), ToolRegistry.CreateDefault(true), ctx, "sys", contextTokens: 100_000);
        var observer = new RecordingObserver();

        await session.RunAsync("wait, then finish", observer, CancellationToken.None);

        Assert.Equal(AgentStopReason.Completed, observer.StopReason);
        Assert.Equal(AgentSession.HandoffRequest, LastText(server.Requests[1]));
        Assert.Contains(session.History, m => m.Content?.Contains("waited once") == true);
    }

    [Fact]
    public async Task A_reply_cut_off_by_the_context_limit_is_redone_after_a_reset()
    {
        var cutOff = "data: " + new JsonObject { ["choices"] = new JsonArray(new JsonObject { ["delta"] = new JsonObject { ["reasoning_content"] = "Let me think about the board" }, ["finish_reason"] = "length" }) }.ToJsonString() + "\n\ndata: [DONE]\n\n";
        var server = new ScriptedServer(cutOff, Sse.Text("NOTE: playing 2048."), Sse.Text("Done."));
        var (session, observer) = CreateSession(server, Path.GetTempPath(), new FakePlatform(), contextTokens: 100_000);

        await session.RunAsync("play", observer, CancellationToken.None);

        Assert.Equal(AgentStopReason.Completed, observer.StopReason);
        Assert.Contains(observer.Notices, n => n.Contains("ran out of context"));
        Assert.Equal(AgentSession.HandoffRequest, LastText(server.Requests[1]));
        Assert.DoesNotContain(session.History, m => m.Reasoning?.Contains("board") == true);
    }

    [Fact]
    public async Task Without_a_note_from_the_model_the_reset_keeps_a_record_of_the_last_steps()
    {
        var server = new ScriptedServer(Sse.Text("Done."));
        server.Failures.Enqueue((HttpStatusCode.InternalServerError, """{"error":"boom"}"""));
        var (session, observer) = LongSession(server, contextTokens: 40_000);

        await session.RunAsync("and now finish", observer, CancellationToken.None);

        Assert.Equal(AgentStopReason.Completed, observer.StopReason);
        var note = session.History.Single(m => m.Content?.StartsWith(AgentSession.HandoffHeader) == true).Content!;
        Assert.Contains("You called wait", note);
        Assert.Equal("THE TASK", session.History[0].Content);
    }

    [Fact]
    public void Old_thinking_is_dropped_first_when_context_runs_out()
    {
        var ctx = new ToolContext { Platform = new FakePlatform(), Settings = new AgentSettings(), WorkingDirectory = Path.GetTempPath() };
        var session = new AgentSession(new OpenAiChatClient(new HttpClient(new ScriptedServer()), "http://test", null),
            new ChatRequestOptions(), ToolRegistry.CreateDefault(true), ctx, "sys");
        for (var i = 0; i < 6; i++)
            session.History.Add(new ChatMessage { Role = ChatRole.Assistant, Content = "step", Reasoning = new string('r', 20_000) });

        session.FitContext(session.EstimateTokens() - 15_000); // needs three of six thoughts dropped

        Assert.Equal(3, session.History.Count(m => m.Reasoning is not null)); // newest three kept
        Assert.NotNull(session.History[^1].Reasoning);
    }

    private static (AgentSession, RecordingObserver) CreateSession(ScriptedServer server, string dir, IPlatformServices platform, int contextTokens = 0)
    {
        var settings = new AgentSettings { ScreenshotAfterActions = false, Approval = ApprovalMode.AskRisky };
        var ctx = new ToolContext { Platform = platform, Settings = settings, WorkingDirectory = dir };
        var client = new OpenAiChatClient(new HttpClient(server), "http://test", null);
        var session = new AgentSession(client, new ChatRequestOptions(), ToolRegistry.CreateDefault(includeScreenTools: true), ctx, "system prompt", contextTokens: contextTokens);
        return (session, new RecordingObserver());
    }
}
