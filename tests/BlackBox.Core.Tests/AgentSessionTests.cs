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
    public async Task A_stop_while_a_call_waits_for_an_OK_is_not_taken_for_a_no()
    {
        // The window answers a pending OK with no when the run stops. That went into the conversation
        // as declined, and after Continue the model asked the user what to do instead.
        var server = new ScriptedServer(Sse.ToolCall("w1", "write_file", """{"path":"x.txt","content":"hi"}"""), Sse.Text("(not asked)"));
        var dir = Directory.CreateTempSubdirectory("bb-test-").FullName;
        var (session, observer) = CreateSession(server, dir, new FakePlatform());
        using var stop = new CancellationTokenSource();
        observer.Approve = false;
        observer.WhileAsked = stop.Cancel;

        await session.RunAsync("write", observer, stop.Token);

        Assert.Equal(AgentStopReason.Cancelled, observer.StopReason);
        Assert.Single(server.Requests);
        Assert.False(File.Exists(Path.Combine(dir, "x.txt")));
        Assert.Equal("Not run: the user stopped the agent.", session.History[^2].Content);
        Assert.Equal(AgentSession.StoppedNote, session.History[^1].Content);
        Assert.DoesNotContain(session.History, m => m.Content?.Contains("declined") == true);
    }

    [Fact]
    public async Task After_a_denial_the_models_question_ends_the_turn_without_a_nudge()
    {
        // Seen live: the model asked how to go on, a nudge ("Continue with the task") read as the
        // user's go-ahead, and it asked for the declined command again.
        var server = new ScriptedServer(
            Sse.ToolCall("s1", "run_shell", """{"command":"Get-ChildItem"}"""),
            Sse.Text("You declined the listing. Could you tell me how you'd like it shown? Let me know and I'll continue."),
            Sse.ToolCall("s2", "run_shell", """{"command":"dir"}"""));
        var (session, observer) = CreateSession(server, Path.GetTempPath(), new FakePlatform());
        observer.Approve = false;

        await session.RunAsync("list the folder", observer, CancellationToken.None);

        Assert.Equal(AgentStopReason.Completed, observer.StopReason);
        Assert.Equal(2, server.Requests.Count);
        Assert.Equal(1, observer.ApprovalRequests);
        Assert.Contains("Don't do the same thing another way", session.History[2].Content);
    }

    [Fact]
    public async Task Cancelling_mid_tool_leaves_a_valid_history()
    {
        var server = new ScriptedServer(Sse.ToolCall("t1", "wait", """{"seconds":10}"""));
        var (session, observer) = CreateSession(server, Path.GetTempPath(), new FakePlatform());
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));

        await session.RunAsync("wait", observer, cts.Token);

        Assert.Equal(AgentStopReason.Cancelled, observer.StopReason);
        Assert.Equal(ChatRole.Tool, session.History[^2].Role);
        Assert.Equal("t1", session.History[^2].ToolCallId);
        Assert.Equal(AgentSession.StoppedNote, session.History[^1].Content);
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
        // The conversation, not the tool list (the same in both), is what has to shrink.
        Assert.True(JsonSize(server.Requests[2]["messages"]!) < JsonSize(server.Requests[0]["messages"]!) / 4);
        Assert.Equal("Short answer.", session.History[^1].Content);

        static int JsonSize(JsonNode o) => o.ToJsonString().Length;
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
    public async Task A_request_too_large_for_the_server_resets_the_context_and_lowers_it()
    {
        // Kilo's gateway takes 20 MB: a request carrying many screenshots is refused for its size.
        var server = new ScriptedServer(Sse.Text("NOTE: answered four old questions."), Sse.Text("Short answer."));
        server.Failures.Enqueue((HttpStatusCode.RequestEntityTooLarge, "Request Entity Too Large\n\nFUNCTION_PAYLOAD_TOO_LARGE"));
        var (session, observer) = CreateSession(server, Path.GetTempPath(), new FakePlatform(), contextTokens: 100_000);
        for (var i = 0; i < 4; i++)
        {
            session.History.Add(ChatMessage.User("old question " + i));
            session.History.Add(new ChatMessage { Role = ChatRole.Assistant, Content = "old answer " + new string('a', 20_000), PromptTokens = 30_000 + i });
        }

        await session.RunAsync("new question", observer, CancellationToken.None);

        Assert.Equal(AgentStopReason.Completed, observer.StopReason);
        Assert.Equal(3, server.Requests.Count); // refused, handoff note, retry
        Assert.InRange(session.ContextTokens, 16_384, 99_999);
        Assert.Contains(observer.Notices, n => n.Contains("more than the server takes"));
        Assert.Equal("Short answer.", session.History[^1].Content);
    }

    [Fact]
    public async Task A_server_holding_requests_back_is_asked_again_after_the_wait_it_names()
    {
        const string limited = """{"error":{"message":"Rate limit exceeded for free models. Please try again later."}}""";
        var server = new ScriptedServer(Sse.Text("Made it.")) { RetryAfter = TimeSpan.FromSeconds(1) };
        server.Failures.Enqueue(((HttpStatusCode)429, limited));
        var (session, observer) = CreateSession(server, Path.GetTempPath(), new FakePlatform());
        var watch = System.Diagnostics.Stopwatch.StartNew();

        await session.RunAsync("go", observer, CancellationToken.None);

        Assert.Equal(AgentStopReason.Completed, observer.StopReason);
        Assert.Equal(2, server.Requests.Count);
        Assert.Equal(server.Requests[0].ToJsonString(), server.Requests[1].ToJsonString());
        Assert.True(watch.Elapsed >= TimeSpan.FromSeconds(0.9), $"waited {watch.Elapsed}");
        Assert.Single(observer.Notices, n => n.Contains("Rate limit exceeded for free models") && n.Contains("Trying again in 1 s"));
        Assert.DoesNotContain(observer.Notices, n => n.Contains("Reconnecting")); // not a dropped connection, nor counted as one
    }

    [Fact]
    public async Task A_tool_call_the_length_limit_cut_short_is_not_run()
    {
        // Repaired by closing what was left open, it ran: write_file wrote half the file and said done.
        var dir = Directory.CreateTempSubdirectory("bb-cut-").FullName;
        var server = new ScriptedServer(
            Sse.CutToolCall("c1", "write_file", """{"path": "notes.txt", "content": "the first half of a long fi"""),
            Sse.ToolCall("c2", "write_file", """{"path": "notes.txt", "content": "a short first part"}"""),
            Sse.Text("Done."));
        var (session, observer) = CreateSession(server, dir, new FakePlatform());
        try
        {
            await session.RunAsync("write the notes", observer, TestContext.Current.CancellationToken);

            Assert.Equal(AgentStopReason.Completed, observer.StopReason);
            Assert.Equal(["write_file"], observer.ToolsRun);
            Assert.Equal("a short first part", await File.ReadAllTextAsync(Path.Combine(dir, "notes.txt"), TestContext.Current.CancellationToken));
            Assert.Contains(observer.Notices, n => n.Contains("cut a tool call short"));
            var told = server.Requests[1]["messages"]!.AsArray()[^1]!;
            Assert.Contains("cut off by the server's limit", (string?)told["content"]);
            // The cut call isn't in the conversation, which stays a valid request.
            Assert.DoesNotContain("c1", server.Requests[1].ToJsonString());
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task A_repaired_call_goes_back_to_the_server_as_whole_json()
    {
        // Sent back as it came, arguments that aren't JSON can make a chat template fail every later request.
        var server = new ScriptedServer(Sse.ToolCall("c1", "list_directory", """{'path': '.'}"""), Sse.Text("Done."));
        var (session, observer) = CreateSession(server, Path.GetTempPath(), new FakePlatform());

        await session.RunAsync("look", observer, TestContext.Current.CancellationToken);

        Assert.Equal(["list_directory"], observer.ToolsRun);
        var call = server.Requests[1]["messages"]!.AsArray().Single(m => m!["tool_calls"] is not null)!["tool_calls"]![0]!;
        Assert.Equal(""".""", JsonNode.Parse(call["function"]!["arguments"]!.GetValue<string>())!["path"]!.ToJsonString().Trim('"'));
    }

    [Fact]
    public void Without_a_wait_named_the_waits_grow_to_five_minutes()
    {
        Assert.Equal(TimeSpan.FromSeconds(2), AgentSession.RateLimitWait(null, 1)); // a server that was only busy
        Assert.Equal(TimeSpan.FromSeconds(10), AgentSession.RateLimitWait(null, 2));
        Assert.Equal(TimeSpan.FromMinutes(5), AgentSession.RateLimitWait(null, 6));
        Assert.Equal(TimeSpan.FromMinutes(5), AgentSession.RateLimitWait(null, 40)); // an hourly allowance used up
        Assert.Equal(TimeSpan.FromSeconds(1), AgentSession.RateLimitWait(TimeSpan.Zero, 1));
        Assert.Equal(TimeSpan.FromHours(1), AgentSession.RateLimitWait(TimeSpan.FromHours(8), 1)); // asked again within the hour
        // A server that goes on saying "in a second" isn't asked once a second for as long as it says no.
        Assert.Equal(TimeSpan.FromSeconds(10), AgentSession.RateLimitWait(TimeSpan.FromSeconds(1), 2));
        Assert.Equal(TimeSpan.FromMinutes(1), AgentSession.RateLimitWait(TimeSpan.Zero, 9));
        Assert.Equal(TimeSpan.FromMinutes(30), AgentSession.RateLimitWait(TimeSpan.FromMinutes(30), 3));
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
    public async Task A_server_that_cannot_be_reached_is_waited_for_and_the_task_goes_on()
    {
        // Twelve quick tries were over in half a minute: an unattended task ended on a short outage
        // (the server's PC restarting, the VPN reconnecting). Waits like these go on until Stop.
        var server = new ScriptedServer(Sse.Text("Made it.")) { Unreachable = 1 };
        var (session, observer) = CreateSession(server, Path.GetTempPath(), new FakePlatform());

        await session.RunAsync("go", observer, TestContext.Current.CancellationToken);

        Assert.Equal(AgentStopReason.Completed, observer.StopReason);
        Assert.Single(server.Requests);
        Assert.Contains(observer.Notices, n => n.StartsWith("Could not reach http://test/v1:") && n.EndsWith("Trying again in 2 s (Stop ends the wait)…"));
        Assert.DoesNotContain(observer.Notices, n => n.Contains("Reconnecting"));
    }

    [Fact]
    public async Task A_bug_that_ends_a_run_is_handed_over_whole_and_a_servers_error_is_not()
    {
        // "Object reference not set to an instance of an object." alone tells nobody where.
        var bug = new InvalidOperationException("a bug");
        var server = new ScriptedServer { Responder = _ => throw bug };
        var (session, observer) = CreateSession(server, Path.GetTempPath(), new FakePlatform());

        await session.RunAsync("go", observer, TestContext.Current.CancellationToken);

        Assert.Equal(AgentStopReason.Error, observer.StopReason);
        Assert.Equal("a bug", observer.Detail);
        Assert.Same(bug, observer.Failed);

        var refused = new ScriptedServer();
        refused.Failures.Enqueue((HttpStatusCode.BadRequest, "no such model"));
        (session, observer) = CreateSession(refused, Path.GetTempPath(), new FakePlatform());
        await session.RunAsync("go", observer, TestContext.Current.CancellationToken);
        Assert.Equal(AgentStopReason.Error, observer.StopReason);
        Assert.Null(observer.Failed);
    }

    [Fact]
    public async Task A_gateways_model_server_failing_mid_stream_is_asked_again_with_the_prompt_a_line_lower()
    {
        // Kilo answers 200 and then says in the stream that the model server behind it failed. That
        // ended the run; and for half an hour Kilo's free Nemotron failed every request that began
        // with BlackBox's prompt, while one with a blank line in front got through in seconds.
        var server = new ScriptedServer(
            """data: {"error":{"message":"Upstream idle timeout exceeded"}}""" + "\n\n",
            Sse.Text("Made it."));
        var client = new OpenAiChatClient(new HttpClient(server), "http://stuck-gateway.test", null);
        var ctx = new ToolContext { Platform = new FakePlatform(), Settings = new AgentSettings(), WorkingDirectory = Path.GetTempPath() };
        var session = new AgentSession(client, new ChatRequestOptions(), ToolRegistry.CreateDefault(true), ctx, "sys");
        var observer = new RecordingObserver();

        await session.RunAsync("go", observer, TestContext.Current.CancellationToken);

        Assert.Equal(AgentStopReason.Completed, observer.StopReason);
        Assert.Equal(2, server.Requests.Count);
        Assert.Equal("sys", server.Requests[0]["messages"]![0]!["content"]!.GetValue<string>());
        Assert.Equal("\nsys", server.Requests[1]["messages"]![0]!["content"]!.GetValue<string>());
        Assert.Contains(observer.Notices, n => n.Contains("Upstream idle timeout exceeded") && n.Contains("Reconnecting"));
        // The requests after it begin the same way, so the server's cache holds again.
        await session.RunAsync("and again", observer, TestContext.Current.CancellationToken);
        Assert.Equal("\nsys", server.Requests[2]["messages"]![0]!["content"]!.GetValue<string>());
    }

    [Theory]
    [InlineData(null, "Upstream error from Nvidia: Internal server error", true)]
    [InlineData("502", "Bad gateway", true)]
    [InlineData(null, "The model is overloaded, try again later", true)]
    [InlineData("400", "Invalid tool schema", false)]
    [InlineData(null, "Your account has no access to this model", false)]
    public void Errors_in_a_stream_are_sent_again_when_they_are_the_servers_own(string? code, string message, bool again) =>
        Assert.Equal(again, OpenAiChatClient.Transient(code, message));

    [Fact]
    public async Task Server_errors_and_busy_answers_are_sent_again_like_drops()
    {
        var server = new ScriptedServer(Sse.Text("Made it."));
        server.Failures.Enqueue((HttpStatusCode.BadGateway, "upstream connection reset"));
        server.Failures.Enqueue(((HttpStatusCode)429, """{"error":"busy"}"""));
        // LM Studio's answer to a request whose upload stalled for five minutes.
        server.Failures.Enqueue((HttpStatusCode.RequestTimeout, ""));
        var (session, observer) = CreateSession(server, Path.GetTempPath(), new FakePlatform());

        await session.RunAsync("go", observer, CancellationToken.None);

        Assert.Equal(AgentStopReason.Completed, observer.StopReason);
        Assert.Equal(4, server.Requests.Count);
        Assert.Equal(server.Requests[0].ToJsonString(), server.Requests[3].ToJsonString());
        Assert.Contains(observer.Notices, n => n.Contains("answered 502"));
        Assert.Contains(observer.Notices, n => n.Contains("answered 408"));
    }

    [Fact]
    public async Task An_upload_that_stops_getting_through_is_cut_and_sent_again()
    {
        // Over a VPN a request with screenshots stopped getting through mid-upload, and nothing
        // happened until LM Studio gave up on it five minutes later.
        var server = new ScriptedServer(Sse.Text("Made it.")) { StalledUploads = 1 };
        var client = new OpenAiChatClient(new HttpClient(server), "http://test", null) { UploadStallLimit = TimeSpan.FromMilliseconds(300) };
        var ctx = new ToolContext { Platform = new FakePlatform(), Settings = new AgentSettings(), WorkingDirectory = Path.GetTempPath() };
        var session = new AgentSession(client, new ChatRequestOptions(), ToolRegistry.CreateDefault(true), ctx, "sys");
        var observer = new RecordingObserver();

        await session.RunAsync("go", observer, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);

        Assert.Equal(AgentStopReason.Completed, observer.StopReason);
        Assert.Single(server.Requests); // the one that got through
        Assert.Contains(observer.Notices, n => n.Contains("stalled"));
        // The window shows how much of the request is out; the retry starts again from nothing.
        Assert.Equal(0, observer.Sending[0].Sent);
        Assert.Equal(observer.Sending[^1].Total, observer.Sending[^1].Sent);
        Assert.Contains(observer.Sending.Skip(1), s => s.Sent == 0);
    }

    [Fact]
    public async Task An_error_answer_cut_off_while_it_comes_in_is_sent_again_like_a_drop()
    {
        // Seen over a VPN: the run ended on "Error while copying content to a stream." instead of reconnecting.
        var server = new ScriptedServer(Sse.Text("Made it.")) { BrokenErrors = 1 };
        var (session, observer) = CreateSession(server, Path.GetTempPath(), new FakePlatform());

        await session.RunAsync("go", observer, CancellationToken.None);

        Assert.Equal(AgentStopReason.Completed, observer.StopReason);
        Assert.Equal(2, server.Requests.Count);
        Assert.Equal(server.Requests[0].ToJsonString(), server.Requests[1].ToJsonString());
        Assert.Contains(observer.Notices, n => n.Contains("Reconnecting"));
    }

    /// <summary>The note a message sent during the run comes with, naming the request it came in on.</summary>
    private static string SentDuring(string request) =>
        $"[Sent while you were working on \"{request}\". Answer it or do what it asks, then carry on with that unless it changes or cancels it.]\n";

    [Fact]
    public async Task A_message_sent_during_a_run_reaches_the_model_before_its_next_step()
    {
        AgentSession? session = null;
        var replies = new Queue<string>([Sse.ToolCall("w1", "wait", """{"seconds":0}"""), Sse.Text("Made it say hello world."), Sse.Text("That was all.")]);
        var server = new ScriptedServer
        {
            Responder = _ =>
            {
                // The user writes while the model is busy with its first step.
                if (replies.Count == 3) session!.Steer("Actually make it say hello world");
                return replies.Dequeue();
            },
        };
        var (s, observer) = CreateSession(server, Path.GetTempPath(), new FakePlatform());
        session = s;

        await session.RunAsync("Write hello into the file", observer, CancellationToken.None);

        Assert.Equal(AgentStopReason.Completed, observer.StopReason);
        Assert.Equal(3, server.Requests.Count);
        Assert.Equal(SentDuring("Write hello into the file") + "Actually make it say hello world", LastText(server.Requests[1]));
        Assert.Contains(session.History, m => m.FromUser && m.Content!.EndsWith("\nActually make it say hello world"));
        Assert.Empty(session.TakeUndelivered());
    }

    [Fact]
    public async Task A_message_sent_while_the_model_writes_its_answer_keeps_the_run_going()
    {
        AgentSession? session = null;
        var replies = new Queue<string>([Sse.Text("Done: the file says hello."), Sse.Text("Changed it to hello world."), Sse.Text("That was all.")]);
        var server = new ScriptedServer
        {
            Responder = _ =>
            {
                if (replies.Count == 3) session!.Steer("Make it hello world instead");
                return replies.Dequeue();
            },
        };
        var (s, observer) = CreateSession(server, Path.GetTempPath(), new FakePlatform());
        session = s;

        await session.RunAsync("Write hello into the file", observer, CancellationToken.None);

        Assert.Equal(AgentStopReason.Completed, observer.StopReason);
        Assert.Equal(3, server.Requests.Count);
        Assert.Equal(SentDuring("Write hello into the file") + "Make it hello world instead", LastText(server.Requests[1]));
    }

    [Fact]
    public async Task A_question_sent_during_a_task_is_answered_and_the_task_goes_on()
    {
        // As in a run of the user's: asked mid-task why something had failed, while it was trying
        // Pony Diffusion for a picture, the model answered the question and ended the run there.
        AgentSession? session = null;
        var replies = new Queue<string>([
            Sse.ToolCall("m1", "wait", """{"seconds":0}"""),
            Sse.Text("Edge quit because another Edge held its profile."),
            Sse.ToolCall("m2", "wait", """{"seconds":0}"""),
            Sse.Text("Made the picture with Pony Diffusion."),
        ]);
        var server = new ScriptedServer
        {
            Responder = _ =>
            {
                if (replies.Count == 4) session!.Steer("Can you analyze what caused the problem starting up Edge?");
                return replies.Dequeue();
            },
        };
        var (s, observer) = CreateSession(server, Path.GetTempPath(), new FakePlatform());
        session = s;

        await session.RunAsync("Try using pony diffusion", observer, CancellationToken.None);

        Assert.Equal(AgentStopReason.Completed, observer.StopReason);
        Assert.Equal(4, server.Requests.Count);
        // The question names the task it came in on...
        Assert.Equal(SentDuring("Try using pony diffusion") + "Can you analyze what caused the problem starting up Edge?", LastText(server.Requests[1]));
        // ...and with it answered, the model is asked about the task before the run can end.
        var reminder = LastText(server.Requests[2]);
        Assert.StartsWith("[BlackBox] Before you finish: the user's message came in while you were working on \"Try using pony diffusion\".", reminder);
        // Once: the run then ends with the picture.
        Assert.Equal("Made the picture with Pony Diffusion.", session.History[^1].Content);
    }

    [Fact]
    public async Task A_run_that_only_says_continue_is_named_by_the_request_before_it()
    {
        AgentSession? session = null;
        var replies = new Queue<string>([Sse.Text("Started the poster."), Sse.ToolCall("w1", "wait", """{"seconds":0}"""), Sse.Text("Made it blue."), Sse.Text("The poster is done.")]);
        var server = new ScriptedServer
        {
            Responder = _ =>
            {
                if (replies.Count == 3) session!.Steer("Make it blue");
                return replies.Dequeue();
            },
        };
        var (s, observer) = CreateSession(server, Path.GetTempPath(), new FakePlatform());
        session = s;

        await session.RunAsync("Make a poster for the bake sale", observer, CancellationToken.None);
        await session.RunAsync("Continue.", observer, CancellationToken.None);

        Assert.Equal(SentDuring("Make a poster for the bake sale") + "Make it blue", LastText(server.Requests[2]));
    }

    [Fact]
    public async Task A_message_ends_a_long_wait_and_one_sent_after_the_run_is_handed_back()
    {
        AgentSession? session = null;
        var server = new ScriptedServer(Sse.ToolCall("w1", "wait", """{"seconds":120}"""), Sse.Text("Okay, stopping the watch."), Sse.Text("Nothing else to do."));
        var (s, observer) = CreateSession(server, Path.GetTempPath(), new FakePlatform());
        session = s;
        _ = Task.Delay(500, TestContext.Current.CancellationToken).ContinueWith(_ => session.Steer("You can stop watching now"), TestContext.Current.CancellationToken);
        var clock = System.Diagnostics.Stopwatch.StartNew();

        await session.RunAsync("Watch the chat for two minutes", observer, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(20), $"the wait went on for {clock.Elapsed}");
        Assert.Contains("the user sent you a message", observer.Results[0].Text);
        Assert.Equal(SentDuring("Watch the chat for two minutes") + "You can stop watching now", LastText(server.Requests[1]));

        // Too late for this run: the app sends it on as the next one.
        session.Steer("And thanks");
        Assert.Equal(["And thanks"], session.TakeUndelivered().Select(u => u.Text));
    }

    /// <summary>A small PNG and a text file in a new folder, as the user would drop them on the window.</summary>
    private static (string Dir, string Picture, string Text) Dropped()
    {
        var dir = Directory.CreateTempSubdirectory("bb-drop-").FullName;
        var picture = Path.Combine(dir, "cat photo.png");
        using (var bitmap = new SkiaSharp.SKBitmap(96, 64))
        {
            bitmap.Erase(SkiaSharp.SKColors.OrangeRed);
            using var data = bitmap.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
            File.WriteAllBytes(picture, data.ToArray());
        }
        var text = Path.Combine(dir, "notes.txt");
        File.WriteAllText(text, "hello");
        return (dir, picture, text);
    }

    private static List<JsonObject> Parts(JsonObject request) =>
        request["messages"]!.AsArray()[^1]!["content"] is JsonArray parts ? parts.Select(p => p!.AsObject()).ToList() : [];

    [Fact]
    public async Task Files_sent_with_a_message_reach_the_model_by_path_and_its_pictures_are_shown()
    {
        var (dir, picture, text) = Dropped();
        var server = new ScriptedServer(Sse.Text("It is an orange-red rectangle."));
        var (session, observer) = CreateSession(server, dir, new FakePlatform());

        await session.RunAsync(new UserInput("What is in these?", [picture, text, dir]), observer, CancellationToken.None);

        Assert.Equal(AgentStopReason.Completed, observer.StopReason);
        var parts = Parts(server.Requests[0]);
        var list = parts[0]["text"]!.GetValue<string>();
        Assert.StartsWith("What is in these?\n\n[The user attached these files:]", list);
        Assert.Contains($"- {picture} (a 96x64 picture, shown below)", list);
        Assert.Contains($"- {text} (a .txt file, 5 bytes)", list);
        Assert.Contains($"- {dir} (a folder)", list);
        // The picture comes labelled with its path, so the model knows which file it is.
        Assert.Equal(picture + ":", parts[1]["text"]!.GetValue<string>());
        Assert.Equal("image_url", parts[2]["type"]!.GetValue<string>());
        Assert.Equal(3, parts.Count);
        Assert.True(session.History[0].FromUser);
    }

    [Fact]
    public async Task A_model_that_cannot_see_gets_only_the_paths_of_attached_pictures()
    {
        var (dir, picture, _) = Dropped();
        var server = new ScriptedServer(Sse.Text("ok"));
        var settings = new AgentSettings { ScreenshotAfterActions = false };
        var ctx = new ToolContext { Platform = new FakePlatform(), Settings = settings, WorkingDirectory = dir, SeesImages = false };
        var client = new OpenAiChatClient(new HttpClient(server), "http://test", null);
        var session = new AgentSession(client, new ChatRequestOptions(), ToolRegistry.CreateDefault(includeScreenTools: false), ctx, "system prompt");

        await session.RunAsync(new UserInput("", [picture]), new RecordingObserver(), CancellationToken.None);

        var text = LastText(server.Requests[0]);
        Assert.StartsWith($"[The user attached this file:]\n- {picture} (picture 96x64, ", text);
        Assert.EndsWith(" bytes)", text);
        Assert.Equal(0, Images(server.Requests[0]));
    }

    [Fact]
    public async Task A_picture_sent_during_a_run_reaches_the_model_with_the_message()
    {
        var (dir, picture, _) = Dropped();
        AgentSession? session = null;
        var replies = new Queue<string>([Sse.ToolCall("w1", "wait", """{"seconds":0}"""), Sse.Text("Using that one."), Sse.Text("That was all.")]);
        var server = new ScriptedServer
        {
            Responder = _ =>
            {
                if (replies.Count == 3) session!.Steer(new UserInput("Use this picture instead", [picture]));
                return replies.Dequeue();
            },
        };
        var (s, observer) = CreateSession(server, dir, new FakePlatform());
        session = s;

        await session.RunAsync("Make a poster", observer, CancellationToken.None);

        // After the wait's screenshot comes the message, with its picture.
        var parts = Parts(server.Requests[1]);
        Assert.StartsWith(SentDuring("Make a poster") + "Use this picture instead\n\n[The user attached this file:]", parts[0]["text"]!.GetValue<string>());
        Assert.Single(parts, p => p["type"]!.GetValue<string>() == "image_url");
        Assert.Empty(session.TakeUndelivered());
    }

    [Fact]
    public void A_saved_chat_keeps_the_paths_of_attached_pictures_but_not_the_pictures()
    {
        var (_, picture, _) = Dropped();
        var message = new UserInput("Brighter, please", [picture]).ToMessage(seesImages: true);
        Assert.NotNull(message.Parts);

        var saved = Chats.ChatStore.ForSaving([message]).Single();

        Assert.Null(saved.Parts);
        Assert.True(saved.FromUser);
        Assert.Contains(picture, saved.Content);
        Assert.Contains("read_file shows them again", saved.Content);
        Assert.DoesNotContain("Screenshot", saved.Content);
    }

    [Fact]
    public async Task Clicks_and_keys_wait_while_the_user_types_to_the_agent()
    {
        var typing = System.Diagnostics.Stopwatch.StartNew();
        var platform = new FakePlatform();
        var ctx = new ToolContext
        {
            Platform = platform,
            Settings = new AgentSettings { ScreenshotAfterActions = false },
            WorkingDirectory = Path.GetTempPath(),
            UserComposing = () => typing.Elapsed < TimeSpan.FromMilliseconds(700),
        };

        await new PressKeysTool().ExecuteAsync(new JsonObject { ["keys"] = "enter" }, ctx, CancellationToken.None);

        Assert.True(typing.Elapsed >= TimeSpan.FromMilliseconds(700), $"pressed after {typing.ElapsedMilliseconds} ms of typing");
        Assert.NotEmpty(platform.Actions);
    }

    [Fact]
    public async Task A_click_waits_for_typing_that_started_while_its_target_was_looked_for()
    {
        // The look is a request to the model, seconds long: the user isn't typing when the click
        // starts, and is by the time it is aimed.
        System.Diagnostics.Stopwatch? typing = null;
        TimeSpan? clicked = null;
        var platform = new FakePlatform();
        platform.OnClick = _ => clicked = typing?.Elapsed;
        var ctx = new ToolContext
        {
            Platform = platform,
            Settings = new AgentSettings { ScreenshotAfterActions = false },
            WorkingDirectory = Path.GetTempPath(),
            UserComposing = () =>
            {
                if (typing is not null)
                    return typing.Elapsed < TimeSpan.FromMilliseconds(700);
                typing = System.Diagnostics.Stopwatch.StartNew();
                return false;
            },
        };

        await new MouseClickTool().ExecuteAsync(new JsonObject { ["target"] = "OK button", ["x"] = 50, ["y"] = 50 }, ctx, CancellationToken.None);

        Assert.NotNull(clicked);
        Assert.True(clicked >= TimeSpan.FromMilliseconds(700), $"clicked {clicked?.TotalMilliseconds} ms into the typing");
    }

    [Fact]
    public async Task Typing_that_starts_during_a_series_of_presses_holds_the_rest_and_gives_the_keyboard_back()
    {
        var platform = new FakePlatform();
        platform.HostWindow = new FocusLog(platform.Actions);
        var ctx = TypingStartsAfter(platform, "keys", done: 2);
        var clock = System.Diagnostics.Stopwatch.StartNew();

        await new PressKeysTool().ExecuteAsync(new JsonObject { ["sequence"] = new JsonArray("a", "b", "c", "d"), ["gap_ms"] = 0 }, ctx, CancellationToken.None);

        // The user clicked into BlackBox's box to type, so the rest goes back to the window it was for once they are done.
        Assert.Equal(["yield", "keys a", "keys b", "yield", "keys c", "keys d"], platform.Actions);
        Assert.True(clock.Elapsed >= TimeSpan.FromMilliseconds(500), $"over after {clock.ElapsedMilliseconds} ms");
    }

    [Fact]
    public async Task Typing_that_starts_while_long_text_is_typed_holds_the_rest_and_gives_the_keyboard_back()
    {
        var platform = new FakePlatform();
        platform.HostWindow = new FocusLog(platform.Actions);
        var ctx = TypingStartsAfter(platform, "type", done: 1);
        string first = new('x', TypeTextTool.Chunk), second = new('y', TypeTextTool.Chunk);

        await new TypeTextTool().ExecuteAsync(new JsonObject { ["text"] = first + second + "z" }, ctx, CancellationToken.None);

        Assert.Equal(["yield", "type " + first, "yield", "type " + second, "type z"], platform.Actions);
    }

    [Fact]
    public async Task Stop_ends_long_text_between_its_pieces()
    {
        // It went on typing the rest after Stop, Enter for every line break, into whatever had the focus.
        using var stop = new CancellationTokenSource();
        var platform = new FakePlatform { OnType = _ => stop.Cancel() };
        var ctx = new ToolContext { Platform = platform, Settings = new AgentSettings { ScreenshotAfterActions = false }, WorkingDirectory = Path.GetTempPath() };
        var text = new string('x', TypeTextTool.Chunk * 3);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new TypeTextTool().ExecuteAsync(new JsonObject { ["text"] = text }, ctx, stop.Token));

        Assert.Single(platform.Actions, a => a.StartsWith("type "));
    }

    /// <summary>A context in which the user types to the agent for half a second once <paramref name="done"/> actions of a kind ("keys", "type") are done.</summary>
    private static ToolContext TypingStartsAfter(FakePlatform platform, string kind, int done)
    {
        System.Diagnostics.Stopwatch? typing = null;
        return new ToolContext
        {
            Platform = platform,
            Settings = new AgentSettings { ScreenshotAfterActions = false },
            WorkingDirectory = Path.GetTempPath(),
            UserComposing = () =>
            {
                if (typing is null && platform.Actions.Count(a => a.StartsWith(kind + " ")) == done)
                    typing = System.Diagnostics.Stopwatch.StartNew();
                return typing is { } t && t.Elapsed < TimeSpan.FromMilliseconds(500);
            },
        };
    }

    /// <summary>BlackBox's window, noting among the actions each time it hands the keyboard to the window under it.</summary>
    private sealed class FocusLog(List<string> actions) : IHostWindow
    {
        public void Attach(nint handle) { }
        public IDisposable StepAside(params (int X, int Y)[] points) => NoHostWindow.Instance.StepAside(points);
        public void YieldFocus() => actions.Add("yield");
    }

    [Fact]
    public async Task A_tool_that_fails_in_an_unexpected_way_reports_it_and_the_run_goes_on()
    {
        var server = new ScriptedServer(Sse.ToolCall("b1", "broken", "{}"), Sse.Text("I'll try something else."));
        var ctx = new ToolContext { Platform = new FakePlatform(), Settings = new AgentSettings(), WorkingDirectory = Path.GetTempPath() };
        var session = new AgentSession(new OpenAiChatClient(new HttpClient(server), "http://test", null),
            new ChatRequestOptions(), new ToolRegistry([new ThrowingTool()]), ctx, "sys");
        var observer = new RecordingObserver();

        await session.RunAsync("go", observer, CancellationToken.None);

        Assert.Equal(AgentStopReason.Completed, observer.StopReason);
        Assert.True(observer.Results[0].IsError);
        Assert.Contains("the format was odd", observer.Results[0].Text);
    }

    private sealed class ThrowingTool : Tool
    {
        public override string Name => "broken";
        public override string Description => "Always throws.";
        public override ToolRisk Risk => ToolRisk.ReadOnly;
        public override JsonObject Parameters => Schema();
        public override Task<ToolResult> ExecuteAsync(JsonObject args, ToolContext ctx, CancellationToken ct) => throw new FormatException("the format was odd");
    }

    [Fact]
    public async Task A_reply_cut_by_the_servers_length_cap_is_continued_not_reset()
    {
        // 6k of a 100k context: the server stopped at its reply-length limit, not at the context's end.
        var capped = "data: " + new JsonObject { ["choices"] = new JsonArray(new JsonObject { ["delta"] = new JsonObject { ["reasoning_content"] = "Long thoughts" }, ["finish_reason"] = "length" }) }.ToJsonString() +
                     "\n\ndata: " + new JsonObject { ["choices"] = new JsonArray(), ["usage"] = new JsonObject { ["prompt_tokens"] = 2_000, ["completion_tokens"] = 4_096 } }.ToJsonString() + "\n\ndata: [DONE]\n\n";
        var server = new ScriptedServer(capped, Sse.Text("Done."));
        var (session, observer) = CreateSession(server, Path.GetTempPath(), new FakePlatform(), contextTokens: 100_000);

        await session.RunAsync("think hard", observer, CancellationToken.None);

        Assert.Equal(AgentStopReason.Completed, observer.StopReason);
        Assert.Equal(2, server.Requests.Count); // no handoff request in between
        Assert.Contains(observer.Notices, n => n.Contains("length limit"));
        Assert.Contains(session.History, m => m.Reasoning == "Long thoughts"); // kept, and the model asked to go on
        Assert.StartsWith("[BlackBox] Continue with the task", LastText(server.Requests[1]));
    }

    [Fact]
    public async Task Nudges_start_over_once_the_model_is_moving_again()
    {
        // Three stalls in one run, with steps in between: each one gets its nudge.
        var server = new ScriptedServer(
            Sse.Text("Let me look."), Sse.ToolCall("w1", "wait", """{"seconds":0}"""),
            Sse.Text("Let me look again."), Sse.ToolCall("w2", "wait", """{"seconds":0}"""),
            Sse.Text("Let me check once more."), Sse.ToolCall("w3", "wait", """{"seconds":0}"""),
            Sse.Text("Done."));
        var (session, observer) = CreateSession(server, Path.GetTempPath(), new FakePlatform());

        await session.RunAsync("go", observer, CancellationToken.None);

        Assert.Equal(AgentStopReason.Completed, observer.StopReason);
        Assert.Equal(7, server.Requests.Count);
        Assert.Equal("Done.", session.History[^1].Content);
    }

    [Fact]
    public async Task Only_the_result_whose_screenshot_follows_says_so()
    {
        var calls = new JsonArray(
            new JsonObject { ["index"] = 0, ["id"] = "a", ["function"] = new JsonObject { ["name"] = "screenshot", ["arguments"] = "{}" } },
            new JsonObject { ["index"] = 1, ["id"] = "b", ["function"] = new JsonObject { ["name"] = "screenshot", ["arguments"] = "{}" } });
        var both = $"data: {new JsonObject { ["choices"] = new JsonArray(new JsonObject { ["delta"] = new JsonObject { ["tool_calls"] = calls } }) }.ToJsonString()}\n\ndata: [DONE]\n\n";
        var server = new ScriptedServer(both, Sse.Text("Done."));
        var (session, observer) = CreateSession(server, Path.GetTempPath(), new FakePlatform());

        await session.RunAsync("look twice", observer, CancellationToken.None);

        var results = session.History.Where(m => m.Role == ChatRole.Tool).ToList();
        Assert.DoesNotContain("attached", results[0].Content);
        Assert.EndsWith("(The screenshot is attached in the next message.)", results[1].Content);
        Assert.Equal(1, Images(server.Requests[1]));
    }

    [Fact]
    public void The_system_prompt_is_the_same_all_day()
    {
        // It starts every request; a clock in it would make the server reread a whole chat at each message.
        var prompt = SystemPrompt.Build("Test OS", null, "C:\\", "");
        Assert.Contains(DateTime.Now.ToString("d MMMM yyyy", System.Globalization.CultureInfo.InvariantCulture), prompt);
        Assert.DoesNotMatch(@"\d{1,2}:\d{2}", prompt);
    }

    [Fact]
    public void What_nothing_on_the_PC_opens_gets_a_tool_in_BlackBoxs_own_folder()
    {
        // The user's rule, after a PDF was decoded by hand in PowerShell: fetch a portable tool or install the program.
        var prompt = SystemPrompt.Build("Test OS", null, "C:\\", "", webSearch: true, toolsDirectory: @"D:\BlackBox\Data\tools");
        Assert.Contains(@"download a portable tool for it into D:\BlackBox\Data\tools (an official build; look it up with web_search), or install the program", prompt);
        Assert.Contains("Accept license agreements", prompt);
        // A reminder whose process died on a quoting error was reported as set.
        Assert.Contains("check that it is running before you say it's set up", prompt);
        Assert.Contains("Make Word, Excel, PowerPoint and PDF files with write_file", prompt);
        Assert.Contains("add the server with mcp_servers and use its tools", prompt);
        // A server it uses goes into Settings, not run once from a script and forgotten.
        Assert.Contains("Always go through mcp_servers, which saves the server in the user's Settings", prompt);
        // Helper files go to BlackBox's own folder, not the user's (a reminder script landed in the home folder).
        Assert.Contains(@"go in D:\BlackBox\Data\scratch, not in the user's folders",
            SystemPrompt.Build("Test OS", null, "C:\\", "", toolsDirectory: @"D:\BlackBox\Data\tools", scratchDirectory: @"D:\BlackBox\Data\scratch"));
        Assert.DoesNotContain("web_search", SystemPrompt.Build("Test OS", null, "C:\\", "", toolsDirectory: @"D:\BlackBox\Data\tools"));
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
    public async Task A_reset_shows_the_last_screenshot_as_the_screen_not_the_last_picture()
    {
        // A picture read_file opened, or one the user attached (kept with their message anyway),
        // was put after the note as "The screen right now:".
        var server = new ScriptedServer { Responder = _ => Sse.Text("NOTE: looked at cat.png.") };
        var (session, observer) = LongSession(server, contextTokens: 40_000);
        session.History.Add(new ChatMessage { Role = ChatRole.User, Parts = [ContentPart.FromText("The picture cat.png:"), ContentPart.FromImage([8, 8, 8], "image/jpeg")] });
        session.History.Add(new ChatMessage { Role = ChatRole.User, Parts = [ContentPart.FromText("Like this one"), ContentPart.FromImage([9, 9, 9], "image/jpeg")], FromUser = true });

        await session.ResetContextAsync(observer, TestContext.Current.CancellationToken);

        Assert.Equal("The screen right now:", session.History[^1].Parts![0].Text);
        Assert.Equal([7, 7, 7], session.History[^1].Parts![1].ImageBytes);
        Assert.Equal([[9, 9, 9], [7, 7, 7]], session.History.SelectMany(m => m.Parts ?? []).Where(p => p.IsImage).Select(p => p.ImageBytes!));
    }

    [Fact]
    public async Task Where_the_user_stopped_a_task_is_said_in_the_conversation_and_kept_through_a_reset()
    {
        // Stopped in Paint and given a story to write next, the model wrote it and then "returned
        // to the Paint task, which is still incomplete"; its handoff note carried Paint on after a reset.
        var server = new ScriptedServer
        {
            Responder = req => LastText(req) == AgentSession.HandoffRequest ? Sse.Text("NOTE: the story is written.")
                : LastText(req) == "Write a story" ? Sse.Text("Done.")
                : Sse.ToolCall("w1", "wait", """{"seconds":30}"""),
        };
        var (session, observer) = CreateSession(server, Path.GetTempPath(), new FakePlatform());
        using var stop = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));

        await session.RunAsync("Draw a circle in Paint", observer, stop.Token);
        Assert.Equal(AgentStopReason.Cancelled, observer.StopReason);
        Assert.Equal(AgentSession.StoppedNote, session.History[^1].Content);
        Assert.Equal("Not run: the user stopped the agent.", session.History[^2].Content);

        await session.RunAsync("Write a story", observer, TestContext.Current.CancellationToken);
        var messages = server.Requests[^1]["messages"]!.AsArray();
        Assert.Equal(AgentSession.StoppedNote, messages[^2]!["content"]!.ToString());
        Assert.Equal("Write a story", messages[^1]!["content"]!.ToString());

        await session.ResetContextAsync(observer, TestContext.Current.CancellationToken);
        Assert.Equal(["Draw a circle in Paint", AgentSession.StoppedNote, "Write a story"], session.History.Take(3).Select(m => m.Content));
    }

    [Theory]
    [InlineData(100_000, 80_000)]  // a fifth stays free
    [InlineData(65_536, 49_152)]   // at least 16k
    [InlineData(32_768, 16_384)]
    [InlineData(16_384, 8_192)]    // but never more than half: 16k free of 16k reset before every step
    [InlineData(8_192, 4_096)]
    public void The_context_is_reset_with_room_left_for_the_reply_and_the_note(int context, int threshold)
    {
        var (session, _) = CreateSession(new ScriptedServer(), Path.GetTempPath(), new FakePlatform(), contextTokens: context);
        Assert.Equal(threshold, session.ResetThreshold);
    }

    [Fact]
    public async Task After_a_reset_the_next_reply_gets_a_step_of_its_own()
    {
        // The window shows each step's reply in its own bubble; a reply right after the handoff
        // note, with no step between them, went into the note's finished bubble and never showed.
        var server = new ScriptedServer
        {
            Responder = req => LastText(req) == AgentSession.HandoffRequest ? Sse.Text("NOTE: all fine.") : Sse.Text("Done."),
        };
        var (session, observer) = LongSession(server, contextTokens: 40_000);
        await session.RunAsync("and now finish", observer, CancellationToken.None);
        Assert.Equal(["step 0", "reply NOTE: all fine.", "step 1", "reply Done."], observer.Events);

        // The same when the server refuses the request as too long.
        var tooLong = new ScriptedServer(Sse.Text("NOTE: nothing done yet."), Sse.Text("Short answer."));
        tooLong.Failures.Enqueue((HttpStatusCode.BadRequest, """{"error":{"message":"This model's maximum context length is 8192 tokens."}}"""));
        var (second, events) = CreateSession(tooLong, Path.GetTempPath(), new FakePlatform(), contextTokens: 100_000);
        await second.RunAsync("question", events, CancellationToken.None);
        Assert.Equal(["step 1", "step 0", "reply NOTE: nothing done yet.", "step 0", "reply Short answer."], events.Events);
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
    public async Task A_session_that_continues_a_chat_counts_from_the_servers_last_usage()
    {
        // The app starts a session for every message, on the chat's history. By the server's
        // count the last prompt was 90k tokens; the estimate of this short history is tiny.
        var server = new ScriptedServer(Sse.Text("NOTE: answered once."), Sse.Text("Done."));
        var (session, observer) = CreateSession(server, Path.GetTempPath(), new FakePlatform(), contextTokens: 100_000);
        session.History.Add(new ChatMessage { Role = ChatRole.User, Content = "first question", FromUser = true });
        session.History.Add(new ChatMessage { Role = ChatRole.Assistant, Content = "first answer", PromptTokens = 90_000, CompletionTokens = 50 });
        Assert.Equal(90_050, session.TokensInUse());

        await session.RunAsync("second question", observer, CancellationToken.None);

        Assert.Equal(AgentStopReason.Completed, observer.StopReason);
        Assert.Equal(AgentSession.HandoffRequest, LastText(server.Requests[0]));
        Assert.Equal(session.EstimateTokens(), session.TokensInUse()); // the old count went with the reset
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
        server.Failures.Enqueue((HttpStatusCode.BadRequest, """{"error":"boom"}"""));
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
