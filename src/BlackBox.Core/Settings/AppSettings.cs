using System.Text.Json;
using System.Text.Json.Serialization;

namespace BlackBox.Core.Settings;

public enum ApprovalMode
{
    /// <summary>Ask before every tool call.</summary>
    AskAll,
    /// <summary>Ask before shell commands and file changes; screen/mouse/keyboard run freely.</summary>
    AskRisky,
    /// <summary>Never ask.</summary>
    Never,
}

/// <summary>The OpenAI-compatible server the agent talks to (LM Studio, llama-server, vLLM, OpenAI…).</summary>
public sealed class ServerSettings
{
    /// <summary>LM Studio's default local address.</summary>
    public string BaseUrl { get; set; } = "http://localhost:1234/v1";
    public string ApiKey { get; set; } = "";
    public string Model { get; set; } = "";

    /// <summary>Whether the model accepts images. Detected from LM Studio; set by hand for other servers.</summary>
    public bool SupportsVision { get; set; } = true;

    /// <summary>
    /// Context window of the model, as reported by the server when the app connects. Not saved;
    /// servers that don't report it get this conservative default.
    /// </summary>
    [JsonIgnore]
    public int ContextSize { get; set; } = 32768;

    /// <summary>
    /// Upper bound for one request, even when the model is loaded with a bigger context. Every
    /// step resends the conversation, and a 200k-token prompt can take minutes to process on a
    /// single GPU, so keeping it smaller keeps the agent responsive.
    /// </summary>
    public int MaxPromptTokens { get; set; } = 65536;
}

public sealed class AgentSettings
{
    public int MaxSteps { get; set; } = 40;
    /// <summary>Everything runs without asking by default; Stop and the failsafe corner stay available.</summary>
    public ApprovalMode Approval { get; set; } = ApprovalMode.Never;

    /// <summary>Screenshots are downscaled to at most this many pixels before going to the model (about 1.3 MP, roughly 1300 image tokens).</summary>
    public int ScreenshotMaxPixels { get; set; } = 1_300_000;

    /// <summary>Only the newest N screenshots stay in context. Internal tuning, not a user setting.</summary>
    [JsonIgnore]
    public int KeepScreenshots { get; set; } = 2;

    /// <summary>Attach a fresh screenshot after mouse/keyboard actions. Internal (tests turn it off).</summary>
    [JsonIgnore]
    public bool ScreenshotAfterActions { get; set; } = true;

    /// <summary>
    /// Keep BlackBox's window out of screenshots so the agent sees the desktop behind it.
    /// Side effect: screen recorders and screen sharing do not show the window either.
    /// </summary>
    public bool HideFromScreenshots { get; set; } = true;

    /// <summary>Moving the mouse into the top-left corner stops the agent.</summary>
    public bool FailsafeCorner { get; set; } = true;

    /// <summary>Default working directory for file and shell tools. Empty = ./Workspace.</summary>
    public string WorkingDirectory { get; set; } = "";

    public string CustomInstructions { get; set; } = "";
}

public sealed class AppSettings
{
    public ServerSettings Server { get; set; } = new();
    public AgentSettings Agent { get; set; } = new();

    public AppSettings Clone() =>
        JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(this, SettingsStore.JsonOptions), SettingsStore.JsonOptions)!;
}

public sealed class SettingsStore(AppPaths paths)
{
    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    public AppSettings Load()
    {
        try
        {
            if (!File.Exists(paths.SettingsFile))
                return new AppSettings();

            var text = File.ReadAllText(paths.SettingsFile);
            var settings = JsonSerializer.Deserialize<AppSettings>(Migrate(text), JsonOptions) ?? new AppSettings();
            // Rewrite the file if it still has keys from older versions, so it only ever holds
            // settings that exist.
            if (JsonSerializer.Serialize(settings, JsonOptions) != text)
                Save(settings);
            return settings;
        }
        catch (JsonException)
        {
            // A hand-edited file that no longer parses: keep a copy and start from defaults.
            File.Copy(paths.SettingsFile, paths.SettingsFile + ".bad", overwrite: true);
        }
        return new AppSettings();
    }

    /// <summary>
    /// Settings from the local-model versions kept the server under "remote" next to "backend"
    /// and "local". Carry the server over; everything else that no longer exists is dropped.
    /// </summary>
    internal static string Migrate(string json)
    {
        if (System.Text.Json.Nodes.JsonNode.Parse(json) is not System.Text.Json.Nodes.JsonObject root)
            return json;
        if (root["server"] is null && root["remote"] is System.Text.Json.Nodes.JsonObject remote)
            root["server"] = remote.DeepClone();
        root.Remove("remote");
        root.Remove("local");
        root.Remove("backend");
        return root.ToJsonString();
    }

    public void Save(AppSettings settings)
    {
        Directory.CreateDirectory(paths.DataDir);
        var tmp = paths.SettingsFile + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(settings, JsonOptions));
        File.Move(tmp, paths.SettingsFile, overwrite: true);
    }
}
