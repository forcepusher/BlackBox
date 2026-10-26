using System.Text.Json;
using System.Text.Json.Nodes;
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
    /// <summary>
    /// Kilo's free models to start with (see <see cref="Llm.KiloGateway"/>), so BlackBox works before
    /// any server is set up, without an account or a key. LM Studio's own address is http://localhost:1234/v1.
    /// </summary>
    public string BaseUrl { get; set; } = Llm.KiloGateway.Address;
    public string ApiKey { get; set; } = "";

    /// <summary>The model picked by hand in the dropdown (or with --model); empty until one is. Kept while the server doesn't offer it.</summary>
    public string Model { get; set; } = "";

    /// <summary>
    /// The strongest model the server offers (see <see cref="Llm.ModelChoice"/>), used while
    /// <see cref="Model"/> is empty or not on the server. Found again at every check and not
    /// saved, so the picked model is back in use as soon as the server lists it again.
    /// </summary>
    [JsonIgnore]
    public string? AutoModel { get; set; }

    /// <summary>The model requests go to.</summary>
    [JsonIgnore]
    public string ModelInUse => AutoModel ?? Model;

    /// <summary>
    /// Whether the model accepts images, which screen control needs. Read from LM Studio each time
    /// the app connects ("vlm" or "llm"); other servers don't say, so their models are assumed to. Not saved.
    /// </summary>
    [JsonIgnore]
    public bool SupportsVision { get; set; } = true;

    /// <summary>How hard the model thinks, picked in the top bar: one of <see cref="Efforts.Levels"/>.</summary>
    public string Effort { get; set; } = Efforts.Default;

    /// <summary>
    /// The effort levels the model in use takes, as the server says (see <see cref="Llm.ServerProbe"/>);
    /// empty when it takes none or doesn't say, and then no effort is sent. Not saved.
    /// </summary>
    [JsonIgnore]
    public IReadOnlyList<string> EffortLevels { get; set; } = [];

    /// <summary>The effort requests ask for: <see cref="Effort"/>, or the nearest level the model takes; null when it takes none.</summary>
    [JsonIgnore]
    public string? EffortInUse => Efforts.Nearest(Effort, EffortLevels);

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
    /// Whether <see cref="DetectedContextSize"/> is the most a hosted service takes for the model
    /// (Kilo and OpenRouter list 1M for some), not a size a server of one's own runs it with.
    /// </summary>
    [JsonIgnore]
    public bool ContextHosted { get; set; }

    /// <summary>
    /// What BlackBox uses on Auto of a hosted service's maximum: every step sends the whole
    /// conversation again, every screenshot in it, so at 1M a request would grow toward 800k tokens,
    /// tens of megabytes, more than Kilo's gateway takes (20 MB). The same size it assumes for a
    /// server that doesn't say.
    /// </summary>
    public const int HostedContextSize = FallbackContextSize;

    /// <summary>
    /// The context size BlackBox budgets with and resets by. A manual size is used as chosen, even
    /// above what the server reports; if the server then refuses a request as too long, the run
    /// drops to the size its error names.
    /// </summary>
    [JsonIgnore]
    public int ContextSize => AutoContext || ManualContextSize <= 0
        ? ContextHosted ? Math.Min(DetectedContextSize, HostedContextSize) : DetectedContextSize
        : ManualContextSize;

    /// <summary>
    /// Takes in what the server says the context is; a "too long" error only ever lowers it.
    /// <paramref name="hosted"/>: the size is a hosted service's maximum (see <see cref="ContextHosted"/>).
    /// </summary>
    public void SetDetectedContext(int? reported, bool hosted = false)
    {
        DetectedContextSize = reported ?? FallbackContextSize;
        ContextReported = reported is not null;
        ContextHosted = hosted && reported is not null;
    }

    /// <summary>A run found the server's real limit (from its "too long" error); keep it for the next run on Auto.</summary>
    public void LearnContextLimit(int tokens)
    {
        if (tokens <= 0 || tokens >= ContextSize)
            return;
        DetectedContextSize = tokens;
        ContextReported = true;
        ContextHosted = false;
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
    /// The stops the slider offers: up to 1M whatever the server says (or further, to a server's
    /// larger size), with the size the server reports as a stop of its own in place of the
    /// standard ones close to it (100k for 96k, 1,000,000 for 1M), so all of it can be picked.
    /// It used to end at the server's size: connected to LM Studio's 100k, nothing larger could be
    /// picked for a model that takes 1M.
    /// </summary>
    public static IReadOnlyList<int> Stops(int? serverMax)
    {
        if (serverMax is not > 0)
            return Standard;
        var stops = Standard.Where(s => Math.Abs(s - serverMax.Value) > serverMax.Value / 20).ToList();
        stops.Add(serverMax.Value);
        stops.Sort();
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

/// <summary>
/// The effort a model can be asked to think with, least to most, as Anthropic names them
/// (OpenAI-style servers take low, medium and high). Medium unless picked otherwise.
/// </summary>
public static class Efforts
{
    public static readonly IReadOnlyList<string> Levels = ["low", "medium", "high", "xhigh", "max"];

    public const string Default = "medium";

    /// <summary>The level as the dropdown says it.</summary>
    public static string Label(string level) => level switch
    {
        "xhigh" => "Extra high effort",
        "max" => "Max effort",
        _ => char.ToUpperInvariant(level[0]) + level[1..] + " effort",
    };

    /// <summary>
    /// <paramref name="wanted"/> if the model takes it, else the closest level it takes (the lower
    /// one between two: max asked of a model that goes up to high gets high); null when it takes none.
    /// </summary>
    public static string? Nearest(string wanted, IReadOnlyList<string> offered)
    {
        if (offered.Count == 0) return null;
        if (offered.Contains(wanted)) return wanted;
        var at = Rank(wanted) is var r and >= 0 ? r : Rank(Default);
        return offered.OrderBy(l => Math.Abs(Rank(l) - at)).ThenBy(Rank).First();
    }

    private static int Rank(string level)
    {
        for (var i = 0; i < Levels.Count; i++)
            if (Levels[i] == level) return i;
        return -1;
    }

    /// <summary>The known levels among <paramref name="names"/>, least to most.</summary>
    public static IReadOnlyList<string> Of(IEnumerable<string> names)
    {
        var set = names.Select(n => n.Trim().ToLowerInvariant()).ToHashSet();
        return Levels.Where(set.Contains).ToList();
    }
}

public sealed class AgentSettings
{
    /// <summary>Everything runs without asking by default; Stop and Pause/Break stay available.</summary>
    public ApprovalMode Approval { get; set; } = ApprovalMode.Never;

    /// <summary>
    /// The box a screenshot is scaled down to fit, keeping its shape and never scaled up: a 4K
    /// screen at full resolution costs four times the context of one at 1920x1080. 2560x1080
    /// by default (the user: "wide full HD"), so Full HD and 21:9 screens go as they are. At full
    /// resolution qwen3.8 hit text links 15/16 against 8/16 at 1920x1080, and buttons on a
    /// 5120x1440 screen 7/16 against 4/16 (ClickAccuracyTests, two rounds).
    /// </summary>
    public int ScreenshotMaxWidth { get; set; } = DefaultScreenshotWidth;

    public int ScreenshotMaxHeight { get; set; } = DefaultScreenshotHeight;

    public const int DefaultScreenshotWidth = 2560, DefaultScreenshotHeight = 1080;

    /// <summary>The smallest box (a smaller one is too blurry to read a button by) and the largest side.</summary>
    public const int MinScreenshotWidth = 640, MinScreenshotHeight = 360, MaxScreenshotSide = 16384;

    /// <summary>The box screenshots fit, with a hand-edited settings.json's sides kept in range.</summary>
    [JsonIgnore]
    public (int Width, int Height) ScreenshotBox =>
        (Math.Clamp(ScreenshotMaxWidth, MinScreenshotWidth, MaxScreenshotSide), Math.Clamp(ScreenshotMaxHeight, MinScreenshotHeight, MaxScreenshotSide));

    /// <summary>Attach a fresh screenshot after mouse/keyboard actions. Internal (tests turn it off).</summary>
    [JsonIgnore]
    public bool ScreenshotAfterActions { get; set; } = true;

    /// <summary>
    /// How long each line of the log of the agent's inputs in the screen corner stays before it has
    /// faded out. The log, and the ring where it clicks, are always shown: no longer settings.
    /// </summary>
    public const int InputLogSeconds = 15;

    public string CustomInstructions { get; set; } = "";
}

/// <summary>Where the main window was, so it opens there again: position in screen pixels, size in the UI's own units.</summary>
public sealed class WindowPlacement
{
    public int X { get; set; }
    public int Y { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    public bool Maximized { get; set; }
}

/// <summary>How the window's toolkit panels show what they show, as the user last left it.</summary>
public sealed class PanelSettings
{
    /// <summary>File changes: long lines wrap inside the panel rather than run off its right edge.</summary>
    public bool WrapDiff { get; set; } = true;

    /// <summary>File changes: the diff follows the agent's edits, to the lines it just changed.</summary>
    public bool WatchChanges { get; set; } = true;

    /// <summary>File changes opens by itself on the agent's first file change in a chat, unless another tool is open.</summary>
    public bool OpenFileChanges { get; set; } = true;
}

public sealed class AppSettings
{
    public ServerSettings Server { get; set; } = new();
    public AgentSettings Agent { get; set; } = new();
    public PanelSettings Panels { get; set; } = new();

    /// <summary>The main window's last size and place; null until it was first closed.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public WindowPlacement? Window { get; set; }

    public AppSettings Clone() =>
        JsonSerializer.Deserialize(JsonSerializer.Serialize(this, SettingsJson.Default.AppSettings), SettingsJson.Default.AppSettings)!;

    /// <summary>
    /// These settings with what changed from <paramref name="before"/> to <paramref name="after"/>
    /// put over them, key by key: a Settings dialog's changes over what another window saved while
    /// it was open, which its copy doesn't have.
    /// </summary>
    public AppSettings With(AppSettings before, AppSettings after)
    {
        JsonObject Node(AppSettings s) => (JsonObject)JsonSerializer.SerializeToNode(s, SettingsJson.Default.AppSettings)!;
        return SettingsStore.Merge(Node(before), Node(after), Node(this)).Deserialize(SettingsJson.Default.AppSettings)!;
    }
}

/// <summary>
/// settings.json. Every BlackBox window has the settings in memory, with a store of its own, so
/// each one writes only what it changed since it last read or wrote the file: a model picked in
/// one window stays picked when another closes and saves where it was.
/// </summary>
public sealed class SettingsStore(AppPaths paths)
{
    /// <summary>The settings as this store last read or wrote them; what differs from them is what its window changed.</summary>
    private JsonObject? _base;

    public AppSettings Load()
    {
        // No file yet (or none that can be read): the defaults are what this window has, so its first
        // save writes what it changed from them, over a file another window wrote meanwhile.
        _base = JsonSerializer.SerializeToNode(new AppSettings(), SettingsJson.Default.AppSettings) as JsonObject;
        try
        {
            if (!File.Exists(paths.SettingsFile))
                return new AppSettings();

            var text = File.ReadAllText(paths.SettingsFile);
            var settings = JsonSerializer.Deserialize(Migrate(text), SettingsJson.Default.AppSettings) ?? new AppSettings();
            // A hand-edited "server": null left the window without a server to show, and it closed at start.
            settings.Server ??= new();
            settings.Agent ??= new();
            settings.Panels ??= new();
            var loaded = JsonSerializer.Serialize(settings, SettingsJson.Default.AppSettings);
            _base = JsonNode.Parse(loaded) as JsonObject;
            // Rewrite the file if it still has keys from older versions, so it only ever holds
            // settings that exist. A folder that can't be written to (a read-only stick) can wait.
            if (loaded != text)
                lock (SaveGate)
                    TryWrite(() => AtomicFile.Write(paths.SettingsFile, loaded));
            return settings;
        }
        catch (JsonException)
        {
            // A hand-edited file that no longer parses: keep a copy and start from defaults.
            TryWrite(() => File.Copy(paths.SettingsFile, paths.SettingsFile + ".bad", overwrite: true));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Unreadable for now (locked by another program): defaults, and the file is left as it is.
        }
        return new AppSettings();
    }

    private static void TryWrite(Action write)
    {
        try { write(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
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
        root.Remove("local");
        root.Remove("backend");
        return root.ToJsonString();
    }

    /// <summary>Held while settings.json is read and written: every window has a store of its own.</summary>
    private static readonly Lock SaveGate = new();

    /// <summary>
    /// Writes what changed in <paramref name="settings"/> since this store last read or wrote the
    /// file, over what is in the file now: another window's changes meanwhile stay.
    /// </summary>
    public void Save(AppSettings settings)
    {
        var mine = (JsonObject)JsonSerializer.SerializeToNode(settings, SettingsJson.Default.AppSettings)!;
        // Read and written by one window at a time.
        lock (SaveGate)
        {
            var merged = _base is { } @base && ReadFile() is { } theirs ? Merge(@base, mine, theirs) : mine;
            using var text = new MemoryStream();
            using (var writer = new Utf8JsonWriter(text, new JsonWriterOptions { Indented = true }))
                merged.WriteTo(writer);
            AtomicFile.Write(paths.SettingsFile, System.Text.Encoding.UTF8.GetString(text.ToArray()));
        }
        _base = (JsonObject)mine.DeepClone();
    }

    /// <summary>settings.json as it is now; null when it isn't there, doesn't parse or stays locked.</summary>
    private JsonObject? ReadFile()
    {
        for (var attempt = 1; ; attempt++)
        {
            try { return File.Exists(paths.SettingsFile) ? JsonNode.Parse(File.ReadAllText(paths.SettingsFile)) as JsonObject : null; }
            catch (JsonException) { return null; }
            // Caught as it is replaced, or held a moment by an antivirus scanner.
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && attempt < 5) { Thread.Sleep(50 * attempt); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
        }
    }

    /// <summary>
    /// <paramref name="theirs"/> with what differs between <paramref name="base"/> and
    /// <paramref name="mine"/> put over it, key by key within the objects (the server's address
    /// changed here, its model there: both stay).
    /// </summary>
    internal static JsonObject Merge(JsonObject @base, JsonObject mine, JsonObject theirs)
    {
        var merged = (JsonObject)theirs.DeepClone();
        foreach (var key in mine.Select(p => p.Key).Union(@base.Select(p => p.Key)).ToList())
        {
            var (now, before) = (mine[key], @base[key]);
            if (JsonNode.DeepEquals(now, before))
                continue; // not changed here: what is there stands
            if (now is JsonObject nowObject && before is JsonObject beforeObject && merged[key] is JsonObject thereObject)
                merged[key] = Merge(beforeObject, nowObject, thereObject);
            else if (mine.ContainsKey(key))
                merged[key] = now?.DeepClone();
            else
                merged.Remove(key);
        }
        return merged;
    }
}

/// <summary>settings.json's format, generated at build time so trimmed builds can read and write it without reflection.</summary>
[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UseStringEnumConverter = true)]
[JsonSerializable(typeof(AppSettings))]
internal sealed partial class SettingsJson : JsonSerializerContext;
