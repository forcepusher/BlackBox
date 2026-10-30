using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Sloperator.Core.Llm;

/// <summary>
/// What Sloperator knows of a Claude model when it asks it something: the most a reply may be
/// (thinking included), whether it thinks adaptively (Claude 4.6 and later, where effort is the
/// control), whether it takes pictures, and whether a refusal can be answered by another model
/// on Anthropic's side (<c>fallbacks: "default"</c>: Opus and Fable 5 on, Sonnet 5.5 on).
/// </summary>
internal sealed partial record ClaudeModel(string Id, int MaxTokens, bool Adaptive, bool Vision, bool Fallbacks)
{
    /// <summary>From Anthropic's /v1/models entry (max_tokens, capabilities), the name filling in what it leaves out.</summary>
    public static ClaudeModel FromEntry(JsonObject entry)
    {
        var id = entry["id"]?.GetValue<string>() ?? "";
        var named = FromName(id);
        var caps = entry["capabilities"] as JsonObject;
        return named with
        {
            MaxTokens = Int(entry["max_tokens"]) ?? named.MaxTokens,
            Adaptive = Supported(caps?["thinking"]?["types"]?["adaptive"]) ?? named.Adaptive,
            Vision = Supported(caps?["image_input"]) ?? named.Vision,
        };

        static bool? Supported(JsonNode? node) =>
            node?["supported"] is JsonValue v && v.TryGetValue<bool>(out var b) ? b : null;
    }

    /// <summary>
    /// The longest side screenshots are sent at, so that Claude points on the picture Sloperator sent:
    /// 2000 px for the models that see up to 2576 px (Claude 4.7 and later), the most a picture may
    /// have in a request with more than 20; 1184 for the ones before, which make a picture over
    /// 1568 px or about 1.15 megapixels (1,600 tokens) smaller. At 1280, a 4:3 screen's 1280x960
    /// and a 5:4 one's 1280x1024 were over it, shrunk by Claude, and its points off by up to 4%.
    /// </summary>
    public int ScreenshotSide
    {
        get
        {
            var (family, version) = Parse(Id);
            return version >= 4.7 || family is null ? AnthropicMessages.ManyImagesSide : 1184;
        }
    }

    /// <summary>What the name says: <c>claude-opus-5-5</c>, <c>claude-haiku-4-5-20251001</c>, <c>claude-3-5-sonnet-latest</c>, <c>anthropic/claude-sonnet-4.6</c>.</summary>
    public static ClaudeModel FromName(string id)
    {
        var (family, version) = Parse(id);
        var adaptive = family is "haiku" ? version >= 5 : family is not null && version >= 4.6;
        var most = version >= 4.6 ? 128_000 : version >= 4 ? 32_000 : version >= 3.5 ? 8_192 : 4_096;
        if (family is "haiku" && version is >= 4.5 and < 5)
            most = 64_000;
        var fallbacks = family is "opus" or "fable" ? version >= 5 : family is "sonnet" && version >= 5.5;
        return new ClaudeModel(id, most, adaptive, true, fallbacks);
    }

    /// <summary>The family (opus, sonnet, haiku, fable, mythos) and version (5.5, 4.6) a Claude model id names; nulls for another name.</summary>
    internal static (string? Family, double Version) Parse(string id)
    {
        if (NewName().Match(id) is { Success: true } m)
            return (m.Groups[1].Value.ToLowerInvariant(), Version(m.Groups[2].Value, m.Groups[3].Value));
        if (OldName().Match(id) is { Success: true } o)
            return (o.Groups[3].Value.ToLowerInvariant(), Version(o.Groups[1].Value, o.Groups[2].Value));
        return (null, 0);

        // A minor version of 1 or 2 digits; a date (20251001) is no version.
        static double Version(string major, string minor) =>
            int.Parse(major) + (minor.Length is 1 or 2 ? int.Parse(minor) / Math.Pow(10, minor.Length) : 0);
    }

    // OpenRouter writes the version with a dot: anthropic/claude-haiku-5.5.
    [GeneratedRegex(@"claude-(opus|sonnet|haiku|fable|mythos)-(\d+)(?:[-.](\d+))?", RegexOptions.IgnoreCase)]
    private static partial Regex NewName();

