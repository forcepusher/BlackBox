using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Sloperator.Core.Media;

/// <summary>A prompt a file was made with: "Prompt", "Negative prompt", "Lyrics".</summary>
public sealed record RecipeText(string Label, string Text);

/// <summary>A setting a file was made with: ("Sampler", "euler"), ("Steps", "20").</summary>
public sealed record RecipeSetting(string Name, string Value);

/// <summary>
/// How a picture, video or sound was made, read back from the file itself: its prompts and the
/// settings that matter for making it again (model, LoRAs, sampler, steps, CFG, seed). ComfyUI keeps
/// the workflow it ran in every file it saves (a PNG's text chunks, an MP4's or MP3's tags, a FLAC's
/// comments), and AUTOMATIC1111 and Forge a "parameters" text in a PNG. Read whatever made the file,
/// a run of Sloperator's or the user's own.
/// </summary>
public sealed partial record MediaRecipe(IReadOnlyList<RecipeText> Texts, IReadOnlyList<RecipeSetting> Settings, string MadeWith)
{
    /// <summary>The prompt, if the file names one.</summary>
    public string? Prompt => Texts.FirstOrDefault(t => t.Label == "Prompt")?.Text;

    /// <summary>The negative prompt, if the file names one.</summary>
    public string? NegativePrompt => Texts.FirstOrDefault(t => t.Label == "Negative prompt")?.Text;

    /// <summary>The whole of it as text, to copy: each prompt under its label, then the settings one a line.</summary>
    public string Describe()
    {
        var sb = new StringBuilder();
        foreach (var text in Texts)
            sb.Append(text.Label).Append(":\n").Append(text.Text).Append("\n\n");
        foreach (var setting in Settings)
            sb.Append(setting.Name).Append(": ").Append(setting.Value).Append('\n');
        return sb.ToString().TrimEnd();
    }

    /// <summary>How much of a file's start and of its end is searched for a workflow: ComfyUI's videos keep it in a header at one end.</summary>
    private const int Searched = 8 << 20;

