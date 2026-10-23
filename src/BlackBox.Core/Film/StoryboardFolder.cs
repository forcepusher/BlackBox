using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace BlackBox.Core.Film;

/// <summary>
/// The film is its folder (the user's call, 2026-10-24: "I want the entire film to be
/// folder-driven"; storyboard.json is gone):
/// <code>
/// Fox Film/
///   Fox Film.mp4 (+ .meta)       the joined film; its meta: title, size, frame rate, sound levels, the shots' order
///   References/                  the references' pictures and the user's own
///   1 Intro/                     a folder per shot, numbered in the film's order
///     Intro start.png (+ .meta, Intro start.workflow.json)
///     Intro end.png   (+ .meta, Intro end.workflow.json)
///     Intro.mp4       (+ .meta, Intro.workflow.json)
///     Takes/                     earlier versions, each with its meta and workflow
///   Sound/song.mp3 (+ .meta, song.workflow.json)
/// </code>
/// Each piece's .meta (JSON) holds what it is (its id, prompt, seed, model, what it is made from)
/// and, once made, how ("made": the stamp of its settings, the template, the look's verdict); the
/// workflow beside it is ComfyUI's API format, which ComfyUI opens as it is. A piece is where its
/// .meta is: moved or renamed with it in Explorer, it stays in the film; its .meta deleted, it
/// leaves it. Reading gathers the metas into one storyboard; saving gives each piece its place and
/// writes them back.
/// </summary>
public sealed partial class Storyboard
{
    /// <summary>Where a piece's earlier versions are kept, in its folder.</summary>
    public const string TakesName = "Takes";

    /// <summary>The folder of the frames in no shot that aren't references either.</summary>
    public const string FramesName = "Frames";

    public const string MetaExtension = ".meta";

    /// <summary>What a file is and how it was made, beside it: "Intro.mp4.meta".</summary>
    public static string MetaOf(string file) => file + MetaExtension;

    /// <summary>The ComfyUI workflow that made a file, beside it: "Intro.workflow.json".</summary>
    public static string WorkflowOf(string file) => Path.Combine(Path.GetDirectoryName(file)!, Path.GetFileNameWithoutExtension(file) + ".workflow.json");

    private string Full(string place) => Path.GetFullPath(Path.Combine(Folder, place.Replace('/', Path.DirectorySeparatorChar)));

    private string Relative(string full) => Path.GetRelativePath(Folder, full).Replace(Path.DirectorySeparatorChar, '/');

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

    /// <summary>Where a frame's picture goes, for one not placed yet: in its shot's folder, References, or Frames.</summary>
    public string PlaceFor(FilmFrame frame)
    {
        if (frame.Reference)
            return $"{ReferencesName}/{Safe(frame.Name is { Length: > 0 } name ? name : frame.Id)}.png";
        if (OwnerOf(frame) is { } owner)
            return $"{DirOf(owner.Shot.Place ?? PlaceFor(owner.Shot)) ?? FolderNameOf(owner.Shot)}/{NameOf(owner.Shot)} {owner.Part}.png";
        return $"{FramesName}/{Safe(frame.Id)}.png";
    }

    // ---- reading ----------------------------------------------------------------------------------