    [GeneratedRegex(@"claude-(\d+)(?:[-.](\d))?-(opus|sonnet|haiku)", RegexOptions.IgnoreCase)]
    private static partial Regex OldName();

    private static int? Int(JsonNode? node) => node is JsonValue v && v.TryGetValue<int>(out var n) ? n : null;
}

/// <summary>
/// Translates between the OpenAI chat format Sloperator speaks and Anthropic's Messages API
/// (<c>POST /v1/messages</c>), so Claude works with its own features rather than through
/// Anthropic's OpenAI-compatible layer, which has no prompt caching and gives no thinking back.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Prompt caching: a breakpoint on the system prompt (which caches the tools too), on the end
/// of the request, and where the previous request ended, so each step reads the whole conversation
/// so far from the cache (at a tenth of the price or less) and pays only for what it adds.</item>
/// <item>Thinking: adaptive, with summaries shown as the reasoning; each reply's blocks are kept on
/// the message (<see cref="ChatMessage.AnthropicContent"/>) and sent back unchanged, signatures and
/// all, as Claude requires. Claude checks that nothing before a thinking block changed since it
/// was written; where Sloperator does change the history (an MCP server added mid-run), the request
/// asks for such blocks to be dropped (<c>drop_block</c>) rather than refused.</item>
/// <item>Messages: tool results become <c>tool_result</c> blocks in a user turn, ahead of the
/// screenshot that follows them; turns of the same role are joined, as Claude wants them to
/// alternate; a tool call without a result gets one, and a result without its call becomes text.</item>
/// <item>Pictures: base64 image blocks, made smaller only where Claude would refuse them (more than
/// 20 in a request with a side over 2000 px, a side over 8000 px, more than 5 MB).</item>
/// </list>
/// </remarks>
public static partial class AnthropicMessages
{
    /// <summary>The Messages API version every request names.</summary>
    internal const string Version = "2023-06-01";

    /// <summary>Lets the request ask for thinking blocks whose history changed to be dropped instead of failing it.</summary>
    internal const string ThinkingBindingBeta = "thinking-binding-controls-2026-08-01";

    /// <summary>Lets a refused request go to the model Anthropic recommends for what was refused.</summary>
    internal const string FallbackBeta = "server-side-fallback-2026-07-01";

    /// <summary>What may vary between tries of a request when the API turns a part of it down.</summary>
    /// <param name="Betas">The beta features (drop_block, fallbacks); off once the API says it doesn't know them.</param>
    /// <param name="Eager">Tool calls streamed as they are written (<c>eager_input_streaming</c>).</param>
    /// <param name="Summaries">Thinking summarized for the reasoning Sloperator shows (<c>display: "summarized"</c>; left out by default since Opus 4.7).</param>
    /// <param name="StripThinking">
    /// Thinking the API refused as written for another history, left out: 1 that of the replies
    /// before the latest (whose thinking must go back whole while its calls are answered), 2 all.
    /// </param>
    /// <param name="MaxImageSide">Pictures made to fit this many pixels a side (20+ pictures at once take 2000 at most).</param>
    internal sealed record Wire(bool Betas = true, bool Eager = true, bool Summaries = true, int StripThinking = 0, int MaxImageSide = 8000);

    /// <summary>The reply limit asked for, thinking included.</summary>
    internal const int MaxReply = 64_000;

    /// <summary>More pictures than this in one request, and none may be over <see cref="ManyImagesSide"/> pixels a side.</summary>
    internal const int ManyImages = 20;
    internal const int ManyImagesSide = 2000;

    /// <summary>The most pictures the API takes in one request.</summary>
    internal const int MaxImages = 100;

