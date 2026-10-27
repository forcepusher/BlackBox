using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Sloperator.Core.Comfy;

/// <summary>A workflow to start from: one the user saved, one of ComfyUI's templates, or a custom node pack's example.</summary>
/// <param name="Name">What the model calls it: "workflows/…" (the user's), "templates/…" (ComfyUI's) or "&lt;pack&gt;/…".</param>
/// <param name="FetchPath">Where the server serves its JSON.</param>
public sealed record ComfyTemplate(string Name, string Title, string Description, string FetchPath, string Kind, IReadOnlyList<string> Tags);

/// <summary>A model file a workflow needs, and whether it is installed.</summary>
public sealed record ComfyModelNeed(string File, string? Folder, string? Url, bool Installed);

/// <summary>Finding a workflow to start from, among the user's saved workflows, ComfyUI's templates and the examples of custom node packs.</summary>
public static partial class ComfyTemplates
{
    /// <summary>Node types the editor uses that never reach the server.</summary>
    internal static readonly HashSet<string> EditorOnly = ["Reroute", "Note", "MarkdownNote", "PrimitiveNode", "GetNode", "SetNode", "Label (rgthree)", "Fast Groups Bypasser (rgthree)"];

    /// <summary>Every workflow the server can hand out, the user's own first.</summary>
    public static async Task<List<ComfyTemplate>> ListAsync(ComfyApi api, CancellationToken ct)
    {
        var list = new List<ComfyTemplate>();

        // The user's saved workflows, described by the README.json notes beside them, if any.
        try
        {
            if (await api.GetJsonAsync("userdata?dir=workflows&recurse=true&split=false", ct).ConfigureAwait(false) is JsonArray files)
            {
                var paths = files.Select(f => (string?)f).OfType<string>().Select(p => p.Replace('\\', '/')).ToList();
                var readmes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var readme in paths.Where(p => Path.GetFileName(p).Equals("README.json", StringComparison.OrdinalIgnoreCase)))
                {
                    try
                    {
                        var json = await api.GetJsonAsync("userdata/" + Uri.EscapeDataString("workflows/" + readme), ct).ConfigureAwait(false);
                        readmes[Folder(readme)] = Notes(json, 4000);
                    }
                    catch (Exception ex) when (ex is ComfyApiException or System.Text.Json.JsonException) { }
                }
                foreach (var path in paths.Where(p => p.EndsWith(".json", StringComparison.OrdinalIgnoreCase) && !Path.GetFileName(p).Equals("README.json", StringComparison.OrdinalIgnoreCase)))
                {
                    var (title, description) = Describe(path, readmes);
                    list.Add(new ComfyTemplate("workflows/" + path[..^5], title, description, "userdata/" + Uri.EscapeDataString("workflows/" + path), "saved",
                        path.Split('/').SkipLast(1).ToList()));
                }
            }
        }
        catch (ComfyApiException) { }

        // Sloperator's own: the classic checkpoint workflows, which run on a PC without a graphics card.
        list.AddRange(ComfyBuiltins.Templates);

        // ComfyUI's own templates, without the ones built on paid API nodes.
        try
        {
            if (await api.GetJsonAsync("templates/index.json", ct).ConfigureAwait(false) is JsonArray categories)
                foreach (var category in categories.OfType<JsonObject>())
                    foreach (var t in (category["templates"] as JsonArray ?? []).OfType<JsonObject>())
                    {
                        if ((string?)t["name"] is not { } name || t["openSource"] is JsonValue os && os.TryGetValue<bool>(out var open) && !open)
                            continue;
                        if (list.Any(x => x.Name == "templates/" + name))
                            continue;
                        var tags = (t["tags"] as JsonArray ?? []).Select(x => (string?)x).OfType<string>().ToList();
                        // What it makes first: the kind a search looks for.
                        if ((string?)t["mediaType"] is { } media) tags.Insert(0, media);
                        tags.AddRange((t["models"] as JsonArray ?? []).Select(x => (string?)x).OfType<string>());
                        list.Add(new ComfyTemplate("templates/" + name, (string?)t["title"] ?? name, (string?)t["description"] ?? "",
                            "templates/" + Uri.EscapeDataString(name) + ".json", "ComfyUI template", tags));
                    }
        }
        catch (ComfyApiException) { }

