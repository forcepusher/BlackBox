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

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Requests.Add(JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!.AsObject());
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
    public void TieToAppLifetime(Process process) { }
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
