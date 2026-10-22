using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace BlackBox.Core.Tests;

/// <summary>
/// Anthropic's Messages API as far as its documentation says how it behaves, for testing without a
/// key: the model list, request rules it answers 400 to (alternating turns, a result for every
/// call and results first, no empty text, at most four cache breakpoints, picture sizes, beta
/// headers), its prompt cache (a prefix match, looked for up to 20 blocks back from each
/// breakpoint), and its check that a thinking block comes back unchanged after an unchanged history.
/// </summary>
internal sealed partial class FakeAnthropic : HttpMessageHandler
{
    public sealed record Request(HttpMethod Method, string Path, Dictionary<string, string> Headers, JsonObject? Body);

    public List<Request> Requests { get; } = [];

    /// <summary>The rules requests broke; each was answered 400, as the API would.</summary>
    public List<string> Violations { get; } = [];

    /// <summary>Thinking blocks the API dropped because the history before them had changed (drop_block).</summary>
    public int DroppedThinking { get; private set; }

    /// <summary>Per message request: tokens read from the cache (an estimate of 4 characters a token).</summary>
    public List<int> CacheReads { get; } = [];

    /// <summary>Error answers given before the scripted ones.</summary>
    public Queue<(HttpStatusCode Status, string Body)> Failures { get; } = [];

    /// <summary>The reply to a message request (its body and number, from 0), as SSE; by default one text reply.</summary>
    public Func<JsonObject, int, string>? Reply { get; set; }

    /// <summary>Beta names this API knows; others are a 400, as on an API without them.</summary>
    public HashSet<string> KnownBetas { get; } = [AnthropicBetas.Binding, AnthropicBetas.Fallback];

    public JsonArray Models { get; } = new(
        Model("claude-haiku-5-5", "Claude Haiku 5.5", 1_000_000, 128_000),
        Model("claude-sonnet-5-5", "Claude Sonnet 5.5", 1_000_000, 128_000),
        Model("claude-opus-5-5", "Claude Opus 5.5", 1_000_000, 128_000),
        Model("claude-haiku-4-5-20251001", "Claude Haiku 4.5", 200_000, 64_000, adaptive: false));

    /// <summary>The page size of the model list, to test reading every page.</summary>
    public int PageSize { get; set; } = 1000;

    public static JsonObject Model(string id, string name, int context, int output, bool adaptive = true) => new()
    {
        ["type"] = "model",
        ["id"] = id,
        ["display_name"] = name,
        ["created_at"] = "2026-10-01T00:00:00Z",
        ["max_input_tokens"] = context,
        ["max_tokens"] = output,
        ["capabilities"] = new JsonObject
        {
            ["image_input"] = new JsonObject { ["supported"] = true },
            ["thinking"] = new JsonObject
            {
                ["supported"] = true,
                ["types"] = new JsonObject { ["adaptive"] = new JsonObject { ["supported"] = adaptive }, ["enabled"] = new JsonObject { ["supported"] = !adaptive } },
            },
            // As the API lists it: Haiku 4.5 takes no effort, the adaptive ones low to max.
            ["effort"] = new JsonObject
            {
                ["supported"] = adaptive,
                ["low"] = new JsonObject { ["supported"] = adaptive },
                ["medium"] = new JsonObject { ["supported"] = adaptive },
                ["high"] = new JsonObject { ["supported"] = adaptive },
                ["xhigh"] = new JsonObject { ["supported"] = adaptive },
                ["max"] = new JsonObject { ["supported"] = adaptive },
            },
        },
    };

    /// <summary>For each thinking signature handed out: the request prefix it was written after (hashed).</summary>
    private readonly Dictionary<string, string> _thinkingPrefixes = [];

    /// <summary>Hashes of the prefixes earlier requests cached, with their size.</summary>
    private readonly Dictionary<string, int> _cached = [];

    private int _messages;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var headers = request.Headers.ToDictionary(h => h.Key.ToLowerInvariant(), h => string.Join(",", h.Value));
        var text = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
        var body = text is null ? null : JsonNode.Parse(text) as JsonObject;
        var path = request.RequestUri!.AbsolutePath;
        Requests.Add(new Request(request.Method, request.RequestUri.PathAndQuery, headers, body));

