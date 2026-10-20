using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace BlackBox.Core.Film;

/// <summary>
/// A film as the user or the agent plans it: shots in order, each going from a first frame to a
/// last one in so many seconds, the pictures they start and end on, and the film's sound. It is
/// the storyboard.json in the film's folder, which the Filmmaking window and the film tool both
/// read and write. Only what was decided is kept in it: what was made sits at fixed places beside
/// it (frames/&lt;id&gt;.png, shots/&lt;id&gt;.mp4, sound/song.mp3), each with the workflow that made it,
/// so the window and the agent can't overwrite each other's results.
/// </summary>
public sealed class Storyboard
{
    public const string FileName = "storyboard.json";

    /// <summary>How long a dissolve or a fade between two shots takes: the two overlap that long.</summary>
    public const double DissolveSeconds = 0.5, FadeSeconds = 1.0;

    public string Title { get; set; } = "";

    /// <summary>
    /// The look of the whole film, from storyboards before each frame and shot had all of its own
    /// prompt: read, moved into every prompt (<see cref="Upgrade"/>) and not written again. A setting
    /// in every prompt made one change remake every frame and shot.
    /// </summary>
    public string? Style { get; set; }

    public int Width { get; set; } = 1280;
    public int Height { get; set; } = 720;
    public int Fps { get; set; } = 24;

    /// <summary>The pictures: frames shots start and end on, and references (a character, a place) others are made from.</summary>
    public List<FilmFrame> Frames { get; set; } = [];

    public List<FilmShot> Shots { get; set; } = [];

    public FilmSound Sound { get; set; } = new();

    /// <summary>
    /// The templates for each kind of job, from storyboards before each frame, shot and song said what
    /// it is made with (<see cref="FilmFrame.Model"/>): read, moved into them, and not written again.
    /// </summary>
    public FilmWorkflows? Workflows { get; set; }

    /// <summary>How loud each shot's own sound is in the film (0 to 1); unset: half under a soundtrack, full without one.</summary>
    public double? ShotVolume { get; set; }

    /// <summary>How loud the soundtrack is in the film (0 to 1).</summary>
    public double SoundVolume { get; set; } = 1;

    /// <summary>The folder the storyboard is in; not saved.</summary>
    [JsonIgnore]
    public string Folder { get; set; } = "";

    [JsonIgnore]
    public string FilePath => Path.Combine(Folder, FileName);

