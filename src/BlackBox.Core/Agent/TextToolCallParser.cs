using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using BlackBox.Core.Llm;

namespace BlackBox.Core.Agent;

/// <summary>
/// Fallback for servers or quantized models that print Qwen/Hermes-style tool calls as plain text
/// (<c>&lt;tool_call&gt;{"name":..., "arguments":{...}}&lt;/tool_call&gt;</c>) instead of returning structured tool_calls.
/// </summary>
public static partial class TextToolCallParser
{
    [GeneratedRegex(@"<tool_call>\s*(?<json>\{.*?\})\s*</tool_call>", RegexOptions.Singleline)]
    private static partial Regex ToolCallTag();

    public static bool TryExtract(ChatMessage message, IReadOnlySet<string> knownTools)
    {
        if (message.HasToolCalls || string.IsNullOrEmpty(message.Content))
            return false;

        var calls = new List<ToolCall>();
        foreach (Match m in ToolCallTag().Matches(message.Content))
        {
            if (TryParse(m.Groups["json"].Value, knownTools) is { } call)
                calls.Add(call);
        }
        if (calls.Count == 0)
            return false;

        message.ToolCalls = calls;
        message.Content = ToolCallTag().Replace(message.Content, "").Trim();
        return true;
    }

    private static ToolCall? TryParse(string json, IReadOnlySet<string> knownTools)
    {
        try
        {
            if (JsonNode.Parse(json) is not JsonObject obj)
                return null;
            var name = obj["name"]?.GetValue<string>();
            if (name is null || !knownTools.Contains(name))
                return null;
            var args = obj["arguments"] ?? obj["parameters"];
            return new ToolCall
            {
                Id = "text_" + Guid.NewGuid().ToString("N")[..12],
                Name = name,
                Arguments = args switch
                {
                    null => "{}",
                    JsonValue v when v.TryGetValue<string>(out var s) => s,
                    _ => args.ToJsonString(),
                },
            };
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return null;
        }
    }
}