        if (!headers.TryGetValue("x-api-key", out var key) || key.Length == 0)
            return Json(HttpStatusCode.Unauthorized, Error("authentication_error", "x-api-key header is required"));
        if (key != "sk-ant-test")
            return Json(HttpStatusCode.Unauthorized, Error("authentication_error", "invalid x-api-key"));
        if (!headers.ContainsKey("anthropic-version"))
            return Json(HttpStatusCode.BadRequest, Error("invalid_request_error", "anthropic-version: header is required"));

        if (request.Method == HttpMethod.Get && path == "/v1/models")
        {
            var query = System.Web.HttpUtility.ParseQueryString(request.RequestUri.Query);
            var all = Models.OfType<JsonObject>().ToList();
            var from = query["after_id"] is { } after ? all.FindIndex(m => (string)m["id"]! == after) + 1 : 0;
            var page = all.Skip(from).Take(PageSize).ToList();
            return Json(HttpStatusCode.OK, new JsonObject
            {
                ["data"] = new JsonArray(page.Select(m => (JsonNode)m.DeepClone()).ToArray()),
                ["has_more"] = from + page.Count < all.Count,
                ["first_id"] = page.FirstOrDefault()?["id"]?.DeepClone(),
                ["last_id"] = page.LastOrDefault()?["id"]?.DeepClone(),
            });
        }
        if (request.Method == HttpMethod.Get && path.StartsWith("/v1/models/", StringComparison.Ordinal))
        {
            var id = Uri.UnescapeDataString(path["/v1/models/".Length..]);
            return Models.OfType<JsonObject>().FirstOrDefault(m => (string)m["id"]! == id) is { } model
                ? Json(HttpStatusCode.OK, (JsonObject)model.DeepClone())
                : Json(HttpStatusCode.NotFound, Error("not_found_error", $"model: {id}"));
        }
        if (request.Method != HttpMethod.Post || path != "/v1/messages" || body is null)
            return Json(HttpStatusCode.NotFound, Error("not_found_error", "Not Found"));

        var betas = headers.TryGetValue("anthropic-beta", out var b) ? b.Split(',', StringSplitOptions.TrimEntries).ToHashSet() : [];
        if (betas.FirstOrDefault(name => !KnownBetas.Contains(name)) is { } unknown)
            return Json(HttpStatusCode.BadRequest, Error("invalid_request_error", $"Unexpected value(s) `{unknown}` for the `anthropic-beta` header."));
        if (Check(body, betas) is { } broken)
        {
            Violations.Add(broken);
            return Json(HttpStatusCode.BadRequest, Error("invalid_request_error", broken));
        }
        if (Failures.TryDequeue(out var failure))
            return new HttpResponseMessage(failure.Status) { Content = new StringContent(failure.Body, Encoding.UTF8, "application/json") };

