using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace BlackBox.Core.Comfy;

/// <summary>One input of a node type, as /object_info describes it.</summary>
public sealed class ComfyInputDef
{
    public required string Name { get; init; }
    public required bool Required { get; init; }

    /// <summary>INT, FLOAT, STRING, BOOLEAN, COMBO, or a socket type such as MODEL or IMAGE ("FLOAT,INT" accepts either).</summary>
    public required string Type { get; init; }

    /// <summary>The choices of a COMBO; null otherwise.</summary>
    public IReadOnlyList<string>? Choices { get; init; }

    /// <summary>Its settings: default, min, max, multiline, tooltip, image_upload…</summary>
    public JsonObject Extra { get; init; } = [];

    public static readonly HashSet<string> WidgetTypes = ["INT", "FLOAT", "STRING", "BOOLEAN", "COMBO"];

    /// <summary>A value typed in (a number, a text, a choice), as opposed to a link from another node's output.</summary>
    public bool IsWidget => (WidgetTypes.Contains(Type) || Choices is not null) && Extra["forceInput"]?.GetValue<bool>() != true;

    /// <summary>A V3 input that stands for several (values.a, values.b…, or the inputs of a chosen option).</summary>
    public bool IsDynamic => Type is "COMFY_AUTOGROW_V3" or "COMFY_DYNAMICCOMBO_V3" or "COMFY_DYNAMICSLOT_V3";

    /// <summary>A file in ComfyUI's input folder (LoadImage, LoadAudio, LoadVideo): a local path given here is uploaded.</summary>
    public bool TakesInputFile => Choices is not null &&
        (Extra["image_upload"] is not null || Extra["audio_upload"] is not null || Extra["video_upload"] is not null || Extra["upload"] is not null ||
         Extra["mesh_upload"] is not null || Extra["file_upload"] is not null);

    public double? Min => Number("min");
    public double? Max => Number("max");

    private double? Number(string key) => Extra[key] is JsonValue v && v.TryGetValue<double>(out var d) ? d : null;

    /// <summary>"INT, default 20, 1..10000"; "one of euler, heun, …" for a COMBO.</summary>
    public string Describe(int maxChoices = 30)
    {
        var sb = new StringBuilder();
        if (Choices is not null)
        {
            sb.Append(Choices.Count == 0 ? "a choice, but none is offered" : "one of ");
            sb.Append(string.Join(", ", Choices.Take(maxChoices)));
            if (Choices.Count > maxChoices)
                sb.Append($" (and {Choices.Count - maxChoices} more)");
        }
        else
        {
            sb.Append(Type);
            if (!IsWidget && !IsDynamic)
                sb.Append(" (link)");
        }
        if (Extra["default"] is { } def && Choices is null)
            sb.Append(", default ").Append(ComfyNodeLibrary.Short(def, 60));
        if (Min is { } min && Max is { } max)
            sb.Append(", ").Append(Num(min)).Append("..").Append(Num(max));
        else if (Min is { } onlyMin)
            sb.Append(", at least ").Append(Num(onlyMin));
        if (Extra["multiline"]?.GetValue<bool>() == true)
            sb.Append(", multiline");
        if (Extra["control_after_generate"] is not null)
            sb.Append(" (change it for a different result)");
        if (TakesInputFile)
            sb.Append(" (a file in ComfyUI's input folder; a full local path is uploaded for you)");
        if (Type == "COMFY_AUTOGROW_V3" && Extra["template"] is JsonObject t)
        {
            var names = t["names"] is JsonArray n ? n.Select(x => (string?)x).Take(4).ToList() : null;
            var prefix = (string?)t["prefix"];
            sb.Append(names is not null ? $": inputs {Name}.{string.Join($", {Name}.", names)}…" : prefix is not null ? $": inputs {Name}.{prefix}0, {Name}.{prefix}1…" : "");
        }
        if (Extra["tooltip"] is JsonValue tip && tip.TryGetValue<string>(out var tooltip) && tooltip.Length > 0)
            sb.Append(" — ").Append(tooltip.ReplaceLineEndings(" ").Clip(140));
        return sb.ToString();
    }