    /// <summary>What the file says it was made with; null when it says nothing (or isn't there).</summary>
    public static MediaRecipe? Read(string path)
    {
        try
        {
            using var file = File.OpenRead(path);
            var bytes = Head(file);
            if (IsPng(bytes))
            {
                var texts = PngTexts(bytes);
                if (texts.TryGetValue("prompt", out var prompt) && ParseObject(prompt) is { } graph && IsApiPrompt(graph))
                    return FromComfy(graph);
                if (texts.TryGetValue("parameters", out var parameters))
                    return FromParameters(parameters);
            }
            if (FindApiPrompt(bytes) is { } found)
                return FromComfy(found);
            if (file.Length > bytes.Length)
            {
                file.Seek(Math.Max(bytes.Length, file.Length - Searched), SeekOrigin.Begin);
                var tail = new byte[file.Length - file.Position];
                file.ReadExactly(tail);
                if (FindApiPrompt(tail) is { } atEnd)
                    return FromComfy(atEnd);
            }
            return null;
        }
        // A file that can't be read, or a workflow shaped as no reader here expects: it says nothing.
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return null;
        }
    }

    /// <summary>The start of the file, the whole of it when it is small.</summary>
    private static byte[] Head(FileStream file)
    {
        var head = new byte[(int)Math.Min(file.Length, file.Length <= 2 * Searched ? 2 * Searched : Searched)];
        file.ReadExactly(head);
        return head;
    }

    // ---- where the text is kept --------------------------------------------------------------

    private static bool IsPng(byte[] b) => b.Length > 8 && b[0] == 0x89 && b[1] == (byte)'P' && b[2] == (byte)'N' && b[3] == (byte)'G';

    /// <summary>A PNG's text chunks (tEXt, zTXt, iTXt) by keyword: ComfyUI's "prompt" and "workflow", AUTOMATIC1111's "parameters".</summary>
    internal static Dictionary<string, string> PngTexts(byte[] png)
    {
        var texts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var at = 8; at + 12 <= png.Length;)
        {
            var length = (png[at] << 24) | (png[at + 1] << 16) | (png[at + 2] << 8) | png[at + 3];
            var type = Encoding.ASCII.GetString(png, at + 4, 4);
            var data = at + 8;
            if (length < 0 || data + length > png.Length || type == "IEND")
                break;
            if (type is "tEXt" or "zTXt" or "iTXt" && Array.IndexOf(png, (byte)0, data, length) is var zero and >= 0)
            {
                var key = Encoding.Latin1.GetString(png, data, zero - data);
                var rest = png.AsSpan(zero + 1, data + length - zero - 1);
                try
                {
                    texts.TryAdd(key, type switch
                    {
                        "tEXt" => Encoding.Latin1.GetString(rest),
                        "zTXt" => Encoding.Latin1.GetString(Inflate(rest[1..])),
                        _ => ITxt(rest),
                    });
                }
                catch (InvalidDataException) { /* a broken chunk: the others may do */ }
            }
            at = data + length + 4; // past the CRC
        }
        return texts;
    }

    /// <summary>An iTXt chunk's text, after its keyword: compressed or not, then language and translated keyword, then UTF-8.</summary>
    private static string ITxt(ReadOnlySpan<byte> rest)
    {
        if (rest.Length < 2)
            return "";
        var compressed = rest[0] == 1;
        rest = rest[2..];
        for (var skip = 0; skip < 2; skip++)
        {
            var zero = rest.IndexOf((byte)0);
            if (zero < 0)
                return "";
            rest = rest[(zero + 1)..];
        }
        return Encoding.UTF8.GetString(compressed ? Inflate(rest) : rest);
    }

    /// <summary>The most a compressed text chunk may grow to: a workflow is kilobytes; a few MB of zeros grew to 2 GB.</summary>
    private const int MaxInflated = 16 << 20;

    private static byte[] Inflate(ReadOnlySpan<byte> data)
    {
        using var input = new MemoryStream(data.ToArray());
        using var z = new ZLibStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        var buffer = new byte[81920];
        for (int read; (read = z.Read(buffer)) > 0;)
        {
            if (output.Length + read > MaxInflated)
                throw new InvalidDataException("A text chunk larger than any workflow.");
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }

    /// <summary>
    /// Where an API-format workflow may start: a node's id, then its inputs or its class. Or the
    /// same written as a JSON string (Video Combine's MP4s keep it so: "{\"7\": {\"inputs\"…").
    /// </summary>
    [GeneratedRegex("""\{\s*"[^"\\]{1,64}"\s*:\s*\{\s*"(inputs|class_type)"\s*:|"\{\\"[^"\\]{1,64}\\"\s*:\s*\{\\"(inputs|class_type)\\"\s*:""")]
    private static partial Regex PromptStart();

    /// <summary>
    /// The API-format workflow ComfyUI ran, in the file however it is kept there (an MP4's metadata,
    /// an MP3's TXXX frame, a FLAC comment, a WebP's EXIF): it is JSON as written, so it is found
    /// by how it begins. The editor's workflow beside it has the same values in a form only the
    /// editor reads, so it isn't used.
    /// </summary>
    internal static JsonObject? FindApiPrompt(byte[] bytes)
    {
        var text = Encoding.Latin1.GetString(bytes);
        foreach (Match m in PromptStart().Matches(text))
        {
            try
            {
                var reader = new Utf8JsonReader(bytes.AsSpan(m.Index));
                var found = JsonNode.Parse(ref reader);
                if (found is JsonValue quoted && quoted.TryGetValue<string>(out var json))
                    found = ParseObject(json);
                if (found is JsonObject graph && IsApiPrompt(graph))
                    return graph;
            }
            catch (JsonException) { }
        }
        return null;
    }

    private static JsonObject? ParseObject(string json)
    {
        try { return JsonNode.Parse(json) as JsonObject; }
        catch (JsonException) { return null; }
    }

    private static bool IsApiPrompt(JsonObject graph) =>
        graph.Count > 0 && graph.All(n => n.Value is JsonObject node && node["class_type"] is JsonValue && node["inputs"] is null or JsonObject);

    // ---- AUTOMATIC1111 and Forge ---------------------------------------------------------------

    /// <summary>
    /// The "parameters" text: the prompt, "Negative prompt: …", then a line of settings
    /// ("Steps: 20, Sampler: Euler a, CFG scale: 7, Seed: 1, Size: 512x768, Model: …").
    /// </summary>
    internal static MediaRecipe FromParameters(string parameters)
    {
        var lines = parameters.ReplaceLineEndings("\n").Split('\n').ToList();
        var settingsAt = lines.FindLastIndex(l => l.StartsWith("Steps: ", StringComparison.Ordinal));
        var settingsLine = settingsAt >= 0 ? string.Join(" ", lines.Skip(settingsAt)) : "";
        // A real one is a line or two; the pairs' pattern takes time that grows with the square of a long run without a colon.
        if (settingsLine.Length > 8000)
            settingsLine = settingsLine[..8000];
        if (settingsAt >= 0)
            lines.RemoveRange(settingsAt, lines.Count - settingsAt);
        var negativeAt = lines.FindIndex(l => l.StartsWith("Negative prompt:", StringComparison.Ordinal));
        var positive = string.Join("\n", negativeAt >= 0 ? lines.Take(negativeAt) : lines).Trim();
        var negative = negativeAt >= 0 ? string.Join("\n", lines.Skip(negativeAt)).Substring("Negative prompt:".Length).Trim() : "";
        var texts = new List<RecipeText>();
        if (positive.Length > 0) texts.Add(new("Prompt", positive));
        if (negative.Length > 0) texts.Add(new("Negative prompt", negative));
        var settings = new List<RecipeSetting>();
        foreach (Match m in ParameterPair().Matches(settingsLine))
        {
            var name = m.Groups[1].Value.Trim();
            var value = m.Groups[2].Value.Trim().Trim('"');
            // Hashes and versions say nothing about how to make it again.
            if (!name.EndsWith("hash", StringComparison.OrdinalIgnoreCase) && !name.EndsWith("hashes", StringComparison.OrdinalIgnoreCase) && name != "Version")
                settings.Add(new(name, value));
        }
        return new MediaRecipe(texts, settings, "AUTOMATIC1111");
    }

    /// <summary>"Name: value" in the settings line; a value in quotes may hold commas.</summary>
    [GeneratedRegex("""\s*([^:,]+):\s*("(?:[^"\\]|\\.)*"|[^,]*)(,|$)""")]
    private static partial Regex ParameterPair();

    // ---- ComfyUI ---------------------------------------------------------------------------------

    /// <summary>How deep a chain of nodes is followed, from a sampler back to a prompt or a model.</summary>
    private const int Deepest = 40;

    private static readonly string[] TextInputs = ["text", "prompt", "tags", "text_g", "t5xxl", "clip_l", "text_l", "value", "string", "positive_prompt"];

    /// <summary>
    /// The prompts and settings of the samplers in a ComfyUI workflow (KSampler, SamplerCustom,
    /// SamplerCustomAdvanced), followed back through the nodes between: guiders, conditioning nodes
    /// (LTX's guides take positive and negative and give both back), switches, primitives. A
    /// workflow without one (a voice cloned, a model run on its own) gives the texts its nodes were given.
    /// </summary>
    internal static MediaRecipe FromComfy(JsonObject graph)
    {
        var g = new Graph(graph);
        var samplers = graph.Where(n => Inputs(n.Value)?["latent_image"] is not null && (Inputs(n.Value)?["positive"] is not null || Inputs(n.Value)?["guider"] is not null))
            .Select(n => n.Key).ToList();
        var texts = new List<RecipeText>();
        var settings = new SettingList();
        foreach (var id in samplers)
        {
            var inputs = Inputs(graph[id])!;
            JsonNode? positive = inputs["positive"], negative = inputs["negative"], model = inputs["model"];
            if (inputs["guider"] is { } guiderLink && g.Node(guiderLink) is { } guider)
            {
                var gi = Inputs(guider)!;
                positive = gi["positive"] ?? gi["conditioning"] ?? gi["cond1"];
                negative = gi["negative"];
                model = gi["model"];
                settings.Add("CFG", g.Number(gi["cfg"] ?? gi["video_cfg"] ?? gi["cfg_conds"]));
            }
            if (g.TextOf(positive) is { Length: > 0 } p)
                AddText(texts, "Prompt", p);
            if (g.TextOf(negative) is { Length: > 0 } n)
                AddText(texts, "Negative prompt", n);
            if (g.LyricsOf(positive) is { Length: > 0 } lyrics)
                AddText(texts, "Lyrics", lyrics);
            foreach (var (name, value) in g.ModelChain(model))
                settings.Add(name, value);
            g.SamplerSettings(inputs, settings);
            foreach (var (name, value) in g.EncoderSettings(positive))
                settings.Add(name, value);
        }
        if (samplers.Count == 0)
        {
            // No sampler: what each node was given to say or make, under its title.
            foreach (var (id, node) in graph)
                foreach (var key in TextInputs.Append("lyrics"))
                    if (Inputs(node)?[key] is JsonValue v && v.TryGetValue<string>(out var s) && s.Trim().Length > 0 && LooksLikeProse(s))
                        AddText(texts, key == "lyrics" ? "Lyrics" : "Prompt", s.Trim());
            foreach (var (id, node) in graph)
            {
                foreach (var (name, value) in Loader(node))
                    settings.Add(name, value);
                // A model node of its own (VoxCPM2, Woosh), not an upscaler on the way out.
                if (Text(Inputs(node)?["model_name"]) is { } model && node?["class_type"]?.GetValue<string>()?.Contains("Upscale", StringComparison.OrdinalIgnoreCase) == false
                    && !model.EndsWith(".pth", StringComparison.OrdinalIgnoreCase))
                    settings.Add("Model", model);
            }
        }
        foreach (var (id, node) in graph)
        {
            var cls = node?["class_type"]?.GetValue<string>() ?? "";
            // What it was made from: the pictures it started with, an upscaler it went through.
            if (cls == "LoadImage" && Text(Inputs(node)?["image"]) is { } picture)
                settings.Add("From", picture);
            if (cls == "UpscaleModelLoader" && Text(Inputs(node)?["model_name"]) is { } upscaler)
                settings.Add("Upscaler", upscaler);
        }
        foreach (var (id, node) in graph)
            if (node?["class_type"]?.GetValue<string>() is { } cls && cls.StartsWith("Empty", StringComparison.Ordinal) && Inputs(node) is { } latent)
            {
                if (g.Number(latent["width"]) is { } w && g.Number(latent["height"]) is { } h)
                    settings.Add("Size", $"{w}x{h}");
                if (g.Number(latent["seconds"]) is { } seconds)
                    settings.Add("Length", seconds + " s");
            }
        return new MediaRecipe(texts, settings.ToList(), "ComfyUI");
    }

    private static void AddText(List<RecipeText> texts, string label, string text)
    {
        var same = texts.FindIndex(t => t.Label == label);
        if (same < 0)
            texts.Add(new(label, text));
        else if (!texts[same].Text.Split("\n\n").Contains(text))
            texts[same] = texts[same] with { Text = texts[same].Text + "\n\n" + text }; // a second pass with a prompt of its own
    }

    /// <summary>A prompt rather than a file name or a choice: words with spaces or commas between them.</summary>
    private static bool LooksLikeProse(string s) => s.Length >= 12 && (s.Contains(' ') || s.Contains(',')) && !s.Contains(".safetensors", StringComparison.OrdinalIgnoreCase);

    private static JsonObject? Inputs(JsonNode? node) => node?["inputs"] as JsonObject;

    /// <summary>What a loader loads: its model or LoRA by name.</summary>
    private static IEnumerable<(string Name, string Value)> Loader(JsonNode? node)
    {
        if (Inputs(node) is not { } inputs)
            yield break;
        if (Text(inputs["ckpt_name"]) is { } ckpt)
            yield return ("Model", ckpt);
        if (Text(inputs["unet_name"]) is { } unet)
            yield return ("Model", unet);
        // A LoRA at strength 0 changes nothing.
        if (Text(inputs["lora_name"]) is { } lora && lora != "None" && Number(inputs["strength_model"]) is var strength && strength != "0")
            yield return ("LoRA", strength is null ? lora : $"{lora} ({strength})");
        // Stacks of LoRAs (rgthree's Power Lora Loader): one object each, with whether it is on.
        foreach (var (key, value) in inputs)
            if (key.StartsWith("lora_", StringComparison.Ordinal) && value is JsonObject entry && Text(entry["lora"]) is { } name && name != "None"
                && !(entry["on"] is JsonValue on && on.GetValueKind() == JsonValueKind.False))
                yield return ("LoRA", Number(entry["strength"]) is { } weight ? $"{name} ({weight})" : name);
    }

    private static string? Text(JsonNode? value) => value is JsonValue v && v.TryGetValue<string>(out var s) && s.Length > 0 ? s : null;

    private static string? Number(JsonNode? value) => value is JsonValue v && v.GetValueKind() == JsonValueKind.Number
        ? double.Parse(v.ToJsonString(), CultureInfo.InvariantCulture).ToString("0.####", CultureInfo.InvariantCulture)
        : null;

    /// <summary>Settings in the order they are shown, each with every value the samplers used ("20 / 8").</summary>
    private sealed class SettingList
    {
        private static readonly string[] Order = ["Model", "LoRA", "Shift", "Sampler", "Scheduler", "Steps", "CFG", "Seed", "Denoise", "Size", "Length", "BPM", "Key", "Time signature", "From", "Upscaler"];
        private readonly List<(string Name, List<string> Values)> _all = [];

        public void Add(string name, string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return;
            var entry = _all.FirstOrDefault(e => e.Name == name);
            if (entry.Values is null)
                _all.Add((name, [value]));
            else if (!entry.Values.Contains(value))
                entry.Values.Add(value);
        }

        public List<RecipeSetting> ToList() => _all
            .OrderBy(e => Array.IndexOf(Order, e.Name) is var i and >= 0 ? i : Order.Length)
            .SelectMany(e => e.Name == "LoRA" ? e.Values.Select(v => new RecipeSetting(e.Name, v))
                : [new RecipeSetting(e.Name, string.Join(e.Name == "From" ? ", " : " / ", e.Values))])
            .ToList();
    }

    /// <summary>The workflow's nodes, and how a value is found by following the links between them.</summary>
    private sealed class Graph(JsonObject nodes)
    {
        /// <summary>
        /// Values followed so far: a chain of switches each linked to the next, in a file made to,
        /// went two ways at each of its 40 steps (2^40 calls), and the preview's read never ended.
        /// </summary>
        private int _followed;

        private const int MostFollowed = 100_000;

        /// <summary>The node a link (["id", output]) comes from.</summary>
        public JsonObject? Node(JsonNode? link) => link is JsonArray { Count: 2 } a && Text(a[0]) is { } id ? nodes[id] as JsonObject : null;

        private static int Slot(JsonNode? link) => link is JsonArray { Count: 2 } a && a[1] is JsonValue v && v.TryGetValue<int>(out var slot) ? slot : 0;

        private static string Class(JsonObject node) => node["class_type"]?.GetValue<string>() ?? "";

        /// <summary>A switch's branch that ran (ComfyUI's If/Else Switch), or null when the node is none or its switch can't be told.</summary>
        private JsonNode? Branch(JsonObject node, int depth)
        {
            if (Inputs(node) is not { } i || i["on_true"] is null && i["on_false"] is null)
                return null;
            return Value(i["switch"] ?? i["boolean"], depth + 1) is JsonValue v && v.GetValueKind() is JsonValueKind.True or JsonValueKind.False
                ? (v.GetValueKind() == JsonValueKind.True ? i["on_true"] : i["on_false"])
                : null;
        }

        /// <summary>A plain value: as given, or what a primitive or a switch it is linked to gives.</summary>
        public JsonNode? Value(JsonNode? value, int depth = 0)
        {
            if (value is not JsonArray || depth > Deepest || ++_followed > MostFollowed)
                return value is JsonArray ? null : value;
            if (Node(value) is not { } node)
                return null;
            if (Branch(node, depth) is { } branch)
                return Value(branch, depth + 1);
            var inputs = Inputs(node);
            // Primitives and seed nodes: one value, under "value", "seed", "int", "float" or the like.
            foreach (var key in new[] { "value", "seed", "noise_seed", "int", "float", "number", "string", "text", "boolean" })
                if (inputs?[key] is { } v)
                    return Value(v, depth + 1);
            return null;
        }

        public string? Number(JsonNode? value) => MediaRecipe.Number(Value(value));

        /// <summary>The words a conditioning link carries, followed back to the node that encoded them.</summary>
        public string? TextOf(JsonNode? link, int depth = 0)
        {
            if (depth > Deepest || Node(link) is not { } node)
                return null;
            var cls = Class(node);
            // ConditioningZeroOut: nothing, whatever it was given.
            if (cls.Contains("ZeroOut", StringComparison.Ordinal))
                return null;
            if (Branch(node, depth) is { } branch)
                return TextOf(branch, depth + 1);
            var inputs = Inputs(node) ?? [];
            foreach (var key in TextInputs)
                switch (inputs[key])
                {
                    case JsonValue v when v.TryGetValue<string>(out var s):
                        return s.Trim();
                    case JsonArray:
                        return TextOf(inputs[key], depth + 1);
                }
            // A node that takes both and gives both back, in that order (LTX's conditioning and guides, ControlNet).
            if (inputs["positive"] is not null && inputs["negative"] is not null)
                return TextOf(Slot(link) == 1 ? inputs["negative"] : inputs["positive"], depth + 1);
            foreach (var (key, value) in inputs)
                if (value is JsonArray && (key.Contains("cond", StringComparison.OrdinalIgnoreCase) || key is "positive" or "negative" or "string_a"))
                    return TextOf(value, depth + 1);
            return null;
        }

        /// <summary>The lyrics a song's text encoder was given (ACE-Step), unless it is an instrumental.</summary>
        public string? LyricsOf(JsonNode? link) => Encoder(link) is { } node && Inputs(node)?["lyrics"] is JsonValue v && v.TryGetValue<string>(out var s)
            && s.Trim() is { Length: > 0 } lyrics && !lyrics.Equals("[Instrumental]", StringComparison.OrdinalIgnoreCase) ? lyrics : null;

        /// <summary>What the song's text encoder was told besides its words: its tempo and key.</summary>
        public IEnumerable<(string, string?)> EncoderSettings(JsonNode? link)
        {
            if (Encoder(link) is not { } node || Inputs(node) is not { } i || i["lyrics"] is null)
                yield break;
            yield return ("BPM", Number(i["bpm"]));
            yield return ("Key", MediaRecipe.Text(Value(i["keyscale"])));
            yield return ("Time signature", MediaRecipe.Text(Value(i["timesignature"])));
        }

        /// <summary>The text encoder a conditioning link comes from, through the nodes between.</summary>
        private JsonObject? Encoder(JsonNode? link, int depth = 0)
        {
            if (depth > Deepest || Node(link) is not { } node)
                return null;
            var inputs = Inputs(node) ?? [];
            if (TextInputs.Any(k => inputs[k] is not null))
                return node;
            if (inputs["positive"] is not null && inputs["negative"] is not null)
                return Encoder(Slot(link) == 1 ? inputs["negative"] : inputs["positive"], depth + 1);
            return inputs.FirstOrDefault(i => i.Value is JsonArray && i.Key.Contains("cond", StringComparison.OrdinalIgnoreCase)).Value is { } next ? Encoder(next, depth + 1) : null;
        }

        /// <summary>The models and LoRAs the sampler's model came through, and the shift a ModelSampling node set.</summary>
        public IEnumerable<(string Name, string? Value)> ModelChain(JsonNode? link)
        {
            var found = new List<(string, string?)>();
            for (var depth = 0; depth < Deepest && Node(link) is { } node; depth++)
            {
                if (Branch(node, depth) is { } branch)
                {
                    link = branch;
                    continue;
                }
                found.AddRange(Loader(node).Select(l => (l.Name, (string?)l.Value)));
                var inputs = Inputs(node) ?? [];
                if (Class(node).StartsWith("ModelSampling", StringComparison.Ordinal))
                    found.Add(("Shift", Number(inputs["shift"])));
                link = inputs["model"];
            }
            // From the checkpoint out: the model first, then its LoRAs as they were put on.
            found.Reverse();
            return found;
        }

        /// <summary>A sampler's sampler, scheduler, steps, CFG, seed and denoise, also when they come from nodes of their own (SamplerCustomAdvanced).</summary>
        public void SamplerSettings(JsonObject inputs, SettingList settings)
        {
            settings.Add("Seed", Number(inputs["seed"] ?? inputs["noise_seed"]));
            if (Node(inputs["noise"]) is { } noise)
                settings.Add("Seed", Number(Inputs(noise)?["noise_seed"] ?? Inputs(noise)?["seed"]));
            settings.Add("Sampler", MediaRecipe.Text(Value(inputs["sampler_name"])));
            if (Node(inputs["sampler"]) is { } sampler)
                settings.Add("Sampler", MediaRecipe.Text(Value(Inputs(sampler)?["sampler_name"])) ?? Spaced(Class(sampler).Replace("Sampler", "")));
            settings.Add("Scheduler", MediaRecipe.Text(Value(inputs["scheduler"])));
            settings.Add("Steps", Number(inputs["steps"]));
            settings.Add("CFG", Number(inputs["cfg"]));
            settings.Add("Denoise", Number(inputs["denoise"]));
            for (var (link, depth) = (inputs["sigmas"], 0); depth < 8 && Node(link) is { } sigmas; depth++)
            {
                var si = Inputs(sigmas) ?? [];
                if (si["sigmas"] is JsonArray next)
                {
                    link = next; // a split or a flip of the schedule: the schedule is further back
                    continue;
                }
                settings.Add("Scheduler", MediaRecipe.Text(Value(si["scheduler"])) ?? Spaced(Class(sigmas).Replace("Scheduler", "")));
                settings.Add("Steps", Number(si["steps"]) ?? (MediaRecipe.Text(si["sigmas"]) is { } list ? (list.Split(',').Length - 1).ToString(CultureInfo.InvariantCulture) : null));
                settings.Add("Denoise", Number(si["denoise"]));
                break;
            }
        }

        /// <summary>"EulerAncestral" as "Euler Ancestral"; "ManualSigmas" as "Manual Sigmas".</summary>
        private static string? Spaced(string name) => name.Length == 0 ? null : Regex.Replace(name, "(?<=[a-z])(?=[A-Z])", " ");
    }
}
