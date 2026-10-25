using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using BlackBox.Core.Comfy;

namespace BlackBox.Core.Video;

/// <summary>What a video job puts into its workflow: the prompt, seed, size, length and the files it starts from.</summary>
public sealed class VideoInputs
{
    public string? Prompt { get; init; }

    /// <summary>What to keep out; the template's own when null.</summary>
    public string? Negative { get; init; }

    public long? Seed { get; init; }
    public int? Width { get; init; }
    public int? Height { get; init; }
    public double? Seconds { get; init; }
    public int? Fps { get; init; }

    /// <summary>The picture a video starts on, and the one it ends on (full local paths).</summary>
    public string? FirstImage { get; init; }

    public string? LastImage { get; init; }

    /// <summary>The pictures an edit starts from: the first is the one changed, the others show what to keep (a character, a place).</summary>
    public IReadOnlyList<string> Pictures { get; init; } = [];

    /// <summary>A sound the video is made with (lip sync).</summary>
    public string? Audio { get; init; }

    /// <summary>A song's style and words.</summary>
    public string? Tags { get; init; }

    public string? Lyrics { get; init; }

    /// <summary>A song's tempo, when its style names one ("80 BPM"): ACE-Step's templates set their own (95, 120) otherwise.</summary>
    public int? Bpm { get; init; }

    /// <summary>Where ComfyUI saves what the job makes, under its output folder.</summary>
    public string? Prefix { get; init; }

    /// <summary>Whether a template's prompt enhancer (a small model rewriting the prompt) stays on.</summary>
    public bool PromptEnhance { get; init; }

    /// <summary>For a template with a "text to video" switch: on when there is no first frame.</summary>
    public bool? TextToVideo { get; init; }
}

