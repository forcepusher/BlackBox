using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using BlackBox.Core.Comfy;

namespace BlackBox.Core.Video;

/// <summary>The kinds of job a video needs ComfyUI for.</summary>
public enum VideoJob { Frame, Edit, Shot, ShotFromFirst, ShotFromText, LipSync, Song, Foley, Motion }

/// <summary>
/// A way to do a job that is installed: a workflow of the user's, one of ComfyUI's templates, or (for
/// a picture from words) a checkpoint run through BlackBox's plain text-to-image workflow.
/// </summary>
/// <param name="Name">What a storyboard names it by: the template's name, or checkpoints/&lt;file&gt;.</param>
/// <param name="Models">The model files it uses.</param>
public sealed record VideoOption(string Name, string Title, string Kind, string About, IReadOnlyList<string> Models)
{
    public const string Yours = "your workflow", Template = "ComfyUI template", Checkpoint = "checkpoint";

    /// <summary>"nova-furry-v17 · your workflow · novaFurryXL_ilV170.safetensors".</summary>
    public string Label => string.Join(" · ", new[] { Title, Kind, string.Join(", ", Models.Take(3)) }.Where(s => s.Length > 0));

    /// <summary>The words it can be named by: its name, title, notes, folders, models and kind.</summary>
    internal IReadOnlyCollection<string> Words => _words ??= Tokens.ToHashSet();

    /// <summary>Those words run together, for words written together ("zimage", "novafurry").</summary>
    internal string Joined => _joined ??= string.Concat(Tokens);

    private IEnumerable<string> Tokens => VideoTemplates.Tokens(
        $"{Name} {Title} {About} {string.Join(" ", Models)} {Kind} " + (Kind == Yours ? "my mine saved" : Kind == Template ? "comfy" : "ckpt"));

    private HashSet<string>? _words;
    private string? _joined;
}

/// <summary>
/// Which ComfyUI template makes each kind of job: the one a frame, shot or song names in words or
/// by name, else the best one this ComfyUI has every model and node for. Templates are turned into
/// the API format by ComfyUI's own page once per server start, and kept.
/// </summary>
public static partial class VideoTemplates
{
    /// <summary>
    /// Templates known to do each job well, best first, by the end of their names: ComfyUI's own
    /// and the user's copies of them. LTX-2.5 acts out a prompt's beats where 2.3 waved through all
    /// of it, and makes the sound with the picture.
    /// </summary>
    private static readonly Dictionary<VideoJob, string[]> Known = new()
    {
        [VideoJob.Frame] = ["image_z_image_turbo", "image_qwen_image_2512", "image_z_image", "image_qwen_image", "image_flux2", "flux_dev_checkpoint_example", "blackbox/text_to_image"],
        [VideoJob.Edit] = ["image_qwen_image_edit_2509", "image_qwen_image_edit_2511", "image_qwen_image_edit", "flux_kontext_dev_basic"],
        [VideoJob.Shot] = ["video_ltx2_5_flf2v", "video_ltx2_3_flf2v", "video_wan2_2_14b_flf2v"],
        [VideoJob.ShotFromFirst] = ["video_ltx2_5_i2v", "video_ltx2_3_i2v", "video_wan2_2_14b_i2v"],
        [VideoJob.ShotFromText] = ["video_ltx2_5_t2v", "video_ltx2_3_t2v", "video_wan2_2_14b_t2v"],
        [VideoJob.LipSync] = ["ltx2_5_ia2v", "ltx2_3_ia2v", "wan2_2_14b_s2v"],
        // A shot that moves as a video of the user's does: the user's own LTX-2.5 pose workflow first,
        // then ComfyUI's LTX-2.3 union control IC-LoRA (pose, depth or edges from the video), Wan
        // Animate's motion transfer, LTX-2's depth and canny controls and Wan VACE's video to video.
        [VideoJob.Motion] = ["ltx2_5_flf2v_pose", "ltx2_5_pose", "ltx2_5_motion", "video_ltx2_3_ic_lora", "video_wan_animate2", "video_ltx2_depth_to_video", "video_ltx2_canny_to_video", "video_wan_vace_14b_v2v"],
        [VideoJob.Song] = ["audio_ace_step1_5_xl_turbo", "audio_ace_step1_5_xl_sft", "audio_ace_step_1_5_checkpoint", "audio_ace_step1_5_xl_base", "audio_ace_step_1_t2a_song"],
        [VideoJob.Foley] = ["blackbox/video_to_audio", "mmaudio"],
    };

