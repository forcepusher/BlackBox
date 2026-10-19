using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using BlackBox.Core.Comfy;

namespace BlackBox.Core.Film;

/// <summary>The kinds of job a film needs ComfyUI for.</summary>
public enum FilmJob { Frame, Edit, Shot, ShotFromFirst, ShotFromText, LipSync, Song }

/// <summary>
/// Which ComfyUI template makes each kind of job: the storyboard's choice, else the best one this
/// ComfyUI has every model and node for. Templates are turned into the API format by ComfyUI's
/// own page once per server start, and kept.
/// </summary>
public static class FilmTemplates
{
    /// <summary>
    /// Templates known to do each job well, best first, by the end of their names: ComfyUI's own
    /// and the user's copies of them. LTX-2.5 acts out a prompt's beats where 2.3 waved through all
    /// of it, and makes the sound with the picture.
    /// </summary>
    private static readonly Dictionary<FilmJob, string[]> Known = new()
    {
        [FilmJob.Frame] = ["image_z_image_turbo", "image_qwen_image_2512", "image_z_image", "image_qwen_image", "image_flux2", "flux_dev_checkpoint_example", "blackbox/text_to_image"],
        [FilmJob.Edit] = ["image_qwen_image_edit_2509", "image_qwen_image_edit_2511", "image_qwen_image_edit", "flux_kontext_dev_basic"],
        [FilmJob.Shot] = ["video_ltx2_5_flf2v", "video_ltx2_3_flf2v", "video_wan2_2_14b_flf2v"],
        [FilmJob.ShotFromFirst] = ["video_ltx2_5_i2v", "video_ltx2_3_i2v", "video_wan2_2_14b_i2v"],
        [FilmJob.ShotFromText] = ["video_ltx2_5_t2v", "video_ltx2_3_t2v", "video_wan2_2_14b_t2v"],
        [FilmJob.LipSync] = ["ltx2_5_ia2v", "ltx2_3_ia2v", "wan2_2_14b_s2v"],
        [FilmJob.Song] = ["audio_ace_step1_5_xl_turbo", "audio_ace_step1_5_xl_sft", "audio_ace_step_1_5_checkpoint", "audio_ace_step1_5_xl_base", "audio_ace_step_1_t2a_song"],
    };

    /// <summary>Words to search the templates by when none of the known ones is ready.</summary>
    private static readonly Dictionary<FilmJob, string> Search = new()
    {
        [FilmJob.Frame] = "text to image",
        [FilmJob.Edit] = "image edit",
        [FilmJob.Shot] = "first last frame to video",
        [FilmJob.ShotFromFirst] = "image to video",
        [FilmJob.ShotFromText] = "text to video",
        [FilmJob.LipSync] = "image audio to video",
        [FilmJob.Song] = "song lyrics music",
    };

    /// <summary>What each job is, said where a template for it is missing.</summary>
    public static string Describe(FilmJob job) => job switch
    {
        FilmJob.Frame => "a picture from words",
        FilmJob.Edit => "a picture from other pictures (an edit model such as Qwen-Image-Edit)",
        FilmJob.Shot => "a video from a first and a last frame",
        FilmJob.ShotFromFirst => "a video from its first frame",
        FilmJob.ShotFromText => "a video from words",
        FilmJob.LipSync => "a video from a picture and a sound, lip-synced (LTX's image and audio to video)",
        _ => "a song from a style and lyrics (ACE-Step)",
    };

    /// <summary>The storyboard's choice for a job, if it made one.</summary>
    public static string? Chosen(FilmWorkflows w, FilmJob job) => job switch
    {
        FilmJob.Frame => w.Frame,
        FilmJob.Edit => w.Edit,
        FilmJob.Shot => w.Shot,
        FilmJob.ShotFromFirst => w.ShotFromFirst,
        FilmJob.ShotFromText => w.ShotFromText,
        FilmJob.LipSync => w.LipSync,
        _ => w.Song,
    };

    public static void Choose(FilmWorkflows w, FilmJob job, string? name)
    {
        switch (job)
        {
            case FilmJob.Frame: w.Frame = name; break;
            case FilmJob.Edit: w.Edit = name; break;
            case FilmJob.Shot: w.Shot = name; break;
            case FilmJob.ShotFromFirst: w.ShotFromFirst = name; break;
            case FilmJob.ShotFromText: w.ShotFromText = name; break;
            case FilmJob.LipSync: w.LipSync = name; break;
            default: w.Song = name; break;
        }
    }

    /// <summary>Templates turned into the API format, by server, server start and name.</summary>
    private static readonly ConcurrentDictionary<(string Server, int Generation, string Name), Loaded> Cache = new();

