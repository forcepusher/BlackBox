using System.Text;
using System.Text.Json.Nodes;
using BlackBox.Core.Comfy;
using BlackBox.Core.Media;

namespace BlackBox.Core.Tools;

/// <summary>
/// Pictures, video, sound, music, speech and 3D models through ComfyUI on this PC. The model
/// builds workflows on the fly, but never from memory alone: it starts from a template (the
/// user's saved workflows, ComfyUI's templates, custom node examples), which ComfyUI's own page
/// turns into the API format, or looks node and model names up on the running server; every
/// workflow is checked against the server's node types before it runs, with the right name next
/// to each wrong one. qwen3.8 drove ComfyUI's HTTP API well but wrote wrong node, input and model
/// names from memory. A run waits for the job and hands back its files, with a look at them.
/// </summary>
public sealed partial class ComfyTool : Tool
{
    public override string Name => "comfyui";

    public override string Description =>
        "Make pictures, video, sound, music, speech and 3D models with ComfyUI on this PC. The user's own is found and used " +
        "(one already running, a portable build, the Desktop app, an install of their own), started for you if needed; " +
        "only when the PC has none, action=install installs one (the portable build that suits its graphics card, or its CPU). " +
        "A workflow is JSON in ComfyUI's API format: {\"node id\": {\"class_type\": \"…\", \"inputs\": {\"name\": a value, or [\"other node id\", output index]}}}. " +
        "Never write node, input or model names from memory: start from a template (action=templates with search finds them; with name it shows one " +
        "as a workflow, saved to a file you can edit), or look names up (action=nodes with search or names, action=models). " +
        "action=run runs a workflow (template, workflow_file, or workflow itself), changed by set {\"node id.input\": value}, e.g. " +
        "{\"6.text\": \"a red fox in snow\", \"3.seed\": 42}; it is checked first, then it waits until the job is done and gives you the files " +
        "with a look at them. For an input picture, sound or video, put its full local path where LoadImage/LoadAudio/LoadVideo name a file: it is uploaded. " +
        "A picture the user gives you to follow (a character, a reference sheet, a style) is where the result starts, not something to describe in words: " +
        "image to image from it (blackbox/image_to_image; denoise 0.5 to 0.7 keeps the character), or a reference or edit model's template when one is installed; " +
        "crop or scale it inside the workflow (ImageCrop, ImageScaleToTotalPixels), not with a script. " +
        "To change a picture or restyle it (put a hat on, make a photo anime or a painting), or to draw the character it shows in a new scene or pose, " +
        "an installed edit model's template (templates search \"edit\": Qwen-Image-Edit, Flux Kontext) does what is asked and keeps the subject, with no download; " +
        "image to image keeps the photo's look at low denoise and loses the subject at high. " +
        "A checkpoint's or LoRA's card (with action=models search=its file name, a template's view, or after its first run) says how its maker uses it: " +
        "the prompt style, size, sampler, CFG and negative prompt to start from. " +
        "Change the seed for a different result. A model a workflow lacks: action=download with its url (a template lists the links of its models); " +
        "action=find_models searches Civitai and Hugging Face for others (a style, a checkpoint the user named, a LoRA, an upscaler) with their sizes and links, " +
        "and what the month's most downloaded on Civitai are for those words; without search it lists this month's most downloaded checkpoints " +
        "(kind=lora: LoRAs) and the base models they are on, so you know what is current before you pick one. " +
        "action=status shows the server, GPU and queue (with prompt_id: waits for that job); " +
        "action=free unloads its models to free the GPU; action=restart restarts it (after installing custom nodes into its custom_nodes folder); action=stop stops it.";

    public override ToolRisk Risk => ToolRisk.Risky;

    public override JsonObject Parameters => Schema(
        ("action", Str("What to do", "templates", "nodes", "models", "run", "status", "install", "find_models", "download", "free", "restart", "stop"), true),
        ("search", Str("Words to look for: in templates (\"video from image\"), node types (\"wan video\"), installed model files (\"lora\"), or models online (find_models: \"furry\", \"watercolor\", \"DreamShaper\"; none for this month's most downloaded)"), false),
        ("name", Str("A template's name as action=templates lists it: to see it (templates) or run it (run)"), false),
        ("names", StrArray("Node types to show in full: inputs, their types and choices, outputs (nodes)"), false),
        ("workflow", new JsonObject { ["description"] = "A workflow in API format to run (a JSON object or its text)" }, false),
        ("workflow_file", Str("A .json workflow file to run (API format, or as ComfyUI's editor saves it)"), false),
        ("set", new JsonObject { ["type"] = "object", ["description"] = "Changes before the run: {\"node id.input\": value}, e.g. {\"6.text\": \"a cat\", \"129:98.length\": 49}" }, false),
        ("save_to", Str("A folder the user named to copy the files the run makes into; without it they stay in ComfyUI's output folder"), false),
        ("wait_minutes", Num("How long to wait for the job (default 30); it keeps running after that, and action=status with its prompt_id waits again"), false),
        ("prompt_id", Str("A job's id, from an earlier run that was still going (status)"), false),
        ("kind", Str("find_models: the kind of model (checkpoint, lora, vae, upscaler, controlnet, embedding); any kind without it"), false),
        ("base", Str("find_models: the model it should be made for, e.g. \"SD 1.5\", \"SDXL\", \"Flux\""), false),
        ("url", Str("download: the model file's link, as find_models or a template gives it (a Civitai or Hugging Face page works too)"), false),
        ("model_folder", Str("download: the models folder it goes in (checkpoints, loras, vae, upscale_models, controlnet, embeddings, diffusion_models, text_encoders…); find_models gives it"), false),
        ("folder", Str("ComfyUI's folder, if it isn't found by itself or another one should be used (install: where to install it, else BlackBox's Data\\tools)"), false));

    public override string Describe(JsonObject args)
    {
        var action = GetString(args, "action") ?? "";
        return action switch
        {
            "templates" => GetString(args, "name") is { } n ? $"ComfyUI template {n}" : $"ComfyUI templates: {GetString(args, "search") ?? "all"}",
            "nodes" => GetStringList(args, "names") is { Count: > 0 } names ? $"ComfyUI nodes {string.Join(", ", names)}" : $"ComfyUI nodes: {GetString(args, "search")}",
            "models" => $"ComfyUI models: {GetString(args, "search") ?? "all"}",
            "run" => "ComfyUI run " + (GetString(args, "name") ?? GetString(args, "workflow_file") ?? "a workflow") +
                     (args["set"] is JsonObject set && set.Count > 0 ? " with " + set.ToJsonString(ComfyWorkflow.Compact).Clip(160) : ""),
            "status" => GetString(args, "prompt_id") is { } id ? $"ComfyUI job {id}" : "ComfyUI status",
            "install" => "Install ComfyUI" + (GetString(args, "folder") is { } f ? $" in {f}" : ""),
            "find_models" => $"Models online: {GetString(args, "search")}" + (GetString(args, "kind") is { } k ? $" ({k})" : ""),
            "download" => $"Download {GetString(args, "url")}" + (GetString(args, "model_folder") is { } m ? $" into {m}" : ""),
            _ => "ComfyUI " + action,
        };
    }

    // Whether this task uses the server, and has run jobs on it.
    private bool _usesServer, _ranJobs;

    public override async Task<ToolResult> ExecuteAsync(JsonObject args, ToolContext ctx, CancellationToken ct)
    {
        if (ctx.Comfy is not { } hub)
            return ToolResult.Error("ComfyUI isn't available in this run.");
        // From before it may be started: when the task is over, a ComfyUI BlackBox started is closed.
        if (!_usesServer)
        {
            _usesServer = true;
            hub.Use();
            ctx.AfterRun(nameof(ComfyTool), () =>
            {
                var ranJobs = _ranJobs;
                (_usesServer, _ranJobs) = (false, false);
                return hub.ReleaseAsync(ranJobs);
            });
        }
        var action = RequireString(args, "action").Trim().ToLowerInvariant();
        var notes = new List<string>();
        try
        {
            switch (action)
            {
                case "stop":
                    return new ToolResult(await hub.RestartAsync(start: false, ctx.Progress, ct).ConfigureAwait(false));
                case "restart":
                    return new ToolResult(await hub.RestartAsync(start: true, ctx.Progress, ct).ConfigureAwait(false));
                case "install":
                    return await InstallAsync(args, hub, ctx, ct).ConfigureAwait(false);
                case "find_models" or "find_model" or "search_models":
                    return await FindModelsAsync(args, hub, ct).ConfigureAwait(false);
                case "download" or "download_model":
                    return await DownloadAsync(args, hub, ctx, ct).ConfigureAwait(false);
            }
            var api = await hub.ConnectAsync(GetString(args, "folder"), notes, ctx.Progress, ct).ConfigureAwait(false);
            var result = action switch
            {
                "status" => await StatusAsync(args, hub, api, ctx, ct).ConfigureAwait(false),
                "free" => await FreeAsync(api, ct).ConfigureAwait(false),
                "nodes" => await NodesAsync(args, hub, api, ct).ConfigureAwait(false),
                "models" => await ModelsAsync(args, hub, api, ct).ConfigureAwait(false),
                "templates" => await TemplatesAsync(args, hub, api, ctx, ct).ConfigureAwait(false),
                "run" => await RunAsync(args, hub, api, ctx, ct).ConfigureAwait(false),
                _ => throw new ToolArgumentException("action must be templates, nodes, models, run, status, install, find_models, download, free, restart or stop."),
            };
            return notes.Count == 0 ? result : result with { Text = string.Join("\n", notes) + "\n" + result.Text };
        }
        catch (ComfyApiException ex)
        {
            return ToolResult.Error(string.Join("\n", notes.Append(ex.Message)));
        }
        catch (ComfyWorkflowException ex)
        {
            return ToolResult.Error(ex.Message);
        }
        catch (Web.DownloadException ex)
        {
            return ToolResult.Error(ex.Message);
        }
    }

