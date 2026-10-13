using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using BlackBox.Core.Settings;

namespace BlackBox.Core.Llm;

/// <param name="Models">The models the server offers that can chat, in its own order (embedding models and the like left out).</param>
/// <param name="LoadedContext">Context size the server runs <paramref name="Model"/> with, if it says; also <paramref name="Vision"/> and <paramref name="Loaded"/> are about that model.</param>
/// <param name="Loaded">False when the server says the model is not loaded yet (LM Studio loads on first use), so it has no context size yet.</param>
/// <param name="BaseUrl">The complete address that answered ("host:1234" typed, "http://host:1234/v1" found).</param>
/// <param name="Model">The model to use: the configured one while the server offers it, else the strongest it offers (see <see cref="ModelChoice"/>); null when there is none.</param>
/// <param name="AutoPicked">True when <paramref name="Model"/> was chosen here, because none was configured or the server doesn't offer it.</param>
/// <param name="ModelSize">For a model chosen here, its size and quantization ("31B, 5-bit") if known.</param>
public sealed record ProbeResult(bool Reachable, IReadOnlyList<string> Models, int? LoadedContext, bool? Vision, string Message, bool? Loaded = null, string? BaseUrl = null,
    string? Model = null, bool AutoPicked = false, string? ModelSize = null);

/// <summary>What the server says about the model in use; nulls where it doesn't say.</summary>
public readonly record struct ModelFacts(int? Context, bool? Loaded, bool? Vision);