    /// <summary>Whether a folder holds a film: the meta of the film at its root, or the storyboard.json of one from before.</summary>
    public static bool IsFilm(string folder)
    {
        try
        {
            return Directory.Exists(folder) && (FilmMetaIn(folder) is not null || File.Exists(Path.Combine(folder, OldFileName)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    /// <summary>The meta of the film at a folder's root (the joined film's, "Fox Film.mp4.meta"); null when there is none.</summary>
    private static string? FilmMetaIn(string folder) =>
        Directory.EnumerateFiles(folder, "*.mp4" + MetaExtension).Where(f => !Path.GetFileName(f).StartsWith('.')).OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();

    /// <summary>The film's folder for a path: the folder, or the folder of a file of it (its storyboard.json from before, a meta).</summary>
    private static string FolderOf(string path)
    {
        var full = Path.GetFullPath(path);
        return File.Exists(full) && !Directory.Exists(full) ? Path.GetDirectoryName(full)! : full;
    }

    /// <summary>
    /// The film in a folder (or of a file in it). One kept in a storyboard.json, from before, is
    /// turned into folders and metas, what was made staying made (<paramref name="upgrade"/> false
    /// only reads it, for its title).
    /// </summary>
    public static Storyboard Load(string path, bool upgrade = true)
    {
        var folder = FolderOf(path);
        if (!IsFilm(folder))
            throw new FilmException($"There is no film in {folder}.");
        if (FilmMetaIn(folder) is null)
            return upgrade ? Convert(folder) : ReadOld(folder);
        return ReadFolder(folder);
    }

    /// <summary>The metas of a film's pieces, with their kind: in its folder and the folders in it, not in Takes.</summary>
    private static IEnumerable<(string Meta, string Kind, JsonObject Json)> Metas(string folder)
    {
        var dirs = new List<string> { folder };
        try
        {
            dirs.AddRange(Directory.EnumerateDirectories(folder).Where(d => !Path.GetFileName(d).StartsWith('.') && !Path.GetFileName(d).Equals(TakesName, StringComparison.OrdinalIgnoreCase)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        foreach (var dir in dirs)
        {
            IEnumerable<string> metas;
            try { metas = Directory.EnumerateFiles(dir, "*" + MetaExtension).ToList(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            foreach (var meta in metas)
            {
                JsonObject? json = null;
                try { json = JsonNode.Parse(File.ReadAllText(meta), documentOptions: Lenient) as JsonObject; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
                if (json?["kind"] is JsonValue k && k.TryGetValue<string>(out var kind))
                    yield return (meta, kind, json);
            }
        }
    }

    private static readonly JsonDocumentOptions Lenient = new() { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip };

    /// <summary>The film from its folder: each piece from its meta, where the meta is; the shots in the order the film's meta gives.</summary>
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
            settings["place"] = Path.GetRelativePath(folder, meta[..^MetaExtension.Length]).Replace(Path.DirectorySeparatorChar, '/');
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
        // The shots in the film's order; one whose folder the film's meta doesn't list (copied in) after them, by its folder.
        var order = (film?["shots"] as JsonArray ?? []).Select(n => n?.ToString() ?? "").ToList();
        int At(JsonObject shot) => order.FindIndex(id => string.Equals(id, shot["id"]?.ToString(), StringComparison.OrdinalIgnoreCase)) is var i and >= 0 ? i : int.MaxValue;
        shots = shots.OrderBy(At).ThenBy(s => s["place"]?.ToString(), NaturalOrder.Instance).ToList();
        var root = new JsonObject();
        foreach (var (key, value) in film ?? [])
            if (key is not ("shots" or "place"))
                root[key] = value?.DeepClone();
        root["title"] ??= Path.GetFileName(folder);
        root["frames"] = new JsonArray([.. frames.OrderBy(f => f["place"]?.ToString(), NaturalOrder.Instance)]);
        root["shots"] = new JsonArray([.. shots]);
        if (sound is not null)
            root["sound"] = sound;
        var board = Parse(root.ToJsonString(), upgrade: false);
        board.Folder = folder;
        // The frames as a person reads the film: the references, then each shot's, then the rest.
        var shotAt = board.Shots.Select((s, i) => (s, i)).ToDictionary(p => p.s.Id, p => p.i, StringComparer.OrdinalIgnoreCase);
        int FrameAt(FilmFrame f) => f.Reference ? -1 : board.OwnerOf(f) is { } owner ? shotAt[owner.Shot.Id] : int.MaxValue;
        board.Frames = [.. board.Frames.OrderBy(FrameAt).ThenBy(f => board.OwnerOf(f)?.Part == "end" ? 1 : 0)];
        return board;
    }

    /// <summary>A film kept in a storyboard.json, from before, as it is: its files where they were.</summary>
    private static Storyboard ReadOld(string folder)
    {
        var board = Parse(File.ReadAllText(Path.Combine(folder, OldFileName)), upgrade: false);
        board.Folder = folder;
        foreach (var frame in board.Frames)
            frame.Place = $"frames/{Safe(frame.Id)}.png";
        foreach (var shot in board.Shots)
            shot.Place = $"shots/{Safe(shot.Id)}.mp4";
        return board;
    }

    /// <summary>
    /// Turns a film kept in a storyboard.json, from before, into folders and metas: each piece's
    /// record (the workflow file that held its stamp and ComfyUI workflow) becomes its meta and its
    /// workflow, the pieces move into their folders with their takes (a file moved keeps its time
    /// and size, so what was made from it stays made), and the storyboard.json goes.
    /// </summary>
    private static Storyboard Convert(string folder)
    {
        var board = ReadOld(folder);
        foreach (var dir in new[] { "frames", "shots", "sound" }.Select(d => Path.Combine(folder, d)).Where(Directory.Exists))
            foreach (var record in Directory.EnumerateFiles(dir, "*.json").ToList())
            {
                var stem = Path.GetFileNameWithoutExtension(record);
                var main = Path.Combine(dir, stem + (Path.GetFileName(dir).ToLowerInvariant() switch
                {
                    "shots" => stem.EndsWith(".line", StringComparison.OrdinalIgnoreCase) || stem.Contains(".line.v", StringComparison.OrdinalIgnoreCase) ? ".flac" : ".mp4",
                    "sound" => ".mp3",
                    _ => ".png",
                }));
                FilmMaker.FromRecord(record, main);
            }
        if (board.IsOld)
            FilmMaker.Upgrade(board, save: false);
        foreach (var shot in board.Shots)
            board.Move(shot.Place!, shot.Place = board.PlaceFor(WithoutPlace(shot)));
        foreach (var frame in board.Frames)
            board.Move(frame.Place!, frame.Place = board.PlaceFor(Unplaced(frame)));
        // The song stays where it was ("sound" is "Sound"); its takes go into Takes.
        board.Move(Path.GetRelativePath(folder, board.SongFile).Replace('\\', '/'), Path.GetRelativePath(folder, board.SongFile).Replace('\\', '/'));
        board.Save();
        try
        {
            File.Delete(Path.Combine(folder, OldFileName));
            foreach (var old in new[] { "frames", "shots" }.Select(d => Path.Combine(folder, d)))
                if (Directory.Exists(old) && !Directory.EnumerateFileSystemEntries(old).Any())
                    Directory.Delete(old);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return board;

        static FilmShot WithoutPlace(FilmShot shot)
        {
            shot.Place = null;
            return shot;
        }

        static FilmFrame Unplaced(FilmFrame frame)
        {
            frame.Place = null;
            return frame;
        }
    }

    // ---- moving a piece's files ---------------------------------------------------------------------

    /// <summary>
    /// Moves a piece's files from one place to another: its file, meta and workflow, a shot's
    /// sound and line, and its takes (into Takes), each renamed after the new place. A file
    /// already at the new place stays, and so does the one that would have taken its place.
    /// </summary>
    private void Move(string from, string to)
    {
        var (fromFull, toFull) = (Full(from), Full(to));
        var (fromDir, toDir) = (Path.GetDirectoryName(fromFull)!, Path.GetDirectoryName(toFull)!);
        var (fromStem, toStem) = (Path.GetFileNameWithoutExtension(fromFull), Path.GetFileNameWithoutExtension(toFull));
        foreach (var dir in new[] { fromDir, Path.Combine(fromDir, TakesName) }.Where(Directory.Exists))
        {
            List<string> family;
            try { family = Directory.EnumerateFiles(dir, fromStem + ".*").ToList(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            foreach (var file in family)
            {
                var rest = Path.GetFileName(file)[fromStem.Length..];
                var target = Path.Combine(TakeRest().IsMatch(rest) ? Path.Combine(toDir, TakesName) : toDir, toStem + rest);
                if (string.Equals(file, target, StringComparison.OrdinalIgnoreCase) || File.Exists(target))
                    continue;
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.Move(file, target);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
    }

    /// <summary>What follows a take's stem: ".v2.png", ".v2.png.meta", ".v2.workflow.json", ".line.v2.flac".</summary>
    [GeneratedRegex(@"\.v\d+\.", RegexOptions.IgnoreCase)]
    private static partial Regex TakeRest();

    // ---- writing ----------------------------------------------------------------------------------

    /// <summary>
    /// Writes the film into its folder: each piece's meta where it goes (see <see cref="Arrange"/>),
    /// the film's own meta at the root, and the References folder for the user's pictures. The meta
    /// of a piece no longer in the film (or placed elsewhere before it was made) goes, or it would
    /// come back; its files stay.
    /// </summary>
    public void Save()
    {
        if (Folder.Length == 0)
            throw new InvalidOperationException("The film has no folder to be saved in.");
        Directory.CreateDirectory(Folder);
        Arrange();
        var written = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var shot in Shots)
            written.Add(WriteMeta(ShotMeta(shot), "shot", SettingsOf(shot)));
        foreach (var frame in Frames)
            written.Add(WriteMeta(FrameMeta(frame), "frame", SettingsOf(frame)));
        written.Add(WriteMeta(SongMeta, "sound", SettingsOf(Sound)));
        written.Add(WriteMeta(FilmMeta, "film", new JsonObject
        {
            ["title"] = Title,
            ["width"] = Width,
            ["height"] = Height,
            ["fps"] = Fps,
            ["soundVolume"] = SoundVolume,
            ["shotVolume"] = ShotVolume,
            ["shots"] = new JsonArray([.. Shots.Select(s => (JsonNode)JsonValue.Create(s.Id))]),
        }));
        foreach (var (meta, kind, _) in Metas(Folder).ToList())
            if (kind is "film" or "frame" or "shot" or "sound" && !written.Contains(Path.GetFullPath(meta)))
                try { File.Delete(meta); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        Directory.CreateDirectory(ReferencesFolder);
    }

    /// <summary>A piece's settings as its meta holds them: as the storyboard has it, without its place (the meta's own).</summary>
    private static JsonObject SettingsOf<T>(T piece)
    {
        var json = JsonSerializer.SerializeToNode(piece, typeof(T), Readable) as JsonObject ?? [];
        json.Remove("place");
        return json;
    }

    /// <summary>Writes a meta (what it says of how the file was made kept), when it says something new; its full path.</summary>
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
    /// reference in References, any other in Frames), named after its part there. A piece made
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
                    films.Add((dir, (FilmMetaIn(dir) ?? Path.Combine(dir, OldFileName)) is var file && File.Exists(file) ? File.GetLastWriteTimeUtc(file) : Directory.GetLastWriteTimeUtc(dir)));
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
