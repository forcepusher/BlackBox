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

    /// <summary>The same options with a reply length limit; the limit is not part of the prompt, so the server's cache still matches.</summary>
    public ChatRequestOptions WithMaxTokens(int maxTokens) =>
        new() { Model = Model, MaxTokens = maxTokens, Extra = Extra, SendReasoning = SendReasoning };
}

public enum StreamChannel { Content, Reasoning }

public readonly record struct StreamDelta(StreamChannel Channel, string Text);