    /// <summary>The request body; <paramref name="betas"/> gets the beta features it uses, for the anthropic-beta header.</summary>
    internal static JsonObject BuildRequest(IReadOnlyList<ChatMessage> messages, IReadOnlyList<ToolDefinition> tools, ChatRequestOptions options,
        ClaudeModel model, Wire wire, List<string> betas)
    {
        var req = new JsonObject { ["model"] = options.Model };
        // Thinking counts toward max_tokens and can't be capped on these models: a reply limit
        // meant for the answer alone (a fresh look's 4096) would cut it off mid-thought. A high one
        // costs nothing: only what is written counts, also toward the output tokens a minute.
        var room = Math.Min(model.MaxTokens, MaxReply);
        req["max_tokens"] = model.Adaptive ? room : Math.Min(options.MaxTokens ?? room, room);

        var system = string.Join("\n\n", messages.Where(m => m.Role == ChatRole.System).Take(1).Select(TextOf));
        if (system.Trim().Length > 0)
            req["system"] = new JsonArray(Cached(new JsonObject { ["type"] = "text", ["text"] = system }));

        if (tools.Count > 0)
        {
            var list = new JsonArray();
            foreach (var tool in tools)
            {
                var schema = (JsonObject)tool.Parameters.DeepClone();
                schema["type"] ??= "object";
                var entry = new JsonObject { ["name"] = tool.Name, ["description"] = tool.Description, ["input_schema"] = schema };
                // Long calls (a whole file) stream as they are written instead of arriving at the end.
                if (wire.Eager)
                    entry["eager_input_streaming"] = true;
                list.Add((JsonNode)entry);
            }
            if (system.Trim().Length == 0)
                Cached((JsonObject)list[^1]!);
            req["tools"] = list;
        }

        req["messages"] = Messages(messages.Skip(messages.Count > 0 && messages[0].Role == ChatRole.System ? 1 : 0).ToList(), wire);

        if (model.Adaptive)
        {
            var thinking = new JsonObject { ["type"] = "adaptive" };
            if (wire.Summaries)
                thinking["display"] = "summarized";
            if (wire.Betas)
            {
                thinking["block_binding"] = new JsonObject { ["prefix_mismatch_behavior"] = "drop_block" };
                betas.Add(ThinkingBindingBeta);
            }
            req["thinking"] = thinking;
        }
        // The effort picked in the top bar (where the model takes one).
        if (options.Effort is { } effort)
            req["output_config"] = new JsonObject { ["effort"] = effort };
        if (model.Fallbacks && wire.Betas)
        {
            req["fallbacks"] = "default";
            betas.Add(FallbackBeta);
        }
        req["stream"] = true;
        return req;
    }

    private static JsonObject Cached(JsonObject block)
    {
        block["cache_control"] = new JsonObject { ["type"] = "ephemeral" };
        return block;
    }

    private static string TextOf(ChatMessage m) =>
        m.Parts is { Count: > 0 } parts ? string.Join("\n", parts.Where(p => !p.IsImage).Select(p => p.Text)) : m.Content ?? "";