    private static string Num(double d) => d >= 1e15 ? d.ToString("0", CultureInfo.InvariantCulture) : d.ToString("0.###", CultureInfo.InvariantCulture);
}

/// <summary>A node type, as /object_info describes it.</summary>
public sealed class ComfyNodeDef
{
    public required string Name { get; init; }
    public string DisplayName { get; init; } = "";
    public string Category { get; init; } = "";
    public string Description { get; init; } = "";
    public string Module { get; init; } = "";
    public bool IsOutput { get; init; }
    public bool Deprecated { get; init; }
    public bool ApiNode { get; init; }
    public IReadOnlyList<string> Aliases { get; init; } = [];
    public required IReadOnlyList<ComfyInputDef> Inputs { get; init; }
    public required IReadOnlyList<(string Type, string Name)> Outputs { get; init; }

    public ComfyInputDef? Input(string name) => Inputs.FirstOrDefault(i => i.Name == name);

    /// <summary>The custom node pack it comes from ("ComfyUI-VideoHelperSuite"), or null for ComfyUI's own nodes.</summary>
    public string? Pack => Module.StartsWith("custom_nodes.", StringComparison.Ordinal) ? Module["custom_nodes.".Length..] : null;

    /// <summary>One line: "WanImageToVideo (conditioning/video_models): positive, negative, vae, width, … → positive, negative, latent".</summary>
    public string Signature()
    {
        var sb = new StringBuilder(Name);
        if (DisplayName.Length > 0 && DisplayName != Name)
            sb.Append($" \"{DisplayName}\"");
        sb.Append(" (").Append(Category).Append(Pack is { } pack ? ", " + pack : "").Append("): ");
        sb.Append(string.Join(", ", Inputs.Select(i => i.Name + (i.Required ? "" : "?"))));
        sb.Append(" → ").Append(Outputs.Count == 0 ? (IsOutput ? "saves/shows its input" : "nothing") : string.Join(", ", Outputs.Select(o => o.Name)));
        if (Deprecated)
            sb.Append(" [deprecated]");
        if (ApiNode)
            sb.Append(" [paid API node: needs a Comfy account]");
        return sb.ToString();
    }

    /// <summary>Everything about it the model needs to wire it up.</summary>
    public string Details()
    {
        var sb = new StringBuilder();
        sb.Append(Name);
        if (DisplayName.Length > 0 && DisplayName != Name)
            sb.Append($" \"{DisplayName}\"");
        sb.Append($" (category {Category}{(Pack is { } p ? ", from " + p : "")})");
        if (IsOutput)
            sb.Append(", an output node");
        if (Deprecated)
            sb.Append(", deprecated");
        if (ApiNode)
            sb.Append(", a paid API node that needs a Comfy account");
        sb.AppendLine(Description.Length > 0 ? ": " + Description.ReplaceLineEndings(" ").Clip(400) : "");
        sb.AppendLine("Inputs:");
        foreach (var i in Inputs)
            sb.Append("  ").Append(i.Name).Append(i.Required ? "" : " (optional)").Append(": ").AppendLine(i.Describe());
        if (Inputs.Count == 0)
            sb.AppendLine("  (none)");
        sb.Append("Outputs: ");
        sb.AppendLine(Outputs.Count == 0 ? "none" : string.Join(", ", Outputs.Select((o, n) => $"{n} {o.Name}" + (o.Name != o.Type ? $" ({o.Type})" : ""))));
        return sb.ToString().TrimEnd();
    }
}

/// <summary>All node types a ComfyUI server offers (its /object_info), to look names up and check workflows against.</summary>
public sealed class ComfyNodeLibrary
{
    private readonly Dictionary<string, ComfyNodeDef> _nodes;

