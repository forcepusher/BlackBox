using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using BlackBox.Core.Comfy;
using BlackBox.Core.Video;
using BlackBox.Core.Media;

namespace BlackBox.Core.Tools;

/// <summary>
/// Videos from a storyboard (see <see cref="Storyboard"/>): the same video folder the user sees in
/// the Video generator panel. The agent plans or changes it, makes its frames, songs and shots with
/// ComfyUI (each with a fixed-seed workflow of its own), checks every shot against what it should
/// show and fixes the ones that don't, joins them and checks the video. A check is numbers (does the
/// clip start and end on its frames, does it move, does it jump) and a fresh look by the model at
/// a sheet of its frames: the model that made a clip judged its own too kindly (four bunnies it
/// called mature and true to the sheet, which the user saw as a chubby teenager).
/// </summary>
public sealed class VideoTool : Tool
{
    public override string Name => "video";

    public override string Description =>
        "Make a video (a music video, a short story, an ad: anything longer than one shot) from a storyboard, with ComfyUI on this PC. " +
        "The video is its folder, which the user also sees in BlackBox's Video generator panel beside the chat, from where they pick its pictures, clips and folders into their messages to you: " +
        "References, with the pictures frames are made from (image files of the user's and generated images BlackBox makes); Generated, with what BlackBox makes: each shot's frames and clip (s1.mp4) and the songs; and the joined video. The user calls the shots clips: clip 1 is the first. " +
        "Each file has a .json of its name beside it (what it is and how it was made: video.json for video.mp4) and the ComfyUI workflow that made it (video.workflow.json). " +
        "The storyboard is all of it at once: shots in order, each going from a first frame to an optional last frame in so many seconds, the pictures they start and end on, and the sounds over them. " +
        "action=storyboard shows it (path: the video's folder), with BlackBox's guide to writing one the first time in a chat, or makes or changes it with storyboard={...}: title (the video's folder is named after it: a new one renames it), " +
        "width, height, fps, voice (who says the lines: age, voice, manner), soundLines (how many sound lines), frames:[{id, prompt (what it shows and its look), model, seed, from: [what it is made from, to keep people and places the same: frames' ids, " +
        "a picture in the References folder by its name (\"Mira\" or \"Mira.png\"), or a picture of the user's from elsewhere by its full path (one they attached: it is copied into References as it is)], " +
        "file (a picture of the user's, cut to the video's frame), name, reference: true for a character or place sheet, made at its own shape into References/<name>.png, " +
        "of: a shot's id, for a frame taken from where that shot's clip ends, so the shot that starts on it goes on from that moment (after a shot without a last frame)}], " +
        "shots:[{id, name (a few words naming it, \"intro\", \"the leap\"), first, last (frame ids), seconds, prompt (what happens, and its look), camera, model, transition: cut|dissolve|fade, " +
        "line (what is said in it, lip-synced, in the video's voice; it starts 0.4 s in, so leave time for it), seed}], " +
        "sounds:[{id, name, source: song (made by BlackBox: tags = its style, lyrics, model, seed) or file (the user's: file = its full path, copied into Sounds), soundLine (from 0), start (seconds into the video), " +
        "trim (seconds cut off its start), seconds (how long it plays; 0 to its end), volume (1 as it is)}]: sounds overlap as they please, as on a video editor's sound lines. " +
        "Frames, shots and sounds are matched by id; {\"id\": …, \"delete\": true} removes one, and a field set to null is taken out (a shot's \"last\": null). " +
        "Nothing is video-wide, so a change remakes only what it changes: write the video's look into every frame's and shot's prompt. " +
        "model is what one is made with: a model or workflow in words (\"z image turbo\", \"juggernaut xl\", \"my pony workflow\"), matched against what ComfyUI has installed, " +
        "or none for the best installed one; action=models lists what is installed for each kind of picture, video and song. " +
        "Without path a new video goes into a folder of its own in BlackBox's Data\\videos, where the Video generator panel finds it " +
        "(BlackBox is portable: what it makes stays next to it). " +
        "A shot that starts on the last frame of the one before goes on with the same camera; " +
        "a new first frame is a cut to another scene or angle. Every shot starts on a frame: the video model animates a picture well and draws one badly. " +
        "Keep one clear action per shot, as many seconds as it takes (2 to 8; a longer clip holds its opening pose, then rushes): say which way things move on screen, from where to where, and what stays still, " +
        "and what is heard (the shot's own sound is kept at half under a song; music comes from songs on the sound lines, not the video model). " +
        "The References folder also holds any picture the user puts there (action=storyboard lists them): name one in a frame's from to make the frame from it, " +
        "whatever its shape: a frame made from pictures is made the video's size with all of each in view. " +
        "The user's pictures stay as they gave them: never crop, resize, retouch or redraw one, or put pictures of your own beside them (a cut-out, a padded copy); " +
        "name the whole picture in from and say in the prompt who or what in it to take (\"the rabbit on the left, in the grey t-shirt\"): the picture model picks one out of a lineup or a sheet. " +
        "action=frame id= makes a frame's picture (a reference's too) and has a fresh look at it; action=sound id= makes a song, a minute long at least (the video uses its start), the best of tries=3 by a judge " +
        "of how good music sounds (SongEval); write lyrics for the part the video uses. action=shot id= makes a shot's clip and checks it (does it start and end on its frames, " +
        "how it moves, and a fresh look at what happens, faults and whether people stay the same); tries=N makes up to N tries with new seeds until one passes. " +
        "What is made again keeps the one before as a version (in Versions, in its folder), which the user can put back in the Video generator panel. " +
        "Fix a frame or shot the look finds wrong: a plainer prompt, another seed, frames closer to each other, or a new frame. " +
        "action=join puts the shots and the sounds together into the video; action=check looks at a frame or shot (id) or, without id, the whole video. " +
        "Order: frames (those others are made from first), the shots, the songs, then join and check the video. " +
        "prompt, camera, seconds, seed, model and from given with action=frame or shot change the storyboard before it is made.";

    public override ToolRisk Risk => ToolRisk.Risky;

    public override JsonObject Parameters => Schema(
        ("action", Str("What to do", "storyboard", "frame", "shot", "sound", "join", "check", "models"), true),
        ("path", Str("The video's folder (or the name of a video in the videos folder); a new video's folder for action=storyboard"), false),
        ("id", Str("A frame's id (action=frame, check), a shot's (action=shot, check) or a sound's (action=sound)"), false),
        ("storyboard", new JsonObject { ["type"] = "object", ["description"] = "action=storyboard: the storyboard, or the parts of it to change (frames and shots by id)" }, false),
        ("prompt", Str("action=frame or shot: a new prompt for it"), false),
        ("camera", Str("action=shot: what the camera does"), false),
        ("seconds", Num("action=shot: how long it lasts"), false),
        ("seed", Int("action=frame or shot: a new seed (another result from the same prompt)"), false),
        ("model", Str("action=frame or shot: what it is made with, a model or workflow in words or by name (\"\" for the best installed)"), false),
        ("from", StrArray("action=frame: what it is made from: frames' ids, references' folders (\"Mira\") or pictures in them (\"Mira/side.png\") in References, or a picture of the user's from elsewhere by its full path"), false),
        ("tries", Int("action=shot: up to this many tries, each with a new seed, until the check passes (default 1); action=sound: songs to make, the best kept (default 3)"), false));

