using System.Text.Json.Nodes;
using BlackBox.Core.Settings;

namespace BlackBox.Core.Llm;

/// <summary>A ready-to-use client plus the request options for the configured server.</summary>
/// <remarks>
/// No sampling parameters are sent: temperature, top-p/k, reply length and thinking are the
/// server's model configuration (in LM Studio, per model).
/// </remarks>
public sealed record BackendConnection(OpenAiChatClient Client, ChatRequestOptions Options, bool Vision, int ContextTokens)
{
    public static BackendConnection Create(HttpClient http, ServerSettings server) => new(
        new OpenAiChatClient(http, server.BaseUrl, server.ApiKey),
        new ChatRequestOptions
        {
            Model = server.ModelInUse,
            // Thinking models (Qwen3.5+, Gemma 4) expect their earlier reasoning back, in the
            // message field and via the template switch that llama-server and vLLM honor.
            SendReasoning = true,
            Extra = new JsonObject { ["chat_template_kwargs"] = new JsonObject { ["preserve_thinking"] = true } },
        },
        server.SupportsVision,
        server.ContextSize);
}
