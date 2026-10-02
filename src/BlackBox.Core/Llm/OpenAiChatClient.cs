using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace BlackBox.Core.Llm;

public sealed class LlmException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Minimal streaming client for any OpenAI-compatible /v1/chat/completions endpoint.
/// Both the bundled llama-server and remote APIs go through this class.
/// </summary>
public sealed class OpenAiChatClient
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

    /// <summary>Turns "http://host:port", ".../v1" or ".../v1/" into ".../v1".</summary>
    public static string NormalizeBaseUrl(string url)
    {
        url = url.Trim().TrimEnd('/');
        if (url.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
            url = url[..^"/chat/completions".Length];
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
            throw new LlmException($"GET /models failed ({(int)resp.StatusCode}): {Truncate(body, 500)}");

        var ids = new List<string>();
        if (JsonNode.Parse(body)?["data"] is JsonArray data)
            foreach (var m in data)
                if (m?["id"]?.GetValue<string>() is { } id)
                    ids.Add(id);
        return ids;
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
        var payload = BuildRequest(messages, tools, options);
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
            throw new LlmException($"Could not reach {_baseUrl}: {ex.Message}", ex);
        }

        using (resp)
        {
            if (!resp.IsSuccessStatusCode)
            {
                var err = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                throw new LlmException($"Chat request failed ({(int)resp.StatusCode}): {Truncate(err, 1000)}");
            }

            await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            return await ReadStreamAsync(reader, onDelta, ct).ConfigureAwait(false);
        }
    }

    internal static async Task<ChatMessage> ReadStreamAsync(TextReader reader, Action<StreamDelta>? onDelta, CancellationToken ct)
    {
        var content = new StringBuilder();
        var reasoning = new StringBuilder();
        var calls = new SortedDictionary<int, (string Id, string Name, StringBuilder Args)>();

        while (await reader.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
        {
            if (!line.StartsWith("data:", StringComparison.Ordinal))
                continue;
            var data = line[5..].Trim();
            if (data.Length == 0)
                continue;
            if (data == "[DONE]")
                break;

            JsonNode? chunk;
            try { chunk = JsonNode.Parse(data); }
            catch (JsonException) { continue; }

            if (chunk?["error"] is JsonNode error)
                throw new LlmException("Server error: " + (error["message"]?.ToString() ?? error.ToJsonString()));

            if (chunk?["choices"] is not JsonArray { Count: > 0 } choices || choices[0]?["delta"] is not JsonObject delta)
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
                    var index = tc["index"]?.GetValue<int>() ?? calls.Count;
                    if (!calls.TryGetValue(index, out var acc))
                        acc = ("", "", new StringBuilder());
                    if (GetString(tc, "id") is { Length: > 0 } id) acc.Id = id;
                    if (tc["function"] is JsonObject fn)
                    {
                        if (GetString(fn, "name") is { Length: > 0 } name) acc.Name += name;
                        if (GetString(fn, "arguments") is { } args) acc.Args.Append(args);
                    }
                    calls[index] = acc;
                }
            }
        }

        var message = new ChatMessage
        {
            Role = ChatRole.Assistant,
            Content = content.ToString(),
            Reasoning = reasoning.Length > 0 ? reasoning.ToString() : null,
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

    internal static JsonObject BuildRequest(IReadOnlyList<ChatMessage> messages, IReadOnlyList<ToolDefinition> tools, ChatRequestOptions options)
    {
        var msgs = new JsonArray();
        foreach (var m in messages)
            msgs.Add(SerializeMessage(m));

        var req = new JsonObject
        {
            ["model"] = options.Model,
            ["messages"] = msgs,
            ["stream"] = true,
        };
        if (options.Temperature is { } t) req["temperature"] = t;
        if (options.TopP is { } p) req["top_p"] = p;
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

        if (options.Extra is not null)
            foreach (var (key, value) in options.Extra)
                req[key] = value?.DeepClone();

        return req;
    }

    private static JsonObject SerializeMessage(ChatMessage m)
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

        return obj;
    }

    private void Authorize(HttpRequestMessage req)
    {
        if (_apiKey is not null)
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
    }

    private static string? GetString(JsonNode node, string key) =>
        node[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}
