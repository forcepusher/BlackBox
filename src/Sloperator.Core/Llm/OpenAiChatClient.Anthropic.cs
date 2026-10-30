using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Sloperator.Core.Llm;

// Anthropic's API, spoken in its own Messages format (see AnthropicMessages): the same calls as
// for any server, so the agent, fresh looks and model questions use Claude unchanged.
public sealed partial class OpenAiChatClient
{
    private readonly bool _anthropic;

    /// <summary>
    /// Whether the address is Anthropic's API (<c>api.anthropic.com</c>), which Sloperator speaks in
    /// its Messages format, or a gateway's way to it: one whose path has an <c>anthropic</c> part,
    /// such as Cloudflare's AI Gateway (<c>…/{account}/{gateway}/anthropic</c>).
    /// </summary>
    public static bool IsAnthropic(string? baseUrl) =>
        Uri.TryCreate(NormalizeBaseUrl(baseUrl ?? ""), UriKind.Absolute, out var uri)
        && (uri.Host.Equals("api.anthropic.com", StringComparison.OrdinalIgnoreCase)
            || uri.AbsolutePath.Split('/').Contains("anthropic", StringComparer.OrdinalIgnoreCase));

    /// <summary>
    /// What is learnt of Anthropic's API, for each HttpClient (the app has one; every task and
    /// look makes a client of its own): what each Claude model can do, from the model list (or the
    /// name), by address and model id, and the parts of a request the API turned down, left out
    /// from then on, by address and model: one model's refusal of the fallbacks or the thinking
    /// binding turned them off for every model on api.anthropic.com until Sloperator closed.
    /// </summary>
    private sealed class ClaudeFacts
    {
        public readonly ConcurrentDictionary<string, ClaudeModel> Models = new(StringComparer.OrdinalIgnoreCase);
        public readonly ConcurrentDictionary<string, AnthropicMessages.Wire> Wires = new(StringComparer.OrdinalIgnoreCase);
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<HttpClient, ClaudeFacts> Learnt = new();

    private ConcurrentDictionary<string, ClaudeModel> ClaudeModels => Learnt.GetOrCreateValue(_http).Models;

    private ConcurrentDictionary<string, AnthropicMessages.Wire> Wires => Learnt.GetOrCreateValue(_http).Wires;

    /// <summary>Set once the API refused earlier thinking as written for another history: this conversation goes on without it (see <see cref="AnthropicMessages.Wire.StripThinking"/>).</summary>
    private int _stripThinking;

    private void ClaudeHeaders(HttpRequestMessage req, IReadOnlyCollection<string>? betas = null)
    {
        Authorize(req);
        req.Headers.Add("anthropic-version", AnthropicMessages.Version);
        if (betas is { Count: > 0 })
            req.Headers.Add("anthropic-beta", string.Join(",", betas));
    }

