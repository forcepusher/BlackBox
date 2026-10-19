using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using BlackBox.Core.Comfy;
using BlackBox.Core.Film;
using BlackBox.Core.Media;

namespace BlackBox.Core.Tools;

/// <summary>
/// Films from a storyboard (see <see cref="Storyboard"/>): the same storyboard.json the user edits in
/// the Filmmaking window. The agent plans or changes it, makes its frames, song and shots with
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
        "The storyboard is the film's storyboard.json, which the user also edits in BlackBox's Filmmaking window: shots in order, each going from a first frame " +
        "to an optional last frame in so many seconds, the pictures they start and end on, and the soundtrack. " +
        "action=storyboard shows it (path: the film's folder), or makes or changes it with storyboard={...}: title, story, style (the look, added to every prompt), " +
        "width, height, fps, frames:[{id, prompt, seed, from: [ids of frames it is made from, to keep people and places the same], file (a picture of the user's), name, reference: true for a character or place sheet}], " +
        "shots:[{id, first, last (frame ids), seconds, prompt (what happens), camera, transition: cut|dissolve|fade, " +
        "line (what is said in it, lip-synced, in the voice sound.voice describes; it starts 0.4 s in, so leave time for it), lipSync (without a line: sings the soundtrack's part under it), seed}], " +
        "sound:{source: none|file|song, file, tags (the song's style), lyrics, voice (who says the lines: age, voice, manner), seed}. Frames and shots are matched by id; {\"id\": …, \"delete\": true} removes one. " +
        "Without path a new film goes into a folder of its own in BlackBox's Data\\films, where the Filmmaking window finds it; its frames, clips and the film are made there too " +
        "(BlackBox is portable: what it makes stays next to it). " +
        "A shot that starts on the last frame of the one before goes on with the same camera; " +
        "a new first frame is a cut to another scene or angle. Keep one clear action per shot, 3 to 8 seconds. " +
        "action=frame id= makes a frame's picture; action=sound makes the song, a minute long at least (the film uses its start), the best of tries=3 by a judge " +
        "of how good music sounds (SongEval); write lyrics for the part the film uses. action=shot id= makes a shot's clip and checks it (does it start and end on its frames, " +
        "how it moves, and a fresh look at what happens, faults and whether people stay the same); tries=N makes up to N takes with new seeds until one passes. " +
        "Fix a shot the check finds wrong: a plainer prompt, another seed, frames closer to each other, or a new frame. " +
        "action=join puts the shots and the soundtrack together into the film; action=check looks at a shot (id) or, without id, the whole film. " +
        "Order: frames (those others are made from first), the song when a shot is lip-synced, the shots, then join and check the film. " +
        "prompt, camera, seconds, seed and from given with action=frame or shot change the storyboard before it is made.";

    public override ToolRisk Risk => ToolRisk.Risky;

    public override JsonObject Parameters => Schema(
        ("action", Str("What to do", "storyboard", "frame", "shot", "sound", "join", "check"), true),
        ("path", Str("The film's folder (or its storyboard.json); a new film's folder for action=storyboard"), false),
        ("id", Str("A frame's id (action=frame) or a shot's (action=shot, check)"), false),
        ("storyboard", new JsonObject { ["type"] = "object", ["description"] = "action=storyboard: the storyboard, or the parts of it to change (frames and shots by id)" }, false),
        ("prompt", Str("action=frame or shot: a new prompt for it"), false),
        ("camera", Str("action=shot: what the camera does"), false),
        ("seconds", Num("action=shot: how long it lasts"), false),
        ("seed", Int("action=frame or shot: a new seed (another result from the same prompt)"), false),
        ("from", StrArray("action=frame: the frames it is made from"), false),
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
            "check" => id is null ? "Check the film" : $"Check shot {id}",
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
            var board = LoadOrCreate(args, ctx, create: action == "storyboard");
            _lastFolder = board.Folder;
            if (action != "storyboard" && Changes(args, action) is { Count: > 0 } changes)
            {
                board = Storyboard.Merge(board, changes);
                board.Save();
            }
            return action switch
            {
                "storyboard" => Show(board, args["storyboard"] is JsonObject, ctx),
                "frame" => await FrameAsync(board, RequireId(args), ctx, ct).ConfigureAwait(false),
                "shot" => await ShotAsync(board, RequireId(args), (int)Math.Clamp(GetNumber(args, "tries") ?? 1, 1, 8), ctx, ct).ConfigureAwait(false),
                "sound" or "song" => await SoundAsync(board, (int)Math.Clamp(GetNumber(args, "tries") ?? 3, 1, 8), ctx, ct).ConfigureAwait(false),
                "join" or "montage" => await JoinAsync(board, ctx, ct).ConfigureAwait(false),
                "check" => await CheckAsync(board, GetString(args, "id"), ctx, ct).ConfigureAwait(false),
                _ => throw new ToolArgumentException("action must be storyboard, frame, shot, sound, join or check."),
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
        var given = GetString(args, "path") is { Length: > 0 } p ? ctx.ResolvePath(p) : _lastFolder;
        var change = args["storyboard"] as JsonObject;
        if (given is not null && (File.Exists(given) && Path.GetFileName(given).Equals(Storyboard.FileName, StringComparison.OrdinalIgnoreCase) || File.Exists(Path.Combine(given, Storyboard.FileName))))
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
            throw new FilmException(given is null
                ? "Which film? Give path: the film's folder (the one with its storyboard.json), or make a new one with action=storyboard and storyboard={…}."
                : $"There is no storyboard in {given}. Make one with action=storyboard and storyboard={{…}} (path is where it goes).");
        var title = (string?)change["title"] ?? "Untitled film";
        var root = ctx.FilmsDirectory ?? Path.Combine(ctx.ScratchDirectory ?? Path.GetTempPath(), "films");
        var folder = given ?? Path.Combine(root, Storyboard.Safe(title));
        // Another film of the same title is another film: it isn't written over.
        for (var n = 2; given is null && File.Exists(Path.Combine(folder, Storyboard.FileName)); n++)
            folder = Path.Combine(root, Storyboard.Safe($"{title} {n}"));
        if (given is not null && Path.GetFileName(given).Equals(Storyboard.FileName, StringComparison.OrdinalIgnoreCase))
            folder = Path.GetDirectoryName(given)!;
        Directory.CreateDirectory(folder);
        var created = Storyboard.Merge(new Storyboard { Title = title, Folder = folder }, change);
        created.Save();
        return created;
    }

    /// <summary>The storyboard changes a frame or shot call gives with it: a new prompt, seed, length.</summary>
    private static JsonObject? Changes(JsonObject args, string action)
    {
        if (GetString(args, "id")?.Trim() is not { Length: > 0 } id || action is not ("frame" or "shot"))
            return null;
        var item = new JsonObject { ["id"] = id };
        if (GetString(args, "prompt") is { } prompt) item["prompt"] = prompt;
        if (GetNumber(args, "seed") is { } seed) item["seed"] = (long)seed;
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

    private static ToolResult Show(Storyboard board, bool changed, ToolContext ctx)
    {
        var text = Overview(board);
        var line = changed ? $"Saved the storyboard: {board.FilePath}. The user can open it in the Filmmaking window (the button at the top of BlackBox).\n" : "";
        return new ToolResult(line + text);
    }

    /// <summary>The storyboard as the model reads it: each frame and shot, what is made and what is out of date, and what to do next.</summary>
    internal static string Overview(Storyboard board)
    {
        var sb = new StringBuilder();
        var (starts, length) = board.Timing();
        sb.AppendLine($"Film \"{board.Title}\" in {board.Folder}: {board.Width}x{board.Height} at {board.Fps} fps, {N(length)} s in {board.Shots.Count} shot(s).");
        if (board.Story.Length > 0) sb.AppendLine("Story: " + board.Story.Trim());
        if (board.Style.Length > 0) sb.AppendLine("Style: " + board.Style.Trim());
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
                                                                                      (f.From.Count > 0 ? $", made from {string.Join(", ", f.From)}" : "");
            var used = board.Shots.Where(s => s.First == f.Id || s.Last == f.Id).Select(s => s.Id).ToList();
            sb.AppendLine($"  {f.Id}{(f.Name is { Length: > 0 } n ? $" \"{n}\"" : "")}{(f.Reference ? " (a reference)" : used.Count > 0 ? $" (in {string.Join(", ", used)})" : " (not in a shot)")}: {source}. {state}" +
                          (made ? $": {board.FramePicture(f)}" : ""));
            if (!current)
                todo.Add($"frame {f.Id}");
        }
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
                              $", seed {sound.Seed}. " + (!File.Exists(board.SongFile) ? "Not made." : songCurrent ? $"Made: {board.SongFile}" : $"Made, but out of date (the film's length or the song changed): {board.SongFile}"));
                if (!songCurrent)
                    todo.Insert(todo.FindIndex(t => t.StartsWith("shot", StringComparison.Ordinal)) is var at and >= 0 ? at : todo.Count, "the song (action=sound)");
                break;
            default:
                sb.AppendLine("Soundtrack: none (each shot's own sound).");
                break;
        }
        if (board.Shots.Any(Storyboard.Speaks))
            sb.AppendLine("The lines' voice: " + (sound.Voice.Trim().Length > 0 ? sound.Voice.Trim() : "not described (sound.voice): the voice model picks one") + ".");
        var chosen = new List<string>();
        foreach (var job in Enum.GetValues<FilmJob>())
            if (FilmTemplates.Chosen(board.Workflows, job) is { } name)
                chosen.Add($"{job}: {name}");
        sb.AppendLine("Templates: " + (chosen.Count == 0 ? "picked from what is installed" : string.Join("; ", chosen)) + ".");
        var film = board.FilmFile;
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

    // ---- making ----------------------------------------------------------------------------------------

    private async Task<ToolResult> FrameAsync(Storyboard board, string id, ToolContext ctx, CancellationToken ct)
    {
        var frame = board.Frame(id) ?? throw new FilmException($"There is no frame \"{id}\" in this film; its frames are {string.Join(", ", board.Frames.Select(f => f.Id))}.");
        var made = await Maker(ctx).MakeFrameAsync(board, frame, ctx.Progress, ctx.UserMessage, ct).ConfigureAwait(false);
        var preview = MediaPreview.ForFiles([made.File], ctx.Media, ctx.SeesImages);
        var sb = new StringBuilder();
        sb.AppendLine($"Frame {frame.Id} made in {Clock(made.Took)}" + (made.Template is { } t ? $" with {t}" : " from the user's picture") + $": {made.File}");
        if (made.Workflow is { } wf) sb.AppendLine($"Its workflow (fixed seed {frame.Seed}): {wf}");
        foreach (var note in made.Notes.Where(n => !n.Contains("no place for the size", StringComparison.Ordinal)))
            sb.AppendLine("Note: " + note);
        sb.Append("Look at it: does it show what its prompt says, in the film's look, with the same people and places as the frames it is made from? " +
                  "If not, change the prompt or the seed and make it again.");
        return new ToolResult(sb.ToString(), preview.Image, ImageCaption: preview.Image is null ? null : $"Frame {frame.Id}:", Files: [made.File]);
    }

    private async Task<ToolResult> SoundAsync(Storyboard board, int tries, ToolContext ctx, CancellationToken ct)
    {
        if (board.Sound.Source == FilmSound.FromFile)
            return new ToolResult(board.SoundtrackFile is { } f && File.Exists(f) ? $"The soundtrack is the user's file {f}; nothing to make." : "The soundtrack is to be a file of the user's, and it isn't there.");
        if (board.Sound.Source != FilmSound.Song)
            throw new FilmException("The storyboard has no song to make: set sound {\"source\": \"song\", \"tags\": …, \"lyrics\": …} first.");
        var made = await Maker(ctx).MakeSongAsync(board, ctx.Progress, ctx.UserMessage, ct, tries).ConfigureAwait(false);
        board.Save(); // the seed of the song kept
        var preview = MediaPreview.ForFiles([made.File], ctx.Media, ctx.SeesImages);
        var sb = new StringBuilder();
        sb.AppendLine($"The song is made in {Clock(made.Took)} with {made.Template}: {made.File} (a minute long at least: shorter songs come out badly; the film uses its start).");
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
        if (tries <= 1)
        {
            var made = await Maker(ctx).MakeShotAsync(board, shot, ctx.Progress, ctx.UserMessage, ct).ConfigureAwait(false);
            var look = await LookAtShotAsync(board, shot, made.File, ctx, ct).ConfigureAwait(false);
            return Result($"Shot {shot.Id} made in {Clock(made.Took)} with {made.Template}: {made.File}\nIts workflow (fixed seed {shot.Seed}): {made.Workflow}", made.Notes, look, made.File);
        }

        // Takes with new seeds until a check passes; the best one is kept as the shot's clip.
        var takes = new List<(long Seed, string File, FilmLook Look, string? Verdict, double Score)>();
        var notes = new List<string>();
        for (var take = 1; take <= tries; take++)
        {
            if (take > 1)
                shot.Seed = Storyboard.NewSeed();
            ctx.Progress?.Invoke($"Take {take} of {tries}…");
            var file = Path.Combine(board.Folder, "shots", $"{Storyboard.Safe(shot.Id)}.take{take}.mp4");
            var made = await Maker(ctx).MakeShotAsync(board, shot, s => ctx.Progress?.Invoke($"Take {take} of {tries}: {s}"), ctx.UserMessage, ct, take: file).ConfigureAwait(false);
            notes.AddRange(made.Notes.Where(n => !notes.Contains(n)));
            var look = await LookAtShotAsync(board, shot, file, ctx, ct).ConfigureAwait(false);
            var verdict = FilmCheck.Verdict(look.Text);
            var score = (verdict == "good" ? 10 : verdict is null ? 5 : 0) - 3 * look.Faults.Count;
            takes.Add((shot.Seed, file, look, verdict, score));
            if (verdict == "good" && look.Faults.Count == 0 || ctx.UserMessage.IsCancellationRequested)
                break;
        }
        var best = takes.MaxBy(t => t.Score);
        shot.Seed = best.Seed;
        board.Save();
        File.Copy(best.File, board.ShotVideo(shot), overwrite: true);
        File.Copy(Path.ChangeExtension(best.File, ".json"), board.ShotWorkflow(shot), overwrite: true);
        var head = $"Shot {shot.Id}: {takes.Count} take(s); " +
                   (best.Verdict == "good" && best.Look.Faults.Count == 0 ? $"take {takes.IndexOf(best) + 1} passed" : $"none passed, take {takes.IndexOf(best) + 1} was the best") +
                   $" and is the shot's clip now: {board.ShotVideo(shot)} (seed {best.Seed}, saved in the storyboard). The other takes are beside it." +
                   (takes.Count > 1 ? "\nEarlier takes: " + string.Join("; ", takes.Where(t => t != best).Select(t => $"{Path.GetFileName(t.File)} {t.Verdict ?? "no verdict"}" +
                                                                                                   (t.Look.Faults.Count > 0 ? $" ({string.Join(", ", t.Look.Faults)})" : ""))) : "");
        return Result(head, notes, best.Look, board.ShotVideo(shot));
    }

    private ToolResult Result(string head, IReadOnlyList<string> notes, FilmLook look, string clip)
    {
        var sb = new StringBuilder();
        sb.AppendLine(head);
        foreach (var note in notes)
            sb.AppendLine("Note: " + note);
        sb.AppendLine(look.Text);
        if (look.Faults.Count > 0)
            sb.AppendLine("To fix: " + string.Join("; ", look.Faults) + ".");
        sb.Append(FixAdvice);
        return new ToolResult(sb.ToString(), look.Sheet, ImageCaption: look.Sheet is null ? null : "The storyboard's frames (top) and the clip's frames with their times:", Files: [clip]);
    }

    private const string FixAdvice =
        "If the clip doesn't show what the shot should (the check above, and your own look), fix it and make it again: a plainer prompt (one action, the people and place named " +
        "as in the frames), another seed, a shorter shot, frames closer to each other, or a new frame. Move on when it is right.";

    // ---- checking ----------------------------------------------------------------------------------------

    private async Task<ToolResult> CheckAsync(Storyboard board, string? id, ToolContext ctx, CancellationToken ct)
    {
        if (ctx.Media is not { } decoder)
            throw new FilmException("Nothing on this PC can read video to check it (ffmpeg in BlackBox's Data\\tools would).");
        if (id is { Length: > 0 })
        {
            var shot = board.Shot(id) ?? throw new FilmException($"There is no shot \"{id}\" in this film.");
            var clip = board.ShotVideo(shot);
            if (!File.Exists(clip))
                throw new FilmException($"Shot {shot.Id} has no clip yet: make it with action=shot.");
            var look = await LookAtShotAsync(board, shot, clip, ctx, ct).ConfigureAwait(false);
            return Result($"Shot {shot.Id}: {clip}" + (FilmMaker.IsCurrent(board, shot) ? "" : " (made before the storyboard's latest changes to it)"), [], look, clip);
        }
        var film = board.FilmFile;
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
        if (check.Sheet is { } sheet && ctx.Ask is { } ask && await ask(sheet, FilmCheck.JudgeFilm(board), ct).ConfigureAwait(false) is { Length: > 0 } judged)
            text.AppendLine("A fresh look at the shots side by side: " + judged.Trim());
        if (check.Faults.Count > 0)
            text.AppendLine("To fix: " + string.Join("; ", check.Faults) + ".");
        text.Append("Fix what a viewer would notice (a frame first when a person or place differs, then its shots), then join again. " +
                    "Each round costs minutes per shot: after two rounds, finish unless something is still clearly wrong, and tell the user what is left.");
        return new ToolResult(text.ToString(), check.Sheet, ImageCaption: check.Sheet is null ? null : "One frame from the middle of each shot, in order:", Files: [film]);
    }

    /// <summary>The numbers and the fresh look at a shot's clip, and for a lip-synced one what is sung under it.</summary>
    private async Task<FilmLook> LookAtShotAsync(Storyboard board, FilmShot shot, string clip, ToolContext ctx, CancellationToken ct)
    {
        if (ctx.Media is not { } decoder)
            return new FilmLook("Nothing on this PC can read video to check it.", null, []);
        ctx.Progress?.Invoke($"Looking at shot {shot.Id}…");
        var look = await Task.Run(() => FilmCheck.Shot(board, shot, clip, decoder), ct).ConfigureAwait(false);
        var text = new StringBuilder(look.Text).AppendLine();
        var question = FilmCheck.JudgeShot(board, shot);
        if (shot.LipSync && File.Exists(board.ShotAudio(shot)) && await HearAsync(ctx, board.ShotAudio(shot), ct).ConfigureAwait(false) is { } heard)
        {
            var sung = heard.Words is null ? "No words are sung or said in the soundtrack's part under this shot." : $"The words sung or said under this shot: \"{heard.Words.ReplaceLineEndings(" ").Clip(600)}\".";
            text.AppendLine(sung + $" ({heard.Model} listened to it.)");
            question += "\n" + sung;
        }
        if (look.Sheet is { } sheet && ctx.Ask is { } ask)
        {
            ctx.Progress?.Invoke($"A fresh look at shot {shot.Id}…");
            if (await ask(sheet, question, ct).ConfigureAwait(false) is { Length: > 0 } judged)
                text.AppendLine("A fresh look at its frames: " + judged.Trim());
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
        sb.AppendLine($"Joined the film in {Clock(made.Took)}: {made.File}");
        sb.AppendLine(string.Join("\n", preview.Lines));
        foreach (var note in made.Notes)
            sb.AppendLine("Note: " + note);
        if (stale.Count > 0)
            sb.AppendLine($"Shots {string.Join(", ", stale)} were made before the storyboard's latest changes to them; make them again if those changes matter.");
        sb.Append("Check the whole film now (action=check without id).");
        return new ToolResult(sb.ToString(), preview.Image, ImageCaption: preview.Image is null ? null : "The film:", Files: [made.File]);
    }

    private static string Clock(TimeSpan t) => ComfyRunner.Clock(t);

    private static string N(double d) => d.ToString("0.##", CultureInfo.InvariantCulture);
}