    public ComfyNodeLibrary(IEnumerable<ComfyNodeDef> nodes) => _nodes = nodes.ToDictionary(n => n.Name, StringComparer.Ordinal);

    public IReadOnlyCollection<ComfyNodeDef> All => _nodes.Values;

    public ComfyNodeDef? Find(string name) => _nodes.GetValueOrDefault(name);

    public static ComfyNodeLibrary Parse(JsonObject objectInfo)
    {
        var list = new List<ComfyNodeDef>();
        foreach (var (name, node) in objectInfo)
        {
            if (node is not JsonObject n)
                continue;
            var inputs = new List<ComfyInputDef>();
            var input = n["input"] as JsonObject;
            foreach (var section in new[] { "required", "optional" })
            {
                if (input?[section] is not JsonObject specs)
                    continue;
                // input_order keeps the order the node declares (JSON objects keep it too, mostly).
                var order = n["input_order"]?[section] is JsonArray o ? o.Select(x => (string?)x).OfType<string>().ToList() : specs.Select(kv => kv.Key).ToList();
                foreach (var key in order.Concat(specs.Select(kv => kv.Key)).Distinct())
                    if (specs[key] is JsonArray spec)
                        inputs.Add(ParseInput(key, section == "required", spec));
            }
            var types = n["output"] as JsonArray ?? [];
            var names = n["output_name"] as JsonArray ?? [];
            var outputs = types.Select((t, i) => (Type: TypeName(t), Name: i < names.Count && names[i] is JsonValue v && v.TryGetValue<string>(out var s) ? s : TypeName(t))).ToList();
            list.Add(new ComfyNodeDef
            {
                Name = name,
                DisplayName = (string?)n["display_name"] ?? "",
                Category = (string?)n["category"] ?? "",
                Description = n["description"] is JsonValue d && d.TryGetValue<string>(out var desc) ? desc : "",
                Module = (string?)n["python_module"] ?? "",
                IsOutput = n["output_node"]?.GetValue<bool>() == true,
                Deprecated = n["deprecated"] is JsonValue dep && dep.TryGetValue<bool>(out var isDep) && isDep,
                ApiNode = n["api_node"] is JsonValue api && api.TryGetValue<bool>(out var isApi) && isApi,
                Aliases = n["search_aliases"] is JsonArray a ? a.Select(x => (string?)x).OfType<string>().ToList() : [],
                Inputs = inputs,
                Outputs = outputs,
            });
        }
        return new ComfyNodeLibrary(list);
    }

    private static string TypeName(JsonNode? t) => t switch
    {
        JsonValue v when v.TryGetValue<string>(out var s) => s,
        JsonArray => "COMBO",
        _ => "*",
    };

    private static ComfyInputDef ParseInput(string name, bool required, JsonArray spec)
    {
        var extra = spec.Count > 1 && spec[1] is JsonObject e ? e : new JsonObject();
        List<string>? choices = null;
        string type;
        if (spec.Count > 0 && spec[0] is JsonArray old)
        {
            // The old way of writing a COMBO: the list of choices in place of the type.
            type = "COMBO";
            choices = old.Select(Choice).ToList();
        }
        else
        {
            type = spec.Count > 0 && spec[0] is JsonValue v && v.TryGetValue<string>(out var s) ? s : "*";
            if (type == "COMBO")
                choices = extra["options"] is JsonArray opts ? opts.Select(Choice).ToList() : [];
        }
        return new ComfyInputDef { Name = name, Required = required, Type = type, Choices = choices, Extra = extra };

        static string Choice(JsonNode? c) => c is JsonValue cv && cv.TryGetValue<string>(out var s) ? s : c?.ToJsonString() ?? "";
    }