    /// <summary>The conversation as alternating user and assistant turns of content blocks, with its cache breakpoints.</summary>
    private static JsonArray Messages(IReadOnlyList<ChatMessage> history, Wire wire)
    {
        var images = history.Sum(m => m.Parts?.Count(p => p.IsImage) ?? 0);
        // A request takes 100 pictures at most (a 1M model 600, but 32 MB comes first): past that,
        // the oldest are left out, fifty at a time, so the prompt cache breaks once every fifty
        // steps rather than at every one. Some 120 steps into a task on Haiku 4.5, every request
        // was refused, and so was every later message in the chat.
        var leftOut = images > MaxImages ? (images - MaxImages + 49) / 50 * 50 : 0;
        var maxSide = images - leftOut > ManyImages ? Math.Min(wire.MaxImageSide, ManyImagesSide) : wire.MaxImageSide;
        var seen = 0;

        var turns = new List<(string Role, List<JsonObject> Blocks)>();
        void Add(string role, List<JsonObject> blocks)
        {
            if (blocks.Count == 0)
                return;
            if (turns.Count > 0 && turns[^1].Role == role)
                turns[^1].Blocks.AddRange(blocks);
            else
                turns.Add((role, blocks));
        }

        var latest = history.LastOrDefault(m => m.Role == ChatRole.Assistant);
        foreach (var m in history)
        {
            switch (m.Role)
            {
                case ChatRole.Assistant:
                    Add(ChatRole.Assistant, AssistantBlocks(m, wire.StripThinking > (ReferenceEquals(m, latest) ? 1 : 0)));
                    break;
                case ChatRole.Tool:
                    var result = new JsonObject { ["type"] = "tool_result", ["tool_use_id"] = ToolId(m.ToolCallId ?? "") };
                    if (!string.IsNullOrWhiteSpace(m.Content))
                        result["content"] = m.Content;
                    Add(ChatRole.User, [result]);
                    break;
                case ChatRole.System:
                    // Only the first is the system prompt; one later on is said in the conversation.
                    Add(ChatRole.User, TextBlocks("[System] " + TextOf(m)));
                    break;
                default:
                    var blocks = new List<JsonObject>();
                    if (m.Parts is { Count: > 0 } parts)
                        foreach (var part in parts)
                            blocks.AddRange(!part.IsImage ? TextBlocks(part.Text)
                                : seen++ < leftOut ? TextBlocks("(An earlier picture, left out: a request takes 100 at most.)")
                                : [ImageBlock(part, maxSide)]);
                    else
                        blocks.AddRange(TextBlocks(m.Content));
                    Add(ChatRole.User, blocks);
                    break;
            }
        }

        // Every call gets its result in the next turn, results come first in it, and a result
        // without its call (steps trimmed out of the middle) is said as text.
        for (var i = 0; i < turns.Count; i++)
        {
            if (turns[i].Role != ChatRole.User)
                continue;
            var calls = i > 0 ? turns[i - 1].Blocks.Where(b => Type(b) == "tool_use").Select(b => (string)b["id"]!).ToList() : [];
            var results = new List<JsonObject>();
            var rest = new List<JsonObject>();
            foreach (var block in turns[i].Blocks)
            {
                if (Type(block) != "tool_result")
                    rest.Add(block);
                else if (calls.Contains((string)block["tool_use_id"]!) && results.All(r => (string)r["tool_use_id"]! != (string)block["tool_use_id"]!))
                    results.Add(block);
                else
                    rest.InsertRange(0, TextBlocks("[Result of an earlier tool call]\n" + (block["content"]?.GetValue<string>() ?? "")));
            }
            foreach (var missing in calls.Where(id => results.All(r => (string)r["tool_use_id"]! != id)))
                results.Add(new JsonObject { ["type"] = "tool_result", ["tool_use_id"] = missing, ["content"] = "Not run." });
            turns[i].Blocks.Clear();
            turns[i].Blocks.AddRange(results);
            turns[i].Blocks.AddRange(rest);
        }
        // An assistant turn with calls at the very end gets its results too.
        if (turns.Count > 0 && turns[^1].Role == ChatRole.Assistant && turns[^1].Blocks.Any(b => Type(b) == "tool_use"))
            turns.Add((ChatRole.User, turns[^1].Blocks.Where(b => Type(b) == "tool_use")
                .Select(b => new JsonObject { ["type"] = "tool_result", ["tool_use_id"] = (string)b["id"]!, ["content"] = "Not run." }).ToList()));
        // The conversation starts with the user. It ends with them too, as every step's request
        // does (a user message or tool results): no "Continue." is made up in their name.
        if (turns.Count == 0 || turns[0].Role != ChatRole.User)
            turns.Insert(0, (ChatRole.User, TextBlocks("[Sloperator] (Earlier messages left out.)")));

        // The end of this request is where the next one reads up to; where the previous request
        // ended (the user turn before the last reply), this one reads.
        Cached(turns[^1].Blocks[^1]);
        if (turns.Count >= 3)
            Cached(turns[turns.FindLastIndex(turns.Count - 2, t => t.Role == ChatRole.User)].Blocks[^1]);

        var array = new JsonArray();
        foreach (var (role, blocks) in turns)
            array.Add((JsonNode)new JsonObject { ["role"] = role, ["content"] = new JsonArray(blocks.Select(b => (JsonNode)b).ToArray()) });
        return array;
    }