/// <summary>
/// Finds, in a template's workflow, the inputs a video job sets (its prompt, seed, size, length,
/// frame rate, pictures and sound) and sets them. Templates name these their own way: LTX-2.5's keep
/// them in primitives titled "Prompt", "Width", "Duration" inside a subgraph, Z-Image's in the
/// text encoder and the empty latent, ACE-Step's on its text encoder; so they are found by title
/// first, then by following the sampler's positive conditioning back to the text it was made from,
/// then by the inputs' own names.
/// </summary>
public static partial class VideoRoles
{
    /// <summary>Sets what <paramref name="inputs"/> gives; returns what was set, and adds to <paramref name="missed"/> what had no place.</summary>
    public static List<string> Apply(ComfyWorkflow workflow, ComfyNodeLibrary library, VideoInputs inputs, List<string> missed)
    {
        var set = new List<string>();
        void Put(string id, string input, JsonNode? value)
        {
            if (workflow.Nodes[id]?["inputs"] is not JsonObject node)
                return;
            node[input] = value;
            var title = ComfyWorkflow.TitleOf((JsonObject)workflow.Nodes[id]!);
            set.Add($"{id}.{input}" + (title is not null ? $" ({title})" : ""));
        }

        if (inputs.Prompt is { } prompt)
        {
            if (PromptInput(workflow, library, negative: false) is { } at)
                Put(at.Id, at.Input, prompt);
            else
                missed.Add("the prompt (no text input found)");
        }
        if (inputs.Negative is { } negative)
        {
            var positive = PromptInput(workflow, library, negative: false);
            if (PromptInput(workflow, library, negative: true) is { } at && at.Id != positive?.Id)
                Put(at.Id, at.Input, negative);
        }

        if (inputs.Seed is { } seed)
        {
            var n = 0;
            foreach (var (id, input) in SeedInputs(workflow))
                Put(id, input, seed + n++);
            if (n == 0)
                missed.Add("the seed (no seed input found)");
        }

        if (inputs.Width is { } width && inputs.Height is { } height)
        {
            var sized = false;
            foreach (var (id, input, which) in SizeInputs(workflow))
            {
                Put(id, input, which == "width" ? width : height);
                sized = true;
            }
            if (!sized && inputs.Pictures.Count > 0)
                sized = SizeFromBlank(workflow, library, width, height, Put);
            if (!sized)
                missed.Add("the size (the template takes it from the picture it starts from)");
        }

        if (inputs.Fps is { } rate)
            foreach (var (id, input) in FpsInputs(workflow))
                Put(id, input, rate);

        if (inputs.Seconds is { } seconds)
        {
            var fps = inputs.Fps ?? 24;
            var timed = false;
            foreach (var (id, input, kind) in LengthInputs(workflow, library))
            {
                timed = true;
                Put(id, input, kind switch
                {
                    // Whole numbers as whole numbers: a 5.0 is a float to ComfyUI.
                    LengthKind.WholeSeconds => JsonValue.Create((int)Math.Ceiling(seconds - 1e-6)),
                    LengthKind.LtxFrames => JsonValue.Create(LtxFrames(seconds, fps)),
                    LengthKind.Frames => JsonValue.Create((int)Math.Ceiling(seconds * fps / 4 - 1e-6) * 4 + 1),
                    _ => JsonValue.Create(Math.Round(seconds, 2)),
                });
            }
            if (!timed)
                missed.Add("the length (no duration input found)");
        }

        SetPictures(workflow, library, inputs, Put, missed);

        if (inputs.Audio is { } audio)
        {
            var loader = workflow.All().FirstOrDefault(n => ComfyWorkflow.ClassOf(n.Node) is "LoadAudio" or "VHS_LoadAudioUpload");
            if (loader.Node is null)
                missed.Add("the sound (no LoadAudio node)");
            else
                Put(loader.Id, "audio", audio);
            // The sound given is already the part wanted: from its start.
            foreach (var (id, node) in workflow.All().Where(n => ComfyWorkflow.ClassOf(n.Node) == "TrimAudioDuration").ToList())
                if (node["inputs"]?["start_index"] is JsonValue)
                    Put(id, "start_index", 0);
        }

        if (inputs.Tags is not null || inputs.Lyrics is not null || inputs.Bpm is not null)
            foreach (var (id, node) in workflow.All().ToList())
            {
                var own = node["inputs"] as JsonObject ?? [];
                if (inputs.Tags is { } t && own["tags"] is JsonValue)
                    Put(id, "tags", t);
                if (inputs.Lyrics is { } l && own["lyrics"] is JsonValue)
                    Put(id, "lyrics", l);
                if (inputs.Bpm is { } bpm && own["bpm"] is JsonValue)
                    Put(id, "bpm", bpm);
            }

        foreach (var (id, node) in workflow.All().ToList())
        {
            if (ComfyWorkflow.ClassOf(node) != "PrimitiveBoolean" || node["inputs"]?["value"] is not JsonValue)
                continue;
            var title = ComfyWorkflow.TitleOf(node) ?? "";
            if (EnhanceTitle().IsMatch(title))
                Put(id, "value", inputs.PromptEnhance);
            else if (inputs.TextToVideo is { } t2v && TextToVideoTitle().IsMatch(title))
                Put(id, "value", t2v);
        }

        if (inputs.Prefix is { } prefix)
            foreach (var (id, node) in workflow.All().ToList())
                if (node["inputs"]?["filename_prefix"] is JsonValue)
                    Put(id, "filename_prefix", prefix);
        return set;
    }

    /// <summary>LTX takes 8n + 1 frames: the fewest that last the seconds asked for.</summary>
    internal static int LtxFrames(double seconds, int fps) => (int)Math.Ceiling(seconds * fps / 8 - 1e-6) * 8 + 1;

    // ---- the prompt ----------------------------------------------------------------------------

    /// <summary>
    /// The input that holds the prompt (or with <paramref name="negative"/>, what to keep out): one
    /// titled so (a primitive "Prompt", a text encoder "Positive Prompt"), else the text the main
    /// sampler's conditioning is made from.
    /// </summary>
    public static (string Id, string Input)? PromptInput(ComfyWorkflow workflow, ComfyNodeLibrary library, bool negative)
    {
        // By title; a node still called by its type's own name ("CLIP Text Encode (Prompt)", which
        // both the positive and the negative encoder are) says nothing. Primitives first: an
        // encoder titled so may take its text from one.
        (string Id, string Input)? titled = null;
        foreach (var (id, node) in workflow.All())
        {
            var title = ComfyWorkflow.TitleOf(node) ?? "";
            if (!PromptTitle().IsMatch(title) || NegativeWord().IsMatch(title) != negative || title == library.Find(ComfyWorkflow.ClassOf(node))?.DisplayName ||
                TextInput(node, library) is not { } input || ComfyWorkflow.IsLink(node["inputs"]?[input]))
                continue;
            if (ComfyWorkflow.ClassOf(node).StartsWith("Primitive", StringComparison.Ordinal))
                return (id, input);
            titled ??= (id, input);
        }
        if (titled is not null)
            return titled;
        foreach (var (_, start) in Samplers(workflow))
        {
            var side = negative ? "negative" : "positive";
            var from = start["inputs"]?[side];
            if (from is null && start["inputs"]?["guider"] is { } guider && workflow.SourceOf(guider) is { } g)
                from = g["inputs"]?[side] ?? (negative ? null : g["inputs"]?["conditioning"]);
            if (Trace(workflow, library, from, negative, [], 0) is { } found)
                return found;
        }
        return null;
    }

