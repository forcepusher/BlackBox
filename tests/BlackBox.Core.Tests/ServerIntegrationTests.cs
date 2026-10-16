using BlackBox.Core.Agent;
using BlackBox.Core.Llm;
using BlackBox.Core.Settings;
using BlackBox.Core.Tools;
using Xunit;

namespace BlackBox.Core.Tests;

/// <summary>
/// Opt-in checks against a real OpenAI-compatible server (tested with LM Studio). Set BB_SERVER_URL (e.g. http://host:1234/v1) and
/// BB_SERVER_MODEL to run them. They send large prompts and can take several minutes.
/// </summary>
public class ServerIntegrationTests
{
    private static readonly string? Url = Environment.GetEnvironmentVariable("BB_SERVER_URL");
    private static readonly string? Model = Environment.GetEnvironmentVariable("BB_SERVER_MODEL");

    private static async Task<(ServerSettings Server, HttpClient Http, int Context)> ConnectAsync()
    {
        Assert.SkipWhen(Url is null || Model is null, "Set BB_SERVER_URL and BB_SERVER_MODEL to run.");
        var server = new ServerSettings { BaseUrl = Url!, Model = Model! };
        var http = LlmHttp.Create(TimeSpan.FromMinutes(20));
        var context = (await ServerProbe.EnsureLoadedAsync(http, server, null, CancellationToken.None)).Context;
        Assert.NotNull(context);
        return (server, http, context!.Value);
    }

    private static AgentSession NewSession(HttpClient http, ServerSettings server, int contextTokens)
    {
        var connection = BackendConnection.Create(http, server);
        var ctx = new ToolContext { Platform = new FakePlatform(), Settings = new AgentSettings(), WorkingDirectory = Path.GetTempPath() };
        return new AgentSession(connection.Client, connection.Options, ToolRegistry.CreateDefault(false), ctx,
            "You are a test assistant.", contextTokens: contextTokens);
    }

    /// <summary>Fills the middle of the history with tool output until it is about <paramref name="tokens"/> long.</summary>
    private static void FillHistory(AgentSession session, int tokens)
    {
        session.History.Add(ChatMessage.User("Remember: the password is BLUEFISH."));
        for (var i = 0; session.EstimateTokens() < tokens; i++)
        {
            var text = $"Log page {i}: the quick brown fox number {i} jumped over lazy dog number {i * 7}. " +
                       string.Concat(Enumerable.Repeat($"Entry {i}: nothing unusual happened. ", 60));
            session.History.Add(new ChatMessage { Role = ChatRole.Assistant, Content = "", ToolCalls = [new ToolCall { Id = $"c{i}", Name = "read_file", Arguments = $$"""{"path":"log{{i}}.txt"}""" }] });
            session.History.Add(ChatMessage.ToolResult($"c{i}", text));
        }
    }

