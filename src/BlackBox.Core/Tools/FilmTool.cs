using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using BlackBox.Core.Comfy;
using BlackBox.Core.Film;
using BlackBox.Core.Media;

namespace BlackBox.Core.Tools;

/// <summary>
/// Films from a storyboard (see <see cref="Storyboard"/>): the same film folder the user sees in
/// the Filmmaking panel. The agent plans or changes it, makes its frames, song and shots with
/// ComfyUI (each with a fixed-seed workflow of its own), checks every shot against what it should
/// show and fixes the ones that don't, joins them and checks the film. A check is numbers (does the
/// clip start and end on its frames, does it move, does it jump) and a fresh look by the model at
/// a sheet of its frames: the model that made a clip judged its own too kindly (four bunnies it
/// called mature and true to the sheet, which the user saw as a chubby teenager).
/// </summary>
public sealed class FilmTool : Tool
{
    public override string Name => "film";

    public override string Description =>
        "Make a film (a music video, a short story, an ad: anything longer than one shot) from a storyboard, with ComfyUI on this PC. " +
        "The film is its folder, which the user also sees in BlackBox's Filmmaking panel beside the chat, from where they pick its pictures, clips and folders into their messages to you: " +
        "a folder for each shot, numbered in the film's order and named after the shot, holding the frames it starts and ends on and its clip; References; Sound; and the joined film. " +
        "Each file has a .meta beside it (what it is and how it was made, as JSON) and the ComfyUI workflow that made it (.workflow.json). " +
        "The storyboard is all of it at once: shots in order, each going from a first frame to an optional last frame in so many seconds, the pictures they start and end on, and the soundtrack. " +
        "action=storyboard shows it (path: the film's folder), with BlackBox's guide to writing one the first time in a chat, or makes or changes it with storyboard={...}: title, " +
        "width, height, fps, frames:[{id, prompt (what it shows and its look), model, seed, from: [what it is made from, to keep people and places the same: frames' ids, and pictures in the film's References folder by file name], " +
        "file (a picture of the user's, cut to the film's frame), name, reference: true for a character or place sheet, made into the film's References folder at its own shape and named after its name, " +
        "of: a shot's id, for a frame taken from where that shot's clip ends, so the shot that starts on it goes on from that moment (after a shot without a last frame)}], " +
        "shots:[{id, name (a few words naming it, \"intro\", \"the leap\": its folder's name), first, last (frame ids), seconds, prompt (what happens, and its look), camera, model, transition: cut|dissolve|fade, " +
        "line (what is said in it, lip-synced, in the voice sound.voice describes; it starts 0.4 s in, so leave time for it), lipSync (without a line: sings the soundtrack's part under it), seed}], " +
        "sound:{source: none|file|song, file, tags (the song's style), lyrics, voice (who says the lines: age, voice, manner), model, seed}. Frames and shots are matched by id; {\"id\": …, \"delete\": true} removes one, and a field set to null is taken out (a shot's \"last\": null). " +
        "Nothing is film-wide, so a change remakes only what it changes: write the film's look into every frame's and shot's prompt. " +
        "model is what one is made with: a model or workflow in words (\"z image turbo\", \"juggernaut xl\", \"my pony workflow\"), matched against what ComfyUI has installed, " +
        "or none for the best installed one; action=models lists what is installed for each kind of picture, video and song. " +
        "Without path a new film goes into a folder of its own in BlackBox's Data\\films, where the Filmmaking panel finds it " +
        "(BlackBox is portable: what it makes stays next to it). " +
        "A shot that starts on the last frame of the one before goes on with the same camera; " +
        "a new first frame is a cut to another scene or angle. Every shot starts on a frame: the video model animates a picture well and draws one badly. " +
        "Keep one clear action per shot, as many seconds as it takes (2 to 8; a longer clip holds its opening pose, then rushes): say which way things move on screen, from where to where, and what stays still, " +
        "and what is heard (the shot's own sound is kept under the song at half; music comes from the song, not the video model). " +
        "The References folder also holds any picture the user puts there (action=storyboard lists them): name one in a frame's from to make the frame from it, " +
        "whatever its shape: a frame made from pictures is made the film's size with all of each in view. " +
        "action=frame id= makes a frame's picture (a reference's too) and has a fresh look at it; action=sound makes the song, a minute long at least (the film uses its start), the best of tries=3 by a judge " +
        "of how good music sounds (SongEval); write lyrics for the part the film uses. action=shot id= makes a shot's clip and checks it (does it start and end on its frames, " +
        "how it moves, and a fresh look at what happens, faults and whether people stay the same); tries=N makes up to N takes with new seeds until one passes. " +
        "What is made again keeps the one before as a take (in Takes, in its folder), which the user can put back in the Filmmaking panel. " +
        "Fix a frame or shot the look finds wrong: a plainer prompt, another seed, frames closer to each other, or a new frame. " +
        "action=join puts the shots and the soundtrack together into the film; action=check looks at a frame or shot (id) or, without id, the whole film. " +
        "Order: frames (those others are made from first), the song when a shot is lip-synced, the shots, then join and check the film. " +
        "prompt, camera, seconds, seed, model and from given with action=frame or shot change the storyboard before it is made.";

    public override ToolRisk Risk => ToolRisk.Risky;

