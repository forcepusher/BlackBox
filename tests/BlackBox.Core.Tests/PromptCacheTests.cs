using System.Text.Json.Nodes;
using BlackBox.Core.Agent;
using BlackBox.Core.Llm;
using BlackBox.Core.Mcp;
using BlackBox.Core.Platform;
using BlackBox.Core.Settings;
using BlackBox.Core.Ssh;
using Xunit;

namespace BlackBox.Core.Tests;

/// <summary>
/// LM Studio reads only what is new in a request that begins with the one before it, token for
/// token. Anything else (a clock in the system prompt, the tools in another order, an old message
/// edited) makes it read the whole chat again: "processing prompt" for a minute or more on a long
/// one, at every step.
/// </summary>
public class PromptCacheTests
{
    [Fact]
    public async Task Every_request_of_a_chat_begins_with_the_one_before_it()
    {
        var paths = new AppPaths(Directory.CreateTempSubdirectory("bb-cache-").FullName);
        AgentSession? session = null;
        var replies = new Queue<string>([
            // A look, a click (whose fresh look is a request of its own) and an answer.
            Sse.Thought("I should look at the screen first.", Sse.ToolCall("c1", "screenshot", "{}")),
            Sse.Thought("The OK button is in the middle.", Sse.ToolCall("c2", "mouse_click", """{"target":"OK button","x":100,"y":50}""")),
            Sse.Thought("It closed.", Sse.Text("Clicked OK.")),
            // The next message, and one the user sends while the model works on it.
            Sse.Thought("Typing it.", Sse.ToolCall("c3", "type_text", """{"text":"hello"}""")),
            Sse.Thought("Enter too, then.", Sse.ToolCall("c4", "press_keys", """{"keys":["enter"]}""")),
            Sse.Thought("Both done.", Sse.Text("Typed hello and pressed Enter.")),
            Sse.Thought("Nothing was left.", Sse.Text("That was all.")),
            // A wait the user stops with Pause/Break, and Continue after it.
            Sse.Thought("It takes a while.", Sse.ToolCall("c5", "wait", """{"seconds":30}""")),
            Sse.Thought("It is there now.", Sse.Text("The download finished.")),
        ]);
        var server = new ScriptedServer
        {
            Responder = request =>
            {
                if (request["tools"] is null)
                    return Sse.Text("(100, 50)"); // a click's fresh look
                if (replies.Count == 6)
                    session!.Steer("and press Enter after it");
                return replies.Dequeue();
            },
        };
        var settings = new ServerSettings { BaseUrl = "http://test", Model = "qwen3.8-27b@q6_k_xl" };
        var history = new List<ChatMessage>();
        await using var mcp = new McpHost();
        using var ssh = new SshHosts(paths.KnownHostsFile);
        var platform = new FakePlatform();

        foreach (var (message, stopAfter, ended) in new (string, TimeSpan?, AgentStopReason)[]
        {
            ("Click OK in the dialog", null, AgentStopReason.Completed),
            ("Now type hello", null, AgentStopReason.Completed),
            ("Wait for the download to finish", TimeSpan.FromSeconds(1), AgentStopReason.Cancelled),
            ("Continue.", null, AgentStopReason.Completed),
        })
        {
            // As the app does it: a session of its own for every message, on the chat's history.
            session = AgentSetup.Create(BackendConnection.Create(new HttpClient(server), settings), platform, new AgentSettings(), paths, mcp, ssh, history);
            var observer = new RecordingObserver();
            using var stop = new CancellationTokenSource();
            if (stopAfter is { } after)
                stop.CancelAfter(after);
            await session.RunAsync(message, observer, stop.Token);
            Assert.Equal(ended, observer.StopReason);
        }

        var requests = server.Requests.Where(r => r["tools"] is not null).ToList();
        Assert.Equal(9, requests.Count);
        Assert.Equal(1, server.Requests.Count(r => r["tools"] is null));
        for (var i = 1; i < requests.Count; i++)
        {
            // The same tools, model and template switches...
            Assert.Equal(AllBut(requests[0], "messages"), AllBut(requests[i], "messages"));
            // ...and everything said so far, unchanged, with the new messages after it.
            var before = requests[i - 1]["messages"]!.AsArray();
            var after = requests[i]["messages"]!.AsArray();
            Assert.True(after.Count > before.Count, $"request {i + 1} is not longer than the one before it");
            for (var m = 0; m < before.Count; m++)
                Assert.Equal(before[m]!.ToJsonString(), after[m]!.ToJsonString());
        }
        // The thinking goes back as it came, for the templates that write it into the prompt again.
        var thoughts = requests[^1]["messages"]!.AsArray().Select(m => m!["reasoning_content"]?.GetValue<string>()).OfType<string>().ToList();
        Assert.Equal(["I should look at the screen first.", "The OK button is in the middle.", "It closed.", "Typing it."], thoughts.Take(4));
    }