        var cacheRead = CacheRead(body);
        CacheReads.Add(cacheRead);
        var sse = (Reply ?? ((_, _) => ClaudeSse.Text("OK.")))(body, _messages++);
        // The thinking this reply writes is bound to the request it answers.
        var prefix = PrefixHash(body, ((JsonArray)body["messages"]!).Count);
        foreach (Match m in SignatureInSse().Matches(sse))
            _thinkingPrefixes[m.Groups[1].Value] = prefix;
        sse = sse.Replace("\"cache_read_input_tokens\":0", $"\"cache_read_input_tokens\":{cacheRead}");
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(sse, Encoding.UTF8, "text/event-stream") };
    }

    [GeneratedRegex(@"""signature_delta"",""signature"":""([^""]+)""")]
    private static partial Regex SignatureInSse();

    /// <summary>The first rule the request breaks, as the API's 400 says it; null when it keeps them all.</summary>
    private string? Check(JsonObject body, HashSet<string> betas)
    {
        if (body["model"] is null) return "model: Field required";
        var model = Models.OfType<JsonObject>().FirstOrDefault(m => (string)m["id"]! == (string)body["model"]!);
        if (model is null) return $"model: {body["model"]}";
        if (body["max_tokens"] is not JsonValue mt || !mt.TryGetValue<int>(out var maxTokens) || maxTokens < 1) return "max_tokens: Field required";
        if (maxTokens > (int)model["max_tokens"]!) return $"max_tokens: {maxTokens} > {model["max_tokens"]}, which is the maximum allowed number of output tokens for {model["id"]}";
        foreach (var field in new[] { "temperature", "top_p", "top_k", "chat_template_kwargs", "stream_options", "reasoning_content" })
            if (body[field] is not null) return $"{field}: Extra inputs are not permitted";
        if (body["thinking"] is JsonObject thinking)
        {
            if ((string?)thinking["type"] is not ("adaptive")) return $"\"thinking.type.{thinking["type"]}\" is not supported for this model.";
            if (model["capabilities"]?["thinking"]?["types"]?["adaptive"]?["supported"]?.GetValue<bool>() != true) return "adaptive thinking is not supported on this model";
            if (thinking["block_binding"] is not null && !betas.Contains(AnthropicBetas.Binding)) return "thinking.block_binding: Extra inputs are not permitted";
        }
        if (body["fallbacks"] is not null && !betas.Contains(AnthropicBetas.Fallback)) return "fallbacks: Extra inputs are not permitted";
        if (body["output_config"]?["effort"] is { } effort && (string?)effort is not ("low" or "medium" or "high" or "xhigh" or "max")) return "output_config.effort: invalid";
        if (body["output_config"]?["effort"] is { } level && model["capabilities"]?["effort"]?[(string)level!]?["supported"]?.GetValue<bool>() != true)
            return $"output_config.effort: '{level}' is not supported for this model";

        var breakpoints = 0;
        foreach (var block in (body["system"] as JsonArray)?.OfType<JsonObject>() ?? [])
            breakpoints += block["cache_control"] is null ? 0 : 1;
        foreach (var tool in (body["tools"] as JsonArray)?.OfType<JsonObject>() ?? [])
        {
            if (!ToolName().IsMatch((string?)tool["name"] ?? "")) return $"tools.name: String should match pattern '^[a-zA-Z0-9_-]{{1,128}}$'";
            if ((string?)tool["input_schema"]?["type"] != "object") return "tools.input_schema.type: Input should be 'object'";
            breakpoints += tool["cache_control"] is null ? 0 : 1;
        }

        if (body["messages"] is not JsonArray messages || messages.Count == 0) return "messages: at least one message is required";
        var images = messages.OfType<JsonObject>().SelectMany(m => (m["content"] as JsonArray)?.OfType<JsonObject>() ?? []).Count(c => (string?)c["type"] == "image");
        HashSet<string> pending = [];
        for (var i = 0; i < messages.Count; i++)
        {
            var message = (JsonObject)messages[i]!;
            var role = (string?)message["role"];
            if (role != (i % 2 == 0 ? "user" : "assistant")) return "messages: roles must alternate between \"user\" and \"assistant\", starting with user";
            if (message["content"] is not JsonArray content || content.Count == 0) return $"messages.{i}: all messages must have non-empty content";
            var results = new HashSet<string>();
            var afterResults = false;
            for (var j = 0; j < content.Count; j++)
            {
                var block = (JsonObject)content[j]!;
                var type = (string?)block["type"];
                if (block["cache_control"] is not null)
                {
                    if (type is "thinking" or "redacted_thinking") return $"messages.{i}.content.{j}.thinking.cache_control: Extra inputs are not permitted";
                    breakpoints++;
                }
                switch (type)
                {
                    case "text":
                        if (string.IsNullOrWhiteSpace((string?)block["text"])) return $"messages.{i}.content.{j}: text content blocks must contain non-whitespace text";
                        afterResults = true;
                        break;
                    case "image":
                        if (role != "user") return $"messages.{i}.content.{j}: images are only allowed in user messages";
                        var source = block["source"];
                        if ((string?)source?["type"] != "base64" || (string?)source["media_type"] is not ("image/jpeg" or "image/png" or "image/gif" or "image/webp"))
                            return $"messages.{i}.content.{j}.source: Input should be a valid image";
                        var data = (string)source["data"]!;
                        if (data.Length > 5 * 1024 * 1024) return $"messages.{i}.content.{j}.image.source.base64: image exceeds 5 MB maximum";
                        var (width, height) = Size(Convert.FromBase64String(data));
                        var most = images > 20 ? 2000 : 8000;
                        if (Math.Max(width, height) > most)
                            return images > 20 ? $"messages.{i}.content.{j}.image.source.base64.data: At least one of the image dimensions exceed max allowed size for many-image requests: 2000 pixels"
                                               : $"messages.{i}.content.{j}.image.source.base64.data: At least one of the image dimensions exceed max allowed size: 8000 pixels";
                        afterResults = true;
                        break;
                    case "tool_result":
                        if (role != "user") return $"messages.{i}.content.{j}: tool_result blocks are only allowed in user messages";
                        if (afterResults) return $"messages.{i}: tool_result blocks must come first in the user message, before any other content";
                        var id = (string?)block["tool_use_id"] ?? "";
                        if (!pending.Contains(id)) return $"messages.{i}.content.{j}: unexpected `tool_use_id` found in `tool_result` blocks: {id}. Each `tool_result` block must have a corresponding `tool_use` block in the previous message.";
                        results.Add(id);
                        if (block["content"] is JsonValue s && string.IsNullOrWhiteSpace((string?)s)) return $"messages.{i}.content.{j}: tool_result content must not be empty text";
                        break;
                    case "tool_use":
                        if (role != "assistant") return $"messages.{i}.content.{j}: tool_use blocks are only allowed in assistant messages";
                        if (!ToolName().IsMatch((string?)block["id"] ?? "")) return $"messages.{i}.content.{j}.id: String should match pattern '^[a-zA-Z0-9_-]+$'";
                        if (block["input"] is not JsonObject) return $"messages.{i}.content.{j}.input: Input should be a valid dictionary";
                        break;
                    case "thinking":
                        if (role != "assistant") return $"messages.{i}.content.{j}: thinking blocks are only allowed in assistant messages";
                        var signature = (string?)block["signature"] ?? "";
                        if (!_thinkingPrefixes.TryGetValue(signature, out var expected) || Sign((string?)block["thinking"] ?? "") != signature)
                            return $"messages.{i}.content.{j}: Invalid `signature` in `thinking` block.";
                        if (expected != PrefixHash(body, i))
                        {
                            if ((string?)body["thinking"]?["block_binding"]?["prefix_mismatch_behavior"] == "drop_block")
                                DroppedThinking++;
                            else
                                return $"messages.{i}.content.{j}: Invalid `signature` in `thinking` block. The block is bound to a different conversation. Remove the block, or set `thinking.block_binding.prefix_mismatch_behavior` to \"drop_block\".";
                        }
                        break;
                    case "redacted_thinking":
                        break;
                    default:
                        return $"messages.{i}.content.{j}.type: Input tag '{type}' found using 'type' does not match any of the expected tags";
                }
            }
            if (role == "user")
            {
                if (pending.FirstOrDefault(id => !results.Contains(id)) is { } missing)
                    return $"messages.{i}: `tool_use` ids were found without `tool_result` blocks immediately after: {missing}. Each `tool_use` block must have a corresponding `tool_result` block in the next message.";
                pending.Clear();
            }
            else
            {
                pending = content.OfType<JsonObject>().Where(c => (string?)c["type"] == "tool_use").Select(c => (string)c["id"]!).ToHashSet();
                if (i == messages.Count - 1) return "This model does not support assistant message prefill. The conversation must end with a user message.";
            }
        }
        if (pending.Count > 0) return $"`tool_use` ids were found without `tool_result` blocks immediately after: {pending.First()}";
        if (breakpoints > 4) return $"A maximum of 4 blocks with cache_control may be provided. Found {breakpoints}.";
        return null;
    }

    [GeneratedRegex("^[a-zA-Z0-9_-]{1,128}$")]
    private static partial Regex ToolName();

    /// <summary>The signature of a thinking block: it names the thinking it signs, so a changed text shows.</summary>
    public static string Sign(string thinking) => "sig_" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(thinking)))[..24];

    private static (int Width, int Height) Size(byte[] image)
    {
        using var codec = SkiaSharp.SKCodec.Create(new SkiaSharp.SKMemoryStream(image));
        return codec is null ? (0, 0) : (codec.Info.Width, codec.Info.Height);
    }

    /// <summary>One line per block of the prompt without its cache markers: tools, system, then each message's blocks.</summary>
    private static List<string> Blocks(JsonObject body, int messages)
    {
        var lines = new List<string>
        {
            "tools:" + Plain(body["tools"]),
            "system:" + Plain(body["system"]),
        };
        var list = (JsonArray)body["messages"]!;
        for (var i = 0; i < Math.Min(messages, list.Count); i++)
            foreach (var block in ((JsonArray)list[i]!["content"]!).OfType<JsonObject>())
                lines.Add(list[i]!["role"] + ":" + Plain(block));
        return lines;
    }

    private static string Plain(JsonNode? node)
    {
        if (node is null) return "";
        var copy = node.DeepClone();
        void Strip(JsonNode? n)
        {
            if (n is JsonObject o) { o.Remove("cache_control"); foreach (var (_, v) in o) Strip(v); }
            else if (n is JsonArray a) foreach (var v in a) Strip(v);
        }
        Strip(copy);
        return copy.ToJsonString();
    }

    private static string Hash(IEnumerable<string> lines)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var line in lines)
            sha.AppendData(Encoding.UTF8.GetBytes(line + "\n"));
        return Convert.ToHexString(sha.GetHashAndReset());
    }

    private static string PrefixHash(JsonObject body, int messages) => Hash(Blocks(body, messages));

    /// <summary>
    /// What the cache gives this request: for each breakpoint, from the last, the longest prefix an
    /// earlier request cached that ends at most 20 blocks before it; then what this request caches.
    /// </summary>
    private int CacheRead(JsonObject body)
    {
        var lines = Blocks(body, int.MaxValue);
        var marked = new List<int>();
        if (((body["tools"] as JsonArray)?.OfType<JsonObject>().Any(t => t["cache_control"] is not null) ?? false)) marked.Add(0);
        if (((body["system"] as JsonArray)?.OfType<JsonObject>().Any(t => t["cache_control"] is not null) ?? false)) marked.Add(1);
        var at = 2;
        foreach (var message in ((JsonArray)body["messages"]!).OfType<JsonObject>())
            foreach (var block in ((JsonArray)message["content"]!).OfType<JsonObject>())
            {
                if (block["cache_control"] is not null) marked.Add(at);
                at++;
            }
        var hashes = new List<string>();
        using (var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            foreach (var line in lines)
            {
                sha.AppendData(Encoding.UTF8.GetBytes(line + "\n"));
                hashes.Add(Convert.ToHexString(sha.GetCurrentHash()));
            }
        var sizes = new List<int>();
        var total = 0;
        foreach (var line in lines)
            sizes.Add(total += line.Length / 4);
        var read = 0;
        foreach (var point in marked.OrderByDescending(p => p))
        {
            for (var back = point; back >= Math.Max(0, point - 20); back--)
                if (_cached.TryGetValue(hashes[back], out var size))
                {
                    read = Math.Max(read, size);
                    break;
                }
        }
        foreach (var point in marked)
            _cached[hashes[point]] = sizes[point];
        return read;
    }

    private static HttpResponseMessage Json(HttpStatusCode status, JsonObject body) =>
        new(status) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };

    public static JsonObject Error(string type, string message) =>
        new() { ["type"] = "error", ["error"] = new JsonObject { ["type"] = type, ["message"] = message }, ["request_id"] = "req_test" };
}

