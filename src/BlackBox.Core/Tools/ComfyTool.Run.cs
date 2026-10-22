using System.Text;
using System.Text.Json.Nodes;
using BlackBox.Core.Comfy;
using BlackBox.Core.Media;

namespace BlackBox.Core.Tools;

public sealed partial class ComfyTool
{
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
        if (ComfyGuides.IsLtx(workflow) && !ComfyGuides.StartsFromPicture(workflow))
            after.Add(TextToVideoNote);
        if (CameraNote(workflow, result.Text) is { } camera)
            after.Add(camera);
        if (VideoGuideFor(workflow) is { } guide)
            after.Add(guide);
        return after.Count > 0 ? result with { Text = result.Text + "\n\n" + string.Join("\n\n", after) } : result;
    }

    /// <summary>
    /// LTX drawing a video's picture from words alone. The user found LTX good at animating and
    /// poor at drawing: the picture comes from an image model, and LTX animates it.
    /// </summary>
    private const string TextToVideoNote = "This LTX workflow draws the video's picture itself from words, which LTX does badly. Unless the user asked for text to video, " +
                                           "make the picture with an image model, look at it, and animate it with an image-to-video template (first and last frames when where it ends matters).";

    /// <summary>
    /// A still camera asked for and the whole picture measured moving. Asked for "static locked-off
    /// camera" on a fox trotting across snow, LTX tracked the fox; the measured movement said the
    /// whole picture moved left, and qwen3.8 checked the fox's way and called the clip right.
    /// </summary>
    internal static string? CameraNote(ComfyWorkflow workflow, string result)
    {
        if (!ComfyGuides.IsLtx(workflow) || WholeClip().Match(result) is not { Success: true } whole || !whole.Value.Contains("the whole picture", StringComparison.Ordinal))
            return null;
        var prompts = string.Join(" ", workflow.All().SelectMany(n => n.Node["inputs"] is JsonObject inputs
            ? inputs.Where(i => i.Key is "text" or "prompt" or "positive" && i.Value is JsonValue v && v.TryGetValue<string>(out _)).Select(i => i.Value!.GetValue<string>())
            : []));
        return StillCamera().IsMatch(prompts)
            ? "The prompt asks for a still camera, and the measured movement has the whole picture moving: the camera moved. If it matters, say again in the prompt " +
              "that the camera is locked off and what stays put in the frame, and run it again."
            : null;
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"Over the whole clip:[^\n]*")]
    private static partial System.Text.RegularExpressions.Regex WholeClip();

    [System.Text.RegularExpressions.GeneratedRegex(@"\b(static|locked[- ]off|locked camera|still camera|tripod|camera (is )?(still|fixed)|camera does not move|camera holds)\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex StillCamera();

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
        "It then tiles without a seam, where a cross-fade of the edges (blend_edges, mirror) leaves doubled, blurred shapes along them. " +
        // A tileset's grass and water came out lighter in the middle: tiled, a grid of light squares.
        "Ask for flat, even light with no vignette: a lighter middle or darker corners show as a grid of squares when tiled, whatever the seams.";

    [System.Text.RegularExpressions.GeneratedRegex(@"seamless|tileable|tileset|tiling", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex SeamlessWords();

    [System.Text.RegularExpressions.GeneratedRegex(@"video|animat|\bclip|movie|\bfilm|\bltx|\bwan\b|\bi2v|\bt2v", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex VideoWords();

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
        var repaints = workflow.All().Any(n => ComfyWorkflow.ClassOf(n.Node) == "SetLatentNoiseMask" && !Edgeless(n.Node));
        return repaints && !types.Contains("DifferentialDiffusion")
            ? "Note: " + MaskAdvice + " Look at where the new part meets the old in the result."
            : null;

        // A mask of one value all over has no edge: LTX's picture-and-sound-to-video templates keep
        // the given sound so, a SolidMask on the audio latent, and were told about edges.
        bool Edgeless(JsonObject setMask) =>
            setMask["inputs"] is JsonObject inputs &&
            (workflow.SourceOf(inputs["mask"]) is { } mask && ComfyWorkflow.ClassOf(mask) == "SolidMask" ||
             workflow.SourceOf(inputs["samples"]) is { } samples && ComfyWorkflow.ClassOf(samples).Contains("Audio", StringComparison.Ordinal));
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
            return new ToolResult($"The job (prompt_id {promptId}) is still {(job.Status == "queued" ? "waiting" : "running")} after {Chats.ChatTimes.Stopwatch(job.Elapsed)}" +
                                  (job.Progress is { } p ? $": {p}" : "") + ". " +
                                  (ctx.UserMessage.IsCancellationRequested ? "The user wrote to you meanwhile; read that first. " : "") +
                                  $"It goes on in ComfyUI; wait for it with action=status prompt_id={promptId}" +
                                  (ctx.UserMessage.IsCancellationRequested ? $", or, if what the user wrote changes it, stop it with action=cancel prompt_id={promptId} (not action=stop, which stops ComfyUI)." : "."));

        var dirs = await ComfyHub.FoldersAsync(api, ct).ConfigureAwait(false) ?? [];
        var files = await ComfyRunner.LocateAsync(api, job.Files, dirs, Path.Combine(ctx.ScratchDirectory ?? Path.GetTempPath(), "comfyui", "outputs"), ct).ConfigureAwait(false);
        // Some nodes say where they saved a file instead of listing it (Trellis2's mesh export
        // gives its .glb's path as text): those files are results too.
        foreach (var path in TextFiles(job.Texts, dirs))
            if (!files.Any(f => string.Equals(f.LocalPath, path, StringComparison.OrdinalIgnoreCase)))
                files.Add(new ComfyOutput("", Path.GetFileName(path), "", "output", path));
        // Previews ComfyUI only keeps in its temp folder are shown, but only saved files are results.
        var saved = files.Where(f => f.Type == "output").ToList();
        var shown = (saved.Count > 0 ? saved : files).Where(f => f.LocalPath is not null).ToList();
        var paths = shown.Select(f => f.LocalPath!).ToList();
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
            sb.AppendLine($"Done in {Chats.ChatTimes.Stopwatch(job.Elapsed)}. " + (paths.Count == 0 ? "It made no files." : paths.Count == 1 ? "It made:" : $"It made {paths.Count} files:"));
        else
            sb.AppendLine((job.Status == "interrupted" ? "The job was stopped" : "The job failed") + $" after {Chats.ChatTimes.Stopwatch(job.Elapsed)}. {job.Error}" +
                          (paths.Count > 0 ? "\nIt made these before that:" : ""));
        var preview = MediaPreview.ForFiles(paths, ctx.Media, ctx.SeesImages);
        var sounds = preview.Lines.Select((line, i) => i < shown.Count && shown[i].Kind is "video" or "audio" && HasSound(line) ? NameInComfy(shown[i], dirs) : null).ToList();
        var (model, heard) = job.Status == "success" && sounds.Any(s => s is not null)
            ? await HearAsync(hub, api, sounds, ctx, ct).ConfigureAwait(false)
            : (null, new Dictionary<int, string>());
        for (var i = 0; i < preview.Lines.Count; i++)
        {
            sb.Append("- ").AppendLine(preview.Lines[i]);
            if (heard.TryGetValue(i, out var words))
                sb.AppendLine(words.Trim('(', ')', '.', ' ').Equals("no words", StringComparison.OrdinalIgnoreCase)
                    ? $"  No words are said or sung in it ({model} listened to it)."
                    : $"  Its words, as {model} heard them: \"{words.ReplaceLineEndings(" ").Clip(1500)}\" Check them against the words asked for.");
        }
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

    /// <summary>Whether a file's line from <see cref="MediaPreview"/> says it has sound, and sound that isn't silent all through.</summary>
    internal static bool HasSound(string line) =>
        !line.Contains("silent throughout", StringComparison.Ordinal) &&
        (line.Contains(", with sound", StringComparison.Ordinal) || line.Contains(": sound ", StringComparison.Ordinal));

    /// <summary>The name LoadAudio knows a made file by ("video/clip_00001_.mp4 [output]"), or null when it isn't in the output folder.</summary>
    internal static string? NameInComfy(ComfyOutput file, IReadOnlyDictionary<string, string> dirs)
    {
        if (file.Type != "output")
            return null;
        if (file.NodeId.Length > 0)
            return (file.Subfolder.Length > 0 ? file.Subfolder.Replace('\\', '/') + "/" : "") + file.Filename + " [output]";
        // A file a node named in its text: where it is in the output folder, if it is in it.
        if (file.LocalPath is null || !dirs.TryGetValue("output", out var output))
            return null;
        var relative = Path.GetRelativePath(output, file.LocalPath);
        return relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative) ? null : relative.Replace('\\', '/') + " [output]";
    }

    /// <summary>
    /// A small Gemma 4 (E2B or E4B) ComfyUI can load to hear sound with: its Generate Text node
    /// takes audio, and LTX-2.5's templates bring gemma4_e2b along. Null when there is none.
    /// </summary>
    internal static string? HearingModel(ComfyNodeLibrary library)
    {
        if (library.Find("TextGenerate")?.Input("audio") is null || library.Find("LoadAudio") is null || library.Find("PreviewAny") is null)
            return null;
        return (library.Find("CLIPLoader")?.Input("clip_name")?.Choices ?? [])
            .Where(c => HearingModelName().IsMatch(c))
            .OrderBy(c => c.Contains("e2b", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .FirstOrDefault();
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"gemma[-_ ]?4[^/\\]*?e[24]b", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex HearingModelName();

    internal const string HearingPrompt =
        "Write down word for word what is said or sung in this audio. Reply with those words only. If no words are said or sung, reply exactly: (no words)";

    /// <summary>The workflow in which <paramref name="model"/> writes down what is said or sung in the sound LoadAudio knows by <paramref name="name"/>.</summary>
    internal static ComfyWorkflow HearingWorkflow(ComfyNodeLibrary library, string model, string name)
    {
        var types = library.Find("CLIPLoader")?.Input("type")?.Choices ?? [];
        return ComfyWorkflow.FromApi(new JsonObject
        {
            ["1"] = new JsonObject { ["class_type"] = "LoadAudio", ["inputs"] = new JsonObject { ["audio"] = name } },
            // The type doesn't matter to a Gemma 4 (ComfyUI knows it by its weights); LTX's templates load it as ltxv.
            ["2"] = new JsonObject { ["class_type"] = "CLIPLoader", ["inputs"] = new JsonObject { ["clip_name"] = model, ["type"] = types.Contains("ltxv") || types.Count == 0 ? "ltxv" : types[0] } },
            ["3"] = new JsonObject
            {
                ["class_type"] = "TextGenerate",
                ["inputs"] = new JsonObject
                {
                    ["clip"] = new JsonArray("2", 0), ["audio"] = new JsonArray("1", 0), ["prompt"] = HearingPrompt, ["max_length"] = 300, ["sampling_mode"] = "off",
                },
            },
            ["4"] = new JsonObject { ["class_type"] = "PreviewAny", ["inputs"] = new JsonObject { ["source"] = new JsonArray("3", 0) } },
        });
    }

    /// <summary>
    /// What is said or sung in each made file that has sound, by its place in <paramref name="names"/>
    /// (null for those not to hear), as a small Gemma 4 that ComfyUI has hears it; nothing when none
    /// can. Asked for a fisherman saying one line, LTX-2.5 had him read out half the prompt after
    /// it, and the model, which saw frames and loudness only, called the video right.
    /// </summary>
    private static async Task<(string? Model, Dictionary<int, string> Heard)> HearAsync(ComfyHub hub, ComfyApi api, IReadOnlyList<string?> names, ToolContext ctx, CancellationToken ct)
    {
        var heard = new Dictionary<int, string>();
        var library = await hub.LibraryAsync(api, ct).ConfigureAwait(false);
        if (HearingModel(library) is not { } model)
            return (null, heard);
        for (var i = 0; i < names.Count && heard.Count < 4; i++)
        {
            if (names[i] is not { } name)
                continue;
            var workflow = HearingWorkflow(library, model, name);
            workflow.FillAdvancedDefaults(library);
            // A Generate Text node of another make than this one takes: no hearing then.
            if (workflow.Validate(library).Count > 0)
                break;
            ctx.Progress?.Invoke($"Listening to {Path.GetFileName(name[..^" [output]".Length])}…");
            var clientId = "blackbox-" + Guid.NewGuid().ToString("N")[..12];
            var (promptId, _) = await ComfyRunner.QueueAsync(api, workflow, clientId, ct).ConfigureAwait(false);
            if (promptId is null)
                break;
            var job = await ComfyRunner.WaitAsync(api, promptId, clientId, new Dictionary<string, string>(), TimeSpan.FromMinutes(3), null, ctx.UserMessage, ct).ConfigureAwait(false);
            // Still going (a queue of the user's ahead of it, or the user wrote): the result goes without it.
            if (job.Status != "success")
                break;
            if (job.Texts.FirstOrDefault(t => t.Trim().Length > 0) is { } words)
                heard[i] = words.Trim();
        }
        return (model, heard);
    }
}
