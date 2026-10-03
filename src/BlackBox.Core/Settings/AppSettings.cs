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
    public bool SupportsVision { get; set; } = true;

    /// <summary>
    /// Context window of the loaded model. Filled in from LM Studio when it reports it; used to
    /// trim old thinking and tool output before a request would overflow.
    /// </summary>
    public int ContextSize { get; set; } = 32768;

    /// <summary>
    /// Send llama.cpp / LM Studio extras (top_k, min_p, repeat_penalty, chat-template switches).
    /// Dropped automatically if the server rejects them.
    /// </summary>
    public bool SendExtras { get; set; } = true;
}
public sealed class AgentSettings
{
    public int MaxSteps { get; set; } = 40;
    /// <summary>Everything runs without asking by default; Stop and the failsafe corner stay available.</summary>
    public ApprovalMode Approval { get; set; } = ApprovalMode.Never;

    /// <summary>Screenshots are downscaled to at most this many pixels before going to the model (about 1.3 MP, roughly 1300 image tokens).</summary>
    public int ScreenshotMaxPixels { get; set; } = 1_300_000;

    /// <summary>Only the newest N screenshots stay in context; older ones are dropped to save tokens.</summary>
    public int KeepScreenshots { get; set; } = 2;

    /// <summary>Attach a fresh screenshot after mouse/keyboard actions.</summary>
    public bool ScreenshotAfterActions { get; set; } = true;

    /// <summary>
    /// Keep BlackBox's window out of screenshots so the agent sees the desktop behind it.
    /// Side effect: screen recorders and screen sharing do not show the window either.
    /// </summary>
    public bool HideFromScreenshots { get; set; } = true;

    /// <summary>Moving the mouse into the top-left corner stops the agent.</summary>
    public bool FailsafeCorner { get; set; } = true;

    public bool EnableThinking { get; set; } = true;
    // Low temperature keeps clicks and tool arguments precise.
    public double Temperature { get; set; } = 0.1;
    public double TopP { get; set; } = 0.95;
    public int TopK { get; set; } = 40;
    public double MinP { get; set; } = 0.05;
    /// <summary>1.0 = off.</summary>
    public double RepeatPenalty { get; set; } = 1.0;
    public int MaxTokensPerReply { get; set; } = 8192;

    /// <summary>If the model thinks longer than this (roughly, in tokens) it is cut off and asked to act directly.</summary>
    public int MaxThinkingTokens { get; set; } = 3000;

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
            if (File.Exists(paths.SettingsFile))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(paths.SettingsFile), JsonOptions) ?? new AppSettings();
        }
        catch (JsonException)
        {
            // A hand-edited file that no longer parses: keep a copy and start from defaults.
            File.Copy(paths.SettingsFile, paths.SettingsFile + ".bad", overwrite: true);
        }
        return new AppSettings();
    }

    public void Save(AppSettings settings)
    {
        Directory.CreateDirectory(paths.DataDir);
        var tmp = paths.SettingsFile + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(settings, JsonOptions));
        File.Move(tmp, paths.SettingsFile, overwrite: true);
    }
}