    /// <summary>
    /// A reply as Claude wrote it (its blocks, thinking among them, in their order) with the tool
    /// calls Sloperator kept: one it took out (a check after "the task is complete") is left out.
    /// A reply from another model is its text and calls; its reasoning can't be sent as Claude's thinking.
    /// </summary>
    private static List<JsonObject> AssistantBlocks(ChatMessage m, bool stripThinking)
    {
        var blocks = new List<JsonObject>();
        var calls = (m.ToolCalls ?? []).ToList();
        if (m.AnthropicContent is { } native)
        {
            foreach (var block in native.OfType<JsonObject>())
            {
                switch (Type(block))
                {
                    // Not thinking cut off before its signature came (a reply that ran out of room
                    // mid-thought): sent back, it is refused, and dropping the latest reply's thinking
                    // alone keeps it.
                    case "thinking" when !stripThinking && block["signature"] is JsonValue signature && signature.TryGetValue<string>(out var signed) && signed.Length > 0:
                    case "redacted_thinking" when !stripThinking:
                        blocks.Add((JsonObject)block.DeepClone());
                        break;
                    case "text" when block["text"]?.GetValue<string>() is { } text && text.Trim().Length > 0:
                        blocks.Add(new JsonObject { ["type"] = "text", ["text"] = text });
                        break;
                    case "tool_use" when calls.FirstOrDefault(c => c.Id == (string?)block["id"]) is { } call:
                        blocks.Add(ToolUse(call));
                        calls.Remove(call);
                        break;
                }
            }
        }
        else
        {
            blocks.AddRange(TextBlocks(m.Content));
        }
        blocks.AddRange(calls.Select(ToolUse));
        // A reply of thinking alone (it wrote nothing) still needs something said.
        if (blocks.Count > 0 && blocks.All(b => Type(b) is "thinking" or "redacted_thinking"))
            blocks.AddRange(TextBlocks("(No reply.)"));
        return blocks;
    }

    private static JsonObject ToolUse(ToolCall call) => new()
    {
        ["type"] = "tool_use",
        ["id"] = ToolId(call.Id),
        ["name"] = call.Name,
        ["input"] = Input(call.Arguments),
    };

    /// <summary>A call's arguments as the object Claude sends them in; arguments that aren't one (from another model) as an empty one.</summary>
    internal static JsonObject Input(string arguments)
    {
        try
        {
            if (JsonNode.Parse(string.IsNullOrWhiteSpace(arguments) ? "{}" : arguments) is JsonObject input)
                return input;
        }
        catch (JsonException) { }
        return new JsonObject();
    }

    /// <summary>Tool call ids from another server (LM Studio, Kilo) with characters Claude doesn't take in one.</summary>
    internal static string ToolId(string id)
    {
        var clean = ToolIdUnsafe().Replace(id, "_");
        return clean.Length > 0 ? clean : "call";
    }

    [GeneratedRegex("[^a-zA-Z0-9_-]")]
    private static partial Regex ToolIdUnsafe();

    private static List<JsonObject> TextBlocks(string? text) =>
        string.IsNullOrWhiteSpace(text) ? [] : [new JsonObject { ["type"] = "text", ["text"] = text }];

    private static string? Type(JsonObject block) => block["type"] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static JsonObject ImageBlock(ContentPart part, int maxSide)
    {
        var (bytes, mime) = AnthropicImages.Fit(part.ImageBytes!, part.ImageMime, maxSide);
        return new JsonObject
        {
            ["type"] = "image",
            ["source"] = new JsonObject { ["type"] = "base64", ["media_type"] = mime, ["data"] = Convert.ToBase64String(bytes) },
        };
    }

