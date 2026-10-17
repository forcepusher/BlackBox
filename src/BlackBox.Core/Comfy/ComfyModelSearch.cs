using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using BlackBox.Core.Web;

namespace BlackBox.Core.Comfy;

/// <summary>ComfyUI's model folders: the names other programs use for them, and the node that loads what is in each.</summary>
public static class ComfyModelFolders
{
    /// <summary>The folder name ComfyUI uses for one given loosely: "lora" and "Lora" are loras, AUTOMATIC1111's "Stable-diffusion" is checkpoints.</summary>
    public static string Normalize(string folder)
    {
        var f = folder.Trim().Trim('/', '\\').Replace('\\', '/');
        if (f.StartsWith("models/", StringComparison.OrdinalIgnoreCase))
            f = f[7..];
        return f.ToLowerInvariant() switch
        {
            "checkpoint" or "ckpt" or "stable-diffusion" or "stable_diffusion" or "models" or "" => "checkpoints",
            "lora" or "lycoris" or "locon" => "loras",
            "upscaler" or "upscalers" or "upscale" or "esrgan" or "realesrgan" or "upscale_model" => "upscale_models",
            "embedding" or "textual_inversion" or "textualinversion" => "embeddings",
            "unet" or "diffusion_model" => "diffusion_models",
            "clip" or "text_encoder" => "text_encoders",
            "controlnets" or "control_net" => "controlnet",
            "vaes" => "vae",
            var other => other,
        };
    }

    /// <summary>How a model in a folder is used, said after it is downloaded.</summary>
    public static string Use(string folder, string file) => folder switch
    {
        "checkpoints" => $"Load it with CheckpointLoaderSimple (ckpt_name \"{file}\"), as blackbox/text_to_image and blackbox/image_to_image do in 1.ckpt_name.",
        "loras" => $"Load it with a LoraLoader node (lora_name \"{file}\") between the checkpoint and the rest; its page names its trigger words, which go in the prompt.",
        "vae" => $"Load it with VAELoader (vae_name \"{file}\") and use it instead of the checkpoint's VAE.",
        "upscale_models" => $"Load it with UpscaleModelLoader (model_name \"{file}\") and use it with ImageUpscaleWithModel.",
        "controlnet" => $"Load it with ControlNetLoader (control_net_name \"{file}\") and apply it with ControlNetApplyAdvanced.",
        "embeddings" => $"Use it by writing embedding:{Path.GetFileNameWithoutExtension(file)} in a prompt.",
        "diffusion_models" => $"Load it with UNETLoader (unet_name \"{file}\").",
        "text_encoders" => $"Load it with CLIPLoader or DualCLIPLoader (clip_name \"{file}\").",
        _ => $"It is in ComfyUI's {folder} folder; look up the node that loads it with action=nodes.",
    };
}

/// <summary>A model file found online: what it is, how big, and the link that downloads it.</summary>
/// <param name="Title">The model's name: "DreamShaper (8)", or a Hugging Face repository.</param>
/// <param name="Kind">checkpoint, LoRA, VAE, upscaler, ControlNet, embedding, diffusion model, text encoder…</param>
/// <param name="Base">The model it is made for or from: SD 1.5, SDXL, Flux.1 D…; null when not said.</param>
/// <param name="Folder">The ComfyUI models folder it goes in: checkpoints, loras, vae, upscale_models…</param>
/// <param name="Note">Anything else worth knowing: "fp16, pruned", "early access".</param>
public sealed record OnlineModel(string Title, string Source, string Page, string Kind, string? Base, string File, long? Size,
    string Url, string Folder, string? Sha256, long Downloads, string? Note);

