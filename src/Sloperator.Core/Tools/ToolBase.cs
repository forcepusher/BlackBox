using System.Text.Json;
using System.Text.Json.Nodes;
using Sloperator.Core.Platform;
using Sloperator.Core.Settings;

namespace Sloperator.Core.Tools;

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

    /// <summary>The app's top bar, for model_server to switch the server, key and model Sloperator runs on; null where there is none (the CLI).</summary>
    public IModelServerSwitch? ModelServer { get; init; }

    /// <summary>
    /// The longest side a screenshot may have for this model, whatever the setting: Claude makes
    /// larger ones smaller itself (and refuses them over 2000 px in a request with more than 20
    /// pictures), and then points on the smaller picture, so a click would miss. Null for no limit.
    /// </summary>
    public int? MaxScreenshotSide { get; init; }

    /// <summary>Whether the model takes images, so read_file can show it a picture. Turned off mid-run when its server refuses one.</summary>
    public bool SeesImages { get; set; } = true;

    /// <summary>Cancelled when the user sends a message during the run: a wait ends early so the model reads it soon.</summary>
    public CancellationToken UserMessage { get; set; }

    /// <summary>
    /// True while the user is typing a message to the agent in Sloperator's own window: clicks and
    /// key presses wait, or they would take the focus from under the user's typing.
    /// </summary>
    public Func<bool>? UserComposing { get; init; }

    /// <summary>
    /// The monitor the agent is looking at (0 = primary): the one the latest screenshot showed.
    /// Screenshot coordinates are relative to it. Set by the screenshot tool.
    /// </summary>
    public int CurrentScreen { get; set; }

    /// <summary>
    /// The screen as the model last saw it: the agent sets it before each reply's tool calls, and
    /// the first action of the reply clears it, as the screen then changes in ways the model hasn't
    /// seen yet. A wait for a change compares it with the screen now (see <see cref="WaitTool"/>).
    /// </summary>
    public (ScreenRect Screen, RawImage Frame)? Seen { get; set; }

    /// <summary>
    /// An action of the model's reply was held back, as another window's agent had used the mouse
    /// and keyboard since the model last saw the screen: the reply's later actions were decided on
    /// that same screen, and would type into whatever the other agent left in front. Cleared by the
    /// agent before each reply's tool calls.
    /// </summary>
    public bool ScreenStale { get; set; }

    /// <summary>
    /// Asks the model about a picture in a conversation of its own, without the run's history:
    /// a fresh look, as at a clip's frames to judge it (see <see cref="VideoGeneratorTool"/>). The answer, or
    /// null when it couldn't be had. Set by the agent session; null when the model can't see.
    /// </summary>
    public Func<IReadOnlyList<byte[]>, string, CancellationToken, Task<string?>>? Ask { get; set; }

    /// <summary>
    /// Whether the conversation still holds this text: a guide a tool showed earlier in the chat
    /// isn't shown again until a context reset drops it. Set by the agent session; null outside one.
    /// </summary>
    public Func<string, bool>? InConversation { get; set; }

    /// <summary>How many times the agent has started the context afresh with a handoff note in this run: what a tool showed before is gone.</summary>
    public int ContextResets { get; set; }

    /// <summary>
    /// Has the context reset with a handoff note before the model's next step (reset_context):
    /// false when it was just reset and nothing has happened since. Set by the agent session.
    /// </summary>
    public Func<bool>? ResetContext { get; set; }

    /// <summary>Shows the user the app's offer to install text recognition (see <see cref="Agent.IAgentObserver.OfferTextRecognition"/>); null where there is no app (the CLI).</summary>
    public Action? OfferTextRecognition { get; set; }

    /// <summary>The latest screenshot taken for the model (screen and frame).</summary>
    public (ScreenRect Screen, RawImage Frame)? LastShot { get; set; }

    /// <summary>Whose turn it is with the mouse and keyboard among Sloperator's windows; null where only one agent runs (the command line, tests).</summary>
    public Platform.DesktopTurn? DesktopTurn { get; init; }

    /// <summary>
    /// The window the agent works in: what its runs saw of the screen counts from one run to the
    /// next, as the chat keeps its last screenshot. Null: this run alone (see <see cref="Platform.DesktopTurn"/>).
    /// </summary>
    public object? DesktopSeat { get; init; }

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

    /// <summary>Sloperator's Data/tools, where portable helpers it sets up for a job live (the song judge); null when there is none.</summary>
    public string? ToolsDirectory { get; init; }

    /// <summary>Where videos go, a folder each (Sloperator's Data/videos, where the Video generator panel finds them); null when there is none.</summary>
    public string? VideosDirectory { get; init; }

    /// <summary>Where folder indexes are kept (Sloperator's Data/index, see <see cref="IndexFolderTool"/>); null to keep them in memory only.</summary>
    public string? IndexDirectory { get; init; }

    /// <summary>Where helper files for a task go (Sloperator's Data/scratch); null to use the system's temp folder.</summary>
    public string? ScratchDirectory { get; init; }

    /// <summary>
    /// The chat's own folder, beside its saved pictures (Data/chats/&lt;id&gt;): what capture_result
    /// takes goes there, so the chat shows it when opened again and deletes it with itself. Null
    /// where there is no saved chat (the command line).
    /// </summary>
    public string? ChatDirectory { get; init; }

    /// <summary>The files the agent changes in this chat, for the File changes panel; null when nobody shows them.</summary>
    public Diffs.FileChanges? Changes { get; init; }

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
        return new(screens[Math.Clamp(screen, 0, screens.Count - 1)], ScreenshotBox());
    }

    /// <summary>One <see cref="ScreenSpace"/> per monitor, in <see cref="Screens"/> order.</summary>
    public IReadOnlyList<ScreenSpace> ScreenSpaces() =>
        Screens.Select(s => new ScreenSpace(s, ScreenshotBox())).ToList();

    /// <summary>The box screenshots are made to fit: the setting's, within <see cref="MaxScreenshotSide"/>.</summary>
    private (int Width, int Height) ScreenshotBox()
    {
        var box = Settings.ScreenshotBox;
        return MaxScreenshotSide is { } most ? (Math.Min(box.Width, most), Math.Min(box.Height, most)) : box;
    }

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
