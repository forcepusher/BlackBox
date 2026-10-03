using System.Text.Json;
using System.Text.Json.Serialization;

namespace BlackBox.Core.Settings;

/// <summary>Largest size screenshots are sent to the model at.</summary>
public enum ScreenshotResolution
{
    R1280x720,
    R1600x900,
    R1920x1080,
    R2560x1440,
    /// <summary>The screen's own resolution, no scaling.</summary>
    Native,
}

public static class ScreenshotResolutionExtensions
{
    /// <summary>The box screenshots must fit in, or null for native size.</summary>
    public static (int Width, int Height)? Box(this ScreenshotResolution r) => r switch
    {
        ScreenshotResolution.R1280x720 => (1280, 720),
        ScreenshotResolution.R1600x900 => (1600, 900),
        ScreenshotResolution.R1920x1080 => (1920, 1080),
        ScreenshotResolution.R2560x1440 => (2560, 1440),
        _ => null,
    };

    /// <summary>Nearest option for an old pixel-count setting.</summary>
    internal static ScreenshotResolution FromPixelBudget(long pixels) =>
        Enum.GetValues<ScreenshotResolution>().Where(r => r.Box() is not null)
            .MinBy(r => Math.Abs((long)r.Box()!.Value.Width * r.Box()!.Value.Height - pixels));
}

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
}

public sealed class AgentSettings
{
    /// <summary>Everything runs without asking by default; Stop and the failsafe corner stay available.</summary>
    public ApprovalMode Approval { get; set; } = ApprovalMode.Never;

    /// <summary>Largest size a screenshot is sent at; the screen is scaled down to fit, keeping its shape.</summary>
    public ScreenshotResolution ScreenshotResolution { get; set; } = ScreenshotResolution.R1920x1080;

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

    /// <summary>Show the inputs the agent makes in a fading log in the screen corner, and a ring where it clicks.</summary>
    public bool ShowInputs { get; set; } = true;

    public const int DefaultInputLogSeconds = 15;

    /// <summary>How long each line of the on-screen input log stays before it has faded out.</summary>
    public int InputLogSeconds { get; set; } = DefaultInputLogSeconds;

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
        // Screenshot size used to be a raw pixel count.
        if (root["agent"] is System.Text.Json.Nodes.JsonObject agent && agent["screenshotMaxPixels"] is System.Text.Json.Nodes.JsonValue px
            && px.TryGetValue<long>(out var pixels))
        {
            agent["screenshotResolution"] = ScreenshotResolutionExtensions.FromPixelBudget(pixels).ToString();
            agent.Remove("screenshotMaxPixels");
        }
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
