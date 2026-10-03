using System.Text.Json.Nodes;
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
/// <item>llama.cpp llama-server: <c>GET /props</c> → <c>default_generation_settings.n_ctx</c></item>
/// <item>vLLM: <c>GET /v1/models</c> → <c>max_model_len</c></item>
/// <item>OpenRouter and similar: <c>GET /v1/models</c> → <c>context_length</c></item>
/// </list>
/// Anything else keeps the context size saved in Settings.
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
            && Int(props["default_generation_settings"]?["n_ctx"]) is { } nctx)
            return new ModelFacts(nctx, true, null);

        // vLLM / OpenRouter style metadata on the model list
        if (await GetJsonAsync(http, server, $"{v1}/models", ct).ConfigureAwait(false) is { } list && list["data"] is JsonArray data)
        {
            var entry = data.FirstOrDefault(m => m?["id"] is JsonValue id && id.TryGetValue<string>(out var s) && s == server.Model);
            if ((Int(entry?["max_model_len"]) ?? Int(entry?["context_length"])) is { } len)
                return new ModelFacts(len, null, null);
        }
        return default;
    }

    private static async Task<JsonNode?> GetJsonAsync(HttpClient http, ServerSettings server, string url, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            if (!string.IsNullOrWhiteSpace(server.ApiKey))
                req.Headers.Authorization = new("Bearer", server.ApiKey.Trim());
            using var resp = await http.SendAsync(req, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                return null;
            return JsonNode.Parse(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException && !ct.IsCancellationRequested)
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