    public override string Describe(JsonObject args)
    {
        var action = GetString(args, "action") ?? "";
        var id = GetString(args, "id");
        return action switch
        {
            "storyboard" => args["storyboard"] is JsonObject ? "Change the storyboard" + (GetString(args, "path") is { } p ? $" in {p}" : "") : $"Storyboard of {GetString(args, "path") ?? "the video"}",
            "frame" => $"Make frame {id}",
            "shot" => $"Make shot {id}" + (GetNumber(args, "tries") is > 1 and var t ? $" ({t} tries at most)" : ""),
            "sound" => $"Make song {id}",
            "join" => "Join the video",
            "check" => id is null ? "Check the video" : $"Check {id}",
            "models" => "List the installed models for videos",
            _ => "video " + action,
        };
    }

    private VideoMaker? _maker;

    /// <summary>The video this task worked on last, for calls that leave path out.</summary>
    private string? _lastFolder;

    /// <summary>What the call's changes did beside them (a picture copied in) or couldn't do as asked (a rename), said first.</summary>
    private string? _note;

    public override async Task<ToolResult> ExecuteAsync(JsonObject args, ToolContext ctx, CancellationToken ct)
    {
        var action = RequireString(args, "action").Trim().ToLowerInvariant();
        try
        {
            if (action is "models" or "workflows" or "templates")
                return await ModelsAsync(ctx, ct).ConfigureAwait(false);
            _note = null;
            var board = LoadOrCreate(args, ctx, create: action == "storyboard");
            _lastFolder = board.Folder;
            if (action != "storyboard" && Changes(args, action) is { Count: > 0 } changes && Fits(board, args, action))
            {
                board = Storyboard.Merge(board, changes);
                Save(board);
            }
            var result = action switch
            {
                "storyboard" => Show(board, args["storyboard"] is JsonObject, GuideFor(ctx)),
                "frame" => await FrameAsync(board, RequireId(args), ctx, ct).ConfigureAwait(false),
                "shot" => await ShotAsync(board, RequireId(args), (int)Math.Clamp(GetNumber(args, "tries") ?? 1, 1, 8), ctx, ct).ConfigureAwait(false),
                "sound" or "song" => await SoundAsync(board, GetString(args, "id"), (int)Math.Clamp(GetNumber(args, "tries") ?? 3, 1, 8), ctx, ct).ConfigureAwait(false),
                "join" or "montage" => await JoinAsync(board, ctx, ct).ConfigureAwait(false),
                "check" => await CheckAsync(board, GetString(args, "id"), ctx, ct).ConfigureAwait(false),
                _ => throw new ToolArgumentException("action must be storyboard, frame, shot, sound, join, check or models."),
            };
            return _note is { } note ? result with { Text = note + result.Text } : result;
        }
        catch (VideoStillRunningException ex)
        {
            return new ToolResult(ex.Message + (ctx.UserMessage.IsCancellationRequested ? " The user wrote to you meanwhile; read that first." : ""));
        }
        catch (Exception ex) when (ex is VideoException or ComfyApiException or ComfyWorkflowException or IOException or UnauthorizedAccessException)
        {
            return ToolResult.Error(ex.Message);
        }
    }

    private static string RequireId(JsonObject args) => GetString(args, "id")?.Trim() is { Length: > 0 } id ? id : throw new ToolArgumentException("Give the id of the frame or shot.");

    /// <summary>The storyboard the call is about: by path, the one used last in this task, or a new one for action=storyboard.</summary>
    private Storyboard LoadOrCreate(JsonObject args, ToolContext ctx, bool create)
    {
        var root = ctx.VideosDirectory ?? Path.Combine(ctx.ScratchDirectory ?? Path.GetTempPath(), "videos");
        var path = GetString(args, "path");
        var given = path is { Length: > 0 } ? ctx.ResolvePath(path) : _lastFolder;
        // A video's name alone ("Spaghetti Days"), or any path not from a drive, is its folder in
        // the videos folder, new or not: from the working folder, a new "lighthouse" was made in the
        // user's home, out of BlackBox's Data, where the Video generator panel doesn't list it.
        if (path is { Length: > 0 } && !Path.IsPathRooted(path) && !Exists(given))
            given = Path.Combine(root, path);
        var change = args["storyboard"] as JsonObject;
        // A whole storyboard of another title, with no path, is a new video, not the last one used in
        // this task, once that one says what is in it: "two ads, coffee and tea" wrote the tea ad
        // over the coffee one. Whole: shots that each say what happens, none taken out. A new title
        // alone, or with changes to some pieces, renames and changes the video in use, and the
        // Video generator panel's new video (its empty s1, f1 and f2) is laid out in place.
        if (create && path is not { Length: > 0 } && Exists(given) && change?["title"] is JsonValue t && t.TryGetValue<string>(out var newTitle) &&
            newTitle.Trim().Length > 0 && change["shots"] is JsonArray { Count: > 0 } shots &&
            shots.All(s => s is JsonObject o && o["delete"] is null && o["prompt"] is JsonValue p && p.ToString().Trim().Length > 0) &&
            Storyboard.Load(given) is { } last &&
            (last.Shots.Any(s => s.Prompt.Trim().Length > 0) || last.Frames.Any(f => f.Prompt.Trim().Length > 0)) &&
            !newTitle.Trim().Equals(last.Title.Trim(), StringComparison.OrdinalIgnoreCase))
            given = null;
        if (Exists(given))
        {
            var board = Storyboard.Load(given);
            if (change is not null)
            {
                board = Storyboard.Merge(board, change);
                // The video's folder is named after it: a new title renames it.
                if (change["title"] is JsonValue named && named.TryGetValue<string>(out var newName) && newName.Trim().Length > 0 &&
                    Storyboard.Safe(newName) != Path.GetFileName(board.Folder))
                    try { board.Rename(newName); }
                    catch (Exception ex) when (ex is VideoException or IOException or UnauthorizedAccessException)
                    {
                        _note = $"The video's folder couldn't be renamed to \"{newName.Trim()}\" ({ex.Message}): the video keeps the name \"{Path.GetFileName(board.Folder)}\".\n";
                    }
                Save(board);
            }
            return board;
        }
        if (!create || change is null)
        {
            var videos = Storyboard.Recent(root);
            throw new VideoException((given is null
                ? "Which video? Give path: the video's folder, or make a new one with action=storyboard and storyboard={…}."
                : $"There is no video in {given}. Make one with action=storyboard and storyboard={{…}} (path is where it goes).") +
                (videos.Count > 0 ? $" The videos in {root}, the latest first: {string.Join("; ", videos.Take(12))}." : ""));
        }
        var title = (string?)change["title"] ?? "Untitled video";
        var name = Storyboard.Safe(title);
        var folder = given ?? Path.Combine(root, name);
        // Another video of the same title is another video: it isn't written over. Its number goes
        // after a name cut short enough to keep it: cut at 80 characters with the number, a long
        // title gave the same folder for every number, and the tool went round for ever.
        for (var n = 2; given is null && Storyboard.IsVideo(folder); n++)
            folder = Path.Combine(root, $"{(name.Length > 72 ? name[..72].TrimEnd() : name)} {n}");
        if (given is not null && File.Exists(given))
            folder = Path.GetDirectoryName(given)!;
        Directory.CreateDirectory(folder);
        var created = Storyboard.Merge(new Storyboard { Title = title, Folder = folder }, change);
        Save(created);
        return created;

        static bool Exists([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] string? at) =>
            at is not null && (Storyboard.IsVideo(at) || File.Exists(at) && Storyboard.IsVideo(Path.GetDirectoryName(at)!));
    }

