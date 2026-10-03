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
            // Non-standard sampling fields that LM Studio and llama.cpp understand. Strict
            // providers reject unknown fields; the client then retries once without them.
            Extra = server.SendExtras
                ? new JsonObject
                {
                    ["top_k"] = agent.TopK,
                    ["min_p"] = agent.MinP,
                    ["repeat_penalty"] = agent.RepeatPenalty,
                    ["chat_template_kwargs"] = new JsonObject
                    {
                        ["enable_thinking"] = agent.EnableThinking,
                        ["preserve_thinking"] = true,
                    },
                }
                : null,
        },
        server.SupportsVision,
        server.ContextSize);
}