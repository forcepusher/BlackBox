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
public sealed class ComfyTool : Tool
{
    public override string Name => "comfyui";

    public override string Description =>
        "Make pictures, video, sound, music, speech and 3D models with ComfyUI on this PC; it is found and started for you. " +
        "A workflow is JSON in ComfyUI's API format: {\"node id\": {\"class_type\": \"…\", \"inputs\": {\"name\": a value, or [\"other node id\", output index]}}}. " +
        "Never write node, input or model names from memory: start from a template (action=templates with search finds them; with name it shows one " +
        "as a workflow, saved to a file you can edit), or look names up (action=nodes with search or names, action=models). " +
        "action=run runs a workflow (template, workflow_file, or workflow itself), changed by set {\"node id.input\": value}, e.g. " +
        "{\"6.text\": \"a red fox in snow\", \"3.seed\": 42}; it is checked first, then it waits until the job is done and gives you the files " +
        "with a look at them. For an input picture, sound or video, put its full local path where LoadImage/LoadAudio/LoadVideo name a file: it is uploaded. " +
        "Change the seed for a different result. action=status shows the server, GPU and queue (with prompt_id: waits for that job); " +
        "action=free unloads its models to free the GPU; action=restart restarts it (after installing custom nodes into its custom_nodes folder); action=stop stops it.";

    public override ToolRisk Risk => ToolRisk.Risky;

