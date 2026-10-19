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

/// <param name="ImageCaption">What the image is, said where it reaches the model; null for a screenshot after an action.</param>
/// <param name="Files">Files the call made or showed (a picture, a video, a sound, a 3D model…), previewed for the user in the chat.</param>
public sealed record ToolResult(string Text, byte[]? Image = null, string ImageMime = "image/jpeg", bool IsError = false, string? ImageCaption = null,
    IReadOnlyList<string>? Files = null)
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

    /// <summary>Whether the model takes images, so read_file can show it a picture. Turned off mid-run when its server refuses one.</summary>
    public bool SeesImages { get; set; } = true;

    /// <summary>Cancelled when the user sends a message during the run: a wait ends early so the model reads it soon.</summary>
    public CancellationToken UserMessage { get; set; }

    /// <summary>
    /// True while the user is typing a message to the agent in BlackBox's own window: clicks and
    /// key presses wait, or they would take the focus from under the user's typing.
    /// </summary>
    public Func<bool>? UserComposing { get; init; }

    /// <summary>
    /// The monitor the agent is looking at (0 = primary): the one the latest screenshot showed.
    /// Screenshot coordinates are relative to it. Set by the screenshot tool.
    /// </summary>
    public int CurrentScreen { get; set; }

    /// <summary>
    /// Finds a named thing on a screenshot (JPEG) of the given space (the whole screen, or the part
    /// around a click) in a fresh conversation with the model; returns its centre in the model's
    /// coordinates for that space, or null. Set by the agent session; mouse_click aims with it
    /// (see <see cref="Agent.FreshLook"/>).
    /// </summary>
    public Func<byte[], ScreenSpace, string, CancellationToken, Task<(double X, double Y)?>>? Locate { get; set; }

    /// <summary>
    /// The screen as the model last saw it, which its next click is aimed by: the agent sets it
    /// before each reply's tool calls, and the first action of the reply clears it, as the
    /// screen then changes in ways the model hasn't seen yet. A click compares it with the screen
    /// at the moment of the click (see <see cref="MouseClickTool"/>).
    /// </summary>
    public (ScreenRect Screen, RawImage Frame)? Seen { get; set; }

    /// <summary>
    /// Asks the model about a picture in a conversation of its own, without the run's history:
    /// a fresh look, as at a clip's frames to judge it (see <see cref="FilmTool"/>). The answer, or
    /// null when it couldn't be had. Set by the agent session; null when the model can't see.
    /// </summary>
    public Func<byte[], string, CancellationToken, Task<string?>>? Ask { get; set; }

    /// <summary>The latest screenshot taken for the model (screen and frame).</summary>
    public (ScreenRect Screen, RawImage Frame)? LastShot { get; set; }

    /// <summary>Where the last click that was held back because the screen had changed was aimed (screen pixels).</summary>
    internal (int X, int Y)? HeldBack { get; set; }

    /// <summary>The PC's own browser, run headless, for the web tools to fall back on; null when there is none.</summary>
    public Web.HeadlessBrowser? Browser { get; init; }

    /// <summary>The servers this chat is logged in to over SSH (the ssh tool, and the file tools with host); null when SSH is off.</summary>
    public Ssh.SshHosts? Ssh { get; init; }

    /// <summary>The app's MCP servers, for the agent to set one up itself (mcp_servers); null when it can't.</summary>
    public Mcp.McpSetup? Mcp { get; init; }

    /// <summary>The ComfyUI server the comfyui tool works with; null when it isn't offered.</summary>
    public Comfy.ComfyHub? Comfy { get; init; }

    /// <summary>
    /// The pictures the user's latest message with pictures came with, in this chat: a picture to
    /// follow, which the comfyui tool shows next to what each job makes. Set by the agent session.
    /// </summary>
    public IReadOnlyList<string> UserPictures { get; set; } = [];

    /// <summary>Reads video and sound, so the model can look at them (frames, waveforms); null when nothing can.</summary>
    public Media.IMediaDecoder? Media { get; init; }

    /// <summary>BlackBox's Data/tools, where portable helpers it sets up for a job live (the song judge); null when there is none.</summary>
    public string? ToolsDirectory { get; init; }

    /// <summary>Where films go, a folder each (BlackBox's Data/films, where the Filmmaking window finds them); null when there is none.</summary>
    public string? FilmsDirectory { get; init; }

    /// <summary>Where helper files for a task go (BlackBox's Data/scratch); null to use the system's temp folder.</summary>
    public string? ScratchDirectory { get; init; }

    /// <summary>
    /// Tells the user how a long call is getting on ("KSampler, step 3 of 20 · 1:12"), shown on its
    /// card while it runs. Set by the agent session for each call; null when nobody listens.
    /// </summary>
    public Action<string>? Progress { get; set; }

    private readonly Dictionary<string, Func<Task>> _afterRun = new();

    /// <summary>Something to do once the task is over (each <paramref name="key"/> once): ComfyUI lets go of its models then.</summary>
    public void AfterRun(string key, Func<Task> action)
    {
        lock (_afterRun)
            _afterRun[key] = action;
    }

    /// <summary>Does what <see cref="AfterRun"/> asked for; a failure there is nobody's business but its own.</summary>
    public async Task FinishRunAsync()
    {
        List<Func<Task>> actions;
        lock (_afterRun)
        {
            actions = _afterRun.Values.ToList();
            _afterRun.Clear();
        }
        foreach (var action in actions)
        {
            try { await action().ConfigureAwait(false); }
            catch (Exception) { /* a server that went away meanwhile */ }
        }
    }

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

    public string ResolvePath(string path)
    {
        path = Environment.ExpandEnvironmentVariables(path);
        // "~/Desktop/report.txt" is the home folder's Desktop, as in PowerShell and every shell;
        // it made a folder named "~" in the working directory instead.
        if (path == "~" || path.StartsWith("~/", StringComparison.Ordinal) || path.StartsWith(@"~\", StringComparison.Ordinal))
            path = Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path[1..].TrimStart('/', '\\'));
        return Path.GetFullPath(path, WorkingDirectory);
    }
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
            if (req) required.Add((JsonNode)name);
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