    /// <summary>
    /// Reads a Messages API stream into one reply: its text, thinking summaries as the reasoning,
    /// its tool calls, token counts, and its blocks as they came for sending back.
    /// </summary>
    internal static async Task<ChatMessage> ReadStreamAsync(TextReader reader, Action<StreamDelta>? onDelta, CancellationToken ct, Action<string>? onToolCall = null)
    {
        var blocks = new SortedDictionary<int, JsonObject>();
        var texts = new Dictionary<int, StringBuilder>();
        var content = new StringBuilder();
        var reasoning = new StringBuilder();
        int? input = null, cacheWrite = null, cacheRead = null, output = null;
        string? stopReason = null;
        JsonNode? stopDetails = null;
        var finished = false;
        var lastWritten = -1;
        string? callInProgress = null;

        while (await reader.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
        {
            if (!line.StartsWith("data:", StringComparison.Ordinal))
                continue;
            JsonObject? e;
            try { e = JsonNode.Parse(line[5..].Trim()) as JsonObject; }
            catch (JsonException) { continue; }
            if (e is null)
                continue;
            var index = e["index"] is JsonValue iv && iv.TryGetValue<int>(out var i) ? i : -1;
            switch (Str(e["type"]))
            {
                case "message_start":
                    Usage(e["message"]?["usage"]);
                    break;
                case "content_block_start" when index >= 0 && e["content_block"] is JsonObject started:
                    var block = (JsonObject)started.DeepClone();
                    blocks[index] = block;
                    if (Str(block["type"]) is "tool_use" or "server_tool_use")
                    {
                        texts[index] = new StringBuilder();
                        callInProgress = Str(block["name"]);
                        if (callInProgress is { Length: > 0 })
                            onToolCall?.Invoke(callInProgress);
                    }
                    else if (Str(block["type"]) is "text" && Str(block["text"]) is { Length: > 0 } first)
                    {
                        Write(index, first);
                    }
                    break;
                case "content_block_delta" when index >= 0 && blocks.TryGetValue(index, out var open) && e["delta"] is JsonObject delta:
                    switch (Str(delta["type"]))
                    {
                        case "text_delta" when Str(delta["text"]) is { Length: > 0 } text:
                            open["text"] = (Str(open["text"]) ?? "") + text;
                            Write(index, text);
                            break;
                        case "thinking_delta" when Str(delta["thinking"]) is { Length: > 0 } thought:
                            open["thinking"] = (Str(open["thinking"]) ?? "") + thought;
                            if (reasoning.Length > 0 && Str(open["thinking"]) == thought)
                            {
                                reasoning.Append("\n\n");
                                onDelta?.Invoke(new StreamDelta(StreamChannel.Reasoning, "\n\n"));
                            }
                            reasoning.Append(thought);
                            onDelta?.Invoke(new StreamDelta(StreamChannel.Reasoning, thought));
                            break;
                        case "signature_delta" when Str(delta["signature"]) is { } signature:
                            open["signature"] = (Str(open["signature"]) ?? "") + signature;
                            break;
                        case "input_json_delta" when texts.TryGetValue(index, out var json):
                            json.Append(Str(delta["partial_json"]));
                            break;
                    }
                    break;
                case "content_block_stop" when index >= 0 && blocks.TryGetValue(index, out var done):
                    if (texts.TryGetValue(index, out var args))
                    {
                        // A call whose input came whole in its start has no pieces to put together.
                        if (args.Length > 0)
                            done["input"] = Input(args.ToString());
                        callInProgress = null;
                    }
                    break;
                case "message_delta":
                    stopReason = Str(e["delta"]?["stop_reason"]) ?? stopReason;
                    stopDetails = e["delta"]?["stop_details"] ?? stopDetails;
                    Usage(e["usage"]);
                    break;
                case "message_stop":
                    finished = true;
                    break;
                case "error":
                    throw StreamError(e["error"] as JsonObject, blocks.Count > 0, callInProgress);
            }
            if (finished)
                break;
        }
        // A stream that just stops, without message_stop, was cut by the network.
        if (!finished)
            throw new IOException("The reply stream ended before the server finished.");

        if (stopReason == "refusal")
            throw new LlmException(RefusalText(stopDetails));

        // After a fallback to another model mid-reply, what the declined model wrote before the
        // switch goes back as text only: its thinking and calls belong to it.
        var boundary = blocks.LastOrDefault(b => Str(b.Value["type"]) == "fallback").Key;
        var hasBoundary = blocks.Any(b => Str(b.Value["type"]) == "fallback");
        var kept = new JsonArray();
        var calls = new List<ToolCall>();
        foreach (var (at, block) in blocks)
        {
            var type = Str(block["type"]);
            if (type == "fallback" || hasBoundary && at < boundary && type != "text")
                continue;
            if (type == "tool_use")
            {
                calls.Add(new ToolCall
                {
                    Id = Str(block["id"]) ?? $"toolu_{Guid.NewGuid():N}"[..20],
                    Name = Str(block["name"]) ?? "",
                    Arguments = texts.TryGetValue(at, out var raw) && raw.Length > 0 ? raw.ToString() : (block["input"] ?? new JsonObject()).ToJsonString(),
                });
                if (block["input"] is null)
                    block["input"] = Input(calls[^1].Arguments);
            }
            if (type is "text" or "thinking" or "redacted_thinking" or "tool_use")
                kept.Add((JsonNode)block);
        }

        return new ChatMessage
        {
            Role = ChatRole.Assistant,
            Content = content.ToString(),
            Reasoning = reasoning.Length > 0 ? reasoning.ToString() : null,
            ToolCalls = calls.Count > 0 ? calls : null,
            FinishReason = stopReason switch
            {
                "tool_use" => "tool_calls",
                "max_tokens" or "model_context_window_exceeded" => "length",
                _ => "stop",
            },
            PromptTokens = input is null && cacheWrite is null && cacheRead is null ? null : (input ?? 0) + (cacheWrite ?? 0) + (cacheRead ?? 0),
            CompletionTokens = output,
            AnthropicContent = kept,
        };

        void Write(int at, string text)
        {
            // Text after a call or thinking starts a paragraph of its own.
            if (content.Length > 0 && lastWritten >= 0 && lastWritten != at && blocks.Keys.Any(k => k > lastWritten && k < at && Str(blocks[k]["type"]) != "text"))
            {
                content.Append("\n\n");
                onDelta?.Invoke(new StreamDelta(StreamChannel.Content, "\n\n"));
            }
            lastWritten = at;
            content.Append(text);
            onDelta?.Invoke(new StreamDelta(StreamChannel.Content, text));
        }

        // The prompt's tokens come in three parts: read anew, written to the cache, read from it.
        void Usage(JsonNode? usage)
        {
            if (usage is not JsonObject u)
                return;
            input = Int(u["input_tokens"]) ?? input;
            cacheWrite = Int(u["cache_creation_input_tokens"]) ?? cacheWrite;
            cacheRead = Int(u["cache_read_input_tokens"]) ?? cacheRead;
            output = Int(u["output_tokens"]) ?? output;
        }
    }

    /// <summary>An error event in the stream: an overload or server error is sent again; a rate limit waited out.</summary>
    private static LlmException StreamError(JsonObject? error, bool received, string? callInProgress)
    {
        var type = Str(error?["type"]) ?? "";
        var message = Str(error?["message"]) ?? type;
        if (type == "rate_limit_error")
            return new RateLimitedException("Server error: " + message, null);
        if (OpenAiChatClient.LooksLikeContextOverflow(message))
            return new ContextOverflowException(message);
        if (type is "overloaded_error" or "api_error" or "timeout_error" || OpenAiChatClient.Transient(null, message))
            return new ConnectionLostException("The server broke off its answer: " + message, received, toolCallInProgress: callInProgress) { ServerFailed = type == "overloaded_error" };
        return new LlmException("Server error: " + message);
    }

    /// <summary>What a refusal says: Claude's safety classifiers declined the request (cyber, bio, ...), or Claude itself did.</summary>
    internal static string RefusalText(JsonNode? details)
    {
        var category = Str(details?["category"]);
        var explanation = Str(details?["explanation"]);
        var why = category is { Length: > 0 } ? $" (Anthropic's safety checks put it under \"{category}\")" : "";
        var said = explanation is { Length: > 0 } ? $": {explanation.Trim().TrimEnd('.')}." : ".";
        return $"Claude declined this request{why}{said} Rewording it, or another model at the top, may get it done.";
    }

    /// <summary>
    /// A model of Anthropic's /v1/models as an OpenAI-style list entry, in the fields Sloperator reads
    /// from OpenRouter-style lists: its context (<c>context_length</c>, the most it takes), whether
    /// it takes pictures and calls tools. The entry as Anthropic gave it stays under "anthropic".
    /// </summary>
    internal static JsonObject ModelEntry(JsonObject model)
    {
        var claude = ClaudeModel.FromEntry(model);
        var entry = new JsonObject
        {
            ["id"] = model["id"]?.DeepClone(),
            ["object"] = "model",
            ["owned_by"] = "anthropic",
            ["name"] = model["display_name"]?.DeepClone(),
            ["architecture"] = new JsonObject
            {
                ["input_modalities"] = claude.Vision ? new JsonArray("text", "image") : new JsonArray("text"),
                ["output_modalities"] = new JsonArray("text"),
            },
            ["supported_parameters"] = new JsonArray("tools", "max_tokens"),
            ["anthropic"] = model.DeepClone(),
        };
        if (Int(model["max_input_tokens"]) is { } context)
            entry["context_length"] = context;
        if (Int(model["max_tokens"]) is { } most)
            entry["top_provider"] = new JsonObject { ["max_completion_tokens"] = most };
        return entry;
    }

    /// <summary>
    /// The model to use on Anthropic's API while none is picked: Claude Opus 5.5, Anthropic's
    /// recommended default; without it the newest Opus, then Sonnet, then Haiku the key may use
    /// (the list comes newest first). Fable and Mythos only when picked: they cost more than twice as much.
    /// </summary>
    internal static string? Pick(IReadOnlyList<string> offered)
    {
        if (offered.Contains(DefaultModel))
            return DefaultModel;
        foreach (var family in new[] { "opus", "sonnet", "haiku" })
            if (offered.FirstOrDefault(id => ClaudeModel.Parse(id).Family == family) is { } newest)
                return newest;
        return offered.FirstOrDefault();
    }

    public const string DefaultModel = "claude-opus-5-5";

    private static string? Str(JsonNode? node) => node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static int? Int(JsonNode? node)
    {
        if (node is not JsonValue v) return null;
        if (v.TryGetValue<int>(out var i)) return i;
        if (v.TryGetValue<long>(out var l)) return (int)Math.Min(l, int.MaxValue);
        return null;
    }
}

/// <summary>
/// Pictures made to fit what Claude takes: JPEG, PNG, GIF or WebP, at most 8000 px a side (2000 when
/// a request has more than 20), at most 5 MB as base64. The same picture always comes out the same,
/// so the requests after it begin the same way and Claude's cache holds.
/// </summary>
internal static class AnthropicImages
{
    private const long MostBase64 = 5 * 1024 * 1024;

