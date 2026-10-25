using System.Text;
using System.Text.Json.Nodes;
using BlackBox.Core.Comfy;

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
        "A video starts from a picture: make it with an image model first and look at it, then animate it with an image-to-video template (first and last frames " +
        "when where it ends matters); text-to-video draws its own picture badly, so only when the user asks for it. LTX makes the video's sound (speech, " +
        "footsteps, wind) but not music: say \"no music\" and make music with a music model (ACE-Step); foley for a video that has no sound is blackbox/video_to_audio (MMAudio). Before writing a video prompt, read action=guide " +
        "search=\"video\": motion, direction, poses and props are what ruin most takes. " +
        "A checkpoint's or LoRA's card (with action=models search=its file name, a template's view, or after its first run) says how its maker uses it: " +
        "the prompt style, size, sampler, CFG and negative prompt to start from. " +
        "Change the seed for a different result. A model a workflow lacks: action=download with its url (a template lists the links of its models); " +
        "action=find_models searches Civitai and Hugging Face for others (a style, a checkpoint the user named, a LoRA, an upscaler) with their sizes and links, " +
        "and what the month's most downloaded on Civitai are for those words; without search it lists this month's most downloaded checkpoints " +
        "(kind=lora: LoRAs) and the base models they are on, so you know what is current before you pick one. " +
        "action=status shows the server, GPU and queue (with prompt_id: waits for that job); action=cancel with prompt_id stops that job only; " +
        "action=free unloads its models to free the GPU; action=restart restarts it (after installing custom nodes into its custom_nodes folder); action=stop stops it.";

    public override ToolRisk Risk => ToolRisk.Risky;

    public override JsonObject Parameters => Schema(
        ("action", Str("What to do", "templates", "nodes", "models", "run", "status", "cancel", "install", "find_models", "download", "free", "restart", "stop", "guide"), true),
        ("search", Str("Words to look for: in templates (\"video from image\"), node types (\"wan video\"), installed model files (\"lora\"), models online (find_models: \"furry\", \"watercolor\", \"DreamShaper\"; none for this month's most downloaded), or guides (guide: \"video\")"), false),
        ("name", Str("A template's name as action=templates lists it: to see it (templates) or run it (run)"), false),
        ("names", StrArray("Node types to show in full: inputs, their types and choices, outputs (nodes)"), false),
        ("workflow", new JsonObject { ["description"] = "A workflow in API format to run (a JSON object or its text)" }, false),
        ("workflow_file", Str("A .json workflow file to run (API format, or as ComfyUI's editor saves it)"), false),
        ("set", new JsonObject { ["type"] = "object", ["description"] = "Changes before the run: {\"node id.input\": value}, e.g. {\"6.text\": \"a cat\", \"129:98.length\": 49}" }, false),
        ("save_to", Str("A folder the user named to copy the files the run makes into; without it they stay in ComfyUI's output folder"), false),
        ("wait_minutes", Num("How long to wait for the job (default 30); it keeps running after that, and action=status with its prompt_id waits again"), false),
        ("prompt_id", Str("A job's id, from an earlier run that was still going (status waits for it, cancel stops it)"), false),
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
            "guide" => $"ComfyUI guide: {GetString(args, "search") ?? "all"}",
            _ => "ComfyUI " + action,
        };
    }

    // Whether this task uses the server, and has run jobs on it.
    private bool _usesServer, _ranJobs, _pythonNoted;

    public override async Task<ToolResult> ExecuteAsync(JsonObject args, ToolContext ctx, CancellationToken ct)
    {
        if (ctx.Comfy is not { } hub)
            return ToolResult.Error("ComfyUI isn't available in this run.");
        var action = RequireString(args, "action").Trim().ToLowerInvariant();
        if (action is "guide" or "guides")
            return Guide(GetString(args, "search"));
        // From before it may be started: when the task is over, a ComfyUI BlackBox started is closed.
        if (!_usesServer)
        {
            _usesServer = true;
            hub.Use();
            ctx.AfterRun(nameof(ComfyTool), () =>
            {
                var ranJobs = _ranJobs;
                (_usesServer, _ranJobs, _guideShown) = (false, false, false);
                return hub.ReleaseAsync(ranJobs);
            });
        }
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
            // Once a task, with its first call that reaches ComfyUI, not only in the status: a run that
            // never asked for it pip-installed Pillow into the PC's own Python to draw a sprite sheet.
            if (!_pythonNoted)
            {
                _pythonNoted = true; // the status says it itself
                if (action != "status" && await ComfyHub.FoldersAsync(api, ct).ConfigureAwait(false) is { } dirs && dirs.TryGetValue("code", out var code) &&
                    PythonNote(code) is { } python)
                    notes.Add(python);
            }
            var result = action switch
            {
                "status" => await StatusAsync(args, hub, api, ctx, ct).ConfigureAwait(false),
                "free" => await FreeAsync(api, ct).ConfigureAwait(false),
                "cancel" => await CancelAsync(args, api, ct).ConfigureAwait(false),
                "nodes" => await NodesAsync(args, hub, api, ct).ConfigureAwait(false),
                "models" => await ModelsAsync(args, hub, api, ct).ConfigureAwait(false),
                "templates" => await TemplatesAsync(args, hub, api, ctx, ct).ConfigureAwait(false),
                "run" => await RunAsync(args, hub, api, ctx, ct).ConfigureAwait(false),
                _ => throw new ToolArgumentException("action must be templates, nodes, models, run, status, cancel, install, find_models, download, free, restart, stop or guide."),
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

    // ---- guides ---------------------------------------------------------------------------------

    /// <summary>Whether the video guide was shown in this task: with an LTX workflow, once.</summary>
    private bool _guideShown;

    private ToolResult Guide(string? search)
    {
        var found = ComfyGuides.Find(search);
        if (found.Count == 0)
            return new ToolResult($"No guide matches \"{search}\". The guides: {string.Join(", ", ComfyGuides.Names)}.");
        _guideShown |= found.Contains(ComfyGuides.Video);
        return new ToolResult(string.Join("\n\n", found.Select(ComfyGuides.Text)));
    }

    /// <summary>The video guide, the first time an LTX workflow is looked at or run in a task: what a model writes for LTX is what ruins most takes.</summary>
    private string? VideoGuideFor(ComfyWorkflow workflow)
    {
        if (_guideShown || !ComfyGuides.IsLtx(workflow))
            return null;
        _guideShown = true;
        return "Before you write its prompt, BlackBox's guide to video prompts:\n" + ComfyGuides.Text(ComfyGuides.Video);
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
                .Append(m.Size is { } size ? $", {Strings.Bytes(size)}" : "")
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
        return new ToolResult($"Downloaded {name} ({Strings.Bytes(length)} in {Chats.ChatTimes.Stopwatch(took)}{checkedNote}) into {dir}. {use}");
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
                if (PythonNote(code) is { } python)
                    sb.AppendLine(python);
            }
            if (dirs.TryGetValue("output", out var output)) sb.AppendLine($"Output folder: {output}.");
        }
        var log = await LogAsync(api, ct).ConfigureAwait(false);
        if (log.Length > 0)
            sb.AppendLine("The end of its log:").AppendLine(log);
        return new ToolResult(sb.ToString().TrimEnd());
    }

    /// <summary>
    /// What ComfyUI's own Python has for working on pictures and models, and where it is; null for the
    /// Desktop app and a ComfyUI run by the PC's own Python. Cropping a view out of a reference sheet
    /// took a guess at python\python.exe and a search of the disk first; ComfyUI can't run without
    /// Pillow and numpy, so its Python has them. To cut a character out and look into a 3D model,
    /// qwen3.8 pip-installed rembg, onnxruntime and trimesh into the PC's own Python, and a walk cycle
    /// run that never asked for the status, Pillow. ComfyUI's had trimesh, and rembg (Trellis2 needs
    /// it) without onnxruntime, which rembg can't run without.
    /// </summary>
    internal static string? PythonNote(string code)
    {
        if (ComfyInstall.FromFolder(code) is not { Kind: ComfyInstallKind.Portable or ComfyInstallKind.Venv } own || !File.Exists(own.Program))
            return null;
        bool Has(string package) => own.SitePackages is { } site && Directory.Exists(Path.Combine(site, package));
        var has = new List<string> { "Pillow", "numpy" };
        if (Has("rembg"))
            has.Add(Has("onnxruntime") ? "rembg (cuts out a background)"
                : "rembg (cuts out a background once onnxruntime is installed into this Python; Trellis2's remove_background needs it too)");
        has.AddRange(new[] { ("cv2", "OpenCV"), ("trimesh", "trimesh (3D models)") }.Where(p => Has(p.Item1)).Select(p => p.Item2));
        return $"Its Python has {string.Join(", ", has.SkipLast(1))} and {has[^1]}, for working on the pictures and models it makes: {own.Program}.";
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

    /// <summary>
    /// Stops one job and takes it out of the queue, ComfyUI left running. Told "and more furry" and
    /// "the style is off" while a picture was being made, qwen3.8 stopped the whole server to drop
    /// the job those made pointless, and the next run waited 49 seconds for it to start again.
    /// </summary>
    private static async Task<ToolResult> CancelAsync(JsonObject args, ComfyApi api, CancellationToken ct)
    {
        var promptId = GetString(args, "prompt_id")?.Trim() is { Length: > 0 } id ? id
            : throw new ToolArgumentException("Give the job's prompt_id for action=cancel (a run that was still going names it).");
        await api.CancelAsync(promptId, ct).ConfigureAwait(false);
        return new ToolResult($"Job {promptId} is stopped and out of the queue. ComfyUI keeps running, with its models loaded for the next run.");
    }

    private static async Task<ToolResult> FreeAsync(ComfyApi api, CancellationToken ct)
    {
        await api.FreeAsync(ct).ConfigureAwait(false);
        return new ToolResult("ComfyUI unloads its models and frees memory (once no job is running). It keeps running.");
    }
}
