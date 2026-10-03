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
    public async Task Screenshots_are_sent_as_user_images_and_old_ones_pruned()
    {
        var server = new ScriptedServer(
            Sse.ToolCall("s1", "screenshot", "{}"),
            Sse.ToolCall("s2", "screenshot", "{}"),
            Sse.ToolCall("s3", "screenshot", "{}"),
            Sse.ToolCall("s4", "screenshot", "{}"),
            Sse.ToolCall("s5", "screenshot", "{}"),
            Sse.ToolCall("s6", "screenshot", "{}"),
            Sse.Text("Done."));
        var (session, observer) = CreateSession(server, Path.GetTempPath(), new FakePlatform(), keepScreenshots: 2);

        await session.RunAsync("look", observer, CancellationToken.None);

        Assert.Equal(AgentStopReason.Completed, observer.StopReason);
        // Pruning waits for keep + 3 images, then drops back to keep, so the prompt prefix
        // (and the server's cache) stays stable for several steps at a time.
        Assert.Equal(5, Images(server.Requests[^2]));
        Assert.Equal(2, Images(server.Requests[^1]));

        static int Images(JsonObject request) => request["messages"]!.AsArray()
            .SelectMany(m => m!["content"] is JsonArray parts ? parts : [])
            .Count(p => p!["type"]!.GetValue<string>() == "image_url");
    }

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
    public async Task Context_overflow_from_the_server_trims_history_and_retries()
    {
        var overflow = """{"error":"The number of tokens to keep from the initial prompt is greater than the context length. Try to load the model with a larger context length"}""";
        var server = new ScriptedServer(Sse.Text("Short answer."));
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
        Assert.Equal(2, server.Requests.Count);
        Assert.True(JsonSize(server.Requests[1]) < JsonSize(server.Requests[0]));

        static int JsonSize(JsonObject o) => o.ToJsonString().Length;
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

        session.FitContext(5_000);

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
    public async Task Long_runs_are_compacted_in_chunks_into_a_summary()
    {
        var server = new ScriptedServer
        {
            Responder = req => req["tools"] is null
                ? Sse.Text("SUMMARY: opened Notepad, typed the report, saved to C:\\notes.txt.")
                : Sse.Text("Done."),
        };
        var ctx = new ToolContext { Platform = new FakePlatform(), Settings = new AgentSettings(), WorkingDirectory = Path.GetTempPath() };
        var session = new AgentSession(new OpenAiChatClient(new HttpClient(server), "http://test", null),
            new ChatRequestOptions { MaxTokens = 1000 }, ToolRegistry.CreateDefault(true), ctx, "sys", contextTokens: 20_000);
        session.History.Add(ChatMessage.User("THE TASK"));
        for (var i = 0; i < 40; i++)
        {
            session.History.Add(new ChatMessage { Role = ChatRole.Assistant, Content = "", ToolCalls = [new ToolCall { Id = $"c{i}", Name = "wait", Arguments = "{}" }] });
            session.History.Add(ChatMessage.ToolResult($"c{i}", new string('x', 1500)));
        }
        var observer = new RecordingObserver();

        await session.RunAsync("and now finish", observer, CancellationToken.None);

        Assert.Equal(AgentStopReason.Completed, observer.StopReason);
        var compactionCalls = server.Requests.Count(r => r["tools"] is null);
        Assert.True(compactionCalls >= 2, $"expected several small summary requests, got {compactionCalls}");
        Assert.All(server.Requests.Where(r => r["tools"] is null), r => Assert.True(r.ToJsonString().Length < 40_000));
        Assert.Equal("THE TASK", session.History[0].Content);
        Assert.StartsWith(AgentSession.SummaryHeader, session.History[1].Content);
        Assert.Contains("C:\\notes.txt", session.History[1].Content);
        Assert.True(session.EstimateTokens() < 15_000);
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

    private static (AgentSession, RecordingObserver) CreateSession(ScriptedServer server, string dir, IPlatformServices platform, int keepScreenshots = 2)
    {
        var settings = new AgentSettings { KeepScreenshots = keepScreenshots, FailsafeCorner = false, ScreenshotAfterActions = false, Approval = ApprovalMode.AskRisky };
        var ctx = new ToolContext { Platform = platform, Settings = settings, WorkingDirectory = dir };
        var client = new OpenAiChatClient(new HttpClient(server), "http://test", null);
        var session = new AgentSession(client, new ChatRequestOptions(), ToolRegistry.CreateDefault(includeScreenTools: true), ctx, "system prompt");
        return (session, new RecordingObserver());
    }
}