    /// <summary>The nodes that sample: KSampler and its kin, SamplerCustomAdvanced (through its guider); the first first.</summary>
    private static IEnumerable<(string Id, JsonObject Node)> Samplers(ComfyWorkflow workflow) =>
        workflow.All().Where(n => n.Node["inputs"] is JsonObject i &&
                                  (i.ContainsKey("positive") && ComfyWorkflow.ClassOf(n.Node).Contains("Sampler", StringComparison.OrdinalIgnoreCase) ||
                                   i.ContainsKey("guider")));

    private static readonly string[] TextNames = ["text", "prompt", "value", "string", "text_g", "positive_prompt"];

    /// <summary>The input of a node that holds text: its text, prompt or value.</summary>
    private static string? TextInput(JsonObject node, ComfyNodeLibrary library)
    {
        var def = library.Find(ComfyWorkflow.ClassOf(node));
        var inputs = node["inputs"] as JsonObject ?? [];
        foreach (var name in TextNames)
        {
            if (!inputs.ContainsKey(name))
                continue;
            // A node type the server doesn't describe: text is what holds text (a switch's "value" is true or false).
            if (def?.Input(name) is { } input ? input.Type == "STRING" : inputs[name] is JsonValue v && v.GetValueKind() == System.Text.Json.JsonValueKind.String || ComfyWorkflow.IsLink(inputs[name]))
                return name;
        }
        return null;
    }

    /// <summary>Follows conditioning back from a sampler to the text it was made from.</summary>
    private static (string Id, string Input)? Trace(ComfyWorkflow workflow, ComfyNodeLibrary library, JsonNode? link, bool negative, HashSet<string> seen, int depth)
    {
        if (depth > 40 || link is not JsonArray { Count: 2 } pair || !ComfyWorkflow.IsLink(pair))
            return null;
        var id = pair[0]!.ToString();
        var output = pair[1] is JsonValue o && o.TryGetValue<int>(out var index) ? index : 0;
        if (workflow.Nodes[id] is not JsonObject node || !seen.Add(id))
            return null;
        var inputs = node["inputs"] as JsonObject ?? [];
        if (TextInput(node, library) is { } text)
        {
            // Text that comes from elsewhere (a primitive, a switch): the prompt is where it starts.
            return ComfyWorkflow.IsLink(inputs[text]) ? Trace(workflow, library, inputs[text], negative, seen, depth + 1) ?? (id, text) : (id, text);
        }
        if (inputs.ContainsKey("on_true") && inputs.ContainsKey("on_false"))
            return Trace(workflow, library, inputs[SwitchOn(workflow, inputs["switch"]) ? "on_true" : "on_false"], negative, seen, depth + 1);
        if (inputs.ContainsKey("positive") && inputs.ContainsKey("negative"))
        {
            // A node that passes both on (LTXVConditioning, LTXVAddGuide) gives positive first,
            // negative second; a guider takes both and gives one.
            var outputs = library.Find(ComfyWorkflow.ClassOf(node))?.Outputs;
            var passesBoth = outputs is { Count: >= 2 } && outputs[0].Type == "CONDITIONING" && outputs[1].Type == "CONDITIONING";
            var side = passesBoth ? (output == 1 ? "negative" : "positive") : negative ? "negative" : "positive";
            return Trace(workflow, library, inputs[side], negative, seen, depth + 1);
        }
        foreach (var name in new[] { "conditioning", "conditioning_to", "conditioning_1", "cond" })
            if (inputs.ContainsKey(name))
                return Trace(workflow, library, inputs[name], negative, seen, depth + 1);
        return null;
    }

