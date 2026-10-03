using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using BlackBox.Core.Settings;

namespace BlackBox.Core.Llm;

public sealed record ProbeResult(bool Reachable, IReadOnlyList<string> Models, int? LoadedContext, bool? Vision, string Message);

/// <summary>What the server says about the configured model; nulls where it doesn't say.</summary>
public readonly record struct ModelFacts(int? Context, bool? Loaded, bool? Vision);

/// <summary>
/// Checks the configured server: is it up, which models does it offer, and how large a context
/// the chosen model actually runs with.
/// </summary>
/// <remarks>
/// The OpenAI API has no field for the context length, so each server is asked its own way:
/// <list type="bullet">
/// <item>LM Studio: <c>GET /api/v0/models/{id}</c> → <c>loaded_context_length</c> (only while loaded)</item>
/// <item>llama.cpp llama-server: <c>GET /props</c> → <c>default_generation_settings.n_ctx</c> (or <c>n_ctx</c>)</item>
/// <item>Ollama: <c>GET /api/ps</c> → <c>context_length</c> of the running model, else <c>POST /api/show</c> →
/// <c>num_ctx</c> in <c>parameters</c>, else <c>model_info.*.context_length</c></item>
/// <item>vLLM: <c>GET /v1/models</c> → <c>max_model_len</c></item>
/// <item>OpenRouter and similar: <c>GET /v1/models</c> → <c>context_length</c></item>
/// </list>
/// The first that answers wins. Anything else gets <see cref="ServerSettings.FallbackContextSize"/>;
/// if that is too big, the server's "too long" error names the real size and the agent adjusts.
/// </remarks>
public static class ServerProbe
{
    public static async Task<ProbeResult> CheckAsync(HttpClient http, ServerSettings server, CancellationToken ct)
    {
        IReadOnlyList<string> models;
        try
        {
            models = await new OpenAiChatClient(http, server.BaseUrl, server.ApiKey).ListModelsAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is LlmException or HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return new ProbeResult(false, [], null, null, "Can't reach the server: " + ex.Message);
        }

        var facts = await TryContextAsync(http, server, ct).ConfigureAwait(false);
        return new ProbeResult(true, models, facts.Context, facts.Vision, models.Count == 0 ? "Connected, but the server lists no models." : "Connected.");
    }

    /// <summary>
    /// Makes sure the model is loaded (LM Studio loads models on first use) and returns the
    /// context length it runs with, if the server reports one.
    /// </summary>
    public static async Task<int?> EnsureLoadedAsync(HttpClient http, ServerSettings server, Action? onLoading, CancellationToken ct)
    {
        var facts = await TryContextAsync(http, server, ct).ConfigureAwait(false);
        if (facts.Loaded != false)
            return facts.Context;

        onLoading?.Invoke();
        try
        {
            var client = new OpenAiChatClient(http, server.BaseUrl, server.ApiKey);
            await client.CompleteAsync([ChatMessage.User("Reply with OK.")], [],
                new ChatRequestOptions { Model = server.Model, MaxTokens = 1 }, null, ct).ConfigureAwait(false);
        }
        catch (LlmException)
        {
            return null; // the real request will report the problem
        }
        return (await TryContextAsync(http, server, ct).ConfigureAwait(false)).Context;
    }

    /// <summary>Context length, load state and image support of the configured model.</summary>
    public static async Task<ModelFacts> TryContextAsync(HttpClient http, ServerSettings server, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(server.Model))
            return default;
        var v1 = OpenAiChatClient.NormalizeBaseUrl(server.BaseUrl);
        var root = v1[..^"/v1".Length];

