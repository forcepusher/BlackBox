using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using BlackBox.Core.Settings;

namespace BlackBox.Core.Comfy;

/// <summary>How one of a model's maker's pictures was made: settings known to give a good one.</summary>
/// <param name="Embeddings">Whether its maker used embeddings (textual inversions), which its prompts name like words.</param>
public sealed record ModelExample(string Prompt, string? Negative, string? Sampler, double? Cfg, int? Steps, int? Width, int? Height, int? ClipSkip, string? Hires,
    bool Embeddings = false);

/// <summary>
/// What Civitai says about a model file: its page, the base model it is made for, its trigger
/// words, what its page recommends and how its maker's own pictures were made.
/// </summary>
public sealed record ModelCard(string Title, string Page, string? Base, IReadOnlyList<string> TriggerWords, string? Advice, IReadOnlyList<ModelExample> Examples);

/// <summary>
/// Model cards for the checkpoints and LoRAs a workflow uses, from Civitai, where most of them come
/// from. Asked for a bunny with the user's Nova Furry XL, an Illustrious model, Nemotron wrote a
/// prompt in plain sentences and ran it at 512x760 with the template's settings; the model's page
/// asks for quality tags and booru-style tags, and its maker's own pictures were made at 768x1344
/// with a 1.5x hires pass. The pictures came out young, soft and off-style, four times running. A
/// file is found on Civitai by its SHA-256, worked out once and kept with the file's size and time
/// in Data/comfyui/model-hashes.json (6.9 GB took 7 s here). The quick hash of 64 KB a megabyte in
/// (Civitai's "AutoV1") was tried first and gave this PC's Pony Diffusion V6 XL the card of
/// "Incomplete anime pony", a fine-tune of it of the same size: models trained on one base with
/// its text encoder left as it was share those bytes. Cards are kept in
/// Data/comfyui/model-cards.json, and a file Civitai doesn't know is asked about again after a week.
/// </summary>
public sealed partial class ComfyModelCards(string stateDirectory)
{
    private static readonly TimeSpan NotFoundLife = TimeSpan.FromDays(7);
    private const int MaxAdvice = 1_200;

    private readonly ConcurrentDictionary<string, (ModelCard? Card, DateTime At)> _cards = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>The SHA-256 of model files, by path, size and time (see <see cref="HashKey"/>).</summary>
    private readonly ConcurrentDictionary<string, string> _hashes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Task<string?>> _hashing = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _gate = new();
    private bool _loaded;

    private string CacheFile => Path.Combine(stateDirectory, "model-cards.json");
    private string HashFile => Path.Combine(stateDirectory, "model-hashes.json");

    /// <summary>
    /// The card of the model file at <paramref name="path"/>, or null when Civitai doesn't know it.
    /// Throws when Civitai can't be asked or <paramref name="ct"/> ends the wait, which a big file's
    /// first hash can outlast: the hash goes on being worked out, for the next look.
    /// </summary>
    public async Task<ModelCard?> LookUpAsync(string path, CancellationToken ct)
    {
        if (await HashAsync(path).WaitAsync(ct).ConfigureAwait(false) is not { } hash)
            return null;
        Load();
        if (_cards.TryGetValue(hash, out var known) && (known.Card is not null || DateTime.UtcNow - known.At < NotFoundLife))
            return known.Card;
        var card = await FetchAsync(hash, ct).ConfigureAwait(false);
        _cards[hash] = (card, DateTime.UtcNow);
        Save();
        return card;
    }