    public FilmFrame? Frame(string? id) => id is null ? null : Frames.FirstOrDefault(f => f.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    public FilmShot? Shot(string? id) => id is null ? null : Shots.FirstOrDefault(s => s.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    /// <summary>Where a frame's picture is once it is made (or taken from the user's file).</summary>
    public string FramePicture(FilmFrame frame) => Path.Combine(Folder, "frames", Safe(frame.Id) + ".png");

    /// <summary>The workflow that made a frame's picture, as it ran.</summary>
    public string FrameWorkflow(FilmFrame frame) => Path.Combine(Folder, "frames", Safe(frame.Id) + ".json");

    public string ShotVideo(FilmShot shot) => Path.Combine(Folder, "shots", Safe(shot.Id) + ".mp4");

    public string ShotWorkflow(FilmShot shot) => Path.Combine(Folder, "shots", Safe(shot.Id) + ".json");

    /// <summary>The sound a lip-synced shot is made with: its line, or the part of the soundtrack under it, as long as the shot.</summary>
    public string ShotAudio(FilmShot shot) => Path.Combine(Folder, "shots", Safe(shot.Id) + ".wav");

    /// <summary>A shot's line as the voice model said it, and the workflow that said it.</summary>
    public string ShotLine(FilmShot shot) => Path.Combine(Folder, "shots", Safe(shot.Id) + ".line.flac");

    public string ShotLineWorkflow(FilmShot shot) => Path.Combine(Folder, "shots", Safe(shot.Id) + ".line.json");

    /// <summary>Whether a shot is lip-synced to a line of its own (rather than the soundtrack).</summary>
    public static bool Speaks(FilmShot shot) => shot.LipSync && shot.Line.Trim().Length > 0;

    public string SongFile => Path.Combine(Folder, "sound", "song.mp3");

    public string SongWorkflow => Path.Combine(Folder, "sound", "song.json");

    /// <summary>The soundtrack to use: the user's file, the song once made, or none.</summary>
    public string? SoundtrackFile => Sound.Source switch
    {
        FilmSound.FromFile => Sound.File is { Length: > 0 } file ? file : null,
        FilmSound.Song => File.Exists(SongFile) ? SongFile : null,
        _ => null,
    };

    /// <summary>The joined film: named after it, in its folder.</summary>
    public string FilmFile => Path.Combine(Folder, Safe(Title.Length > 0 ? Title : "film") + ".mp4");

    /// <summary>A name made safe as a file name.</summary>
    public static string Safe(string name)
    {
        var bad = Path.GetInvalidFileNameChars();
        var safe = string.Concat(name.Trim().Select(c => bad.Contains(c) ? '_' : c)).Trim('.', ' ');
        return safe.Length == 0 ? "film" : safe.Length > 80 ? safe[..80].Trim() : safe;
    }

    /// <summary>
    /// When each shot starts in the film and how long the film is: shots follow each other, and
    /// a dissolve or a fade makes two overlap for its length.
    /// </summary>
    public (IReadOnlyList<double> Starts, double Length) Timing()
    {
        var starts = new List<double>();
        var at = 0.0;
        for (var i = 0; i < Shots.Count; i++)
        {
            var overlap = i == 0 ? 0 : Overlap(Shots[i]);
            at = Math.Max(0, at - overlap);
            starts.Add(at);
            at += Math.Max(0, Shots[i].Seconds);
        }
        return (starts, at);
    }

    /// <summary>How long a shot overlaps the one before it.</summary>
    public static double Overlap(FilmShot shot) => shot.Transition switch
    {
        FilmShot.Dissolve => Math.Min(DissolveSeconds, shot.Seconds / 2),
        FilmShot.Fade => Math.Min(FadeSeconds, shot.Seconds / 2),
        _ => 0,
    };

    /// <summary>The next free id with this prefix: f1, f2… for frames, s1, s2… for shots.</summary>
    public string NewId(string prefix)
    {
        var taken = Frames.Select(f => f.Id).Concat(Shots.Select(s => s.Id)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        for (var n = 1; ; n++)
            if (!taken.Contains(prefix + n))
                return prefix + n;
    }

    /// <summary>A seed to start from: fixed once given, so the same prompt makes the same picture again.</summary>
    public static long NewSeed() => Random.Shared.NextInt64(1, 1L << 48);

    /// <summary>
    /// Gives each shot without a first frame one of its own, from the shot's words and the references
    /// of the frames nearest it: the video model animates a picture well and draws one badly (the
    /// user's finding), so every shot starts on a picture a picture model made. The frames added.
    /// </summary>
    public IReadOnlyList<FilmFrame> AddFirstFrames()
    {
        var added = new List<FilmFrame>();
        for (var i = 0; i < Shots.Count; i++)
        {
            var shot = Shots[i];
            if (shot.First is not null && Frame(shot.First) is not null)
                continue;
            // The people and places the shots around it are made from.
            var near = Enumerable.Range(1, Shots.Count).SelectMany(d => new[] { i - d, i + d }).Where(j => j >= 0 && j < Shots.Count)
                .Select(j => Frame(Shots[j].First)).OfType<FilmFrame>().FirstOrDefault();
            var frame = new FilmFrame { Id = NewId("f"), Prompt = shot.Prompt.Trim(), Seed = NewSeed(), From = References(near) };
            Frames.Add(frame);
            shot.First = frame.Id;
            added.Add(frame);
        }
        return added;
    }

    /// <summary>The references a frame is made from, however far back.</summary>
    private List<string> References(FilmFrame? frame)
    {
        var found = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Walk(FilmFrame f)
        {
            foreach (var id in f.From)
                if (seen.Add(id) && Frame(id) is { } from)
                {
                    if (from.Reference)
                        found.Add(from.Id);
                    else
                        Walk(from);
                }
        }
        if (frame is not null)
            Walk(frame);
        return found;
    }

    // ---- reading and writing ---------------------------------------------------------------------

    /// <summary>
    /// The storyboard in a folder, or in the storyboard.json given. One from before frames and shots
    /// said all of their own look and model is upgraded and saved, what was made staying made
    /// (<paramref name="upgrade"/> false only reads it, for its title).
    /// </summary>
    public static Storyboard Load(string path, bool upgrade = true)
    {
        var file = Directory.Exists(path) ? Path.Combine(path, FileName) : path;
        var board = Parse(File.ReadAllText(file), upgrade: false);
        board.Folder = Path.GetDirectoryName(Path.GetFullPath(file))!;
        if (upgrade && board.IsOld)
            FilmMaker.Upgrade(board);
        return board;
    }

    /// <param name="upgrade">Moves an old storyboard's film-wide look and templates into its frames and shots (see <see cref="Upgrade"/>).</param>
    public static Storyboard Parse(string json, bool upgrade = true)
    {
        try
        {
            var node = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            if (node is not JsonObject root)
                throw new FilmException("The storyboard isn't valid: it must be a JSON object.");
            Tidy(root);
            var board = root.Deserialize(StoryboardJson.Default.Storyboard) ?? new Storyboard();
            board.Normalize();
            if (upgrade)
                board.Upgrade();
            return board;
        }
        catch (JsonException ex)
        {
            throw new FilmException($"The storyboard isn't valid: {ex.Message}");
        }
    }

    /// <summary>
    /// Shapes a model writes that mean what the storyboard means: "from": "f1" for ["f1"], a
    /// frame's id as a number, a sound given as its file's path.
    /// </summary>
    private static void Tidy(JsonObject root)
    {
        foreach (var frame in (root["frames"] as JsonArray ?? []).OfType<JsonObject>())
        {
            if (frame["from"] is JsonValue one)
                frame["from"] = new JsonArray(JsonValue.Create(one.ToString()));
            else if (frame["from"] is JsonArray many)
                frame["from"] = new JsonArray(many.Select(x => (JsonNode)JsonValue.Create(x?.ToString() ?? "")).ToArray());
            Text(frame, "id");
        }
        foreach (var shot in (root["shots"] as JsonArray ?? []).OfType<JsonObject>())
            foreach (var key in new[] { "id", "first", "last" })
                Text(shot, key);
        if (root["sound"] is JsonValue sound && sound.TryGetValue<string>(out var path))
            root["sound"] = new JsonObject { ["source"] = FilmSound.FromFile, ["file"] = path };

        static void Text(JsonObject o, string key)
        {
            if (o[key] is JsonValue v && v.GetValueKind() == JsonValueKind.Number)
                o[key] = v.ToJsonString();
        }
    }

    /// <summary>Whether it still has a film-wide look or templates, from before frames and shots had all of their own.</summary>
    [JsonIgnore]
    public bool IsOld => Style is not null || Workflows is not null;

    /// <summary>
    /// Moves a film-wide look into every frame's and shot's own prompt, and film-wide templates into
    /// what each frame, shot and the song is made with, so that from then on a change to one of them
    /// changes only it. The prompts come out as they were made with, so frames stay made; shots are
    /// stamped again by <see cref="FilmMaker.Upgrade"/>.
    /// </summary>
    public void Upgrade()
    {
        if (Style is { } style && style.Trim().Length > 0)
        {
            foreach (var f in Frames.Where(f => f.File is not { Length: > 0 }))
                f.Prompt = JoinText(f.Prompt, style);
            foreach (var s in Shots)
                s.Prompt = JoinText(s.Prompt, style);
        }
        Style = null;
        if (Workflows is { } w)
        {
            foreach (var f in Frames)
                f.Model ??= Clean(FilmTemplates.Chosen(w, f.From.Count > 0 ? FilmJob.Edit : FilmJob.Frame));
            foreach (var s in Shots)
                s.Model ??= Clean(FilmTemplates.Chosen(w, FilmMaker.JobOf(s)));
            Sound.Model ??= Clean(w.Song);
        }
        Workflows = null;
    }

    /// <summary>Two parts of a prompt as one: a full stop between them unless the first ends a sentence.</summary>
    public static string JoinText(string a, string b)
    {
        a = a.Trim();
        b = b.Trim();
        if (a.Length == 0) return b;
        if (b.Length == 0) return a;
        return a.EndsWith('.') || a.EndsWith('!') || a.EndsWith('?') ? $"{a} {b}" : $"{a}. {b}";
    }

    private static string? Clean(string? s) => s?.Trim() is { Length: > 0 } t ? t : null;

    public string ToJson() => JsonSerializer.Serialize(this, Readable.Storyboard);

    /// <summary>Written as people read it: "fox's", not "fox's" (the user may open the file).</summary>
    private static readonly StoryboardJson Readable = new(new JsonSerializerOptions(StoryboardJson.Default.Options)
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    });

    public JsonObject ToJsonObject() => (JsonObject)JsonNode.Parse(ToJson())!;

    /// <summary>Saves it to storyboard.json in its folder.</summary>
    public void Save()
    {
        if (Folder.Length == 0)
            throw new InvalidOperationException("The storyboard has no folder to be saved in.");
        Settings.AtomicFile.Write(FilePath, ToJson());
    }

    /// <summary>
    /// Ids filled in and made unique, seeds given, numbers kept in range: a storyboard written by
    /// hand or by a model can leave any of them out.
    /// </summary>
    public void Normalize()
    {
        Frames ??= [];
        Shots ??= [];
        Sound ??= new FilmSound();
        Width = Even(Math.Clamp(Width <= 0 ? 1280 : Width, 64, 4096));
        Height = Even(Math.Clamp(Height <= 0 ? 720 : Height, 64, 4096));
        Fps = Math.Clamp(Fps <= 0 ? 24 : Fps, 1, 120);
        SoundVolume = Math.Clamp(SoundVolume, 0, 2);
        if (ShotVolume is { } v)
            ShotVolume = Math.Clamp(v, 0, 2);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in Frames)
        {
            f.From ??= [];
            if (string.IsNullOrWhiteSpace(f.Id) || !seen.Add(f.Id.Trim()))
                f.Id = NextFree("f", seen);
            f.Id = f.Id.Trim();
            if (f.Seed <= 0)
                f.Seed = NewSeed();
            f.Prompt ??= "";
            f.Model = Clean(f.Model);
        }
        foreach (var s in Shots)
        {
            if (string.IsNullOrWhiteSpace(s.Id) || !seen.Add(s.Id.Trim()))
                s.Id = NextFree("s", seen);
            s.Id = s.Id.Trim();
            if (s.Seed <= 0)
                s.Seed = NewSeed();
            s.Seconds = s.Seconds <= 0 ? 5 : Math.Clamp(s.Seconds, 0.5, 60);
            s.Prompt ??= "";
            s.Camera ??= "";
            s.Line ??= "";
            s.Model = Clean(s.Model);
            // A line is said lip-synced: a shot given one is.
            if (s.Line.Trim().Length > 0)
                s.LipSync = true;
            s.Transition = s.Transition?.Trim().ToLowerInvariant() switch
            {
                FilmShot.Dissolve or "crossfade" or "cross-fade" or "mix" => FilmShot.Dissolve,
                FilmShot.Fade or "fade to black" or "fadeblack" or "black" => FilmShot.Fade,
                _ => FilmShot.Cut,
            };
            if (Frame(s.First) is null) s.First = null;
            if (Frame(s.Last) is null) s.Last = null;
        }
        if (Sound.Seed <= 0)
            Sound.Seed = NewSeed();
        Sound.Voice ??= "";
        Sound.Tags ??= "";
        Sound.Lyrics ??= "";
        Sound.Model = Clean(Sound.Model);
        Sound.Source = Sound.Source?.Trim().ToLowerInvariant() switch
        {
            FilmSound.FromFile or "audio" => FilmSound.FromFile,
            FilmSound.Song or "music" or "generate" or "generated" => FilmSound.Song,
            _ => FilmSound.None,
        };

        static int Even(int n) => n / 2 * 2;

        static string NextFree(string prefix, HashSet<string> seen)
        {
            for (var n = 1; ; n++)
                if (seen.Add(prefix + n))
                    return prefix + n;
        }
    }

    /// <summary>
    /// A new film in <paramref name="folder"/> (made if needed), with one shot of two frames to
    /// start from. An existing storyboard there is loaded instead.
    /// </summary>
    public static Storyboard Create(string folder, string title)
    {
        var board = New(folder, title);
        if (board.FilePath is var file && !File.Exists(file))
            board.Save();
        return board;
    }

    /// <summary>
    /// A new film for <paramref name="folder"/>, not saved yet: one shot of two frames to start
    /// from. A storyboard already there is loaded instead.
    /// </summary>
    public static Storyboard New(string folder, string title)
    {
        folder = Path.GetFullPath(folder);
        if (File.Exists(Path.Combine(folder, FileName)))
            return Load(folder);
        var board = new Storyboard { Title = title, Folder = folder };
        board.Frames.Add(new FilmFrame { Id = "f1", Seed = NewSeed() });
        board.Frames.Add(new FilmFrame { Id = "f2", Seed = NewSeed() });
        board.Shots.Add(new FilmShot { Id = "s1", First = "f1", Last = "f2", Seconds = 5, Seed = NewSeed() });
        board.Normalize();
        return board;
    }

    /// <summary>The films in <paramref name="root"/> (BlackBox's Data\films), the latest changed first.</summary>
    public static IReadOnlyList<string> Recent(string root)
    {
        try
        {
            return Directory.Exists(root)
                ? new DirectoryInfo(root).EnumerateFiles(FileName, new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 2, IgnoreInaccessible = true })
                    .OrderByDescending(f => f.LastWriteTimeUtc).Select(f => f.DirectoryName!).ToList()
                : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>
    /// Merges a change into the storyboard, as the agent writes one: fields given replace those
    /// there, frames and shots are matched by id (a new id adds one, "delete": true takes one out),
    /// and anything not given stays as it is.
    /// </summary>
    public static Storyboard Merge(Storyboard board, JsonObject change)
    {
        change = Spread(change);
        var json = board.ToJsonObject();
        foreach (var (key, value) in change)
        {
            var name = JsonKey(key);
            if (name is "frames" or "shots" && value is JsonArray items)
                json[name] = MergeList(json[name] as JsonArray ?? [], items);
            else if (name is "sound" && value is JsonObject inner && json[name] is JsonObject existing)
                foreach (var (k, v) in inner)
                    existing[JsonKey(k)] = v?.DeepClone();
            else
                json[name] = value?.DeepClone();
        }
        var merged = Parse(json.ToJsonString());
        merged.Folder = board.Folder;
        return merged;

        // A film-wide look or templates in a change go to the frames and shots that change gives,
        // never to those there before: one setting in every prompt remade everything when it changed.
        static JsonObject Spread(JsonObject change)
        {
            var keys = change.Select(p => p.Key).Where(k => JsonKey(k) is "style" or "story" or "workflows").ToList();
            if (keys.Count == 0)
                return change;
            change = (JsonObject)change.DeepClone();
            var style = keys.Where(k => JsonKey(k) == "style").Select(k => change[k]).OfType<JsonValue>().Select(v => v.TryGetValue<string>(out var s) ? s : null).FirstOrDefault();
            var workflows = keys.Where(k => JsonKey(k) == "workflows").Select(k => change[k]).OfType<JsonObject>().FirstOrDefault();
            foreach (var key in keys)
                change.Remove(key);
            string? Job(JsonObject? w, string job) =>
                w?.FirstOrDefault(p => p.Key.Equals(job, StringComparison.OrdinalIgnoreCase)).Value is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
            foreach (var (kind, item) in Items(change))
            {
                if (style is { Length: > 0 } && item["prompt"] is JsonValue p && p.TryGetValue<string>(out var prompt))
                    item["prompt"] = JoinText(prompt, style);
                if (workflows is not null && item["model"] is null)
                {
                    var job = kind == "frames"
                        ? item["from"] is JsonArray { Count: > 0 } ? "edit" : "frame"
                        : item["lipSync"] is JsonValue ls && ls.TryGetValue<bool>(out var lip) && lip ||
                          item["line"] is JsonValue lv && lv.TryGetValue<string>(out var line) && line.Trim().Length > 0 ? "lipSync"
                        : item["first"] is null ? "shotFromText" : item["last"] is null ? "shotFromFirst" : "shot";
                    if (Job(workflows, job) is { Length: > 0 } model)
                        item["model"] = model;
                }
            }
            if (Job(workflows, "song") is { Length: > 0 } song)
            {
                var given = change.FirstOrDefault(p => JsonKey(p.Key) == "sound");
                if (given.Key is null)
                    change["sound"] = new JsonObject { ["model"] = song };
                else if (given.Value is JsonObject sound && sound["model"] is null)
                    sound["model"] = song;
            }
            return change;

            static IEnumerable<(string Kind, JsonObject Item)> Items(JsonObject change) =>
                change.Where(p => JsonKey(p.Key) is "frames" or "shots").SelectMany(p => (p.Value as JsonArray ?? []).OfType<JsonObject>().Select(o => (JsonKey(p.Key), o)));
        }

        static JsonArray MergeList(JsonArray existing, JsonArray changes)
        {
            var list = existing.Select(n => n?.DeepClone()).OfType<JsonObject>().ToList();
            foreach (var item in changes.OfType<JsonObject>())
            {
                var id = (string?)item["id"];
                var at = id is null ? -1 : list.FindIndex(o => string.Equals((string?)o["id"], id, StringComparison.OrdinalIgnoreCase));
                if (item["delete"] is JsonValue d && d.TryGetValue<bool>(out var delete) && delete)
                {
                    if (at >= 0)
                        list.RemoveAt(at);
                    continue;
                }
                if (at < 0)
                {
                    var added = new JsonObject();
                    foreach (var (k, v) in item)
                        added[JsonKey(k)] = v?.DeepClone();
                    list.Add(added);
                    continue;
                }
                var hadLine = Said(list[at]["line"]);
                foreach (var (k, v) in item)
                    list[at][JsonKey(k)] = v?.DeepClone();
                // A shot whose line is taken out stops being lip-synced (else it would be lip-synced
                // to the song under it), unless the change says it stays so.
                if (hadLine && !Said(list[at]["line"]) && !item.Any(p => JsonKey(p.Key) == "lipSync"))
                    list[at]["lipSync"] = false;
            }
            return new JsonArray(list.Cast<JsonNode>().ToArray());
        }

        static bool Said(JsonNode? line) => line is JsonValue v && v.TryGetValue<string>(out var s) && s.Trim().Length > 0;

        // Written as the file has them (camelCase), whatever case the change used.
        static string JsonKey(string key) => key.Length == 0 ? key : char.ToLowerInvariant(key[0]) + key[1..];
    }
}

public sealed class FilmFrame
{
    public string Id { get; set; } = "";

    /// <summary>What a reference is called ("Mira", "the lighthouse"); frames in shots may have one too.</summary>
    public string? Name { get; set; }

    /// <summary>A character, place or thing other frames are made from, not a frame of a shot.</summary>
    public bool Reference { get; set; }

    /// <summary>What the picture shows, and how it looks.</summary>
    public string Prompt { get; set; } = "";

    /// <summary>
    /// What it is made with: a model or workflow in the user's words ("furry nova xl", "my pony
    /// workflow"), matched against what ComfyUI has installed, or a template's name; none: the best
    /// installed one (<see cref="FilmTemplates.PickAsync"/>).
    /// </summary>
    public string? Model { get; set; }

    public long Seed { get; set; }

    /// <summary>Frames this one is made from, so the same people and places come out the same: an edit model takes them.</summary>
    public List<string> From { get; set; } = [];

    /// <summary>A picture of the user's own to use instead of making one.</summary>
    public string? File { get; set; }

    /// <summary>Changes to the workflow after the storyboard's own, as the comfyui tool's set takes them: {"node id.input": value}.</summary>
    public JsonObject? Set { get; set; }

    /// <summary>"Frame 3", or its name.</summary>
    public string Label => Name is { Length: > 0 } name ? name : Id;
}

public sealed class FilmShot
{
    public const string Cut = "cut", Dissolve = "dissolve", Fade = "fade";

    public string Id { get; set; } = "";

    /// <summary>The frame it starts on; the last frame of the shot before when the camera goes on from there.</summary>
    public string? First { get; set; }

    /// <summary>The frame it ends on, or none: then it goes where its prompt takes it.</summary>
    public string? Last { get; set; }

    public double Seconds { get; set; } = 5;

    /// <summary>What happens in it.</summary>
    public string Prompt { get; set; } = "";

    /// <summary>What the camera does ("slow push in", "pans left", "still").</summary>
    public string Camera { get; set; } = "";

    /// <summary>What it is made with, as <see cref="FilmFrame.Model"/>: a video model or workflow.</summary>
    public string? Model { get; set; }

    public long Seed { get; set; }

    /// <summary>
    /// Lip-synced: made with a sound under it, so the people in it say or sing it: its own line when
    /// it has one, else the soundtrack's part under it.
    /// </summary>
    public bool LipSync { get; set; }

    /// <summary>What is said in it, in the film's voice (<see cref="FilmSound.Voice"/>), lip-synced.</summary>
    public string Line { get; set; } = "";

    /// <summary>How it follows the shot before: cut, dissolve or fade (through black).</summary>
    public string Transition { get; set; } = Cut;

    /// <summary>Changes to the workflow after the storyboard's own, as the comfyui tool's set takes them.</summary>
    public JsonObject? Set { get; set; }
}

public sealed class FilmSound
{
    public const string None = "none", FromFile = "file", Song = "song";

    /// <summary>none, file (the user's) or song (made with a music model).</summary>
    public string Source { get; set; } = None;

    public string? File { get; set; }

    /// <summary>The song's style: genre, instruments, voice, tempo ("dreamy synthwave, female vocals, 90 BPM").</summary>
    public string Tags { get; set; } = "";

    /// <summary>The words sung; none makes an instrumental.</summary>
    public string Lyrics { get; set; } = "";

    /// <summary>The voice the shots' lines are said in ("a man in his forties, warm and a little hoarse, deadpan").</summary>
    public string Voice { get; set; } = "";

    /// <summary>What the song is made with, as <see cref="FilmFrame.Model"/>: a music model or workflow.</summary>
    public string? Model { get; set; }

    public long Seed { get; set; }

    public JsonObject? Set { get; set; }
}

/// <summary>
/// The ComfyUI templates a film was made with, by the job, from storyboards before each frame, shot
/// and song said what it is made with; only read, to move them there (<see cref="Storyboard.Upgrade"/>).
/// </summary>
public sealed class FilmWorkflows
{
    /// <summary>A picture from words.</summary>
    public string? Frame { get; set; }

    /// <summary>A picture from other pictures (the same character elsewhere): an edit model.</summary>
    public string? Edit { get; set; }

    /// <summary>A video from its first and last frame.</summary>
    public string? Shot { get; set; }

    /// <summary>A video from its first frame only.</summary>
    public string? ShotFromFirst { get; set; }

    /// <summary>A video from words only.</summary>
    public string? ShotFromText { get; set; }

    /// <summary>A video from its first frame and a sound, the people in it singing or speaking it.</summary>
    public string? LipSync { get; set; }

    /// <summary>A song from a style and lyrics.</summary>
    public string? Song { get; set; }
}

/// <summary>Something in a film that can't be done as asked, said so the user or the model can fix it.</summary>
public sealed class FilmException(string message) : Exception(message);

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault, IgnoreReadOnlyProperties = true, AllowTrailingCommas = true,
    ReadCommentHandling = JsonCommentHandling.Skip, NumberHandling = JsonNumberHandling.AllowReadingFromString)]
[JsonSerializable(typeof(Storyboard))]
[JsonSerializable(typeof(FilmFrame))]
[JsonSerializable(typeof(FilmShot))]
[JsonSerializable(typeof(FilmSound))]
internal sealed partial class StoryboardJson : JsonSerializerContext;
