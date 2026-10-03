using System.Text.Json.Nodes;
using BlackBox.Core.Settings;

namespace BlackBox.Core.Llm;

/// <summary>A ready-to-use client plus the request options for the configured server.</summary>
public sealed record BackendConnection(OpenAiChatClient Client, ChatRequestOptions Options, bool Vision, int ContextTokens)
{
    public static BackendConnection Create(HttpClient http, ServerSettings server, AgentSettings agent) => new(
        new OpenAiChatClient(http, server.BaseUrl, server.ApiKey),
        new ChatRequestOptions
        {
            Model = server.Model,
            Temperature = agent.Temperature,
            TopP = agent.TopP,
            MaxTokens = agent.MaxTokensPerReply,
            MaxReasoningChars = agent.MaxThinkingTokens > 0 ? agent.MaxThinkingTokens * 4 : null,
            // Thinking models (Qwen3.5+, Gemma 4) expect their earlier reasoning back.
            SendReasoning = true,
            // Non-standard fields that LM Studio, llama-server and vLLM understand. A stricter
            // server that rejects them gets the request again without them (see OpenAiChatClient).
            Extra = new JsonObject
            {
                ["top_k"] = agent.TopK,
                ["min_p"] = agent.MinP,
                ["repeat_penalty"] = agent.RepeatPenalty,
                ["chat_template_kwargs"] = new JsonObject { ["preserve_thinking"] = true },
            },
        },
        server.SupportsVision,
        server.MaxPromptTokens > 0 ? Math.Min(server.ContextSize, server.MaxPromptTokens) : server.ContextSize);
}