using BlackBox.Core.Agent;
using BlackBox.Core.Llm;
using BlackBox.Core.Settings;
using BlackBox.Core.Tools;
using Xunit;

namespace BlackBox.Core.Tests;

/// <summary>
/// Opt-in checks against a real LM Studio server. Set BB_LMSTUDIO_URL (e.g. http://host:1234/v1)
/// and BB_LMSTUDIO_MODEL to run them; they send prompts larger than the model's context.
/// </summary>
public class LmStudioOverflowTests
{
    private static readonly string? Url = Environment.GetEnvironmentVariable("BB_LMSTUDIO_URL");
    private static readonly string? Model = Environment.GetEnvironmentVariable("BB_LMSTUDIO_MODEL");

    [Theory]
    [InlineData(false)] // context size unknown: the server's overflow error triggers trimming
    [InlineData(true)]  // context size known: trimming happens before anything is sent
    public async Task Oversized_history_still_gets_an_answer(bool knowContext)
    {
        Assert.SkipWhen(Url is null || Model is null, "Set BB_LMSTUDIO_URL and BB_LMSTUDIO_MODEL to run.");
        var server = new ServerSettings { BaseUrl = Url!, Model = Model! };
        using var http = LlmHttp.Create(TimeSpan.FromMinutes(20));
        var context = await ServerProbe.EnsureLoadedAsync(http, server, null, CancellationToken.None);
        Assert.NotNull(context);

        var connection = BackendConnection.Create(http, server, new AgentSettings { EnableThinking = false });
        var ctx = new ToolContext { Platform = new FakePlatform(), Settings = new AgentSettings(), WorkingDirectory = Path.GetTempPath() };
        var session = new AgentSession(connection.Client, connection.Options, ToolRegistry.CreateDefault(false), ctx,
            "You are a test assistant.", contextTokens: knowContext ? Math.Min(context!.Value, new ServerSettings().MaxPromptTokens) : 0);

        // ~1.2x the context in old tool output, between the task and the latest message.
        session.History.Add(ChatMessage.User("Remember: the password is BLUEFISH."));
        var filler = string.Concat(Enumerable.Repeat("The quick brown fox jumps over the lazy dog. ", 400));
        for (var i = 0; session.EstimateTokens() < context!.Value * 1.2; i++)
        {
            session.History.Add(new ChatMessage { Role = ChatRole.Assistant, Content = "", ToolCalls = [new ToolCall { Id = $"c{i}", Name = "read_file", Arguments = """{"path":"x.txt"}""" }] });
            session.History.Add(ChatMessage.ToolResult($"c{i}", filler));
        }

        var observer = new RecordingObserver();
        await session.RunAsync("Without using tools, reply with just the password I gave you at the start.", observer, CancellationToken.None);

        Assert.True(observer.StopReason == AgentStopReason.Completed, $"{observer.StopReason}: {observer.Detail}");
        Assert.Contains("BLUEFISH", session.History[^1].Content, StringComparison.OrdinalIgnoreCase);
    }
}
