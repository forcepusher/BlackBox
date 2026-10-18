using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace BlackBox.Core.Comfy;

/// <summary>A workflow that can't be used as given: not JSON, or not a workflow.</summary>
public sealed class ComfyWorkflowException(string message) : Exception(message);

/// <summary>
/// A workflow in ComfyUI's API format, the one /prompt runs: node id → {class_type, inputs}, an
/// input holding a value or a link ["other id", output index]. The format the editor saves (nodes
/// and links, with positions) is turned into this one by ComfyUI's own page (see <see cref="ComfyFrontend"/>).
/// </summary>
public sealed class ComfyWorkflow
{
    internal static readonly JsonSerializerOptions Compact = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    internal static readonly JsonSerializerOptions Indented = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public JsonObject Nodes { get; }

    private ComfyWorkflow(JsonObject nodes) => Nodes = nodes;

    /// <summary>Whether this JSON is a workflow as the editor saves it (nodes and links) rather than the API format.</summary>
    public static bool IsEditorFormat(JsonNode? json) => json is JsonObject o && o["nodes"] is JsonArray && o["links"] is not null;

    /// <summary>
    /// Reads an API-format workflow: the bare map of nodes, or one wrapped as {"prompt": …} or
    /// {"output": …}. Node ids become strings and links ["4", 0], as /prompt wants them.
    /// </summary>
    public static ComfyWorkflow FromApi(JsonNode? json)
    {
        if (json is JsonObject wrapper)
            foreach (var key in new[] { "prompt", "output" })
                if (wrapper[key] is JsonObject inner && LooksLikeNodes(inner))
                    json = inner;
        if (json is not JsonObject nodes || nodes.Count == 0)
            throw new ComfyWorkflowException("That isn't a workflow: expected a JSON object of nodes, {\"1\": {\"class_type\": \"…\", \"inputs\": {…}}, …}.");
        if (!LooksLikeNodes(nodes))
            throw new ComfyWorkflowException("That isn't an API-format workflow: each node needs \"class_type\" and \"inputs\", as in {\"3\": {\"class_type\": \"KSampler\", \"inputs\": {\"seed\": 1, \"model\": [\"4\", 0]}}}.");
        var copy = (JsonObject)nodes.DeepClone();
        foreach (var (_, node) in copy)
        {
            if (node is not JsonObject n)
                continue;
            if (n["inputs"] is not JsonObject inputs)
                n["inputs"] = inputs = [];
            foreach (var (name, value) in inputs.ToList())
                if (value is JsonArray link && IsLink(link))
                    inputs[name] = new JsonArray(LinkSource(link)!, LinkOutput(link)!.Value);
        }
        return new ComfyWorkflow(copy);
    }

    private static bool LooksLikeNodes(JsonObject o) => o.Count > 0 && o.All(kv => kv.Value is JsonObject n && n["class_type"] is JsonValue);