    public override JsonObject Parameters => Schema(
        ("action", Str("What to do", "templates", "nodes", "models", "run", "status", "free", "restart", "stop"), true),
        ("search", Str("Words to look for: in templates (\"video from image\"), node types (\"wan video\") or model files (\"lora\")"), false),
        ("name", Str("A template's name as action=templates lists it: to see it (templates) or run it (run)"), false),
        ("names", StrArray("Node types to show in full: inputs, their types and choices, outputs (nodes)"), false),
        ("workflow", new JsonObject { ["description"] = "A workflow in API format to run (a JSON object or its text)" }, false),
        ("workflow_file", Str("A .json workflow file to run (API format, or as ComfyUI's editor saves it)"), false),
        ("set", new JsonObject { ["type"] = "object", ["description"] = "Changes before the run: {\"node id.input\": value}, e.g. {\"6.text\": \"a cat\", \"129:98.length\": 49}" }, false),
        ("save_to", Str("A folder to copy the files the run makes into (they also stay in ComfyUI's output folder)"), false),
        ("wait_minutes", Num("How long to wait for the job (default 30); it keeps running after that, and action=status with its prompt_id waits again"), false),
        ("prompt_id", Str("A job's id, from an earlier run that was still going (status)"), false),
        ("folder", Str("ComfyUI's folder, if it isn't found by itself or another one should be used"), false));

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
            _ => "ComfyUI " + action,
        };
    }

    public override async Task<ToolResult> ExecuteAsync(JsonObject args, ToolContext ctx, CancellationToken ct)
    {
        if (ctx.Comfy is not { } hub)
            return ToolResult.Error("ComfyUI isn't available in this run.");
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
                _ => throw new ToolArgumentException("action must be templates, nodes, models, run, status, free, restart or stop."),
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
            if (dirs.TryGetValue("code", out var code)) sb.AppendLine($"Its folder: {code} (custom nodes in {Path.Combine(code, "custom_nodes")}).");
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

    private static async Task<ToolResult> ModelsAsync(JsonObject args, ComfyHub hub, ComfyApi api, CancellationToken ct)
    {
        var words = ComfyNodeLibrary.Words(GetString(args, "search") ?? "");
        var models = await hub.ModelsAsync(api, ct).ConfigureAwait(false);
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
        sb.Append("A model too big for the GPU still runs, slower: ComfyUI keeps part of it in RAM.");
        return new ToolResult(sb.ToString().TrimEnd());
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

    private static async Task<ToolResult> TemplatesAsync(JsonObject args, ComfyHub hub, ComfyApi api, ToolContext ctx, CancellationToken ct)
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
            if (ComfyTemplates.Notes(editor, 1500) is { Length: > 0 } notes)
                sb.AppendLine("Its notes:").AppendLine(notes);
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
        if (found.Count == 0)
            return new ToolResult($"No template matches \"{search}\" among {all.Count}. Try other words, or build the workflow from node types (action=nodes).");
        var lines = new StringBuilder();
        lines.AppendLine(search.Length == 0 ? "The user's saved workflows:" : $"Templates matching \"{search}\", best first (of {all.Count}; [ready] means its models and nodes are installed):");
        foreach (var t in found)
        {
            var ready = "";
            try
            {
                var editor = await api.GetJsonAsync(t.FetchPath, ct).ConfigureAwait(false);
                var missingNodes = ComfyWorkflow.IsEditorFormat(editor) ? ComfyTemplates.MissingNodeTypes(editor, library) : [];
                var missingModels = ComfyTemplates.Models(editor, null, models).Where(m => !m.Installed).ToList();
                ready = missingNodes.Count == 0 && missingModels.Count == 0 ? " [ready]"
                    : " [needs " + string.Join(", ", missingModels.Select(m => m.File).Concat(missingNodes.Select(n => "node " + n)).Take(4)) + "]";
            }
            catch (Exception ex) when (ex is ComfyApiException or System.Text.Json.JsonException) { }
            lines.Append("- ").Append(t.Name).Append(t.Title != Path.GetFileName(t.Name) ? $" \"{t.Title}\"" : "")
                .Append(t.Description.Length > 0 ? ": " + t.Description.Clip(240) : "").AppendLine(ready);
        }
        lines.Append("See one as a workflow with action=templates name=…, or run it directly with action=run name=… and set.");
        return new ToolResult(lines.ToString().TrimEnd());
    }

    private static void AppendReadiness(StringBuilder sb, JsonNode? editor, ComfyWorkflow workflow, ComfyNodeLibrary library, Dictionary<string, HashSet<string>> models)
    {
        var needs = ComfyTemplates.Models(editor, workflow, models);
        var missing = needs.Where(m => !m.Installed).ToList();
        if (needs.Count > 0)
            sb.AppendLine(missing.Count == 0 ? $"Models: all {needs.Count} installed." :
                "Models not installed: " + string.Join("; ", missing.Select(m => m.File + (m.Folder is not null ? $" (goes in models/{m.Folder})" : "") + (m.Url is not null ? $", from {m.Url}" : ""))) + ".");
        var missingNodes = editor is not null && ComfyWorkflow.IsEditorFormat(editor) ? ComfyTemplates.MissingNodeTypes(editor, library)
            : workflow.All().Select(n => ComfyWorkflow.ClassOf(n.Node)).Where(c => library.Find(c) is null).Distinct().ToList();
        if (missingNodes.Count > 0)
            sb.AppendLine($"Node types not installed (a custom node pack is missing): {string.Join(", ", missingNodes)}.");
    }

    /// <summary>A template as the editor saved it and as a workflow to run.</summary>
    private static async Task<(JsonNode? Editor, ComfyWorkflow Workflow)> LoadTemplateAsync(ComfyHub hub, ComfyApi api, ComfyTemplate template, CancellationToken ct)
    {
        var json = await api.GetJsonAsync(template.FetchPath, ct).ConfigureAwait(false);
        return ComfyWorkflow.IsEditorFormat(json)
            ? (json, ComfyWorkflow.FromApi(await hub.Frontend.ConvertAsync(api.BaseUrl, hub.Generation, json!, ct).ConfigureAwait(false)))
            : (null, ComfyWorkflow.FromApi(json));
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

    private static async Task<ToolResult> RunAsync(JsonObject args, ComfyHub hub, ComfyApi api, ToolContext ctx, CancellationToken ct)
    {
        var library = await hub.LibraryAsync(api, ct).ConfigureAwait(false);
        ComfyWorkflow workflow;
        string what;
        if (GetString(args, "name") is { Length: > 0 } name && args["workflow"] is null && GetString(args, "workflow_file") is null)
        {
            var all = await hub.TemplatesAsync(api, ct).ConfigureAwait(false);
            var template = ComfyTemplates.Resolve(all, name, out var nearest)
                           ?? throw new ComfyWorkflowException($"There is no template \"{name}\"" + (nearest.Count > 0 ? $"; did you mean {string.Join(", ", nearest)}?" : "; list them with action=templates and search."));
            (_, workflow) = await LoadTemplateAsync(hub, api, template, ct).ConfigureAwait(false);
            what = template.Name;
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

        if (args["set"] is JsonObject changes && changes.Count > 0 && workflow.Set(changes, library) is { Count: > 0 } setProblems)
            return ToolResult.Error("Nothing was run; these changes don't fit the workflow:\n- " + string.Join("\n- ", setProblems));

        // Files of the user's own: uploaded into ComfyUI's input folder, and named as it knows them.
        var uploaded = new HashSet<string>();
        foreach (var (id, input, path) in workflow.LocalFiles(library, ctx.ResolvePath))
        {
            if (!File.Exists(path))
                return ToolResult.Error($"Nothing was run: node {id} input \"{input}\" names {path}, which doesn't exist.");
            ctx.Progress?.Invoke($"Uploading {Path.GetFileName(path)}…");
            ((JsonObject)workflow.Nodes[id]!["inputs"]!)[input] = await api.UploadAsync(path, ct).ConfigureAwait(false);
            uploaded.Add(id + "." + input);
        }

        var warnings = new List<string>();
        var problems = workflow.Validate(library, uploaded, warnings);
        if (problems.Any(p => p.Contains("has no node type", StringComparison.Ordinal)))
        {
            // Custom nodes installed since the list was fetched count too.
            library = await hub.LibraryAsync(api, ct, fresh: true).ConfigureAwait(false);
            warnings.Clear();
            problems = workflow.Validate(library, uploaded, warnings);
        }
        if (problems.Count > 0)
            return ToolResult.Error($"Nothing was run; fix these in {what} and run it again:\n- " + string.Join("\n- ", problems.Take(40)) +
                                    (problems.Count > 40 ? $"\n- … and {problems.Count - 40} more" : ""));

        var clientId = "blackbox-" + Guid.NewGuid().ToString("N")[..12];
        var (promptId, refused) = await ComfyRunner.QueueAsync(api, workflow, clientId, ct).ConfigureAwait(false);
        if (promptId is null)
            return ToolResult.Error($"ComfyUI refused {what}; nothing was run:\n- " + string.Join("\n- ", refused));
        // Its models are let go once the task is over, so the GPU is free for whatever comes next.
        ctx.AfterRun(nameof(ComfyTool), () => api.FreeAsync(CancellationToken.None));
        hub.RememberJob(promptId, clientId, workflow.All().ToDictionary(n => n.Id, n => ComfyWorkflow.TitleOf(n.Node) ?? ComfyWorkflow.ClassOf(n.Node)));
        var result = await WaitForAsync(args, hub, api, promptId, ctx, ct).ConfigureAwait(false);
        var notes = refused.Select(r => "Left out: " + r).Concat(warnings.Select(w => "Note: " + w)).ToList();
        return notes.Count > 0 ? result with { Text = string.Join("\n", notes) + "\n" + result.Text } : result;
    }

    /// <summary>A workflow from JSON the model gave: the API format as it is, the editor's format turned into it.</summary>
    private static async Task<ComfyWorkflow> FromJsonAsync(ComfyHub hub, ComfyApi api, JsonNode json, CancellationToken ct) =>
        ComfyWorkflow.IsEditorFormat(json)
            ? ComfyWorkflow.FromApi(await hub.Frontend.ConvertAsync(api.BaseUrl, hub.Generation, json, ct).ConfigureAwait(false))
            : ComfyWorkflow.FromApi(json);

    private static async Task<ToolResult> WaitForAsync(JsonObject args, ComfyHub hub, ComfyApi api, string promptId, ToolContext ctx, CancellationToken ct)
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
        if (saved.Count == 0 && files.Count > 0 && job.Status == "success")
            sb.AppendLine("(These are previews in ComfyUI's temp folder, which it empties when it restarts: add a save node, or copy them with save_to, to keep them.)");
        foreach (var text in job.Texts.Take(10))
            sb.AppendLine("Text it gave: " + text.Clip(2000));
        if (preview.Image is not null)
            sb.Append(preview.Caption);
        var resultText = sb.ToString().TrimEnd();
        return job.Status == "success"
            ? new ToolResult(resultText, preview.Image, ImageCaption: preview.Image is null ? null : "What the job made:", Files: paths)
            : new ToolResult(resultText, preview.Image, IsError: true, ImageCaption: preview.Image is null ? null : "What the job made before it stopped:", Files: paths);
    }
}