/// <summary>
/// Finds models for ComfyUI on Hugging Face and Civitai by their APIs, which give each file's size,
/// SHA-256 and direct link, and turns a page's link into the file's (a Civitai model page, a
/// Hugging Face "blob" page). Asked before, qwen3.8 found a model with web_search and read pages
/// to get its link; this answers in one call with what fits.
/// </summary>
public static partial class ComfyModelSearch
{
    /// <summary>The sites' addresses; tests point them at a stand-in server.</summary>
    internal static string CivitaiBase { get; set; } = "https://civitai.com";
    internal static string HuggingFaceBase { get; set; } = "https://huggingface.co";

    /// <summary>The kinds a search can be narrowed to, as the model names them, with Civitai's types and ComfyUI's folder.</summary>
    private static readonly (string Kind, string[] Civitai, string Folder)[] Kinds =
    [
        ("checkpoint", ["Checkpoint"], "checkpoints"),
        ("lora", ["LORA", "LoCon", "DoRA"], "loras"),
        ("vae", ["VAE"], "vae"),
        ("upscaler", ["Upscaler"], "upscale_models"),
        ("controlnet", ["Controlnet"], "controlnet"),
        ("embedding", ["TextualInversion"], "embeddings"),
    ];

    /// <summary>The Civitai types and the folder for a kind the model asked for ("loras", "LoRA", "upscale" all work); null for any kind.</summary>
    internal static (string Kind, string[] Civitai, string Folder)? KindOf(string? kind)
    {
        var k = (kind ?? "").Trim().ToLowerInvariant().TrimEnd('s');
        if (k is "" or "any" or "all" or "model")
            return null;
        foreach (var x in Kinds)
            if (x.Kind == k || x.Folder.TrimEnd('s') == k || k.Length >= 3 && x.Kind.StartsWith(k, StringComparison.Ordinal))
                return x;
        return k switch
        {
            "textual inversion" or "textualinversion" => Kinds[5],
            "upscale" or "upscale_model" or "esrgan" => Kinds[3],
            _ => null,
        };
    }

    /// <summary>
    /// Model files for <paramref name="query"/> on both sites, the most downloaded first on each, up
    /// to <paramref name="max"/> together. A site that fails is said in <paramref name="problems"/>.
    /// </summary>
    public static async Task<List<OnlineModel>> SearchAsync(string query, string? kind, string? baseModel, int max, List<string> problems, CancellationToken ct)
    {
        var wanted = KindOf(kind);
        var civitai = SafelyAsync("Civitai", () => CivitaiAsync(query, wanted, baseModel, ct), problems);
        var huggingFace = SafelyAsync("Hugging Face", () => HuggingFaceAsync(query, wanted, baseModel, ct), problems);
        var (a, b) = (await civitai.ConfigureAwait(false), await huggingFace.ConfigureAwait(false));
        var all = new List<OnlineModel>();
        for (var i = 0; all.Count < max && (i < a.Count || i < b.Count); i++)
        {
            if (i < a.Count) all.Add(a[i]);
            if (i < b.Count && all.Count < max) all.Add(b[i]);
        }
        return all;
    }

    private static async Task<List<OnlineModel>> SafelyAsync(string site, Func<Task<List<OnlineModel>>> search, List<string> problems)
    {
        try
        {
            return await search().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or System.Text.Json.JsonException or TimeoutException or InvalidOperationException
                                       or OperationCanceledException { CancellationToken.IsCancellationRequested: false })
        {
            lock (problems) problems.Add($"{site} couldn't be searched: {ex.Message.Clip(160)}");
            return [];
        }
    }