    public override JsonObject Parameters => Schema(
        ("action", Str("What to do", "storyboard", "frame", "shot", "sound", "join", "check", "models"), true),
        ("path", Str("The film's folder (or the name of a film in the films folder); a new film's folder for action=storyboard"), false),
        ("id", Str("A frame's id (action=frame, check) or a shot's (action=shot, check)"), false),
        ("storyboard", new JsonObject { ["type"] = "object", ["description"] = "action=storyboard: the storyboard, or the parts of it to change (frames and shots by id)" }, false),
        ("prompt", Str("action=frame or shot: a new prompt for it"), false),
        ("camera", Str("action=shot: what the camera does"), false),
        ("seconds", Num("action=shot: how long it lasts"), false),
        ("seed", Int("action=frame or shot: a new seed (another result from the same prompt)"), false),
        ("model", Str("action=frame or shot: what it is made with, a model or workflow in words or by name (\"\" for the best installed)"), false),
        ("from", StrArray("action=frame: what it is made from: frames' ids, pictures in the References folder by name"), false),
        ("tries", Int("action=shot: up to this many takes, each with a new seed, until the check passes (default 1); action=sound: songs to make, the best kept (default 3)"), false));

    public override string Describe(JsonObject args)
    {
        var action = GetString(args, "action") ?? "";
        var id = GetString(args, "id");
        return action switch
        {
            "storyboard" => args["storyboard"] is JsonObject ? "Change the storyboard" + (GetString(args, "path") is { } p ? $" in {p}" : "") : $"Storyboard of {GetString(args, "path") ?? "the film"}",
            "frame" => $"Make frame {id}",
            "shot" => $"Make shot {id}" + (GetNumber(args, "tries") is > 1 and var t ? $" ({t} tries at most)" : ""),
            "sound" => "Make the film's song",
            "join" => "Join the film",
            "check" => id is null ? "Check the film" : $"Check {id}",
            "models" => "List the installed models for films",
            _ => "film " + action,
        };
    }

    private FilmMaker? _maker;

    /// <summary>The film this task worked on last, for calls that leave path out.</summary>
    private string? _lastFolder;

    public override async Task<ToolResult> ExecuteAsync(JsonObject args, ToolContext ctx, CancellationToken ct)
    {
        var action = RequireString(args, "action").Trim().ToLowerInvariant();
        try
        {
            if (action is "models" or "workflows" or "templates")
                return await ModelsAsync(ctx, ct).ConfigureAwait(false);
            var board = LoadOrCreate(args, ctx, create: action == "storyboard");
            _lastFolder = board.Folder;
            if (action != "storyboard" && Changes(args, action) is { Count: > 0 } changes && Fits(board, args, action))
            {
                board = Storyboard.Merge(board, changes);
                board.Save();
            }
            return action switch
            {
                "storyboard" => Show(board, args["storyboard"] is JsonObject, GuideFor(ctx)),
                "frame" => await FrameAsync(board, RequireId(args), ctx, ct).ConfigureAwait(false),
                "shot" => await ShotAsync(board, RequireId(args), (int)Math.Clamp(GetNumber(args, "tries") ?? 1, 1, 8), ctx, ct).ConfigureAwait(false),
                "sound" or "song" => await SoundAsync(board, (int)Math.Clamp(GetNumber(args, "tries") ?? 3, 1, 8), ctx, ct).ConfigureAwait(false),
                "join" or "montage" => await JoinAsync(board, ctx, ct).ConfigureAwait(false),
                "check" => await CheckAsync(board, GetString(args, "id"), ctx, ct).ConfigureAwait(false),
                _ => throw new ToolArgumentException("action must be storyboard, frame, shot, sound, join, check or models."),
            };
        }
        catch (FilmStillRunningException ex)
        {
            return new ToolResult(ex.Message + (ctx.UserMessage.IsCancellationRequested ? " The user wrote to you meanwhile; read that first." : ""));
        }
        catch (Exception ex) when (ex is FilmException or ComfyApiException or ComfyWorkflowException or IOException or UnauthorizedAccessException)
        {
            return ToolResult.Error(ex.Message);
        }
    }

    private static string RequireId(JsonObject args) => GetString(args, "id")?.Trim() is { Length: > 0 } id ? id : throw new ToolArgumentException("Give the id of the frame or shot.");