    /// <summary>
    /// Saves the video, the pictures from elsewhere its frames are made from copied into References
    /// as they are (see <see cref="Storyboard.TakeInPictures"/>), which the result says first.
    /// </summary>
    private void Save(Storyboard board)
    {
        board.Save();
        if (board.TakeInPictures().Distinct().ToList() is not { Count: > 0 } taken)
            return;
        board.Save();
        _note += $"Copied into References as {(taken.Count == 1 ? "it is" : "they are")}, where the user sees {(taken.Count == 1 ? "it" : "them")} as their own: " +
                 string.Join("; ", taken.Select(t => $"{t.From} as \"{t.To}\"")) + ", the name from has for it now. Keep the user's pictures as they are, and make the references from them.\n";
    }

    /// <summary>The storyboard changes a frame or shot call gives with it: a new prompt, seed, length.</summary>
    /// <summary>
    /// Whether a frame or shot call's changes go into the storyboard: to a piece of that kind, or a
    /// new one it says what is in. A frame call with a shot's id added a frame, renamed, that the call
    /// then didn't find; one with only a seed for an unknown id left an empty frame.
    /// </summary>
    private static bool Fits(Storyboard board, JsonObject args, string action)
    {
        var id = GetString(args, "id")?.Trim();
        var known = action == "frame" ? board.Frame(id) is not null : board.Shot(id) is not null;
        var other = action == "frame" ? board.Shot(id) is not null : board.Frame(id) is not null;
        return known || !other && GetString(args, "prompt")?.Trim() is { Length: > 0 };
    }

    private static JsonObject? Changes(JsonObject args, string action)
    {
        if (GetString(args, "id")?.Trim() is not { Length: > 0 } id || action is not ("frame" or "shot"))
            return null;
        var item = new JsonObject { ["id"] = id };
        if (GetString(args, "prompt") is { } prompt) item["prompt"] = prompt;
        if (GetNumber(args, "seed") is { } seed) item["seed"] = (long)seed;
        if (GetString(args, "model") is { } model) item["model"] = model;
        if (action == "shot")
        {
            if (GetString(args, "camera") is { } camera) item["camera"] = camera;
            if (GetNumber(args, "seconds") is { } seconds) item["seconds"] = seconds;
        }
        else if (GetStringList(args, "from") is { } from)
            item["from"] = new JsonArray(from.Select(f => (JsonNode)JsonValue.Create(f)!).ToArray());
        return item.Count == 1 ? null : new JsonObject { [action == "frame" ? "frames" : "shots"] = new JsonArray(item) };
    }

    private VideoMaker Maker(ToolContext ctx)
    {
        if (ctx.Comfy is not { } hub)
            throw new VideoException("ComfyUI isn't available in this run.");
        if (_maker is null)
        {
            _maker = new VideoMaker(hub, ctx.Media) { Judge = ctx.ToolsDirectory is { } tools ? new SongJudge(Path.Combine(tools, "songeval")) : null };
            // From before it may be started: when the task is over, a ComfyUI BlackBox started is closed.
            _maker.Use();
            ctx.AfterRun(nameof(VideoTool), () =>
            {
                var maker = _maker;
                _maker = null;
                return maker?.ReleaseAsync() ?? Task.CompletedTask;
            });
        }
        return _maker;
    }

    // ---- the storyboard -------------------------------------------------------------------------------

    private static ToolResult Show(Storyboard board, bool changed, string? guide)
    {
        var line = "";
        if (changed)
        {
            // Every shot starts on a picture a picture model makes: one written without gets its own
            // now, so the Video generator panel shows it, and it can be changed before it is made.
            var added = board.AddFirstFrames();
            if (added.Count > 0)
                board.Save();
            line = $"Saved the video in {board.Folder}: each piece in References or Generated with its settings in a .json beside it. The user sees it in the Video generator panel (Toolkit, at the top of BlackBox).\n" +
                   (added.Count == 0 ? "" : $"Shots without a first frame got one each from their prompts ({string.Join(", ", added.Select(f => f.Id))}): " +
                                            "rewrite each as the still picture the shot starts on, with who is in it as the references show them.\n");
        }
        return new ToolResult(line + Overview(board) + (guide is null ? "" : "\n\n" + guide));
    }

    /// <summary>How the storyboard guide starts as the tool shows it, by which a chat that has it is told.</summary>
    internal const string GuideHead = "BlackBox's guide to writing a storyboard";

    /// <summary>The context reset the guide was last shown after (see <see cref="ToolContext.ContextResets"/>).</summary>
    private int? _guideShown;

    /// <summary>
    /// The guide to writing a storyboard, and how the video model reads a shot's prompt, with the
    /// first storyboard a chat reads or writes: the user never writes a prompt, so what the models
    /// are given is what the agent writes. Null when the chat has it already (a context reset drops it).
    /// </summary>
    private string? GuideFor(ToolContext ctx)
    {
        if (_guideShown == ctx.ContextResets || ctx.InConversation?.Invoke(GuideHead) == true)
            return null;
        _guideShown = ctx.ContextResets;
        return $"{GuideHead}, to follow when you write or change one:\n\n" + ComfyGuides.Text(ComfyGuides.Storyboards) +
               "\n\nHow the video model (LTX) reads a shot's prompt, which is how to write one:\n\n" +
               ComfyGuides.Without(ComfyGuides.Video, "Pictures first", "Sound and music", "Exact motion: tracks and controls", "Settings");
    }