    /// <summary>What each job is, said where a template for it is missing.</summary>
    public static string Describe(VideoJob job) => job switch
    {
        VideoJob.Frame => "a picture from words",
        VideoJob.Edit => "a picture from other pictures (an edit model such as Qwen-Image-Edit)",
        VideoJob.Shot => "a video from a first and a last frame",
        VideoJob.ShotFromFirst => "a video from its first frame",
        VideoJob.ShotFromText => "a video from words",
        VideoJob.LipSync => "a video from a picture and a sound, lip-synced (LTX's image and audio to video)",
        VideoJob.Foley => "foley, a sound from a video (MMAudio)",
        VideoJob.Motion => "a video that moves as a reference video does (LTX's IC-LoRA control or Wan Animate, from a first frame and the video)",
        _ => "a song from a style and lyrics (ACE-Step)",
    };

    /// <summary>A checkpoint as a way to make a picture: checkpoints/&lt;its file as ComfyUI lists it&gt;.</summary>
    public const string CheckpointPrefix = "checkpoints/";

    /// <summary>Templates turned into the API format, by server, server start and name.</summary>
    private static readonly ConcurrentDictionary<(string Server, int Generation, string Name), Loaded> Cache = new();

    /// <summary>A template as a workflow to run, and whether its models and nodes are all there.</summary>
    public sealed record Loaded(ComfyTemplate Template, ComfyWorkflow Workflow, IReadOnlyList<string> Missing)
    {
        public bool Ready => Missing.Count == 0;
    }