    /// <summary>Anthropic's model list (all its pages), each model as an OpenAI-style entry (see <see cref="AnthropicMessages.ModelEntry"/>).</summary>
    private async Task<IReadOnlyList<JsonObject>> ListClaudeModelsAsync(CancellationToken ct)
    {
        var entries = new List<JsonObject>();
        string? after = null;
        do
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, _baseUrl + "/models?limit=1000" + (after is null ? "" : "&after_id=" + Uri.EscapeDataString(after)));
            ClaudeHeaders(req);
            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                throw KeyRefusedException.For((int)resp.StatusCode, body, _apiKey is not null)
                      ?? new LlmException($"GET /models failed ({(int)resp.StatusCode}): {ErrorText(body).Clip(500)}");
            JsonObject? page;
            try { page = JsonNode.Parse(body) as JsonObject; }
            catch (JsonException) { throw new LlmException("The answer at /models is not a model list (is this the right address?)."); }
            foreach (var model in (page?["data"] as JsonArray)?.OfType<JsonObject>() ?? [])
            {
                if (model["id"] is not JsonValue id || !id.TryGetValue<string>(out var name))
                    continue;
                ClaudeModels[_baseUrl + "|" + name] = ClaudeModel.FromEntry(model);
                entries.Add(AnthropicMessages.ModelEntry(model));
            }
            after = page?["has_more"] is JsonValue more && more.TryGetValue<bool>(out var hasMore) && hasMore && page["last_id"] is JsonValue last && last.TryGetValue<string>(out var lastId) ? lastId : null;
        }
        while (after is not null && entries.Count < 10_000);
        return entries;
    }

    /// <summary>What the model can do: from the list if it was read, else its own entry (<c>/v1/models/{id}</c>), else its name.</summary>
    private async Task<ClaudeModel> ClaudeModelAsync(string id, CancellationToken ct)
    {
        var key = _baseUrl + "|" + id;
        if (ClaudeModels.TryGetValue(key, out var known))
            return known;
        ClaudeModel? model = null;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, _baseUrl + "/models/" + Uri.EscapeDataString(id));
            ClaudeHeaders(req);
            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            if (resp.IsSuccessStatusCode && JsonNode.Parse(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false)) is JsonObject entry)
                model = ClaudeModel.FromEntry(entry);
        }
        // The request itself then says what is wrong (no key, no such model, no connection).
        catch (Exception ex) when (ex is HttpRequestException or JsonException or IOException || ex is TaskCanceledException && !ct.IsCancellationRequested) { }
        model ??= ClaudeModel.FromName(id);
        ClaudeModels[key] = model;
        return model;
    }

    private async Task<ChatMessage> CompleteClaudeAsync(
        IReadOnlyList<ChatMessage> messages, IReadOnlyList<ToolDefinition> tools, ChatRequestOptions options,
        Action<StreamDelta>? onDelta, CancellationToken ct, Action<long, long>? onSending)
    {
        var model = await ClaudeModelAsync(options.Model, ct).ConfigureAwait(false);
        for (var attempt = 0; ; attempt++)
        {
            var wire = Wires.GetOrAdd(_baseUrl + "|" + model.Id, _ => new AnthropicMessages.Wire()) with { StripThinking = _stripThinking };
            var betas = new List<string>();
            var payload = Encoding.UTF8.GetBytes(AnthropicMessages.BuildRequest(messages, tools, options, model, wire, betas).ToJsonString());
            using var stall = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var upload = new UploadContent(payload, stall, UploadStallLimit, onSending);
            using var req = new HttpRequestMessage(HttpMethod.Post, _baseUrl + "/messages") { Content = upload };
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
            ClaudeHeaders(req, betas);

            using (var resp = await SendAsync(req, upload, stall, ct).ConfigureAwait(false))
            {
                if (!resp.IsSuccessStatusCode)
                {
                    var status = (int)resp.StatusCode;
                    var err = await ErrorBodyAsync(resp, ct).ConfigureAwait(false);
                    var said = ErrorText(err);
                    // A part of the request this account, model or API version doesn't take: left
                    // out, and the request sent again.
                    if (status == 400 && attempt < 6 && Adjust(said, wire, ref model) is { } adjusted)
                    {
                        Wires[_baseUrl + "|" + model.Id] = adjusted with { StripThinking = 0 };
                        _stripThinking = adjusted.StripThinking;
                        ClaudeModels[_baseUrl + "|" + model.Id] = model;
                        continue;
                    }
                    if (LooksLikeContextOverflow(said))
                        throw new ContextOverflowException(said.Clip(500));
                    if (status == 413)
                        throw new RequestTooLargeException(said.ReplaceLineEndings(" ").Clip(300), payload.Length);
                    if (status is 400 or 415 or 422 && HasImages(messages) && LooksLikeImagesRefused(said))
                        throw new ImagesRefusedException(said.Clip(300));
                    if (CreditWords().IsMatch(said))
                        throw new KeyRefusedException($"The Anthropic account behind the API key has run out of credits (it says \"{said.Clip(300)}\"). Add credits in Anthropic's Console, or pick another server at the top.");
                    // A spend limit reached comes as a 400 (one set for the account) or a 429 without
                    // retry-after (its tier's monthly cap): asking again fails until it is raised or
                    // the month is over, so it isn't waited out like a rate limit.
                    if (status is 400 or 429 && SpendWords().IsMatch(said + err))
                        throw new KeyRefusedException($"The Anthropic account behind the API key has reached its spend limit (it says \"{said.Clip(300)}\"). Raise it in Anthropic's Console, or pick another server at the top.");
                    if (status == 429 && InputPerMinute(resp.Headers, said) is { } perMinute)
                        throw new InputRateException(said.Clip(300), perMinute);
                    if (status == 429)
                        throw new RateLimitedException($"{_baseUrl} answered 429: {said.Clip(300)}", RetryAfter(resp.Headers, err, DateTimeOffset.UtcNow));
                    // 529: Anthropic is overloaded. Sent again after growing waits, like a model
                    // server that failed, rather than twelve times within seconds.
                    if (status is 408 or >= 500)
                        throw new ConnectionLostException($"{_baseUrl} answered {status}: {said.Clip(300)}", false) { ServerFailed = status == 529 };
                    if (KeyRefusedException.For(status, err, _apiKey is not null) is { } refused)
                        throw refused;
                    throw new LlmException($"Chat request failed ({status}): {said.Clip(1000)}");
                }

                var received = false;
                string? toolCall = null;
                try
                {
                    await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                    using var reader = new StreamReader(stream, Encoding.UTF8);
                    return await AnthropicMessages.ReadStreamAsync(reader, d => { received = true; onDelta?.Invoke(d); }, ct,
                        onToolCall: name => { received = true; toolCall = name; }).ConfigureAwait(false);
                }
                catch (Exception ex) when (!ct.IsCancellationRequested && ex is IOException or HttpRequestException or TaskCanceledException)
                {
                    throw new ConnectionLostException($"The connection to {_baseUrl} dropped: {ex.Message}", received, ex, toolCall);
                }
            }
        }
    }

    /// <summary>
    /// The request without the part a 400 says the API won't take, or null when it names none:
    /// a beta feature it doesn't know (an older API, or a proxy), streamed tool calls, thinking
    /// summaries, a reply limit over the model's, thinking where the model has none, pictures too
    /// large for a request with many, and earlier thinking refused as written for another history.
    /// </summary>
    internal static AnthropicMessages.Wire? Adjust(string said, AnthropicMessages.Wire wire, ref ClaudeModel model)
    {
        if (MaxTokensWords().Match(said) is { Success: true } most && int.TryParse(most.Groups[1].Value, out var cap) && cap < model.MaxTokens)
        {
            model = model with { MaxTokens = cap };
            return wire;
        }
        if (said.Contains("signature", StringComparison.OrdinalIgnoreCase) && said.Contains("thinking", StringComparison.OrdinalIgnoreCase) && wire.StripThinking < 2)
            return wire with { StripThinking = wire.StripThinking + 1 };
        if (wire.Betas && (said.Contains("anthropic-beta", StringComparison.OrdinalIgnoreCase) || said.Contains("block_binding", StringComparison.OrdinalIgnoreCase)
                           || said.Contains("fallback", StringComparison.OrdinalIgnoreCase)))
            return wire with { Betas = false };
        if (wire.Eager && said.Contains("eager_input_streaming", StringComparison.OrdinalIgnoreCase))
            return wire with { Eager = false };
        if (wire.Summaries && said.Contains("display", StringComparison.OrdinalIgnoreCase))
            return wire with { Summaries = false };
        if (model.Adaptive && (said.Contains("adaptive", StringComparison.OrdinalIgnoreCase) || said.Contains("thinking", StringComparison.OrdinalIgnoreCase) && said.Contains("not supported", StringComparison.OrdinalIgnoreCase)))
        {
            model = model with { Adaptive = false };
            return wire;
        }
        if (wire.MaxImageSide > AnthropicMessages.ManyImagesSide && ManyImagesWords().IsMatch(said))
            return wire with { MaxImageSide = AnthropicMessages.ManyImagesSide };
        return null;
    }

    /// <summary>
    /// The account's input tokens a minute, when a 429 for them came with that minute's budget all
    /// but unspent: the request alone is more than it, and no wait makes it fit. Null for a rate
    /// limit that waiting ends (other requests spent the budget).
    /// </summary>
    internal static int? InputPerMinute(System.Net.Http.Headers.HttpResponseHeaders headers, string said)
    {
        if (!said.Contains("input tokens per minute", StringComparison.OrdinalIgnoreCase))
            return null;
        static long? Header(System.Net.Http.Headers.HttpResponseHeaders h, string name) =>
            h.TryGetValues(name, out var values) && long.TryParse(values.FirstOrDefault(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var n) ? n : null;
        return Header(headers, "anthropic-ratelimit-input-tokens-limit") is { } limit and > 0 and <= int.MaxValue
               && Header(headers, "anthropic-ratelimit-input-tokens-remaining") is { } left && left >= limit * 9 / 10
            ? (int)limit : null;
    }

    // "max_tokens: 64000 > 32000, which is the maximum allowed number of output tokens for claude-opus-4-1"
    [GeneratedRegex(@"max_tokens: \d+ > (\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex MaxTokensWords();

    // "At least one of the image dimensions exceed max allowed size for many-image requests: 2000 pixels"
    [GeneratedRegex(@"many-image|image dimensions exceed", RegexOptions.IgnoreCase)]
    private static partial Regex ManyImagesWords();

    // "Your credit balance is too low to access the Anthropic API." comes as a 400.
    [GeneratedRegex(@"credit balance is too low", RegexOptions.IgnoreCase)]
    private static partial Regex CreditWords();

    // "You have reached your specified API usage limits…" (400); the monthly cap's 429 says
    // "You have reached your API usage limits" with error_code "enforced_spend_limit_reached".
    [GeneratedRegex(@"reached your (specified )?(workspace )?API usage limits|enforced_spend_limit_reached", RegexOptions.IgnoreCase)]
    private static partial Regex SpendWords();
}
