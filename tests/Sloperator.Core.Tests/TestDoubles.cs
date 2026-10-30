using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Sloperator.Core.Agent;
using Sloperator.Core.Llm;
using Sloperator.Core.Platform;
using Sloperator.Core.Tools;

namespace Sloperator.Core.Tests;

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

    /// <summary>A tool call the server's length limit ends in the middle of its arguments.</summary>
    public static string CutToolCall(string id, string name, string partialArgs) =>
        ToolCall(id, name, partialArgs).Replace("data: [DONE]\n\n", "") +
        $"data: {new JsonObject { ["choices"] = new JsonArray(new JsonObject { ["delta"] = new JsonObject(), ["finish_reason"] = "length" }) }.ToJsonString()}\n\ndata: [DONE]\n\n";

    /// <summary>A reply that thinks <paramref name="reasoning"/> first, then streams <paramref name="then"/>.</summary>
    public static string Thought(string reasoning, string then) =>
        $"data: {new JsonObject { ["choices"] = new JsonArray(new JsonObject { ["delta"] = new JsonObject { ["reasoning_content"] = reasoning } }) }.ToJsonString()}\n\n" + then;

    /// <summary>Where a reply stops coming in, as a model that thinks on and on: nothing more until the request is cancelled.</summary>
    public const string Stall = "\u0001stall\u0001";

    /// <summary>A reply that thinks <paramref name="reasoning"/> and then goes on thinking, sending nothing more.</summary>
    public static string ThinkingOn(string reasoning) => Thought(reasoning, Stall);

    /// <summary>One reply with several tool calls: (id, name, arguments) each.</summary>
    public static string ToolCalls(params (string Id, string Name, string Args)[] calls)
    {
        var list = new JsonArray(calls.Select((c, i) => (JsonNode)new JsonObject
        {
            ["index"] = i,
            ["id"] = c.Id,
            ["function"] = new JsonObject { ["name"] = c.Name, ["arguments"] = c.Args },
        }).ToArray());
        var chunk = new JsonObject { ["choices"] = new JsonArray(new JsonObject { ["delta"] = new JsonObject { ["tool_calls"] = list } }) };
        return $"data: {chunk.ToJsonString()}\n\ndata: [DONE]\n\n";
    }
}

/// <summary>A tool that runs until it is cancelled, as a long build would, and says when it started.</summary>
internal sealed class EndlessTool : Tool
{
    public override string Name => "endless";
    public override string Description => "Runs until it is stopped.";
    public override ToolRisk Risk => ToolRisk.ReadOnly;
    public override JsonObject Parameters => Schema();

    public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public int Runs { get; private set; }

    public override async Task<ToolResult> ExecuteAsync(JsonObject args, ToolContext ctx, CancellationToken ct)
    {
        Runs++;
        Started.TrySetResult();
        await Task.Delay(Timeout.Infinite, ct);
        return new ToolResult("never");
    }
}

internal sealed class ScriptedServer(params string[] responses) : HttpMessageHandler
{
    private int _next;
    public List<JsonObject> Requests { get; } = [];

    /// <summary>Error responses returned before the scripted ones.</summary>
    public Queue<(HttpStatusCode Status, string Body)> Failures { get; } = [];

    /// <summary>The Retry-After header the 429s among <see cref="Failures"/> carry, if any.</summary>
    public TimeSpan? RetryAfter { get; set; }

    /// <summary>Number of requests to fail with a dropped connection first.</summary>
    public int Drops { get; set; }

    /// <summary>Number of requests to fail before any of them is sent, as with a server that is down.</summary>
    public int Unreachable { get; set; }

    /// <summary>Number of requests answered with an error whose body breaks off, as when the link drops while it comes in.</summary>
    public int BrokenErrors { get; set; }

    /// <summary>Number of requests whose upload stops getting through, as over a VPN that stalls, until the client gives up on it.</summary>
    public int StalledUploads { get; set; }

