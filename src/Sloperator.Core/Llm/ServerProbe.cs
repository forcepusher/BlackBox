using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Sloperator.Core.Settings;

namespace Sloperator.Core.Llm;

/// <param name="Models">The models the server offers that can chat, in its own order (embedding models and the like left out).</param>
/// <param name="LoadedContext">Context size the server runs <paramref name="Model"/> with, if it says; also <paramref name="Vision"/> and <paramref name="Loaded"/> are about that model.</param>
/// <param name="Loaded">False when the server says the model is not loaded yet (LM Studio loads on first use), so it has no context size yet.</param>
/// <param name="BaseUrl">The complete address that answered ("host:1234" typed, "http://host:1234/v1" found).</param>
/// <param name="Model">The model to use: the configured one while the server offers it, else the strongest it offers (see <see cref="ModelChoice"/>); null when there is none.</param>
/// <param name="AutoPicked">True when <paramref name="Model"/> was chosen here, because none was configured or the server doesn't offer it.</param>
/// <param name="ModelDetails">For a model chosen here, its size, quantization and benchmark score ("27B, 6-bit, 79.8% on Terminal-Bench 2.1") as far as known.</param>
/// <param name="ContextHosted"><paramref name="LoadedContext"/> is the most a hosted service takes for the model, not a size a server runs it with (see <see cref="ModelFacts.Hosted"/>).</param>
/// <param name="KeyRefused">The server answered, but turned the API key down (see <see cref="KeyRefusedException"/>); <paramref name="Message"/> says what to change.</param>
/// <param name="Efforts">The effort levels <paramref name="Model"/> takes (see <see cref="ModelInfo.Efforts"/>); empty when it takes none or nothing says.</param>
public sealed record ProbeResult(bool Reachable, IReadOnlyList<string> Models, int? LoadedContext, bool? Vision, string Message, bool? Loaded = null, string? BaseUrl = null,
    string? Model = null, bool AutoPicked = false, string? ModelDetails = null, bool ContextHosted = false, bool KeyRefused = false, IReadOnlyList<string>? Efforts = null)
{
    /// <summary>What the server said of <see cref="Model"/>, as <see cref="ServerProbe.TryContextAsync"/> gives it; null when it wasn't reached.</summary>
    public ModelFacts? Facts => Reachable && Model is not null ? new ModelFacts(LoadedContext, Loaded, Vision, ContextHosted) : null;
}

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
/// maximum there, of which Sloperator uses <see cref="ServerSettings.HostedContextSize"/> on Auto</item>
/// </list>
/// The first that answers wins. Anything else gets <see cref="ServerSettings.FallbackContextSize"/>;
/// if that is too big, the server's "too long" error names the real size and the agent adjusts.
/// OpenRouter-style lists also say which models take images, call tools and cost nothing.
/// </remarks>
public static partial class ServerProbe
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
        // Pasted into the wrong box, a key was looked up as a host name: "No such host is known. (sk-or-v1-…:80)".
        if (ServerAddress.LooksLikeKey(server.BaseUrl))
            return new ProbeResult(false, [], null, null, server.BaseUrl.Trim().StartsWith("sk-ant-", StringComparison.Ordinal)
                ? "That looks like an Anthropic API key, not a server address: it goes in the API key box, and the Server box takes https://api.anthropic.com."
                : "That looks like an API key, not a server address: it goes in the API key box. " +
                  "The Server box takes the address of the service's API, such as https://openrouter.ai/api/v1.");
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
            // so with none picked the one Sloperator was tested with comes first there.
            // On Anthropic's API, Claude Opus 5.5 unless another is picked (see AnthropicMessages.Pick).
            found.Model = (server.Model.Length == 0 && KiloGateway.Is(candidates[i]) && catalog.Any(m => m.Id == KiloGateway.Model)
                ? KiloGateway.Model
                : OpenAiChatClient.IsAnthropic(candidates[i]) && catalog.All(m => m.Id != server.Model)
                    ? AnthropicMessages.Pick(catalog.Where(m => m.Chat).Select(m => m.Id).ToList())
                    : ModelChoice.Choose(server.Model, catalog)) ?? "";
            var auto = found.Model.Length > 0 && found.Model != server.Model;
            var facts = await TryContextAsync(http, found, ct, models).ConfigureAwait(false);
            return new ProbeResult(true, catalog.Where(m => m.Chat).Select(m => m.Id).ToList(), facts.Context, facts.Vision,
                models.Count == 0 ? "Connected, but the server lists no models." : "Connected.", facts.Loaded, candidates[i],
                found.Model.Length > 0 ? found.Model : null, auto, auto ? ModelChoice.Describe(catalog.First(m => m.Id == found.Model)) : null, facts.Hosted,
                Efforts: catalog.FirstOrDefault(m => m.Id == found.Model)?.Efforts ?? []);
        }
        // A server that answered but turned the key down is reachable all right: that is what to fix.
        if (attempts.FirstOrDefault(a => a.Result.KeyRefused) is { } refused)
            return new ProbeResult(false, [], null, null, refused.Result.Error! + AnthropicKeyHint(server, candidates[0]), KeyRefused: true);
        // The plain-http error reads better than a failed TLS handshake.
        var http0 = Math.Max(0, candidates.ToList().FindIndex(c => c.StartsWith("http://", StringComparison.OrdinalIgnoreCase)));
        return new ProbeResult(false, [], null, null, "Can't reach the server: " + attempts[http0].Result.Error);
    }

    /// <summary>An Anthropic key (sk-ant-…) given to another server: Claude's address is said.</summary>
    private static string AnthropicKeyHint(ServerSettings server, string address) =>
        server.ApiKey.Trim().StartsWith("sk-ant-", StringComparison.Ordinal) && !OpenAiChatClient.IsAnthropic(address)
            ? " That is an Anthropic API key: for Claude, the server address is https://api.anthropic.com."
            : "";

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
        // Anthropic's list says it all, and LM Studio's addresses there only answer "not found".
        var lmStudio = !OpenAiChatClient.IsAnthropic(server.BaseUrl);
        if (lmStudio && await GetJsonAsync(http, server, $"{root}/api/v1/models", ct).ConfigureAwait(false) is { } v1 && v1["models"] is JsonArray models)
        {
            foreach (var m in models.OfType<JsonObject>())
                if (Str(m["key"]) is { } key)
                    described[key] = FromLmStudio(key, m);
        }
        else if (lmStudio && await GetJsonAsync(http, server, $"{root}/api/v0/models", ct).ConfigureAwait(false) is { } v0 && v0["data"] is JsonArray data)
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
    /// (output_modalities: a music model writes audio), whether it calls tools and can be asked for
    /// an effort (supported_parameters) and whether it is free (pricing). A list of ids alone says
    /// none of it. Anthropic's list says the effort levels in its capabilities.
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
            Efforts = ClaudeEfforts(entry) ?? ListedEfforts(parameters, name.Id) ?? EffortsByName(name.Id),
        };

        static List<string>? Strings(JsonNode? node) => node is JsonArray a ? a.Select(Str).OfType<string>().ToList() : null;

        // OpenRouter routes "reasoning" to every model that thinks on request, as low to high. Not
        // Claude's or Gemini's through it: with thinking on, a tool call wants the signed thinking
        // before it back (OpenRouter's reasoning_details), which Sloperator doesn't keep, and the
        // request is refused; they ran with thinking off before there was an effort to pick.
        static IReadOnlyList<string>? ListedEfforts(List<string>? parameters, string id) =>
            parameters is null ? null
            : id.StartsWith("anthropic/", StringComparison.OrdinalIgnoreCase) || id.StartsWith("google/gemini", StringComparison.OrdinalIgnoreCase) ? []
            : parameters.Contains("reasoning") || parameters.Contains("reasoning_effort") ? OpenAiEfforts : [];

        // Prices come as text ("0", "0.0000003" per token) or as numbers.
        static decimal? Price(JsonNode? node) => node is JsonValue v
            && (v.TryGetValue<decimal>(out var n) || v.TryGetValue<string>(out var s) && decimal.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out n))
            ? n : null;
    }

    /// <summary>
    /// A model in LM Studio's /api/v1/models: "type" llm or embedding, "params_string" such as 27B
    /// or 30B-A3B, "quantization" with its "bits_per_weight", "capabilities.vision", "size_bytes"
    /// and "loaded_instances". What it leaves out comes from the name. Its effort levels are those
    /// of "capabilities.reasoning.allowed_options" (gpt-oss: low, medium, high); a model whose
    /// thinking only goes on and off there, or that has no such entry, as qwen3.8 on the test
    /// server, takes none: its thinking is LM Studio's model setting.
    /// </summary>
    private static ModelInfo FromLmStudio(string id, JsonObject m)
    {
        var name = ModelChoice.FromName(id);
        var (total, active) = ModelChoice.ParseSize(Str(m["params_string"]));
        var quantization = m["quantization"] as JsonObject;
        var caps = m["capabilities"] as JsonObject;
        return name with
        {
            Chat = Str(m["type"]) is { } type ? type is "llm" or "vlm" : name.Chat,
            Vision = caps?["vision"] is JsonValue v && v.TryGetValue<bool>(out var vision) ? vision : name.Vision,
            Params = total ?? name.Params,
            ActiveParams = active ?? name.ActiveParams,
            Bits = Number(quantization?["bits_per_weight"]) ?? ModelChoice.ParseBits(Str(quantization?["name"])) ?? name.Bits,
            SizeBytes = Number(m["size_bytes"]) is { } size ? (long)size : null,
            Loaded = m["loaded_instances"] is JsonArray { Count: > 0 },
            Efforts = caps?["reasoning"]?["allowed_options"] is JsonArray options ? Efforts.Of(options.Select(Str).OfType<string>()) : [],
        };
    }

    /// <summary>What OpenAI's reasoning models, and services routing to thinking models, take.</summary>
    private static readonly IReadOnlyList<string> OpenAiEfforts = ["low", "medium", "high"];

    /// <summary>
    /// For a model of Anthropic's list (its entry kept under "anthropic", see
    /// <see cref="AnthropicMessages.ModelEntry"/>), the levels its capabilities say ("effort":
    /// {"supported": true, "low": {"supported": true}, …}), or those of its version where they
    /// don't; null for any other list's entry.
    /// </summary>
    private static IReadOnlyList<string>? ClaudeEfforts(JsonObject entry)
    {
        if (entry["anthropic"] is not JsonObject claude)
            return null;
        if (claude["capabilities"]?["effort"] is not JsonObject effort)
            return EffortsByName(Str(claude["id"]) ?? "");
        if (effort["supported"] is JsonValue s && s.TryGetValue<bool>(out var supported) && !supported)
            return [];
        return Efforts.Of(effort.Where(p => p.Value is JsonObject level && level["supported"] is JsonValue v && v.TryGetValue<bool>(out var yes) && yes).Select(p => p.Key));
    }

    /// <summary>
    /// For a list that doesn't say: Claude by its version (4.7 on: low to max, 4.6: no xhigh, Opus
    /// 4.5: low to high; before that none, Haiku 4.5 among them) and OpenAI's reasoning models
    /// (gpt-5 and the o series: low to high). Anything else takes none.
    /// </summary>
    internal static IReadOnlyList<string> EffortsByName(string id)
    {
        var (family, version) = ClaudeModel.Parse(id);
        if (family is not null)
            return family is "haiku" ? version >= 5 ? Efforts.Levels : []
                : version >= 4.7 ? Efforts.Levels
                : version >= 4.6 ? ["low", "medium", "high", "max"]
                : family is "opus" && version >= 4.5 ? OpenAiEfforts : [];
        var bare = id[(id.LastIndexOf('/') + 1)..];
        return OpenAiReasoning().IsMatch(bare) ? OpenAiEfforts : [];
    }

    [GeneratedRegex(@"^(gpt-5|o[1-9])(\b|[-.])", RegexOptions.IgnoreCase)]
    private static partial Regex OpenAiReasoning();

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
    /// box that is 4k: less than Sloperator's instructions and tools alone (about 9k tokens), so
    /// every request failed as too long. A model loaded under <see cref="ReloadBelow"/> for
    /// Sloperator's first request is loaded again with <see cref="MinimumContext"/> (16k when the GPU
    /// can't hold that), and <paramref name="onNote"/> says so. A size the user chose above that is
    /// kept: a model set to 31k, loaded again at 64k beside another one, crashed at its first
    /// answer. A model the user loaded themselves is left as it is.
    /// </remarks>
    /// <param name="known">What a check just now said of the model (see <see cref="ProbeResult.Facts"/>), so the server isn't asked again before it loads.</param>
    public static async Task<ModelFacts> EnsureLoadedAsync(HttpClient http, ServerSettings server, Action? onLoading, CancellationToken ct, Action<string>? onNote = null,
        ModelFacts? known = null)
    {
        var facts = known ?? await TryContextAsync(http, server, ct).ConfigureAwait(false);
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
        if (facts.Context is { } small && small < ReloadBelow && await ReloadRoomierAsync(http, server, small, ct).ConfigureAwait(false) is { } roomier)
        {
            onNote?.Invoke($"LM Studio loaded {server.ModelInUse} with a {small / 1024}k context, too small for Sloperator; it runs with {roomier / 1024}k now. " +
                           "To choose the size yourself, load it in LM Studio with the context length you want.");
            facts = await TryContextAsync(http, server, ct).ConfigureAwait(false);
        }
        return facts;
    }

    /// <summary>Less than this, and Sloperator's instructions and tools (about 9k tokens) leave room for a few steps at most.</summary>
    public const int MinimumContext = 32 * 1024;

    /// <summary>A context LM Studio gave a model on first use under this is its out-of-the-box 4k or 8k, not the user's choice.</summary>
    public const int ReloadBelow = 16 * 1024;

    /// <summary>
    /// Loads the model in use again through LM Studio's REST API (/api/v1/models/unload and
    /// /load) with <see cref="MinimumContext"/>, or <see cref="ReloadBelow"/> when its memory
    /// guardrail refuses that; the context it then runs with, or null if neither loaded.
    /// </summary>
    private static async Task<int?> ReloadRoomierAsync(HttpClient http, ServerSettings server, int current, CancellationToken ct)
    {
        var root = OpenAiChatClient.NormalizeBaseUrl(server.BaseUrl)[..^"/v1".Length];
        if (await GetJsonAsync(http, server, $"{root}/api/v1/models", ct).ConfigureAwait(false) is not { } list
            || (list["models"] as JsonArray)?.OfType<JsonObject>().FirstOrDefault(m => Str(m["key"]) == server.ModelInUse) is not { } entry
            || entry["loaded_instances"] is not JsonArray { Count: > 0 } instances)
            return null;
        var most = Int(entry["max_context_length"]) ?? MinimumContext;
        var sizes = new[] { MinimumContext, ReloadBelow }.Select(s => Math.Min(s, most)).Where(s => s > current).Distinct().ToList();
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

        // Anthropic: its model list, in OpenAI style, says the context (max_input_tokens) and whether it takes pictures.
        if (OpenAiChatClient.IsAnthropic(v1))
        {
            try { listed ??= await new OpenAiChatClient(http, server.BaseUrl, server.ApiKey).ListModelEntriesAsync(ct).ConfigureAwait(false); }
            catch (Exception ex) when (ex is LlmException or HttpRequestException or IOException || ex is OperationCanceledException && !ct.IsCancellationRequested) { return default; }
        }
        // LM Studio
        else if (await GetJsonAsync(http, server, $"{root}/api/v0/models/{Uri.EscapeDataString(model)}", ct).ConfigureAwait(false) is { } lms
            && lms["state"] is JsonValue stateValue && stateValue.TryGetValue<string>(out var state))
        {
            // Only a loaded model has a real context length. max_context_length is what the model
            // could support (often 256k), not what LM Studio will actually load it with.
            // "vlm" models take images, "llm" models don't.
            bool? vision = lms["type"] is JsonValue t && t.TryGetValue<string>(out var type) && type is "vlm" or "llm" ? type == "vlm" : null;
            return new ModelFacts(state == "loaded" ? Int(lms["loaded_context_length"]) : null, state == "loaded", vision);
        }
        else
        {
            // llama.cpp llama-server; "modalities" (2025 on) says whether it runs with a vision projector.
            // Taken to see without it, a model that can't refused every request after its first screenshot.
            if (await GetJsonAsync(http, server, $"{root}/props", ct).ConfigureAwait(false) is { } props
                && (Int(props["default_generation_settings"]?["n_ctx"]) ?? Int(props["n_ctx"])) is { } nctx)
                return new ModelFacts(nctx, true, props["modalities"]?["vision"] is JsonValue projector && projector.TryGetValue<bool>(out var takesImages) ? takesImages : null);

            // Ollama: the context a model runs with is its num_ctx, not what the model could do.
            if (await OllamaFactsAsync(http, server, root, ct).ConfigureAwait(false) is { } ollama)
                return ollama;
        }

        // vLLM / OpenRouter style metadata on the model list
        listed ??= await GetJsonAsync(http, server, $"{v1}/models", ct).ConfigureAwait(false) is { } list && list["data"] is JsonArray data
            ? data.OfType<JsonObject>().ToList()
            : null;
        if (listed?.FirstOrDefault(m => Str(m["id"]) == model) is not { } entry)
            return default;
        var sees = FromListEntry(new ModelInfo(model), entry).Vision;
        // vLLM's max_model_len is what it serves the model with; context_length is the most a
        // hosted service takes for it. Claude's is used whole on Auto (the user, 22 October 2026,
        // and again 26 October after a day on 100k): each step reads what came before from its
        // cache, which doesn't count toward the input tokens a minute, where OpenRouter and Kilo
        // pass the whole request on at full price.
        if (Int(entry["max_model_len"]) is { } served)
            return new ModelFacts(served, null, sees);
        return Int(entry["context_length"]) is { } most ? new ModelFacts(most, null, sees, Hosted: !OpenAiChatClient.IsAnthropic(v1)) : new ModelFacts(null, null, sees);
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
