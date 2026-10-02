using System.Text.Json;
using System.Text.Json.Serialization;

namespace BlackBox.Core.Settings;

public enum BackendKind { Local, Remote }

public enum ApprovalMode
{
    /// <summary>Ask before every tool call.</summary>
    AskAll,
    /// <summary>Ask before shell commands and file changes; screen/mouse/keyboard run freely.</summary>
    AskRisky,
    /// <summary>Never ask.</summary>
    Never,
}

/// <summary>Settings for the llama-server process that BlackBox starts and stops itself.</summary>
public sealed class LocalModelSettings
{
    /// <summary>Path to llama-server(.exe). Empty = look in ./llama, then on PATH.</summary>
    public string ServerPath { get; set; } = "";

    public string ModelPath { get; set; } = "Models/Qwen3.8-27B-UD-IQ2_S.gguf";

    /// <summary>Vision projector GGUF. Needed for the model to see screenshots; skipped with a warning if missing.</summary>
    public string MmprojPath { get; set; } = "Models/mmproj-F16.gguf";

    public int Port { get; set; } = 18080;

    // Defaults tuned for a 27B IQ2_S model on a 16 GB GPU: all layers on the GPU,
    // 8-bit KV cache with flash attention so a 32k context fits next to the weights.
    public int ContextSize { get; set; } = 32768;
    public int GpuLayers { get; set; } = 99;
    public bool FlashAttention { get; set; } = true;
    public string KvCacheType { get; set; } = "q8_0";
    public int Threads { get; set; } = 0;
    public int BatchSize { get; set; } = 512;
    public string ExtraArgs { get; set; } = "";

    /// <summary>Start llama-server automatically when the app opens.</summary>
    public bool AutoStart { get; set; } = true;
}

public sealed class RemoteApiSettings
{
    public string BaseUrl { get; set; } = "https://api.openai.com/v1";
    public string ApiKey { get; set; } = "";
    public string Model { get; set; } = "";
    public bool SupportsVision { get; set; } = true;
}

public sealed class AgentSettings
{
    public int MaxSteps { get; set; } = 40;
    public ApprovalMode Approval { get; set; } = ApprovalMode.AskRisky;

    /// <summary>Screenshots are downscaled to fit this box before going to the model.</summary>
    public int ScreenshotMaxWidth { get; set; } = 1280;
    public int ScreenshotMaxHeight { get; set; } = 800;

    /// <summary>Only the newest N screenshots stay in context; older ones are dropped to save tokens.</summary>
    public int KeepScreenshots { get; set; } = 2;

    /// <summary>Attach a fresh screenshot after mouse/keyboard actions.</summary>
    public bool ScreenshotAfterActions { get; set; } = true;

    /// <summary>Moving the mouse into the top-left corner stops the agent.</summary>
    public bool FailsafeCorner { get; set; } = true;

    public bool EnableThinking { get; set; } = true;
    public double Temperature { get; set; } = 0.6;
    public double TopP { get; set; } = 0.95;
    public int TopK { get; set; } = 20;
    public int MaxTokensPerReply { get; set; } = 8192;

    /// <summary>Default working directory for file and shell tools. Empty = ./Workspace.</summary>
    public string WorkingDirectory { get; set; } = "";

    public string CustomInstructions { get; set; } = "";
}

public sealed class AppSettings
{
    public BackendKind Backend { get; set; } = BackendKind.Local;
    public LocalModelSettings Local { get; set; } = new();
    public RemoteApiSettings Remote { get; set; } = new();
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
