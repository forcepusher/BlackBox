using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace BlackBox.Core.Film;

/// <summary>
/// The film is its folder (the user's call, 2026-10-24: "I want the entire film to be
/// folder-driven"; there is no storyboard.json), and its title is the folder's name:
/// <code>
/// Fox Film/
///   Fox Film.mp4 (+ .json)       the joined film; its .json: size, frame rate, the shots' order
///   References/                  a folder for each reference, with its pictures and the user's own
///     Fox/Fox.png (+ .json, Fox.workflow.json)
///   1 Intro/                     a folder per shot, numbered in the film's order
///     Intro start.png (+ .json, Intro start.workflow.json)
///     Intro end.png   (+ .json, Intro end.workflow.json)
///     Intro.mp4       (+ .json, Intro.workflow.json)
///     Takes/                     earlier versions, each with its .json and workflow
///   Sound/song.mp3 (+ .json, song.workflow.json)
/// </code>
/// Each piece's .json (named as its file, "Intro.json" for "Intro.mp4": the user didn't want a
/// .meta) holds what it is (its kind, id, prompt, seed, model, what it is made from) and, once
/// made, how ("made": the stamp of its settings, the template, the look's verdict); the workflow
/// beside it is ComfyUI's API format, which ComfyUI opens as it is. A piece is where its .json is:
/// moved or renamed with it in Explorer, it stays in the film; its .json deleted, it leaves it.
/// Reading gathers them into one storyboard; saving gives each piece its place and writes them back.
/// Films from before folders aren't read (the user's call: no one is to be carried over).
/// </summary>
public sealed partial class Storyboard
{
    /// <summary>Where a piece's earlier versions are kept, in its folder.</summary>
    public const string TakesName = "Takes";

    /// <summary>The folder of the frames in no shot that aren't references either.</summary>
    public const string FramesName = "Frames";

    private const string WorkflowExtension = ".workflow.json";

    /// <summary>What a file is and how it was made, beside it: "Intro.json" for "Intro.mp4".</summary>
    public static string MetaOf(string file) => Path.ChangeExtension(file, ".json");

    /// <summary>The ComfyUI workflow that made a file, beside it: "Intro.workflow.json".</summary>
    public static string WorkflowOf(string file) => Path.Combine(Path.GetDirectoryName(file)!, Path.GetFileNameWithoutExtension(file) + WorkflowExtension);

    /// <summary>Whether a file is a workflow beside a piece, not a piece's .json.</summary>
    public static bool IsWorkflow(string file) => file.EndsWith(WorkflowExtension, StringComparison.OrdinalIgnoreCase);

    private string Full(string place) => Path.GetFullPath(Path.Combine(Folder, place.Replace('/', Path.DirectorySeparatorChar)));

    private static string? DirOf(string place) => place.LastIndexOf('/') is var at and > 0 ? place[..at] : null;

    // ---- where each piece goes ----------------------------------------------------------------

    /// <summary>A shot's name as its folder and files have it: its own, or "Shot".</summary>
    private static string NameOf(FilmShot shot) => Safe(shot.Name is { Length: > 0 } name ? name : "Shot");

    /// <summary>A shot's folder: its place in the film and its name, "2 The leap".</summary>
    private string FolderNameOf(FilmShot shot) => $"{Shots.IndexOf(shot) + 1} {NameOf(shot)}";

    /// <summary>Where a shot's clip goes, for one not placed yet: "2 The leap/The leap.mp4".</summary>
    public string PlaceFor(FilmShot shot) => $"{FolderNameOf(shot)}/{NameOf(shot)}.mp4";

