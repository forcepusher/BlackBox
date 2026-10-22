using System.Text;
using System.Text.Json.Nodes;
using BlackBox.Core.Comfy;

namespace BlackBox.Core.Tools;

public sealed partial class ComfyTool
{
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
            if (ComfyGuides.IsLtx(workflow) && !ComfyGuides.StartsFromPicture(workflow))
                sb.Append("\n").Append(TextToVideoNote);
            if (VideoGuideFor(workflow) is { } guide)
                sb.Append("\n\n").Append(guide);
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
        if (VideoWords().IsMatch(search))
            lines.AppendLine("For a video, make its picture first with an image model and look at it, then animate it with an image-to-video template " +
                             "(first and last frames when where it ends matters); text to video only when the user asks for it. action=guide search=\"video\" says how to write its prompt.");
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
}
