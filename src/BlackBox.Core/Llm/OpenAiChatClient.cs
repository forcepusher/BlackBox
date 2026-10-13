using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace BlackBox.Core.Llm;

public class LlmException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>The server said the prompt does not fit in the model's context window.</summary>
public sealed class ContextOverflowException(string serverMessage)
    : LlmException("The conversation no longer fits in the model's context window: " + serverMessage)
{
    /// <summary>The context size the error names, if it names one (OpenAI, vLLM, llama.cpp and OpenRouter do).</summary>
    public int? ContextLimit { get; } = ParseLimit(serverMessage);

    internal static int? ParseLimit(string message)
    {
        foreach (var pattern in new[]
        {
            @"maximum context length is (\d+)",                 // OpenAI, vLLM, OpenRouter
            @"""n_ctx""\s*:\s*(\d+)",                            // llama.cpp server error details
            @"context (?:length|size|window) (?:is|of) (\d+)",   // other wordings
            @"\((\d+) tokens\) exceeds",                        // "... (4096 tokens) exceeds"
        })
        {
            var m = Regex.Match(message, pattern, RegexOptions.IgnoreCase);
            if (m.Success && int.TryParse(m.Groups[1].Value, out var n) && n >= 512)
                return n;
        }
        return null;
    }
}

/// <summary>
/// The connection broke before the reply was complete (network drop, proxy idle timeout).
/// Safe to retry: the server keeps the prompt it already processed in its cache.
/// </summary>
public sealed class ConnectionLostException(string message, bool receivedTokens, Exception? inner = null, string? toolCallInProgress = null)
    : LlmException(message, inner)
{
    /// <summary>True if part of the reply had already streamed in.</summary>
    public bool ReceivedTokens { get; } = receivedTokens;

    /// <summary>
    /// Name of the tool call the model was writing when the link dropped. Servers such as
    /// LM Studio send a call's arguments only once the call is complete, so a long call (a whole
    /// file) is a silent stretch that idle-cutting proxies kill, and resending repeats it.
    /// </summary>
    public string? ToolCallInProgress { get; } = toolCallInProgress;
}

/// <summary>
/// Minimal streaming client for any OpenAI-compatible /v1/chat/completions endpoint.
/// LM Studio, llama-server, vLLM and hosted APIs all go through this class.
/// </summary>
public sealed partial class OpenAiChatClient
{
    private readonly HttpClient _http;
    private readonly string _baseUrl;
    private readonly string? _apiKey;

    public OpenAiChatClient(HttpClient http, string baseUrl, string? apiKey)
    {
        _http = http;
        _baseUrl = NormalizeBaseUrl(baseUrl);
        _apiKey = string.IsNullOrWhiteSpace(apiKey) ? null : apiKey.Trim();
    }

    public string BaseUrl => _baseUrl;

    // Set once the server rejected our non-standard fields, so later requests stay plain.
    private bool _reasoningRejected;
    private bool _extrasRejected;

    /// <summary>
    /// Turns "host:port", "http://host:port", ".../v1/", ".../v1/chat/completions" or ".../v1/models"
    /// into "http(s)://host:port/v1". Without a scheme, http is assumed (see <see cref="ServerAddress"/>
    /// for trying both).
    /// </summary>
    public static string NormalizeBaseUrl(string url)
    {
        url = url.Trim();
        if (!url.Contains("://"))
            url = "http://" + url.TrimStart('/');
        url = url.TrimEnd('/');
        foreach (var endpoint in new[] { "/chat/completions", "/completions", "/models" })
        {
            if (url.EndsWith(endpoint, StringComparison.OrdinalIgnoreCase))
            {
                url = url[..^endpoint.Length].TrimEnd('/');
                break;
            }
        }
        if (!url.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
            url += "/v1";
        return url;
    }

    public async Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, _baseUrl + "/models");
        Authorize(req);
        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            throw new LlmException($"GET /models failed ({(int)resp.StatusCode}): {body.Clip(500)}");

        JsonNode? list;
        try { list = JsonNode.Parse(body); }
        catch (JsonException) { throw new LlmException("The answer at /models is not a model list (is this the right address?)."); }
        return list?["data"] is JsonArray data
            ? data.OfType<JsonObject>().Select(m => GetString(m, "id")).OfType<string>().ToList()
            : [];
    }

    /// <summary>
    /// Sends the conversation and streams the reply. Returns the fully assembled assistant message,
    /// including any tool calls.
    /// </summary>
    public async Task<ChatMessage> CompleteAsync(
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<ToolDefinition> tools,
        ChatRequestOptions options,
        Action<StreamDelta>? onDelta,
        CancellationToken ct)
    {
        var sendReasoning = options.SendReasoning && !_reasoningRejected;
        var sendExtras = !_extrasRejected;
        var payload = BuildRequest(messages, tools, options, sendReasoning, sendExtras);
        using var req = new HttpRequestMessage(HttpMethod.Post, _baseUrl + "/chat/completions")
        {
            Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        Authorize(req);

        HttpResponseMessage resp;
        try
        {
            resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new ConnectionLostException($"Could not reach {_baseUrl}: {ex.Message}", false, ex);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new ConnectionLostException($"{_baseUrl} did not answer in time.", false, ex);
        }

        using (resp)
        {
            if (!resp.IsSuccessStatusCode)
            {
                var status = (int)resp.StatusCode;
                var err = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                if (LooksLikeContextOverflow(err))
                    throw new ContextOverflowException(err.Clip(500));
                // Strict providers reject fields they do not know. Drop ours and try once more.
                if (status is 400 or 422 && (sendReasoning || sendExtras) && LooksLikeUnknownField(err))
                {
                    _reasoningRejected |= sendReasoning;
                    _extrasRejected |= sendExtras;
                    return await CompleteAsync(messages, tools, options, onDelta, ct).ConfigureAwait(false);
                }
                // A proxy whose link hiccuped, a server that is restarting or busy: worth sending
                // again, like a dropped connection, rather than ending a long run.
                if (status is 429 or >= 500)
                    throw new ConnectionLostException($"{_baseUrl} answered {status}: {err.Clip(300)}", false);
                throw new LlmException($"Chat request failed ({status}): {err.Clip(1000)}");
            }

            var received = false;
            string? toolCall = null;
            try
            {
                await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                using var reader = new StreamReader(stream, Encoding.UTF8);
                return await ReadStreamAsync(reader, d => { received = true; onDelta?.Invoke(d); }, ct,
                    onToolCall: name => { received = true; toolCall = name; }).ConfigureAwait(false);
            }
            // After Stop the read fails too; that is the stop, not a dropped connection to resend on.
            catch (Exception ex) when (!ct.IsCancellationRequested && ex is IOException or HttpRequestException or TaskCanceledException)
            {
                throw new ConnectionLostException($"The connection to {_baseUrl} dropped: {ex.Message}", received, ex, toolCall);
            }
        }
    }

    internal static async Task<ChatMessage> ReadStreamAsync(TextReader reader, Action<StreamDelta>? onDelta, CancellationToken ct, Action<string>? onToolCall = null)
    {
        var content = new StringBuilder();
        var reasoning = new StringBuilder();
        var calls = new SortedDictionary<int, (string Id, string Name, StringBuilder Args)>();
        var finished = false;
        string? finishReason = null;
        int? promptTokens = null, completionTokens = null;

        while (await reader.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
        {
            if (!line.StartsWith("data:", StringComparison.Ordinal))
                continue;
            var data = line[5..].Trim();
            if (data.Length == 0)
                continue;
            if (data == "[DONE]")
            {
                finished = true;
                break;
            }

            JsonNode? chunk;
            try { chunk = JsonNode.Parse(data); }
            catch (JsonException) { continue; }

            if (chunk?["error"] is JsonNode error)
            {
                var errorText = error["message"]?.ToString() ?? error.ToJsonString();
                throw LooksLikeContextOverflow(errorText) ? new ContextOverflowException(errorText) : new LlmException("Server error: " + errorText);
            }

            if (chunk?["usage"] is JsonObject usage)
            {
                promptTokens = GetInt(usage, "prompt_tokens") ?? promptTokens;
                completionTokens = GetInt(usage, "completion_tokens") ?? completionTokens;
            }
            if (chunk?["choices"] is not JsonArray { Count: > 0 } choices || choices[0] is not JsonObject choice)
                continue;
            if (choice["finish_reason"] is JsonValue fr && fr.GetValueKind() == JsonValueKind.String)
            {
                finished = true;
                finishReason = fr.GetValue<string>();
            }
            if (choice["delta"] is not JsonObject delta)
                continue;

            if ((GetString(delta, "reasoning_content") ?? GetString(delta, "reasoning")) is { Length: > 0 } r)
            {
                reasoning.Append(r);
                onDelta?.Invoke(new StreamDelta(StreamChannel.Reasoning, r));
            }

            if (GetString(delta, "content") is { Length: > 0 } c)
            {
                content.Append(c);
                onDelta?.Invoke(new StreamDelta(StreamChannel.Content, c));
            }

            if (delta["tool_calls"] is JsonArray toolCalls)
            {
                foreach (var tc in toolCalls)
                {
                    if (tc is null) continue;
                    var index = GetInt(tc, "index") ?? calls.Count;
                    if (!calls.TryGetValue(index, out var acc))
                        acc = ("", "", new StringBuilder());
                    if (GetString(tc, "id") is { Length: > 0 } id) acc.Id = id;
                    if (tc["function"] is JsonObject fn)
                    {
                        if (GetString(fn, "name") is { Length: > 0 } name)
                        {
                            acc.Name += name;
                            onToolCall?.Invoke(acc.Name);
                        }
                        if (GetString(fn, "arguments") is { } args) acc.Args.Append(args);
                    }
                    calls[index] = acc;
                }
            }
        }

        // A stream that just stops, without [DONE] or a finish reason, was cut by the network.
        if (!finished)
            throw new IOException("The reply stream ended before the server finished.");

        // Thinking that leaked into the visible text (a stray </think> from a quantized model,
        // or a server without a reasoning parser) belongs with the reasoning.
        var text = content.ToString();
        foreach (Match m in GemmaThought().Matches(text))
            reasoning.Append(m.Groups[1].Value);
        text = GemmaThought().Replace(text, "");
        var thinkEnd = text.LastIndexOf("</think>", StringComparison.Ordinal);
        if (thinkEnd >= 0)
        {
            reasoning.Append(text[..thinkEnd].Replace("<think>", "", StringComparison.Ordinal));
            text = text[(thinkEnd + "</think>".Length)..].TrimStart();
        }

        var message = new ChatMessage
        {
            Role = ChatRole.Assistant,
            Content = text,
            Reasoning = reasoning.Length > 0 ? reasoning.ToString() : null,
            FinishReason = finishReason,
            PromptTokens = promptTokens,
            CompletionTokens = completionTokens,
        };
        if (calls.Count > 0)
        {
            message.ToolCalls = calls.Values
                .Where(c => c.Name.Length > 0)
                .Select((c, i) => new ToolCall
                {
                    Id = c.Id.Length > 0 ? c.Id : $"call_{Guid.NewGuid():N}"[..16] + i,
                    Name = c.Name,
                    Arguments = c.Args.ToString(),
                })
                .ToList();
        }
        return message;
    }

    internal static JsonObject BuildRequest(IReadOnlyList<ChatMessage> messages, IReadOnlyList<ToolDefinition> tools, ChatRequestOptions options, bool sendReasoning = false, bool sendExtras = true)
    {
        var msgs = new JsonArray();
        foreach (var m in messages)
            msgs.Add(SerializeMessage(m, sendReasoning));

        var req = new JsonObject
        {
            ["model"] = options.Model,
            ["messages"] = msgs,
            ["stream"] = true,
        };
        if (options.MaxTokens is { } mt) req["max_tokens"] = mt;

        if (tools.Count > 0)
        {
            var arr = new JsonArray();
            foreach (var tool in tools)
            {
                arr.Add(new JsonObject
                {
                    ["type"] = "function",
                    ["function"] = new JsonObject
                    {
                        ["name"] = tool.Name,
                        ["description"] = tool.Description,
                        ["parameters"] = tool.Parameters.DeepClone(),
                    },
                });
            }
            req["tools"] = arr;
        }

        if (sendExtras)
        {
            // Exact prompt and reply sizes, so the agent knows how full the context really is.
            req["stream_options"] = new JsonObject { ["include_usage"] = true };
            if (options.Extra is not null)
                foreach (var (key, value) in options.Extra)
                    req[key] = value?.DeepClone();
        }

        return req;
    }

    private static JsonObject SerializeMessage(ChatMessage m, bool sendReasoning)
    {
        var obj = new JsonObject { ["role"] = m.Role };

        if (m.Parts is { Count: > 0 })
        {
            var parts = new JsonArray();
            foreach (var part in m.Parts)
            {
                if (part.IsImage)
                {
                    parts.Add(new JsonObject
                    {
                        ["type"] = "image_url",
                        ["image_url"] = new JsonObject
                        {
                            ["url"] = $"data:{part.ImageMime};base64,{Convert.ToBase64String(part.ImageBytes!)}",
                        },
                    });
                }
                else
                {
                    parts.Add(new JsonObject { ["type"] = "text", ["text"] = part.Text ?? "" });
                }
            }
            obj["content"] = parts;
        }
        else
        {
            obj["content"] = m.Content ?? "";
        }

        if (m.HasToolCalls)
        {
            var calls = new JsonArray();
            foreach (var c in m.ToolCalls!)
            {
                calls.Add(new JsonObject
                {
                    ["id"] = c.Id,
                    ["type"] = "function",
                    ["function"] = new JsonObject
                    {
                        ["name"] = c.Name,
                        ["arguments"] = string.IsNullOrWhiteSpace(c.Arguments) ? "{}" : c.Arguments,
                    },
                });
            }
            obj["tool_calls"] = calls;
        }

        if (m.ToolCallId is not null)
            obj["tool_call_id"] = m.ToolCallId;

        // Qwen3.5+ templates re-render every past <think> block ("preserve thinking"). Without
        // this field they render empty, which both hides the model's earlier reasoning from it
        // and breaks the server's prompt cache at every assistant turn.
        if (sendReasoning && m.Role == ChatRole.Assistant && !string.IsNullOrEmpty(m.Reasoning))
            obj["reasoning_content"] = m.Reasoning;

        return obj;
    }

    private static bool LooksLikeUnknownField(string err) =>
        new[] { "unrecognized", "unknown", "not permitted", "extra", "unexpected", "reasoning", "chat_template_kwargs" }
            .Any(w => err.Contains(w, StringComparison.OrdinalIgnoreCase));

    internal static bool LooksLikeContextOverflow(string err) =>
        new[] { "context length", "context_length", "context window", "n_ctx", "maximum context", "too many tokens", "exceeds the context", "context size", "context overflow" }
            .Any(w => err.Contains(w, StringComparison.OrdinalIgnoreCase));

    private void Authorize(HttpRequestMessage req)
    {
        if (_apiKey is not null)
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
    }

    private static string? GetString(JsonNode node, string key) =>
        node[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    /// <summary>A whole number, or null for anything else, so an odd value can't break a reply.</summary>
    private static int? GetInt(JsonNode node, string key) =>
        node[key] is JsonValue v && v.TryGetValue<int>(out var n) ? n : null;

    /// <summary>Gemma 4 marks its thinking as a "thought" channel.</summary>
    [GeneratedRegex(@"<\|channel>thought\s*(.*?)<channel\|>\s*", RegexOptions.Singleline)]
    private static partial Regex GemmaThought();
}