    [Fact]
    public async Task A_repaired_call_and_one_cut_short_change_nothing_sent_before_them()
    {
        // Both are put right before the next request: the repaired call's arguments in place of the
        // model's, the note about the cut call after the last result or as a message of its own.
        var paths = new AppPaths(Directory.CreateTempSubdirectory("bb-cache-").FullName);
        // In the test's own folder: a bare name would land in the profile folder the agent starts in.
        var file = Path.Combine(paths.Root, "a.txt").Replace('\\', '/');
        // With the token counts LM Studio sends: far from a full context, so cut by a reply-length limit.
        var usage = new JsonObject { ["choices"] = new JsonArray(), ["usage"] = new JsonObject { ["prompt_tokens"] = 3000, ["completion_tokens"] = 500 } };
        var server = new ScriptedServer(
            Sse.ToolCall("c1", "wait", "{'seconds': 0}"),
            Sse.CutToolCall("c2", "write_file", $$"""{"path": "{{file}}", "content": "the first half of a long fi""")
                .Replace("data: [DONE]", $"data: {usage.ToJsonString()}\n\ndata: [DONE]"),
            Sse.ToolCall("c3", "write_file", $$"""{"path": "{{file}}", "content": "short"}"""),
            Sse.Text("Done."),
            Sse.Text("Again."));
        var settings = new ServerSettings { BaseUrl = "http://test", Model = "qwen3.8-27b@q6_k_xl" };
        var history = new List<ChatMessage>();
        await using var mcp = new McpHost();
        using var ssh = new SshHosts(paths.KnownHostsFile);
        var platform = new FakePlatform();
        foreach (var message in new[] { "Write it down", "Once more" })
        {
            var session = AgentSetup.Create(BackendConnection.Create(new HttpClient(server), settings), platform, new AgentSettings(), paths, mcp, ssh, history);
            var observer = new RecordingObserver();
            await session.RunAsync(message, observer, TestContext.Current.CancellationToken);
            Assert.Equal(AgentStopReason.Completed, observer.StopReason);
        }

        Assert.Equal(5, server.Requests.Count);
        Assert.Contains("cut off by the server's limit", (string?)server.Requests[2]["messages"]!.AsArray()[^1]!["content"]);
        for (var i = 1; i < server.Requests.Count; i++)
        {
            Assert.Equal(AllBut(server.Requests[0], "messages"), AllBut(server.Requests[i], "messages"));
            var before = server.Requests[i - 1]["messages"]!.AsArray();
            var after = server.Requests[i]["messages"]!.AsArray();
            Assert.True(after.Count > before.Count, $"request {i + 1} is not longer than the one before it");
            for (var m = 0; m < before.Count; m++)
                Assert.Equal(before[m]!.ToJsonString(), after[m]!.ToJsonString());
        }
    }

    private static string AllBut(JsonObject request, string key)
    {
        var copy = request.DeepClone().AsObject();
        copy.Remove(key);
        return copy.ToJsonString();
    }

    private static readonly string? Url = Environment.GetEnvironmentVariable("BB_SERVER_URL");
    private static readonly string? Model = Environment.GetEnvironmentVariable("BB_SERVER_MODEL");