internal static class AnthropicBetas
{
    public const string Binding = "thinking-binding-controls-2026-08-01";
    public const string Fallback = "server-side-fallback-2026-07-01";
}

/// <summary>Messages API streams as Anthropic sends them.</summary>
internal static class ClaudeSse
{
    public abstract record Block;
    public sealed record Thinking(string Text) : Block;
    public sealed record Said(string Text) : Block;
    public sealed record Call(string Id, string Name, string Json) : Block;
    public sealed record Fallback(string From, string To) : Block;

    public static string Text(string text) => Message("end_turn", new Said(text));

    public static string Message(string stopReason, params Block[] blocks) => Message(stopReason, null, blocks);

    public static string Message(string stopReason, JsonObject? stopDetails, params Block[] blocks) => Message(40, stopReason, stopDetails, blocks);

    /// <summary>A reply to a prompt of <paramref name="inputTokens"/> (read anew; 300 more are written to the cache).</summary>
    public static string Message(int inputTokens, string stopReason, JsonObject? stopDetails, params Block[] blocks)
    {
        var sse = new StringBuilder();
        void Event(string type, JsonObject data)
        {
            data["type"] = type;
            sse.Append("event: ").Append(type).Append('\n').Append("data: ").Append(data.ToJsonString()).Append("\n\n");
        }
        Event("message_start", new JsonObject
        {
            ["message"] = new JsonObject
            {
                ["id"] = "msg_test", ["type"] = "message", ["role"] = "assistant", ["model"] = "claude-opus-5-5", ["content"] = new JsonArray(),
                ["usage"] = new JsonObject { ["input_tokens"] = inputTokens, ["cache_creation_input_tokens"] = 300, ["cache_read_input_tokens"] = 0, ["output_tokens"] = 1 },
            },
        });
        Event("ping", new JsonObject());
        for (var i = 0; i < blocks.Length; i++)
        {
            switch (blocks[i])
            {
                case Thinking t:
                    Event("content_block_start", new JsonObject { ["index"] = i, ["content_block"] = new JsonObject { ["type"] = "thinking", ["thinking"] = "", ["signature"] = "" } });
                    foreach (var piece in Pieces(t.Text))
                        Event("content_block_delta", new JsonObject { ["index"] = i, ["delta"] = new JsonObject { ["type"] = "thinking_delta", ["thinking"] = piece } });
                    Event("content_block_delta", new JsonObject { ["index"] = i, ["delta"] = new JsonObject { ["type"] = "signature_delta", ["signature"] = FakeAnthropic.Sign(t.Text) } });
                    break;
                case Said s:
                    Event("content_block_start", new JsonObject { ["index"] = i, ["content_block"] = new JsonObject { ["type"] = "text", ["text"] = "" } });
                    foreach (var piece in Pieces(s.Text))
                        Event("content_block_delta", new JsonObject { ["index"] = i, ["delta"] = new JsonObject { ["type"] = "text_delta", ["text"] = piece } });
                    break;
                case Call c:
                    Event("content_block_start", new JsonObject { ["index"] = i, ["content_block"] = new JsonObject { ["type"] = "tool_use", ["id"] = c.Id, ["name"] = c.Name, ["input"] = new JsonObject() } });
                    foreach (var piece in Pieces(c.Json))
                        Event("content_block_delta", new JsonObject { ["index"] = i, ["delta"] = new JsonObject { ["type"] = "input_json_delta", ["partial_json"] = piece } });
                    break;
                case Fallback f:
                    Event("content_block_start", new JsonObject { ["index"] = i, ["content_block"] = new JsonObject { ["type"] = "fallback", ["from"] = new JsonObject { ["model"] = f.From }, ["to"] = new JsonObject { ["model"] = f.To } } });
                    break;
            }
            Event("content_block_stop", new JsonObject { ["index"] = i });
        }
        var delta = new JsonObject { ["stop_reason"] = stopReason, ["stop_sequence"] = null };
        if (stopDetails is not null)
            delta["stop_details"] = stopDetails;
        Event("message_delta", new JsonObject { ["delta"] = delta, ["usage"] = new JsonObject { ["output_tokens"] = 25 } });
        Event("message_stop", new JsonObject());
        return sse.ToString();
    }

    /// <summary>Text in pieces of a few characters, as a stream brings it.</summary>
    private static IEnumerable<string> Pieces(string text)
    {
        for (var at = 0; at < text.Length; at += 7)
            yield return text.Substring(at, Math.Min(7, text.Length - at));
    }
}
