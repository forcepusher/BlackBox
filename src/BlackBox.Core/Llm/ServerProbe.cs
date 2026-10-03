using System.Text.Json.Nodes;
using BlackBox.Core.Settings;

namespace BlackBox.Core.Llm;

public sealed record ProbeResult(bool Reachable, IReadOnlyList<string> Models, int? LoadedContext, bool? ModelLoaded, string Message);

/// <summary>
/// Checks the configured server: is it up, which models does it offer, and (for LM Studio) how
/// large a context the chosen model was loaded with.
/// </summary>
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

        var (context, loaded) = await TryLmStudioContextAsync(http, server, ct).ConfigureAwait(false);
        return new ProbeResult(true, models, context, loaded, models.Count == 0 ? "Connected, but the server lists no models." : "Connected.");
    }

    /// <summary>
    /// Makes sure the model is loaded (LM Studio loads models on first use) and returns the
    /// context length it was loaded with, if the server reports one.
    /// </summary>
    public static async Task<int?> EnsureLoadedAsync(HttpClient http, ServerSettings server, Action? onLoading, CancellationToken ct)
    {
        var (context, loaded) = await TryLmStudioContextAsync(http, server, ct).ConfigureAwait(false);
        if (loaded != false)
            return context;

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
        return (await TryLmStudioContextAsync(http, server, ct).ConfigureAwait(false)).Context;
    }

    /// <summary>
    /// LM Studio's native REST API reports the context length a model is loaded with;
    /// OpenAI's /models does not. Other servers simply don't have this endpoint.
    /// </summary>
    public static async Task<(int? Context, bool? Loaded)> TryLmStudioContextAsync(HttpClient http, ServerSettings server, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(server.Model))
            return (null, null);
        try
        {
            var root = OpenAiChatClient.NormalizeBaseUrl(server.BaseUrl)[..^"/v1".Length];
            using var req = new HttpRequestMessage(HttpMethod.Get, $"{root}/api/v0/models/{Uri.EscapeDataString(server.Model)}");
            if (!string.IsNullOrWhiteSpace(server.ApiKey))
                req.Headers.Authorization = new("Bearer", server.ApiKey.Trim());
            using var resp = await http.SendAsync(req, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                return (null, null);
            var json = JsonNode.Parse(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            var state = json?["state"]?.GetValue<string>();
            var loaded = json?["loaded_context_length"]?.GetValue<int>();
            // Only a loaded model has a real context length. max_context_length is what the model
            // could support (often 256k), not what LM Studio will actually load it with.
            return (state == "loaded" ? loaded : null, state is null ? null : state == "loaded");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or InvalidOperationException && !ct.IsCancellationRequested)
        {
            return (null, null);
        }
    }
}
