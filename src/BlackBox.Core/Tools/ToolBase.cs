using System.Text.Json;
using System.Text.Json.Nodes;
using BlackBox.Core.Platform;
using BlackBox.Core.Settings;

namespace BlackBox.Core.Tools;

public enum ToolRisk
{
    /// <summary>Only reads state (screenshots, file reads).</summary>
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
    /// <summary>Where relative paths and shell commands start (the user's home folder). Not a limit: tools reach the whole PC.</summary>
    public required string WorkingDirectory { get; init; }

    /// <summary>The relative grid the model gives x/y on (see <see cref="ScreenSpace"/>), or null for screenshot pixels. Set from the model, see <see cref="ModelCoordinates"/>.</summary>
    public int? CoordinateGrid { get; init; }

    /// <summary>
    /// The monitor the agent is looking at (0 = primary): the one the latest screenshot showed.
    /// Screenshot coordinates are relative to it. Set by the screenshot tool.
    /// </summary>
    public int CurrentScreen { get; set; }

    /// <summary>
    /// Finds a named thing on a screenshot (JPEG) in a fresh conversation with the model; returns its
    /// centre in the model's coordinates, or null. Set by the agent session; mouse_click aims with it
    /// (see <see cref="Agent.FreshLook"/>).
    /// </summary>
    public Func<byte[], string, CancellationToken, Task<(double X, double Y)?>>? Locate { get; set; }

    /// <summary>The PC's own browser, run headless, for the web tools to fall back on; null when there is none.</summary>
    public Web.HeadlessBrowser? Browser { get; init; }

    /// <summary>The servers this chat is logged in to over SSH (the ssh tool, and the file tools with host); null when SSH is off.</summary>
    public Ssh.SshHosts? Ssh { get; init; }

    /// <summary>All monitors in physical pixels, the primary first.</summary>
    public IReadOnlyList<ScreenRect> Screens => Platform.Screen.GetScreens();

    public ScreenSpace GetScreenSpace() => GetScreenSpace(CurrentScreen);

    public ScreenSpace GetScreenSpace(int screen)
    {
        var screens = Screens;
        return new(screens[Math.Clamp(screen, 0, screens.Count - 1)], Settings.ScreenshotResolution.Box(), CoordinateGrid);
    }

    /// <summary>One <see cref="ScreenSpace"/> per monitor, in <see cref="Screens"/> order.</summary>
    public IReadOnlyList<ScreenSpace> ScreenSpaces() =>
        Screens.Select(s => new ScreenSpace(s, Settings.ScreenshotResolution.Box(), CoordinateGrid)).ToList();

    /// <summary>The monitor a physical point lies on; the current one if none contains it.</summary>
    public int ScreenAt(int x, int y)
    {
        var screens = Screens;
        for (var i = 0; i < screens.Count; i++)
            if (screens[i].Contains(x, y))
                return i;
        return CurrentScreen;
    }

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

    protected static JsonObject Bool(string description) => new() { ["type"] = "boolean", ["description"] = description };

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

    /// <summary>True for JSON true and for the strings "true"/"yes"/"1" some models send instead.</summary>
    protected static bool GetBool(JsonObject args, string key) => args[key] is JsonValue v && v.GetValueKind() switch
    {
        JsonValueKind.True => true,
        JsonValueKind.String => v.GetValue<string>().Trim().ToLowerInvariant() is "true" or "yes" or "1",
        JsonValueKind.Number => v.ToJsonString() != "0",
        _ => false,
    };

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