    private static readonly ConditionalWeakTable<byte[], Dictionary<int, (byte[] Bytes, string Mime)>> Made = new();

    public static (byte[] Bytes, string Mime) Fit(byte[] bytes, string mime, int maxSide)
    {
        var sizes = Made.GetOrCreateValue(bytes);
        lock (sizes)
        {
            if (!sizes.TryGetValue(maxSide, out var made))
                sizes[maxSide] = made = Make(bytes, mime, maxSide);
            return made;
        }
    }

    private static (byte[] Bytes, string Mime) Make(byte[] bytes, string mime, int maxSide)
    {
        var supported = mime is "image/jpeg" or "image/png" or "image/gif" or "image/webp";
        using var codec = SkiaSharp.SKCodec.Create(new SkiaSharp.SKMemoryStream(bytes));
        if (codec is null)
            return (bytes, supported ? mime : "image/png");
        var (width, height) = (codec.Info.Width, codec.Info.Height);
        if (supported && Math.Max(width, height) <= maxSide && (bytes.Length + 2) / 3 * 4L <= MostBase64)
            return (bytes, mime);

        using var bitmap = SkiaSharp.SKBitmap.Decode(bytes);
        if (bitmap is null)
            return (bytes, mime);
        for (var side = Math.Min(maxSide, Math.Max(width, height)); ; side = side * 3 / 4)
        {
            var scale = Math.Min(1.0, (double)side / Math.Max(width, height));
            var info = new SkiaSharp.SKImageInfo(Math.Max(1, (int)Math.Round(width * scale)), Math.Max(1, (int)Math.Round(height * scale)));
            using var resized = scale < 1 ? bitmap.Resize(info, Media.Sampling.High(width, info.Width)) : null;
            using var image = SkiaSharp.SKImage.FromBitmap(resized ?? bitmap);
            using var data = image.Encode(SkiaSharp.SKEncodedImageFormat.Jpeg, 90);
            var jpeg = data.ToArray();
            if ((jpeg.Length + 2) / 3 * 4L <= MostBase64 || side < 256)
                return (jpeg, "image/jpeg");
        }
    }
}