        // Examples that come with custom node packs.
        try
        {
            if (await api.GetJsonAsync("workflow_templates", ct).ConfigureAwait(false) is JsonObject packs)
                foreach (var (pack, names) in packs)
                    foreach (var name in (names as JsonArray ?? []).Select(x => (string?)x).OfType<string>())
                        list.Add(new ComfyTemplate(pack + "/" + name, name, $"An example from the {pack} custom nodes.",
                            "api/workflow_templates/" + Uri.EscapeDataString(pack) + "/" + Uri.EscapeDataString(name) + ".json", "example", [pack]));
        }
        catch (ComfyApiException) { }
        return list;
    }

    private static string Folder(string path) => path.Contains('/') ? path[..path.LastIndexOf('/')] : "";

    /// <summary>
    /// What a saved workflow does, from the README notes the user keeps beside it: the line that
    /// names its file ("- `video_wan2_2_14B_i2v.json` -- one picture -> video."), and the README's heading.
    /// </summary>
    private static (string Title, string Description) Describe(string path, IReadOnlyDictionary<string, string> readmes)
    {
        var file = Path.GetFileName(path);
        var title = Path.GetFileNameWithoutExtension(path);
        var parts = new List<string>();
        for (var folder = Folder(path); ; folder = Folder(folder))
        {
            if (readmes.TryGetValue(folder, out var readme))
            {
                var lines = readme.Split('\n');
                var line = lines.FirstOrDefault(l => l.Contains(file, StringComparison.OrdinalIgnoreCase) || l.Contains(title + "`", StringComparison.OrdinalIgnoreCase));
                if (line is not null && parts.Count == 0)
                    parts.Add(Cleanup(line[(line.IndexOf(title, StringComparison.OrdinalIgnoreCase) + title.Length)..]));
                var heading = lines.FirstOrDefault(l => l.StartsWith("# ", StringComparison.Ordinal));
                if (heading is not null)
                    parts.Add(heading[2..].Trim());
            }
            if (folder.Length == 0 || parts.Count >= 3)
                break;
        }
        return (title, string.Join(" · ", parts.Where(p => p.Length > 0).Distinct()));

        static string Cleanup(string s)
        {
            if (s.StartsWith(".json", StringComparison.OrdinalIgnoreCase))
                s = s[5..];
            return s.Trim().TrimStart('`', '*').Trim().TrimStart('-', '–', ':', '—').Trim();
        }
    }

    /// <summary>The templates that best match some words, best first: the user's own first among equals, then ones whose models are all there.</summary>
    public static List<ComfyTemplate> Search(IReadOnlyList<ComfyTemplate> all, string query, int max)
    {
        var words = ComfyNodeLibrary.Words(query);
        if (words.Count == 0)
            return all.Where(t => t.Kind is "saved" or "sloperator").Take(max).ToList();
        return all
            .Select(t => (Template: t, Score: Score(t, words)))
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score + (x.Template.Kind == "saved" ? 2 : x.Template.Kind == "example" ? -1 : 0))
            .Take(max)
            .Select(x => x.Template)
            .ToList();
    }

    private static double Score(ComfyTemplate t, IReadOnlyList<string> words)
    {
        var name = Squash(Expand(t.Name));
        var title = Squash(Expand(t.Title));
        var tags = Squash(string.Join(" ", t.Tags));
        var description = Squash(t.Description);
        double score = 0;
        var found = 0;
        var asked = words.Where(w => !StopWords.Contains(w)).ToList();
        if (asked.Count == 0)
            asked = [.. words];
        foreach (var w in asked)
        {
            var alike = Synonyms.FirstOrDefault(g => g.Contains(w)) ?? [w];
            var hit = alike.Any(name.Contains) || alike.Any(title.Contains) ? 3 : alike.Any(tags.Contains) ? 2 : alike.Any(description.Contains) ? 1 : 0;
            if (hit > 0) found++;
            score += hit;
        }
        score = found == asked.Count ? score + 4 * found : found * 0.5;
        // "a picture" wants a template that makes pictures, not one that starts from a picture.
        var wanted = asked.Select(MakesKind).FirstOrDefault(k => k is not null);
        var makes = OutputKind(t);
        return wanted is not null && makes is not null && wanted != makes ? score * 0.5 : score;

        static string Squash(string s) => ComfyNodeLibrary.Squash(s);
    }

    /// <summary>What kind of file a word asks for: image, video, audio or 3d (null for other words).</summary>
    private static string? MakesKind(string word) =>
        Synonyms[0].Contains(word) ? "image" : Synonyms[1].Contains(word) ? "video" :
        Synonyms[2].Contains(word) || Synonyms[3].Contains(word) || Synonyms[4].Contains(word) ? "audio" : Synonyms[5].Contains(word) ? "3d" : null;

    /// <summary>What a template makes, from ComfyUI's media type or the folder a saved workflow sits in (Video/, Music/, Mesh/…).</summary>
    private static string? OutputKind(ComfyTemplate t)
    {
        foreach (var tag in t.Tags.Select(x => x.ToLowerInvariant()))
        {
            switch (tag)
            {
                case "image" or "imageedit" or "imagerotate" or "imagelayerseparation" or "texturemap" or "poseextraction" or "upscale": return "image";
                case "video": return "video";
                case "audio" or "sounds" or "music" or "speech" or "audioedit": return "audio";
                case "3d" or "mesh" or "3d model": return "3d";
            }
        }
        return null;
    }

    private static readonly HashSet<string> StopWords = ["a", "an", "the", "from", "to", "of", "with", "for", "and", "in", "on", "by", "into", "make", "some", "my", "me"];

    private static readonly string[][] Synonyms =
    [
        ["picture", "image", "photo", "img", "still"],
        ["video", "clip", "movie", "animate", "animation"],
        ["sound", "audio", "sfx", "foley"],
        ["music", "song", "track"],
        ["speech", "voice", "tts", "speak", "talk", "narration"],
        ["3d", "mesh", "model", "glb"],
        ["upscale", "enlarge", "bigger", "upscaler", "resolution"],
        ["edit", "change", "modify"],
    ];

    /// <summary>Template names abbreviate what they do (i2v, t2v, s2v): written out, they match the words people use.</summary>
    private static string Expand(string name) =>
        name.Replace("i2v", " image to video ", StringComparison.OrdinalIgnoreCase)
            .Replace("t2v", " text to video ", StringComparison.OrdinalIgnoreCase)
            .Replace("s2v", " sound to video ", StringComparison.OrdinalIgnoreCase)
            .Replace("ia2v", " image audio to video ", StringComparison.OrdinalIgnoreCase)
            .Replace("flf2v", " first last frame to video ", StringComparison.OrdinalIgnoreCase)
            .Replace("t2i", " text to image ", StringComparison.OrdinalIgnoreCase)
            .Replace("i2i", " image to image ", StringComparison.OrdinalIgnoreCase)
            .Replace("t2a", " text to audio ", StringComparison.OrdinalIgnoreCase);

    /// <summary>The template a name means: exactly, without its ".json", or by the end of its name when only one ends so.</summary>
    public static ComfyTemplate? Resolve(IReadOnlyList<ComfyTemplate> all, string name, out IReadOnlyList<string> nearest)
    {
        nearest = [];
        var n = name.Trim().Replace('\\', '/');
        if (n.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            n = n[..^5];
        var exact = all.FirstOrDefault(t => t.Name.Equals(n, StringComparison.OrdinalIgnoreCase));
        if (exact is not null)
            return exact;
        var tail = all.Where(t => t.Name.EndsWith("/" + n, StringComparison.OrdinalIgnoreCase) || t.Name.EndsWith(n, StringComparison.OrdinalIgnoreCase)).ToList();
        if (tail.Count == 1)
            return tail[0];
        // The user's own copy of a template wins over ComfyUI's original of the same name.
        if (tail.Count(t => t.Kind == "saved") == 1)
            return tail.First(t => t.Kind == "saved");
        nearest = tail.Count > 1 ? tail.Select(t => t.Name).Take(5).ToList() : ComfyNodeLibrary.Closest(n, all.Select(t => t.Name), 5);
        return null;
    }

    /// <summary>The notes written into a workflow (Note and MarkdownNote nodes, inside subgraphs too), as plain text.</summary>
    public static string Notes(JsonNode? workflow, int max)
    {
        var sb = new StringBuilder();
        foreach (var node in EditorNodes(workflow))
        {
            if ((string?)node["type"] is not ("Note" or "MarkdownNote"))
                continue;
            if (node["widgets_values"] is JsonArray { Count: > 0 } w && w[0] is JsonValue v && v.TryGetValue<string>(out var text) && text.Trim().Length > 0)
                sb.AppendLine(text.Trim()).AppendLine();
        }
        return sb.ToString().Trim().Clip(max);
    }

    /// <summary>Every node of an editor-format workflow, those inside its subgraphs too.</summary>
    internal static IEnumerable<JsonObject> EditorNodes(JsonNode? workflow)
    {
        foreach (var n in (workflow?["nodes"] as JsonArray ?? []).OfType<JsonObject>())
            yield return n;
        foreach (var sg in (workflow?["definitions"]?["subgraphs"] as JsonArray ?? []).OfType<JsonObject>())
            foreach (var n in (sg["nodes"] as JsonArray ?? []).OfType<JsonObject>())
                yield return n;
    }

    /// <summary>The node types an editor-format workflow uses that the server doesn't have (custom nodes that aren't installed).</summary>
    public static List<string> MissingNodeTypes(JsonNode? workflow, ComfyNodeLibrary library)
    {
        var subgraphs = (workflow?["definitions"]?["subgraphs"] as JsonArray ?? []).OfType<JsonObject>().Select(s => (string?)s["id"]).OfType<string>().ToHashSet();
        return EditorNodes(workflow)
            .Where(n => (int?)n["mode"] is not 2 and not 4) // muted and bypassed nodes don't run
            .Select(n => (string?)n["type"])
            .OfType<string>()
            .Where(t => !EditorOnly.Contains(t) && !subgraphs.Contains(t) && library.Find(t) is null)
            .Distinct()
            .ToList();
    }

    /// <summary>
    /// The model files a workflow needs and whether they are installed: those its template lists
    /// with download links (a node's "models" property), and the model files its nodes are set to.
    /// </summary>
    public static List<ComfyModelNeed> Models(JsonNode? editorWorkflow, ComfyWorkflow? api, IReadOnlyDictionary<string, HashSet<string>> installed)
    {
        var needs = new Dictionary<string, ComfyModelNeed>(StringComparer.OrdinalIgnoreCase);
        var allFiles = installed.Values.SelectMany(s => s).Select(Path.GetFileName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var node in EditorNodes(editorWorkflow))
            foreach (var m in (node["properties"]?["models"] as JsonArray ?? []).OfType<JsonObject>())
                if ((string?)m["name"] is { Length: > 0 } file)
                {
                    var folder = (string?)m["directory"];
                    needs[file] = new ComfyModelNeed(file, folder, (string?)m["url"], IsInstalled(file, folder));
                }
        if (api is not null)
            foreach (var (_, node) in api.All())
                foreach (var (_, value) in node["inputs"] as JsonObject ?? [])
                    if (value is JsonValue v && v.TryGetValue<string>(out var s) && ModelFile().IsMatch(s) && !needs.ContainsKey(Path.GetFileName(s)))
                    {
                        // A well-known file comes with its link even where the workflow gives none.
                        var known = ComfyBuiltins.Known.TryGetValue(Path.GetFileName(s), out var k) ? k : default;
                        needs[Path.GetFileName(s)] = new ComfyModelNeed(s, known.Folder, known.Url, allFiles.Contains(Path.GetFileName(s)));
                    }
        return needs.Values.ToList();

        bool IsInstalled(string file, string? folder) =>
            folder is not null && installed.TryGetValue(folder, out var files) ? files.Contains(file) || files.Any(f => Path.GetFileName(f).Equals(file, StringComparison.OrdinalIgnoreCase)) : allFiles.Contains(file);
    }

    [GeneratedRegex(@"\.(safetensors|ckpt|pt|pth|gguf|sft|onnx)$", RegexOptions.IgnoreCase)]
    private static partial Regex ModelFile();
}