        // LM Studio
        if (await GetJsonAsync(http, server, $"{root}/api/v0/models/{Uri.EscapeDataString(server.Model)}", ct).ConfigureAwait(false) is { } lms
            && lms["state"] is JsonValue stateValue && stateValue.TryGetValue<string>(out var state))
        {
            // Only a loaded model has a real context length. max_context_length is what the model
            // could support (often 256k), not what LM Studio will actually load it with.
            // "vlm" models take images, "llm" models don't.
            bool? vision = lms["type"] is JsonValue t && t.TryGetValue<string>(out var type) && type is "vlm" or "llm" ? type == "vlm" : null;
            return new ModelFacts(state == "loaded" ? Int(lms["loaded_context_length"]) : null, state == "loaded", vision);
        }

        // llama.cpp llama-server
        if (await GetJsonAsync(http, server, $"{root}/props", ct).ConfigureAwait(false) is { } props
            && (Int(props["default_generation_settings"]?["n_ctx"]) ?? Int(props["n_ctx"])) is { } nctx)
            return new ModelFacts(nctx, true, null);

        // Ollama: the context a model runs with is its num_ctx, not what the model could do.
        if (await OllamaContextAsync(http, server, root, ct).ConfigureAwait(false) is { } ollama)
            return new ModelFacts(ollama, null, null);

        // vLLM / OpenRouter style metadata on the model list
        if (await GetJsonAsync(http, server, $"{v1}/models", ct).ConfigureAwait(false) is { } list && list["data"] is JsonArray data)
        {
            var entry = data.FirstOrDefault(m => m?["id"] is JsonValue id && id.TryGetValue<string>(out var s) && s == server.Model);
            if ((Int(entry?["max_model_len"]) ?? Int(entry?["context_length"])) is { } len)
                return new ModelFacts(len, null, null);
        }
        return default;
    }

    private static async Task<int?> OllamaContextAsync(HttpClient http, ServerSettings server, string root, CancellationToken ct)
    {
        if (await GetJsonAsync(http, server, $"{root}/api/version", ct).ConfigureAwait(false) is not { } version || version["version"] is null)
            return null;
        if (await GetJsonAsync(http, server, $"{root}/api/ps", ct).ConfigureAwait(false) is { } ps && ps["models"] is JsonArray running)
        {
            var entry = running.FirstOrDefault(m => (m?["name"] ?? m?["model"])?.ToString() == server.Model);
            if (Int(entry?["context_length"]) is { } loaded)
                return loaded;
        }
        if (await GetJsonAsync(http, server, $"{root}/api/show", ct, new JsonObject { ["model"] = server.Model }).ConfigureAwait(false) is not { } show)
            return null;
        if (show["parameters"]?.ToString() is { } parameters
            && Regex.Match(parameters, @"(?m)^\s*num_ctx\s+(\d+)") is { Success: true } numCtx)
            return int.Parse(numCtx.Groups[1].Value);
        return show["model_info"] is JsonObject info
            ? info.Where(p => p.Key.EndsWith(".context_length", StringComparison.Ordinal)).Select(p => Int(p.Value)).FirstOrDefault(v => v is not null)
            : null;
    }

    private static async Task<JsonNode?> GetJsonAsync(HttpClient http, ServerSettings server, string url, CancellationToken ct, JsonObject? postBody = null)
    {
        try
        {
            using var req = new HttpRequestMessage(postBody is null ? HttpMethod.Get : HttpMethod.Post, url);
            if (postBody is not null)
                req.Content = new StringContent(postBody.ToJsonString(), Encoding.UTF8, "application/json");
            if (!string.IsNullOrWhiteSpace(server.ApiKey))
                req.Headers.Authorization = new("Bearer", server.ApiKey.Trim());
            using var resp = await http.SendAsync(req, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                return null;
            return JsonNode.Parse(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException && !ct.IsCancellationRequested)
        {
            return null;
        }
    }

    private static int? Int(JsonNode? node)
    {
        if (node is not JsonValue v) return null;
        if (v.TryGetValue<long>(out var l)) return (int)Math.Min(l, int.MaxValue);
        if (v.TryGetValue<int>(out var i)) return i;
        if (v.TryGetValue<double>(out var d)) return (int)d;
        return null;
    }
}
