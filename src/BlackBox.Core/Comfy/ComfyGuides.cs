using System.Text;
using System.Text.RegularExpressions;

namespace BlackBox.Core.Comfy;

/// <summary>
/// How to write what ComfyUI's models are given, as BlackBox carries it (Comfy/Guides/*.md, built
/// in). The video guide is LTX's own advice (docs.ltx.io's prompting guide and motion control) and
/// what went wrong in BlackBox's films: the user found most takes ruined by motion going the wrong
/// way, poses bending wrong and props moving by themselves, and LTX drawing its own first picture
/// badly. The comfyui tool shows it with a video workflow, and the film director writes shots by it.
/// </summary>
public static partial class ComfyGuides
{
    public const string Video = "video-prompts";

    private static readonly Dictionary<string, string> Loaded = [];

    /// <summary>The guides there are, by name.</summary>
    public static IReadOnlyList<string> Names { get; } =
        typeof(ComfyGuides).Assembly.GetManifestResourceNames().Where(n => n.StartsWith("guide:", StringComparison.Ordinal)).Select(n => n["guide:".Length..]).Order().ToList();

    /// <summary>A guide's text; empty when there is none by that name.</summary>
    public static string Text(string name)
    {
        lock (Loaded)
        {
            if (Loaded.TryGetValue(name, out var text))
                return text;
            using var stream = typeof(ComfyGuides).Assembly.GetManifestResourceStream("guide:" + name);
            text = stream is null ? "" : new StreamReader(stream, Encoding.UTF8).ReadToEnd().ReplaceLineEndings("\n").Trim();
            Loaded[name] = text;
            return text;
        }
    }

    /// <summary>A guide without the sections under these headings (## …), for a reader that doesn't run workflows.</summary>
    public static string Without(string name, params string[] headings)
    {
        var sb = new StringBuilder();
        var skip = false;
        foreach (var line in Text(name).Split('\n'))
        {
            if (line.StartsWith("## ", StringComparison.Ordinal))
                skip = headings.Any(h => line[3..].Trim().Equals(h, StringComparison.OrdinalIgnoreCase));
            if (!skip)
                sb.Append(line).Append('\n');
        }
        return sb.ToString().Trim();
    }

    /// <summary>The guides whose name or text hold the words; all of them for none.</summary>
    public static IReadOnlyList<string> Find(string? words)
    {
        var terms = (words ?? "").ToLowerInvariant().Split([' ', ',', ';'], StringSplitOptions.RemoveEmptyEntries).Where(w => w.Length > 2).ToList();
        if (terms.Count == 0)
            return Names;
        return Names.Where(n => terms.Any(t => n.Contains(t, StringComparison.OrdinalIgnoreCase) || Text(n).Contains(t, StringComparison.OrdinalIgnoreCase))).ToList();
    }

    /// <summary>Whether a workflow makes video with LTX (its nodes are LTXV…, as ComfyUI's own and ComfyUI-LTXVideo's are).</summary>
    public static bool IsLtx(ComfyWorkflow workflow) => workflow.All().Any(n => LtxNode().IsMatch(ComfyWorkflow.ClassOf(n.Node)));

    /// <summary>
    /// Whether an LTX workflow animates a picture (image to video, first and last frames: a picture
    /// put in as a frame), not one LTX draws from words alone.
    /// </summary>
    public static bool StartsFromPicture(ComfyWorkflow workflow) =>
        workflow.All().Any(n => ComfyWorkflow.ClassOf(n.Node) is var type &&
                                (type.StartsWith("LTXVImgToVideo", StringComparison.Ordinal) || type.StartsWith("LTXVAddGuide", StringComparison.Ordinal)));

    [GeneratedRegex(@"^LTXV|^LTX ", RegexOptions.IgnoreCase)]
    private static partial Regex LtxNode();
}