    /// <summary>A template as a workflow to run, and whether its models and nodes are all there.</summary>
    public sealed record Loaded(ComfyTemplate Template, ComfyWorkflow Workflow, IReadOnlyList<string> Missing)
    {
        public bool Ready => Missing.Count == 0;
    }

    /// <summary>A template by name, as a workflow to run (a copy of its own to change).</summary>
    public static async Task<Loaded> LoadAsync(ComfyHub hub, ComfyApi api, string name, CancellationToken ct)
    {
        var all = await hub.TemplatesAsync(api, ct).ConfigureAwait(false);
        var template = ComfyTemplates.Resolve(all, name, out var nearest)
                       ?? throw new FilmException($"There is no ComfyUI template \"{name}\"" + (nearest.Count > 0 ? $"; did you mean {string.Join(", ", nearest)}?" : "."));
        var key = (api.BaseUrl.ToString(), hub.Generation, template.Name);
        if (!Cache.TryGetValue(key, out var loaded))
        {
            var library = await hub.LibraryAsync(api, ct).ConfigureAwait(false);
            var models = await hub.ModelsAsync(api, ct).ConfigureAwait(false);
            JsonNode? editor;
            ComfyWorkflow workflow;
            if (ComfyBuiltins.For(template) is { } builtin)
                (editor, workflow) = (null, ComfyWorkflow.FromApi(ComfyWorkflow.ParseText(builtin.Workflow)));
            else
            {
                editor = await api.GetJsonAsync(template.FetchPath, ct).ConfigureAwait(false);
                workflow = ComfyWorkflow.IsEditorFormat(editor)
                    ? ComfyWorkflow.FromApi(await hub.Frontend.ConvertAsync(api.BaseUrl, hub.Generation, editor!, ct).ConfigureAwait(false))
                    : ComfyWorkflow.FromApi(editor);
            }
            var missing = ComfyTemplates.Models(editor, workflow, models).Where(m => !m.Installed).Select(m => m.File)
                .Concat(workflow.All().Select(n => ComfyWorkflow.ClassOf(n.Node)).Where(c => library.Find(c) is null).Distinct().Select(c => "node " + c))
                .ToList();
            loaded = Cache[key] = new Loaded(template, workflow, missing);
        }
        return loaded with { Workflow = ComfyWorkflow.FromApi(loaded.Workflow.Nodes) };
    }

    /// <summary>
    /// The template for a job: the storyboard's choice, else the first known one that is ready,
    /// else the best ready match of a search. Null when none is.
    /// </summary>
    public static async Task<Loaded?> PickAsync(ComfyHub hub, ComfyApi api, FilmWorkflows chosen, FilmJob job, CancellationToken ct)
    {
        if (Chosen(chosen, job) is { Length: > 0 } name)
            return await LoadAsync(hub, api, name, ct).ConfigureAwait(false);
        foreach (var candidate in await CandidatesAsync(hub, api, job, ct).ConfigureAwait(false))
        {
            try
            {
                var loaded = await LoadAsync(hub, api, candidate, ct).ConfigureAwait(false);
                if (loaded.Ready)
                    return loaded;
            }
            catch (Exception ex) when (ex is FilmException or ComfyApiException or ComfyWorkflowException or System.Text.Json.JsonException) { }
        }
        return null;
    }

    /// <summary>The templates that could do a job, the known good ones first, then the best matches of a search.</summary>
    public static async Task<List<string>> CandidatesAsync(ComfyHub hub, ComfyApi api, FilmJob job, CancellationToken ct)
    {
        var all = await hub.TemplatesAsync(api, ct).ConfigureAwait(false);
        var names = new List<string>();
        foreach (var fragment in Known[job])
            foreach (var t in all.Where(t => Matches(t.Name, fragment)).OrderBy(t => t.Kind == "saved" ? 0 : 1))
                if (!names.Contains(t.Name))
                    names.Add(t.Name);
        foreach (var t in ComfyTemplates.Search(all, Search[job], 6))
            if (!names.Contains(t.Name) && OutputFits(t, job))
                names.Add(t.Name);
        return names;

        static bool Matches(string name, string fragment) =>
            name.EndsWith("/" + fragment, StringComparison.OrdinalIgnoreCase) || name.Equals(fragment, StringComparison.OrdinalIgnoreCase) ||
            !fragment.Contains('/') && Path.GetFileName(name).Contains(fragment, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Whether a template a search found makes what the job wants (a picture, a video, a sound).</summary>
    private static bool OutputFits(ComfyTemplate t, FilmJob job)
    {
        var tags = string.Join(" ", t.Tags).ToLowerInvariant() + " " + t.Name.ToLowerInvariant();
        return job switch
        {
            FilmJob.Frame or FilmJob.Edit => tags.Contains("image") && !tags.Contains("video"),
            FilmJob.Song => tags.Contains("audio") || tags.Contains("music"),
            _ => tags.Contains("video"),
        };
    }
}