    /// <summary>A file's place in the hash cache: its full path, size and time, so a changed file is hashed again.</summary>
    private static string? HashKey(string path)
    {
        try
        {
            var file = new FileInfo(path);
            return file.Exists ? $"{file.FullName}|{file.Length}|{file.LastWriteTimeUtc.Ticks}" : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>
    /// The SHA-256 of the file at <paramref name="path"/> (upper-case hex), as Civitai finds files by
    /// it: the saved one, or worked out once in the background, where it goes on when a wait for it
    /// ends. Null for a file that can't be read.
    /// </summary>
    internal Task<string?> HashAsync(string path)
    {
        if (HashKey(path) is not { } key)
            return Task.FromResult<string?>(null);
        Load();
        if (_hashes.TryGetValue(key, out var saved))
            return Task.FromResult<string?>(saved);
        lock (_hashing)
        {
            if (_hashing.TryGetValue(key, out var running))
                return running;
            // Done between the look above and the lock.
            if (_hashes.TryGetValue(key, out saved))
                return Task.FromResult<string?>(saved);
            var hashing = Task.Run(() =>
            {
                string? hash = null;
                try
                {
                    using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 20, FileOptions.SequentialScan);
                    hash = Convert.ToHexString(SHA256.HashData(file));
                    _hashes[key] = hash;
                    SaveHashes();
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                lock (_hashing)
                    _hashing.Remove(key);
                return hash;
            });
            _hashing[key] = hashing;
            return hashing;
        }
    }

    internal static async Task<ModelCard?> FetchAsync(string hash, CancellationToken ct)
    {
        var site = ComfyModelSearch.CivitaiBase;
        JsonObject? version;
        try
        {
            version = await ComfyModelSearch.GetJsonAsync($"{site}/api/v1/model-versions/by-hash/{hash}", ct).ConfigureAwait(false) as JsonObject;
        }
        catch (HttpRequestException ex) when (ex.Message.StartsWith("404", StringComparison.Ordinal))
        {
            return null;
        }
        if (version is null || (long?)version["modelId"] is not { } modelId)
            return null;

        var model = await ComfyModelSearch.GetJsonAsync($"{site}/api/v1/models/{modelId}", ct).ConfigureAwait(false) as JsonObject;
        var title = (string?)model?["name"] ?? (string?)version["model"]?["name"] ?? "?";
        if ((string?)version["name"] is { Length: > 0 } versionName && !title.Contains(versionName, StringComparison.OrdinalIgnoreCase))
            title += $" ({versionName})";
        var words = (version["trainedWords"] as JsonArray ?? []).Select(w => ((string?)w)?.Trim()).OfType<string>().Where(w => w.Length > 0).Distinct().Take(12).ToList();
        var advice = Advice((string?)model?["description"]) ?? Advice((string?)version["description"]);

        var examples = new List<ModelExample>();
        if ((string?)model?["creator"]?["username"] is { Length: > 0 } maker && (long?)version["id"] is { } versionId)
        {
            try
            {
                var images = await ComfyModelSearch.GetJsonAsync($"{site}/api/v1/images?modelVersionId={versionId}&username={Uri.EscapeDataString(maker)}" +
                                                                 "&withMeta=true&nsfw=None&sort=Most%20Reactions&limit=20", ct).ConfigureAwait(false);
                examples.AddRange((images?["items"] as JsonArray ?? []).OfType<JsonObject>().Select(Example).OfType<ModelExample>().Take(1));
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested) { }
        }
        return new ModelCard(title, $"{site}/models/{modelId}", (string?)version["baseModel"], words, advice, examples);
    }

    /// <summary>The settings a picture of Civitai's was made with, from its "meta"; null without a prompt.</summary>
    internal static ModelExample? Example(JsonObject image)
    {
        if (image["meta"] is not JsonObject meta || Text(meta["prompt"]) is not { Length: > 0 } prompt)
            return null;
        int? width = null, height = null;
        if (Text(meta["Size"]) is { } size && SizeText().Match(size) is { Success: true } m)
            (width, height) = (int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value));
        width ??= Number(meta["width"]) is { } w ? (int)w : null;
        height ??= Number(meta["height"]) is { } h ? (int)h : null;
        var hires = Text(meta["Hires upscale"]) is { Length: > 0 } scale
            ? $"then hires {scale}x" + (Text(meta["Hires upscaler"]) is { Length: > 0 } upscaler && upscaler != "None" ? $" with {upscaler}" : "")
              + (Text(meta["Denoising strength"]) is { Length: > 0 } d ? $" at denoise {d}" : "")
            : null;
        var clip = Number(meta["clipSkip"]) ?? Number(meta["Clip skip"]);
        // Since AUTOMATIC1111 1.9 the schedule is a field of its own ("Schedule type: Karras"), no
        // longer part of the sampler's name: Juggernaut XL's maker's "DPM++ 2M SDE" was Karras, and
        // run with ComfyUI's normal schedule it sprinkled the sky with coloured specks.
        var sampler = Text(meta["sampler"]);
        if (sampler is { Length: > 0 } && (Text(meta["Schedule type"]) ?? Text(meta["scheduler"])) is { Length: > 0 } schedule &&
            !schedule.Equals("Automatic", StringComparison.OrdinalIgnoreCase) && !sampler.EndsWith(schedule, StringComparison.OrdinalIgnoreCase))
            sampler += " " + schedule;
        return new ModelExample(prompt, Text(meta["negativePrompt"]), sampler, Number(meta["cfgScale"]), Number(meta["steps"]) is { } s ? (int)s : null,
            width, height, clip is { } c ? (int)c : null, hires,
            new[] { meta["civitaiResources"], meta["resources"] }.OfType<JsonArray>().SelectMany(a => a.OfType<JsonObject>())
                .Any(r => ((string?)r["type"])?.ToLowerInvariant() is "embed" or "embedding" or "textualinversion") || meta["TI hashes"] is not null);

        static string? Text(JsonNode? n) => n is JsonValue v ? v.ToString().Trim() : null;
        static double? Number(JsonNode? n) => n is JsonValue v && (v.TryGetValue<double>(out var d) || double.TryParse(v.ToString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out d)) ? d : null;
    }

    /// <summary>
    /// The part of a model's page that says how to use it: from its first line about settings
    /// ("Recommended settings", the sampler, CFG, steps…), the lines about settings and the short
    /// ones (headings like "(Illustrious)"), as many as fit; the paragraphs in between that say
    /// nothing of settings are left out. Null when the page says nothing of that.
    /// </summary>
    internal static string? Advice(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
            return null;
        var text = BlockEnd().Replace(html, "\n");
        text = WebUtility.HtmlDecode(Tag().Replace(text, " "));
        var lines = text.Split('\n').Select(l => Spaces().Replace(l, " ").Trim()).Where(l => l.Length > 0).ToList();
        var start = lines.FindIndex(l => StrongSettingsWord().IsMatch(l));
        if (start < 0)
            start = lines.FindIndex(l => SettingsWord().IsMatch(l));
        if (start < 0)
            return null;
        var sb = new StringBuilder();
        foreach (var line in lines.Skip(start).Where(l => l.Length <= 100 || SettingsWord().IsMatch(l) || StrongSettingsWord().IsMatch(l)))
        {
            if (sb.Length + line.Length > MaxAdvice)
            {
                if (sb.Length == 0)
                    sb.Append(line[..MaxAdvice]).Append('…');
                break;
            }
            sb.Append(line).Append('\n');
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// ComfyUI's sampler_name and scheduler for a sampler as AUTOMATIC1111 names it, which is how
    /// Civitai's pictures give it: "DPM++ 2M Karras" is dpmpp_2m with karras. Null for one it doesn't know.
    /// </summary>
    internal static (string Sampler, string Scheduler)? ComfySampler(string? a1111)
    {
        if (string.IsNullOrWhiteSpace(a1111))
            return null;
        var name = a1111.Trim().ToLowerInvariant();
        var scheduler = "normal";
        foreach (var (suffix, comfy) in new[] { (" karras", "karras"), (" exponential", "exponential"), (" sgm uniform", "sgm_uniform"), (" simple", "simple"), (" beta", "beta"),
                     (" normal", "normal"), (" kl optimal", "kl_optimal"), (" linear quadratic", "linear_quadratic"), (" ddim", "ddim_uniform") })
            if (name.EndsWith(suffix, StringComparison.Ordinal))
                (name, scheduler) = (name[..^suffix.Length], comfy);
        var sampler = name switch
        {
            "euler a" or "euler ancestral" => "euler_ancestral",
            "euler" => "euler",
            "heun" => "heun",
            "lms" => "lms",
            "ddim" => "ddim",
            "unipc" or "uni_pc" => "uni_pc",
            "lcm" => "lcm",
            "dpm2" => "dpm_2",
            "dpm2 a" => "dpm_2_ancestral",
            "dpm++ 2s a" => "dpmpp_2s_ancestral",
            "dpm++ 2m" => "dpmpp_2m",
            "dpm++ sde" => "dpmpp_sde",
            "dpm++ 2m sde" => "dpmpp_2m_sde",
            "dpm++ 3m sde" => "dpmpp_3m_sde",
            _ => null,
        };
        return sampler is null ? null : (sampler, scheduler);
    }

    /// <summary>
    /// The side of the square a base model makes pictures at best: 512 for SD 1.x, 768 for SD 2.x,
    /// 1024 for SDXL and the ones built on it (Pony, Illustrious, NoobAI) and the newer picture
    /// models. Null for the others (video models make other sizes: LTX 768x512).
    /// </summary>
    public static int? NativeSide(string baseModel) =>
        SdVersion().Match(baseModel) is { Success: true } m ? (m.Groups[1].Value == "1" ? 512 : m.Groups[1].Value == "2" ? 768 : 1024)
        : PictureModel().IsMatch(baseModel) ? 1024 : null;

    /// <summary>How a LoRA made for one base model fits a checkpoint of another.</summary>
    public enum Fit
    {
        /// <summary>The same base, or one the LoRA is as good on, or one this can't tell.</summary>
        Fits,
        /// <summary>The same kind of model but another line of it (SDXL, Pony, Illustrious): it applies, but weaker or off-style.</summary>
        OtherLine,
        /// <summary>Another kind of model (SD 1.5 and SDXL, SDXL and Flux): ComfyUI can't apply it at all.</summary>
        OtherKind,
    }

    /// <summary>
    /// How a LoRA for <paramref name="loraBase"/> fits a checkpoint of <paramref name="checkpointBase"/>,
    /// as Civitai names base models ("SD 1.5", "SDXL 1.0", "Pony", "Illustrious", "Flux.1 D").
    /// </summary>
    public static Fit FitOf(string loraBase, string checkpointBase)
    {
        if (Kind(loraBase) is not { } lora || Kind(checkpointBase) is not { } checkpoint)
            return Fit.Fits;
        if (lora != checkpoint)
            return Fit.OtherKind;
        return lora == "SDXL" && Line(loraBase) != Line(checkpointBase) ? Fit.OtherLine : Fit.Fits;

        static string? Kind(string b) =>
            b.Contains("flux", StringComparison.OrdinalIgnoreCase) ? "Flux"
            : SdxlFamily().IsMatch(b) ? "SDXL"
            : SdVersion().Match(b) is { Success: true } m ? "SD " + m.Groups[1].Value
            : null;

        // NoobAI is trained on Illustrious, and their LoRAs are used on both.
        static string Line(string b) =>
            b.Contains("pony", StringComparison.OrdinalIgnoreCase) ? "Pony"
            : b.Contains("illustrious", StringComparison.OrdinalIgnoreCase) || b.Contains("noob", StringComparison.OrdinalIgnoreCase) ? "Illustrious"
            : "SDXL";
    }

    /// <summary>What a base model is, said for the sizes it makes: "Illustrious, an SDXL model, made for about 1 megapixel".</summary>
    public static string Describe(string baseModel)
    {
        var family = SdxlFamily().IsMatch(baseModel) && !baseModel.Contains("SDXL", StringComparison.OrdinalIgnoreCase) ? ", an SDXL model" : "";
        var size = NativeSide(baseModel) switch
        {
            512 => ", made for about 512x512 (512x768 or 768x512 for portrait or landscape)",
            768 => ", made for about 768x768",
            1024 => ", made for about 1 megapixel (1024x1024, 832x1216 or 1216x832)",
            _ => "",
        };
        return $"{baseModel}{family}{size}";
    }

    /// <summary>
    /// The base model of a checkpoint or LoRA as its file says it (the names of its tensors, or the
    /// metadata trainers write), without asking anyone: "SD 1.5", "SD 2", "SDXL", "SD 3", "Flux"; null when it can't be told.
    /// </summary>
    public static string? BaseFromFile(string path)
    {
        if (!path.EndsWith(".safetensors", StringComparison.OrdinalIgnoreCase))
            return null;
        try
        {
            using var file = File.OpenRead(path);
            Span<byte> size = stackalloc byte[8];
            file.ReadExactly(size);
            var length = BitConverter.ToUInt64(size);
            if (length is 0 or > 100_000_000 || (long)length > file.Length - 8)
                return null;
            var header = new byte[length];
            file.ReadExactly(header);
            using var doc = JsonDocument.Parse(header);
            string? said = null;
            var keys = new List<string>();
            foreach (var p in doc.RootElement.EnumerateObject())
            {
                if (p.Name == "__metadata__" && p.Value.ValueKind == JsonValueKind.Object)
                {
                    foreach (var name in new[] { "modelspec.architecture", "ss_base_model_version" })
                        if (p.Value.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String)
                            said ??= BaseFromName(v.GetString()!);
                }
                else
                    keys.Add(p.Name);
            }
            return said ?? BaseFromTensors(keys);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or EndOfStreamException)
        {
            return null;
        }
    }

    /// <summary>The base model a trainer's or a model spec's name says: "sdxl_base_v1-0" and "stable-diffusion-xl-v1-base" are SDXL.</summary>
    internal static string? BaseFromName(string name)
    {
        var n = name.ToLowerInvariant();
        return n.Contains("flux") ? "Flux"
            : n.Contains("xl") ? "SDXL"
            : n.Contains("v3") || n.Contains("sd3") || n.Contains("-3-") ? "SD 3"
            : n.Contains("v2") || n.Contains("sd2") || n.Contains("v-2") ? "SD 2"
            : n.Contains("v1") || n.Contains("sd1") || n.Contains("v-1") ? "SD 1.5"
            : null;
    }

    /// <summary>The base model the names of a file's tensors say: each has parts no other has.</summary>
    internal static string? BaseFromTensors(IReadOnlyCollection<string> keys)
    {
        bool Any(string part) => keys.Any(k => k.Contains(part, StringComparison.Ordinal));
        return Any("double_blocks") ? "Flux"
            : Any("joint_blocks") ? "SD 3"
            : Any("conditioner.embedders.1.") || Any("lora_te2_") ? "SDXL"
            : Any("cond_stage_model.model.") ? "SD 2"
            : Any("cond_stage_model.transformer.") || Any("lora_te_") ? "SD 1.5"
            : null;
    }

    /// <summary>A card as the model reads it, with the file it is about.</summary>
    public static string Format(string file, ModelCard card)
    {
        var sb = new StringBuilder();
        sb.Append($"{file} is {card.Title} on Civitai ({card.Page})");
        sb.AppendLine(card.Base is { Length: > 0 } b ? $": {Describe(b)}." : ".");
        if (card.TriggerWords.Count > 0)
            sb.AppendLine("Its trigger words, which go in the prompt: " + string.Join(", ", card.TriggerWords) + ".");
        if (card.Advice is { } advice)
            sb.AppendLine("Its page says:").AppendLine("  " + advice.Replace("\n", "\n  "));
        foreach (var e in card.Examples)
        {
            var sampler = e.Sampler is { } s && ComfySampler(s) is { } comfy ? $"{s} (sampler_name {comfy.Sampler}, scheduler {comfy.Scheduler})" : e.Sampler;
            var settings = new[]
            {
                sampler, e.Cfg is { } cfg ? $"CFG {cfg.ToString(System.Globalization.CultureInfo.InvariantCulture)}" : null,
                e.Steps is { } steps ? $"{steps} steps" : null, e.ClipSkip is > 1 and var c ? $"clip skip {c}" : null,
                e.Width is { } w && e.Height is { } h ? $"{w}x{h}" : null, e.Hires,
            }.OfType<string>();
            sb.AppendLine($"A picture its maker made with it: {string.Join(", ", settings)};");
            sb.AppendLine("  prompt: " + e.Prompt.ReplaceLineEndings(" ").Clip(700));
            if (e.Negative is { Length: > 0 } negative)
                sb.AppendLine("  negative: " + negative.ReplaceLineEndings(" ").Clip(400));
        }
        // What these pictures were made in (AUTOMATIC1111, Forge) says some things ComfyUI says otherwise.
        if (card.Examples.Any(e => e.ClipSkip is > 1))
            sb.AppendLine("Clip skip 2 is a CLIPSetLastLayer node (stop_at_clip_layer -2) between the checkpoint's CLIP and the text nodes.");
        if (card.Examples.Any(e => e.Hires is not null))
            sb.AppendLine("A hires pass is a second KSampler on the first picture made bigger (LatentUpscaleBy, or an upscale model), at that denoise.");
        if (card.Examples.Any(e => e.Prompt.Contains("BREAK", StringComparison.Ordinal)))
            sb.AppendLine("BREAK is AUTOMATIC1111's: ComfyUI's text node reads it as a word, so leave it out; (word:1.2) weights work the same.");
        if (card.Examples.Any(e => e.Embeddings))
            sb.AppendLine("Some words in its prompts may be embeddings its maker had installed (in ComfyUI written embedding:Name, from models/embeddings): leave out those this PC doesn't have.");
        if (card.Examples.Any(e => e.Prompt.Contains("<lora:", StringComparison.OrdinalIgnoreCase)))
            sb.AppendLine("<lora:name:weight> in a prompt is AUTOMATIC1111's: in ComfyUI a LoRA is a LoraLoader node, and the tag is left out.");
        return sb.ToString().TrimEnd();
    }

    private void Load()
    {
        lock (_gate)
        {
            if (_loaded)
                return;
            _loaded = true;
            try
            {
                if (File.Exists(CacheFile) && JsonSerializer.Deserialize(File.ReadAllText(CacheFile), ModelCardJson.Default.DictionaryStringCachedCard) is { } saved)
                    foreach (var (key, entry) in saved)
                        _cards[key] = (entry.Card, entry.At);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
            try
            {
                if (File.Exists(HashFile) && JsonSerializer.Deserialize(File.ReadAllText(HashFile), ModelCardJson.Default.DictionaryStringString) is { } hashes)
                    foreach (var (key, hash) in hashes)
                        _hashes[key] = hash;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
        }
    }

    private void SaveHashes()
    {
        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(stateDirectory);
                AtomicFile.Write(HashFile, JsonSerializer.Serialize(new Dictionary<string, string>(_hashes, StringComparer.OrdinalIgnoreCase), ModelCardJson.Default.DictionaryStringString));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    private void Save()
    {
        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(stateDirectory);
                var all = _cards.ToDictionary(kv => kv.Key, kv => new CachedCard(kv.Value.Card, kv.Value.At));
                AtomicFile.Write(CacheFile, JsonSerializer.Serialize(all, ModelCardJson.Default.DictionaryStringCachedCard));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    [GeneratedRegex(@"(?i)<\s*(br\s*/?|/p|/li|/h[1-6]|/div|/tr|/blockquote)\s*>")]
    private static partial Regex BlockEnd();

    [GeneratedRegex(@"<[^>]*>")]
    private static partial Regex Tag();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();

    [GeneratedRegex(@"(?i)\b(recommended|suggested|settings?|sampler|steps|cfg|clip ?skip|resolution)\b")]
    private static partial Regex StrongSettingsWord();

    [GeneratedRegex(@"(?i)\b(negative|positive|prompts?|tags?|trigger|hires|highres\w*|upscal\w*|denois\w*|scheduler|vae|weight)\b")]
    private static partial Regex SettingsWord();

    [GeneratedRegex(@"(\d{2,5})\s*x\s*(\d{2,5})")]
    private static partial Regex SizeText();

    [GeneratedRegex(@"(?i)\bSD\s*([123])(\.\d+)?\b")]
    private static partial Regex SdVersion();

    [GeneratedRegex(@"(?i)pony|illustrious|noob|animagine|playground|sdxl")]
    private static partial Regex SdxlFamily();

    [GeneratedRegex(@"(?i)pony|illustrious|noob|animagine|playground|sdxl|flux|kolors|pixart|hidream|chroma|lumina|aura ?flow|qwen|z-?image|hunyuan ?(image|dit)")]
    private static partial Regex PictureModel();
}

/// <summary>A card as kept on disk, with when it was asked for (a missing one is asked for again after a while).</summary>
public sealed record CachedCard(ModelCard? Card, DateTime At);

[System.Text.Json.Serialization.JsonSourceGenerationOptions(PropertyNamingPolicy = System.Text.Json.Serialization.JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[System.Text.Json.Serialization.JsonSerializable(typeof(Dictionary<string, CachedCard>))]
[System.Text.Json.Serialization.JsonSerializable(typeof(Dictionary<string, string>))]
internal sealed partial class ModelCardJson : System.Text.Json.Serialization.JsonSerializerContext;
