using System.Text.Json;
using System.Text.Json.Nodes;
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

    /// <summary>
    /// Whether the model accepts images, which screen control needs. Read from LM Studio each time
    /// the app connects ("vlm" or "llm"); other servers don't say, so their models are assumed to. Not saved.
    /// </summary>
    [JsonIgnore]
    public bool SupportsVision { get; set; } = true;

    /// <summary>Assumed context window when the server doesn't report one. If the real one is smaller, the server's "too long" error says so and BlackBox adjusts.</summary>
    public const int FallbackContextSize = 100_000;

    /// <summary>Use the context size the server reports (the default), rather than <see cref="ManualContextSize"/>.</summary>
    public bool AutoContext { get; set; } = true;

    /// <summary>Context size chosen on the slider, used while <see cref="AutoContext"/> is off; 0 until one is chosen.</summary>
    public int ManualContextSize { get; set; }

    /// <summary>
    /// Context window of the model, as reported by the server when the app connects (see
    /// <see cref="Llm.ServerProbe"/>), or learnt from its "too long" error. Not saved.
    /// </summary>
    [JsonIgnore]
    public int DetectedContextSize { get; set; } = FallbackContextSize;

    /// <summary>Whether <see cref="DetectedContextSize"/> came from the server, rather than being <see cref="FallbackContextSize"/>.</summary>
    [JsonIgnore]
    public bool ContextReported { get; set; }

    /// <summary>
    /// The context size BlackBox budgets with and resets by. A manual size is used as chosen, even
    /// above what the server reports; if the server then refuses a request as too long, the run
    /// drops to the size its error names.
    /// </summary>
    [JsonIgnore]
    public int ContextSize => AutoContext || ManualContextSize <= 0 ? DetectedContextSize : ManualContextSize;

    /// <summary>Takes in what the server says the context is; a "too long" error only ever lowers it.</summary>
    public void SetDetectedContext(int? reported)
    {
        DetectedContextSize = reported ?? FallbackContextSize;
        ContextReported = reported is not null;
    }

    /// <summary>A run found the server's real limit (from its "too long" error); keep it for the next run on Auto.</summary>
    public void LearnContextLimit(int tokens)
    {
        if (tokens <= 0 || tokens >= ContextSize)
            return;
        DetectedContextSize = tokens;
        ContextReported = true;
    }
}

/// <summary>The sizes the context slider stops at, and how sizes are written.</summary>
public static class ContextSizes
{
    /// <summary>4k up to 1M in steps that get coarser as they grow, so the common sizes (32k, 40k, 64k, 128k, 256k…) are all on it.</summary>
    public static readonly IReadOnlyList<int> Standard =
        new[] { 4, 8, 12, 16, 24, 32, 40, 48, 64, 80, 96, 128, 160, 192, 256, 320, 384, 512, 640, 768, 1024 }
            .Select(k => k * 1024).ToArray();

    /// <summary>
    /// The stops the slider offers. When the server reports its size, the slider ends exactly
    /// there, so its far right is "everything the server has" and nothing beyond it can be picked.
    /// </summary>
    public static IReadOnlyList<int> Stops(int? serverMax)
    {
        if (serverMax is not > 0)
            return Standard;
        var stops = Standard.Where(s => s < serverMax).ToList();
        stops.Add(serverMax.Value);
        return stops;
    }

    /// <summary>Smallest and largest size that can be typed in; the agent needs room for its prompt and a reply.</summary>
    public const int Min = 4096, Max = 16 * 1024 * 1024;

    /// <summary>
    /// Reads a typed size: "65536", "64k", "64 K", "1M", "1.5m", "250,000". It reads back what
    /// <see cref="Format"/> writes: "64k" is 65536 (a slider stop), "250k" is 250000 (as LM Studio
    /// would have it), "1M" is 1048576. Null when it isn't a size; clamped to <see cref="Min"/>..<see cref="Max"/>.
    /// </summary>
    public static int? Parse(string? text)
    {
        var t = (text ?? "").Trim().Replace(",", "").Replace(" ", "").Replace("_", "");
        if (t.Length == 0) return null;
        var suffix = char.ToLowerInvariant(t[^1]);
        if (suffix is 'k' or 'm')
            t = t[..^1];
        if (!double.TryParse(t, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var n)
            || !double.IsFinite(n) || n <= 0)
            return null;
        var tokens = suffix switch
        {
            'k' => Standard.Contains((int)Math.Min(n * 1024, int.MaxValue)) ? n * 1024 : n * 1000,
            'm' => n * 1024 * 1024,
            _ => n,
        };
        return (int)Math.Clamp(Math.Round(tokens), Min, Max);
    }

    /// <summary>Position of a size on the slider: the largest stop not above it (the first stop if all are).</summary>
    public static int IndexOf(IReadOnlyList<int> stops, int tokens)
    {
        var index = 0;
        for (var i = 0; i < stops.Count; i++)
            if (stops[i] <= tokens)
                index = i;
        return index;
    }

    /// <summary>
    /// "64k", "1M". Powers of two are counted in 1024s (65536 is 64k), round numbers in 1000s, so a
    /// size typed into LM Studio as 250000 reads 250k here too.
    /// </summary>
    public static string Format(int tokens)
    {
        if (tokens >= 1024 * 1024 && tokens % (1024 * 1024) == 0) return $"{tokens / (1024 * 1024)}M";
        if (tokens >= 1_000_000 && tokens % 1_000_000 == 0) return $"{tokens / 1_000_000}M";
        return tokens % 1024 == 0 ? $"{tokens / 1024}k" : $"{Math.Round(tokens / 1000.0)}k";
    }
}

public sealed class AgentSettings
{
    /// <summary>Everything runs without asking by default; Stop and the Break key stay available.</summary>
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
        if (JsonNode.Parse(json) is not JsonObject root)
            return json;
        if (root["server"] is null && root["remote"] is JsonObject remote)
            root["server"] = remote.DeepClone();
        root.Remove("remote");
        // Screenshot size used to be a raw pixel count.
        if (root["agent"] is JsonObject agent && agent["screenshotMaxPixels"] is JsonValue px
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