    /// <summary>
    /// The node types that best match some words (in their names, category, description, pack and
    /// aliases), best first. Deprecated and paid API nodes come last.
    /// </summary>
    public IReadOnlyList<ComfyNodeDef> Search(string query, int max)
    {
        var words = Words(query);
        if (words.Count == 0)
            return [];
        return _nodes.Values
            .Select(n => (Node: n, Score: Score(n, words)))
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score - (x.Node.Deprecated ? 5 : 0) - (x.Node.ApiNode ? 8 : 0))
            .ThenBy(x => x.Node.Name.Length)
            .Take(max)
            .Select(x => x.Node)
            .ToList();
    }

    private static double Score(ComfyNodeDef n, IReadOnlyList<string> words)
    {
        double score = 0;
        var name = Squash(n.Name);
        var display = n.DisplayName.ToLowerInvariant();
        var rest = (n.Category + " " + n.Module + " " + string.Join(" ", n.Aliases)).ToLowerInvariant();
        var description = n.Description.ToLowerInvariant();
        var all = 0;
        foreach (var w in words)
        {
            var hit = 0.0;
            if (name == w) hit = 10;
            else if (name.Contains(w)) hit = 4 + (name.StartsWith(w) ? 1 : 0);
            else if (display.Contains(w)) hit = 3;
            else if (rest.Contains(w)) hit = 2;
            else if (description.Contains(w)) hit = 1;
            if (hit > 0) all++;
            score += hit;
        }
        // Every word found counts most: "wan video" means a node about both.
        return all == words.Count ? score + 5 * all : score * 0.4;
    }

    internal static List<string> Words(string text) =>
        text.ToLowerInvariant().Split([' ', ',', ';', '/', '\t', '\n', '-', '_'], StringSplitOptions.RemoveEmptyEntries).Select(Squash).Where(w => w.Length > 0).Distinct().ToList();

    /// <summary>Lower case, letters and digits only: "KSampler (Advanced)" → "ksampleradvanced".</summary>
    internal static string Squash(string s) => new(s.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

    /// <summary>The candidates closest to a name that doesn't exist, best first ("SaveImag" → SaveImage).</summary>
    public static IReadOnlyList<string> Closest(string wrong, IEnumerable<string> candidates, int max = 3)
    {
        var w = Squash(wrong);
        if (w.Length == 0)
            return [];
        return candidates
            .Select(c => (Name: c, Score: Similarity(w, Squash(c))))
            .Where(x => x.Score >= 0.45)
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Name.Length)
            .Take(max)
            .Select(x => x.Name)
            .ToList();
    }

    /// <summary>0..1: how alike two squashed names are, by edit distance, with a bonus for one containing the other.</summary>
    internal static double Similarity(string a, string b)
    {
        if (a.Length == 0 || b.Length == 0)
            return 0;
        if (a == b)
            return 1;
        var distance = Levenshtein(a, b);
        var score = 1.0 - (double)distance / Math.Max(a.Length, b.Length);
        if (a.Contains(b) || b.Contains(a))
            score = Math.Max(score, 0.6 + 0.4 * Math.Min(a.Length, b.Length) / Math.Max(a.Length, b.Length));
        return score;
    }

    private static int Levenshtein(string a, string b)
    {
        if (a.Length > 200 || b.Length > 200)
            return Math.Max(a.Length, b.Length);
        var prev = new int[b.Length + 1];
        var cur = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++) prev[j] = j;
        for (var i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            for (var j = 1; j <= b.Length; j++)
                cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            (prev, cur) = (cur, prev);
        }
        return prev[b.Length];
    }

    /// <summary>A JSON value as the model would write it, cut short: 20, 3.5, "euler", ["4", 0].</summary>
    internal static string Short(JsonNode? node, int max) => node switch
    {
        null => "null",
        JsonValue v when v.TryGetValue<string>(out var s) => "\"" + s.ReplaceLineEndings("\\n").Clip(max) + "\"",
        _ => node.ToJsonString().Clip(max),
    };
}
