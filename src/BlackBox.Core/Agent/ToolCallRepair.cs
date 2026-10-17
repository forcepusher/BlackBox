using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using BlackBox.Core.Tools;

namespace BlackBox.Core.Agent;

/// <summary>
/// Fixes the tool calls that heavily quantized models get slightly wrong, so a near miss runs
/// instead of costing a whole round trip: sloppy JSON, misspelled tool names and arguments
/// under the wrong key.
/// </summary>
public static partial class ToolCallRepair
{
    private static readonly Dictionary<string, string> NameAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["click"] = "mouse_click", ["left_click"] = "mouse_click", ["double_click"] = "mouse_click",
        ["right_click"] = "mouse_click", ["mouse_double_click"] = "mouse_click", ["ctrl_click"] = "mouse_click", ["shift_click"] = "mouse_click",
        ["hold_key"] = "press_keys", ["hold_keys"] = "press_keys", ["key_hold"] = "press_keys",
        ["move_mouse"] = "mouse_move", ["hover"] = "mouse_move", ["mouse_hover"] = "mouse_move",
        ["drag"] = "mouse_drag", ["left_click_drag"] = "mouse_drag",
        ["type"] = "type_text", ["typing"] = "type_text", ["input_text"] = "type_text", ["write_text"] = "type_text",
        ["key"] = "press_keys", ["keys"] = "press_keys", ["press_key"] = "press_keys", ["hotkey"] = "press_keys", ["keyboard"] = "press_keys",
        ["take_screenshot"] = "screenshot", ["capture_screen"] = "screenshot", ["screen_capture"] = "screenshot",
        ["sleep"] = "wait",
        ["shell"] = "run_shell", ["bash"] = "run_shell", ["powershell"] = "run_shell", ["run_command"] = "run_shell",
        ["execute_command"] = "run_shell", ["terminal"] = "run_shell", ["cmd"] = "run_shell",
        ["read"] = "read_file", ["cat"] = "read_file", ["open_file"] = "read_file",
        ["write"] = "write_file", ["create_file"] = "write_file", ["save_file"] = "write_file",
        ["edit"] = "edit_file", ["replace_in_file"] = "edit_file", ["str_replace"] = "edit_file",
        ["ls"] = "list_directory", ["list_files"] = "list_directory", ["list_dir"] = "list_directory", ["dir"] = "list_directory",
        ["scp"] = "copy", ["copy_file"] = "copy", ["copy_files"] = "copy", ["upload"] = "copy", ["download"] = "copy",
        ["ssh_run"] = "ssh", ["ssh_command"] = "ssh", ["remote_shell"] = "ssh", ["run_ssh"] = "ssh",
    };

    // Argument names models borrow from other agent frameworks, per canonical parameter.
    private static readonly Dictionary<string, string[]> ArgAliases = new()
    {
        ["text"] = ["content", "value", "string", "input", "message"],
        ["keys"] = ["key", "combo", "shortcut", "hotkey", "keys_combo"],
        ["command"] = ["cmd", "script", "shell_command", "code"],
        ["path"] = ["file", "file_path", "filepath", "filename", "directory", "dir", "folder"],
        ["content"] = ["text", "data", "contents", "body"],
        ["old_text"] = ["old", "old_str", "old_string", "search", "find"],
        ["new_text"] = ["new", "new_str", "new_string", "replace", "replacement"],
        ["seconds"] = ["duration", "time", "secs", "delay"],
        ["direction"] = ["dir", "scroll_direction"],
        ["amount"] = ["clicks", "scroll_amount", "steps", "distance"],
        ["modifiers"] = ["modifier", "keys", "hold_keys", "holding", "with_keys"],
        ["hold_seconds"] = ["hold", "hold_time", "hold_duration", "duration", "seconds"],
        ["until_change"] = ["until_changed", "wait_for_change", "on_change"],
        ["from"] = ["source", "src", "local_path", "remote_path"],
        ["to"] = ["destination", "dest", "target_path"],
        ["host"] = ["server", "hostname", "ssh_host"],
    };

    /// <summary>Maps a tool name the model produced to a registered tool, or returns null.</summary>
    public static Tool? ResolveTool(string name, ToolRegistry registry)
    {
        if (registry.Find(name) is { } exact)
            return exact;
        var normalized = Regex.Replace(name.Trim(), @"^(functions|tools|default_api)[.:]", "", RegexOptions.IgnoreCase)
            .Replace('-', '_').Replace(' ', '_').Replace('.', '_').ToLowerInvariant();
        if (registry.Find(normalized) is { } n)
            return n;
        if (NameAliases.TryGetValue(normalized, out var alias) && registry.Find(alias) is { } aliased)
            return aliased;
        // An MCP tool called without its server prefix.
        return registry.FindBySuffix(normalized);
    }

    /// <summary>
    /// Keeps the intent of aliased names like "double_click" or "ctrl_click" that map onto a more
    /// general tool. Only parameters the tool has are filled in, so an MCP tool whose name happens
    /// to contain "double" gets no clicks argument.
    /// </summary>
    public static void ApplyNameHints(string calledName, JsonObject args, Tool tool)
    {
        if (tool.Parameters["properties"] is not JsonObject props)
            return;
        var n = calledName.Trim().ToLowerInvariant().Replace('-', '_');
        if (n.Contains("double")) Hint("clicks", 2);
        if (n.Contains("triple")) Hint("clicks", 3);
        if (!n.Contains("click"))
            return;
        if (n.Contains("right")) Hint("button", "right");
        if (n.Contains("middle")) Hint("button", "middle");
        if (new[] { "ctrl", "shift", "alt" }.Where(k => n.Contains(k)).ToArray() is { Length: > 0 } held)
            Hint("modifiers", new JsonArray(held.Select(k => (JsonNode)k).ToArray()));

        void Hint(string key, JsonNode value)
        {
            if (props.ContainsKey(key) && !args.ContainsKey(key))
                args[key] = value;
        }
    }

    /// <summary>Parses tool arguments, repairing the usual JSON mistakes if strict parsing fails.</summary>
    public static JsonObject ParseArguments(string raw)
    {
        raw = raw.Trim();
        if (raw.Length == 0)
            return [];
        if (TryParseObject(raw) is { } strict)
            return strict;

        var text = StripWrappers(raw);
        foreach (var candidate in Candidates(text))
        {
            if (TryParseObject(candidate) is { } obj)
                return obj;
        }
        throw new ToolArgumentException("Tool arguments are not valid JSON. Send a single JSON object like {\"x\": 100, \"y\": 200}.");
    }

    /// <summary>
    /// Moves arguments that arrived under a different name to the name the tool expects,
    /// and unpacks coordinate pairs such as "coordinate": [x, y].
    /// </summary>
    public static JsonObject NormalizeArguments(JsonObject args, Tool tool)
    {
        if (tool.Parameters["properties"] is not JsonObject props)
            return args;
        var expected = props.Select(p => p.Key).ToHashSet(StringComparer.Ordinal);

        // Case differences ("X", "Path").
        foreach (var key in args.Select(a => a.Key).ToList())
        {
            if (expected.Contains(key)) continue;
            var match = expected.FirstOrDefault(e => string.Equals(e, key, StringComparison.OrdinalIgnoreCase));
            if (match is not null && !args.ContainsKey(match))
                Move(args, key, match);
        }

        // Coordinate pairs in all the shapes models like to send.
        if (expected.Contains("x") && !args.ContainsKey("x"))
            UnpackPoint(args, ["coordinate", "coordinates", "coord", "position", "point", "pos", "location", "start", "from", "start_coordinate"], "x", "y");
        if (expected.Contains("to_x") && !args.ContainsKey("to_x"))
            UnpackPoint(args, ["to", "end", "end_coordinate", "target", "destination", "coordinate2"], "to_x", "to_y");

        foreach (var (canonical, aliases) in ArgAliases)
        {
            if (!expected.Contains(canonical) || args.ContainsKey(canonical)) continue;
            var found = aliases.FirstOrDefault(a => args.ContainsKey(a) && !expected.Contains(a));
            if (found is not null)
                Move(args, found, canonical);
        }

        // Last resort: one required argument missing and exactly one unknown argument present.
        var required = (tool.Parameters["required"] as JsonArray)?.Select(r => r!.GetValue<string>()).ToList() ?? [];
        var missing = required.Where(r => !args.ContainsKey(r)).ToList();
        var unknown = args.Select(a => a.Key).Where(k => !expected.Contains(k)).ToList();
        if (missing.Count == 1 && unknown.Count == 1)
            Move(args, unknown[0], missing[0]);

        return args;
    }

    private static void UnpackPoint(JsonObject args, string[] keys, string xKey, string yKey)
    {
        foreach (var key in keys)
        {
            switch (args[key])
            {
                case JsonArray { Count: >= 2 } arr:
                    args[xKey] = arr[0]?.DeepClone();
                    args[yKey] = arr[1]?.DeepClone();
                    args.Remove(key);
                    return;
                case JsonObject o when o["x"] is not null && o["y"] is not null:
                    args[xKey] = o["x"]!.DeepClone();
                    args[yKey] = o["y"]!.DeepClone();
                    args.Remove(key);
                    return;
                case JsonValue v when v.TryGetValue<string>(out var s) && PointText().Match(s) is { Success: true } m:
                    args[xKey] = double.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
                    args[yKey] = double.Parse(m.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture);
                    args.Remove(key);
                    return;
            }
        }
    }

    private static void Move(JsonObject args, string from, string to)
    {
        var value = args[from]?.DeepClone();
        args.Remove(from);
        args[to] = value;
    }

    private static JsonObject? TryParseObject(string text)
    {
        try
        {
            var node = JsonNode.Parse(text, documentOptions: new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            return node switch
            {
                JsonObject o => o,
                // Arguments double-encoded as a JSON string: "{\"x\": 1}"
                JsonValue v when v.TryGetValue<string>(out var inner) && inner.TrimStart().StartsWith('{') => TryParseObject(inner),
                _ => null,
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Whether tool arguments are a whole JSON object as they came (or nothing), with no repair needed.</summary>
    public static bool IsWholeJson(string raw) => raw.Trim() is var t && (t.Length == 0 || TryParseObject(t) is not null);

    private static string StripWrappers(string text)
    {
        // Only a fence around the whole of it: one inside a value (a Markdown file's ```python
        // block, written with write_file) was taken out of the file.
        text = text.Trim();
        if (text.StartsWith("```", StringComparison.Ordinal) && text.IndexOf('\n') is var open and > 0 && text.LastIndexOf("```", StringComparison.Ordinal) is var close and > 0 && close > open)
            text = text[(open + 1)..close].Trim();
        text = text.Replace("<tool_call>", "").Replace("</tool_call>", "").Trim();
        var start = text.IndexOf('{');
        return start > 0 ? text[start..] : text;
    }

    private static IEnumerable<string> Candidates(string text)
    {
        // Python-style literals and single quotes.
        var pythonish = PythonLiterals(text);
        yield return pythonish;
        if (!pythonish.Contains('"'))
            yield return pythonish.Replace('\'', '"');

        // Unquoted keys: {x: 10, y: 20}
        var quotedKeys = QuoteBareKeys(pythonish);
        yield return quotedKeys;

        // Truncated output: close whatever is still open.
        yield return Balance(quotedKeys);
        // Extra text after the object.
        yield return FirstObject(quotedKeys);
    }

    /// <summary>
    /// Python's True, False and None as JSON's, outside strings only: replaced everywhere, a script
    /// written with write_file got "return null" for "return None" whenever its call needed a repair.
    /// </summary>
    internal static string PythonLiterals(string text)
    {
        var sb = new StringBuilder(text.Length);
        var inString = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (inString)
            {
                sb.Append(c);
                if (c == '\\' && i + 1 < text.Length) sb.Append(text[++i]);
                else if (c == '"') inString = false;
                continue;
            }
            if (c == '"')
            {
                inString = true;
                sb.Append(c);
                continue;
            }
            if (char.IsLetter(c) && (i == 0 || !char.IsLetterOrDigit(text[i - 1]) && text[i - 1] != '_'))
            {
                var end = i;
                while (end < text.Length && (char.IsLetterOrDigit(text[end]) || text[end] == '_')) end++;
                var word = text[i..end];
                sb.Append(word switch { "True" => "true", "False" => "false", "None" => "null", _ => word });
                i = end - 1;
                continue;
            }
            sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>Quotes bare object keys ({x: 1} to {"x": 1}) without touching text inside strings.</summary>
    internal static string QuoteBareKeys(string text)
    {
        var sb = new StringBuilder(text.Length + 16);
        var inString = false;
        var expectKey = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (inString)
            {
                sb.Append(c);
                if (c == '\\' && i + 1 < text.Length) sb.Append(text[++i]);
                else if (c == '"') inString = false;
                continue;
            }
            if (c == '"') { inString = true; expectKey = false; sb.Append(c); continue; }
            if (c is '{' or ',') { expectKey = true; sb.Append(c); continue; }
            if (expectKey && (char.IsLetter(c) || c == '_'))
            {
                var start = i;
                while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] == '_')) i++;
                var j = i;
                while (j < text.Length && char.IsWhiteSpace(text[j])) j++;
                var word = text[start..i];
                if (j < text.Length && text[j] == ':') sb.Append('"').Append(word).Append('"');
                else sb.Append(word);
                i--;
                expectKey = false;
                continue;
            }
            if (!char.IsWhiteSpace(c)) expectKey = false;
            sb.Append(c);
        }
        return sb.ToString();
    }

    private static string Balance(string text)
    {
        var stack = new Stack<char>();
        var inString = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (inString)
            {
                if (c == '\\') i++;
                else if (c == '"') inString = false;
                continue;
            }
            switch (c)
            {
                case '"': inString = true; break;
                case '{': stack.Push('}'); break;
                case '[': stack.Push(']'); break;
                case '}' or ']' when stack.Count > 0: stack.Pop(); break;
            }
        }
        var sb = new StringBuilder(text.TrimEnd().TrimEnd(','));
        if (inString) sb.Append('"');
        while (stack.Count > 0) sb.Append(stack.Pop());
        return sb.ToString();
    }

    private static string FirstObject(string text)
    {
        var depth = 0;
        var inString = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (inString)
            {
                if (c == '\\') i++;
                else if (c == '"') inString = false;
                continue;
            }
            if (c == '"') inString = true;
            else if (c == '{') depth++;
            else if (c == '}' && --depth == 0) return text[..(i + 1)];
        }
        return text;
    }

    [GeneratedRegex(@"^\s*\(?\s*(-?\d+(?:\.\d+)?)\s*[, ]\s*(-?\d+(?:\.\d+)?)\s*\)?\s*$")]
    private static partial Regex PointText();
}