    /// <summary>Which way a switch goes: its own value, or a boolean primitive's it links to.</summary>
    private static bool SwitchOn(ComfyWorkflow workflow, JsonNode? value)
    {
        if (workflow.SourceOf(value) is { } source)
            value = source["inputs"]?["value"];
        return value is JsonValue v && v.TryGetValue<bool>(out var b) && b;
    }

    // ---- seed, size, length, frame rate ----------------------------------------------------------

    /// <summary>Every seed the workflow sets itself: sampler and noise seeds, and primitives titled as one.</summary>
    internal static List<(string Id, string Input)> SeedInputs(ComfyWorkflow workflow)
    {
        var found = new List<(string, string)>();
        foreach (var (id, node) in workflow.All())
        {
            if (node["inputs"] is not JsonObject inputs)
                continue;
            foreach (var name in new[] { "seed", "noise_seed" })
            {
                if (inputs[name] is JsonValue)
                    found.Add((id, name));
                // A seed taken from a primitive, whatever it is titled: ACE-Step's SFT template calls it just "Int",
                // and its seed stayed 0 whatever the storyboard said.
                else if (inputs[name] is JsonArray link && ComfyWorkflow.IsLink(link) && link[0]?.ToString() is { } from &&
                         workflow.Nodes[from] is JsonObject source && ComfyWorkflow.ClassOf(source).StartsWith("Primitive", StringComparison.Ordinal) &&
                         source["inputs"] is JsonObject own && Single(own) is { } value)
                    found.Add((from, value));
            }
            if (Single(inputs) is { } only && SeedTitle().IsMatch(ComfyWorkflow.TitleOf(node) ?? ""))
                found.Add((id, only));
        }
        return found.Distinct().ToList();
    }

    /// <summary>The width and height to set: primitives titled so, else the empty latent's own.</summary>
    internal static List<(string Id, string Input, string Which)> SizeInputs(ComfyWorkflow workflow)
    {
        var titled = new List<(string, string, string)>();
        foreach (var (id, node) in workflow.All())
            if (node["inputs"] is JsonObject inputs && Single(inputs, linksToo: true) is { } only &&
                ComfyWorkflow.ClassOf(node).StartsWith("Primitive", StringComparison.Ordinal) && SizeTitle().Match(ComfyWorkflow.TitleOf(node) ?? "") is { Success: true } m)
                titled.Add((id, only, m.Groups[1].Value.ToLowerInvariant()));
        if (titled.Count > 0)
            return titled;
        var latent = new List<(string, string, string)>();
        foreach (var (id, node) in workflow.All())
        {
            var cls = ComfyWorkflow.ClassOf(node);
            if (!cls.Contains("Latent", StringComparison.Ordinal) || !cls.StartsWith("Empty", StringComparison.Ordinal) || node["inputs"] is not JsonObject inputs)
                continue;
            foreach (var which in new[] { "width", "height" })
                if (inputs[which] is JsonValue)
                    latent.Add((id, which, which));
        }
        return latent;
    }

    /// <summary>
    /// The size of a picture made from others, in a template that takes it from the first of them:
    /// Qwen-Image-Edit's samples the latent its first picture is encoded to, so a frame made from a
    /// square or upright reference came out that shape, and was then cut to the video's frame (a
    /// character's head and feet cut off). The sampler draws the picture anew (denoise 1), so the
    /// latent is encoded from a blank picture of the size asked for instead (to whole 16 pixels, for
    /// the model; the frame is cut to the video's after), and the pictures go to the model as they
    /// are. False when no sampler starts from an encoded picture that way.
    /// </summary>
    internal static bool SizeFromBlank(ComfyWorkflow workflow, ComfyNodeLibrary library, int width, int height, Action<string, string, JsonNode?> put)
    {
        if (library.Find("EmptyImage") is null)
            return false;
        var done = false;
        foreach (var (_, sampler) in workflow.All().ToList())
        {
            if (sampler["inputs"] is not JsonObject inputs || !ComfyWorkflow.IsLink(inputs["latent_image"]))
                continue;
            if (inputs["denoise"] is JsonValue d && d.TryGetValue<double>(out var denoise) && denoise < 0.999)
                continue;
            var source = ((JsonArray)inputs["latent_image"]!)[0]!.ToString();
            if (workflow.Nodes[source] is not JsonObject encode || ComfyWorkflow.ClassOf(encode) is not ("VAEEncode" or "VAEEncodeTiled") || encode["inputs"] is not JsonObject)
                continue;
            var blank = "video_size_" + source.Replace(':', '_');
            workflow.Nodes[blank] = new JsonObject
            {
                ["class_type"] = "EmptyImage",
                ["inputs"] = new JsonObject { ["width"] = Up16(width), ["height"] = Up16(height), ["batch_size"] = 1, ["color"] = 0 },
                ["_meta"] = new JsonObject { ["title"] = "The picture's size" },
            };
            put(source, "pixels", new JsonArray(blank, 0));
            done = true;
        }
        return done;

        static int Up16(int n) => (n + 15) / 16 * 16;
    }