    /// <summary>Reads a workflow the model wrote: JSON text, or JSON already parsed by the tool call.</summary>
    public static JsonNode ParseText(string text)
    {
        try
        {
            return JsonNode.Parse(text, documentOptions: new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip })
                   ?? throw new ComfyWorkflowException("The workflow is empty.");
        }
        catch (JsonException ex)
        {
            throw new ComfyWorkflowException($"The workflow isn't valid JSON: {ex.Message}");
        }
    }

    /// <summary>A link: ["4", 0] or [4, 0].</summary>
    public static bool IsLink(JsonNode? value) =>
        value is JsonArray { Count: 2 } a && LinkSource(a) is not null && LinkOutput(a) is not null;

    private static string? LinkSource(JsonArray a) => a[0] switch
    {
        JsonValue v when v.TryGetValue<string>(out var s) => s,
        JsonValue v when v.TryGetValue<long>(out var l) => l.ToString(CultureInfo.InvariantCulture),
        _ => null,
    };

    private static int? LinkOutput(JsonArray a) => a[1] is JsonValue v && v.TryGetValue<int>(out var i) ? i
        : a[1] is JsonValue s && s.TryGetValue<string>(out var str) && int.TryParse(str, out var parsed) ? parsed : null;

    public IEnumerable<(string Id, JsonObject Node)> All() =>
        Nodes.Where(kv => kv.Value is JsonObject).Select(kv => (kv.Key, (JsonObject)kv.Value!));

    public static string ClassOf(JsonObject node) => (string?)node["class_type"] ?? "";

    public static string? TitleOf(JsonObject node) => node["_meta"]?["title"] is JsonValue t && t.TryGetValue<string>(out var s) ? s : null;

    public string ToJson(bool indented = false) => Nodes.ToJsonString(indented ? Indented : Compact);

    /// <summary>
    /// Applies changes such as {"6.text": "a cat", "3.seed": 7}: the part before the first dot is
    /// the node id (ids may contain colons: "129:93.text"). Returns what went wrong, if anything.
    /// </summary>
    public List<string> Set(JsonObject changes, ComfyNodeLibrary? library)
    {
        var problems = new List<string>();
        foreach (var (key, value) in changes)
        {
            // {"6": {"text": "a cat"}} is the same change written as nested objects.
            if (value is JsonObject nested && !key.Contains('.') && Nodes[key] is JsonObject)
            {
                problems.AddRange(Set(new JsonObject(nested.Select(kv => KeyValuePair.Create(key + "." + kv.Key, kv.Value?.DeepClone()))), library));
                continue;
            }
            var dot = key.IndexOf('.');
            // {"433:443": false} for a template's "Enable Lightning LoRA" switch, a PrimitiveBoolean:
            // a node holding a single value is set by its id alone (qwen3.8 wrote it so in two runs).
            if (dot < 0 && Nodes[key]?["inputs"] is JsonObject own && own.Where(kv => kv.Value is not JsonArray).ToList() is [var only])
            {
                own[only.Key] = value?.DeepClone();
                continue;
            }
            if (dot <= 0 || dot == key.Length - 1)
            {
                problems.Add($"set \"{key}\": write it as \"node id.input name\", e.g. \"6.text\".");
                continue;
            }
            var (id, input) = (key[..dot], key[(dot + 1)..]);
            if (Nodes[id] is not JsonObject node)
            {
                var near = ComfyNodeLibrary.Closest(id, Nodes.Select(kv => kv.Key), 3);
                problems.Add($"set \"{key}\": there is no node \"{id}\"{(near.Count > 0 ? $"; did you mean {string.Join(" or ", near.Select(n => $"\"{n}\""))}?" : ".")}");
                continue;
            }
            var inputs = (JsonObject)node["inputs"]!;
            var def = library?.Find(ClassOf(node));
            if (inputs[input] is null && def is not null && def.Input(input) is null && !IsDynamicInput(def, input))
            {
                var near = ComfyNodeLibrary.Closest(input, def.Inputs.Select(i => i.Name), 2);
                problems.Add($"set \"{key}\": {ClassOf(node)} has no input \"{input}\"" +
                             (near.Count > 0 ? $"; did you mean \"{near[0]}\"?" : ".") + $" Its inputs: {string.Join(", ", def.Inputs.Select(i => i.Name))}.");
                continue;
            }
            inputs[input] = value?.DeepClone();
        }
        return problems;
    }

    /// <summary>Changes as <see cref="Set"/> takes them, each under its "node id.input" key (the nested form flattened).</summary>
    public static Dictionary<string, JsonNode?> Changes(JsonObject changes)
    {
        var flat = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
        foreach (var (key, value) in changes)
        {
            if (value is JsonObject nested && !key.Contains('.'))
                foreach (var (input, v) in nested)
                    flat[key + "." + input] = v?.DeepClone();
            else
                flat[key] = value?.DeepClone();
        }
        return flat;
    }

    /// <summary>The value an input ("node id.input") has, or null when the node or input isn't there.</summary>
    public JsonNode? ValueOf(string key)
    {
        var dot = key.IndexOf('.');
        return dot > 0 ? Nodes[key[..dot]]?["inputs"]?[key[(dot + 1)..]] : null;
    }

    private static bool IsDynamicInput(ComfyNodeDef def, string input)
    {
        var dot = input.IndexOf('.');
        return dot > 0 && def.Input(input[..dot]) is { IsDynamic: true };
    }

    /// <summary>
    /// Checks the workflow against the server's node types before it is queued, so the model hears
    /// about every wrong name at once and with the right one next to it, rather than ComfyUI's
    /// first complaint only (it stops at the first unknown node, and ignores inputs it doesn't know).
    /// </summary>
    /// <param name="uploaded">Inputs ("id.input") whose file was just uploaded: ComfyUI knows the file, the cached list of choices doesn't yet.</param>
    /// <param name="warnings">
    /// Collects what doesn't stop a run but may not be meant: an input the node doesn't have, which
    /// ComfyUI ignores, next to one it has that isn't set (a misspelt optional input).
    /// </param>
    public List<string> Validate(ComfyNodeLibrary library, IReadOnlySet<string>? uploaded = null, List<string>? warnings = null)
    {
        var problems = new List<string>();
        var hasOutput = false;
        var classNames = library.All.Select(n => n.Name).ToList();
        foreach (var (id, node) in All())
        {
            var cls = ClassOf(node);
            var def = library.Find(cls);
            var where = $"node {id} {cls}";
            if (def is null)
            {
                var near = ComfyNodeLibrary.Closest(cls, classNames, 3);
                var byDisplay = library.All.Where(n => n.DisplayName.Equals(cls, StringComparison.OrdinalIgnoreCase)).Select(n => n.Name).ToList();
                problems.Add($"{where}: ComfyUI has no node type \"{cls}\"" +
                             (byDisplay.Count > 0 ? $"; that is the shown name of {string.Join(", ", byDisplay)}, use that as class_type." :
                              near.Count > 0 ? $"; did you mean {string.Join(" or ", near)}?" :
                              ". Look the right one up with action=nodes; if it comes from a custom node pack, that pack isn't installed."));
                continue;
            }
            hasOutput |= def.IsOutput;
            var inputs = node["inputs"] as JsonObject ?? [];
            foreach (var (name, value) in inputs)
            {
                var input = def.Input(name);
                if (input is null)
                {
                    // ComfyUI ignores inputs a node doesn't have (and the editor adds some of its
                    // own: an audio player's audioUI, a format's options for nodes that take any).
                    // Only one next to a declared input that isn't set looks like a slip.
                    if (IsDynamicInput(def, name) || warnings is null)
                        continue;
                    var unset = def.Inputs.Where(i => !inputs.ContainsKey(i.Name)).Select(i => i.Name).ToList();
                    var near = ComfyNodeLibrary.Closest(name, unset, 1);
                    if (near.Count > 0 && !EditorInputs.Contains(name))
                        warnings.Add($"{where} has no input \"{name}\", so ComfyUI ignores it; did you mean \"{near[0]}\"?");
                    continue;
                }
                if (IsLink(value))
                {
                    var link = (JsonArray)value!;
                    var (from, output) = (LinkSource(link)!, LinkOutput(link)!.Value);
                    if (Nodes[from] is not JsonObject source)
                    {
                        problems.Add($"{where}: input \"{name}\" links to node \"{from}\", which isn't in the workflow.");
                        continue;
                    }
                    if (library.Find(ClassOf(source)) is not { } sourceDef)
                        continue; // reported with that node
                    if (output < 0 || output >= sourceDef.Outputs.Count)
                    {
                        problems.Add($"{where}: input \"{name}\" takes output {output} of node {from} {sourceDef.Name}, which has " +
                                     (sourceDef.Outputs.Count == 0 ? "no outputs." : $"outputs {string.Join(", ", sourceDef.Outputs.Select((o, n) => $"{n} {o.Name}"))}."));
                        continue;
                    }
                    var got = sourceDef.Outputs[output].Type;
                    if (!Compatible(got, input))
                        problems.Add($"{where}: input \"{name}\" needs {input.Type}, but output {output} of node {from} {sourceDef.Name} is {got}.");
                    continue;
                }
                if (input.IsDynamic)
                    continue;
                if (!input.IsWidget)
                {
                    // Custom input types may have a widget of their own in the editor (a recorder,
                    // an image comparer); the common ones are only ever outputs of other nodes.
                    if (SocketTypes.Contains(input.Type))
                        problems.Add($"{where}: input \"{name}\" takes a link from another node's {input.Type} output, as [\"node id\", output index], not a value.");
                    continue;
                }
                if (CheckValue(input, value, uploaded?.Contains(id + "." + name) == true) is { } bad)
                    problems.Add($"{where}: {bad}");
            }
            foreach (var missing in def.Inputs.Where(i => i.Required && !i.IsDynamic && inputs[i.Name] is null && !inputs.ContainsKey(i.Name)))
                problems.Add($"{where}: required input \"{missing.Name}\" is missing ({missing.Describe(12)}).");
        }
        if (!hasOutput && problems.Count == 0)
            problems.Add("Nothing in the workflow saves or shows a result: add an output node such as SaveImage, SaveVideo, SaveAudio or SaveGLB.");
        return problems;
    }

    /// <summary>
    /// Sets the required inputs the editor keeps out of sight ("advanced", with a default) that the
    /// workflow leaves out, to their defaults, as the editor does when it sends a workflow; returns
    /// what was set ("5.resolution_steps = 1"). New ComfyUI versions add such inputs to old nodes:
    /// ImageScaleToTotalPixels got resolution_steps, and a workflow written before was refused for it.
    /// </summary>
    public List<string> FillAdvancedDefaults(ComfyNodeLibrary library)
    {
        var filled = new List<string>();
        foreach (var (id, node) in All())
        {
            if (library.Find(ClassOf(node)) is not { } def || node["inputs"] is not JsonObject inputs)
                continue;
            foreach (var input in def.Inputs.Where(i => i.Required && i.IsWidget && !inputs.ContainsKey(i.Name) &&
                                                        i.Extra["advanced"] is JsonValue a && a.TryGetValue<bool>(out var advanced) && advanced && i.Extra["default"] is not null))
            {
                inputs[input.Name] = input.Extra["default"]!.DeepClone();
                filled.Add($"{id}.{input.Name} = {input.Extra["default"]!.ToJsonString()}");
            }
        }
        return filled;
    }

    /// <summary>Inputs the editor adds to the workflow it sends, which no node declares.</summary>
    private static readonly HashSet<string> EditorInputs = ["audioUI", "videoUI", "image", "upload", "previewMode", "preview_text", "preview_markdown", "camera_info"];

    /// <summary>Types that only ever come from another node's output: a value typed in for one of these is a mistake.</summary>
    private static readonly HashSet<string> SocketTypes =
    [
        "MODEL", "CLIP", "VAE", "CONDITIONING", "LATENT", "IMAGE", "MASK", "AUDIO", "VIDEO", "CLIP_VISION", "CLIP_VISION_OUTPUT",
        "CONTROL_NET", "UPSCALE_MODEL", "LATENT_UPSCALE_MODEL", "SIGMAS", "NOISE", "GUIDER", "SAMPLER", "STYLE_MODEL", "GLIGEN",
        "MESH", "VOXEL", "AUDIO_ENCODER", "AUDIO_ENCODER_OUTPUT", "MODEL_PATCH", "HOOKS", "WEBCAM",
    ];

    /// <summary>Whether an output of type <paramref name="got"/> can feed <paramref name="input"/>, as ComfyUI judges it.</summary>
    private static bool Compatible(string got, ComfyInputDef input)
    {
        var want = input.Choices is not null ? "COMBO" : input.Type;
        if (got == want || got == "*" || want == "*" || got.StartsWith("COMFY_", StringComparison.Ordinal) || want.StartsWith("COMFY_", StringComparison.Ordinal))
            return true;
        // A primitive can feed a choice or a text.
        if (want == "COMBO" && got is "STRING" or "COMBO")
            return true;
        var a = got.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var b = want.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        return a.Intersect(b).Any();
    }

    /// <summary>Why a typed-in value doesn't fit its input, or null when it does.</summary>
    private static string? CheckValue(ComfyInputDef input, JsonNode? value, bool uploaded)
    {
        var name = input.Name;
        if (input.Choices is not null)
        {
            var text = value is JsonValue v && v.TryGetValue<string>(out var s) ? s : value?.ToJsonString() ?? "";
            if (input.Choices.Contains(text) || uploaded || input.Choices.Count == 0)
                return null;
            // A number among numbers: 1 is one of [0.5, 1.0, 2.0] to ComfyUI (Python's 1 == 1.0).
            if (value is JsonValue number && number.GetValueKind() == JsonValueKind.Number && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) &&
                input.Choices.Any(c => double.TryParse(c, NumberStyles.Float, CultureInfo.InvariantCulture, out var x) && x == n))
                return null;
            // ComfyUI accepts "file.png [output]" for files in its other folders.
            if (input.TakesInputFile && (text.EndsWith(" [output]", StringComparison.Ordinal) || text.EndsWith(" [temp]", StringComparison.Ordinal) || text.EndsWith(" [input]", StringComparison.Ordinal)))
                return null;
            if (input.TakesInputFile)
                return $"\"{name}\" is \"{text.Clip(120)}\", which isn't in ComfyUI's input folder (a template's example file, or a name made up): " +
                       "give the full local path of a file of your own (it is uploaded)" +
                       (input.Choices.Count <= 20 ? $", or one of: {string.Join(", ", input.Choices)}." : ".");
            var near = ComfyNodeLibrary.Closest(text, input.Choices, 5);
            var all = input.Choices.Count <= 40 ? $" Its choices: {string.Join(", ", input.Choices)}." : $" ({input.Choices.Count} choices; list them with action=nodes names=[\"…\"].)";
            var file = LooksLikeFile(text) || input.Choices.Take(5).Any(LooksLikeFile);
            return $"\"{name}\" is \"{text.Clip(120)}\", which {(file ? "isn't installed" : "isn't one of its choices")}" +
                   (near.Count > 0 ? $"; closest: {string.Join(", ", near)}." : ".") + all;
        }
        if (value is JsonObject wrapped && wrapped["__value__"] is { } inner)
            value = inner; // a list value, wrapped so it isn't read as a link
        switch (input.Type)
        {
            case "INT":
            case "FLOAT":
            {
                // ComfyUI converts what it can: true is 1, "20" is 20.
                double? d = value is JsonValue v && v.TryGetValue<double>(out var x) ? x
                    : value is JsonValue b && b.GetValueKind() is JsonValueKind.True or JsonValueKind.False ? (b.GetValueKind() == JsonValueKind.True ? 1 : 0)
                    : value is JsonValue sv && sv.TryGetValue<string>(out var str) && double.TryParse(str, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
                if (d is null)
                    return $"\"{name}\" must be a number ({input.Type}), not {ComfyNodeLibrary.Short(value, 60)}.";
                if (input.Type == "INT" && Math.Abs(d.Value - Math.Round(d.Value)) > 1e-9)
                    return $"\"{name}\" must be a whole number, not {d.Value.ToString(CultureInfo.InvariantCulture)}.";
                if (input.Min is { } min && d < min)
                    return $"\"{name}\" is {d.Value.ToString(CultureInfo.InvariantCulture)}, below its least value {min.ToString(CultureInfo.InvariantCulture)}.";
                if (input.Max is { } max && d > max)
                    return $"\"{name}\" is {d.Value.ToString(CultureInfo.InvariantCulture)}, above its greatest value {max.ToString(CultureInfo.InvariantCulture)}.";
                return null;
            }
            case "BOOLEAN":
                // ComfyUI takes bool() of anything; only a list or an object is surely a mistake.
                return value is JsonValue ? null : $"\"{name}\" must be true or false, not {ComfyNodeLibrary.Short(value, 60)}.";
            default:
                return null;
        }
    }

    private static bool LooksLikeFile(string s) =>
        Path.GetExtension(s).ToLowerInvariant() is ".safetensors" or ".ckpt" or ".pt" or ".pth" or ".bin" or ".gguf" or ".onnx" or ".sft"
            or ".png" or ".jpg" or ".jpeg" or ".webp" or ".mp4" or ".wav" or ".mp3" or ".flac";

    /// <summary>
    /// Inputs that name a file on this PC by its full path where ComfyUI wants a file from its
    /// input folder (LoadImage, LoadAudio, LoadVideo…): those are uploaded before the run.
    /// </summary>
    public List<(string Id, string Input, string Path)> LocalFiles(ComfyNodeLibrary library, Func<string, string> resolve)
    {
        var files = new List<(string, string, string)>();
        foreach (var (id, node) in All())
        {
            if (library.Find(ClassOf(node)) is not { } def || node["inputs"] is not JsonObject inputs)
                continue;
            foreach (var (name, value) in inputs)
            {
                if (value is not JsonValue v || !v.TryGetValue<string>(out var text) || def.Input(name) is not { } input)
                    continue;
                if (!(input.TakesInputFile || (input.Choices is not null && name is "image" or "audio" or "video" or "file")))
                    continue;
                if (!System.IO.Path.IsPathFullyQualified(text) && !text.StartsWith('~'))
                    continue;
                files.Add((id, name, resolve(text)));
            }
        }
        return files;
    }

    /// <summary>The workflow one node per line, as JSON the model can copy from, with each node's title.</summary>
    public string Listing()
    {
        var sb = new StringBuilder("{\n");
        var nodes = All().ToList();
        for (var i = 0; i < nodes.Count; i++)
        {
            var (id, node) = nodes[i];
            var line = new JsonObject
            {
                ["class_type"] = ClassOf(node),
                ["inputs"] = node["inputs"]?.DeepClone(),
            };
            if (TitleOf(node) is { } title && title != ClassOf(node))
                line["_meta"] = new JsonObject { ["title"] = title };
            sb.Append(JsonValue.Create(id).ToJsonString(Compact)).Append(": ").Append(line.ToJsonString(Compact)).AppendLine(i < nodes.Count - 1 ? "," : "");
        }
        return sb.Append('}').ToString();
    }

    /// <summary>
    /// The inputs a run usually changes, to point the model at them: prompts, input files, sizes,
    /// lengths and seeds. "129:93.text (Positive Prompt)".
    /// </summary>
    public List<string> UsualChanges(ComfyNodeLibrary library)
    {
        var picks = new List<string>();
        foreach (var (id, node) in All())
        {
            if (library.Find(ClassOf(node)) is not { } def || node["inputs"] is not JsonObject inputs)
                continue;
            var title = TitleOf(node);
            foreach (var (name, value) in inputs)
            {
                if (IsLink(value) || def.Input(name) is not { } input)
                    continue;
                string? why = null;
                if (input.TakesInputFile)
                    why = value is JsonValue fv && fv.TryGetValue<string>(out var file) && input.Choices?.Contains(file) == false
                        ? "an input file; the template's example isn't there, give a full local path"
                        : "an input file";
                else if (input.Type == "STRING" && (ComfyNodeLibrary.IsTrue(input.Extra["multiline"]) || name is "text" or "prompt" or "lyrics" or "tags" or "positive" or "negative" or "value"))
                    why = "text";
                else if (input.Extra["control_after_generate"] is not null || name is "seed" or "noise_seed")
                    why = "seed";
                else if (name is "width" or "height" or "length" or "duration" or "seconds" or "frames" or "num_frames" or "batch_size" or "fps" or "frame_rate" or "steps")
                    why = name;
                else if (name == "filename_prefix")
                    why = "where it saves";
                if (why is not null)
                    picks.Add($"{id}.{name}" + (title is not null && title != def.Name && !why.Contains(';') ? $" ({title})" : $" ({why})"));
            }
        }
        return picks;
    }
}
