using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Sloperator.Core.Llm;

public class LlmException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>The server said the prompt does not fit in the model's context window.</summary>
public class ContextOverflowException : LlmException
{
    public ContextOverflowException(string serverMessage)
        : this("The conversation no longer fits in the model's context window: " + serverMessage, serverMessage) { }

    private protected ContextOverflowException(string message, string serverMessage) : base(message) =>
        ContextLimit = ParseLimit(serverMessage);

    /// <summary>The context size the error names, if it names one (OpenAI, vLLM, llama.cpp and OpenRouter do).</summary>
    public int? ContextLimit { get; }

    internal static int? ParseLimit(string message)
    {
        foreach (var pattern in new[]
        {
            @"maximum context length is (\d+)",                 // OpenAI, vLLM, OpenRouter
            @"""n_ctx""\s*:\s*(\d+)",                            // llama.cpp server error details
            @"context (?:length|size|window) (?:is|of) (\d+)",   // other wordings
            @"\((\d+) tokens\) exceeds",                        // "... (4096 tokens) exceeds"
            @"tokens > (\d+) maximum",                          // Anthropic: "prompt is too long: 215430 tokens > 200000 maximum"
            @"exceed context limit: \d+ \+ \d+ > (\d+)",          // Anthropic, older: "input length and max_tokens exceed context limit: 188240 + 21333 > 200000"
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
/// Anthropic's limit on input tokens a minute is less than one request of the whole context: the
/// prompt cache expired (an idle while, a long tool call, a reopened chat), and a context near a
/// million is more than Fable's 500k a minute. Waiting doesn't help; a smaller context does.
/// </summary>
public sealed class InputRateException(string serverMessage, int perMinute)
    : ContextOverflowException($"The account takes {perMinute:N0} input tokens a minute, and one request of this conversation is more than that: {serverMessage}", serverMessage)
{
    public int PerMinute { get; } = perMinute;
}

/// <summary>
/// The server refused the request for its size in bytes (HTTP 413), which is the screenshots it
/// carries rather than its tokens: Kilo's gateway takes 20 MB and answers a 22 MB request with
/// "FUNCTION_PAYLOAD_TOO_LARGE". A smaller context keeps fewer screenshots in each request.
/// </summary>
public sealed class RequestTooLargeException(string serverMessage, long bytes)
    : ContextOverflowException($"The request ({bytes / 1e6:0.#} MB) is more than the server takes: {serverMessage}", serverMessage)
{
    public long Bytes { get; } = bytes;
}

/// <summary>
/// The server refused a request with pictures in it because its model takes none: one whose list
/// doesn't say so (llama-server without its projector, Ollama, most other servers) is taken to
/// see, and every request after the first screenshot failed, the picture being in the conversation.
/// </summary>
public sealed class ImagesRefusedException(string serverMessage) : LlmException("The model in use doesn't take pictures: " + serverMessage);

/// <summary>
/// The server takes pictures, but fewer in one request than this one carried: munito's gateway
/// takes 8 (measured 28 October 2026, whatever their size, across all the messages) and answers a
/// 9th with 400 "too many image inputs". A run keeps a screenshot of every step, so the step after
/// the 8th failed, and every one after it.
/// </summary>
public sealed class TooManyImagesException(string serverMessage, int sent)
    : LlmException($"The server takes fewer pictures in one request than the {sent} sent: {serverMessage}")
{
    /// <summary>The pictures the refused request carried (0 when said in the stream, until the request is looked at).</summary>
    public int Sent { get; } = sent;

    public string ServerMessage { get; } = serverMessage;

    /// <summary>The most the error says it takes, if it says.</summary>
    public int? Limit { get; } = ParseLimit(serverMessage);

    internal static int? ParseLimit(string message)
    {
        var m = Regex.Match(message, @"too many images[^:]*:[^>]*> (\d+)|(?:at most|maximum(?: number)? of|max(?:imum)?(?: is|:)?|up to|limit(?: is|:| of)?) (\d+) (?:images?|pictures?)|(\d+) (?:images?|pictures?) (?:at most|maximum|per (?:request|prompt|message))", RegexOptions.IgnoreCase);
        return m.Success && int.TryParse(m.Groups.Values.Skip(1).First(g => g.Success).Value, out var n) ? n : null;
    }
}

/// <summary>
/// The server is holding requests back (HTTP 429): too many in a while, or a free allowance used up
/// (Kilo's free models take 200 requests an hour). Sending again after a wait gets through.
/// </summary>
public sealed class RateLimitedException(string message, TimeSpan? retryAfter) : LlmException(message)
{
    /// <summary>How long the server says to wait, if it says.</summary>
    public TimeSpan? RetryAfter { get; } = retryAfter;
}

/// <summary>
/// The server turned the request down for its API key (HTTP 401, 402, 403): none given, a wrong
/// one, or an account without credits. The message says so and what to change at the top, in
/// place of the server's JSON: a wrong OpenRouter key got only "Chat request failed (401):
/// {"error":{"message":"User not found.","code":401}}", and OpenAI's "Can't reach the server",
/// though both servers had answered.
/// </summary>
public sealed class KeyRefusedException(string message) : LlmException(message)
{
    internal static KeyRefusedException? For(int status, string body, bool keySent)
    {
        var said = OpenAiChatClient.ErrorText(body).ReplaceLineEndings(" ").Clip(300);
        said = said.Length > 0 ? $" (it says \"{said}\")" : "";
        return status switch
        {
            401 when keySent => new($"The server turned down the API key{said}. Check the key next to the server address at the top."),
            401 => new($"The server wants an API key{said}. Enter yours next to the server address at the top."),
            402 => new($"The account behind the API key has run out of credits{said}. Add credits there, or pick a free model at the top."),
            403 when keySent => new($"The server refused access{said}: the API key may be wrong, or not allowed to use this model."),
            403 => new($"The server refused access{said}: it may want an API key, next to the server address at the top."),
            _ => null,
        };
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

    /// <summary>
    /// Nothing of the request got out: the server couldn't be reached at all (its PC asleep or
    /// restarting, LM Studio closed, the VPN down), rather than a link that broke mid-request.
    /// </summary>
    public bool Unreachable { get; init; }

    /// <summary>
    /// A gateway (Kilo, OpenRouter) said in its stream that the model server behind it failed or
    /// stalled ("Upstream idle timeout exceeded", "Upstream error from Nvidia: …").
    /// </summary>
    public bool Upstream { get; init; }

    /// <summary>
    /// The model server failed on the request rather than the link (see
    /// <see cref="OpenAiChatClient.ServerFailedWords"/>): sent again after a growing wait, since
    /// the requests that filled its memory, or its reloading model, take a while.
    /// </summary>
    public bool ServerFailed { get; init; }
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
        _anthropic = IsAnthropic(_baseUrl);
    }

    public string BaseUrl => _baseUrl;

    // Set once the server rejected our non-standard fields, so later requests stay plain.
    private bool _reasoningRejected;
    private bool _extrasRejected;

    /// <summary>
    /// How long sending a request may go without any of it getting through before the connection
    /// counts as dropped. A VPN can stall an upload without breaking it: LM Studio then waited
    /// five minutes for the rest of a request with screenshots and answered 408.
    /// </summary>
    internal TimeSpan UploadStallLimit { get; init; } = TimeSpan.FromSeconds(30);

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

    public async Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct) =>
        (await ListModelEntriesAsync(ct).ConfigureAwait(false)).Select(m => GetString(m, "id")!).ToList();

    /// <summary>
    /// The model list as the server gives it, one object per model with its "id". OpenRouter-style
    /// services (OpenRouter, Kilo) also say there what each model takes, costs and can do.
    /// </summary>
    public async Task<IReadOnlyList<JsonObject>> ListModelEntriesAsync(CancellationToken ct)
    {
        if (_anthropic)
            return await ListClaudeModelsAsync(ct).ConfigureAwait(false);
        using var req = new HttpRequestMessage(HttpMethod.Get, _baseUrl + "/models");
        Authorize(req);
        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            throw KeyRefusedException.For((int)resp.StatusCode, body, _apiKey is not null)
                  // A website's 404 page: its HTML filled the banner.
                  ?? new LlmException(body.TrimStart().StartsWith('<')
                      ? $"there is a website at this address ({(int)resp.StatusCode} for /models), not a model server's API. The service's documentation names its OpenAI-compatible API address, often ending in /v1."
                      : $"GET /models failed ({(int)resp.StatusCode}): {body.Clip(500)}");

        JsonNode? list;
        try { list = JsonNode.Parse(body); }
        catch (JsonException) { throw new LlmException("The answer at /models is not a model list (is this the right address?)."); }
        return list?["data"] is JsonArray data
            ? data.OfType<JsonObject>().Where(m => GetString(m, "id") is not null).ToList()
            : [];
    }

    /// <summary>
    /// Sends the conversation and streams the reply. Returns the fully assembled assistant message,
    /// including any tool calls. <paramref name="onSending"/> hears how much of the request has gone out.
    /// </summary>
    public async Task<ChatMessage> CompleteAsync(
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<ToolDefinition> tools,
        ChatRequestOptions options,
        Action<StreamDelta>? onDelta,
        CancellationToken ct,
        Action<long, long>? onSending = null)
    {
        if (_anthropic)
            return await CompleteClaudeAsync(messages, tools, options, onDelta, ct, onSending).ConfigureAwait(false);
        var sendReasoning = options.SendReasoning && !_reasoningRejected;
        var sendExtras = !_extrasRejected;
        var payload = Encoding.UTF8.GetBytes(BuildRequest(messages, tools, options, sendReasoning, sendExtras, PromptShifts.GetValueOrDefault(_baseUrl),
            effortAsObject: OpenRouterStyle(_baseUrl)).ToJsonString());
        // Cancelled when sending stalls. Once the request is out, only Stop ends the wait: a
        // server reading a long prompt sends nothing for minutes.
        using var stall = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var upload = new UploadContent(payload, stall, UploadStallLimit, onSending);
        using var req = new HttpRequestMessage(HttpMethod.Post, _baseUrl + "/chat/completions") { Content = upload };
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        Authorize(req);

        HttpResponseMessage resp;
        try
        {
            resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, stall.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested && stall.IsCancellationRequested && ex is HttpRequestException or OperationCanceledException)
        {
            throw new ConnectionLostException($"Sending the request to {_baseUrl} stalled: nothing got through for {UploadStallLimit.TotalSeconds:0} s.", false, ex);
        }
        catch (HttpRequestException ex)
        {
            throw new ConnectionLostException($"Could not reach {_baseUrl}: {ex.Message}", false, ex) { Unreachable = !upload.Started };
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
                string err;
                try { err = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false); }
                // The link broke while the error came in ("Error while copying content to a
                // stream."): what it said is lost, so it is a drop like any other.
                catch (Exception ex) when (!ct.IsCancellationRequested && ex is IOException or HttpRequestException or TaskCanceledException)
                {
                    throw new ConnectionLostException($"The connection to {_baseUrl} dropped while it answered {status}: {ex.Message}", false, ex);
                }
                if (status is 400 or 413 or 422 && HasImages(messages) && LooksLikeTooManyImages(err))
                    throw new TooManyImagesException(ErrorText(err).Clip(300), ImageCount(messages));
                if (LooksLikeContextOverflow(err))
                    throw new ContextOverflowException(err.Clip(500));
                if (status == 413)
                    throw new RequestTooLargeException(err.Trim().ReplaceLineEndings(" ").Clip(300), payload.Length);
                // Before the 5xx below are sent again: llama-server says it with a 500.
                if ((status is 400 or 404 or 415 or 422 || status >= 500) && HasImages(messages) && LooksLikeImagesRefused(err))
                    throw new ImagesRefusedException(ErrorText(err).Clip(300));
                // Strict providers reject fields they do not know. Drop ours and try once more.
                // Claude's thinking sent back through a gateway and refused (its signature no longer
                // matches): the run goes on without the earlier thinking.
                if (status == 400 && sendReasoning && messages.Any(m => m.ReasoningDetails is { Count: > 0 })
                    && err.Contains("signature", StringComparison.OrdinalIgnoreCase))
                {
                    _reasoningRejected = true;
                    return await CompleteAsync(messages, tools, options, onDelta, ct, onSending).ConfigureAwait(false);
                }
                if (status is 400 or 422 && (sendReasoning || sendExtras) && LooksLikeUnknownField(err))
                {
                    _reasoningRejected |= sendReasoning;
                    _extrasRejected |= sendExtras;
                    return await CompleteAsync(messages, tools, options, onDelta, ct, onSending).ConfigureAwait(false);
                }
                if (status == 429)
                    throw new RateLimitedException($"{_baseUrl} answered 429: {ErrorText(err).Clip(300)}", RetryAfter(resp.Headers, err, DateTimeOffset.UtcNow));
                // A proxy whose link hiccuped, a server that is restarting or busy, or one that gave
                // up waiting for the rest of the request (408): worth sending again, like a
                // dropped connection, rather than ending a long run.
                if (status is 408 or >= 500)
                    throw new ConnectionLostException($"{_baseUrl} answered {status}: {err.Clip(300)}", false);
                if (KeyRefusedException.For(status, err, _apiKey is not null) is { } refused)
                    throw refused;
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
            catch (TooManyImagesException ex) when (ex.Sent == 0)
            {
                throw new TooManyImagesException(ex.ServerMessage, ImageCount(messages));
            }
            // A gateway passes the model server's refusal on in the stream.
            catch (LlmException ex) when (ex is not (RateLimitedException or ContextOverflowException or ImagesRefusedException or TooManyImagesException)
                                          && HasImages(messages) && LooksLikeImagesRefused(ex.Message))
            {
                throw new ImagesRefusedException(ex.Message.Replace("Server error: ", "").Clip(300));
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
        var details = new SortedDictionary<int, JsonObject>();
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
                var errorText = (error as JsonObject)?["message"]?.ToString()
                                ?? (error is JsonValue said && said.TryGetValue<string>(out var words) ? words : error.ToJsonString());
                var code = (error as JsonObject)?["code"]?.ToString();
                // OpenRouter and Kilo pass on an upstream 429 in the stream, under HTTP 200.
                if (code == "429")
                    throw new RateLimitedException("Server error: " + errorText.Clip(300), RetryAfter(null, error.ToJsonString(), DateTimeOffset.UtcNow));
                if (LooksLikeTooManyImages(errorText))
                    throw new TooManyImagesException(errorText.Clip(300), 0);
                if (LooksLikeContextOverflow(errorText))
                    throw new ContextOverflowException(errorText);
                // The model server behind a gateway failed or stalled mid-way: sent again like a
                // server error answered with a status. It ended the run, every time for over half
                // an hour while Kilo's free Nemotron was stuck on Sloperator's prompt (see ShiftPrompt).
                if (Transient(code, errorText) || ServerFailedWords().IsMatch(errorText))
                    throw new ConnectionLostException("The server broke off its answer: " + errorText.Clip(300),
                        content.Length > 0 || reasoning.Length > 0 || calls.Count > 0, toolCallInProgress: calls.Values.LastOrDefault().Name is { Length: > 0 } inProgress ? inProgress : null)
                    {
                        Upstream = errorText.Contains("upstream", StringComparison.OrdinalIgnoreCase),
                        ServerFailed = ServerFailedWords().IsMatch(errorText),
                    };
                throw new LlmException("Server error: " + errorText);
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

            // Pieces of one entry share its index: its text comes a few words at a time, its
            // signature last.
            if (delta["reasoning_details"] is JsonArray pieces)
            {
                foreach (var piece in pieces.OfType<JsonObject>())
                {
                    var index = GetInt(piece, "index") ?? details.Count;
                    if (!details.TryGetValue(index, out var entry))
                        details[index] = entry = new JsonObject();
                    foreach (var (key, value) in piece)
                    {
                        if (key is "text" or "summary" or "data" && entry[key] is JsonValue before && before.TryGetValue<string>(out var start) && value is JsonValue more && more.TryGetValue<string>(out var rest))
                            entry[key] = start + rest;
                        else if (value is not null)
                            entry[key] = value.DeepClone();
                    }
                }
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
        // Not Claude's thinking cut off before its signature came (a reply that ran out of room
        // mid-thought): sent back, it would be refused.
        var signed = details.Values.Where(d => GetString(d, "format")?.StartsWith("anthropic", StringComparison.Ordinal) != true || GetString(d, "signature") is { Length: > 0 } || GetString(d, "type") == "reasoning.encrypted").ToList();
        if (signed.Count > 0)
            message.ReasoningDetails = new JsonArray(signed.ToArray<JsonNode?>());
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

    /// <summary>
    /// OpenRouter and Kilo's gateway, which take the effort as <c>reasoning: {effort}</c> for every
    /// model they route to; other servers take OpenAI's <c>reasoning_effort</c>.
    /// </summary>
    internal static bool OpenRouterStyle(string baseUrl) =>
        KiloGateway.Is(baseUrl) || Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) && uri.Host.EndsWith("openrouter.ai", StringComparison.OrdinalIgnoreCase);

    /// <param name="promptShift">Blank lines in front of the system prompt (see <see cref="ShiftPrompt"/>).</param>
    /// <param name="effortAsObject"><see cref="ChatRequestOptions.Effort"/> as OpenRouter's <c>reasoning: {effort}</c> (see <see cref="OpenRouterStyle"/>).</param>
    internal static JsonObject BuildRequest(IReadOnlyList<ChatMessage> messages, IReadOnlyList<ToolDefinition> tools, ChatRequestOptions options,
        bool sendReasoning = false, bool sendExtras = true, int promptShift = 0, bool effortAsObject = false)
    {
        var msgs = new JsonArray();
        foreach (var m in messages)
        {
            var shifted = promptShift > 0 && msgs.Count == 0 && m.Role == ChatRole.System && m.Parts is null
                ? new ChatMessage { Role = m.Role, Content = new string('\n', promptShift) + m.Content }
                : m;
            msgs.Add((JsonNode)SerializeMessage(shifted, sendReasoning));
        }

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
                arr.Add((JsonNode)new JsonObject
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
            // Only for a model its server says takes it (see ServerProbe), so a strict server that
            // turns it down anyway gets it dropped with the other extras.
            if (options.Effort is { } effort)
            {
                if (effortAsObject)
                    req["reasoning"] = new JsonObject { ["effort"] = effort };
                else
                    req["reasoning_effort"] = effort;
            }
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
                    parts.Add((JsonNode)new JsonObject
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
                    parts.Add((JsonNode)new JsonObject { ["type"] = "text", ["text"] = part.Text ?? "" });
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
                calls.Add((JsonNode)new JsonObject
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
        if (sendReasoning && m.Role == ChatRole.Assistant && m.ReasoningDetails is { Count: > 0 } details)
            obj["reasoning_details"] = details.DeepClone();

        return obj;
    }

    /// <summary>The message of a JSON error ({"error":{"message":…}} or {"error":"busy"}), else the answer as it is.</summary>
    internal static string ErrorText(string err)
    {
        try
        {
            if (JsonNode.Parse(err) is JsonObject answer && answer["error"] is JsonNode error)
                return ((error as JsonObject)?["message"] ?? error).ToString();
        }
        catch (JsonException) { }
        return err.Trim();
    }

    /// <summary>
    /// How long a 429 says to wait: Retry-After (seconds or a date); X-RateLimit-Reset, OpenRouter's
    /// time it lifts, which Kilo passes on in the error's metadata; or Gemini's "retry in 37s". Null
    /// when it doesn't say.
    /// </summary>
    internal static TimeSpan? RetryAfter(HttpResponseHeaders? headers, string body, DateTimeOffset now)
    {
        if (headers?.RetryAfter?.Delta is { } delta)
            return delta;
        if (headers?.RetryAfter?.Date is { } date)
            return date - now;
        var reset = headers is not null && headers.TryGetValues("X-RateLimit-Reset", out var values) ? values.FirstOrDefault() : null;
        reset ??= ResetInBody().Match(body) is { Success: true } r ? r.Groups[1].Value : null;
        if (double.TryParse(reset, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var at))
            return at switch
            {
                > 1e12 => DateTimeOffset.FromUnixTimeMilliseconds((long)at) - now, // milliseconds since 1970 (OpenRouter)
                > 1e9 => DateTimeOffset.FromUnixTimeSeconds((long)at) - now,       // seconds since 1970
                _ => TimeSpan.FromSeconds(at),                                     // seconds from now
            };
        if (RetryInBody().Match(body) is { Success: true } m
            && double.TryParse(m.Groups[1].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var seconds))
            return TimeSpan.FromSeconds(seconds);
        return null;
    }

    [GeneratedRegex(@"X-RateLimit-Reset""?\s*:\s*""?(\d{9,13})", RegexOptions.IgnoreCase)]
    private static partial Regex ResetInBody();

    [GeneratedRegex(@"(?:""retryDelay""\s*:\s*""|retry (?:in|after) )(\d+(?:\.\d+)?)\s*s", RegexOptions.IgnoreCase)]
    private static partial Regex RetryInBody();

    /// <summary>
    /// An error in the stream that is the server's own and may not happen again: a 408 or 5xx code,
    /// or, without one, words that say so ("Upstream idle timeout exceeded", "overloaded").
    /// </summary>
    internal static bool Transient(string? code, string text) =>
        code == "408" || int.TryParse(code, out var status) && status >= 500 || TransientWords().IsMatch(text);

    [GeneratedRegex(@"time(d)?[ -]?out|overloaded|unavailable|internal server error|upstream|try again|temporar", RegexOptions.IgnoreCase)]
    private static partial Regex TransientWords();

    /// <summary>
    /// The model server itself failed on the request: llama.cpp out of room in its KV cache while
    /// other requests ran ("failed to process mtmd chunk", "failed to find a memory slot"), or
    /// LM Studio's model crashed or was unloaded, which the next request loads again. An hour of
    /// 2048 ended on "Server error: failed to process mtmd chunk" from LM Studio.
    /// </summary>
    // After "The model has crashed without additional information", LM Studio's next answer was
    // "Model reloaded.": the model was back, and the run had ended on it.
    [GeneratedRegex(@"failed to process|failed to decode|memory slot|crashed|unloaded|reloaded", RegexOptions.IgnoreCase)]
    internal static partial Regex ServerFailedWords();

    /// <summary>Of <see cref="ServerFailedWords"/>, those saying llama.cpp had no room in its KV cache for the request.</summary>
    [GeneratedRegex(@"failed to process|failed to decode|memory slot", RegexOptions.IgnoreCase)]
    internal static partial Regex OutOfRoomWords();

    /// <summary>
    /// Blank lines put in front of the system prompt sent to a server (see <see cref="ShiftPrompt"/>);
    /// for as long as Sloperator runs, so the requests after it begin the same way again.
    /// </summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> PromptShifts = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Moves the system prompt down a line for this server from now on. Kilo's free Nemotron once
    /// failed every request that began with Sloperator's prompt for over half an hour (an idle
    /// timeout after two minutes, later an internal error), while the same request with a blank
    /// line in front got an answer in seconds: the model server had got stuck on how it began.
    /// </summary>
    public void ShiftPrompt() => PromptShifts.AddOrUpdate(_baseUrl, 1, (_, n) => n % 8 + 1);

    private static bool LooksLikeUnknownField(string err) =>
        new[] { "unrecognized", "unknown", "not permitted", "extra", "unexpected", "reasoning", "chat_template_kwargs" }
            .Any(w => err.Contains(w, StringComparison.OrdinalIgnoreCase));

    internal static bool LooksLikeContextOverflow(string err) =>
        new[] { "context length", "context_length", "context window", "n_ctx", "maximum context", "too many tokens", "exceeds the context", "context size", "context overflow",
                "prompt is too long", "exceed context limit" }
            .Any(w => err.Contains(w, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// An error saying the model takes no pictures: "image input is not supported" (llama-server
    /// without its projector), "No endpoints found that support image input" (OpenRouter), "image_url
    /// is only supported by certain models" (OpenAI), "is not a multimodal model" (vLLM), "Model does
    /// not support images" (LM Studio), "unknown variant `image_url`" (DeepSeek), "'image' is not
    /// supported" (munito).
    /// </summary>
    internal static bool LooksLikeImagesRefused(string err)
    {
        // Close together, and the picture word not part of a model's name: "llama3.2-vision:latest
        // does not support tools" is about tools.
        foreach (var word in ImageWord().EnumerateMatches(err))
        {
            var from = Math.Max(0, word.Index - 80);
            var to = Math.Min(err.Length, word.Index + word.Length + 80);
            if (RefusalWord().IsMatch(err.AsSpan(from, to - from)))
                return true;
        }
        return false;
    }

    private static bool HasImages(IReadOnlyList<ChatMessage> messages) => messages.Any(m => m.Parts?.Any(p => p.IsImage) == true);

    internal static int ImageCount(IReadOnlyList<ChatMessage> messages) => messages.Sum(m => m.Parts?.Count(p => p.IsImage) ?? 0);

    /// <summary>
    /// An error saying the request carried more pictures than the server takes. The wording isn't
    /// the same anywhere: "too many image inputs" (munito), "At most 1 image(s) may be provided in
    /// one request." (vLLM's --limit-mm-per-prompt, and Azure), "too many images and documents:
    /// 104 + 1 > 100" (Anthropic); gateways (OpenRouter, Kilo) pass the model server's on. OpenAI,
    /// llama-server and LM Studio name no such cap.
    /// </summary>
    internal static bool LooksLikeTooManyImages(string err) => TooManyImagesWords().IsMatch(err);

    [GeneratedRegex(@"too many (image|picture)|(at most|maximum( number)? of|max(imum)?|up to|limit( of)?) \d* ?(image\(s\)|images|pictures|image inputs)|(images?|pictures?) (limit|count) (exceeded|reached)|exceeds? the (maximum|max|limit)[^.]{0,30}(images|pictures)", RegexOptions.IgnoreCase)]
    private static partial Regex TooManyImagesWords();

    [GeneratedRegex(@"(?<![-/])\b(images?|vision|multi-?modal|image_url|mmproj)\b(?![-:/])", RegexOptions.IgnoreCase)]
    private static partial Regex ImageWord();

    [GeneratedRegex(@"not (be )?supported|unsupported|do(es)?(n't| not) (support|accept|take)|only supported|no endpoints? found|not a multi-?modal|missing data required|unknown variant|can ?not (accept|process|read|handle)|can't (accept|process|read|handle)", RegexOptions.IgnoreCase)]
    private static partial Regex RefusalWord();

    private void Authorize(HttpRequestMessage req)
    {
        if (_apiKey is null)
            return;
        if (_anthropic)
            req.Headers.Add("x-api-key", _apiKey);
        else
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

    /// <summary>
    /// A request body sent in pieces under a watchdog: when a piece doesn't get through within the
    /// limit, <c>stall</c> is cancelled, which aborts the request so it can be sent again.
    /// </summary>
    private sealed class UploadContent : HttpContent
    {
        private const int Piece = 64 * 1024;
        private readonly byte[] _body;
        private readonly CancellationTokenSource _stall;
        private readonly TimeSpan _limit;
        private readonly Action<long, long>? _onSending;

        public UploadContent(byte[] body, CancellationTokenSource stall, TimeSpan limit, Action<long, long>? onSending)
        {
            (_body, _stall, _limit, _onSending) = (body, stall, limit, onSending);
            Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
        }

        protected override Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context) =>
            SerializeToStreamAsync(stream, context, CancellationToken.None);

        /// <summary>Whether sending the body began, which takes a connection to the server.</summary>
        public bool Started { get; private set; }

        protected override async Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context, CancellationToken ct)
        {
            Started = true;
            for (var at = 0; at < _body.Length; at += Piece)
            {
                _onSending?.Invoke(at, _body.Length);
                Watch(_limit);
                await stream.WriteAsync(_body.AsMemory(at, Math.Min(Piece, _body.Length - at)), ct).ConfigureAwait(false);
            }
            Watch(Timeout.InfiniteTimeSpan);
            _onSending?.Invoke(_body.Length, _body.Length);
        }

        private void Watch(TimeSpan limit)
        {
            try { _stall.CancelAfter(limit); }
            catch (ObjectDisposedException) { } // the request is already over
        }

        protected override bool TryComputeLength(out long length)
        {
            length = _body.Length;
            return true;
        }
    }
}