    internal static List<(string Id, string Input)> FpsInputs(ComfyWorkflow workflow)
    {
        var found = new List<(string, string)>();
        foreach (var (id, node) in workflow.All())
        {
            if (node["inputs"] is not JsonObject inputs)
                continue;
            if (Single(inputs) is { } only && ComfyWorkflow.ClassOf(node).StartsWith("Primitive", StringComparison.Ordinal) && FpsTitle().IsMatch(ComfyWorkflow.TitleOf(node) ?? ""))
                found.Add((id, only));
            else
                foreach (var name in new[] { "fps", "frame_rate" })
                    if (inputs[name] is JsonValue v && v.GetValueKind() == System.Text.Json.JsonValueKind.Number)
                        found.Add((id, name));
        }
        return found;
    }

    internal enum LengthKind { Seconds, WholeSeconds, LtxFrames, Frames }

    /// <summary>
    /// Where the length goes: a primitive titled Duration (whole seconds when it is an INT), inputs
    /// in seconds (an audio latent's seconds, a song's duration), else frame counts.
    /// </summary>
    internal static List<(string Id, string Input, LengthKind Kind)> LengthInputs(ComfyWorkflow workflow, ComfyNodeLibrary library)
    {
        var found = new List<(string, string, LengthKind)>();
        foreach (var (id, node) in workflow.All())
        {
            if (node["inputs"] is not JsonObject inputs)
                continue;
            var cls = ComfyWorkflow.ClassOf(node);
            if (cls.StartsWith("Primitive", StringComparison.Ordinal) && Single(inputs) is { } only && DurationTitle().IsMatch(ComfyWorkflow.TitleOf(node) ?? ""))
            {
                found.Add((id, only, cls.Contains("Int", StringComparison.Ordinal) ? LengthKind.WholeSeconds : LengthKind.Seconds));
                continue;
            }
            foreach (var name in new[] { "seconds", "duration" })
                if (inputs[name] is JsonValue v && v.GetValueKind() == System.Text.Json.JsonValueKind.Number)
                    found.Add((id, name, library.Find(cls)?.Input(name)?.Type == "INT" ? LengthKind.WholeSeconds : LengthKind.Seconds));
        }
        if (found.Count > 0)
            return found;
        foreach (var (id, node) in workflow.All())
        {
            if (node["inputs"] is not JsonObject inputs)
                continue;
            var cls = ComfyWorkflow.ClassOf(node);
            foreach (var name in new[] { "length", "frames_number", "num_frames" })
                if (inputs[name] is JsonValue v && v.GetValueKind() == System.Text.Json.JsonValueKind.Number &&
                    (cls.Contains("Latent", StringComparison.Ordinal) || cls.Contains("ToVideo", StringComparison.Ordinal)))
                    found.Add((id, name, cls.StartsWith("LTXV", StringComparison.Ordinal) || cls.Contains("LTXV", StringComparison.Ordinal) ? LengthKind.LtxFrames : LengthKind.Frames));
        }
        return found;
    }

    /// <summary>The one input a primitive-like node holds: a value (or, with <paramref name="linksToo"/>, a link a value can replace).</summary>
    private static string? Single(JsonObject inputs, bool linksToo = false)
    {
        var values = inputs.Where(kv => linksToo || !ComfyWorkflow.IsLink(kv.Value)).Select(kv => kv.Key).ToList();
        return values.Count == 1 ? values[0] : inputs.ContainsKey("value") && (linksToo || !ComfyWorkflow.IsLink(inputs["value"])) ? "value" : null;
    }

    // ---- pictures ----------------------------------------------------------------------------------