    /// <summary>
    /// The shot a frame belongs to: the first that ends on it (the frame a shot after starts on is
    /// the end of the one before), else the first that starts on it; null for a frame in no shot.
    /// </summary>
    private (FilmShot Shot, string Part)? OwnerOf(FilmFrame frame) =>
        Shots.FirstOrDefault(s => !s.LipSync && string.Equals(s.Last, frame.Id, StringComparison.OrdinalIgnoreCase)) is { } ends ? (ends, "end")
        : Shots.FirstOrDefault(s => string.Equals(s.First, frame.Id, StringComparison.OrdinalIgnoreCase)) is { } starts ? (starts, "start")
        : null;

    /// <summary>
    /// Where a frame's picture goes, for one not placed yet: a reference's in its folder in References
    /// ("Mira" in "References/Mira/Mira.png", "Mira/side" in "References/Mira/side.png"), a shot's
    /// frame in the shot's folder, any other in Frames.
    /// </summary>
    public string PlaceFor(FilmFrame frame)
    {
        if (frame.Reference)
        {
            var parts = (frame.Name is { Length: > 0 } name ? name : frame.Id).Split('/', '\\', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(Safe).ToList();
            if (parts.Count == 0)
                parts.Add(Safe(frame.Id));
            return $"{ReferencesName}/{parts[0]}/{(parts.Count > 1 ? string.Join(" ", parts.Skip(1)) : parts[0])}.png";
        }
        if (OwnerOf(frame) is { } owner)
            return $"{DirOf(owner.Shot.Place ?? PlaceFor(owner.Shot)) ?? FolderNameOf(owner.Shot)}/{NameOf(owner.Shot)} {owner.Part}.png";
        return $"{FramesName}/{Safe(frame.Id)}.png";
    }

    // ---- reading ----------------------------------------------------------------------------------

    /// <summary>Whether a folder holds a film: the .json of the film at its root.</summary>
    public static bool IsFilm(string folder)
    {
        try
        {
            return Directory.Exists(folder) && FilmMetaIn(folder) is not null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    /// <summary>The .json of the film at a folder's root (the joined film's, "Fox Film.json"); null when there is none.</summary>
    private static string? FilmMetaIn(string folder) =>
        MetasIn(folder).Where(m => m.Kind == "film").Select(m => m.Meta).OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();

    /// <summary>The film's folder for a path: the folder, or the folder of a file of it.</summary>
    private static string FolderOf(string path)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        return File.Exists(full) && !Directory.Exists(full) ? Path.GetDirectoryName(full)! : full;
    }

    /// <summary>The film in a folder (or of a file in it).</summary>
    public static Storyboard Load(string path)
    {
        var folder = FolderOf(path);
        if (!IsFilm(folder))
            throw new FilmException($"There is no film in {folder}.");
        return ReadFolder(folder);
    }

    /// <summary>
    /// The pieces' .json files in a film's folder, with their kind: at its root, in its folders and
    /// in theirs (a reference's folder in References), not in Takes.
    /// </summary>
    private static IEnumerable<(string Meta, string Kind, JsonObject Json)> Metas(string folder)
    {
        var dirs = new List<string> { folder };
        for (var i = 0; i < dirs.Count; i++)
        {
            if (Path.GetRelativePath(folder, dirs[i]).Split(Path.DirectorySeparatorChar).Length >= 2 && dirs[i] != folder)
                continue;
            try
            {
                dirs.AddRange(Directory.EnumerateDirectories(dirs[i]).Where(d => !Path.GetFileName(d).StartsWith('.') && !Path.GetFileName(d).Equals(TakesName, StringComparison.OrdinalIgnoreCase)));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return dirs.SelectMany(MetasIn);
    }

    /// <summary>The pieces' .json files in one folder: a .json with a kind, not a workflow.</summary>
    private static IEnumerable<(string Meta, string Kind, JsonObject Json)> MetasIn(string dir)
    {
        List<string> files;
        try { files = Directory.EnumerateFiles(dir, "*.json").Where(f => !IsWorkflow(f) && !Path.GetFileName(f).StartsWith('.')).ToList(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { yield break; }
        foreach (var meta in files)
        {
            JsonObject? json = null;
            try { json = JsonNode.Parse(File.ReadAllText(meta), documentOptions: Lenient) as JsonObject; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
            if (json?["kind"] is JsonValue k && k.TryGetValue<string>(out var kind))
                yield return (meta, kind, json);
        }
    }

    private static readonly JsonDocumentOptions Lenient = new() { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip };

    /// <summary>
    /// The file a piece's .json is beside: of its name, with the extension its kind has (a frame's
    /// picture as it is there, else a PNG; a shot's or the film's MP4).
    /// </summary>
    private static string FileOf(string meta, string kind)
    {
        var stem = Path.Combine(Path.GetDirectoryName(meta)!, Path.GetFileNameWithoutExtension(meta));
        if (kind == "frame")
            return PictureExtensions.Select(e => stem + e).FirstOrDefault(File.Exists) ?? stem + ".png";
        return stem + ".mp4";
    }

    /// <summary>The film from its folder: each piece from its .json, where the .json is; the shots in the order the film's .json gives.</summary>
    private static Storyboard ReadFolder(string folder)
    {
        JsonObject? film = null;
        var frames = new List<JsonObject>();
        var shots = new List<JsonObject>();
        JsonObject? sound = null;
        foreach (var (meta, kind, json) in Metas(folder))
        {
            var settings = (JsonObject)json.DeepClone();
            settings.Remove("kind");
            settings.Remove("made");
            settings["place"] = Path.GetRelativePath(folder, FileOf(meta, kind)).Replace(Path.DirectorySeparatorChar, '/');
            switch (kind)
            {
                case "film" when film is null && Path.GetDirectoryName(meta) == folder:
                    film = settings;
                    break;
                case "frame":
                    frames.Add(settings);
                    break;
                case "shot":
                    shots.Add(settings);
                    break;
                case "sound" when sound is null:
                    settings.Remove("place");
                    sound = settings;
                    break;
            }
        }
        // The shots in the film's order; one whose folder the film's .json doesn't list (copied in) after them, by its folder.
        var order = (film?["shots"] as JsonArray ?? []).Select(n => n?.ToString() ?? "").ToList();
        int At(JsonObject shot) => order.FindIndex(id => string.Equals(id, shot["id"]?.ToString(), StringComparison.OrdinalIgnoreCase)) is var i and >= 0 ? i : int.MaxValue;
        shots = shots.OrderBy(At).ThenBy(s => s["place"]?.ToString(), NaturalOrder.Instance).ToList();
        var root = new JsonObject();
        foreach (var (key, value) in film ?? [])
            if (key is not ("shots" or "place"))
                root[key] = value?.DeepClone();
        // Its title is its folder's name: renamed in Explorer or in the panel, the film is.
        root["title"] = Path.GetFileName(folder);
        root["frames"] = new JsonArray([.. frames.OrderBy(f => f["place"]?.ToString(), NaturalOrder.Instance)]);
        root["shots"] = new JsonArray([.. shots]);
        if (sound is not null)
            root["sound"] = sound;
        var board = Parse(root.ToJsonString());
        board.Folder = folder;
        // The frames as a person reads the film: the references, then each shot's, then the rest.
        var shotAt = board.Shots.Select((s, i) => (s, i)).ToDictionary(p => p.s.Id, p => p.i, StringComparer.OrdinalIgnoreCase);
        int FrameAt(FilmFrame f) => f.Reference ? -1 : board.OwnerOf(f) is { } owner ? shotAt[owner.Shot.Id] : int.MaxValue;
        board.Frames = [.. board.Frames.OrderBy(FrameAt).ThenBy(f => board.OwnerOf(f)?.Part == "end" ? 1 : 0)];
        return board;
    }

    // ---- writing ----------------------------------------------------------------------------------

    /// <summary>
    /// Writes the film into its folder: each piece's .json where it goes (see <see cref="Arrange"/>),
    /// the film's own .json at the root, and the References folder for the user's pictures. The
    /// .json of a piece no longer in the film (or placed elsewhere before it was made) goes, or it
    /// would come back; its files stay.
    /// </summary>
    public void Save()
    {
        if (Folder.Length == 0)
            throw new InvalidOperationException("The film has no folder to be saved in.");
        // Named after its folder, as it is read: a new title renames the folder first (Rename).
        Title = Path.GetFileName(Path.TrimEndingDirectorySeparator(Folder));
        Directory.CreateDirectory(Folder);
        NameJoined(Folder, Title);
        Arrange();
        var written = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var shot in Shots)
            written.Add(WriteMeta(ShotMeta(shot), "shot", SettingsOf(shot)));
        foreach (var frame in Frames)
            written.Add(WriteMeta(FrameMeta(frame), "frame", SettingsOf(frame)));
        written.Add(WriteMeta(SongMeta, "sound", SettingsOf(Sound)));
        written.Add(WriteMeta(FilmMeta, "film", new JsonObject
        {
            ["width"] = Width,
            ["height"] = Height,
            ["fps"] = Fps,
            ["shots"] = new JsonArray([.. Shots.Select(s => (JsonNode)JsonValue.Create(s.Id))]),
        }));
        foreach (var (meta, kind, _) in Metas(Folder).ToList())
            if (kind is "film" or "frame" or "shot" or "sound" && !written.Contains(Path.GetFullPath(meta)))
                try { File.Delete(meta); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        Directory.CreateDirectory(ReferencesFolder);
    }

    /// <summary>A piece's settings as its .json holds them: as the storyboard has it, without its place (the .json's own).</summary>
    private static JsonObject SettingsOf<T>(T piece)
    {
        var json = JsonSerializer.SerializeToNode(piece, typeof(T), Readable) as JsonObject ?? [];
        json.Remove("place");
        return json;
    }

    /// <summary>Writes a piece's .json (what it says of how the file was made kept), when it says something new; its full path.</summary>
    private static string WriteMeta(string path, string kind, JsonObject settings)
    {
        path = Path.GetFullPath(path);
        var meta = new JsonObject { ["kind"] = kind };
        foreach (var (key, value) in settings)
            meta[key] = value?.DeepClone();
        string? before = null;
        try
        {
            if (File.Exists(path))
            {
                before = File.ReadAllText(path);
                if (JsonNode.Parse(before, documentOptions: Lenient)?["made"] is { } made)
                    meta["made"] = made.DeepClone();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
        var text = meta.ToJsonString(Comfy.ComfyWorkflow.Indented);
        if (text != before)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            Settings.AtomicFile.Write(path, text);
        }
        return path;
    }

    /// <summary>
    /// Gives each piece its place before it is saved. A shot's folder is named after its place in
    /// the film and its name, and renamed to follow them (not while a piece of the film is being
    /// made: the job writes into it). A frame goes in the folder of the shot it ends or starts (a
    /// reference in its folder in References, any other in Frames), named after its part there. A piece made
    /// stays where its file is; one not made yet goes where the film's order and names say, never
    /// onto a file a piece taken out left there.
    /// </summary>
    private void Arrange()
    {
        var busy = FilmMaker.IsMaking(Folder);
        var given = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var shot in Shots)
        {
            var want = FolderNameOf(shot);
            var dir = shot.Place is { } place ? DirOf(place) : null;
            if (dir is not null && Directory.Exists(Full(dir)))
            {
                if (!dir.Equals(want, StringComparison.OrdinalIgnoreCase) && !busy && !Directory.Exists(Full(want)) && !given.Contains(want))
                    try
                    {
                        Directory.Move(Full(dir), Full(want));
                        foreach (var piece in Frames.Select(f => (Get: (Func<string?>)(() => f.Place), Set: (Action<string>)(p => f.Place = p)))
                                     .Concat(Shots.Select(s => (Get: (Func<string?>)(() => s.Place), Set: (Action<string>)(p => s.Place = p)))))
                            if (piece.Get() is { } p && p.StartsWith(dir + "/", StringComparison.OrdinalIgnoreCase))
                                piece.Set(want + p[dir.Length..]);
                        dir = want;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
            else
            {
                // Not one a shot taken out left: its files would be taken for this one's.
                dir = want;
                for (var n = 2; Directory.Exists(Full(dir)) || given.Contains(dir); n++)
                    dir = $"{want} ({n})";
            }
            given.Add(dir!);
            if (shot.Place is not { } made || !made.StartsWith(dir + "/", StringComparison.OrdinalIgnoreCase) || !File.Exists(Full(made)))
                shot.Place = $"{dir}/{NameOf(shot)}.mp4";
        }
        var placed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var frame in Frames)
        {
            if (frame.Place is { } place && File.Exists(Full(place)))
            {
                placed.Add(place);
                continue;
            }
            var want = PlaceFor(frame);
            var free = want;
            for (var n = 2; placed.Contains(free) || File.Exists(Full(free)) && !string.Equals(free, frame.Place, StringComparison.OrdinalIgnoreCase); n++)
                free = $"{want[..^4]} {n}.png";
            frame.Place = free;
            placed.Add(free);
        }
    }

    // ---- renaming and taking out --------------------------------------------------------------

    /// <summary>
    /// Renames the film: its folder, named after it, and the joined film and its .json in it (the
    /// user's call, 2026-10-24: films are folders, renamed as in Explorer). Not while a piece of
    /// it is being made: the job writes into the folder.
    /// </summary>
    public void Rename(string title)
    {
        if (FilmMaker.IsMaking(Folder))
            throw new FilmException("A piece of the film is being made: rename it once that is done.");
        Folder = Rename(Folder, title);
        Title = Path.GetFileName(Folder);
    }

    /// <summary>
    /// Renames a film's folder, with the joined film and its .json, which are named after it; the
    /// new folder. A film whose folder isn't made yet only gets the new name.
    /// </summary>
    public static string Rename(string folder, string title)
    {
        folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        var name = Safe(title);
        var to = Path.Combine(Path.GetDirectoryName(folder)!, name);
        if (string.Equals(to, folder, StringComparison.Ordinal))
            return folder;
        // Only the case changed: the same folder, renamed.
        if (!string.Equals(to, folder, StringComparison.OrdinalIgnoreCase) && (Directory.Exists(to) || File.Exists(to)))
            throw new FilmException($"There is a {name} there already.");
        if (Directory.Exists(folder))
        {
            Directory.Move(folder, to);
            NameJoined(to, name);
        }
        return to;
    }

    /// <summary>The joined film and its .json named as the film (renamed in Explorer, the folder left them under the old name).</summary>
    private static void NameJoined(string folder, string name)
    {
        try
        {
            if (FilmMetaIn(folder) is not { } meta || Path.GetFileNameWithoutExtension(meta) == name)
                return;
            foreach (var (from, to) in new[] { (FileOf(meta, "film"), Path.Combine(folder, name + ".mp4")), (meta, Path.Combine(folder, name + ".json")) })
                if (File.Exists(from) && (!File.Exists(to) || string.Equals(from, to, StringComparison.OrdinalIgnoreCase)))
                    File.Move(from, to);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>
    /// Takes a shot out of the film with its folder, which goes to <paramref name="remove"/> (the
    /// Recycle Bin), and the frames only it starts or ends on. A frame in its folder that stays (the
    /// end the next shot goes on from) moves out first, with its .json, workflow and takes, to
    /// where it goes now. The film is to be saved then; when <paramref name="remove"/> fails, read
    /// again: what is on the disk is a film still.
    /// </summary>
    public void DeleteShot(FilmShot shot, Action<string> remove)
    {
        var folder = Path.GetFullPath(ShotFolder(shot));
        var own = new[] { shot.First, shot.Last }.OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
        var used = Shots.Where(s => s != shot).SelectMany(s => new[] { s.First, s.Last }).OfType<string>()
            .Concat(Frames.Where(f => !own.Contains(f.Id)).SelectMany(f => f.From))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        Shots.Remove(shot);
        Frames.RemoveAll(f => own.Contains(f.Id) && !f.Reference && !used.Contains(f.Id));
        foreach (var frame in Frames.Where(f => In(FramePicture(f))).ToList())
        {
            var from = FramePicture(frame);
            frame.Place = null;
            var want = FramePicture(frame);
            if (!File.Exists(from) || In(want))
                continue;
            var to = Path.ChangeExtension(want, Path.GetExtension(from));
            for (var n = 2; File.Exists(to); n++)
                to = Path.Combine(Path.GetDirectoryName(want)!, $"{Path.GetFileNameWithoutExtension(want)} {n}{Path.GetExtension(from)}");
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            foreach (var (source, target) in new[] { (from, to), (MetaOf(from), MetaOf(to)), (WorkflowOf(from), WorkflowOf(to)) })
                if (File.Exists(source))
                    File.Move(source, target);
            // Its takes, "Takes/Shot end.v1.png" with theirs, go with it.
            var (stem, takes) = (Path.GetFileNameWithoutExtension(from), Path.Combine(Path.GetDirectoryName(from)!, TakesName));
            if (Directory.Exists(takes))
                foreach (var take in Directory.EnumerateFiles(takes, stem + ".v*").ToList())
                {
                    var into = Path.Combine(Path.GetDirectoryName(to)!, TakesName);
                    Directory.CreateDirectory(into);
                    File.Move(take, Path.Combine(into, Path.GetFileNameWithoutExtension(to) + Path.GetFileName(take)[stem.Length..]));
                }
            frame.Place = Path.GetRelativePath(Folder, to).Replace(Path.DirectorySeparatorChar, '/');
        }
        Normalize();
        // A shot's own folder only, never the film's.
        if (Directory.Exists(folder) && folder.StartsWith(Path.GetFullPath(Folder) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            remove(folder);

        bool In(string path) => Path.GetFullPath(path).StartsWith(folder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The films in <paramref name="root"/> (BlackBox's Data\films), the latest changed first.</summary>
    public static IReadOnlyList<string> Recent(string root)
    {
        try
        {
            if (!Directory.Exists(root))
                return [];
            var films = new List<(string Folder, DateTime Changed)>();
            foreach (var dir in Directory.EnumerateDirectories(root).SelectMany(d => new[] { d }.Concat(SubFolders(d))))
                if (IsFilm(dir))
                    films.Add((dir, FilmMetaIn(dir) is { } meta ? File.GetLastWriteTimeUtc(meta) : Directory.GetLastWriteTimeUtc(dir)));
            return films.OrderByDescending(f => f.Changed).Select(f => f.Folder).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }

        // A folder of films inside the films folder ("Ads/Coffee"), not the films' own folders.
        static IEnumerable<string> SubFolders(string dir)
        {
            try { return IsFilm(dir) ? [] : Directory.EnumerateDirectories(dir).ToList(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
        }
    }
}

/// <summary>Names in the order a person counts them: "2 Leap" before "10 Howl".</summary>
internal sealed partial class NaturalOrder : IComparer<string?>
{
    public static readonly NaturalOrder Instance = new();

    public int Compare(string? x, string? y)
    {
        if (x is null || y is null)
            return string.Compare(x, y, StringComparison.OrdinalIgnoreCase);
        var (a, b) = (Number().Match(x), Number().Match(y));
        if (a.Success && b.Success && long.TryParse(a.Value, out var m) && long.TryParse(b.Value, out var n) && m != n)
            return m.CompareTo(n);
        return string.Compare(x, y, StringComparison.OrdinalIgnoreCase);
    }

    [GeneratedRegex(@"^\d+")]
    private static partial Regex Number();
}