    /// <summary>
    /// Opt-in (BB_SERVER_URL, BB_SERVER_MODEL): a chat of three messages about the Unity editor's
    /// screenshot, run as the app runs it, timing how long the server takes from the end of each
    /// request to the first word of its reply. A server that reads only what is new answers a step
    /// in seconds; one that reads the whole chat again takes as long as the first, cold request
    /// per token. The timings go to bb-cache-timings.txt in the temp folder.
    /// </summary>
    [Fact]
    public async Task On_a_real_server_each_request_reads_only_what_is_new()
    {
        Assert.SkipWhen(Url is null || Model is null, "Set BB_SERVER_URL and BB_SERVER_MODEL to run.");
        var settings = new ServerSettings { BaseUrl = Url!, Model = Model! };
        using var http = LlmHttp.Create(TimeSpan.FromMinutes(20));
        var facts = await ServerProbe.EnsureLoadedAsync(http, settings, null, TestContext.Current.CancellationToken);
        settings.SetDetectedContext(facts.Context, facts.Hosted);
        var paths = new AppPaths(Directory.CreateTempSubdirectory("bb-cache-").FullName);
        using var scene = SkiaSharp.SKBitmap.Decode(Path.Combine(AppContext.BaseDirectory, "ClickScenes", "unity-editor.png"));
        using var bgra = scene.Copy(SkiaSharp.SKColorType.Bgra8888);
        var platform = new FakePlatform { ScreenBounds = new(0, 0, bgra.Width, bgra.Height), Desktop = new RawImage(bgra.Width, bgra.Height, bgra.Bytes) };
        var history = new List<ChatMessage>();
        await using var mcp = new McpHost();
        using var ssh = new SshHosts(paths.KnownHostsFile);
        var steps = new List<TimedStep>();

        // A long first message, so that reading the whole chat again takes far longer than a new screenshot.
        var notes = string.Concat(Enumerable.Range(1, 400).Select(i => $"Note {i}: build {i * 7} passed on machine {i % 13} after {i % 9 + 1} minutes.\n"));
        foreach (var message in new[]
        {
            "Here are some build notes, only to read; don't save or work on them:\n" + notes + "\nNow take a screenshot and tell me in one sentence which program is open.",
            "Without any tools: which panels does it show? One sentence.",
            "Take another screenshot and tell me in one sentence whether anything changed.",
        })
        {
            var session = AgentSetup.Create(BackendConnection.Create(http, settings), platform, new AgentSettings(), paths, mcp, ssh, history);
            var observer = new TimingObserver(steps);
            await session.RunAsync(message, observer, TestContext.Current.CancellationToken);
            Assert.True(observer.StopReason == AgentStopReason.Completed, $"{observer.StopReason}: {observer.Detail}");
        }

        var counted = steps.Where(s => s.Prompt is not null).ToList();
        Assert.True(counted.Count >= 4 && counted[0].Waited is not null, "The server reported no token counts.");
        // How fast the server reads a prompt it has nothing of: the first request.
        var perSecond = counted[0].Prompt!.Value / Math.Max(counted[0].Waited!.Value.TotalSeconds, 0.5);
        var report = new List<string> { $"{Model}: first request {counted[0].Prompt} tokens in {counted[0].Waited!.Value.TotalSeconds:0.0} s, {perSecond:0} tokens/s" };
        var reread = new List<string>();
        for (var i = 1; i < counted.Count; i++)
        {
            var (before, step) = (counted[i - 1], counted[i]);
            var fresh = step.Prompt!.Value - before.Prompt!.Value - (before.Completion ?? 0);
            var (ifFresh, ifAll) = (Math.Max(fresh, 0) / perSecond, step.Prompt.Value / perSecond);
            if (step.Waited is not { } wait)
            {
                report.Add($"#{i + 1}: {step.Prompt} tokens, {fresh} new, not timed (no text or thinking streamed)");
                continue;
            }
            var waited = wait.TotalSeconds;
            report.Add($"#{i + 1}: {step.Prompt} tokens, {fresh} new, waited {waited:0.0} s (new only: {ifFresh:0.0} s, all: {ifAll:0.0} s)");
            // Only where the two can be told apart: most of the prompt is old.
            if (ifAll - ifFresh > 10 && waited > (ifFresh + ifAll) / 2)
                reread.Add(report[^1]);
        }
        foreach (var line in report)
            TestContext.Current.SendDiagnosticMessage(line);
        File.WriteAllLines(Path.Combine(Path.GetTempPath(), "bb-cache-timings.txt"), report);
        Assert.True(reread.Count == 0, "The server read the whole chat again:\n" + string.Join("\n", reread));
    }

    private sealed class TimedStep
    {
        public DateTime? Sent;
        public TimeSpan? Waited;
        public int? Prompt, Completion;
    }

    /// <summary>Times each step from the moment its request has all gone out to its first streamed token.</summary>
    private sealed class TimingObserver(List<TimedStep> steps) : IAgentObserver
    {
        private TimedStep? _step;
        public AgentStopReason? StopReason { get; private set; }
        public string? Detail { get; private set; }

        public void OnStepStarted(int step) => steps.Add(_step = new TimedStep());

        public void OnSending(long sent, long total)
        {
            if (sent == total && _step is { } s)
                s.Sent = DateTime.UtcNow;
        }

        public void OnDelta(StreamDelta delta)
        {
            if (_step is { Sent: { } sent, Waited: null } s)
                s.Waited = DateTime.UtcNow - sent;
        }

        public void OnAssistantMessage(ChatMessage message)
        {
            if (_step is { } s)
                (s.Prompt, s.Completion) = (message.PromptTokens, message.CompletionTokens);
        }

        public Task<bool> RequestApprovalAsync(ToolCall call, Tools.Tool tool, string summary, CancellationToken ct) => Task.FromResult(true);
        public void OnToolStarted(ToolCall call, string summary) { }
        public void OnNotice(string text) => TestContext.Current.SendDiagnosticMessage("Notice: " + text);
        public void OnToolFinished(ToolCall call, Tools.ToolResult result) { }

        public void OnFinished(AgentStopReason reason, string? detail) => (StopReason, Detail) = (reason, detail);
    }
}