    private static void SetPictures(ComfyWorkflow workflow, ComfyNodeLibrary library, VideoInputs inputs, Action<string, string, JsonNode?> put, List<string> missed)
    {
        var loaders = workflow.All().Where(n => ComfyWorkflow.ClassOf(n.Node) == "LoadImage" ||
                                                library.Find(ComfyWorkflow.ClassOf(n.Node))?.Input("image") is { TakesInputFile: true })
            .Select(n => (n.Id, Title: ComfyWorkflow.TitleOf(n.Node) ?? "")).ToList();
        var free = loaders.ToList();
        (string Id, string Title)? Take(Regex? named)
        {
            var pick = named is null ? free.FirstOrDefault(l => !FirstTitle().IsMatch(l.Title) && !LastTitle().IsMatch(l.Title)) : free.FirstOrDefault(l => named.IsMatch(l.Title));
            if (pick.Id is null && named is null && free.Count > 0)
                pick = free[0];
            if (pick.Id is null)
                return null;
            free.Remove(pick);
            return pick;
        }

        if (inputs.FirstImage is { } first)
        {
            if ((Take(FirstTitle()) ?? Take(null)) is { } at)
                put(at.Id, "image", first);
            else
                missed.Add("the first frame (no picture loader)");
        }
        if (inputs.LastImage is { } last)
        {
            if ((Take(LastTitle()) ?? Take(null)) is { } at)
                put(at.Id, "image", last);
            else
                missed.Add("the last frame (the template takes no second picture)");
        }
        if (inputs.Pictures.Count == 0)
            return;
        var extra = new List<string>();
        foreach (var picture in inputs.Pictures)
        {
            if (Take(null) is { } at)
                put(at.Id, "image", picture);
            else
                extra.Add(picture);
        }
        if (extra.Count == 0)
            return;
        // Qwen-Image-Edit's encoders take up to three pictures; templates wire only the first.
        var encoders = workflow.All().Where(n => ComfyWorkflow.ClassOf(n.Node) == "TextEncodeQwenImageEditPlus").ToList();
        var slots = new[] { "image2", "image3" };
        var used = 0;
        foreach (var picture in extra)
        {
            var slot = encoders.Count == 0 ? null : slots.FirstOrDefault(s => encoders.All(e => e.Node["inputs"]?[s] is null));
            if (slot is null)
            {
                missed.Add($"{extra.Count - used} of the pictures to make it from (the template takes {inputs.Pictures.Count - extra.Count + used})");
                return;
            }
            var id = "video_picture_" + (++used + 1);
            workflow.Nodes[id] = new JsonObject
            {
                ["class_type"] = "LoadImage",
                ["inputs"] = new JsonObject { ["image"] = picture },
                ["_meta"] = new JsonObject { ["title"] = $"Picture {used + 1}" },
            };
            foreach (var (eid, encoder) in encoders)
                ((JsonObject)encoder["inputs"]!)[slot] = new JsonArray(id, 0);
            put(id, "image", picture);
        }
    }

    [GeneratedRegex(@"\bprompt\b", RegexOptions.IgnoreCase)]
    private static partial Regex PromptTitle();

    [GeneratedRegex(@"negative|\bneg\b", RegexOptions.IgnoreCase)]
    private static partial Regex NegativeWord();

    [GeneratedRegex(@"\bseed\b", RegexOptions.IgnoreCase)]
    private static partial Regex SeedTitle();

    [GeneratedRegex(@"^\s*(?:int\s*\(\s*)?(width|height)\b", RegexOptions.IgnoreCase)]
    private static partial Regex SizeTitle();

    [GeneratedRegex(@"frame\s*rate|\bfps\b", RegexOptions.IgnoreCase)]
    private static partial Regex FpsTitle();

    [GeneratedRegex(@"duration|\bseconds\b|\blength\s*\(s", RegexOptions.IgnoreCase)]
    private static partial Regex DurationTitle();

    [GeneratedRegex(@"\b(first|start|begin)", RegexOptions.IgnoreCase)]
    private static partial Regex FirstTitle();

    [GeneratedRegex(@"\b(last|end|final)", RegexOptions.IgnoreCase)]
    private static partial Regex LastTitle();

    [GeneratedRegex(@"prompt\s*enhance|enhance\s*prompt", RegexOptions.IgnoreCase)]
    private static partial Regex EnhanceTitle();

    [GeneratedRegex(@"text\s*to\s*video|\bt2v\b", RegexOptions.IgnoreCase)]
    private static partial Regex TextToVideoTitle();
}
