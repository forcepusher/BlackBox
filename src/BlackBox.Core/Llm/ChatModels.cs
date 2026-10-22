using System.Text.Json.Nodes;

namespace BlackBox.Core.Llm;

public static class ChatRole
{
    public const string System = "system";
    public const string User = "user";
    public const string Assistant = "assistant";
    public const string Tool = "tool";
}

/// <summary>A piece of multimodal message content (text or an inline image).</summary>
public sealed record ContentPart(string? Text = null, byte[]? ImageBytes = null, string ImageMime = "image/jpeg")
{
    public bool IsImage => ImageBytes is not null;

    public static ContentPart FromText(string text) => new(Text: text);

    public static ContentPart FromImage(byte[] bytes, string mime) => new(ImageBytes: bytes, ImageMime: mime);
}

public sealed class ToolCall
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Arguments { get; set; } = "";
}

public sealed class ChatMessage
{
    public string Role { get; set; } = ChatRole.User;

    /// <summary>Plain text content. Ignored when <see cref="Parts"/> is set.</summary>
    public string? Content { get; set; }

    /// <summary>Multimodal content; used for messages that carry screenshots.</summary>
    public List<ContentPart>? Parts { get; set; }

    /// <summary>Model "thinking" text; sent back as reasoning_content when <see cref="ChatRequestOptions.SendReasoning"/> is on.</summary>
    public string? Reasoning { get; set; }

    public List<ToolCall>? ToolCalls { get; set; }

    public string? ToolCallId { get; set; }

    /// <summary>Typed by the user, as opposed to BlackBox's notes, nudges and screenshots. Kept verbatim when the context is reset.</summary>
    public bool FromUser { get; set; }

    /// <summary>For replies: the prompt and reply size the server reported, when it reports usage.</summary>
    public int? PromptTokens { get; set; }
    public int? CompletionTokens { get; set; }

    /// <summary>For replies: why the server stopped ("stop", "tool_calls", "length" when out of room).</summary>
    public string? FinishReason { get; set; }

    /// <summary>
    /// For replies from Anthropic's Messages API: the reply's content blocks as they came, its
    /// thinking (with the signature Claude checks) among them, sent back as they are (see
    /// <see cref="AnthropicMessages"/>). Not saved with the chat: a saved chat leaves its screenshots
    /// out, and thinking sent back after an earlier message changed is refused.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public JsonArray? AnthropicContent { get; set; }

    public bool HasToolCalls => ToolCalls is { Count: > 0 };

    public static ChatMessage System(string text) => new() { Role = ChatRole.System, Content = text };

    public static ChatMessage User(string text) => new() { Role = ChatRole.User, Content = text };

    public static ChatMessage ToolResult(string toolCallId, string text) =>
        new() { Role = ChatRole.Tool, ToolCallId = toolCallId, Content = text };
}

public sealed class ToolDefinition
{
    public required string Name { get; init; }
    public required string Description { get; init; }
    public required JsonObject Parameters { get; init; }
}

public sealed class ChatRequestOptions
{
    public string Model { get; set; } = "local";
    public int? MaxTokens { get; set; }

    /// <summary>Extra top-level request fields (e.g. chat_template_kwargs).</summary>
    public JsonObject? Extra { get; set; }

    /// <summary>Send earlier reasoning back as reasoning_content so thinking models keep their train of thought.</summary>
    public bool SendReasoning { get; set; }

    /// <summary>
    /// The answer is wanted soon, with little thinking first (a click's fresh look). Claude, which
    /// always thinks, is asked for low effort; for Qwen the caller begins the reply itself.
    /// </summary>
    public bool QuickAnswer { get; set; }

    /// <summary>
    /// How hard the model is asked to think ("low" to "max", see <see cref="Settings.Efforts"/>), for a
    /// model that takes it; null sends nothing and the model thinks as it does by default.
    /// </summary>
    public string? Effort { get; set; }
}

public enum StreamChannel { Content, Reasoning }

public readonly record struct StreamDelta(StreamChannel Channel, string Text);