    /// <summary>Optional: computes the reply from the request instead of the script.</summary>
    public Func<JsonObject, string>? Responder { get; set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        if (Unreachable > 0)
        {
            Unreachable--;
            throw new HttpRequestException(HttpRequestError.ConnectionError, "No connection could be made because the target machine actively refused it. (test:1234)");
        }
        if (StalledUploads > 0)
        {
            // Before anything reads the body: a body once read is kept in memory and copied from there.
            StalledUploads--;
            await request.Content!.CopyToAsync(new StalledStream(), ct);
            throw new InvalidOperationException("the client never gave up on a stalled upload");
        }
        Requests.Add(JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!.AsObject());
        if (Failures.TryDequeue(out var failure))
        {
            var response = new HttpResponseMessage(failure.Status) { Content = new StringContent(failure.Body, Encoding.UTF8, "application/json") };
            if ((int)failure.Status == 429 && RetryAfter is { } wait)
                response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(wait);
            return response;
        }
        if (Drops > 0)
        {
            Drops--;
            throw new HttpRequestException("The connection was closed (simulated drop)");
        }
        if (BrokenErrors > 0)
        {
            BrokenErrors--;
            return new HttpResponseMessage(HttpStatusCode.BadGateway) { Content = new StreamContent(new BrokenStream()) };
        }
        if (Responder is not null)
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = Stream(Responder(Requests[^1])) };
        var body = _next < responses.Length ? responses[_next++] : Sse.Text("(script exhausted)");
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = Stream(body) };
    }

    /// <summary>The reply as it streams; one with <see cref="Sse.Stall"/> in it sends what comes before it and then nothing more.</summary>
    private static HttpContent Stream(string body)
    {
        if (body.IndexOf(Sse.Stall, StringComparison.Ordinal) is not (>= 0 and var at))
            return new StringContent(body, Encoding.UTF8, "text/event-stream");
        var content = new StreamContent(new StallingStream(Encoding.UTF8.GetBytes(body[..at])));
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/event-stream");
        return content;
    }

    /// <summary>Gives what it has, then waits on every read until that read is cancelled.</summary>
    private sealed class StallingStream(byte[] start) : MemoryStream(start)
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            var read = await base.ReadAsync(buffer, ct);
            if (read > 0)
                return read;
            await Task.Delay(Timeout.Infinite, ct);
            return 0;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();
    }

    /// <summary>A connection that takes nothing: every write waits until it is cancelled.</summary>
    private sealed class StalledStream : MemoryStream
    {
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct) => await Task.Delay(Timeout.Infinite, ct);
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default) => await Task.Delay(Timeout.Infinite, ct);
    }

    /// <summary>A body whose connection resets as soon as it is read.</summary>
    private sealed class BrokenStream : MemoryStream
    {
        public override int Read(byte[] buffer, int offset, int count) => throw new IOException("An existing connection was forcibly closed by the remote host.");
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => throw new IOException("An existing connection was forcibly closed by the remote host.");
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => throw new IOException("An existing connection was forcibly closed by the remote host.");
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

    /// <summary>Step starts and replies in order: "step 1", "reply Done.".</summary>
    public List<string> Events { get; } = [];

    public void OnStepStarted(int step) => Events.Add($"step {step}");
    public void OnDelta(StreamDelta delta) => FirstDelta.TrySetResult();
    public void OnAssistantMessage(ChatMessage message) => Events.Add("reply " + message.Content);

    /// <summary>Runs while an OK is asked for, before the answer (e.g. the user pressing Stop meanwhile).</summary>
    public Action? WhileAsked { get; set; }

    public Task<bool> RequestApprovalAsync(ToolCall call, Tool tool, string summary, CancellationToken ct)
    {
        ApprovalRequests++;
        WhileAsked?.Invoke();
        return Task.FromResult(Approve);
    }

    public void OnToolStarted(ToolCall call, string summary) => ToolsRun.Add(call.Name);

    /// <summary>The user's messages that joined the conversation during the run, as sent.</summary>
    public List<UserInput> Delivered { get; } = [];
    public void OnUserMessageDelivered(UserInput input) => Delivered.Add(input);

    /// <summary>The first streamed piece of the reply in progress: the model has started on it.</summary>
    public TaskCompletionSource FirstDelta { get; set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public List<string> Notices { get; } = [];
    public event Action<string>? NoticeReceived;
    public void OnNotice(string text) { Notices.Add(text); NoticeReceived?.Invoke(text); }

    /// <summary>The server's failures the notices said what was done about, with each notice.</summary>
    public List<(string Notice, Exception Error)> NoticeErrors { get; } = [];
    public void OnNotice(string text, Exception error) { NoticeErrors.Add((text, error)); OnNotice(text); }
    public void OnToolFinished(ToolCall call, ToolResult result) => Results.Add(result);

    /// <summary>What the request upload reported, in order: (sent, total).</summary>
    public List<(long Sent, long Total)> Sending { get; } = [];
    public void OnSending(long sent, long total) => Sending.Add((sent, total));

    /// <summary>The exception a bug ended the run with, if one did.</summary>
    public Exception? Failed { get; private set; }
    public void OnFailed(Exception error) => Failed = error;

    public void OnFinished(AgentStopReason reason, string? detail)
    {
        StopReason = reason;
        Detail = detail;
    }

    /// <summary>The exception the run ended on, a server's answer or a bug, if it ended on one.</summary>
    public Exception? Error { get; private set; }
    public void OnFinished(AgentStopReason reason, string? detail, Exception error)
    {
        Error = error;
        OnFinished(reason, detail);
    }
}

/// <summary>A 200x100 screen that records input instead of sending it.</summary>
internal sealed class FakePlatform : IPlatformServices, IScreenCapture, IInputController
{
    public IHostWindow HostWindow { get; set; } = NoHostWindow.Instance;
    public IInputOverlay InputOverlay => NoInputOverlay.Instance;

    public List<string> Actions { get; } = [];
    public (int X, int Y) Cursor { get; set; } = (50, 50);

    public string Name => "Fake OS";
    public IScreenCapture Screen => this;
    public IInputController Input => this;
    public ProcessStartInfo CreateShellStartInfo(string command) => new("cmd.exe") { ArgumentList = { "/c", command } };

    /// <summary>Text recognition for find_text: none unless a test gives one (the click benchmark gives Windows' own).</summary>
    public ITextReader? Text { get; set; }

    public ScreenRect ScreenBounds { get; set; } = new(0, 0, 200, 100);

    /// <summary>All monitors; by default just <see cref="ScreenBounds"/>.</summary>
    public List<ScreenRect>? Screens { get; set; }
    public List<ScreenRect> Captures { get; } = [];
    public IStopKey StopKey => NoStopKey.Instance;

    public IReadOnlyList<ScreenRect> GetScreens() => Screens ?? [ScreenBounds];
    /// <summary>What the screens show, if anything (black otherwise), with its top-left corner at <see cref="DesktopOrigin"/>.</summary>
    public RawImage? Desktop { get; set; }
    public (int X, int Y) DesktopOrigin { get; set; }

    /// <summary>
    /// Rows the first screenshot (a capture with the cursor) shows the desktop shifted up by, so the
    /// model misjudges where things are, as when it misreads a screen. Later captures are true.
    /// </summary>
    public int ShiftFirstShot { get; set; }

    public RawImage Capture(ScreenRect region, bool includeCursor)
    {
        Captures.Add(region);
        var shift = 0;
        if (includeCursor && ShiftFirstShot != 0)
            (shift, ShiftFirstShot) = (ShiftFirstShot, 0);
        var pixels = new byte[region.Width * region.Height * 4];
        if (Desktop is { } d)
            for (var row = 0; row < region.Height; row++)
            {
                var from = Math.Clamp(region.Y - DesktopOrigin.Y + row + shift, 0, d.Height - 1);
                Array.Copy(d.Bgra, from * d.Stride + (region.X - DesktopOrigin.X) * 4, pixels, row * region.Width * 4, region.Width * 4);
            }
        if (includeCursor)
            DrawPointer(pixels, region);
        return new(region.Width, region.Height, pixels);
    }

    /// <summary>
    /// The mouse pointer on a capture that includes it, as the real screen shows it: a dark arrow tip at
    /// the cursor. Without it the model never sees where its last click landed.
    /// </summary>
    private void DrawPointer(byte[] pixels, ScreenRect region)
    {
        var (px, py) = (Cursor.X - region.X, Cursor.Y - region.Y);
        for (var dy = 0; dy < 16; dy++)
            for (var dx = 0; dx <= Math.Min(dy, 9); dx++)
            {
                var (x, y) = (px + dx, py + dy);
                if (x < 0 || y < 0 || x >= region.Width || y >= region.Height)
                    continue;
                var i = (y * region.Width + x) * 4;
                (pixels[i], pixels[i + 1], pixels[i + 2], pixels[i + 3]) = (0, 0, 0, 255);
            }
    }
    public (int X, int Y) GetCursorPosition() => Cursor;
    public void MoveMouse(int x, int y) { Cursor = (x, y); Actions.Add($"move {x},{y}"); }
    public void MouseDown(MouseButton button) => Actions.Add($"down {button}");
    public void MouseUp(MouseButton button) => Actions.Add($"up {button}");
    public void Click(MouseButton button, int count) { Actions.Add($"click {button} x{count}"); OnClick?.Invoke(this); }

    /// <summary>How the fake screen reacts to a click (e.g. by changing <see cref="Desktop"/>).</summary>
    public Action<FakePlatform>? OnClick { get; set; }
    public void Scroll(int dx, int dy) => Actions.Add($"scroll {dx},{dy}");
    public void TypeText(string text) { Actions.Add($"type {text}"); OnType?.Invoke(this); }

    /// <summary>What happens as text is typed (e.g. the user pressing Stop meanwhile).</summary>
    public Action<FakePlatform>? OnType { get; set; }
    public void PressKeys(IReadOnlyList<string> keys) => Actions.Add("keys " + string.Join("+", keys));
    public IDisposable HoldKeys(IReadOnlyList<string> keys)
    {
        var chord = string.Join("+", keys);
        Actions.Add("hold " + chord);
        return new Released(() => Actions.Add("release " + chord));
    }
    public bool IsKnownKey(string key) => key.Length > 0 && key != "nokey";

    private sealed class Released(Action onRelease) : IDisposable
    {
        public void Dispose() => onRelease();
    }
}