    private static async Task AssertAnswersPassword(AgentSession session)
    {
        var observer = new RecordingObserver();
        await session.RunAsync("Without using tools, reply with just the password I gave you at the start.", observer, CancellationToken.None);
        TestContext.Current.SendDiagnosticMessage("Notices: " + string.Join(" | ", observer.Notices));
        Assert.True(observer.StopReason == AgentStopReason.Completed, $"{observer.StopReason}: {observer.Detail}");
        Assert.Contains("BLUEFISH", session.History[^1].Content, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The normal path, as in a long run: the history grows (cached on the server) until it is
    /// nearly full, then the model writes a handoff note and the run continues from a fresh
    /// context. A value only found in a tool result must survive through the note. Timings of
    /// each phase are printed (and written to bb-reset-timings.txt in the temp folder).
    /// </summary>
    [Fact]
    public async Task Full_context_is_reset_with_a_handoff_note_and_the_task_continues()
    {
        var (server, http, context) = await ConnectAsync();
        var session = NewSession(http, server, context); // the whole context, as the app does
        var log = new List<string>();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        void Mark(string what) { log.Add($"{clock.Elapsed.TotalSeconds,7:0.0}s  {what}"); TestContext.Current.SendDiagnosticMessage(log[^1]); }

        // Phase 1: a history just under the reset point, sent once so the server caches it.
        session.History.Add(new ChatMessage { Role = ChatRole.User, Content = "We are reading the server logs to find the vault code. Read them one by one.", FromUser = true });
        AddLogs(session, session.ResetThreshold - 6_000, codeAt: 3);
        var observer = new RecordingObserver();
        await session.RunAsync("Without using tools, reply with just OK for now.", observer, CancellationToken.None);
        Assert.True(observer.StopReason == AgentStopReason.Completed, $"{observer.StopReason}: {observer.Detail}");
        Mark($"cold prompt processed ({session.TokensInUse() / 1000}k tokens, {observer.Notices.Count(n => n.StartsWith("Connection lost"))} drops)");

        // Phase 2: a few more steps push it just over the reset point; the next request resets.
        AddLogs(session, session.ResetThreshold + 2_000, codeAt: -1);
        observer = new RecordingObserver();
        observer.NoticeReceived += n =>
        {
            if (n.StartsWith("The context is nearly full")) Mark("reset started, asking for the handoff note");
            if (n.StartsWith("Context reset")) Mark("handoff note written. " + n);
        };
        await session.RunAsync("Without using tools, reply with just the vault code you found in the logs.", observer, CancellationToken.None);
        Mark($"answered after the reset ({observer.Notices.Count(n => n.StartsWith("Connection lost"))} drops)");
        File.WriteAllLines(Path.Combine(Path.GetTempPath(), "bb-reset-timings.txt"), log.Concat(observer.Notices.Select(n => "notice: " + n)));

        Assert.True(observer.StopReason == AgentStopReason.Completed, $"{observer.StopReason}: {observer.Detail}");
        Assert.Contains(observer.Notices, n => n.StartsWith("Context reset"));
        Assert.StartsWith(AgentSession.HandoffHeader, session.History.First(m => !m.FromUser).Content);
        Assert.Contains("ORCHID-7", session.History[^1].Content, StringComparison.OrdinalIgnoreCase);
    }

    private static void AddLogs(AgentSession session, int untilTokens, int codeAt)
    {
        for (var i = session.History.Count; session.TokensInUse() < untilTokens; i++)
        {
            var text = $"Log page {i}: the quick brown fox number {i} jumped over lazy dog number {i * 7}. " +
                       (i == codeAt ? "IMPORTANT: the vault code is ORCHID-7. " : "") +
                       string.Concat(Enumerable.Repeat($"Entry {i}: nothing unusual happened. ", 60));
            session.History.Add(new ChatMessage { Role = ChatRole.Assistant, Content = "", ToolCalls = [new ToolCall { Id = $"c{i}", Name = "read_file", Arguments = $$"""{"path":"log{{i}}.txt"}""" }] });
            session.History.Add(ChatMessage.ToolResult($"c{i}", text));
        }
    }

    /// <summary>A prompt that takes minutes to process; on links that cut silent connections it only finishes by resuming.</summary>
    [Fact]
    public async Task Slow_prompt_survives_dropped_connections()
    {
        var (server, http, context) = await ConnectAsync();
        var session = NewSession(http, server, contextTokens: 0); // no trimming: send it all
        FillHistory(session, Math.Min(context / 2, 110_000));

        await AssertAnswersPassword(session);
    }

    /// <summary>The server's own overflow error (context size unknown to BlackBox) leads to trimming and a retry.</summary>
    [Fact]
    public async Task Server_overflow_error_is_recovered()
    {
        var (server, http, context) = await ConnectAsync();
        Assert.SkipWhen(context > 100_000, "Needs a model loaded with at most 100k context to stay fast.");
        var session = NewSession(http, server, contextTokens: 0);
        FillHistory(session, (int)(context * 1.2));

        await AssertAnswersPassword(session);
    }

    /// <summary>The real model, with the test MCP server started as a child process: it must call the server's add tool and use the answer.</summary>
    [Fact]
    public async Task Model_calls_an_mcp_tool()
    {
        var (server, http, context) = await ConnectAsync();
        var dll = Path.Combine(AppContext.BaseDirectory, "BlackBox.McpTestServer.dll");
        await using var mcp = new BlackBox.Core.Mcp.McpHost();
        await mcp.ConnectAsync([new BlackBox.Core.Mcp.McpServerConfig { Name = "calc", Command = "dotnet", Args = [dll] }], CancellationToken.None);
        Assert.True(mcp.Statuses[0].State == BlackBox.Core.Mcp.McpServerState.Connected, mcp.Statuses[0].Summary);

        var connection = BackendConnection.Create(http, server);
        var ctx = new ToolContext { Platform = new FakePlatform(), Settings = new AgentSettings(), WorkingDirectory = Path.GetTempPath() };
        var registry = ToolRegistry.CreateDefault(false, extra: mcp.Tools);
        var prompt = SystemPrompt.Build("Test OS", null, Path.GetTempPath(), "", mcp.Connected);
        var session = new AgentSession(connection.Client, connection.Options, registry, ctx, prompt, contextTokens: context);
        var observer = new RecordingObserver();

        await session.RunAsync("Use the calc add tool to add 48213 and 27719, then reply with just the number it returned.", observer, CancellationToken.None);

        TestContext.Current.SendDiagnosticMessage("Tools run: " + string.Join(", ", observer.ToolsRun) + " | reply: " + session.History[^1].Content);
        Assert.True(observer.StopReason == AgentStopReason.Completed, $"{observer.StopReason}: {observer.Detail}");
        Assert.Contains(observer.ToolsRun, t => t.EndsWith("add", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("75932", observer.Results.Select(r => r.Text));
        Assert.Contains("75932", session.History[^1].Content);
    }
}
