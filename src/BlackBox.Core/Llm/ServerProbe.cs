using System.Text;
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
/// <param name="ModelDetails">For a model chosen here, its size, quantization and benchmark score ("27B, 6-bit, 79.8% on Terminal-Bench 2.1") as far as known.</param>
/// <param name="ContextHosted"><paramref name="LoadedContext"/> is the most a hosted service takes for the model, not a size a server runs it with (see <see cref="ModelFacts.Hosted"/>).</param>
/// <param name="KeyRefused">The server answered, but turned the API key down (see <see cref="KeyRefusedException"/>); <paramref name="Message"/> says what to change.</param>
public sealed record ProbeResult(bool Reachable, IReadOnlyList<string> Models, int? LoadedContext, bool? Vision, string Message, bool? Loaded = null, string? BaseUrl = null,
    string? Model = null, bool AutoPicked = false, string? ModelDetails = null, bool ContextHosted = false, bool KeyRefused = false);

/// <summary>What the server says about the model in use; nulls where it doesn't say.</summary>
/// <param name="Hosted">
/// The context is the most a hosted service takes for the model, from an OpenRouter-style list
/// (context_length: 1M for Kilo's free Nemotron), not the size a server of one's own runs it with.
/// </param>
public readonly record struct ModelFacts(int? Context, bool? Loaded, bool? Vision, bool Hosted = false);

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
/// <item>OpenRouter, Kilo and similar: <c>GET /v1/models</c> → <c>context_length</c>, the model's
/// maximum there, of which BlackBox uses <see cref="ServerSettings.HostedContextSize"/> on Auto</item>
/// </list>
/// The first that answers wins. Anything else gets <see cref="ServerSettings.FallbackContextSize"/>;
/// if that is too big, the server's "too long" error names the real size and the agent adjusts.
/// OpenRouter-style lists also say which models take images, call tools and cost nothing.
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
            var catalog = Usable(await CatalogAsync(http, found, models, ct).ConfigureAwait(false), server.ApiKey);
            // Kilo's free models carry no benchmark score, and their sizes don't tell how they click,
            // so with none picked the one BlackBox was tested with comes first there.
            found.Model = (server.Model.Length == 0 && KiloGateway.Is(candidates[i]) && catalog.Any(m => m.Id == KiloGateway.Model)
                ? KiloGateway.Model
                : ModelChoice.Choose(server.Model, catalog)) ?? "";
            var auto = found.Model.Length > 0 && found.Model != server.Model;
            var facts = await TryContextAsync(http, found, ct, models).ConfigureAwait(false);
            return new ProbeResult(true, catalog.Where(m => m.Chat).Select(m => m.Id).ToList(), facts.Context, facts.Vision,
                models.Count == 0 ? "Connected, but the server lists no models." : "Connected.", facts.Loaded, candidates[i],
                found.Model.Length > 0 ? found.Model : null, auto, auto ? ModelChoice.Describe(catalog.First(m => m.Id == found.Model)) : null, facts.Hosted);
        }
        // A server that answered but turned the key down is reachable all right: that is what to fix.
        if (attempts.FirstOrDefault(a => a.Result.KeyRefused) is { } refused)
            return new ProbeResult(false, [], null, null, refused.Result.Error!, KeyRefused: true);
        // The plain-http error reads better than a failed TLS handshake.
        var http0 = Math.Max(0, candidates.ToList().FindIndex(c => c.StartsWith("http://", StringComparison.OrdinalIgnoreCase)));
        return new ProbeResult(false, [], null, null, "Can't reach the server: " + attempts[http0].Result.Error);
    }

    private static async Task<(IReadOnlyList<JsonObject>? Models, string? Error, bool KeyRefused)> ListModelsAsync(HttpClient http, string baseUrl, string apiKey, CancellationToken ct)
    {
        try
        {
            return (await new OpenAiChatClient(http, baseUrl, apiKey).ListModelEntriesAsync(ct).ConfigureAwait(false), null, false);
        }
        catch (KeyRefusedException ex)
        {
            return (null, ex.Message, true);
        }
        // Anything from a web page where the model list should be to a timeout: not this address.
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return (null, ex is OperationCanceledException ? "it did not answer in time." : ex.Message, false);
        }
    }

    /// <summary>
    /// The models that answer with this key. Without one, a service that sells most of its models
    /// and gives some away (Kilo: 18 free of 398 on 2026-10-16) answers only for the free ones, so
    /// the others are neither listed nor picked.
    /// </summary>
    private static IReadOnlyList<ModelInfo> Usable(IReadOnlyList<ModelInfo> catalog, string apiKey) =>
        string.IsNullOrWhiteSpace(apiKey) && catalog.Any(m => m.Free == true) ? catalog.Where(m => m.Free == true).ToList() : catalog;

    /// <inheritdoc cref="CatalogAsync(HttpClient, ServerSettings, IReadOnlyList{JsonObject}, CancellationToken)"/>
    internal static Task<IReadOnlyList<ModelInfo>> CatalogAsync(HttpClient http, ServerSettings server, IReadOnlyList<string> ids, CancellationToken ct) =>
        CatalogAsync(http, server, ids.Select(id => new JsonObject { ["id"] = id }).ToList(), ct);

    /// <summary>
    /// What is known about each listed model, in the server's order. LM Studio describes them all
    /// in its REST API (/api/v1, or /api/v0 in older versions): type, size, quantization, image
    /// support, whether loaded. OpenRouter-style lists say what each takes, gives, costs and whether
    /// it calls tools (see <see cref="FromListEntry"/>). Otherwise the names tell what they can.
    /// </summary>
    internal static async Task<IReadOnlyList<ModelInfo>> CatalogAsync(HttpClient http, ServerSettings server, IReadOnlyList<JsonObject> listed, CancellationToken ct)
    {
        var ids = listed.Select(m => Str(m["id"])!).ToList();
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
        return listed.Select((m, i) => described.GetValueOrDefault(ids[i]) ?? FromListEntry(ModelChoice.FromName(ids[i]), m)).ToList();
    }

    /// <summary>
    /// What an OpenRouter-style list (OpenRouter, Kilo) says of a model, over what its name tells:
    /// what it takes (architecture.input_modalities), whether it writes text at all
    /// (output_modalities: a music model writes audio), whether it calls tools
    /// (supported_parameters) and whether it is free (pricing). A list of ids alone says none of it.
    /// </summary>
    internal static ModelInfo FromListEntry(ModelInfo name, JsonObject entry)
    {
        var architecture = entry["architecture"] as JsonObject;
        var inputs = Strings(architecture?["input_modalities"]);
        var outputs = Strings(architecture?["output_modalities"]);
        var parameters = Strings(entry["supported_parameters"]);
        var pricing = entry["pricing"] as JsonObject;
        return name with
        {
            Chat = name.Chat && (outputs is null || outputs.Contains("text")),
            Vision = inputs is null ? name.Vision : inputs.Contains("image"),
            Tools = parameters is null ? name.Tools : parameters.Contains("tools"),
            Free = pricing is null ? name.Free : Price(pricing["prompt"]) is { } input && Price(pricing["completion"]) is { } output ? input == 0 && output == 0 : null,
        };

        static List<string>? Strings(JsonNode? node) => node is JsonArray a ? a.Select(Str).OfType<string>().ToList() : null;

        // Prices come as text ("0", "0.0000003" per token) or as numbers.
        static decimal? Price(JsonNode? node) => node is JsonValue v
            && (v.TryGetValue<decimal>(out var n) || v.TryGetValue<string>(out var s) && decimal.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out n))
            ? n : null;
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
    /// Makes sure the model is loaded (LM Studio loads models on first use) and returns what the
    /// server then says of it: the context length it runs with, if it reports one.
    /// </summary>
    /// <remarks>
    /// LM Studio loads a model on first use with the context its settings give it, and out of the
    /// box that is 4k: less than BlackBox's instructions and tools alone (about 9k tokens), so
    /// every request failed as too long. A model loaded that small for BlackBox's first request is
    /// loaded again with <see cref="RoomyContext"/> (32k when the GPU can't hold that), and
    /// <paramref name="onNote"/> says so. A model the user loaded themselves is left as it is.
    /// </remarks>
    public static async Task<ModelFacts> EnsureLoadedAsync(HttpClient http, ServerSettings server, Action? onLoading, CancellationToken ct, Action<string>? onNote = null)
    {
        var facts = await TryContextAsync(http, server, ct).ConfigureAwait(false);
        if (facts.Loaded != false)
            return facts;

        onLoading?.Invoke();
        try
        {
            var client = new OpenAiChatClient(http, server.BaseUrl, server.ApiKey);
            await client.CompleteAsync([ChatMessage.User("Reply with OK.")], [],
                new ChatRequestOptions { Model = server.ModelInUse, MaxTokens = 1 }, null, ct).ConfigureAwait(false);
        }
        catch (LlmException)
        {
            return default; // the real request will report the problem
        }
        facts = await TryContextAsync(http, server, ct).ConfigureAwait(false);
        if (facts.Context is { } small && small < MinimumContext && await ReloadRoomierAsync(http, server, small, ct).ConfigureAwait(false) is { } roomier)
        {
            onNote?.Invoke($"LM Studio loaded {server.ModelInUse} with a {small / 1024}k context, too small for BlackBox; it runs with {roomier / 1024}k now. " +
                           "To choose the size yourself, load it in LM Studio with the context length you want.");
            facts = await TryContextAsync(http, server, ct).ConfigureAwait(false);
        }
        return facts;
    }

    /// <summary>Less than this, and BlackBox's instructions and tools (about 9k tokens) leave room for a few steps at most.</summary>
    public const int MinimumContext = 32 * 1024;

    /// <summary>What a model LM Studio loaded too small for BlackBox is loaded again with, if the model takes it.</summary>
    public const int RoomyContext = 64 * 1024;

    /// <summary>
    /// Loads the model in use again through LM Studio's REST API (/api/v1/models/unload and
    /// /load) with <see cref="RoomyContext"/>, or <see cref="MinimumContext"/> when its memory
    /// guardrail refuses that; the context it then runs with, or null if neither loaded.
    /// </summary>
    private static async Task<int?> ReloadRoomierAsync(HttpClient http, ServerSettings server, int current, CancellationToken ct)
    {
        var root = OpenAiChatClient.NormalizeBaseUrl(server.BaseUrl)[..^"/v1".Length];
        if (await GetJsonAsync(http, server, $"{root}/api/v1/models", ct).ConfigureAwait(false) is not { } list
            || (list["models"] as JsonArray)?.OfType<JsonObject>().FirstOrDefault(m => Str(m["key"]) == server.ModelInUse) is not { } entry
            || entry["loaded_instances"] is not JsonArray { Count: > 0 } instances)
            return null;
        var most = Int(entry["max_context_length"]) ?? RoomyContext;
        var sizes = new[] { RoomyContext, MinimumContext }.Select(s => Math.Min(s, most)).Where(s => s > current).Distinct().ToList();
        if (sizes.Count == 0)
            return null;
        foreach (var instance in instances.OfType<JsonObject>().Select(i => Str(i["id"])).OfType<string>())
            await GetJsonAsync(http, server, $"{root}/api/v1/models/unload", ct, new JsonObject { ["instance_id"] = instance }).ConfigureAwait(false);
        foreach (var size in sizes)
            if (await GetJsonAsync(http, server, $"{root}/api/v1/models/load", ct,
                    new JsonObject { ["model"] = server.ModelInUse, ["context_length"] = size, ["echo_load_config"] = true }).ConfigureAwait(false) is { } loaded
                && Str(loaded["status"]) == "loaded")
                return Int(loaded["load_config"]?["context_length"]) ?? size;
        return null; // unloaded: the next request loads it as LM Studio's settings say, as before
    }

    /// <summary>Context length, load state and image support of the model in use.</summary>
    /// <param name="listed">The server's model list, when the caller has it already (Kilo's is half a megabyte).</param>
    public static async Task<ModelFacts> TryContextAsync(HttpClient http, ServerSettings server, CancellationToken ct, IReadOnlyList<JsonObject>? listed = null)
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

        // llama.cpp llama-server; "modalities" (2025 on) says whether it runs with a vision projector.
        // Taken to see without it, a model that can't refused every request after its first screenshot.
        if (await GetJsonAsync(http, server, $"{root}/props", ct).ConfigureAwait(false) is { } props
            && (Int(props["default_generation_settings"]?["n_ctx"]) ?? Int(props["n_ctx"])) is { } nctx)
            return new ModelFacts(nctx, true, props["modalities"]?["vision"] is JsonValue projector && projector.TryGetValue<bool>(out var takesImages) ? takesImages : null);

        // Ollama: the context a model runs with is its num_ctx, not what the model could do.
        if (await OllamaFactsAsync(http, server, root, ct).ConfigureAwait(false) is { } ollama)
            return ollama;

        // vLLM / OpenRouter style metadata on the model list
        listed ??= await GetJsonAsync(http, server, $"{v1}/models", ct).ConfigureAwait(false) is { } list && list["data"] is JsonArray data
            ? data.OfType<JsonObject>().ToList()
            : null;
        if (listed?.FirstOrDefault(m => Str(m["id"]) == model) is not { } entry)
            return default;
        var sees = FromListEntry(new ModelInfo(model), entry).Vision;
        // vLLM's max_model_len is what it serves the model with; context_length is the most a
        // hosted service takes for it.
        if (Int(entry["max_model_len"]) is { } served)
            return new ModelFacts(served, null, sees);
        return Int(entry["context_length"]) is { } most ? new ModelFacts(most, null, sees, Hosted: true) : new ModelFacts(null, null, sees);
    }

    /// <summary>The context an Ollama model runs with, and whether it takes images ("vision" among its capabilities).</summary>
    private static async Task<ModelFacts?> OllamaFactsAsync(HttpClient http, ServerSettings server, string root, CancellationToken ct)
    {
        if (await GetJsonAsync(http, server, $"{root}/api/version", ct).ConfigureAwait(false) is not { } version || version["version"] is null)
            return null;
        int? context = null;
        if (await GetJsonAsync(http, server, $"{root}/api/ps", ct).ConfigureAwait(false) is { } ps && ps["models"] is JsonArray running)
        {
            var entry = running.FirstOrDefault(m => (m?["name"] ?? m?["model"])?.ToString() == server.ModelInUse);
            context = Int(entry?["context_length"]);
        }
        var show = await GetJsonAsync(http, server, $"{root}/api/show", ct, new JsonObject { ["model"] = server.ModelInUse }).ConfigureAwait(false);
        bool? vision = show?["capabilities"] is JsonArray capabilities ? capabilities.Any(c => c?.ToString() == "vision") : null;
        if (context is null && show?["parameters"]?.ToString() is { } parameters
            && Regex.Match(parameters, @"(?m)^\s*num_ctx\s+(\d+)") is { Success: true } numCtx)
            context = int.Parse(numCtx.Groups[1].Value);
        if (context is null && show?["model_info"] is JsonObject info)
            context = info.Where(p => p.Key.EndsWith(".context_length", StringComparison.Ordinal)).Select(p => Int(p.Value)).FirstOrDefault(v => v is not null);
        return context is null && vision is null ? null : new ModelFacts(context, null, vision);
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
