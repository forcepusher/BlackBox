using System.Text.Json;
using System.Text.Json.Nodes;
using BlackBox.Core.Platform;
using BlackBox.Core.Settings;

namespace BlackBox.Core.Tools;

public enum ToolRisk
{
    /// <summary>Only reads state (screenshots, file reads, window lists).</summary>
    ReadOnly,
    /// <summary>Drives the mouse and keyboard.</summary>
    Desktop,
    /// <summary>Changes files or runs arbitrary commands.</summary>
    Risky,
}

public sealed record ToolResult(string Text, byte[]? Image = null, string ImageMime = "image/jpeg", bool IsError = false)
{
    public static ToolResult Error(string text) => new(text, IsError: true);
}

public sealed class ToolContext
{
    public required IPlatformServices Platform { get; init; }
    public required AgentSettings Settings { get; init; }
    public required string WorkingDirectory { get; init; }

    public ScreenSpace GetScreenSpace() =>
        new(Platform.Screen.GetPrimaryScreen(), Settings.ScreenshotMaxWidth, Settings.ScreenshotMaxHeight);

    public string ResolvePath(string path) =>
        Path.GetFullPath(Environment.ExpandEnvironmentVariables(path), WorkingDirectory);
}

public abstract class Tool
{
    public abstract string Name { get; }
    public abstract string Description { get; }
    public abstract ToolRisk Risk { get; }
    public abstract JsonObject Parameters { get; }

    public abstract Task<ToolResult> ExecuteAsync(JsonObject args, ToolContext ctx, CancellationToken ct);

    /// <summary>Short human-readable summary of a call, for the chat UI and approval prompts.</summary>
    public virtual string Describe(JsonObject args) => $"{Name} {args.ToJsonString()}";

    // --- tiny JSON-schema builders -------------------------------------------------------

    protected static JsonObject Schema(params (string Name, JsonObject Prop, bool Required)[] props)
    {
        var properties = new JsonObject();
        var required = new JsonArray();
        foreach (var (name, prop, req) in props)
        {
            properties[name] = prop;
            if (req) required.Add(name);
        }
        return new JsonObject { ["type"] = "object", ["properties"] = properties, ["required"] = required };
    }

    protected static JsonObject Str(string description, params string[] allowed)
    {
        var o = new JsonObject { ["type"] = "string", ["description"] = description };
        if (allowed.Length > 0)
            o["enum"] = new JsonArray(allowed.Select(a => (JsonNode)a).ToArray());
        return o;
    }

    protected static JsonObject Int(string description) => new() { ["type"] = "integer", ["description"] = description };

    protected static JsonObject Num(string description) => new() { ["type"] = "number", ["description"] = description };

    protected static JsonObject StrArray(string description) =>
        new() { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" }, ["description"] = description };

    // --- argument readers that tolerate what small models actually send ---------------------

    protected static string? GetString(JsonObject args, string key) => args[key] switch
    {
        null => null,
        JsonValue v when v.TryGetValue<string>(out var s) => s,
        JsonNode n => n.ToJsonString(),
    };

    protected static string RequireString(JsonObject args, string key) =>
        GetString(args, key) ?? throw new ToolArgumentException($"Missing required argument '{key}'.");

    protected static double? GetNumber(JsonObject args, string key)
    {
        if (args[key] is not JsonValue v) return null;
        // Covers both parsed JSON and in-memory values of any numeric CLR type; small models
        // also like to send numbers as strings ("120").
        var text = v.GetValueKind() switch
        {
            JsonValueKind.Number => v.ToJsonString(),
            JsonValueKind.String => v.GetValue<string>(),
            _ => null,
        };
        return double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : null;
    }

    protected static double RequireNumber(JsonObject args, string key) =>
        GetNumber(args, key) ?? throw new ToolArgumentException($"Missing or non-numeric argument '{key}'.");

    protected static List<string>? GetStringList(JsonObject args, string key) => args[key] switch
    {
        JsonArray a => a.Select(n => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : n?.ToJsonString() ?? "").ToList(),
        JsonValue v when v.TryGetValue<string>(out var s) => [s],
        _ => null,
    };
}

public sealed class ToolArgumentException(string message) : Exception(message);

public static class ToolArgs
{
    /// <summary>Parses the raw arguments string from a tool call, accepting empty strings as "{}".</summary>
    public static JsonObject Parse(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return [];
        try
        {
            return JsonNode.Parse(raw) as JsonObject ?? throw new ToolArgumentException("Tool arguments must be a JSON object.");
        }
        catch (JsonException ex)
        {
            throw new ToolArgumentException("Tool arguments are not valid JSON: " + ex.Message);
        }
    }
}
