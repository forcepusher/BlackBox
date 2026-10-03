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
        var context = await ServerProbe.EnsureLoadedAsync(http, server, null, CancellationToken.None);
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

    /// <summary>The normal path: the run outgrows the prompt budget and gets compacted before sending.</summary>
    [Fact]
    public async Task Long_run_is_compacted_and_still_knows_the_task()
    {
        var (server, http, context) = await ConnectAsync();
        var budget = Math.Min(context, server.MaxPromptTokens);
        var session = NewSession(http, server, budget);
        FillHistory(session, (int)(budget * 1.2));

        await AssertAnswersPassword(session);
        Assert.StartsWith(AgentSession.SummaryHeader, session.History[1].Content);
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
}