/// <summary>
/// Checks the configured server: is it up, which models does it offer, which of them to use, and
/// how large a context that model actually runs with.
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
    /// <summary>
    /// The address may be typed in any form (see <see cref="ServerAddress"/>); the first form that
    /// lists models wins and comes back as <see cref="ProbeResult.BaseUrl"/>, for the caller to
    /// keep if the address hasn't changed in the meantime. The model to use is the configured
    /// <see cref="ServerSettings.Model"/> while the server offers it, else the strongest one it
    /// offers (<see cref="ModelChoice"/>).
    /// </summary>
    public static async Task<ProbeResult> CheckAsync(HttpClient http, ServerSettings server, CancellationToken ct)
    {
        var candidates = ServerAddress.Candidates(server.BaseUrl);
        if (candidates.Count == 0)
            return new ProbeResult(false, [], null, null, server.BaseUrl.Trim().Length == 0 ? "No server address." : "That is not a server address.");

        // https and http are tried at the same time; whichever answers is the one.
        var attempts = candidates.Select(url => ListModelsAsync(http, url, server.ApiKey, ct)).ToList();
        await Task.WhenAll(attempts).ConfigureAwait(false);
        for (var i = 0; i < candidates.Count; i++)
        {
            if (attempts[i].Result.Models is not { } models)
                continue;
            var found = new ServerSettings { BaseUrl = candidates[i], ApiKey = server.ApiKey };
            var catalog = await CatalogAsync(http, found, models, ct).ConfigureAwait(false);
            found.Model = ModelChoice.Choose(server.Model, catalog) ?? "";
            var auto = found.Model.Length > 0 && found.Model != server.Model;
            var facts = await TryContextAsync(http, found, ct).ConfigureAwait(false);
            return new ProbeResult(true, catalog.Where(m => m.Chat).Select(m => m.Id).ToList(), facts.Context, facts.Vision,
                models.Count == 0 ? "Connected, but the server lists no models." : "Connected.", facts.Loaded, candidates[i],
                found.Model.Length > 0 ? found.Model : null, auto, auto ? ModelChoice.Describe(catalog.First(m => m.Id == found.Model)) : null);
        }
        // The plain-http error reads better than a failed TLS handshake.
        var http0 = Math.Max(0, candidates.ToList().FindIndex(c => c.StartsWith("http://", StringComparison.OrdinalIgnoreCase)));
        return new ProbeResult(false, [], null, null, "Can't reach the server: " + attempts[http0].Result.Error);
    }

    private static async Task<(IReadOnlyList<string>? Models, string? Error)> ListModelsAsync(HttpClient http, string baseUrl, string apiKey, CancellationToken ct)
    {
        try
        {
            return (await new OpenAiChatClient(http, baseUrl, apiKey).ListModelsAsync(ct).ConfigureAwait(false), null);
        }
        // Anything from a web page where the model list should be to a timeout: not this address.
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return (null, ex is OperationCanceledException ? "it did not answer in time." : ex.Message);
        }
    }

    /// <summary>
    /// What is known about each listed model, in the server's order. LM Studio describes them all
    /// in its REST API (/api/v1, or /api/v0 in older versions): type, size, quantization, image
    /// support, whether loaded. For other servers the names tell what they can.
    /// </summary>
    internal static async Task<IReadOnlyList<ModelInfo>> CatalogAsync(HttpClient http, ServerSettings server, IReadOnlyList<string> ids, CancellationToken ct)
    {
        var root = OpenAiChatClient.NormalizeBaseUrl(server.BaseUrl)[..^"/v1".Length];
        var described = new Dictionary<string, ModelInfo>();
        if (await GetJsonAsync(http, server, $"{root}/api/v1/models", ct).ConfigureAwait(false) is { } v1 && v1["models"] is JsonArray models)
        {
            foreach (var m in models.OfType<JsonObject>())
                if (Str(m["key"]) is { } key)
                    described[key] = FromLmStudio(key, m);
        }
        else if (await GetJsonAsync(http, server, $"{root}/api/v0/models", ct).ConfigureAwait(false) is { } v0 && v0["data"] is JsonArray data)
        {
            foreach (var m in data.OfType<JsonObject>())
                if (Str(m["id"]) is { } id)
                    described[id] = FromLmStudioV0(id, m);
        }
        return ids.Select(id => described.GetValueOrDefault(id) ?? ModelChoice.FromName(id)).ToList();
    }

    /// <summary>
    /// A model in LM Studio's /api/v1/models: "type" llm or embedding, "params_string" such as 27B
    /// or 30B-A3B, "quantization" with its "bits_per_weight", "capabilities.vision", "size_bytes"
    /// and "loaded_instances". What it leaves out comes from the name.
    /// </summary>
    private static ModelInfo FromLmStudio(string id, JsonObject m)
    {
        var name = ModelChoice.FromName(id);
        var (total, active) = ModelChoice.ParseSize(Str(m["params_string"]));
        var quantization = m["quantization"] as JsonObject;
        return name with
        {
            Chat = Str(m["type"]) is { } type ? type is "llm" or "vlm" : name.Chat,
            Vision = m["capabilities"] is JsonObject caps && caps["vision"] is JsonValue v && v.TryGetValue<bool>(out var vision) ? vision : name.Vision,
            Params = total ?? name.Params,
            ActiveParams = active ?? name.ActiveParams,
            Bits = Number(quantization?["bits_per_weight"]) ?? ModelChoice.ParseBits(Str(quantization?["name"])) ?? name.Bits,
            SizeBytes = Number(m["size_bytes"]) is { } size ? (long)size : null,
            Loaded = m["loaded_instances"] is JsonArray { Count: > 0 },
        };
    }

    /// <summary>A model in LM Studio's older /api/v0/models: "type" llm, vlm or embeddings, "quantization" such as Q6_K_XL, and "state". No size; the name may tell it.</summary>
    private static ModelInfo FromLmStudioV0(string id, JsonObject m)
    {
        var name = ModelChoice.FromName(id);
        var type = Str(m["type"]);
        return name with
        {
            Chat = type is not null ? type is "llm" or "vlm" : name.Chat,
            Vision = type switch { "vlm" => true, "llm" => false, _ => name.Vision },
            Bits = ModelChoice.ParseBits(Str(m["quantization"])) ?? name.Bits,
            Loaded = Str(m["state"]) == "loaded",
        };
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
                new ChatRequestOptions { Model = server.ModelInUse, MaxTokens = 1 }, null, ct).ConfigureAwait(false);
        }
        catch (LlmException)
        {
            return null; // the real request will report the problem
        }
        return (await TryContextAsync(http, server, ct).ConfigureAwait(false)).Context;
    }

    /// <summary>Context length, load state and image support of the model in use.</summary>
    public static async Task<ModelFacts> TryContextAsync(HttpClient http, ServerSettings server, CancellationToken ct)
    {
        var model = server.ModelInUse;
        if (string.IsNullOrWhiteSpace(model))
            return default;
        var v1 = OpenAiChatClient.NormalizeBaseUrl(server.BaseUrl);
        var root = v1[..^"/v1".Length];

        // LM Studio
        if (await GetJsonAsync(http, server, $"{root}/api/v0/models/{Uri.EscapeDataString(model)}", ct).ConfigureAwait(false) is { } lms
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
            var entry = data.FirstOrDefault(m => m?["id"] is JsonValue id && id.TryGetValue<string>(out var s) && s == model);
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
            var entry = running.FirstOrDefault(m => (m?["name"] ?? m?["model"])?.ToString() == server.ModelInUse);
            if (Int(entry?["context_length"]) is { } loaded)
                return loaded;
        }
        if (await GetJsonAsync(http, server, $"{root}/api/show", ct, new JsonObject { ["model"] = server.ModelInUse }).ConfigureAwait(false) is not { } show)
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
        // Whatever fails (no such endpoint, not JSON, an address that isn't one): this server doesn't say.
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return null;
        }
    }

    private static string? Str(JsonNode? node) => node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static double? Number(JsonNode? node) => node is JsonValue v && v.TryGetValue<double>(out var d) ? d : null;

    private static int? Int(JsonNode? node)
    {
        if (node is not JsonValue v) return null;
        if (v.TryGetValue<long>(out var l)) return (int)Math.Min(l, int.MaxValue);
        if (v.TryGetValue<int>(out var i)) return i;
        if (v.TryGetValue<double>(out var d)) return (int)d;
        return null;
    }
}