    /// <summary>A template by name (or a checkpoint, checkpoints/…), as a workflow to run (a copy of its own to change).</summary>
    public static async Task<Loaded> LoadAsync(ComfyHub hub, ComfyApi api, string name, CancellationToken ct)
    {
        if (name.StartsWith(CheckpointPrefix, StringComparison.OrdinalIgnoreCase))
            return await CheckpointAsync(hub, api, name[CheckpointPrefix.Length..], ct).ConfigureAwait(false);
        var all = await hub.TemplatesAsync(api, ct).ConfigureAwait(false);
        var template = ComfyTemplates.Resolve(all, name, out var nearest)
                       ?? throw new VideoException($"There is no ComfyUI template \"{name}\"" + (nearest.Count > 0 ? $"; did you mean {string.Join(", ", nearest)}?" : "."));
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

    /// <summary>A checkpoint run through BlackBox's plain text-to-image workflow.</summary>
    private static async Task<Loaded> CheckpointAsync(ComfyHub hub, ComfyApi api, string file, CancellationToken ct)
    {
        var models = await hub.ModelsAsync(api, ct).ConfigureAwait(false);
        var builtin = ComfyBuiltins.All.First(b => b.Name == "text_to_image");
        var workflow = ComfyWorkflow.FromApi(ComfyWorkflow.ParseText(builtin.Workflow));
        ((JsonObject)workflow.Nodes["1"]!["inputs"]!)["ckpt_name"] = file;
        var installed = models.TryGetValue("checkpoints", out var files) && files.Contains(file);
        var template = new ComfyTemplate(CheckpointPrefix + file, Path.GetFileNameWithoutExtension(file), builtin.Description, "builtin:text_to_image", VideoOption.Checkpoint, ["image"]);
        return new Loaded(template, workflow, installed ? [] : [file]);
    }

    /// <summary>
    /// The template for a job: the one <paramref name="model"/> names (a template's or checkpoint's
    /// name, or words matched against what is installed), else the first known one that is ready,
    /// else the first installed way to do it. A note says when the words named something installed
    /// that can't do this job and the best one was used instead.
    /// </summary>
    public static async Task<(Loaded? Template, string? Note)> PickAsync(ComfyHub hub, ComfyApi api, string? model, VideoJob job, CancellationToken ct)
    {
        string? note = null;
        if (model?.Trim() is { Length: > 0 } words)
        {
            var all = await hub.TemplatesAsync(api, ct).ConfigureAwait(false);
            var options = await OptionsAsync(hub, api, job, ct).ConfigureAwait(false);
            var named = words.StartsWith(CheckpointPrefix, StringComparison.OrdinalIgnoreCase) || all.Any(t => t.Name.Equals(words, StringComparison.OrdinalIgnoreCase));
            if (named && options.Any(o => o.Name.Equals(words, StringComparison.OrdinalIgnoreCase)))
                return (await LoadAsync(hub, api, words, ct).ConfigureAwait(false), null);
            if (!named && Match(options, words) is { } option)
                return (await LoadAsync(hub, api, option.Name, ct).ConfigureAwait(false), null);
            // Installed, but for another kind of job: a picture from words for a frame made from others;
            // a shot that kept the template its job had once (an upgraded storyboard's) and has a last
            // frame or a line since, which that template has no place for.
            VideoOption? elsewhere = null;
            foreach (var other in Siblings(job).Where(j => j != job))
            {
                var theirs = await OptionsAsync(hub, api, other, ct).ConfigureAwait(false);
                elsewhere ??= (named ? theirs.FirstOrDefault(o => o.Name.Equals(words, StringComparison.OrdinalIgnoreCase)) : Match(theirs, words)) is { } o
                    ? o with { About = Describe(other) } : null;
            }
            // Named exactly, and known for no job: the user's own, used as they say.
            if (named && elsewhere is null)
                return (await LoadAsync(hub, api, words, ct).ConfigureAwait(false), null);
            if (elsewhere is null)
                throw new VideoException($"Nothing installed for {Describe(job)} matches \"{words}\". " +
                                        (options.Count == 0 ? "Nothing installed does this job." : $"What is installed for it: {string.Join("; ", options.Take(25).Select(o => o.Label))}.") +
                                        " Name one of these, or leave it empty for the best one.");
            note = $"\"{words}\" is {elsewhere.Title} ({elsewhere.Kind}), which makes {elsewhere.About}, not {Describe(job)}, so the best installed one for that was used.";
        }
        var everything = await hub.TemplatesAsync(api, ct).ConfigureAwait(false);
        foreach (var fragment in Known[job])
            foreach (var t in everything.Where(t => Ends(t.Name, fragment)).OrderBy(t => t.Kind == "saved" ? 0 : 1))
            {
                try
                {
                    var loaded = await LoadAsync(hub, api, t.Name, ct).ConfigureAwait(false);
                    if (loaded.Ready)
                        return (loaded, note);
                }
                catch (Exception ex) when (ex is VideoException or ComfyApiException or ComfyWorkflowException or JsonException) { }
            }
        foreach (var option in await OptionsAsync(hub, api, job, ct).ConfigureAwait(false))
        {
            try
            {
                var loaded = await LoadAsync(hub, api, option.Name, ct).ConfigureAwait(false);
                if (loaded.Ready)
                    return (loaded, note);
            }
            catch (Exception ex) when (ex is VideoException or ComfyApiException or ComfyWorkflowException or JsonException) { }
        }
        return (null, note);

        static bool Ends(string name, string fragment) =>
            name.EndsWith("/" + fragment, StringComparison.OrdinalIgnoreCase) || name.Equals(fragment, StringComparison.OrdinalIgnoreCase) ||
            !fragment.Contains('/') && Path.GetFileName(name).Contains(fragment, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The jobs that make the same kind of thing: words for one may name a way to do another.</summary>
    private static VideoJob[] Siblings(VideoJob job) => job switch
    {
        VideoJob.Frame or VideoJob.Edit => [VideoJob.Frame, VideoJob.Edit],
        VideoJob.Song => [VideoJob.Song],
        VideoJob.Foley => [VideoJob.Foley],
        _ => [VideoJob.Shot, VideoJob.ShotFromFirst, VideoJob.ShotFromText, VideoJob.LipSync, VideoJob.Motion],
    };

    // ---- what is installed ----------------------------------------------------------------------------

    /// <summary>What each server start offers for each job.</summary>
    private static readonly ConcurrentDictionary<(string Server, int Generation, VideoJob Job), IReadOnlyList<VideoOption>> Options = new();

    /// <summary>Workflows as the server hands them out (editor or API format), by server start and name.</summary>
    private static readonly ConcurrentDictionary<(string Server, int Generation, string Name), JsonNode?> Fetched = new();

    /// <summary>
    /// Every way this ComfyUI can do a job with what is installed: the user's saved workflows and
    /// ComfyUI's templates whose nodes make that kind of thing from those inputs (a picture from
    /// words alone, a video from two pictures…) and whose models and nodes are all there, the known
    /// good ones first; and for a picture from words, every single-file SD 1.5, SD 2 or SDXL
    /// checkpoint, run through BlackBox's plain text-to-image workflow. Not a search: nothing
    /// installed that can do the job is left out.
    /// </summary>
    public static async Task<IReadOnlyList<VideoOption>> OptionsAsync(ComfyHub hub, ComfyApi api, VideoJob job, CancellationToken ct)
    {
        var key = (api.BaseUrl.ToString(), hub.Generation, job);
        if (Options.TryGetValue(key, out var known))
            return known;
        var all = await hub.TemplatesAsync(api, ct).ConfigureAwait(false);
        var models = await hub.ModelsAsync(api, ct).ConfigureAwait(false);
        var library = await hub.LibraryAsync(api, ct).ConfigureAwait(false);
        var files = models.Values.SelectMany(s => s).Select(f => Path.GetFileName(f.Replace('\\', '/'))).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var media = job switch { VideoJob.Frame or VideoJob.Edit => "image", VideoJob.Song or VideoJob.Foley => "audio", _ => "video" };
        var candidates = all
            .Where(t => t.Kind == "saved" || t.Kind == "ComfyUI template" && (t.Tags.FirstOrDefault()?.Equals(media, StringComparison.OrdinalIgnoreCase) == true || Rank(t.Name, job) < int.MaxValue))
            .Select((t, i) => (Template: t, Order: i))
            .OrderBy(x => Rank(x.Template.Name, job)).ThenBy(x => x.Template.Kind == "saved" ? 0 : 1).ThenBy(x => x.Order)
            .Select(x => x.Template)
            .ToList();
        var found = new VideoOption?[candidates.Count];
        await Parallel.ForEachAsync(Enumerable.Range(0, candidates.Count), new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = ct }, async (i, token) =>
        {
            var t = candidates[i];
            var json = await FetchAsync(hub, api, t, token).ConfigureAwait(false);
            if (json is null || !Fits(json, job))
                return;
            var needs = ModelFiles(json);
            if (needs.Any(f => !files.Contains(Path.GetFileName(f.Replace('\\', '/')))) || MissingNodes(json, library).Count > 0)
                return;
            found[i] = new VideoOption(t.Name, t.Title, t.Kind == "saved" ? VideoOption.Yours : VideoOption.Template, t.Description,
                needs.Select(f => Path.GetFileName(f.Replace('\\', '/'))).Distinct(StringComparer.OrdinalIgnoreCase).ToList());
        }).ConfigureAwait(false);
        var options = found.OfType<VideoOption>().ToList();
        if (job == VideoJob.Frame)
            options.AddRange(await CheckpointsAsync(api, models, ct).ConfigureAwait(false));
        return Options[key] = options;
    }

    /// <summary>Where a template stands among the known good ones for a job (int.MaxValue: not one of them).</summary>
    private static int Rank(string name, VideoJob job)
    {
        var known = Known[job];
        for (var i = 0; i < known.Length; i++)
            if (name.EndsWith("/" + known[i], StringComparison.OrdinalIgnoreCase) || Path.GetFileName(name).Contains(known[i], StringComparison.OrdinalIgnoreCase))
                return i;
        return int.MaxValue;
    }

    private static async Task<JsonNode?> FetchAsync(ComfyHub hub, ComfyApi api, ComfyTemplate t, CancellationToken ct)
    {
        var key = (api.BaseUrl.ToString(), hub.Generation, t.Name);
        if (Fetched.TryGetValue(key, out var json))
            return json;
        try
        {
            json = ComfyBuiltins.For(t) is { } builtin ? ComfyWorkflow.ParseText(builtin.Workflow) : await api.GetJsonAsync(t.FetchPath, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is ComfyApiException or JsonException or ComfyWorkflowException)
        {
            json = null;
        }
        return Fetched[key] = json;
    }

    /// <summary>The node types a workflow runs (not those muted or bypassed), from either format.</summary>
    private static List<string> ActiveTypes(JsonNode workflow)
    {
        if (ComfyWorkflow.IsEditorFormat(workflow))
        {
            var subgraphs = (workflow["definitions"]?["subgraphs"] as JsonArray ?? []).OfType<JsonObject>().Select(s => (string?)s["id"]).OfType<string>().ToHashSet();
            return ComfyTemplates.EditorNodes(workflow)
                .Where(n => n["mode"] is not JsonValue m || !m.TryGetValue<int>(out var mode) || mode is not 2 and not 4)
                .Select(n => n["type"] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null)
                .OfType<string>()
                .Where(t => !ComfyTemplates.EditorOnly.Contains(t) && !subgraphs.Contains(t))
                .ToList();
        }
        return (workflow as JsonObject ?? []).Select(p => p.Value?["class_type"] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null).OfType<string>().ToList();
    }

    private static List<string> MissingNodes(JsonNode workflow, ComfyNodeLibrary library) =>
        ActiveTypes(workflow).Where(t => library.Find(t) is null).Distinct().ToList();

    /// <summary>
    /// Whether a workflow does a job, by the nodes it runs: what it saves (a picture, a video, a
    /// sound), what it loads (pictures, a sound, a video), and that it takes a prompt (foley needn't).
    /// </summary>
    internal static bool Fits(JsonNode workflow, VideoJob job)
    {
        var types = ActiveTypes(workflow);
        bool Any(Func<string, bool> test) => types.Any(test);
        var savesImage = Any(t => t.Contains("SaveImage", StringComparison.OrdinalIgnoreCase) || t is "PreviewImage" or "Image Save");
        var savesVideo = Any(t => t is "SaveVideo" or "CreateVideo" or "SaveWEBM" or "SaveAnimatedWEBP" or "SaveAnimatedPNG" or "VHS_VideoCombine");
        var savesAudio = Any(t => t.StartsWith("SaveAudio", StringComparison.OrdinalIgnoreCase) || t is "PreviewAudio");
        // A 3D model's workflow: what it saves is a mesh, whatever pictures it shows on the way.
        var savesMesh = Any(t => t.Contains("3D", StringComparison.OrdinalIgnoreCase) || t.Contains("GLB", StringComparison.OrdinalIgnoreCase) ||
                                 t.Contains("Mesh", StringComparison.OrdinalIgnoreCase) || t.Contains("Trellis", StringComparison.OrdinalIgnoreCase));
        var pictures = types.Count(t => t.StartsWith("LoadImage", StringComparison.OrdinalIgnoreCase));
        var sound = Any(t => t.StartsWith("LoadAudio", StringComparison.OrdinalIgnoreCase));
        var video = Any(t => t.Contains("LoadVideo", StringComparison.OrdinalIgnoreCase));
        var prompt = Any(t => t.Contains("TextEncode", StringComparison.OrdinalIgnoreCase));
        // Foley is heard from a video: its prompt (what is heard) is the sampler's own, if it has one.
        if (job == VideoJob.Foley)
            return video && savesAudio && !savesMesh;
        // A workflow that loads a video is one that follows it: only a shot with a motion video is made with one.
        if (!prompt || savesMesh || video != (job == VideoJob.Motion))
            return false;
        return job switch
        {
            VideoJob.Frame => savesImage && !savesVideo && !savesAudio && pictures == 0 && !sound,
            VideoJob.Edit => savesImage && !savesVideo && !savesAudio && pictures > 0 && !sound,
            VideoJob.Shot => savesVideo && pictures >= 2 && !sound,
            VideoJob.ShotFromFirst => savesVideo && pictures == 1 && !sound,
            VideoJob.ShotFromText => savesVideo && pictures == 0 && !sound,
            VideoJob.LipSync => savesVideo && pictures >= 1 && sound,
            VideoJob.Motion => savesVideo && !sound,
            _ => savesAudio && !savesVideo && pictures == 0 && !sound,
        };
    }

    /// <summary>The model files a workflow names: in its loaders' model notes and in their settings.</summary>
    private static List<string> ModelFiles(JsonNode workflow)
    {
        var files = new List<string>();
        if (ComfyWorkflow.IsEditorFormat(workflow))
            foreach (var node in ComfyTemplates.EditorNodes(workflow).Where(n => n["mode"] is not JsonValue m || !m.TryGetValue<int>(out var mode) || mode is not 2 and not 4))
            {
                foreach (var m in (node["properties"]?["models"] as JsonArray ?? []).OfType<JsonObject>())
                    if (m["name"] is JsonValue v && v.TryGetValue<string>(out var name) && name.Length > 0)
                        files.Add(name);
                Collect(node["widgets_values"]);
            }
        else
            foreach (var (_, node) in workflow as JsonObject ?? [])
                Collect(node?["inputs"]);
        return files.Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        void Collect(JsonNode? values)
        {
            switch (values)
            {
                case JsonArray a:
                    foreach (var x in a) Collect(x);
                    break;
                case JsonObject o:
                    foreach (var (_, x) in o) Collect(x);
                    break;
                case JsonValue v when v.TryGetValue<string>(out var s) && ModelFile().IsMatch(s):
                    files.Add(s);
                    break;
            }
        }
    }

    [GeneratedRegex(@"^[^<>|*?""\n]+\.(safetensors|ckpt|pt|pth|gguf|sft)$", RegexOptions.IgnoreCase)]
    private static partial Regex ModelFile();

    // ---- checkpoints ------------------------------------------------------------------------------------

    /// <summary>
    /// Every checkpoint BlackBox's plain text-to-image workflow can run: a single file with an SD 1.5,
    /// SD 2 or SDXL model, its text encoder and VAE all in it, told by the names of its tensors (a
    /// video, 3D, sound or pose model in the same folder has other parts, or none of these). A
    /// checkpoint on another PC, which can't be read, is offered as it is.
    /// </summary>
    private static async Task<List<VideoOption>> CheckpointsAsync(ComfyApi api, Dictionary<string, HashSet<string>> models, CancellationToken ct)
    {
        if (!models.TryGetValue("checkpoints", out var names))
            return [];
        var dirs = new List<string>();
        try
        {
            if (await api.GetJsonAsync("internal/folder_paths", ct).ConfigureAwait(false) is JsonObject paths && paths["checkpoints"] is JsonArray list)
                dirs.AddRange(list.OfType<JsonValue>().Select(v => v.TryGetValue<string>(out var s) ? s : null).OfType<string>());
        }
        catch (Exception ex) when (ex is ComfyApiException or JsonException) { }
        var options = new List<VideoOption>();
        foreach (var name in names.Order(StringComparer.OrdinalIgnoreCase))
        {
            var path = dirs.Select(d => Path.Combine(d, name)).FirstOrDefault(File.Exists);
            string? kind = null;
            if (path is not null && (kind = CheckpointKind(path)) is null)
                continue;
            options.Add(new VideoOption(CheckpointPrefix + name, Path.GetFileNameWithoutExtension(name), VideoOption.Checkpoint,
                $"{(kind is null ? "A checkpoint" : $"An {kind} checkpoint")}, run with BlackBox's plain text-to-image workflow (20 steps, cfg 7).", [name]));
        }
        return options;
    }

    private static readonly ConcurrentDictionary<(string Path, long Length, DateTime Written), string?> Kinds = new();

    /// <summary>"SDXL", "SD 1.5" or "SD 2" for a single-file SD checkpoint, by its tensors' names; null for anything else (or a file that can't be read).</summary>
    internal static string? CheckpointKind(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return Kinds.GetOrAdd((path, info.Length, info.LastWriteTimeUtc), _ =>
            {
                if (!path.EndsWith(".safetensors", StringComparison.OrdinalIgnoreCase))
                    return null;
                using var file = File.OpenRead(path);
                Span<byte> size = stackalloc byte[8];
                file.ReadExactly(size);
                var length = BitConverter.ToInt64(size);
                if (length is <= 0 or > 64 << 20)
                    return null;
                var head = new byte[length];
                file.ReadExactly(head);
                using var doc = JsonDocument.Parse(head);
                var keys = doc.RootElement.EnumerateObject().Select(p => p.Name).ToList();
                bool Has(string prefix) => keys.Any(k => k.StartsWith(prefix, StringComparison.Ordinal));
                if (!Has("model.diffusion_model.input_blocks.") || !Has("first_stage_model."))
                    return null;
                return Has("conditioner.embedders.1.") ? "SDXL" : Has("cond_stage_model.transformer.") ? "SD 1.5" : Has("cond_stage_model.model.") ? "SD 2" : null;
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or EndOfStreamException)
        {
            return null;
        }
    }

    // ---- naming one in words ----------------------------------------------------------------------------

    /// <summary>Words that say nothing about which model: "made with the model", "use my".</summary>
    private static readonly HashSet<string> Filler = ["the", "a", "an", "with", "and", "of", "for", "in", "on", "use", "using", "made", "make", "model", "models",
        "please", "by", "from", "via", "it", "this", "that", "one", "to", "is", "style", "look"];

    /// <summary>Words that say what kind of thing it is (one of the user's, ComfyUI's, a checkpoint), not which.</summary>
    private static readonly HashSet<string> KindWords = ["my", "mine", "saved", "your", "workflow", "comfy", "ui", "template", "checkpoint", "ckpt"];

    /// <summary>
    /// Words as tokens: lower case, split at anything not a letter or digit, at a capital after a
    /// small letter and between letters and digits ("novaFurryXL_ilV170" → nova, furry, xl, il, v, 170).
    /// </summary>
    internal static IEnumerable<string> Tokens(string text) =>
        Token().Matches(text).Select(m => m.Value.ToLowerInvariant());

    [GeneratedRegex(@"[A-Z]?[a-z]+|[A-Z]+(?![a-z])|\d+")]
    private static partial Regex Token();

    /// <summary>
    /// The installed way the words name ("furry nova xl", "z image turbo", "my pony workflow"): the
    /// one that has most of the words among its name, title, notes, folders and models, the user's
    /// own workflows first among equals, then ComfyUI's, then bare checkpoints. Null when none has
    /// more than half of them.
    /// </summary>
    public static VideoOption? Match(IReadOnlyList<VideoOption> options, string words)
    {
        var said = words.Trim();
        if (options.FirstOrDefault(o => o.Name.Equals(said, StringComparison.OrdinalIgnoreCase) || o.Title.Equals(said, StringComparison.OrdinalIgnoreCase)) is { } named)
            return named;
        var asked = Tokens(said).Where(t => !Filler.Contains(t)).Distinct().ToList();
        if (asked.Count == 0)
            return null;
        var best = options
            .Select((o, i) => (Option: o, Found: asked.Where(t => Has(o, t)).ToList(), Order: i))
            // A number alone ("2", "5" of "ltx 2.5") is in half the model files, and "my workflow" fits all
            // the user's own: a word that names the model has to be found too.
            .Where(x => x.Found.Any(t => t.Length >= 2 && !char.IsDigit(t[0]) && !KindWords.Contains(t)))
            .OrderByDescending(x => x.Found.Count)
            .ThenBy(x => x.Option.Kind switch { VideoOption.Yours => 0, VideoOption.Template => 1, _ => 2 })
            .ThenBy(x => x.Order)
            .FirstOrDefault();
        // Most of the words: "ace step" isn't a Qwen workflow with "4 steps" in its notes.
        return best.Option is not null && (asked.Count == 1 || best.Found.Count * 2 > asked.Count) ? best.Option : null;

        // A word as one of its words, or a long one written into them ("zimage" in z_image).
        static bool Has(VideoOption o, string t) => o.Words.Contains(t) || t.Length >= 5 && o.Joined.Contains(t, StringComparison.Ordinal);
    }
}
