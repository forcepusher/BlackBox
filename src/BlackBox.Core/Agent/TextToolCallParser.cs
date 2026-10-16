using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using BlackBox.Core.Llm;
using BlackBox.Core.Tools;

namespace BlackBox.Core.Agent;

/// <summary>
/// Fallback for when the server returns a tool call as plain text instead of structured tool_calls,
/// which happens when a quantized model slightly breaks the expected format. Understands:
/// <list type="bullet">
/// <item>Qwen 3.5+ XML: <c>&lt;tool_call&gt;&lt;function=name&gt;&lt;parameter=x&gt;1&lt;/parameter&gt;&lt;/function&gt;&lt;/tool_call&gt;</c></item>
/// <item>Hermes JSON: <c>&lt;tool_call&gt;{"name": ..., "arguments": {...}}&lt;/tool_call&gt;</c></item>
/// <item>Gemma 4: <c>&lt;|tool_call&gt;call:name{key:&lt;|"|&gt;text&lt;|"|&gt;,n:1}&lt;tool_call|&gt;</c></item>
/// <item>A bare or fenced JSON object with "name" and "arguments".</item>
/// </list>
/// </summary>
public static partial class TextToolCallParser
{
    public static bool TryExtract(ChatMessage message, ToolRegistry registry)
    {
        if (message.HasToolCalls || string.IsNullOrWhiteSpace(message.Content))
            return false;

        var content = message.Content;
        var calls = new List<ToolCall>();

        foreach (Match m in XmlFunction().Matches(content))
        {
            var args = new JsonObject();
            foreach (Match p in XmlParameter().Matches(m.Groups["body"].Value))
                args[p.Groups["name"].Value.Trim()] = ParseValue(p.Groups["value"].Value);
            Add(m.Groups["name"].Value, args.ToJsonString());
        }

        if (calls.Count == 0)
        {
            foreach (Match m in GemmaCall().Matches(content))
            {
                // Gemma quotes strings with <|"|> and leaves keys bare; turn that into JSON and let
                // the argument repair handle the unquoted keys.
                var json = GemmaString().Replace(m.Groups["args"].Value, s => JsonValue.Create(s.Groups[1].Value)!.ToJsonString());
                try { Add(m.Groups["name"].Value, ToolCallRepair.ParseArguments(json).ToJsonString()); }
                catch (ToolArgumentException) { }
            }
        }

        if (calls.Count == 0)
        {
            foreach (Match m in JsonToolCall().Matches(content))
                AddJson(m.Groups["json"].Value);
        }

        if (calls.Count == 0)
        {
            // Whole reply is a JSON call, possibly in a code fence.
            var trimmed = FenceOnly().Match(content) is { Success: true } f ? f.Groups[1].Value : content.Trim();
            if (trimmed.StartsWith('{') && trimmed.EndsWith('}'))
                AddJson(trimmed);
        }

        if (calls.Count == 0)
            return false;

        message.ToolCalls = calls;
        var cleaned = XmlFunction().Replace(content, "");
        cleaned = JsonToolCall().Replace(cleaned, "");
        cleaned = GemmaCall().Replace(cleaned, "");
        cleaned = cleaned.Replace("<tool_call>", "").Replace("</tool_call>", "").Trim();
        message.Content = cleaned.StartsWith('{') || cleaned.StartsWith("```") ? "" : cleaned;
        return true;

        void Add(string name, string arguments)
        {
            if (ToolCallRepair.ResolveTool(name, registry) is null)
                return;
            calls.Add(new ToolCall { Id = "text_" + Guid.NewGuid().ToString("N")[..12], Name = name.Trim(), Arguments = arguments });
        }

        void AddJson(string json)
        {
            JsonObject obj;
            try { obj = ToolCallRepair.ParseArguments(json); }
            catch (ToolArgumentException) { return; }
            if (obj["name"] is not JsonValue nameValue || !nameValue.TryGetValue<string>(out var name))
                return;
            var args = obj["arguments"] ?? obj["parameters"] ?? obj["args"];
            Add(name, args switch
            {
                null => "{}",
                JsonValue v when v.TryGetValue<string>(out var s) => s,
                _ => args.ToJsonString(),
            });
        }
    }

    private static JsonNode? ParseValue(string raw)
    {
        // Qwen wraps values in newlines; strip exactly that so intentional whitespace survives.
        var value = raw.StartsWith('\n') ? raw[1..] : raw;
        if (value.EndsWith('\n')) value = value[..^1];
        var t = value.Trim();
        if (t.Length > 0 && (char.IsDigit(t[0]) || t[0] is '-' or '[' or '{' || t is "true" or "false" or "null"))
        {
            try { return JsonNode.Parse(t); }
            catch (JsonException) { }
        }
        return JsonValue.Create(value);
    }

    [GeneratedRegex(@"<function=(?<name>[^>\s]+)\s*>(?<body>.*?)(</function>|(?=</tool_call>)|$)", RegexOptions.Singleline)]
    private static partial Regex XmlFunction();

    [GeneratedRegex(@"<parameter=(?<name>[^>]+)>(?<value>.*?)</parameter>", RegexOptions.Singleline)]
    private static partial Regex XmlParameter();

    [GeneratedRegex(@"<\|tool_call>\s*call:(?<name>[\w.\-]+)\s*(?<args>\{.*?\})\s*(<tool_call\|>|$)", RegexOptions.Singleline)]
    private static partial Regex GemmaCall();

    [GeneratedRegex(@"<\|""\|>(.*?)<\|""\|>", RegexOptions.Singleline)]
    private static partial Regex GemmaString();

    [GeneratedRegex(@"<tool_call>\s*(?<json>\{.*?\})\s*(</tool_call>|$)", RegexOptions.Singleline)]
    private static partial Regex JsonToolCall();

    [GeneratedRegex(@"^\s*```(?:json)?\s*(\{.*\})\s*```\s*$", RegexOptions.Singleline)]
    private static partial Regex FenceOnly();
}