    /// <summary>The storyboard as the model reads it: each frame and shot, what is made and what is out of date, and what to do next.</summary>
    internal static string Overview(Storyboard board)
    {
        var sb = new StringBuilder();
        var (starts, length) = board.Timing();
        sb.AppendLine($"Video \"{board.Title}\" in {board.Folder}: {board.Width}x{board.Height} at {board.Fps} fps, {N(length)} s in {board.Shots.Count} shot(s).");
        sb.AppendLine("Frames:");
        if (board.Frames.Count == 0) sb.AppendLine("  (none)");
        var todo = new List<string>();
        // What is to be made again: frames out of date, frames made from those, and shots that start or end on them.
        var stale = board.Frames.Where(f => !VideoMaker.IsCurrent(board, f)).Select(f => f.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        while (board.Frames.FirstOrDefault(f => !stale.Contains(f.Id) && f.File is not { Length: > 0 } && f.From.Any(stale.Contains)) is { } after)
            stale.Add(after.Id);
        foreach (var f in board.Frames)
        {
            var made = File.Exists(board.FramePicture(f));
            var current = !stale.Contains(f.Id);
            var state = !made ? "not made" : current ? "made" : VideoMaker.IsCurrent(board, f) ? $"made, but to be made again after {string.Join(", ", f.From.Where(stale.Contains))}" : "made, but out of date (changed since)";
            var source = f.File is { Length: > 0 } file ? $"the user's picture {file}" : $"\"{f.Prompt.Trim().ReplaceLineEndings(" ").Clip(300)}\" seed {f.Seed}" +
                                                                                      (f.From.Count > 0 ? $", made from {string.Join(", ", f.From)}" : "") +
                                                                                      (f.Model is { } fm ? $", made with \"{fm}\"" : "");
            var used = board.Shots.Where(s => s.First == f.Id || s.Last == f.Id).Select(s => s.Id).ToList();
            sb.AppendLine($"  {f.Id}{(f.Name is { Length: > 0 } n ? $" \"{n}\"" : "")}{(f.Reference ? " (a reference)" : used.Count > 0 ? $" (in {string.Join(", ", used)})" : " (not in a shot)")}: {source}. {state}" +
                          (made ? $": {board.FramePicture(f)}" : ""));
            if (!current)
                todo.Add($"frame {f.Id}");
        }
        // The user's own pictures in the References folder, which no reference of the storyboard is for.
        var references = board.Frames.Where(f => f.Reference).Select(board.FramePicture).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var own = board.ReferenceFiles().Where(f => !references.Contains(f)).Select(board.InReferences).ToList();
        sb.AppendLine($"References folder: {board.ReferencesFolder}" + (own.Count == 0 ? "." : $"; the user's pictures there to make frames from, by name in from: {string.Join(", ", own.Take(40))}" +
                                                                                       (own.Count > 40 ? $" and {own.Count - 40} more." : ".")));
        sb.AppendLine("Shots:");
        if (board.Shots.Count == 0) sb.AppendLine("  (none)");
        for (var i = 0; i < board.Shots.Count; i++)
        {
            var s = board.Shots[i];
            var join = i == 0 ? "" : s.First is not null && s.First == board.Shots[i - 1].Last ? $", goes on from {board.Shots[i - 1].Id}" : $", a {s.Transition} from {board.Shots[i - 1].Id}";
            if (i > 0 && s.Transition != VideoShot.Cut && !join.Contains(s.Transition, StringComparison.Ordinal)) join += $" ({s.Transition})";
            var frames = $"{s.First ?? "no first frame"} → {(s.LipSync ? "(lip-synced, first frame only)" : s.Last ?? "no last frame")}";
            var made = File.Exists(board.ShotVideo(s));
            var staleFrames = new[] { s.First, s.LipSync ? null : s.Last }.OfType<string>().Where(stale.Contains).Distinct().ToList();
            var current = VideoMaker.IsCurrent(board, s) && staleFrames.Count == 0;
            sb.AppendLine($"  {s.Id}, clip {i + 1} ({N(starts[i])}–{N(starts[i] + s.Seconds)} s, {N(s.Seconds)} s{join}): {frames}. \"{s.Prompt.Trim().ReplaceLineEndings(" ").Clip(300)}\"" +
                          (s.Camera.Length > 0 ? $" Camera: {s.Camera}." : "") +
                          (s.Model is { } sm ? $" Made with \"{sm}\"." : "") +
                          (Storyboard.Speaks(s) ? $" Says: \"{s.Line.Trim().ReplaceLineEndings(" ")}\"" + (VideoMaker.LineIsCurrent(board, s) ? " (said)." : " (not said yet).") : "") +
                          $" Seed {s.Seed}. " +
                          (!made ? "Not made." : current ? $"Made: {board.ShotVideo(s)}" :
                           VideoMaker.IsCurrent(board, s) ? $"Made, but to be made again after frame {string.Join(", ", staleFrames)}: {board.ShotVideo(s)}" : $"Made, but out of date: {board.ShotVideo(s)}"));
            if (!current)
                todo.Add($"shot {s.Id}");
        }
        sb.AppendLine($"Sounds ({board.SoundLines} sound line{(board.SoundLines == 1 ? "" : "s")}, over each shot's own sound):" + (board.Sounds.Count == 0 ? " none." : ""));
        foreach (var a in board.Sounds)
        {
            var file = board.SoundFile(a);
            var where = $"soundLine {a.SoundLine}, from {N(a.Start)} s" + (a.Trim > 0 ? $", its first {N(a.Trim)} s cut" : "") + (a.Seconds > 0 ? $", playing {N(a.Seconds)} s" : "") +
                        (Math.Abs(a.Volume - 1) > 0.001 ? $", volume {N(a.Volume)}" : "");
            if (a.Source == VideoSound.FromFile)
                sb.AppendLine($"  {a.Id} \"{a.Label}\" ({where}): the user's sound file " + (File.Exists(file) ? file : $"{file}, which isn't there."));
            else
            {
                var current = VideoMaker.IsCurrent(board, a);
                sb.AppendLine($"  {a.Id} \"{a.Label}\" ({where}): a song, \"{a.Tags.Trim()}\", " + (a.Lyrics.Trim().Length > 0 ? $"lyrics: \"{a.Lyrics.Trim().ReplaceLineEndings(" / ").Clip(400)}\"" : "instrumental") +
                              (a.Model is { } model ? $", made with \"{model}\"" : "") + $", seed {a.Seed}. " + (!File.Exists(file) ? "Not made." : current ? $"Made: {file}" : $"Made, but out of date (the video's length or the song changed): {file}"));
                if (!current)
                    todo.Add($"sound {a.Id} (action=sound)");
            }
        }
        if (board.Shots.Any(Storyboard.Speaks))
            sb.AppendLine("The lines' voice: " + (board.Voice.Trim().Length > 0 ? board.Voice.Trim() : "not described (voice): the voice model picks one") + ".");
        var video = board.JoinedFile;
        var newest = board.Shots.Select(s => board.ShotVideo(s)).Concat(board.Sounds.Select(board.SoundFile)).Where(p => p is not null && File.Exists(p))
            .Select(p => File.GetLastWriteTimeUtc(p!)).DefaultIfEmpty(DateTime.MinValue).Max();
        var joined = File.Exists(video);
        sb.AppendLine("Video: " + (!joined ? "not joined yet." : File.GetLastWriteTimeUtc(video) >= newest ? video : $"{video}, joined before the latest changes."));
        if (todo.Count > 0)
            sb.Append("To make: " + string.Join(", ", todo) + (todo.Count > 0 ? ", then join." : "."));
        else if (!joined || File.GetLastWriteTimeUtc(video) < newest)
            sb.Append("Everything is made: join it (action=join), then check the video.");
        else
            sb.Append("Everything is made and joined.");
        return sb.ToString();
    }

    /// <summary>What is installed for each kind of job, to name in a frame's, shot's or the song's model.</summary>
    private async Task<ToolResult> ModelsAsync(ToolContext ctx, CancellationToken ct)
    {
        Maker(ctx); // ComfyUI started here is closed with the task
        var hub = ctx.Comfy!;
        var notes = new List<string>();
        var api = await hub.ConnectAsync(null, notes, ctx.Progress, ct).ConfigureAwait(false);
        var sb = new StringBuilder();
        sb.AppendLine("Installed in ComfyUI, for a frame's, shot's or the song's model (name one in words; none picks the first of each):");
        foreach (var job in Enum.GetValues<VideoJob>())
        {
            ctx.Progress?.Invoke($"Looking at what makes {VideoTemplates.Describe(job)}…");
            var options = await VideoTemplates.OptionsAsync(hub, api, job, ct).ConfigureAwait(false);
            sb.AppendLine($"{VideoTemplates.Describe(job)}:" + (options.Count == 0 ? " nothing installed." : ""));
            foreach (var o in options.Take(40))
                sb.AppendLine("  " + o.Label);
            if (options.Count > 40)
                sb.AppendLine($"  …and {options.Count - 40} more.");
        }
        foreach (var note in notes)
            sb.AppendLine("Note: " + note);
        return new ToolResult(sb.ToString().TrimEnd());
    }

    // ---- making ----------------------------------------------------------------------------------------

    private async Task<ToolResult> FrameAsync(Storyboard board, string id, ToolContext ctx, CancellationToken ct)
    {
        var frame = board.Frame(id) ?? throw new VideoException($"There is no frame \"{id}\" in this video; its frames are {string.Join(", ", board.Frames.Select(f => f.Id))}.");
        var made = await KeepingVersionAsync(board, VideoPiece.Of(frame), () => Maker(ctx).MakeFrameAsync(board, frame, ctx.Progress, ctx.UserMessage, ct)).ConfigureAwait(false);
        var preview = MediaPreview.ForFiles([made.File], ctx.Media, ctx.SeesImages);
        var sb = new StringBuilder();
        sb.AppendLine($"Frame {frame.Id} made in {Chats.ChatTimes.Stopwatch(made.Took)}" + (made.Template is { } t ? $" with {t}" : frame.Of is { } of ? $" from where {of}'s clip ends" : " from the user's picture") + $": {made.File}");
        if (made.Template is not null) sb.AppendLine($"Its ComfyUI workflow (fixed seed {frame.Seed}): {Storyboard.WorkflowOf(made.File)}");
        foreach (var note in made.Notes.Where(n => !n.Contains("no place for the size", StringComparison.Ordinal)))
            sb.AppendLine("Note: " + note);
        // A frame made from its prompt alone draws its people anew; in a video with a character sheet that drifts.
        var references = board.Frames.Where(f => f.Reference && f != frame).ToList();
        var pictures = board.ReferenceFiles().Where(p => references.All(r => !string.Equals(board.FramePicture(r), p, StringComparison.OrdinalIgnoreCase))).Select(board.InReferences).ToList();
        var sheets = references.Select(f => f.Name is { Length: > 0 } n ? $"{f.Id} (\"{n}\")" : f.Id).Concat(pictures).ToList();
        if (frame.From.Count == 0 && frame.File is not { Length: > 0 } && frame.Of is null && !frame.Reference && sheets.Count > 0)
            sb.AppendLine($"It is made from its prompt alone, so whoever is in it is drawn anew; if it shows someone or somewhere of {string.Join(", ", sheets.Take(12))}, " +
                          $"make it again with from=[{string.Join(", ", references.Select(f => f.Id).Concat(pictures).Take(12))}] to keep them the same.");
        if (await LookAtFrameAsync(board, frame, ctx, ct).ConfigureAwait(false) is { } look)
            sb.Append(look);
        else if (frame.Of is null)
            sb.Append("Look at it: does it show what its prompt says, in the video's look, with the same people and places as the frames it is made from? " +
                      "If not, change the prompt or the seed and make it again.");
        return new ToolResult(sb.ToString().TrimEnd(), preview.Image, ImageCaption: preview.Image is null ? null : $"Frame {frame.Id}:", Files: [made.File]);
    }

    /// <summary>
    /// A fresh look at a frame (the model that wrote its prompt judges it too kindly), kept in its
    /// workflow file for the Video generator panel to show, with what to fix; null when it couldn't be had.
    /// </summary>
    private static async Task<string?> LookAtFrameAsync(Storyboard board, VideoFrame frame, ToolContext ctx, CancellationToken ct)
    {
        if (ctx.Ask is not { } ask)
            return null;
        ctx.Progress?.Invoke($"Looking at {frame.Label}…");
        if (await VideoCheck.JudgeFrameAsync(board, frame, ask, ct).ConfigureAwait(false) is not { } answer)
            return null;
        RememberLook(board.FrameMeta(frame), answer, []);
        var text = $"A fresh look at it: {answer.Trim()}";
        if (VideoCheck.Verdict(answer) == "redo")
            text += $"\nTo fix: {VideoCheck.Reason(answer)}. Change its prompt, what it is made from or its seed, and make it again." +
                    (VideoCheck.FramesAdvice(board, frame) is { } advice ? " " + advice : "");
        return text;
    }

    /// <summary>
    /// What a fresh look found, kept in the piece's workflow file: the Video generator panel shows it with
    /// the piece, and it goes when the piece is made again.
    /// </summary>
    private static void RememberLook(string? workflow, string answer, IReadOnlyList<string> faults)
    {
        if (workflow is null || VideoCheck.Verdict(answer) is not { } verdict)
            return;
        var said = verdict == "redo" ? $"It needs another go: {VideoCheck.Reason(answer)}." : faults.Count > 0 ? $"It looks right, though {string.Join("; ", faults)}." : "It looks right.";
        VideoVersions.RememberLook(workflow, verdict, said);
    }

    /// <summary>
    /// Makes a piece again with the one there kept as a version first, so the user can put it back; a
    /// make that fails or is stopped leaves the piece as it was, and takes that version back.
    /// </summary>
    private static async Task<T> KeepingVersionAsync<T>(Storyboard board, VideoPiece piece, Func<Task<T>> make)
    {
        var kept = VideoVersions.Keep(board, piece);
        try
        {
            return await make().ConfigureAwait(false);
        }
        catch when (kept is not null)
        {
            VideoVersions.Restore(board, piece, kept);
            throw;
        }
    }

    /// <summary>
    /// Saves what a long job settled (a seed) onto the storyboard as it is on the disk now: saved
    /// whole, the board read at the call's start wrote over what the user changed in the Video generator
    /// window during minutes of tries, and the window then showed the tool's version.
    /// </summary>
    private static void SaveSettled(Storyboard board, Action<Storyboard> settle)
    {
        Storyboard fresh;
        try { fresh = Storyboard.Load(board.Folder); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or VideoException or System.Text.Json.JsonException) { fresh = board; }
        settle(fresh);
        fresh.Save();
    }

    private async Task<ToolResult> SoundAsync(Storyboard board, string? id, int tries, ToolContext ctx, CancellationToken ct)
    {
        var songs = board.Sounds.Where(s => s.Source == VideoSound.Song).ToList();
        var sound = id is { Length: > 0 } ? board.Sound(id.Trim()) : songs.Count == 1 ? songs[0] : null;
        if (sound is null)
            throw new VideoException(songs.Count == 0
                ? "The storyboard has no song to make: add one to sounds [{\"id\": \"a1\", \"source\": \"song\", \"tags\": …, \"lyrics\": …, \"start\": 0}] first."
                : $"Which sound? Give its id: {string.Join(", ", board.Sounds.Select(s => s.Id))}.");
        if (sound.Source == VideoSound.FromFile)
            return new ToolResult(File.Exists(board.SoundFile(sound)) ? $"Sound {sound.Id} is the user's file {board.SoundFile(sound)}; nothing to make." : $"Sound {sound.Id} is a file of the user's, and it isn't there.");
        var songSeed = sound.Seed;
        var made = await KeepingVersionAsync(board, VideoPiece.Of(sound), () => Maker(ctx).MakeSongAsync(board, sound, ctx.Progress, ctx.UserMessage, ct, tries)).ConfigureAwait(false);
        // The seed of the song kept; not over a seed the Video generator panel gave meanwhile.
        SaveSettled(board, fresh =>
        {
            if (fresh.Sound(sound.Id) is { } kept && kept.Seed == songSeed) kept.Seed = sound.Seed;
        });
        var preview = MediaPreview.ForFiles([made.File], ctx.Media, ctx.SeesImages);
        var sb = new StringBuilder();
        sb.AppendLine($"The song is made in {Chats.ChatTimes.Stopwatch(made.Took)} with {made.Template}: {made.File} (a minute long at least: shorter songs come out badly; the video uses its start).");
        sb.AppendLine(string.Join("\n", preview.Lines));
        if (made.Score is { } score)
            sb.AppendLine($"How it sounds to SongEval, a judge trained on musicians' ratings, for the part the video uses: {score.Describe()}." +
                          (score.Musicality < SongScore.Good ? " Make it again (more tries, another style or simpler lyrics) before the shots that use it." : "") +
                          (sound.Lyrics.Trim().Length == 0 ? " SongEval is trained on songs with singing: an instrumental scored 2.5 to 3.3 in every style tried, so here it is a rough guide." : ""));
        if (sound.Lyrics.Trim().Length > 0 && await HearAsync(ctx, made.File, ct).ConfigureAwait(false) is { } heard)
            sb.AppendLine(heard.Words is null ? $"{heard.Model} heard no words in it." : $"Its words, as {heard.Model} heard them: \"{heard.Words.ReplaceLineEndings(" ").Clip(1500)}\" Check them against the lyrics.");
        foreach (var note in made.Notes)
            sb.AppendLine("Note: " + note);
        return new ToolResult(sb.ToString().TrimEnd(), preview.Image, ImageCaption: preview.Image is null ? null : "The song:", Files: [made.File]);
    }

    private async Task<ToolResult> ShotAsync(Storyboard board, string id, int tries, ToolContext ctx, CancellationToken ct)
    {
        var shot = board.Shot(id) ?? throw new VideoException($"There is no shot \"{id}\" in this video; its shots are {string.Join(", ", board.Shots.Select(s => s.Id))}.");
        // A shot starts on a picture a picture model makes (the video model draws one badly): one
        // without a first frame gets one from its words, and frames not made yet are made first.
        var first = new List<string>();
        if (board.Frame(shot.First) is null && board.AddFirstFrames() is { Count: > 0 } added)
        {
            board.Save();
            first.Add($"It had no first frame, so it got {string.Join(", ", added.Select(f => f.Id))} from its prompt: look at it, and make it again from the references if someone in it should look the same.");
        }
        foreach (var frame in new[] { shot.First, shot.LipSync ? null : shot.Last }.Distinct().Select(board.Frame).OfType<VideoFrame>())
            if (!VideoMaker.IsCurrent(board, frame))
            {
                var picture = await KeepingVersionAsync(board, VideoPiece.Of(frame), () => Maker(ctx).MakeFrameAsync(board, frame, ctx.Progress, ctx.UserMessage, ct)).ConfigureAwait(false);
                first.Add($"Made {frame.Label} first: {picture.File}." +
                          (await LookAtFrameAsync(board, frame, ctx, ct).ConfigureAwait(false) is { } seen && seen.Contains("To fix:", StringComparison.Ordinal) ? " " + seen.ReplaceLineEndings(" ") : ""));
            }
        var piece = VideoPiece.Of(shot);
        if (tries <= 1)
        {
            var made = await KeepingVersionAsync(board, piece, () => Maker(ctx).MakeShotAsync(board, shot, ctx.Progress, ctx.UserMessage, ct)).ConfigureAwait(false);
            var look = await LookAtShotAsync(board, shot, made.File, ctx, ct).ConfigureAwait(false);
            RememberLook(made.Meta, look.Text, look.Faults);
            return Result(board, shot, $"Shot {shot.Id} made in {Chats.ChatTimes.Stopwatch(made.Took)} with {made.Template}: {made.File}\nIts ComfyUI workflow (fixed seed {shot.Seed}): {Storyboard.WorkflowOf(made.File)}", [.. first, .. made.Notes], look, made.File);
        }

        // Tries with new seeds until a check passes; the best one is kept as the shot's clip, and
        // the others (and the clip there was before) as versions of the shot, to put back in the window.
        var attempts = new List<(long Seed, string File, VideoLook Look, string? Verdict, double Score)>();
        var notes = first;
        var before = VideoVersions.Keep(board, piece);
        (long Seed, string File, VideoLook Look, string? Verdict, double Score) best;
        var replaced = false;
        var startSeed = shot.Seed;
        try
        {
            for (var attempt = 1; attempt <= tries; attempt++)
            {
                if (attempt > 1)
                    shot.Seed = Storyboard.NewSeed();
                ctx.Progress?.Invoke($"Try {attempt} of {tries}…");
                var file = Path.Combine(board.ShotFolder(shot), $"{Path.GetFileNameWithoutExtension(board.ShotVideo(shot))}.try{attempt}.mp4");
                var made = await Maker(ctx).MakeShotAsync(board, shot, s => ctx.Progress?.Invoke($"Try {attempt} of {tries}: {s}"), ctx.UserMessage, ct, tryFile: file).ConfigureAwait(false);
                notes.AddRange(made.Notes.Where(n => !notes.Contains(n)));
                var look = await LookAtShotAsync(board, shot, file, ctx, ct).ConfigureAwait(false);
                RememberLook(made.Meta, look.Text, look.Faults);
                var verdict = VideoCheck.Verdict(look.Text);
                var score = (verdict == "good" ? 10 : verdict is null ? 5 : 0) - 3 * look.Faults.Count;
                attempts.Add((shot.Seed, file, look, verdict, score));
                if (verdict == "good" && look.Faults.Count == 0 || ctx.UserMessage.IsCancellationRequested)
                    break;
            }
            best = attempts.MaxBy(t => t.Score);
            shot.Seed = best.Seed;
            SaveSettled(board, fresh =>
            {
                // Not over a seed the Video generator panel gave meanwhile: the version it
                // asked for was made again with the best one's, the same clip once more.
                if (fresh.Shot(shot.Id) is { } kept && kept.Seed == startSeed)
                    kept.Seed = best.Seed;
            });
            VideoVersions.Promote(best.File, board.ShotVideo(shot));
            replaced = true;
        }
        finally
        {
            // Stopped half way, the tries made so far are kept too; the shot's clip is still the one
            // there was, so its copy as a version goes (kept, every call again added one more).
            if (!replaced && before is not null)
                VideoVersions.Drop(before);
            foreach (var other in attempts.Where(t => File.Exists(t.File)))
                VideoVersions.Add(board, piece, other.File);
        }
        var head = $"Shot {shot.Id}: {attempts.Count} tr{(attempts.Count == 1 ? "y" : "ies")}; " +
                   (best.Verdict == "good" && best.Look.Faults.Count == 0 ? $"try {attempts.IndexOf(best) + 1} passed" : $"none passed, try {attempts.IndexOf(best) + 1} was the best") +
                   $" and is the shot's clip now: {board.ShotVideo(shot)} (seed {best.Seed}, saved in the storyboard)." +
                   (attempts.Count > 1 ? " The others are kept as versions of the shot, which the user can put back in the Video generator panel: " +
                                      string.Join("; ", attempts.Where(t => t != best).Select(t => $"try {attempts.IndexOf(t) + 1} {t.Verdict ?? "no verdict"}" +
                                                                                                (t.Look.Faults.Count > 0 ? $" ({string.Join(", ", t.Look.Faults)})" : ""))) + "." : "");
        return Result(board, shot, head, notes, best.Look, board.ShotVideo(shot));
    }

    private static ToolResult Result(Storyboard board, VideoShot shot, string head, IReadOnlyList<string> notes, VideoLook look, string clip)
    {
        var sb = new StringBuilder();
        sb.AppendLine(head);
        foreach (var note in notes)
            sb.AppendLine("Note: " + note);
        sb.AppendLine(look.Text);
        if (look.Faults.Count > 0)
            sb.AppendLine("To fix: " + string.Join("; ", look.Faults) + ".");
        if (VideoCheck.Verdict(look.Text) == "redo" && VideoCheck.FramesAdvice(board, shot) is { } frames)
            sb.AppendLine(frames);
        sb.Append(FixAdvice);
        return new ToolResult(sb.ToString(), look.Sheet, ImageCaption: look.Sheet is null ? null : "The storyboard's frames (top) and the clip's frames with their times:", Files: [clip]);
    }

    private const string FixAdvice =
        "If the clip doesn't show what the shot should (the check above, and your own look), fix it and make it again: a plainer prompt (one action, the people and place named " +
        "as in the frames; which way things move on screen, from where to where, and what stays still), another seed, a shorter shot, frames closer to each other, " +
        "or a new frame. comfyui action=guide search=\"video\" has more on what makes a video model move things right. Move on when it is right.";

    // ---- checking ----------------------------------------------------------------------------------------

    private async Task<ToolResult> CheckAsync(Storyboard board, string? id, ToolContext ctx, CancellationToken ct)
    {
        if (id is { Length: > 0 } && board.Shot(id) is null && board.Frame(id) is { } frame)
            return await CheckFrameAsync(board, frame, ctx, ct).ConfigureAwait(false);
        if (ctx.Media is not { } decoder)
            throw new VideoException("Nothing on this PC can read video to check it (ffmpeg in BlackBox's Data\\tools would).");
        if (id is { Length: > 0 })
        {
            var shot = board.Shot(id) ?? throw new VideoException($"There is no frame or shot \"{id}\" in this video.");
            var clip = board.ShotVideo(shot);
            if (!File.Exists(clip))
                throw new VideoException($"Shot {shot.Id} has no clip yet: make it with action=shot.");
            var look = await LookAtShotAsync(board, shot, clip, ctx, ct).ConfigureAwait(false);
            RememberLook(board.ShotMeta(shot), look.Text, look.Faults);
            return Result(board, shot, $"Shot {shot.Id}: {clip}" + (VideoMaker.IsCurrent(board, shot) ? "" : " (made before the storyboard's latest changes to it)"), [], look, clip);
        }
        var video = board.JoinedFile;
        if (!File.Exists(video))
            throw new VideoException("The video isn't joined yet: action=join first, or give a shot's id to check that shot.");
        var check = await Task.Run(() => VideoCheck.Whole(board, video, decoder), ct).ConfigureAwait(false);
        var text = new StringBuilder(check.Text).AppendLine();
        // Its sound as people hear it: no one has to listen to every try.
        if (board.Sounds.Count > 0 && ctx.Comfy is { } hub && SongJudge.PythonOf(hub) is { } python && decoder.Ffmpeg is { } ffmpeg && Maker(ctx).Judge is { } judge)
        {
            try
            {
                if ((await judge.ScoreAsync([video], python, ffmpeg, 0, ctx.Progress, ct).ConfigureAwait(false)).Values.FirstOrDefault() is { } heard)
                    text.AppendLine($"Its sound, to SongEval: {heard.Describe()}." + (heard.Musicality < SongScore.Poor ? " Its sounds or the shots' own sound under them spoil it." : ""));
            }
            catch (VideoException ex)
            {
                text.AppendLine("Its sound couldn't be judged: " + ex.Message);
            }
        }
        if (ctx.Ask is { } ask && await VideoCheck.JudgeVideoAsync(board, check, ask, ct).ConfigureAwait(false) is { } judged)
            text.AppendLine("Fresh looks at the shots side by side and at each cut: " + judged.Answer.Trim());
        if (check.Faults.Count > 0)
            text.AppendLine("To fix: " + string.Join("; ", check.Faults) + ".");
        text.Append("Fix what a viewer would notice (a frame first when a person or place differs, then its shots), then join again. " +
                    "Each round costs minutes per shot: after two rounds, finish unless something is still clearly wrong, and tell the user what is left.");
        return new ToolResult(text.ToString(), check.Sheet, ImageCaption: check.Sheet is null ? null : "One frame from the middle of each shot, in order:", Files: [video]);
    }

    /// <summary>A fresh look at a frame as it is: action=check with a frame's id.</summary>
    private static async Task<ToolResult> CheckFrameAsync(Storyboard board, VideoFrame frame, ToolContext ctx, CancellationToken ct)
    {
        var picture = board.FramePicture(frame);
        if (!File.Exists(picture))
            throw new VideoException($"{frame.Label} isn't made yet: make it with action=frame.");
        if (frame.Of is { } of)
            return new ToolResult($"{frame.Label} is where {of}'s clip ends, taken from it: check shot {of} instead.");
        var preview = MediaPreview.ForFiles([picture], ctx.Media, ctx.SeesImages);
        var look = await LookAtFrameAsync(board, frame, ctx, ct).ConfigureAwait(false)
                   ?? "It couldn't be looked at: the model doesn't see pictures here. Look at it yourself.";
        return new ToolResult($"Frame {frame.Id}: {picture}" + (VideoMaker.IsCurrent(board, frame) ? "" : " (made before the storyboard's latest changes to it)") + "\n" + look,
            preview.Image, ImageCaption: preview.Image is null ? null : $"Frame {frame.Id}:", Files: [picture]);
    }

    /// <summary>The numbers and the fresh look at a shot's clip, and for a lip-synced one what is sung under it.</summary>
    private async Task<VideoLook> LookAtShotAsync(Storyboard board, VideoShot shot, string clip, ToolContext ctx, CancellationToken ct)
    {
        if (ctx.Media is not { } decoder)
            return new VideoLook("Nothing on this PC can read video to check it.", null, []);
        ctx.Progress?.Invoke($"Looking at shot {shot.Id}…");
        var look = await Task.Run(() => VideoCheck.Shot(board, shot, clip, decoder), ct).ConfigureAwait(false);
        var text = new StringBuilder(look.Text).AppendLine();
        string? sung = null;
        if (shot.LipSync && File.Exists(board.ShotAudio(shot)) && await HearAsync(ctx, board.ShotAudio(shot), ct).ConfigureAwait(false) is { } heard)
        {
            sung = heard.Words is null ? "No words are said in this shot's line." : $"The words said in this shot's line: \"{heard.Words.ReplaceLineEndings(" ").Clip(600)}\".";
            text.AppendLine(sung + $" ({heard.Model} listened to it.)");
        }
        if (ctx.Ask is { } ask)
        {
            ctx.Progress?.Invoke($"Two fresh looks at shot {shot.Id}: physics and bodies, then behaviour over time…");
            if (await VideoCheck.JudgeShotAsync(board, shot, look, sung, ask, ct).ConfigureAwait(false) is { Length: > 0 } judged)
                text.AppendLine("Fresh looks at its frames and at a closer look at its busiest moment: " + judged.Trim());
        }
        return look with { Text = text.ToString().TrimEnd() };
    }

    /// <summary>What is sung or said in a sound, as a small Gemma 4 that ComfyUI has hears it; null when none can.</summary>
    private async Task<(string Model, string? Words)?> HearAsync(ToolContext ctx, string file, CancellationToken ct)
    {
        if (ctx.Comfy is not { } hub)
            return null;
        try
        {
            Maker(ctx);
            var api = await hub.ConnectAsync(null, [], ctx.Progress, ct).ConfigureAwait(false);
            var library = await hub.LibraryAsync(api, ct).ConfigureAwait(false);
            if (ComfyTool.HearingModel(library) is not { } model)
                return null;
            ctx.Progress?.Invoke($"Listening to {Path.GetFileName(file)}…");
            var workflow = ComfyTool.HearingWorkflow(library, model, await api.UploadAsync(file, ct).ConfigureAwait(false));
            workflow.FillAdvancedDefaults(library);
            // The file just uploaded isn't among LoadAudio's choices the library knows yet.
            if (workflow.Validate(library, new HashSet<string> { "1.audio" }).Count > 0)
                return null;
            var clientId = "blackbox-" + Guid.NewGuid().ToString("N")[..12];
            var (promptId, _) = await ComfyRunner.QueueAsync(api, workflow, clientId, ct).ConfigureAwait(false);
            if (promptId is null)
                return null;
            var job = await ComfyRunner.WaitAsync(api, promptId, clientId, new Dictionary<string, string>(), TimeSpan.FromMinutes(3), null, ctx.UserMessage, ct).ConfigureAwait(false);
            if (job.Status != "success")
                return null;
            var words = job.Texts.FirstOrDefault(t => t.Trim().Length > 0)?.Trim();
            return (model, words is null || words.Trim('(', ')', '.', ' ').Equals("no words", StringComparison.OrdinalIgnoreCase) ? null : words);
        }
        catch (Exception ex) when (ex is ComfyApiException or ComfyWorkflowException or IOException)
        {
            return null;
        }
    }

    private async Task<ToolResult> JoinAsync(Storyboard board, ToolContext ctx, CancellationToken ct)
    {
        var stale = board.Shots.Where(s => File.Exists(board.ShotVideo(s)) && !VideoMaker.IsCurrent(board, s)).Select(s => s.Id).ToList();
        var made = await VideoMontage.JoinAsync(board, ctx.Media?.Ffmpeg, ctx.Media, ctx.Progress, ct).ConfigureAwait(false);
        var preview = MediaPreview.ForFiles([made.File], ctx.Media, ctx.SeesImages);
        var sb = new StringBuilder();
        sb.AppendLine($"Joined the video in {Chats.ChatTimes.Stopwatch(made.Took)}: {made.File}");
        sb.AppendLine(string.Join("\n", preview.Lines));
        sb.AppendLine($"It fades in from black over its first {N(VideoMontage.FadeIn)} s and out over its last {N(VideoMontage.FadeOut)} s on purpose, so a dark first or last frame is that, not a fault.");
        foreach (var note in made.Notes)
            sb.AppendLine("Note: " + note);
        if (stale.Count > 0)
            sb.AppendLine($"Shots {string.Join(", ", stale)} were made before the storyboard's latest changes to them; make them again if those changes matter.");
        sb.Append("Check the whole video now (action=check without id).");
        return new ToolResult(sb.ToString(), preview.Image, ImageCaption: preview.Image is null ? null : "The video:", Files: [made.File]);
    }


    private static string N(double d) => d.ToString("0.##", CultureInfo.InvariantCulture);
}
