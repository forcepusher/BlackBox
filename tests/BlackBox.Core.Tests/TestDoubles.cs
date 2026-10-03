using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using BlackBox.Core.Agent;
using BlackBox.Core.Llm;
using BlackBox.Core.Platform;
using BlackBox.Core.Tools;

namespace BlackBox.Core.Tests;

// Shared fakes for the unit tests: a scripted chat server, an observer that records what the
// agent did, and a 200x100 "desktop" that records input instead of sending it.
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
    public IInputOverlay InputOverlay => NoInputOverlay.Instance;
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

    public ScreenRect ScreenBounds { get; set; } = new(0, 0, 200, 100);
    public ScreenRect GetPrimaryScreen() => ScreenBounds;
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