    // ---- install, find_models, download ------------------------------------------------------

    private static async Task<ToolResult> InstallAsync(JsonObject args, ComfyHub hub, ToolContext ctx, CancellationToken ct)
    {
        var said = await hub.InstallAsync(GetString(args, "folder"), ctx.Progress, ct).ConfigureAwait(false);
        var notes = new List<string> { said };
        // Started at once, so a problem shows now rather than at the first job.
        var api = await hub.ConnectAsync(null, notes, ctx.Progress, ct).ConfigureAwait(false);
        var models = await hub.ModelsAsync(api, ct, fresh: true).ConfigureAwait(false);
        var count = models.Where(kv => kv.Key is not ("custom_nodes" or "configs")).Sum(kv => kv.Value.Count);
        var (device, cpu) = ComfyHardware.Device(await api.TrySystemStatsAsync(ct).ConfigureAwait(false));
        if (!notes.Any(n => n.Contains(device, StringComparison.Ordinal)))
            notes.Add($"It runs on {device}." + (cpu ? " " + ComfyHardware.CpuAdvice : ""));
        notes.Add((count == 0 ? "It has no models yet. " : $"It has {count} model files. ") +
                  "action=templates with a few words finds a workflow and says which models it needs, with their links for action=download; " +
                  "action=find_models looks for other models online.");
        return new ToolResult(string.Join("\n", notes));
    }

    /// <summary>The models find_models showed in this task, by link, so download knows their name, folder and checksum.</summary>
    private readonly Dictionary<string, OnlineModel> _found = new(StringComparer.Ordinal);

    private async Task<ToolResult> FindModelsAsync(JsonObject args, ComfyHub hub, CancellationToken ct)
    {
        var search = GetString(args, "search")?.Trim() ?? "";
        var (kind, baseModel) = (GetString(args, "kind"), GetString(args, "base"));
        var sb = new StringBuilder();
        var (device, cpu) = await DeviceAsync(hub, ct).ConfigureAwait(false);
        var hardware = $"ComfyUI {device}." + (cpu ? " " + ComfyHardware.CpuAdvice : "");
        var n = 0;
        void List(OnlineModel m, string? popularity)
        {
            lock (_found)
                _found[m.Url] = m;
            sb.Append(++n).Append(". ").Append(m.Title).Append(": ").Append(m.Kind)
                .Append(m.Base is { Length: > 0 } b ? $" for {b}" : "")
                .Append(m.Size is { } size ? $", {Size(size)}" : "")
                .Append(popularity ?? (m.Downloads > 0 ? $", {m.Downloads:N0} downloads" : ""))
                .Append(m.Note is { } note ? $", {note}" : "")
                .Append($" ({m.Source}, {m.Page})").AppendLine();
            sb.Append("   ").Append(m.File).Append(": url=").Append(m.Url).Append(" model_folder=").AppendLine(m.Folder);
        }

        // What people use now: with no search the month's list itself, with one the ones that match it.
        var snapshot = await hub.Popular.GetAsync(ct).ConfigureAwait(false);
        var asOf = snapshot is null ? "" : $" (as of {snapshot.Fetched.ToLocalTime():d MMMM})";
        var popular = snapshot is null ? [] : ComfyPopularModels.Matching(snapshot.Models, ComfyNodeLibrary.Words(search), kind ?? (search.Length == 0 ? "checkpoint" : null), baseModel);
        if (search.Length == 0)
        {
            if (snapshot is null)
                throw new ToolArgumentException("Civitai couldn't be asked for this month's popular models; give search: words for the model you want (\"watercolor\", \"DreamShaper\", \"4x upscaler\").");
            var folder = ComfyModelSearch.KindOf(kind ?? "checkpoint")?.Folder ?? "checkpoints";
            sb.AppendLine($"Civitai's most downloaded {(folder == "loras" ? "LoRAs" : "checkpoints")} this month{(baseModel is null ? "" : $" for {baseModel}")}{asOf}. {hardware}");
            sb.AppendLine($"The month's most downloaded are on these base models: {ComfyPopularModels.Bases(snapshot.Models, folder)}.");
            foreach (var p in popular.Take(10))
                List(p.Model, $", #{p.Rank} this month");
            sb.Append("search narrows it to what you need (a style, a subject: \"anime\", \"furry\", \"photorealistic\", \"landscape\"); kind=lora lists LoRAs; base= one base model. " +
                      "Download one with action=download url=… model_folder=…; its card then says how its maker uses it.");
            return new ToolResult(sb.ToString().TrimEnd());
        }

        var problems = new List<string>();
        var found = await ComfyModelSearch.SearchAsync(search, kind, baseModel, popular.Count > 0 ? 6 : 10, problems, ct).ConfigureAwait(false);
        if (popular.Count > 0)
        {
            sb.AppendLine($"Most downloaded on Civitai this month for \"{search}\"{asOf}: what people make such pictures with now. {hardware}");
            foreach (var p in popular.Take(5))
                List(p.Model, $", #{p.Rank} this month");
        }
        sb.AppendLine(found.Count == 0 ? $"No model named for \"{search}\" was found on Civitai or Hugging Face{(kind is { } k ? $" as a {k}" : "")}. Try other or fewer words."
            : $"Models named for \"{search}\", the most downloaded of all time first on each site." + (popular.Count > 0 ? "" : " " + hardware));
        foreach (var m in found)
            List(m, null);
        foreach (var p in problems)
            sb.AppendLine(p);
        if (found.Count + popular.Count > 0)
            sb.Append("Download one with action=download url=… model_folder=…; prefer .safetensors files. Its card then says how its maker uses it, and a LoRA's trigger words.");
        return new ToolResult(sb.ToString().TrimEnd());
    }

    /// <summary>What ComfyUI runs on, said for model choices: "runs on …" when it runs, "would run on …" when none has been used yet.</summary>
    private static async Task<(string Text, bool Cpu)> DeviceAsync(ComfyHub hub, CancellationToken ct)
    {
        if (await new ComfyApi(hub.Address).TrySystemStatsAsync(ct).ConfigureAwait(false) is { } stats)
        {
            var (text, cpu) = ComfyHardware.Device(stats);
            return ("runs on " + text, cpu);
        }
        var build = ComfyHardware.Pick(ComfyHardware.Cards(), ComfyInstaller.Arm64);
        var ram = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 1073741824.0;
        // Not "isn't installed": a BlackBox that hasn't used one yet doesn't know. Asked for the
        // month's most downloaded first thing in a fresh Data folder, qwen3.8 read that and ran
        // action=install, which found the PC's own ComfyUI and started it.
        var installed = hub.SavedInstall is not null ? "isn't running; it runs"
            : "isn't running, and none has been used from this BlackBox yet (action=status finds the PC's own and starts it; action=install is for a PC without one); it would run";
        return ($"{installed} on {build.Device}, with {ram:0} GB of RAM", build.Cpu);
    }

    private async Task<ToolResult> DownloadAsync(JsonObject args, ComfyHub hub, ToolContext ctx, CancellationToken ct)
    {
        var given = RequireString(args, "url").Trim();
        if (!Uri.TryCreate(given, UriKind.Absolute, out var link) || link.Scheme is not ("http" or "https"))
            throw new ToolArgumentException("url must be the model file's web link (https://…), as find_models or a template gives it.");
        OnlineModel? known;
        lock (_found)
            known = _found.GetValueOrDefault(given);
        known ??= await ComfyModelSearch.ResolveAsync(link, ct).ConfigureAwait(false);
        var url = known is not null ? new Uri(known.Url) : link;
        var (name, size) = (known?.File is { Length: > 0 } file ? Path.GetFileName(file) : null, known?.Size);
        if (name is null)
        {
            var probe = await Web.Downloader.ProbeAsync(url, ct).ConfigureAwait(false);
            (name, size) = (probe.Name, size ?? probe.Size);
        }
        var folder = ComfyModelFolders.Normalize(GetString(args, "model_folder") ?? known?.Folder ?? ComfyModelSearch.FolderFor(known?.File ?? name, null));
        var dir = await hub.ModelsFolderAsync(folder, ct).ConfigureAwait(false)
                  ?? throw new ComfyApiException("ComfyUI isn't installed yet, so there is no models folder to download into: install it first with action=install.");
        var target = Path.Combine(dir, name);
        if (File.Exists(target) && await IsWholeAsync(target, size, known?.Sha256, ct).ConfigureAwait(false))
            return new ToolResult($"{name} is there already, in {dir}. {DiffusionModelAlone(folder, target) ?? ComfyModelFolders.Use(folder, name)}");

        var started = DateTime.UtcNow;
        await Web.Downloader.DownloadAsync(url, target, known?.Sha256, ctx.Progress, ct).ConfigureAwait(false);
        var took = DateTime.UtcNow - started;
        var length = new FileInfo(target).Length;
        if (known?.Sha256 is { Length: > 0 } sha)
            hub.Cards.Remember(target, sha);
        var use = DiffusionModelAlone(folder, target) ?? ComfyModelFolders.Use(folder, name);
        // The new file is a choice in its loader node from now on.
        var api = new ComfyApi(hub.Address);
        if (await api.TrySystemStatsAsync(ct).ConfigureAwait(false) is not null)
            await hub.ModelsAsync(api, ct, fresh: true).ConfigureAwait(false);
        var checkedNote = known?.Sha256 is { Length: > 0 } ? ", its SHA-256 checked" : "";
        return new ToolResult($"Downloaded {name} ({Size(length)} in {ComfyRunner.Clock(took)}{checkedNote}) into {dir}. {use}");
    }

