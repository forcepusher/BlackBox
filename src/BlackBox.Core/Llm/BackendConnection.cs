using System.Text.Json.Nodes;
using BlackBox.Core.Settings;

namespace BlackBox.Core.Llm;

/// <summary>A ready-to-use client plus the request options that go with the selected backend.</summary>
public sealed record BackendConnection(OpenAiChatClient Client, ChatRequestOptions Options, bool Vision)
{
    /// <summary>Connection to the local llama-server, with llama.cpp-specific sampling fields.</summary>
    public static BackendConnection ForLocal(HttpClient http, LlamaServerHost server, AgentSettings agent) => new(
        new OpenAiChatClient(http, server.BaseUrl, null),
        new ChatRequestOptions
        {
            Model = "local",
            Temperature = agent.Temperature,
            TopP = agent.TopP,
            MaxTokens = agent.MaxTokensPerReply,
            Extra = new JsonObject
            {
                ["top_k"] = agent.TopK,
                ["min_p"] = agent.MinP,
                ["repeat_penalty"] = agent.RepeatPenalty,
                ["chat_template_kwargs"] = new JsonObject { ["enable_thinking"] = agent.EnableThinking },
            },
        },
        server.HasVision);

    /// <summary>
    /// Connection to a remote OpenAI-compatible API. Only standard fields are sent, since strict
    /// providers reject unknown ones like top_k or min_p.
    /// </summary>
    public static BackendConnection ForRemote(HttpClient http, RemoteApiSettings remote, AgentSettings agent) => new(
        new OpenAiChatClient(http, remote.BaseUrl, remote.ApiKey),
        new ChatRequestOptions
        {
            Model = remote.Model,
            Temperature = agent.Temperature,
            TopP = agent.TopP,
            MaxTokens = agent.MaxTokensPerReply,
        },
        remote.SupportsVision);
}
