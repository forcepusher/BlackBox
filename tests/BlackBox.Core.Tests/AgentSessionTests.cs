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
        Assert.All(session.History.Where(m => m.Role == ChatRole.Tool), m => Assert.Contains(m.ToolCallId, callIds));
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

    [Fact]
    public async Task Runaway_thinking_is_cut_off_and_retried_without_thinking()
    {
        var thinking = new JsonObject { ["choices"] = new JsonArray(new JsonObject { ["delta"] = new JsonObject { ["reasoning_content"] = new string('h', 500) } }) };
        var longThought = string.Concat(Enumerable.Repeat($"data: {thinking.ToJsonString()}\n\n", 10));
        var server = new ScriptedServer(longThought, Sse.Text("Done."));
        var settings = new AgentSettings { FailsafeCorner = false };
        var ctx = new ToolContext { Platform = new FakePlatform(), Settings = settings, WorkingDirectory = Path.GetTempPath() };
        var options = new ChatRequestOptions
        {
            MaxReasoningChars = 1000,
            Extra = new JsonObject { ["chat_template_kwargs"] = new JsonObject { ["enable_thinking"] = true } },
        };
        var session = new AgentSession(new OpenAiChatClient(new HttpClient(server), "http://test", null), options, ToolRegistry.CreateDefault(true), ctx, "sys");
        var observer = new RecordingObserver();

        await session.RunAsync("go", observer, CancellationToken.None);

        Assert.Equal(AgentStopReason.Completed, observer.StopReason);
        Assert.Equal(2, server.Requests.Count);
        Assert.False(server.Requests[1]["chat_template_kwargs"]!["enable_thinking"]!.GetValue<bool>());
        Assert.Equal("Done.", session.History[^1].Content);
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

internal static class Sse
{
    public static string Text(string text) =>
        $"data: {new JsonObject { ["choices"] = new JsonArray(new JsonObject { ["delta"] = new JsonObject { ["content"] = text } }) }.ToJsonString()}\n\ndata: [DONE]\n\n";

    public static string ToolCall(string id, string name, string args)
    {
        var call = new JsonObject
        {
            ["index"] = 0,
            ["id"] = id,
            ["function"] = new JsonObject { ["name"] = name, ["arguments"] = args },
        };
        var chunk = new JsonObject { ["choices"] = new JsonArray(new JsonObject { ["delta"] = new JsonObject { ["tool_calls"] = new JsonArray(call) } }) };
        return $"data: {chunk.ToJsonString()}\n\ndata: [DONE]\n\n";
    }
}

internal sealed class ScriptedServer(params string[] responses) : HttpMessageHandler
{
    private int _next;
    public List<JsonObject> Requests { get; } = [];

    /// <summary>Error responses returned before the scripted ones.</summary>
    public Queue<(HttpStatusCode Status, string Body)> Failures { get; } = [];

    /// <summary>Number of requests to fail with a dropped connection first.</summary>
    public int Drops { get; set; }

    /// <summary>Optional: computes the reply from the request instead of the script.</summary>
    public Func<JsonObject, string>? Responder { get; set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Requests.Add(JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!.AsObject());
        if (Failures.TryDequeue(out var failure))
            return new HttpResponseMessage(failure.Status) { Content = new StringContent(failure.Body, Encoding.UTF8, "application/json") };
        if (Drops > 0)
        {
            Drops--;
            throw new HttpRequestException("The connection was closed (simulated drop)");
        }
        if (Responder is not null)
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Responder(Requests[^1]), Encoding.UTF8, "text/event-stream") };
        var body = _next < responses.Length ? responses[_next++] : Sse.Text("(script exhausted)");
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "text/event-stream") };
    }
}

internal sealed class RecordingObserver : IAgentObserver
{
    public bool Approve { get; set; } = true;
    public int ApprovalRequests { get; private set; }
    public List<string> ToolsRun { get; } = [];
    public List<ToolResult> Results { get; } = [];
    public AgentStopReason? StopReason { get; private set; }
    public string? Detail { get; private set; }

    public void OnStepStarted(int step) { }
    public void OnDelta(StreamDelta delta) { }
    public void OnAssistantMessage(ChatMessage message) { }

    public Task<bool> RequestApprovalAsync(ToolCall call, Tool tool, string summary, CancellationToken ct)
    {
        ApprovalRequests++;
        return Task.FromResult(Approve);
    }

    public void OnToolStarted(ToolCall call, string summary) => ToolsRun.Add(call.Name);
    public List<string> Notices { get; } = [];
    public void OnNotice(string text) => Notices.Add(text);
    public void OnToolFinished(ToolCall call, ToolResult result) => Results.Add(result);

    public void OnFinished(AgentStopReason reason, string? detail)
    {
        StopReason = reason;
        Detail = detail;
    }
}

/// <summary>A 200x100 screen that records input instead of sending it.</summary>
internal sealed class FakePlatform : IPlatformServices, IScreenCapture, IInputController, IWindowManager, IUiAutomation
{
    public IUiAutomation? Automation => this;
    public IHostWindow HostWindow { get; set; } = NoHostWindow.Instance;
    public IReadOnlyList<UiElement> GetElements(string? windowTitle, int maxCount) =>
    [
        new UiElement(1, "Button", "Save", null, new ScreenRect(100, 50, 40, 20), true),
        new UiElement(2, "Edit", "File name", "notes.txt", new ScreenRect(10, 10, 80, 20), true),
    ];
    public bool Activate(int id) { Actions.Add($"activate {id}"); return id is 1 or 2; }

    public List<string> Actions { get; } = [];
    public (int X, int Y) Cursor { get; set; } = (50, 50);

    public string Name => "Fake OS";
    public IScreenCapture Screen => this;
    public IInputController Input => this;
    public IWindowManager Windows => this;
    public ProcessStartInfo CreateShellStartInfo(string command) => new("cmd.exe") { ArgumentList = { "/c", command } };

    public ScreenRect GetPrimaryScreen() => new(0, 0, 200, 100);
    public RawImage Capture(ScreenRect region, bool includeCursor) => new(region.Width, region.Height, new byte[region.Width * region.Height * 4]);
    public (int X, int Y) GetCursorPosition() => Cursor;
    public void MoveMouse(int x, int y) { Cursor = (x, y); Actions.Add($"move {x},{y}"); }
    public void MouseDown(MouseButton button) => Actions.Add($"down {button}");
    public void MouseUp(MouseButton button) => Actions.Add($"up {button}");
    public void Click(MouseButton button, int count) => Actions.Add($"click {button} x{count}");
    public void Scroll(int dx, int dy) => Actions.Add($"scroll {dx},{dy}");
    public void TypeText(string text) => Actions.Add($"type {text}");
    public void PressKeys(IReadOnlyList<string> keys) => Actions.Add("keys " + string.Join("+", keys));
    public bool IsKnownKey(string key) => key.Length > 0;
    public IReadOnlyList<WindowInfo> ListWindows() => [new WindowInfo(42, "Notepad", "notepad", new ScreenRect(0, 0, 100, 50), false)];
    public bool FocusWindow(long handle) { Actions.Add($"focus {handle}"); return true; }
}