    internal static async Task<JsonNode?> GetJsonAsync(string url, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            using var response = await Downloader.Http.GetAsync(url, timeout.Token).ConfigureAwait(false);
            var text = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException($"{(int)response.StatusCode} {response.ReasonPhrase}");
            return JsonNode.Parse(text);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException("no answer within 30 s");
        }
    }

    // ---- Civitai --------------------------------------------------------------------------------

    private static async Task<List<OnlineModel>> CivitaiAsync(string query, (string Kind, string[] Civitai, string Folder)? kind, string? baseModel, CancellationToken ct)
    {
        var url = CivitaiBase + "/api/v1/models?limit=12&sort=Most%20Downloaded&nsfw=false&query=" + Uri.EscapeDataString(query);
        foreach (var type in kind?.Civitai ?? [])
            url += "&types=" + Uri.EscapeDataString(type);
        var found = new List<OnlineModel>();
        foreach (var item in (await GetJsonAsync(url, ct).ConfigureAwait(false))?["items"] as JsonArray ?? [])
        {
            if (item is not JsonObject model || (bool?)model["nsfw"] == true)
                continue;
            var version = (model["modelVersions"] as JsonArray ?? []).OfType<JsonObject>().FirstOrDefault(v => baseModel is null || SameBase((string?)v["baseModel"], baseModel));
            if (version is null)
                continue;
            var files = (version["files"] as JsonArray ?? []).OfType<JsonObject>().ToList();
            var file = files.FirstOrDefault(f => (bool?)f["primary"] == true) ?? files.FirstOrDefault(f => (string?)f["type"] == "Model");
            if (file is null || (string?)file["downloadUrl"] is not { } link || (string?)file["name"] is not { } name)
                continue;
            var type = (string?)model["type"] ?? "";
            var folder = Kinds.FirstOrDefault(k => k.Civitai.Contains(type, StringComparer.OrdinalIgnoreCase)).Folder ?? FolderFor(name, null);
            var meta = file["metadata"] as JsonObject;
            var note = string.Join(", ", new[] { (string?)meta?["fp"], (string?)meta?["size"] }.Where(s => !string.IsNullOrEmpty(s)));
            if ((string?)version["availability"] is "EarlyAccess")
                note = (note.Length > 0 ? note + ", " : "") + "early access: may need a paid Civitai login";
            var title = (string?)model["name"] ?? name;
            if ((string?)version["name"] is { Length: > 0 } versionName && !title.Contains(versionName, StringComparison.OrdinalIgnoreCase))
                title += $" ({versionName})";
            found.Add(new OnlineModel(title, "Civitai", $"{CivitaiBase}/models/{(long?)model["id"]}", KindName(folder, type), (string?)version["baseModel"], name,
                (long?)((double?)file["sizeKB"] * 1024), link, folder, (string?)file["hashes"]?["SHA256"], (long?)model["stats"]?["downloadCount"] ?? 0,
                note.Length > 0 ? note : null));
        }
        return found;
    }

    // ---- Hugging Face ---------------------------------------------------------------------------

    /// <summary>Folders of a diffusers-format repository: parts of a model, not files ComfyUI loads alone.</summary>
    private static readonly HashSet<string> DiffusersParts = new(StringComparer.OrdinalIgnoreCase)
        { "unet", "text_encoder", "text_encoder_2", "text_encoder_3", "vae", "transformer", "tokenizer", "tokenizer_2", "scheduler", "image_encoder", "safety_checker", "feature_extractor", "vae_decoder", "vae_encoder" };

    private static async Task<List<OnlineModel>> HuggingFaceAsync(string query, (string Kind, string[] Civitai, string Folder)? kind, string? baseModel, CancellationToken ct)
    {
        var search = await GetJsonAsync(HuggingFaceBase + "/api/models?sort=downloads&direction=-1&limit=30&search=" + Uri.EscapeDataString(query), ct).ConfigureAwait(false);
        var repos = (search as JsonArray ?? []).OfType<JsonObject>()
            .Where(r => (string?)r["id"] is not null && LooksLikeImageModel(r, kind))
            .Take(6)
            .ToList();
        var details = await Task.WhenAll(repos.Select(async r =>
        {
            try { return await GetJsonAsync($"{HuggingFaceBase}/api/models/{(string)r["id"]!}?blobs=true", ct).ConfigureAwait(false) as JsonObject; }
            catch (Exception ex) when (ex is HttpRequestException or TimeoutException or System.Text.Json.JsonException) { return null; }
        })).ConfigureAwait(false);

        var words = ComfyNodeLibrary.Words(query);
        var found = new List<OnlineModel>();
        foreach (var repo in details.OfType<JsonObject>())
        {
            var id = (string)repo["id"]!;
            if (repo["gated"] is JsonValue g && g.TryGetValue<string>(out var gate) && gate is "auto" or "manual")
                continue; // a login and an accepted license first
            var tags = (repo["tags"] as JsonArray ?? []).Select(t => (string?)t).OfType<string>().ToList();
            var repoBase = BaseFromTags(tags) ?? BaseFromName(id);
            var files = (repo["siblings"] as JsonArray ?? []).OfType<JsonObject>()
                .Select(s => (Path: (string?)s["rfilename"] ?? "", Size: (long?)s["size"] ?? (long?)s["lfs"]?["size"], Sha: (string?)s["lfs"]?["sha256"]))
                .Where(f => ModelFile().IsMatch(f.Path) && UsableAlone(f.Path))
                .Select(f => (f.Path, f.Size, f.Sha, Folder: FolderFor(f.Path, tags), Base: BaseFromName(f.Path) ?? repoBase))
                .Where(f => kind is null || f.Folder == kind.Value.Folder)
                .Where(f => baseModel is null || f.Base is null || SameBase(f.Base, baseModel))
                .OrderByDescending(f => Rank(f.Path, words))
                .ThenByDescending(f => f.Path, NaturalOrder.Instance)
                .Take(3);
            foreach (var f in files)
                found.Add(new OnlineModel(id, "Hugging Face", HuggingFaceBase + "/" + id, KindName(f.Folder, null), f.Base, f.Path, f.Size,
                    $"{HuggingFaceBase}/{id}/resolve/main/{string.Join('/', f.Path.Split('/').Select(Uri.EscapeDataString))}",
                    f.Folder, f.Sha, (long?)repo["downloads"] ?? 0, null));
        }
        return found;
    }

    private static bool LooksLikeImageModel(JsonObject repo, (string Kind, string[] Civitai, string Folder)? kind)
    {
        var pipeline = (string?)repo["pipeline_tag"];
        var tags = string.Join(" ", (repo["tags"] as JsonArray ?? []).Select(t => (string?)t)).ToLowerInvariant();
        if (pipeline is "text-to-image" or "image-to-image" or "unconditional-image-generation")
            return true;
        if (kind?.Folder == "upscale_models" && (tags.Contains("super-resolution") || tags.Contains("esrgan") || tags.Contains("upscal")))
            return true;
        return tags.Contains("stable-diffusion") || tags.Contains("comfyui") || tags.Contains("diffusion-single-file") || tags.Contains("lora") && tags.Contains("diffusers");
    }

    /// <summary>A file ComfyUI loads by itself: at the repository's top, or in a folder named as ComfyUI's are; not a part of a diffusers model.</summary>
    private static bool UsableAlone(string path)
    {
        var parts = path.Split('/');
        return parts.Length == 1 || !parts.SkipLast(1).Any(DiffusersParts.Contains);
    }

    private static int Rank(string path, IReadOnlyList<string> words)
    {
        var name = ComfyNodeLibrary.Squash(Path.GetFileName(path));
        var rank = path.EndsWith(".safetensors", StringComparison.OrdinalIgnoreCase) ? 4 : 0;
        if (name.Contains("inpaint") && !words.Contains("inpaint") && !words.Contains("inpainting")) rank -= 3;
        if (name.Contains("pruned") || name.Contains("fp16")) rank += 1;
        rank += words.Count(w => name.Contains(w)) * 2;
        return rank;
    }

    [GeneratedRegex(@"\.(safetensors|ckpt|pt|pth|gguf|sft)$", RegexOptions.IgnoreCase)]
    private static partial Regex ModelFile();

    // ---- what a file is -------------------------------------------------------------------------

    private static readonly string[] Folders = ["checkpoints", "loras", "vae", "upscale_models", "controlnet", "embeddings", "diffusion_models", "unet",
        "text_encoders", "clip", "clip_vision", "style_models", "gligen", "hypernetworks", "photomaker", "audio_encoders", "model_patches", "latent_upscale_models"];

    /// <summary>The ComfyUI models folder a file goes in, from its path (Comfy-Org's split_files/&lt;folder&gt;/…), name and the repository's tags.</summary>
    public static string FolderFor(string path, IReadOnlyList<string>? tags)
    {
        var parts = path.Replace('\\', '/').Split('/');
        foreach (var dir in parts.SkipLast(1).Reverse())
            if (Folders.FirstOrDefault(f => f.Equals(dir, StringComparison.OrdinalIgnoreCase) || f.TrimEnd('s').Equals(dir, StringComparison.OrdinalIgnoreCase)) is { } folder)
                return folder == "unet" ? "diffusion_models" : folder == "clip" ? "text_encoders" : folder;
        var name = Path.GetFileName(path).ToLowerInvariant();
        var tagText = string.Join(" ", tags ?? []).ToLowerInvariant();
        if (name.Contains("lora") || tagText.Contains("lora")) return "loras";
        if (Upscaler().IsMatch(name) || tagText.Contains("super-resolution")) return "upscale_models";
        if (name.Contains("controlnet") || name.StartsWith("control_", StringComparison.Ordinal) || tagText.Contains("controlnet")) return "controlnet";
        // sdxl_vae.safetensors, vae-ft-mse-840000…; not DreamShaper_4NoVae or …_fixedVae_half, checkpoints with a VAE in them.
        if (Vae().IsMatch(name) && !name.Contains("baked")) return "vae";
        return "checkpoints";
    }

    [GeneratedRegex(@"(^|[^a-z])vae([^a-z]|$)")]
    private static partial Regex Vae();

    [GeneratedRegex(@"esrgan|upscal|swinir|\b[248]x[_-]|[_-]x[248]\b|^[248]x", RegexOptions.IgnoreCase)]
    private static partial Regex Upscaler();

    private static string KindName(string folder, string? civitaiType) => civitaiType switch
    {
        "LORA" or "LoCon" or "DoRA" => "LoRA",
        "TextualInversion" => "embedding",
        _ => folder switch
        {
            "checkpoints" => "checkpoint",
            "loras" => "LoRA",
            "vae" => "VAE",
            "upscale_models" => "upscaler",
            "controlnet" => "ControlNet",
            "embeddings" => "embedding",
            "diffusion_models" => "diffusion model",
            "text_encoders" => "text encoder",
            "clip_vision" => "CLIP vision model",
            _ => folder.TrimEnd('s').Replace('_', ' '),
        },
    };

    /// <summary>The base model a Hugging Face repository is made from, by its base_model tag or its kind of pipeline.</summary>
    private static string? BaseFromTags(IReadOnlyList<string> tags)
    {
        foreach (var tag in tags.Where(t => t.StartsWith("base_model:", StringComparison.OrdinalIgnoreCase)))
            if (BaseFromName(tag) is { } known)
                return known;
        var all = string.Join(" ", tags).ToLowerInvariant();
        return all.Contains("stable-diffusion-xl") || all.Contains("sdxl") ? "SDXL" : all.Contains("flux") ? "Flux" : null;
    }

    /// <summary>The base model a name says: "v1-5", "sd15", "sd_xl", "flux1-dev"…</summary>
    internal static string? BaseFromName(string name)
    {
        var n = name.ToLowerInvariant();
        // "DreamShaperXL1.0", "sd_xl_base": xl not followed by a letter.
        var xl = n.Contains("sdxl") || n.Contains("stable-diffusion-xl") || Regex.IsMatch(n, @"xl(?![a-z])");
        if (xl && n.Contains("turbo")) return "SDXL Turbo";
        if (xl) return "SDXL";
        if (n.Contains("sd-turbo") || n.Contains("sd_turbo")) return "SD 2.1 Turbo";
        if (n.Contains("flux.1-dev") || n.Contains("flux1-dev") || n.Contains("flux-dev")) return "Flux.1 D";
        if (n.Contains("flux.1-schnell") || n.Contains("flux1-schnell") || n.Contains("flux-schnell")) return "Flux.1 S";
        if (n.Contains("flux")) return "Flux";
        if (n.Contains("stable-diffusion-v1-5") || n.Contains("v1-5") || n.Contains("sd15") || n.Contains("sd1.5") || n.Contains("sd_1.5") || n.Contains("sd-1-5")
            || n.Contains("stable-diffusion-v1") || n.Contains("compvis/stable-diffusion")) return "SD 1.5";
        if (n.Contains("stable-diffusion-2") || n.Contains("sd21") || n.Contains("v2-1")) return "SD 2.1";
        if (n.Contains("sd3")) return "SD 3";
        return null;
    }

    /// <summary>Whether a base model is the one asked for: "sd 1.5", "SD1.5" and "1.5" all mean Civitai's "SD 1.5".</summary>
    internal static bool SameBase(string? have, string wanted)
    {
        if (have is null) return false;
        static string Plain(string s) => Regex.Replace(s.ToLowerInvariant(), @"[^a-z0-9.]", "").Replace("stablediffusion", "sd");
        var (h, w) = (Plain(have), Plain(wanted));
        if (w is "15" or "1.5") w = "sd1.5";
        return h.Contains(w) || w.Contains(h) && h.Length >= 3;
    }

    // ---- links ----------------------------------------------------------------------------------

    /// <summary>
    /// The file a link means, ready to download: a Hugging Face page ("…/blob/main/x.safetensors")
    /// becomes its download link; a Civitai model page ("…/models/4384?modelVersionId=128713", or
    /// without a version for the newest) becomes its main file's, with the name, folder and
    /// SHA-256 Civitai gives. Other links stay as they are.
    /// </summary>
    public static async Task<OnlineModel?> ResolveAsync(Uri link, CancellationToken ct)
    {
        var host = link.Authority.ToLowerInvariant();
        if ((host.EndsWith("huggingface.co", StringComparison.Ordinal) || host == new Uri(HuggingFaceBase).Authority) && HuggingFaceFile().Match(link.AbsolutePath) is { Success: true } hf)
            return await HuggingFaceFileAsync(hf.Groups[1].Value, hf.Groups[2].Value, hf.Groups[3].Value, ct).ConfigureAwait(false);
        if (!host.EndsWith("civitai.com", StringComparison.Ordinal) && host != new Uri(CivitaiBase).Authority)
            return null;
        var path = link.AbsolutePath;
        var versionId = CivitaiVersion().Match(link.Query) is { Success: true } q ? q.Groups[1].Value
            : CivitaiDownload().Match(path) is { Success: true } d ? d.Groups[1].Value : null;
        JsonObject? version = null;
        JsonObject? model = null;
        if (versionId is not null)
            version = await GetJsonAsync($"{CivitaiBase}/api/v1/model-versions/{versionId}", ct).ConfigureAwait(false) as JsonObject;
        else if (CivitaiModel().Match(path) is { Success: true } m)
        {
            model = await GetJsonAsync($"{CivitaiBase}/api/v1/models/{m.Groups[1].Value}", ct).ConfigureAwait(false) as JsonObject;
            version = (model?["modelVersions"] as JsonArray)?.OfType<JsonObject>().FirstOrDefault();
        }
        if (version is null)
            return null;
        var files = (version["files"] as JsonArray ?? []).OfType<JsonObject>().ToList();
        var file = files.FirstOrDefault(f => (bool?)f["primary"] == true) ?? files.FirstOrDefault();
        if (file is null || (string?)file["downloadUrl"] is not { } url || (string?)file["name"] is not { } name)
            return null;
        var type = (string?)model?["type"] ?? (string?)version["model"]?["type"] ?? "";
        var folder = Kinds.FirstOrDefault(k => k.Civitai.Contains(type, StringComparer.OrdinalIgnoreCase)).Folder ?? FolderFor(name, null);
        return new OnlineModel((string?)model?["name"] ?? (string?)version["model"]?["name"] ?? name, "Civitai", link.ToString(), KindName(folder, type),
            (string?)version["baseModel"], name, (long?)((double?)file["sizeKB"] * 1024), url, folder, (string?)file["hashes"]?["SHA256"], 0, null);
    }

    /// <summary>
    /// A file of a Hugging Face repository (from its …/blob/… page or its …/resolve/… link): its
    /// download link, and its size and SHA-256 from the repository's file list, to check it by.
    /// </summary>
    private static async Task<OnlineModel> HuggingFaceFileAsync(string repo, string revision, string path, CancellationToken ct)
    {
        var file = Uri.UnescapeDataString(path);
        string? sha = null;
        long? size = null;
        try
        {
            var info = await GetJsonAsync($"{HuggingFaceBase}/api/models/{repo}{(revision == "main" ? "" : "/revision/" + revision)}?blobs=true", ct).ConfigureAwait(false);
            if ((info?["siblings"] as JsonArray ?? []).OfType<JsonObject>().FirstOrDefault(s => (string?)s["rfilename"] == file) is { } sibling)
                (sha, size) = ((string?)sibling["lfs"]?["sha256"], (long?)sibling["size"]);
        }
        catch (Exception ex) when (ex is HttpRequestException or TimeoutException or System.Text.Json.JsonException) { }
        var url = $"{HuggingFaceBase}/{repo}/resolve/{revision}/{path}";
        return new OnlineModel(repo, "Hugging Face", $"{HuggingFaceBase}/{repo}", KindName(FolderFor(file, null), null), BaseFromName(repo + "/" + file),
            Path.GetFileName(file), size, url, FolderFor(file, null), sha, 0, null);
    }

    /// <summary>A file in a model repository: /&lt;owner&gt;/&lt;repo&gt;/blob|resolve/&lt;revision&gt;/&lt;path&gt; (not a dataset's or a space's).</summary>
    [GeneratedRegex(@"^/(?!datasets/|spaces/)([^/]+/[^/]+)/(?:blob|resolve)/([^/]+)/(.+)$")]
    private static partial Regex HuggingFaceFile();

    [GeneratedRegex(@"[?&]modelVersionId=(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex CivitaiVersion();

    [GeneratedRegex(@"/api/download/models/(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex CivitaiDownload();

    [GeneratedRegex(@"/models/(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex CivitaiModel();

    /// <summary>Names with numbers in them by their numbers: DreamShaper_8 after DreamShaper_7 and DreamShaper_10 after both.</summary>
    private sealed partial class NaturalOrder : IComparer<string>
    {
        public static readonly NaturalOrder Instance = new();

        public int Compare(string? x, string? y)
        {
            var a = Parts().Matches(x ?? "");
            var b = Parts().Matches(y ?? "");
            for (var i = 0; i < Math.Min(a.Count, b.Count); i++)
            {
                var (p, q) = (a[i].Value, b[i].Value);
                var c = long.TryParse(p, out var pn) && long.TryParse(q, out var qn) ? pn.CompareTo(qn) : string.Compare(p, q, StringComparison.OrdinalIgnoreCase);
                if (c != 0) return c;
            }
            return a.Count.CompareTo(b.Count);
        }

        [GeneratedRegex(@"\d{1,18}|\D+")]
        private static partial Regex Parts();
    }
}
