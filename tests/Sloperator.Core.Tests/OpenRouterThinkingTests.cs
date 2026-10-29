using System.Text.Json.Nodes;
using Sloperator.Core.Llm;
using Sloperator.Core.Settings;
using Xunit;

namespace Sloperator.Core.Tests;

/// <summary>
/// Opt-in: Claude through OpenRouter shows its thinking and gets it back after a tool call. Set
/// BB_OPENROUTER_KEY; BB_OPENROUTER_MODEL picks the model (anthropic/claude-haiku-5.5). Costs well
/// under a cent.
/// </summary>
public class OpenRouterThinkingTests
{
    private static readonly string? Key = Environment.GetEnvironmentVariable("BB_OPENROUTER_KEY");
    private static readonly string Model = Environment.GetEnvironmentVariable("BB_OPENROUTER_MODEL") ?? "anthropic/claude-haiku-5.5";

    [Fact]
    public async Task Claude_thinks_aloud_and_keeps_its_thinking_over_a_tool_call()
    {
        Assert.SkipWhen(Key is null, "Set BB_OPENROUTER_KEY to run.");
        var server = new ServerSettings { BaseUrl = "https://openrouter.ai/api/v1", Model = Model, ApiKey = Key!, Effort = "high" };
        using var http = LlmHttp.Create(TimeSpan.FromMinutes(5));
        var probe = await ServerProbe.CheckAsync(http, server, TestContext.Current.CancellationToken);
        Assert.True(probe.Reachable, probe.Message);
        server.EffortLevels = probe.Efforts ?? [];
        Assert.Contains("high", server.EffortLevels);

        var connection = BackendConnection.Create(http, server);
        Assert.Equal("high", connection.Options.Effort);
        var tools = new List<ToolDefinition>
        {
            new()
            {
                Name = "record", Description = "Records a number and says the time.",
                Parameters = JsonNode.Parse("""{"type":"object","properties":{"n":{"type":"integer"}},"required":["n"]}""")!.AsObject(),
            },
        };
        List<ChatMessage> history = [ChatMessage.User("Work out carefully, step by step in your head, the sum of the primes below 120, then call record with n = that sum. After that tell me the time.")];
        var first = await connection.Client.CompleteAsync(history, tools, connection.Options, null, TestContext.Current.CancellationToken);
        Assert.True(first.HasToolCalls, first.Content);
        Assert.False(string.IsNullOrWhiteSpace(first.Reasoning), "no thinking shown");
        Assert.Contains(first.ReasoningDetails!, d => (string?)d!["signature"] is { Length: > 0 });
        history.Add(first);
        history.Add(ChatMessage.ToolResult(first.ToolCalls![0].Id, "14:52"));

        var second = await connection.Client.CompleteAsync(history, tools, connection.Options, null, TestContext.Current.CancellationToken);
        Assert.Contains("14:52", second.Content);

        // The thinking went back: the prompt is longer by it than one without it.
        var bare = history.Select(m => m.ReasoningDetails is null ? m : new ChatMessage { Role = m.Role, Content = m.Content, ToolCalls = m.ToolCalls }).ToList();
        var without = await connection.Client.CompleteAsync(bare, tools, connection.Options, null, TestContext.Current.CancellationToken);
        TestContext.Current.SendDiagnosticMessage($"prompt with thinking {second.PromptTokens}, without {without.PromptTokens}; first thought {first.CompletionTokens} tokens");
        Assert.True(second.PromptTokens > without.PromptTokens + 50, $"{second.PromptTokens} vs {without.PromptTokens}");
    }
}