    /// <summary>The storyboard the call is about: by path, the one used last in this task, or a new one for action=storyboard.</summary>
    private Storyboard LoadOrCreate(JsonObject args, ToolContext ctx, bool create)
    {
        var root = ctx.FilmsDirectory ?? Path.Combine(ctx.ScratchDirectory ?? Path.GetTempPath(), "films");
        var path = GetString(args, "path");
        var given = path is { Length: > 0 } ? ctx.ResolvePath(path) : _lastFolder;
        // A film's name alone ("Spaghetti Days"), or any path not from a drive, is its folder in
        // the films folder, new or not: from the working folder, a new "lighthouse" was made in the
        // user's home, out of BlackBox's Data, where the Filmmaking panel doesn't list it.
        if (path is { Length: > 0 } && !Path.IsPathRooted(path) && !Exists(given))
            given = Path.Combine(root, path);
        var change = args["storyboard"] as JsonObject;
        // A whole storyboard of another title, with no path, is a new film, not the last one used in
        // this task, once that one says what is in it: "two ads, coffee and tea" wrote the tea ad
        // over the coffee one. Whole: shots that each say what happens, none taken out. A new title
        // alone, or with changes to some pieces, renames and changes the film in use, and the
        // Filmmaking panel's new film (its empty s1, f1 and f2) is laid out in place.
        if (create && path is not { Length: > 0 } && Exists(given) && change?["title"] is JsonValue t && t.TryGetValue<string>(out var newTitle) &&
            newTitle.Trim().Length > 0 && change["shots"] is JsonArray { Count: > 0 } shots &&
            shots.All(s => s is JsonObject o && o["delete"] is null && o["prompt"] is JsonValue p && p.ToString().Trim().Length > 0) &&
            Storyboard.Load(given, upgrade: false) is { } last &&
            (last.Shots.Any(s => s.Prompt.Trim().Length > 0) || last.Frames.Any(f => f.Prompt.Trim().Length > 0)) &&
            !newTitle.Trim().Equals(last.Title.Trim(), StringComparison.OrdinalIgnoreCase))
            given = null;
        if (Exists(given))
        {
            var board = Storyboard.Load(given);
            if (change is not null)
            {
                board = Storyboard.Merge(board, change);
                board.Save();
            }
            return board;
        }
        if (!create || change is null)
        {
            var films = Storyboard.Recent(root);
            throw new FilmException((given is null
                ? "Which film? Give path: the film's folder, or make a new one with action=storyboard and storyboard={…}."
                : $"There is no film in {given}. Make one with action=storyboard and storyboard={{…}} (path is where it goes).") +
                (films.Count > 0 ? $" The films in {root}, the latest first: {string.Join("; ", films.Take(12))}." : ""));
        }
        var title = (string?)change["title"] ?? "Untitled film";
        var name = Storyboard.Safe(title);
        var folder = given ?? Path.Combine(root, name);
        // Another film of the same title is another film: it isn't written over. Its number goes
        // after a name cut short enough to keep it: cut at 80 characters with the number, a long
        // title gave the same folder for every number, and the tool went round for ever.
        for (var n = 2; given is null && Storyboard.IsFilm(folder); n++)
            folder = Path.Combine(root, $"{(name.Length > 72 ? name[..72].TrimEnd() : name)} {n}");
        if (given is not null && File.Exists(given))
            folder = Path.GetDirectoryName(given)!;
        Directory.CreateDirectory(folder);
        var created = Storyboard.Merge(new Storyboard { Title = title, Folder = folder }, change);
        created.Save();
        return created;

        static bool Exists([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] string? at) =>
            at is not null && (Storyboard.IsFilm(at) || File.Exists(at) && Storyboard.IsFilm(Path.GetDirectoryName(at)!));
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

    private FilmMaker Maker(ToolContext ctx)
    {
        if (ctx.Comfy is not { } hub)
            throw new FilmException("ComfyUI isn't available in this run.");
        if (_maker is null)
        {
            _maker = new FilmMaker(hub, ctx.Media) { Judge = ctx.ToolsDirectory is { } tools ? new SongJudge(Path.Combine(tools, "songeval")) : null };
            // From before it may be started: when the task is over, a ComfyUI BlackBox started is closed.
            _maker.Use();
            ctx.AfterRun(nameof(FilmTool), () =>
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
            // now, so the Filmmaking panel shows it, and it can be changed before it is made.
            var added = board.AddFirstFrames();
            if (added.Count > 0)
                board.Save();
            line = $"Saved the film in {board.Folder}: a folder for each shot, each piece's settings in a .meta beside it. The user sees it in the Filmmaking panel (Toolkit, at the top of BlackBox).\n" +
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
        sb.AppendLine($"Film \"{board.Title}\" in {board.Folder}: {board.Width}x{board.Height} at {board.Fps} fps, {N(length)} s in {board.Shots.Count} shot(s).");
        sb.AppendLine("Frames:");
        if (board.Frames.Count == 0) sb.AppendLine("  (none)");
        var todo = new List<string>();
        // What is to be made again: frames out of date, frames made from those, and shots that start or end on them.
        var stale = board.Frames.Where(f => !FilmMaker.IsCurrent(board, f)).Select(f => f.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        while (board.Frames.FirstOrDefault(f => !stale.Contains(f.Id) && f.File is not { Length: > 0 } && f.From.Any(stale.Contains)) is { } after)
            stale.Add(after.Id);
        foreach (var f in board.Frames)
        {
            var made = File.Exists(board.FramePicture(f));
            var current = !stale.Contains(f.Id);
            var state = !made ? "not made" : current ? "made" : FilmMaker.IsCurrent(board, f) ? $"made, but to be made again after {string.Join(", ", f.From.Where(stale.Contains))}" : "made, but out of date (changed since)";
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
        var own = board.ReferenceFiles().Where(f => !references.Contains(f)).Select(f => Path.GetRelativePath(board.ReferencesFolder, f)).ToList();
        sb.AppendLine($"References folder: {board.ReferencesFolder}" + (own.Count == 0 ? " (the references' pictures)." : $"; pictures there to make frames from, by name in from: {string.Join(", ", own.Take(40))}" +
                                                                                       (own.Count > 40 ? $" and {own.Count - 40} more." : ".")));
        sb.AppendLine("Shots:");
        if (board.Shots.Count == 0) sb.AppendLine("  (none)");
        for (var i = 0; i < board.Shots.Count; i++)
        {
            var s = board.Shots[i];
            var join = i == 0 ? "" : s.First is not null && s.First == board.Shots[i - 1].Last ? $", goes on from {board.Shots[i - 1].Id}" : $", a {s.Transition} from {board.Shots[i - 1].Id}";
            if (i > 0 && s.Transition != FilmShot.Cut && !join.Contains(s.Transition, StringComparison.Ordinal)) join += $" ({s.Transition})";
            var frames = $"{s.First ?? "no first frame"} → {(s.LipSync ? "(lip-synced, first frame only)" : s.Last ?? "no last frame")}";
            var made = File.Exists(board.ShotVideo(s));
            var staleFrames = new[] { s.First, s.LipSync ? null : s.Last }.OfType<string>().Where(stale.Contains).Distinct().ToList();
            var current = FilmMaker.IsCurrent(board, s) && staleFrames.Count == 0;
            sb.AppendLine($"  {s.Id} ({N(starts[i])}–{N(starts[i] + s.Seconds)} s, {N(s.Seconds)} s{join}): {frames}. \"{s.Prompt.Trim().ReplaceLineEndings(" ").Clip(300)}\"" +
                          (s.Camera.Length > 0 ? $" Camera: {s.Camera}." : "") +
                          (s.Model is { } sm ? $" Made with \"{sm}\"." : "") +
                          (Storyboard.Speaks(s) ? $" Says: \"{s.Line.Trim().ReplaceLineEndings(" ")}\"" + (FilmMaker.LineIsCurrent(board, s) ? " (said)." : " (not said yet).") :
                           s.LipSync ? " Sings the soundtrack's part under it (lipSync without a line)." : "") +
                          $" Seed {s.Seed}. " +
                          (!made ? "Not made." : current ? $"Made: {board.ShotVideo(s)}" :
                           FilmMaker.IsCurrent(board, s) ? $"Made, but to be made again after frame {string.Join(", ", staleFrames)}: {board.ShotVideo(s)}" : $"Made, but out of date: {board.ShotVideo(s)}"));
            if (!current)
                todo.Add($"shot {s.Id}");
        }
        var sound = board.Sound;
        switch (sound.Source)
        {
            case FilmSound.FromFile:
                sb.AppendLine($"Soundtrack: the file {sound.File}" + (sound.File is { Length: > 0 } f2 && File.Exists(f2) ? "." : ", which isn't there."));
                break;
            case FilmSound.Song:
                var songCurrent = FilmMaker.SongIsCurrent(board);
                sb.AppendLine($"Soundtrack: a song, \"{sound.Tags.Trim()}\", " + (sound.Lyrics.Trim().Length > 0 ? $"lyrics: \"{sound.Lyrics.Trim().ReplaceLineEndings(" / ").Clip(400)}\"" : "instrumental") +
                              (sound.Model is { } model ? $", made with \"{model}\"" : "") + $", seed {sound.Seed}. " + (!File.Exists(board.SongFile) ? "Not made." : songCurrent ? $"Made: {board.SongFile}" : $"Made, but out of date (the film's length or the song changed): {board.SongFile}"));
                if (!songCurrent)
                    todo.Insert(todo.FindIndex(t => t.StartsWith("shot", StringComparison.Ordinal)) is var at and >= 0 ? at : todo.Count, "the song (action=sound)");
                break;
            default:
                sb.AppendLine("Soundtrack: none (each shot's own sound).");
                break;
        }
        if (board.Shots.Any(Storyboard.Speaks))
            sb.AppendLine("The lines' voice: " + (sound.Voice.Trim().Length > 0 ? sound.Voice.Trim() : "not described (sound.voice): the voice model picks one") + ".");
        var film = board.JoinedFile;
        var newest = board.Shots.Select(s => board.ShotVideo(s)).Append(board.SoundtrackFile).Where(p => p is not null && File.Exists(p))
            .Select(p => File.GetLastWriteTimeUtc(p!)).DefaultIfEmpty(DateTime.MinValue).Max();
        var joined = File.Exists(film);
        sb.AppendLine("Film: " + (!joined ? "not joined yet." : File.GetLastWriteTimeUtc(film) >= newest ? film : $"{film}, joined before the latest changes."));
        if (todo.Count > 0)
            sb.Append("To make: " + string.Join(", ", todo) + (todo.Count > 0 ? ", then join." : "."));
        else if (!joined || File.GetLastWriteTimeUtc(film) < newest)
            sb.Append("Everything is made: join it (action=join), then check the film.");
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
        foreach (var job in Enum.GetValues<FilmJob>())
        {
            ctx.Progress?.Invoke($"Looking at what makes {FilmTemplates.Describe(job)}…");
            var options = await FilmTemplates.OptionsAsync(hub, api, job, ct).ConfigureAwait(false);
            sb.AppendLine($"{FilmTemplates.Describe(job)}:" + (options.Count == 0 ? " nothing installed." : ""));
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
        var frame = board.Frame(id) ?? throw new FilmException($"There is no frame \"{id}\" in this film; its frames are {string.Join(", ", board.Frames.Select(f => f.Id))}.");
        var made = await KeepingTakeAsync(board, FilmPiece.Of(frame), () => Maker(ctx).MakeFrameAsync(board, frame, ctx.Progress, ctx.UserMessage, ct)).ConfigureAwait(false);
        var preview = MediaPreview.ForFiles([made.File], ctx.Media, ctx.SeesImages);
        var sb = new StringBuilder();
        sb.AppendLine($"Frame {frame.Id} made in {Chats.ChatTimes.Stopwatch(made.Took)}" + (made.Template is { } t ? $" with {t}" : frame.Of is { } of ? $" from where {of}'s clip ends" : " from the user's picture") + $": {made.File}");
        if (made.Template is not null) sb.AppendLine($"Its ComfyUI workflow (fixed seed {frame.Seed}): {Storyboard.WorkflowOf(made.File)}");
        foreach (var note in made.Notes.Where(n => !n.Contains("no place for the size", StringComparison.Ordinal)))
            sb.AppendLine("Note: " + note);
        // A frame made from its prompt alone draws its people anew; in a film with a character sheet that drifts.
        var references = board.Frames.Where(f => f.Reference && f != frame).ToList();
        var pictures = board.ReferenceFiles().Where(p => references.All(r => !string.Equals(board.FramePicture(r), p, StringComparison.OrdinalIgnoreCase))).Select(Path.GetFileName).OfType<string>().ToList();
        var sheets = references.Select(f => f.Name is { Length: > 0 } n ? $"{f.Id} (\"{n}\")" : f.Id).Concat(pictures).ToList();
        if (frame.From.Count == 0 && frame.File is not { Length: > 0 } && frame.Of is null && !frame.Reference && sheets.Count > 0)
            sb.AppendLine($"It is made from its prompt alone, so whoever is in it is drawn anew; if it shows someone or somewhere of {string.Join(", ", sheets.Take(12))}, " +
                          $"make it again with from=[{string.Join(", ", references.Select(f => f.Id).Concat(pictures).Take(12))}] to keep them the same.");
        if (await LookAtFrameAsync(board, frame, ctx, ct).ConfigureAwait(false) is { } look)
            sb.Append(look);
        else if (frame.Of is null)
            sb.Append("Look at it: does it show what its prompt says, in the film's look, with the same people and places as the frames it is made from? " +
                      "If not, change the prompt or the seed and make it again.");
        return new ToolResult(sb.ToString().TrimEnd(), preview.Image, ImageCaption: preview.Image is null ? null : $"Frame {frame.Id}:", Files: [made.File]);
    }

    /// <summary>
    /// A fresh look at a frame (the model that wrote its prompt judges it too kindly), kept in its
    /// workflow file for the Filmmaking panel to show, with what to fix; null when it couldn't be had.
    /// </summary>
    private static async Task<string?> LookAtFrameAsync(Storyboard board, FilmFrame frame, ToolContext ctx, CancellationToken ct)
    {
        if (ctx.Ask is not { } ask)
            return null;
        ctx.Progress?.Invoke($"Looking at {frame.Label}…");
        if (await FilmCheck.JudgeFrameAsync(board, frame, ask, ct).ConfigureAwait(false) is not { } answer)
            return null;
        RememberLook(board.FrameMeta(frame), answer, []);
        var text = $"A fresh look at it: {answer.Trim()}";
        if (FilmCheck.Verdict(answer) == "redo")
            text += $"\nTo fix: {FilmCheck.Reason(answer)}. Change its prompt, what it is made from or its seed, and make it again." +
                    (FilmCheck.FramesAdvice(board, frame) is { } advice ? " " + advice : "");
        return text;
    }

    /// <summary>
    /// What a fresh look found, kept in the piece's workflow file: the Filmmaking panel shows it with
    /// the piece, and it goes when the piece is made again.
    /// </summary>
    private static void RememberLook(string? workflow, string answer, IReadOnlyList<string> faults)
    {
        if (workflow is null || FilmCheck.Verdict(answer) is not { } verdict)
            return;
        var said = verdict == "redo" ? $"It needs another go: {FilmCheck.Reason(answer)}." : faults.Count > 0 ? $"It looks right, though {string.Join("; ", faults)}." : "It looks right.";
        FilmTakes.RememberLook(workflow, verdict, said);
    }

    /// <summary>
    /// Makes a piece again with the one there kept as a take first, so the user can put it back; a
    /// make that fails or is stopped leaves the piece as it was, and takes that take back.
    /// </summary>
    private static async Task<T> KeepingTakeAsync<T>(Storyboard board, FilmPiece piece, Func<Task<T>> make)
    {
        var kept = FilmTakes.Keep(board, piece);
        try
        {
            return await make().ConfigureAwait(false);
        }
        catch when (kept is not null)
        {
            FilmTakes.Restore(board, piece, kept);
            throw;
        }
    }

    /// <summary>
    /// Saves what a long job settled (a seed) onto the storyboard as it is on the disk now: saved
    /// whole, the board read at the call's start wrote over what the user changed in the Filmmaking
    /// window during minutes of takes, and the window then showed the tool's version.
    /// </summary>
    private static void SaveSettled(Storyboard board, Action<Storyboard> settle)
    {
        Storyboard fresh;
        try { fresh = Storyboard.Load(board.Folder); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FilmException or System.Text.Json.JsonException) { fresh = board; }
        settle(fresh);
        fresh.Save();
    }

    private async Task<ToolResult> SoundAsync(Storyboard board, int tries, ToolContext ctx, CancellationToken ct)
    {
        if (board.Sound.Source == FilmSound.FromFile)
            return new ToolResult(board.SoundtrackFile is { } f && File.Exists(f) ? $"The soundtrack is the user's file {f}; nothing to make." : "The soundtrack is to be a file of the user's, and it isn't there.");
        if (board.Sound.Source != FilmSound.Song)
            throw new FilmException("The storyboard has no song to make: set sound {\"source\": \"song\", \"tags\": …, \"lyrics\": …} first.");
        var (songSeed, voiceSeed) = (board.Sound.Seed, board.Sound.VoiceSeed);
        var made = await KeepingTakeAsync(board, FilmPiece.Song, () => Maker(ctx).MakeSongAsync(board, ctx.Progress, ctx.UserMessage, ct, tries)).ConfigureAwait(false);
        // The seed of the song kept, and the voice the lines keep; not over a seed the Filmmaking
        // window gave meanwhile (Another take), which the take it asks for is then made with.
        SaveSettled(board, fresh =>
        {
            if (fresh.Sound.Seed == songSeed) fresh.Sound.Seed = board.Sound.Seed;
            if (fresh.Sound.VoiceSeed == voiceSeed) fresh.Sound.VoiceSeed = board.Sound.VoiceSeed;
        });
        var preview = MediaPreview.ForFiles([made.File], ctx.Media, ctx.SeesImages);
        var sb = new StringBuilder();
        sb.AppendLine($"The song is made in {Chats.ChatTimes.Stopwatch(made.Took)} with {made.Template}: {made.File} (a minute long at least: shorter songs come out badly; the film uses its start).");
        sb.AppendLine(string.Join("\n", preview.Lines));
        if (made.Score is { } score)
            sb.AppendLine($"How it sounds to SongEval, a judge trained on musicians' ratings, for the part the film uses: {score.Describe()}." +
                          (score.Musicality < SongScore.Good ? " Make it again (more tries, another style or simpler lyrics) before the shots that use it." : "") +
                          (board.Sound.Lyrics.Trim().Length == 0 ? " SongEval is trained on songs with singing: an instrumental scored 2.5 to 3.3 in every style tried, so here it is a rough guide." : ""));
        if (board.Sound.Lyrics.Trim().Length > 0 && await HearAsync(ctx, made.File, ct).ConfigureAwait(false) is { } heard)
            sb.AppendLine(heard.Words is null ? $"{heard.Model} heard no words in it." : $"Its words, as {heard.Model} heard them: \"{heard.Words.ReplaceLineEndings(" ").Clip(1500)}\" Check them against the lyrics.");
        foreach (var note in made.Notes)
            sb.AppendLine("Note: " + note);
        return new ToolResult(sb.ToString().TrimEnd(), preview.Image, ImageCaption: preview.Image is null ? null : "The song:", Files: [made.File]);
    }

    private async Task<ToolResult> ShotAsync(Storyboard board, string id, int tries, ToolContext ctx, CancellationToken ct)
    {
        var shot = board.Shot(id) ?? throw new FilmException($"There is no shot \"{id}\" in this film; its shots are {string.Join(", ", board.Shots.Select(s => s.Id))}.");
        // A shot starts on a picture a picture model makes (the video model draws one badly): one
        // without a first frame gets one from its words, and frames not made yet are made first.
        var first = new List<string>();
        if (board.Frame(shot.First) is null && board.AddFirstFrames() is { Count: > 0 } added)
        {
            board.Save();
            first.Add($"It had no first frame, so it got {string.Join(", ", added.Select(f => f.Id))} from its prompt: look at it, and make it again from the references if someone in it should look the same.");
        }
        foreach (var frame in new[] { shot.First, shot.LipSync ? null : shot.Last }.Distinct().Select(board.Frame).OfType<FilmFrame>())
            if (!FilmMaker.IsCurrent(board, frame))
            {
                var picture = await KeepingTakeAsync(board, FilmPiece.Of(frame), () => Maker(ctx).MakeFrameAsync(board, frame, ctx.Progress, ctx.UserMessage, ct)).ConfigureAwait(false);
                first.Add($"Made {frame.Label} first: {picture.File}." +
                          (await LookAtFrameAsync(board, frame, ctx, ct).ConfigureAwait(false) is { } seen && seen.Contains("To fix:", StringComparison.Ordinal) ? " " + seen.ReplaceLineEndings(" ") : ""));
            }
        var piece = FilmPiece.Of(shot);
        if (tries <= 1)
        {
            var made = await KeepingTakeAsync(board, piece, () => Maker(ctx).MakeShotAsync(board, shot, ctx.Progress, ctx.UserMessage, ct)).ConfigureAwait(false);
            var look = await LookAtShotAsync(board, shot, made.File, ctx, ct).ConfigureAwait(false);
            RememberLook(made.Meta, look.Text, look.Faults);
            return Result(board, shot, $"Shot {shot.Id} made in {Chats.ChatTimes.Stopwatch(made.Took)} with {made.Template}: {made.File}\nIts ComfyUI workflow (fixed seed {shot.Seed}): {Storyboard.WorkflowOf(made.File)}", [.. first, .. made.Notes], look, made.File);
        }

        // Takes with new seeds until a check passes; the best one is kept as the shot's clip, and
        // the others (and the clip there was before) as takes of the shot, to put back in the window.
        var takes = new List<(long Seed, string File, FilmLook Look, string? Verdict, double Score)>();
        var notes = first;
        var before = FilmTakes.Keep(board, piece);
        (long Seed, string File, FilmLook Look, string? Verdict, double Score) best;
        var replaced = false;
        var startSeed = shot.Seed;
        try
        {
            for (var take = 1; take <= tries; take++)
            {
                if (take > 1)
                    shot.Seed = Storyboard.NewSeed();
                ctx.Progress?.Invoke($"Take {take} of {tries}…");
                var file = Path.Combine(board.ShotFolder(shot), $"{Path.GetFileNameWithoutExtension(board.ShotVideo(shot))}.take{take}.mp4");
                var made = await Maker(ctx).MakeShotAsync(board, shot, s => ctx.Progress?.Invoke($"Take {take} of {tries}: {s}"), ctx.UserMessage, ct, take: file).ConfigureAwait(false);
                notes.AddRange(made.Notes.Where(n => !notes.Contains(n)));
                var look = await LookAtShotAsync(board, shot, file, ctx, ct).ConfigureAwait(false);
                RememberLook(made.Meta, look.Text, look.Faults);
                var verdict = FilmCheck.Verdict(look.Text);
                var score = (verdict == "good" ? 10 : verdict is null ? 5 : 0) - 3 * look.Faults.Count;
                takes.Add((shot.Seed, file, look, verdict, score));
                if (verdict == "good" && look.Faults.Count == 0 || ctx.UserMessage.IsCancellationRequested)
                    break;
            }
            best = takes.MaxBy(t => t.Score);
            shot.Seed = best.Seed;
            SaveSettled(board, fresh =>
            {
                // Not over a seed the Filmmaking panel gave meanwhile (Another take): the take it
                // asked for was made again with the best one's, the same clip once more.
                if (fresh.Shot(shot.Id) is { } kept && kept.Seed == startSeed)
                    kept.Seed = best.Seed;
            });
            FilmTakes.Promote(best.File, board.ShotVideo(shot));
            replaced = true;
        }
        finally
        {
            // Stopped half way, the takes made so far are kept too; the shot's clip is still the one
            // there was, so its copy as a take goes (kept, every call again added one more).
            if (!replaced && before is not null)
                FilmTakes.Drop(before);
            foreach (var other in takes.Where(t => File.Exists(t.File)))
                FilmTakes.Add(board, piece, other.File);
        }
        var head = $"Shot {shot.Id}: {takes.Count} take(s); " +
                   (best.Verdict == "good" && best.Look.Faults.Count == 0 ? $"take {takes.IndexOf(best) + 1} passed" : $"none passed, take {takes.IndexOf(best) + 1} was the best") +
                   $" and is the shot's clip now: {board.ShotVideo(shot)} (seed {best.Seed}, saved in the storyboard)." +
                   (takes.Count > 1 ? " The others are kept as takes of the shot, which the user can put back in the Filmmaking panel: " +
                                      string.Join("; ", takes.Where(t => t != best).Select(t => $"take {takes.IndexOf(t) + 1} {t.Verdict ?? "no verdict"}" +
                                                                                                (t.Look.Faults.Count > 0 ? $" ({string.Join(", ", t.Look.Faults)})" : ""))) + "." : "");
        return Result(board, shot, head, notes, best.Look, board.ShotVideo(shot));
    }

    private static ToolResult Result(Storyboard board, FilmShot shot, string head, IReadOnlyList<string> notes, FilmLook look, string clip)
    {
        var sb = new StringBuilder();
        sb.AppendLine(head);
        foreach (var note in notes)
            sb.AppendLine("Note: " + note);
        sb.AppendLine(look.Text);
        if (look.Faults.Count > 0)
            sb.AppendLine("To fix: " + string.Join("; ", look.Faults) + ".");
        if (FilmCheck.Verdict(look.Text) == "redo" && FilmCheck.FramesAdvice(board, shot) is { } frames)
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
            throw new FilmException("Nothing on this PC can read video to check it (ffmpeg in BlackBox's Data\\tools would).");
        if (id is { Length: > 0 })
        {
            var shot = board.Shot(id) ?? throw new FilmException($"There is no frame or shot \"{id}\" in this film.");
            var clip = board.ShotVideo(shot);
            if (!File.Exists(clip))
                throw new FilmException($"Shot {shot.Id} has no clip yet: make it with action=shot.");
            var look = await LookAtShotAsync(board, shot, clip, ctx, ct).ConfigureAwait(false);
            RememberLook(board.ShotMeta(shot), look.Text, look.Faults);
            return Result(board, shot, $"Shot {shot.Id}: {clip}" + (FilmMaker.IsCurrent(board, shot) ? "" : " (made before the storyboard's latest changes to it)"), [], look, clip);
        }
        var film = board.JoinedFile;
        if (!File.Exists(film))
            throw new FilmException("The film isn't joined yet: action=join first, or give a shot's id to check that shot.");
        var check = await Task.Run(() => FilmCheck.Film(board, film, decoder), ct).ConfigureAwait(false);
        var text = new StringBuilder(check.Text).AppendLine();
        // Its sound as people hear it: no one has to listen to every take.
        if (board.Sound.Source != FilmSound.None && ctx.Comfy is { } hub && SongJudge.PythonOf(hub) is { } python && decoder.Ffmpeg is { } ffmpeg && Maker(ctx).Judge is { } judge)
        {
            try
            {
                if ((await judge.ScoreAsync([film], python, ffmpeg, 0, ctx.Progress, ct).ConfigureAwait(false)).Values.FirstOrDefault() is { } heard)
                    text.AppendLine($"Its sound, to SongEval: {heard.Describe()}." + (heard.Musicality < SongScore.Poor ? " The song or the shots' own sound under it spoils it." : ""));
            }
            catch (FilmException ex)
            {
                text.AppendLine("Its sound couldn't be judged: " + ex.Message);
            }
        }
        if (ctx.Ask is { } ask && await FilmCheck.JudgeFilmAsync(board, check, ask, ct).ConfigureAwait(false) is { } judged)
            text.AppendLine("Fresh looks at the shots side by side and at each cut: " + judged.Answer.Trim());
        if (check.Faults.Count > 0)
            text.AppendLine("To fix: " + string.Join("; ", check.Faults) + ".");
        text.Append("Fix what a viewer would notice (a frame first when a person or place differs, then its shots), then join again. " +
                    "Each round costs minutes per shot: after two rounds, finish unless something is still clearly wrong, and tell the user what is left.");
        return new ToolResult(text.ToString(), check.Sheet, ImageCaption: check.Sheet is null ? null : "One frame from the middle of each shot, in order:", Files: [film]);
    }

    /// <summary>A fresh look at a frame as it is: action=check with a frame's id.</summary>
    private static async Task<ToolResult> CheckFrameAsync(Storyboard board, FilmFrame frame, ToolContext ctx, CancellationToken ct)
    {
        var picture = board.FramePicture(frame);
        if (!File.Exists(picture))
            throw new FilmException($"{frame.Label} isn't made yet: make it with action=frame.");
        if (frame.Of is { } of)
            return new ToolResult($"{frame.Label} is where {of}'s clip ends, taken from it: check shot {of} instead.");
        var preview = MediaPreview.ForFiles([picture], ctx.Media, ctx.SeesImages);
        var look = await LookAtFrameAsync(board, frame, ctx, ct).ConfigureAwait(false)
                   ?? "It couldn't be looked at: the model doesn't see pictures here. Look at it yourself.";
        return new ToolResult($"Frame {frame.Id}: {picture}" + (FilmMaker.IsCurrent(board, frame) ? "" : " (made before the storyboard's latest changes to it)") + "\n" + look,
            preview.Image, ImageCaption: preview.Image is null ? null : $"Frame {frame.Id}:", Files: [picture]);
    }

    /// <summary>The numbers and the fresh look at a shot's clip, and for a lip-synced one what is sung under it.</summary>
    private async Task<FilmLook> LookAtShotAsync(Storyboard board, FilmShot shot, string clip, ToolContext ctx, CancellationToken ct)
    {
        if (ctx.Media is not { } decoder)
            return new FilmLook("Nothing on this PC can read video to check it.", null, []);
        ctx.Progress?.Invoke($"Looking at shot {shot.Id}…");
        var look = await Task.Run(() => FilmCheck.Shot(board, shot, clip, decoder), ct).ConfigureAwait(false);
        var text = new StringBuilder(look.Text).AppendLine();
        string? sung = null;
        if (shot.LipSync && File.Exists(board.ShotAudio(shot)) && await HearAsync(ctx, board.ShotAudio(shot), ct).ConfigureAwait(false) is { } heard)
        {
            sung = heard.Words is null ? "No words are sung or said in the soundtrack's part under this shot." : $"The words sung or said under this shot: \"{heard.Words.ReplaceLineEndings(" ").Clip(600)}\".";
            text.AppendLine(sung + $" ({heard.Model} listened to it.)");
        }
        if (ctx.Ask is { } ask)
        {
            ctx.Progress?.Invoke($"Two fresh looks at shot {shot.Id}: physics and bodies, then behaviour over time…");
            if (await FilmCheck.JudgeShotAsync(board, shot, look, sung, ask, ct).ConfigureAwait(false) is { Length: > 0 } judged)
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
        var stale = board.Shots.Where(s => File.Exists(board.ShotVideo(s)) && !FilmMaker.IsCurrent(board, s)).Select(s => s.Id).ToList();
        var made = await FilmMontage.JoinAsync(board, ctx.Media?.Ffmpeg, ctx.Media, ctx.Progress, ct).ConfigureAwait(false);
        var preview = MediaPreview.ForFiles([made.File], ctx.Media, ctx.SeesImages);
        var sb = new StringBuilder();
        sb.AppendLine($"Joined the film in {Chats.ChatTimes.Stopwatch(made.Took)}: {made.File}");
        sb.AppendLine(string.Join("\n", preview.Lines));
        sb.AppendLine($"It fades in from black over its first {N(FilmMontage.FadeIn)} s and out over its last {N(FilmMontage.FadeOut)} s on purpose, so a dark first or last frame is that, not a fault.");
        foreach (var note in made.Notes)
            sb.AppendLine("Note: " + note);
        if (stale.Count > 0)
            sb.AppendLine($"Shots {string.Join(", ", stale)} were made before the storyboard's latest changes to them; make them again if those changes matter.");
        sb.Append("Check the whole film now (action=check without id).");
        return new ToolResult(sb.ToString(), preview.Image, ImageCaption: preview.Image is null ? null : "The film:", Files: [made.File]);
    }


    private static string N(double d) => d.ToString("0.##", CultureInfo.InvariantCulture);
}