    /// <summary>
    /// How to load a file in models/checkpoints that holds the diffusion model alone (no VAE), as the
    /// Anima, Krea and Z-Image models Civitai lists as checkpoints do; null for a whole checkpoint.
    /// The download said "Load it with CheckpointLoaderSimple" of MiaoMiao Harem, an Anima model,
    /// which gives such a file's MODEL and nothing else: qwen3.8 had to find its text encoder and VAE.
    /// </summary>
    internal static string? DiffusionModelAlone(string folder, string path) =>
        folder == "checkpoints" && ComfyModelCards.HoldsVae(path) == false
            ? $"It holds the diffusion model alone, no text encoder or VAE: CheckpointLoaderSimple (ckpt_name \"{Path.GetFileName(path)}\") gives its MODEL only, " +
              "so load the text encoder and VAE as a template of its base model does (CLIPLoader, VAELoader), or load it with UNETLoader from models/diffusion_models as such a template does."
            : null;

    private static string Size(long bytes) =>
        bytes >= 1L << 30 ? $"{bytes / 1073741824.0:0.0} GB" : bytes >= 1L << 20 ? $"{bytes / 1048576.0:0} MB" : $"{Math.Max(1, bytes / 1024)} KB";

    /// <summary>
    /// Whether a model file already there is all of it: its size as the site gives it, else its
    /// SHA-256; one neither says anything about is taken as whole. A file of that name was taken
    /// as whole whatever its size when the link was one find_models hadn't given, so what an
    /// earlier curl left half done went to ComfyUI.
    /// </summary>
    private static async Task<bool> IsWholeAsync(string path, long? size, string? sha256, CancellationToken ct)
    {
        if (size is { } expected)
            return Math.Abs(new FileInfo(path).Length - expected) < 2048;
        if (sha256 is not { Length: > 0 })
            return true;
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, useAsync: true);
        var hash = await System.Security.Cryptography.SHA256.HashDataAsync(file, ct).ConfigureAwait(false);
        return Convert.ToHexString(hash).Equals(sha256, StringComparison.OrdinalIgnoreCase);
    }

    // ---- status, free -------------------------------------------------------------------

    private static async Task<ToolResult> StatusAsync(JsonObject args, ComfyHub hub, ComfyApi api, ToolContext ctx, CancellationToken ct)
    {
        if (GetString(args, "prompt_id") is { Length: > 0 } promptId)
            return await WaitForAsync(args, hub, api, promptId.Trim(), ctx, ct).ConfigureAwait(false);

        var stats = await api.TrySystemStatsAsync(ct).ConfigureAwait(false);
        var sb = new StringBuilder();
        var system = stats?["system"];
        sb.AppendLine($"ComfyUI {(string?)system?["comfyui_version"]} runs at {api.BaseUrl}" +
                      (hub.SavedInstall is { } install ? $", from {install.Root}" : "") + ".");
        if (ComfyHardware.Device(stats) is { Cpu: true } onCpu)
            sb.AppendLine($"It runs on {onCpu.Text}. {ComfyHardware.CpuAdvice}");
        else
            foreach (var device in (stats?["devices"] as JsonArray ?? []).OfType<JsonObject>())
                sb.AppendLine($"GPU: {(string?)device["name"]}, {Gb((long?)device["vram_free"])} of {Gb((long?)device["vram_total"])} free.");
        sb.AppendLine($"RAM: {Gb((long?)system?["ram_free"])} of {Gb((long?)system?["ram_total"])} free.");
        if (await api.GetJsonAsync("queue", ct).ConfigureAwait(false) is JsonObject q)
            sb.AppendLine($"Queue: {(q["queue_running"] as JsonArray)?.Count ?? 0} running, {(q["queue_pending"] as JsonArray)?.Count ?? 0} waiting.");
        var library = await hub.LibraryAsync(api, ct).ConfigureAwait(false);
        var packs = library.All.Select(n => n.Pack).OfType<string>().Distinct().OrderBy(p => p).ToList();
        sb.AppendLine($"{library.All.Count} node types; custom node packs: {(packs.Count == 0 ? "none" : string.Join(", ", packs))}.");
        if (await ComfyHub.FoldersAsync(api, ct).ConfigureAwait(false) is { } dirs)
        {
            if (dirs.TryGetValue("code", out var code))
            {
                sb.AppendLine($"Its folder: {code} (custom nodes in {Path.Combine(code, "custom_nodes")}).");
                // Cropping a view out of a reference sheet took a guess at python\python.exe and a search
                // of the disk first; ComfyUI can't run without Pillow and numpy, so its Python has them.
                // And to cut a character out for a 3D model and look into the model it made, qwen3.8
                // pip-installed rembg and trimesh into the PC's own Python, while ComfyUI's had both.
                if (ComfyInstall.FromFolder(code) is { Kind: ComfyInstallKind.Portable or ComfyInstallKind.Venv } own && File.Exists(own.Program))
                {
                    var has = new List<string> { "Pillow", "numpy" };
                    has.AddRange(new[] { ("rembg", "rembg (cuts out a background)"), ("cv2", "OpenCV"), ("trimesh", "trimesh (3D models)") }
                        .Where(p => own.SitePackages is { } site && Directory.Exists(Path.Combine(site, p.Item1))).Select(p => p.Item2));
                    sb.AppendLine($"Its Python has {string.Join(", ", has.SkipLast(1))} and {has[^1]}, " +
                                  $"for working on the pictures and models it makes without installing anything: {own.Program}.");
                }
            }
            if (dirs.TryGetValue("output", out var output)) sb.AppendLine($"Output folder: {output}.");
        }
        var log = await LogAsync(api, ct).ConfigureAwait(false);
        if (log.Length > 0)
            sb.AppendLine("The end of its log:").AppendLine(log);
        return new ToolResult(sb.ToString().TrimEnd());
    }

    private static async Task<string> LogAsync(ComfyApi api, CancellationToken ct)
    {
        try
        {
            if (await api.GetJsonAsync("internal/logs/raw", ct).ConfigureAwait(false) is JsonObject raw && raw["entries"] is JsonArray entries)
                return string.Join("", entries.TakeLast(25).Select(e => (string?)e?["m"])).Trim().Clip(3000);
            var text = await api.GetTextAsync("internal/logs", ct).ConfigureAwait(false);
            var log = JsonNode.Parse(text) is JsonValue v && v.TryGetValue<string>(out var s) ? s : text;
            return string.Join("\n", log.Split('\n').TakeLast(25)).Trim().Clip(3000);
        }
        catch (Exception ex) when (ex is ComfyApiException or System.Text.Json.JsonException)
        {
            return "";
        }
    }

    private static string Gb(long? bytes) => bytes is { } b ? $"{b / 1073741824.0:0.0} GB" : "?";

    private static async Task<ToolResult> FreeAsync(ComfyApi api, CancellationToken ct)
    {
        await api.FreeAsync(ct).ConfigureAwait(false);
        return new ToolResult("ComfyUI unloads its models and frees memory (once no job is running). It keeps running.");
    }

    // ---- nodes, models --------------------------------------------------------------------

    private static async Task<ToolResult> NodesAsync(JsonObject args, ComfyHub hub, ComfyApi api, CancellationToken ct)
    {
        var library = await hub.LibraryAsync(api, ct).ConfigureAwait(false);
        var sb = new StringBuilder();
        if (GetStringList(args, "names") is { Count: > 0 } names)
        {
            foreach (var name in names.Select(n => n.Trim()).Where(n => n.Length > 0).Distinct())
            {
                var def = library.Find(name) ?? library.All.FirstOrDefault(n => n.DisplayName.Equals(name, StringComparison.OrdinalIgnoreCase));
                if (def is null)
                {
                    var near = ComfyNodeLibrary.Closest(name, library.All.Select(n => n.Name), 5);
                    sb.AppendLine($"There is no node type \"{name}\"" + (near.Count > 0 ? $"; closest: {string.Join(", ", near)}." : "; look for it with search."));
                }
                else
                    sb.AppendLine(def.Details());
                sb.AppendLine();
            }
            // Before the first run of an outpaint or inpaint workflow, not after a seamed one.
            if (names.Any(n => n.Trim() is "SetLatentNoiseMask" or "ImagePadForOutpaint"))
                sb.AppendLine(char.ToUpperInvariant(MaskAdvice[0]) + MaskAdvice[1..]);
            return new ToolResult(sb.ToString().TrimEnd());
        }
        var search = GetString(args, "search") ?? throw new ToolArgumentException("Give search (words) or names (node types) for action=nodes.");
        var found = library.Search(search, 25);
        if (found.Count == 0)
            return new ToolResult($"No node type matches \"{search}\". Try other words, or a template (action=templates).");
        sb.AppendLine($"Node types matching \"{search}\", best first (inputs marked ? are optional; names=[…] shows one in full):");
        foreach (var n in found)
            sb.Append("- ").AppendLine(n.Signature().Clip(400));
        return new ToolResult(sb.ToString().TrimEnd());
    }

    private async Task<ToolResult> ModelsAsync(JsonObject args, ComfyHub hub, ComfyApi api, CancellationToken ct)
    {
        // A model file asked for by name= as a template is: qwen3.8 did so for the checkpoint it had
        // just downloaded, got every model on the PC and no card, and read the file's header itself.
        var words = ComfyNodeLibrary.Words(GetString(args, "search") ?? GetString(args, "name") ?? "");
        // Asked for on purpose, often right after a download: never from the cache.
        var models = await hub.ModelsAsync(api, ct, fresh: true).ConfigureAwait(false);
        var sb = new StringBuilder();
        var dirs = await ComfyHub.FoldersAsync(api, ct).ConfigureAwait(false);
        var shown = 0;
        foreach (var (folder, files) in models.OrderBy(kv => kv.Key))
        {
            if (folder is "custom_nodes" or "configs" or "VHS_video_formats" || files.Count == 0)
                continue;
            var folderHit = words.Count > 0 && words.All(w => ComfyNodeLibrary.Squash(folder).Contains(w));
            var list = files.Where(f => words.Count == 0 || folderHit || words.All(w => ComfyNodeLibrary.Squash(f).Contains(w))).OrderBy(f => f).ToList();
            if (list.Count == 0)
                continue;
            var sizes = await SizesAsync(api, folder, ct).ConfigureAwait(false);
            var where = dirs is not null && dirs.TryGetValue("models:" + folder, out var d) ? $" ({d})" : "";
            sb.AppendLine($"{folder}{where}:");
            foreach (var f in list.Take(60))
                sb.AppendLine("  " + f + (sizes.TryGetValue(f, out var size) ? $"  {size / 1073741824.0:0.00} GB" : ""));
            if (list.Count > 60)
                sb.AppendLine($"  … and {list.Count - 60} more");
            shown += list.Count;
        }
        if (shown == 0)
            return new ToolResult(words.Count > 0 ? $"No installed model matches \"{GetString(args, "search")}\"." : "No models are installed.");
        sb.AppendLine("A model too big for the GPU still runs, slower: ComfyUI keeps part of it in RAM.");
        // Looked for by name, a checkpoint or LoRA is about to be used: how to use it comes with it.
        if (words.Count > 0)
        {
            var named = models.Where(kv => kv.Key is "checkpoints" or "loras")
                .SelectMany(kv => kv.Value.Where(f => words.All(w => ComfyNodeLibrary.Squash(f).Contains(w))).Select(f => (Folder: kv.Key, File: f)))
                .ToList();
            if (named.Count is > 0 and <= 3)
                foreach (var card in await CardsAsync(api, hub, named, ct).ConfigureAwait(false))
                    sb.AppendLine().AppendLine(card);
        }
        return new ToolResult(sb.ToString().TrimEnd());
    }

    /// <summary>The checkpoints and LoRAs whose card this task has seen: each comes once.</summary>
    private readonly HashSet<string> _carded = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// What Civitai says about the checkpoints and LoRAs given that this task hasn't seen yet (three
    /// at most): how to prompt them, at what size and with which settings their makers made their
    /// pictures. For a file Civitai doesn't know, the size its base model is made for.
    /// </summary>
    private async Task<List<string>> CardsAsync(ComfyApi api, ComfyHub hub, IEnumerable<(string Folder, string File)> files, CancellationToken ct)
    {
        var dirs = await ComfyHub.FoldersAsync(api, ct).ConfigureAwait(false) ?? [];
        var wanted = new List<(string File, string Path, string Folder)>();
        foreach (var (folder, file) in files.Where(f => f.Folder is "checkpoints" or "loras").DistinctBy(f => f.File, StringComparer.OrdinalIgnoreCase).Take(3))
        {
            if (!dirs.TryGetValue("models:" + folder, out var dir) || Path.Combine(dir, file) is not { } path || !File.Exists(path))
                continue;
            lock (_carded)
                if (!_carded.Add(file))
                    continue;
            wanted.Add((file, path, folder));
        }
        // All at once: a template of LTX 2.3 names two 27 GB checkpoints, whose first hashes take a while each.
        var looked = await Task.WhenAll(wanted.Select(w => CardAsync(hub, w.Path, ct))).ConfigureAwait(false);
        var cards = new List<string>();
        foreach (var ((file, path, folder), (card, answered)) in wanted.Zip(looked))
        {
            if (card is not null)
                cards.Add(ComfyModelCards.Format(file, card));
            else if (!answered)
                lock (_carded) // Looked up again next time, not called unknown.
                    _carded.Remove(file);
            else if (ComfyModelCards.BaseFromFile(path) is { } made)
                cards.Add($"{file} isn't on Civitai; it is {ComfyModelCards.Describe(made)}.");
            if (DiffusionModelAlone(folder, path) is { } alone)
                cards.Add($"{file}: {alone}");
        }
        return cards;
    }

    /// <summary>
    /// A model file's card, or none: Civitai doesn't know the file (answered), or it couldn't be
    /// asked within ten seconds (not answered). A big file's first hash takes longer (a 27 GB LTX
    /// checkpoint held a template's view up half a minute): it goes on, and the card comes with a
    /// later call, after the run at the latest. A card is a help; it must never hold up or fail the
    /// call it comes with.
    /// </summary>
    private static async Task<(ModelCard? Card, bool Answered)> CardAsync(ComfyHub hub, string path, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            return (await hub.Cards.LookUpAsync(path, timeout.Token).ConfigureAwait(false), true);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return (null, false);
        }
    }

    private static async Task<Dictionary<string, long>> SizesAsync(ComfyApi api, string folder, CancellationToken ct)
    {
        var sizes = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (await api.GetJsonAsync("experiment/models/" + Uri.EscapeDataString(folder), ct).ConfigureAwait(false) is JsonArray files)
                foreach (var f in files.OfType<JsonObject>())
                    if ((string?)f["name"] is { } name && f["size"] is JsonValue v && v.TryGetValue<long>(out var size))
                        sizes[name] = size;
        }
        catch (Exception ex) when (ex is ComfyApiException or System.Text.Json.JsonException) { }
        return sizes;
    }

    // ---- templates ------------------------------------------------------------------------

    private async Task<ToolResult> TemplatesAsync(JsonObject args, ComfyHub hub, ComfyApi api, ToolContext ctx, CancellationToken ct)
    {
        var all = await hub.TemplatesAsync(api, ct).ConfigureAwait(false);
        var library = await hub.LibraryAsync(api, ct).ConfigureAwait(false);
        var models = await hub.ModelsAsync(api, ct).ConfigureAwait(false);
        if (GetString(args, "name") is { Length: > 0 } name)
        {
            var template = ComfyTemplates.Resolve(all, name, out var nearest)
                           ?? throw new ComfyWorkflowException($"There is no template \"{name}\"" + (nearest.Count > 0 ? $"; did you mean {string.Join(", ", nearest)}?" : "; list them with search."));
            var (editor, workflow) = await LoadTemplateAsync(hub, api, template, ct).ConfigureAwait(false);
            var file = SaveWorkflow(ctx, template.Name, workflow);
            var sb = new StringBuilder();
            sb.AppendLine($"Template {template.Name}" + (template.Title != Path.GetFileName(template.Name) ? $" \"{template.Title}\"" : "") +
                          (template.Description.Length > 0 ? ": " + template.Description.Clip(400) : "."));
            AppendReadiness(sb, editor, workflow, library, models);
            if ((ComfyBuiltins.For(template)?.Notes ?? ComfyTemplates.Notes(editor, 1500)) is { Length: > 0 } notes)
                sb.AppendLine("Its notes:").AppendLine(notes);
            foreach (var card in await CardsAsync(api, hub, ModelFiles(workflow, models), ct).ConfigureAwait(false))
                sb.AppendLine(card);
            sb.AppendLine($"As an API-format workflow ({workflow.Nodes.Count} nodes), also saved to {file}:");
            sb.AppendLine(workflow.Listing());
            var usual = workflow.UsualChanges(library);
            if (usual.Count > 0)
                sb.AppendLine("Inputs a run usually changes: " + string.Join(", ", usual.Take(30)) + ".");
            sb.Append($"Run it with action=run name=\"{template.Name}\" and set {{\"node id.input\": value}}, or edit {Path.GetFileName(file)} and run it with workflow_file. " +
                      "Nodes can be added the same way, with ids of your own.");
            return new ToolResult(sb.ToString());
        }

        var search = GetString(args, "search") ?? "";
        var found = ComfyTemplates.Search(all, search, 12);
        // Asked to extend a photo into a panorama, qwen3.8 once painted the sides itself with an
        // installed model (and left seams), and once downloaded Flux Fill with its text encoder, 32 GB,
        // to the same end; and two of its three panoramas got the photo's waterfall again on a side.
        var outpaint = OutpaintWords().IsMatch(search);
        const string OutpaintPrompt = "To extend a picture, describe in the prompt what the new parts should hold (more of the forest, the stream), " +
                                      "not the picture's subject, or the subject is drawn again there.";
        const string OutpaintHere = "No outpaint template here is ready. Without downloading one (a fill model with its text encoder can be 30 GB): " +
                                    "pad the picture (ImagePadForOutpaint) and repaint the padding with an installed model for whole pictures through SetLatentNoiseMask; ";
        var seamless = SeamlessWords().IsMatch(search) ? SeamlessAdvice(library) : null;
        if (found.Count == 0)
            return new ToolResult($"No template matches \"{search}\" among {all.Count}. Try other words, or build the workflow from node types (action=nodes)." +
                                  (outpaint ? $"\n{OutpaintHere}{MaskAdvice} An edit model leaves the padding grey. {OutpaintPrompt}" : "") +
                                  (seamless is null ? "" : "\n" + seamless) +
                                  (MatchingModels(search, models) is { Count: > 0 } matching ? $"\nInstalled models whose names match: {string.Join(", ", matching)}." : ""));
        var lines = new StringBuilder();
        // On the CPU most templates are out of reach: said before the list, so the choice is made with it.
        var (device, cpu) = ComfyHardware.Device(await api.TrySystemStatsAsync(ct).ConfigureAwait(false));
        if (cpu)
            lines.AppendLine($"ComfyUI runs on {device}. {ComfyHardware.CpuAdvice}");
        lines.AppendLine(search.Length == 0 ? "The user's saved workflows, and BlackBox's own:" : $"Templates matching \"{search}\", best first (of {all.Count}; [ready] means its models and nodes are installed):");
        var outpaintReady = false;
        foreach (var t in found)
        {
            var ready = "";
            try
            {
                var builtin = ComfyBuiltins.For(t);
                var editor = builtin is null ? await api.GetJsonAsync(t.FetchPath, ct).ConfigureAwait(false) : null;
                var missingNodes = ComfyWorkflow.IsEditorFormat(editor) ? ComfyTemplates.MissingNodeTypes(editor, library) : [];
                var missingModels = ComfyTemplates.Models(editor, builtin is null ? null : ComfyWorkflow.FromApi(ComfyWorkflow.ParseText(builtin.Workflow)), models)
                    .Where(m => !m.Installed).ToList();
                ready = missingNodes.Count == 0 && missingModels.Count == 0 ? " [ready]"
                    : " [needs " + string.Join(", ", missingModels.Select(m => m.File).Concat(missingNodes.Select(n => "node " + n)).Take(4)) + "]";
            }
            catch (Exception ex) when (ex is ComfyApiException or ComfyWorkflowException or System.Text.Json.JsonException) { }
            lines.Append("- ").Append(t.Name).Append(t.Title != Path.GetFileName(t.Name) ? $" \"{t.Title}\"" : "")
                .Append(t.Description.Length > 0 ? ": " + t.Description.Clip(240) : "").AppendLine(ready);
            outpaintReady |= ready == " [ready]" && (t.Name + " " + t.Description).Contains("outpaint", StringComparison.OrdinalIgnoreCase);
        }
        if (outpaint)
            lines.AppendLine((outpaintReady ? "" : $"{OutpaintHere}{MaskAdvice} An edit model leaves the padding grey. ") + OutpaintPrompt);
        if (seamless is not null)
            lines.AppendLine(seamless);
        // Templates are workflows; a style may be a LoRA or a checkpoint. Finding no pixel art
        // template, qwen3.8 drew a 32x32 knight by hand, row by row, with Pixel Art XL installed.
        if (MatchingModels(search, models) is { Count: > 0 } named)
            lines.AppendLine($"Installed models whose names match: {string.Join(", ", named)}. A LoRA goes on a checkpoint of the base model it was made for " +
                             "(action=models search=<its name> gives its card).");
        lines.Append("See one as a workflow with action=templates name=…, or run it directly with action=run name=… and set.");
        return new ToolResult(lines.ToString().TrimEnd());
    }

    private static void AppendReadiness(StringBuilder sb, JsonNode? editor, ComfyWorkflow workflow, ComfyNodeLibrary library, Dictionary<string, HashSet<string>> models)
    {
        var needs = ComfyTemplates.Models(editor, workflow, models);
        var missing = needs.Where(m => !m.Installed).ToList();
        if (needs.Count > 0)
            sb.AppendLine(missing.Count == 0 ? $"Models: all {needs.Count} installed." :
                "Models not installed: " + string.Join("; ", missing.Select(m => m.File + (m.Folder is not null ? $" (goes in models/{m.Folder})" : "") +
                                                                                  (m.Url is not null ? $", download it with action=download url={m.Url}" : ""))) + ".");
        // A checkpoint of the user's own may do as well as the one the template names.
        if (missing.Any(m => m.Folder == "checkpoints") && models.TryGetValue("checkpoints", out var checkpoints) && checkpoints.Count > 0)
            sb.AppendLine("Installed checkpoints that could take its place: " + string.Join(", ", checkpoints.Order().Take(12)) + ".");
        var missingNodes = editor is not null && ComfyWorkflow.IsEditorFormat(editor) ? ComfyTemplates.MissingNodeTypes(editor, library)
            : workflow.All().Select(n => ComfyWorkflow.ClassOf(n.Node)).Where(c => library.Find(c) is null).Distinct().ToList();
        if (missingNodes.Count > 0)
            sb.AppendLine($"Node types not installed (a custom node pack is missing): {string.Join(", ", missingNodes)}.");
    }

    /// <summary>A template as the editor saved it and as a workflow to run; one of BlackBox's own is a workflow only.</summary>
    private static async Task<(JsonNode? Editor, ComfyWorkflow Workflow)> LoadTemplateAsync(ComfyHub hub, ComfyApi api, ComfyTemplate template, CancellationToken ct)
    {
        if (ComfyBuiltins.For(template) is { } builtin)
            return (null, ComfyWorkflow.FromApi(ComfyWorkflow.ParseText(builtin.Workflow)));
        var json = await api.GetJsonAsync(template.FetchPath, ct).ConfigureAwait(false);
        return ComfyWorkflow.IsEditorFormat(json)
            ? (json, ComfyWorkflow.FromApi(await ToApiAsync(hub, api, json!, ct).ConfigureAwait(false)))
            : (null, ComfyWorkflow.FromApi(json));
    }

    /// <summary>
    /// A workflow in the editor's format turned into the API format by ComfyUI's page. When the page
    /// can't be had, the model learns how to go on without it: told only that the browser closed, it
    /// wrote and debugged its own HTTP client in PowerShell, which took most of a run.
    /// </summary>
    private static async Task<JsonObject> ToApiAsync(ComfyHub hub, ComfyApi api, JsonNode editor, CancellationToken ct)
    {
        try
        {
            return await hub.Frontend.ConvertAsync(api.BaseUrl, hub.Generation, editor, ct).ConfigureAwait(false);
        }
        catch (ComfyApiException ex)
        {
            throw new ComfyApiException(ex.Message + " This workflow is in the editor's format, which ComfyUI's page turns into the API format to run it. " +
                "Without the page, write the workflow in the API format yourself (node id -> class_type and inputs; a link is [\"node id\", output index]) " +
                "and give it to action=run in workflow=: this tool uploads the input files, runs it and brings back the results as usual.");
        }
    }

    /// <summary>Keeps a workflow where the model can edit it and run it again: Data/scratch/comfyui/&lt;name&gt;.json.</summary>
    private static string SaveWorkflow(ToolContext ctx, string name, ComfyWorkflow workflow)
    {
        var dir = Path.Combine(ctx.ScratchDirectory ?? Path.GetTempPath(), "comfyui");
        Directory.CreateDirectory(dir);
        var safe = string.Concat(Path.GetFileName(name).Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        var path = Path.Combine(dir, safe + ".json");
        File.WriteAllText(path, workflow.ToJson(indented: true));
        return path;
    }

    // ---- run --------------------------------------------------------------------------------

    // What the last run of each template in this task set, and the reminders already given.
    private readonly Dictionary<string, Dictionary<string, JsonNode?>> _lastRuns = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _reminded = new(StringComparer.Ordinal);

    /// <summary>
    /// A note on what the last run of the same template set and this one doesn't: each run starts
    /// from the template again, and qwen3.8 retried an LTX video with a new prompt and seed but
    /// without the picture, length and size it had set the first time. Said once; a note, never a
    /// refusal (the user's rule: the model is steered by guidelines, not hard limits).
    /// </summary>
    private string? Forgotten(string template, ComfyWorkflow workflow, JsonObject? set)
    {
        lock (_lastRuns)
        {
            if (!_lastRuns.TryGetValue(template, out var last))
                return null;
            var now = set is null ? [] : ComfyWorkflow.Changes(set);
            var missing = last.Where(kv => !now.ContainsKey(kv.Key)).ToList();
            if (missing.Count == 0 || !_reminded.Add(template + "\n" + string.Join("\n", missing.Select(kv => kv.Key).Order(StringComparer.Ordinal))))
                return null;
            static string Show(JsonNode? value) => value switch
            {
                null => "nothing",
                JsonValue v when v.TryGetValue<string>(out var s) => s.ReplaceLineEndings(" ").Clip(60),
                JsonArray { Count: 2 } link when link[0] is JsonValue => $"a link from node {link[0]}",
                _ => value.ToJsonString(ComfyWorkflow.Compact).Clip(60),
            };
            var one = missing.Count == 1;
            return $"your last run of {template} also set {string.Join(", ", missing.Select(kv => $"{kv.Key} ({Show(kv.Value)})"))}; " +
                   $"this one doesn't, so {(one ? "it has the template's value" : "they have the template's values")} " +
                   $"({string.Join(", ", missing.Select(kv => Show(workflow.ValueOf(kv.Key))))}): each run starts from the template. " +
                   "Set them again when you mean to keep them.";
        }
    }

    private async Task<ToolResult> RunAsync(JsonObject args, ComfyHub hub, ComfyApi api, ToolContext ctx, CancellationToken ct)
    {
        var library = await hub.LibraryAsync(api, ct).ConfigureAwait(false);
        ComfyWorkflow workflow;
        string what;
        string? templateName = null, forgotten = null;
        if (GetString(args, "name") is { Length: > 0 } name && args["workflow"] is null && GetString(args, "workflow_file") is null)
        {
            var all = await hub.TemplatesAsync(api, ct).ConfigureAwait(false);
            var template = ComfyTemplates.Resolve(all, name, out var nearest)
                           ?? throw new ComfyWorkflowException($"There is no template \"{name}\"" + (nearest.Count > 0 ? $"; did you mean {string.Join(", ", nearest)}?" : "; list them with action=templates and search."));
            (_, workflow) = await LoadTemplateAsync(hub, api, template, ct).ConfigureAwait(false);
            what = templateName = template.Name;
            forgotten = Forgotten(template.Name, workflow, args["set"] as JsonObject);
        }
        else if (GetString(args, "workflow_file") is { Length: > 0 } file)
        {
            var path = ctx.ResolvePath(file);
            if (!File.Exists(path))
                return ToolResult.Error($"File not found: {path}");
            workflow = await FromJsonAsync(hub, api, ComfyWorkflow.ParseText(await File.ReadAllTextAsync(path, ct).ConfigureAwait(false)), ct).ConfigureAwait(false);
            what = path;
        }
        else if (args["workflow"] is { } given)
        {
            var json = given is JsonValue v && v.TryGetValue<string>(out var text) ? ComfyWorkflow.ParseText(text) : given;
            workflow = await FromJsonAsync(hub, api, json, ct).ConfigureAwait(false);
            what = "the workflow";
        }
        else
            throw new ToolArgumentException("action=run needs name (a template), workflow_file or workflow.");
        // The note on changes left out comes with whatever this call ends in.
        ToolResult Fail(string text) => ToolResult.Error(forgotten is null ? text : text + "\nNote: " + forgotten);

        if (args["set"] is JsonObject changes && changes.Count > 0 && workflow.Set(changes, library) is { Count: > 0 } setProblems)
        {
            // Node ids guessed before the template was looked at ("6.text" for its prompt): its usual inputs say which they are.
            var usual = setProblems.Any(p => p.Contains("there is no node", StringComparison.Ordinal)) ? workflow.UsualChanges(library) : [];
            return Fail("Nothing was run; these changes don't fit the workflow:\n- " + string.Join("\n- ", setProblems) +
                        (usual.Count > 0 ? $"\nInputs a run of {what} usually changes: {string.Join(", ", usual.Take(30))}." : ""));
        }

        // Files of the user's own: uploaded into ComfyUI's input folder, and named as it knows them.
        var uploaded = new HashSet<string>();
        var inputPictures = new List<string>();
        foreach (var (id, input, path) in workflow.LocalFiles(library, ctx.ResolvePath))
        {
            if (!File.Exists(path))
                return Fail($"Nothing was run: node {id} input \"{input}\" names {path}, which doesn't exist.");
            ctx.Progress?.Invoke($"Uploading {Path.GetFileName(path)}…");
            ((JsonObject)workflow.Nodes[id]!["inputs"]!)[input] = await api.UploadAsync(path, ct).ConfigureAwait(false);
            uploaded.Add(id + "." + input);
            if (Picture.Handles(path))
                inputPictures.Add(path);
        }

        var warnings = new List<string>();
        // As the editor sends a workflow: the inputs it keeps out of sight have their defaults.
        workflow.FillAdvancedDefaults(library);
        var problems = workflow.Validate(library, uploaded, warnings);
        if (problems.Any(p => p.Contains("has no node type", StringComparison.Ordinal) || p.Contains("isn't installed", StringComparison.Ordinal)
                              || p.Contains("isn't in ComfyUI's input folder", StringComparison.Ordinal)))
        {
            // Custom nodes and models installed since the list was fetched count too.
            library = await hub.LibraryAsync(api, ct, fresh: true).ConfigureAwait(false);
            warnings.Clear();
            workflow.FillAdvancedDefaults(library);
            problems = workflow.Validate(library, uploaded, warnings);
        }
        if (problems.Count > 0)
            return Fail($"Nothing was run; fix these in {what} and run it again:\n- " + string.Join("\n- ", problems.Take(40)) +
                        (problems.Count > 40 ? $"\n- … and {problems.Count - 40} more" : ""));

        var clientId = "blackbox-" + Guid.NewGuid().ToString("N")[..12];
        var (promptId, refused) = await ComfyRunner.QueueAsync(api, workflow, clientId, ct).ConfigureAwait(false);
        if (promptId is null)
            return Fail($"ComfyUI refused {what}; nothing was run:\n- " + string.Join("\n- ", refused));
        if (templateName is not null)
            lock (_lastRuns) _lastRuns[templateName] = args["set"] is JsonObject done ? ComfyWorkflow.Changes(done) : [];
        _ranJobs = true;
        hub.RememberJob(promptId, clientId, workflow.All().ToDictionary(n => n.Id, n => ComfyWorkflow.TitleOf(n.Node) ?? ComfyWorkflow.ClassOf(n.Node)));
        var result = await WaitForAsync(args, hub, api, promptId, ctx, ct, inputPictures).ConfigureAwait(false);
        var notes = refused.Select(r => "Left out: " + r).Concat(warnings.Select(w => "Note: " + w))
            .Concat(forgotten is null ? [] : ["Note: " + forgotten]).ToList();
        if (notes.Count > 0)
            result = result with { Text = string.Join("\n", notes) + "\n" + result.Text };
        if (result.IsError)
            return result;
        // With the pictures in front of it: whether they were made at the checkpoint's size, and
        // how the makers of the models it used make theirs, the first time it uses them.
        var used = ModelFiles(workflow, await hub.ModelsAsync(api, ct).ConfigureAwait(false));
        var after = new List<string>();
        if (await SizeNoteAsync(api, hub, workflow, used, result.Files ?? [], ct).ConfigureAwait(false) is { } sizeNote)
            after.Add(sizeNote);
        after.AddRange(await LoraNotesAsync(api, hub, used, ct).ConfigureAwait(false));
        if (!_maskNoted && MaskNote(workflow) is { } maskNote)
        {
            _maskNoted = true;
            after.Add(maskNote);
        }
        if (!_viewsNoted && inputPictures.Count == 1 && (result.Files ?? []).Any(f => Path.GetExtension(f).ToLowerInvariant() is ".glb" or ".gltf" or ".obj" or ".ply" or ".stl" or ".fbx"))
        {
            _viewsNoted = true;
            after.Add(ViewsNote(await hub.TemplatesAsync(api, ct).ConfigureAwait(false)));
        }
        after.AddRange(await CardsAsync(api, hub, used, ct).ConfigureAwait(false));
        return after.Count > 0 ? result with { Text = result.Text + "\n\n" + string.Join("\n\n", after) } : result;
    }

    private bool _maskNoted, _viewsNoted;

    private static readonly HashSet<string> Plain = new(StringComparer.Ordinal)
    {
        "the", "and", "for", "with", "from", "into", "this", "that", "make", "made", "picture", "pictures", "image", "images", "photo", "model", "models",
        "style", "text", "video", "sound", "workflow", "template", "using", "high", "quality",
    };

    /// <summary>
    /// The installed checkpoints and LoRAs whose file names hold a template search's words (two of
    /// them, or one of five letters or more), as "loras/pixel-art-xl-v1.1.safetensors"; at most six.
    /// </summary>
    internal static List<string> MatchingModels(string search, Dictionary<string, HashSet<string>> models)
    {
        var words = ComfyNodeLibrary.Words(search).Where(w => w.Length >= 3 && !Plain.Contains(w)).ToList();
        if (words.Count == 0)
            return [];
        return new[] { "checkpoints", "loras" }
            .SelectMany(folder => (models.GetValueOrDefault(folder) ?? []).Select(file => (Folder: folder, File: file)))
            .Select(m => (m.Folder, m.File, Hits: words.Where(w => ComfyNodeLibrary.Squash(m.File).Contains(w, StringComparison.Ordinal)).ToList()))
            .Where(m => m.Hits.Count >= 2 || m.Hits.Any(w => w.Length >= 5))
            .OrderByDescending(m => m.Hits.Count).ThenBy(m => m.File, StringComparer.OrdinalIgnoreCase)
            .Take(6).Select(m => $"{m.Folder}/{m.File}").ToList();
    }

    /// <summary>
    /// A note after a 3D model made from one picture. From the front picture of the user's
    /// character, Hunyuan3D 2.1 left out the head and the lower legs, and Trellis2 made a relief on a
    /// wall the background became; qwen3.8 spent an hour cutting the wall off. Its side and back views
    /// made with the multiple-angles template, and the four given to Hunyuan3D's multi-view
    /// template, gave a whole figure in under 13 minutes.
    /// </summary>
    internal static string ViewsNote(IEnumerable<ComfyTemplate> templates)
    {
        var list = templates.ToList();
        var multi = list.Where(t => MultiView().IsMatch(t.Name)).Select(t => t.Name).Take(2).ToList();
        var angles = list.FirstOrDefault(t => t.Name.Contains("angles", StringComparison.OrdinalIgnoreCase)
                                              && t.Name.Contains("character", StringComparison.OrdinalIgnoreCase))?.Name;
        return "Note: a 3D model made from one picture guesses the sides it can't see, and a figure drawn from the front often comes out as a relief. " +
               "When the views show that, build it from more sides: " +
               (multi.Count > 0 ? $"a multi-view template ({string.Join(", ", multi)}) takes " : "a multi-view 3D workflow takes ") +
               "pictures of the front, sides and back. Crop them from a reference sheet that shows them, or make them with an edit model first" +
               (angles is not null ? $" ({angles} turns the camera; a prompt changed to 180 degrees gives the back)." : " (rotate the camera 90 degrees left and right, and 180 for the back).");
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"multi[-_ ]?view", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex MultiView();

    /// <summary>
    /// How to make a texture tile without a seam. Asked for a seamless mossy stone floor, qwen3.8
    /// cross-faded the picture's edges (TextureAlchemy's SeamlessTiling, blend_edges): tiled, the
    /// joins showed doubled, blurred stones. Rolled by half and the seam cross repainted through
    /// DifferentialDiffusion, the same picture tiled cleanly, in 18 seconds.
    /// </summary>
    internal static string SeamlessAdvice(ComfyNodeLibrary library) =>
        "For a seamless texture, make the picture first, then roll it by half so its edges meet in a cross in the middle (" +
        (library.Find("TextureOffset") is not null
            ? "TextureOffset with offset_x and offset_y 0.5 and wrap_mode repeat does it, and gives the cross as its edge_mask, a picture that ImageToMask turns into a mask"
            : "numpy's roll in ComfyUI's Python does it; make a mask of the cross, a band about a tenth of the picture wide, and load it with LoadImageMask") +
        "), and repaint the cross through that mask: SetLatentNoiseMask, the model through DifferentialDiffusion, denoise about 0.75. " +
        "It then tiles without a seam, where a cross-fade of the edges (blend_edges, mirror) leaves doubled, blurred shapes along them.";

    [System.Text.RegularExpressions.GeneratedRegex(@"seamless|tileable|tileset|tiling", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex SeamlessWords();

    [System.Text.RegularExpressions.GeneratedRegex(@"outpaint|uncrop|panorama|\b(extend|expand|widen|wider)\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex OutpaintWords();

    /// <summary>How a part repainted through a mask meets the rest of the picture without an edge.</summary>
    internal const string MaskAdvice =
        "a part repainted through a mask (SetLatentNoiseMask, a picture padded with ImagePadForOutpaint) meets the rest with a hard edge, " +
        "unless the model goes through DifferentialDiffusion on its way to the sampler; give the mask a soft edge too (ImagePadForOutpaint's feathering about 200).";

    /// <summary>
    /// A note when a workflow repaints part of a picture through a mask and its model doesn't go
    /// through DifferentialDiffusion. Asked to extend a photo into a panorama, qwen3.8 padded it
    /// (feathering 40) and had Flux.1 dev draw the sides: both times they met the photo with a hard
    /// edge, and it called the second "natural". With the model through DifferentialDiffusion the
    /// same seed joined without one; a 192-pixel feather alone still showed it.
    /// </summary>
    internal static string? MaskNote(ComfyWorkflow workflow)
    {
        var types = workflow.All().Select(n => ComfyWorkflow.ClassOf(n.Node)).ToHashSet(StringComparer.Ordinal);
        return types.Contains("SetLatentNoiseMask") && !types.Contains("DifferentialDiffusion")
            ? "Note: " + MaskAdvice + " Look at where the new part meets the old in the result."
            : null;
    }

    /// <summary>The LoRAs and checkpoints whose fit this task has been told about.</summary>
    private readonly HashSet<(string Lora, string Checkpoint)> _paired = [];

    /// <summary>
    /// A note for each LoRA a workflow put on a checkpoint of another base model. Asked for a pixel
    /// art tavern, qwen3.8 put Pixel Art XL, made for SDXL 1.0, on Pony Diffusion V6 and got a
    /// washed-out picture twice, while a Pony one was in the list it chose from; and a LoRA of
    /// another kind of model (SD 1.5 on SDXL) isn't applied at all: ComfyUI only logs "lora key not
    /// loaded" and goes on. A line of SDXL (Pony, Illustrious) is told only from Civitai's cards.
    /// Each pair is said once in a task.
    /// </summary>
    private async Task<List<string>> LoraNotesAsync(ComfyApi api, ComfyHub hub, List<(string Folder, string File)> used, CancellationToken ct)
    {
        var notes = new List<string>();
        if (used.FirstOrDefault(u => u.Folder == "checkpoints").File is not { } checkpoint || !used.Any(u => u.Folder == "loras"))
            return notes;
        var dirs = await ComfyHub.FoldersAsync(api, ct).ConfigureAwait(false) ?? [];
        if (await BaseAsync("checkpoints", checkpoint) is not { } made)
            return notes;
        foreach (var lora in used.Where(u => u.Folder == "loras").Select(u => u.File).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            lock (_paired)
                if (_paired.Contains((lora, checkpoint)))
                    continue;
            if (await BaseAsync("loras", lora) is not { } own)
                continue;
            lock (_paired)
                _paired.Add((lora, checkpoint));
            var fit = ComfyModelCards.FitOf(own.Base, made.Base);
            if (fit == ComfyModelCards.Fit.OtherKind)
                notes.Add($"Note: {lora} is a LoRA for {own.Base}, but the base model of {checkpoint} is {made.Base}: a LoRA only fits the kind of model it was made for, " +
                          $"so ComfyUI couldn't apply it (its log says \"lora key not loaded\"). Use a LoRA made for {made.Base} " +
                          $"(action=find_models kind=lora base=\"{made.Base}\"), or a checkpoint for {own.Base}.");
            else if (fit == ComfyModelCards.Fit.OtherLine && own.FromCard && made.FromCard)
                notes.Add($"Note: {lora} was made for {own.Base}, and the base model of {checkpoint} is {made.Base}: it applies, but weaker or off-style; " +
                          $"a LoRA made for {made.Base} fits better (action=find_models kind=lora base=\"{made.Base}\").");
        }
        return notes;

        async Task<(string Base, bool FromCard)?> BaseAsync(string folder, string file)
        {
            if (!dirs.TryGetValue("models:" + folder, out var dir) || Path.Combine(dir, file) is not { } path || !File.Exists(path))
                return null;
            if ((await CardAsync(hub, path, ct).ConfigureAwait(false)).Card?.Base is { Length: > 0 } fromCard)
                return (fromCard, true);
            return ComfyModelCards.BaseFromFile(path) is { } fromFile ? (fromFile, false) : null;
        }
    }

    /// <summary>The installed checkpoints and LoRAs a workflow's nodes are set to, with their folders.</summary>
    private static List<(string Folder, string File)> ModelFiles(ComfyWorkflow workflow, Dictionary<string, HashSet<string>> models)
    {
        var found = new List<(string Folder, string File)>();
        foreach (var (_, node) in workflow.All())
            foreach (var (_, value) in node["inputs"] as JsonObject ?? [])
                if (value is JsonValue v && v.TryGetValue<string>(out var name))
                    foreach (var folder in new[] { "checkpoints", "loras" })
                        if (models.TryGetValue(folder, out var files) && files.Contains(name))
                            found.Add((folder, name));
        return found;
    }

    /// <summary>
    /// A note when a workflow makes its pictures at under half the size its checkpoint is made for:
    /// the user's furry template ran an SDXL model at 512x760, and every picture came out soft.
    /// Image to image works at the size of the picture it starts from, so there the pictures made
    /// say it: qwen3.8 repainted a figure cropped from the user's reference sheet at 432x1216.
    /// </summary>
    /// <param name="files">The files the job made.</param>
    private static async Task<string?> SizeNoteAsync(ComfyApi api, ComfyHub hub, ComfyWorkflow workflow, List<(string Folder, string File)> used,
        IReadOnlyList<string> files, CancellationToken ct)
    {
        if (used.FirstOrDefault(u => u.Folder == "checkpoints").File is not { } checkpoint ||
            (await ComfyHub.FoldersAsync(api, ct).ConfigureAwait(false))?.GetValueOrDefault("models:checkpoints") is not { } dir)
            return null;
        var path = Path.Combine(dir, checkpoint);
        if (!File.Exists(path) || ((await CardAsync(hub, path, ct).ConfigureAwait(false)).Card?.Base ?? ComfyModelCards.BaseFromFile(path)) is not { } made ||
            ComfyModelCards.NativeSide(made) is not { } side)
            return null;
        var empty = false;
        foreach (var (_, node) in workflow.All())
        {
            var type = ComfyWorkflow.ClassOf(node);
            if (!type.StartsWith("Empty", StringComparison.Ordinal) || !type.Contains("Latent", StringComparison.Ordinal))
                continue;
            empty = true;
            if (Number(node["inputs"]?["width"]) is { } w && Number(node["inputs"]?["height"]) is { } h && w * h < 0.55 * side * side)
                return $"Note: it made its pictures at {w}x{h}, under half the size {checkpoint} is made for ({ComfyModelCards.Describe(made)}): " +
                       "at that size it draws blurrier, with worse faces and anatomy. Make them at its size; to get them bigger still, upscale after.";
        }
        if (empty || !workflow.All().Any(n => ComfyWorkflow.ClassOf(n.Node) == "VAEEncode"))
            return null;
        var biggest = files.Where(Picture.Handles).Select(PictureSize).OfType<(int Width, int Height)>().DefaultIfEmpty().MaxBy(s => (long)s.Width * s.Height);
        if (biggest.Width == 0 || (long)biggest.Width * biggest.Height >= 0.55 * side * side)
            return null;
        return $"Note: it made its picture at {biggest.Width}x{biggest.Height}, under half the size {checkpoint} is made for ({ComfyModelCards.Describe(made)}): " +
               "image to image works at the size of the picture it starts from, and at this size it draws blurrier, with worse faces and anatomy. " +
               FormattableString.Invariant($"Scale that picture to the checkpoint's size before it is encoded (ImageScaleToTotalPixels, megapixels {side * side / 1048576.0:0.##}); ") +
               "a tall or wide crop can be padded to a usual shape first (ImagePadForOutpaint, which pads with grey), so the figure keeps its proportions.";

        static (int Width, int Height)? PictureSize(string file)
        {
            try
            {
                using var codec = SkiaSharp.SKCodec.Create(file);
                return codec is null ? null : (codec.Info.Width, codec.Info.Height);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }

        static double? Number(JsonNode? n) =>
            n is JsonValue && double.TryParse(n.ToString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : null;
    }

    /// <summary>Files named in the text a job's nodes showed: full paths, or paths in ComfyUI's output folder, that exist.</summary>
    internal static List<string> TextFiles(IEnumerable<string> texts, IReadOnlyDictionary<string, string> dirs)
    {
        var found = new List<string>();
        foreach (var text in texts)
        {
            found.AddRange(FileMentions.Find(text));
            var t = text.Trim();
            if (t.Length is > 0 and < 300 && !t.Contains('\n') && MediaPreview.KindOf(t) != "other" && !Path.IsPathRooted(t) && dirs.TryGetValue("output", out var output))
            {
                var candidate = Path.GetFullPath(Path.Combine(output, t));
                if (File.Exists(candidate)) found.Add(candidate);
            }
        }
        return found.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>A workflow from JSON the model gave: the API format as it is, the editor's format turned into it.</summary>
    private static async Task<ComfyWorkflow> FromJsonAsync(ComfyHub hub, ComfyApi api, JsonNode json, CancellationToken ct) =>
        ComfyWorkflow.IsEditorFormat(json)
            ? ComfyWorkflow.FromApi(await ToApiAsync(hub, api, json, ct).ConfigureAwait(false))
            : ComfyWorkflow.FromApi(json);

    /// <param name="inputs">The pictures the job's workflow loads (LoadImage), for when the user gave none to follow.</param>
    private static async Task<ToolResult> WaitForAsync(JsonObject args, ComfyHub hub, ComfyApi api, string promptId, ToolContext ctx, CancellationToken ct,
        IReadOnlyList<string>? inputs = null)
    {
        var wait = TimeSpan.FromMinutes(Math.Clamp(GetNumber(args, "wait_minutes") ?? 30, 0.1, 240));
        var (clientId, titles) = hub.JobClient(promptId);
        var job = await ComfyRunner.WaitAsync(api, promptId, clientId, titles, wait, ctx.Progress, ctx.UserMessage, ct).ConfigureAwait(false);
        if (job.Status is "running" or "queued")
            return new ToolResult($"The job (prompt_id {promptId}) is still {(job.Status == "queued" ? "waiting" : "running")} after {ComfyRunner.Clock(job.Elapsed)}" +
                                  (job.Progress is { } p ? $": {p}" : "") + ". " +
                                  (ctx.UserMessage.IsCancellationRequested ? "The user wrote to you meanwhile; read that first. " : "") +
                                  $"It goes on in ComfyUI; wait for it with action=status prompt_id={promptId}.");

        var dirs = await ComfyHub.FoldersAsync(api, ct).ConfigureAwait(false) ?? [];
        var files = await ComfyRunner.LocateAsync(api, job.Files, dirs, Path.Combine(ctx.ScratchDirectory ?? Path.GetTempPath(), "comfyui", "outputs"), ct).ConfigureAwait(false);
        // Some nodes say where they saved a file instead of listing it (Trellis2's mesh export
        // gives its .glb's path as text): those files are results too.
        foreach (var path in TextFiles(job.Texts, dirs))
            if (!files.Any(f => string.Equals(f.LocalPath, path, StringComparison.OrdinalIgnoreCase)))
                files.Add(new ComfyOutput("", Path.GetFileName(path), "", "output", path));
        // Previews ComfyUI only keeps in its temp folder are shown, but only saved files are results.
        var saved = files.Where(f => f.Type == "output").ToList();
        var shown = saved.Count > 0 ? saved : files;
        var paths = shown.Select(f => f.LocalPath).OfType<string>().ToList();
        if (GetString(args, "save_to") is { Length: > 0 } saveTo && paths.Count > 0)
        {
            var target = ctx.ResolvePath(saveTo);
            Directory.CreateDirectory(target);
            paths = paths.Select(p =>
            {
                var copy = Path.Combine(target, Path.GetFileName(p));
                File.Copy(p, copy, overwrite: true);
                return copy;
            }).ToList();
        }

        var sb = new StringBuilder();
        if (job.Status == "success")
            sb.AppendLine($"Done in {ComfyRunner.Clock(job.Elapsed)}. " + (paths.Count == 0 ? "It made no files." : paths.Count == 1 ? "It made:" : $"It made {paths.Count} files:"));
        else
            sb.AppendLine((job.Status == "interrupted" ? "The job was stopped" : "The job failed") + $" after {ComfyRunner.Clock(job.Elapsed)}. {job.Error}" +
                          (paths.Count > 0 ? "\nIt made these before that:" : ""));
        var preview = MediaPreview.ForFiles(paths, ctx.Media, ctx.SeesImages);
        foreach (var line in preview.Lines)
            sb.Append("- ").AppendLine(line);
        // Asked for a video with the sound of wind, qwen3.8 searched ComfyUI's 600 GB folder for
        // ffmpeg.exe for three minutes to put a sound on it; another run looked for it too.
        if (job.Status == "success" && ctx.Media?.Ffmpeg is { } ffmpeg && paths.Any(p => MediaPreview.KindOf(p) is "video" or "audio"))
            sb.AppendLine($"To cut, join or put a sound on these: {ffmpeg}");
        if (saved.Count == 0 && files.Count > 0 && job.Status == "success")
            sb.AppendLine("(These are previews in ComfyUI's temp folder, which it empties when it restarts: add a save node, or copy them with save_to, to keep them.)");
        foreach (var text in job.Texts.Take(10))
            sb.AppendLine("Text it gave: " + text.Clip(2000));
        var image = preview.Image;
        var imageCaption = "What the job made:";
        // Next to a picture to follow, so the result is judged against it rather than against the
        // model's memory of it: asked for a bunny from the user's reference sheet, Nemotron called
        // four results mature and close to the sheet's style while the user saw a chubby teenager.
        var (reference, given) = ctx.UserPictures.FirstOrDefault(File.Exists) is { } picture ? (picture, true) : (inputs?.FirstOrDefault(File.Exists), false);
        if (image is not null && job.Status == "success" && reference is not null &&
            MediaPreview.SideBySide(reference, image, given ? "The user's picture" : "The picture it started from") is { } both)
        {
            image = both;
            imageCaption = given ? "The user's picture, and what the job made:" : "The picture the job started from, and what it made:";
            sb.Append(given
                ? $"On the left is the picture the user gave you ({Path.GetFileName(reference)}); on the right, what the job made. " +
                  "Compare them where the user asked the result to follow it (the character, its age and build, colours, style), " +
                  "and say what differs. On the right: " + preview.Caption
                : $"On the left is the picture the job started from ({Path.GetFileName(reference)}); on the right, what it made. " + preview.Caption);
        }
        else if (image is not null)
            sb.Append(preview.Caption);
        var resultText = sb.ToString().TrimEnd();
        return job.Status == "success"
            ? new ToolResult(resultText, image, ImageCaption: image is null ? null : imageCaption, Files: paths)
            : new ToolResult(resultText, image, IsError: true, ImageCaption: image is null ? null : "What the job made before it stopped:", Files: paths);
    }
}
