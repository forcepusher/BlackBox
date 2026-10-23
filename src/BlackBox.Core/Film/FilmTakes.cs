using System.Text.Json;
using System.Text.Json.Nodes;
using BlackBox.Core.Comfy;

namespace BlackBox.Core.Film;

public enum FilmPieceKind { Frame, Shot, Song }

/// <summary>A piece of a film that is made on its own: a frame (a reference too), a shot's clip, or the song.</summary>
public sealed record FilmPiece(FilmPieceKind Kind, string Id)
{
    public static readonly FilmPiece Song = new(FilmPieceKind.Song, "song");

    public static FilmPiece Of(FilmFrame frame) => new(FilmPieceKind.Frame, frame.Id);

    public static FilmPiece Of(FilmShot shot) => new(FilmPieceKind.Shot, shot.Id);

    /// <summary>Where it is once made.</summary>
    public string? File(Storyboard board) => Kind switch
    {
        FilmPieceKind.Frame => board.Frame(Id) is { } f ? board.FramePicture(f) : null,
        FilmPieceKind.Shot => board.Shot(Id) is { } s ? board.ShotVideo(s) : null,
        _ => board.SongFile,
    };

    /// <summary>Its meta: what it is, and how it was made.</summary>
    public string? Meta(Storyboard board) => File(board) is { } file ? Storyboard.MetaOf(file) : null;

    /// <summary>Its settings in the storyboard, as JSON (without its place, which is the folder's).</summary>
    public JsonNode? Settings(Storyboard board)
    {
        var settings = Kind switch
        {
            FilmPieceKind.Frame => board.Frame(Id) is { } f ? JsonSerializer.SerializeToNode(f, StoryboardJson.Default.FilmFrame) : null,
            FilmPieceKind.Shot => board.Shot(Id) is { } s ? JsonSerializer.SerializeToNode(s, StoryboardJson.Default.FilmShot) : null,
            _ => JsonSerializer.SerializeToNode(board.Sound, StoryboardJson.Default.FilmSound),
        };
        (settings as JsonObject)?.Remove("place");
        return settings;
    }

    public bool IsCurrent(Storyboard board) => Kind switch
    {
        FilmPieceKind.Frame => board.Frame(Id) is { } f && FilmMaker.IsCurrent(board, f),
        FilmPieceKind.Shot => board.Shot(Id) is { } s && FilmMaker.IsCurrent(board, s),
        _ => FilmMaker.SongIsCurrent(board),
    };
}

/// <summary>An earlier version of a piece, kept when it was made again.</summary>
/// <param name="Meta">Its meta as it was then: what it was made with, and what its look found.</param>
/// <param name="Settings">The piece's settings it was made with, to put back with it.</param>
public sealed record FilmTake(int Number, string File, string Meta, DateTime Made, JsonObject? Settings);

/// <summary>
/// The takes of a piece: each time a frame, clip or song is made again, the one before is kept in
/// its folder's Takes (Takes/Intro.v1.mp4, with its meta and workflow) with the settings it was made
/// with, and so are the takes not kept of several made at once, so the user can put an earlier one
/// back, settings and all.
/// </summary>
public static class FilmTakes
{
    /// <summary>Changes what a meta says of how its file was made; false when there is no such meta, or nothing made in it.</summary>
    internal static bool ChangeMade(string meta, Action<JsonObject> change)
    {
        try
        {
            if (JsonNode.Parse(System.IO.File.ReadAllText(meta)) is JsonObject json && json["made"] is JsonObject made)
            {
                change(made);
                Settings.AtomicFile.Write(meta, json.ToJsonString(ComfyWorkflow.Indented));
                return true;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
        return false;
    }

    /// <summary>The settings a piece was made with, kept in its meta.</summary>
    public static void Remember(string meta, JsonNode? settings)
    {
        if (settings is not null)
            ChangeMade(meta, made => made["settings"] = settings.DeepClone());
    }

    /// <summary>What a fresh look at a piece found, kept in its meta: it goes with the piece, and goes when it is made again.</summary>
    public static void RememberLook(string meta, string verdict, string said) =>
        ChangeMade(meta, made => made["look"] = new JsonObject { ["verdict"] = verdict, ["said"] = said });

    /// <summary>What the last look at a piece found (good or redo, and what it said); null when it wasn't looked at since it was made.</summary>
    public static (string Verdict, string Said)? LookIn(string? meta)
    {
        try
        {
            if (meta is null || !System.IO.File.Exists(meta) || JsonNode.Parse(System.IO.File.ReadAllText(meta))?["made"]?["look"] is not JsonObject look)
                return null;
            return look["verdict"] is JsonValue v && v.TryGetValue<string>(out var verdict) ? (verdict, look["said"] is JsonValue s && s.TryGetValue<string>(out var said) ? said : "") : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary>A file with what goes beside it: its meta and its workflow.</summary>
    private static string[] Group(string file) => [file, Storyboard.MetaOf(file), Storyboard.WorkflowOf(file)];

    /// <summary>Moves a file with its meta and workflow (one take made beside a piece, in among its takes).</summary>
    private static void MoveGroup(string from, string to)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(to)!);
        foreach (var (source, target) in Group(from).Zip(Group(to)))
            if (System.IO.File.Exists(source))
                System.IO.File.Move(source, target, overwrite: true);
    }

    /// <summary>
    /// Puts a file made beside a piece (the best of several takes) in its place, with its workflow;
    /// the piece's meta takes what the made file's meta says of how it was made, and keeps what it
    /// says of the piece.
    /// </summary>
    internal static void Promote(string from, string to)
    {
        Media.MediaRelease.Release(to);
        System.IO.File.Move(from, to, overwrite: true);
        if (System.IO.File.Exists(Storyboard.WorkflowOf(from)))
            System.IO.File.Move(Storyboard.WorkflowOf(from), Storyboard.WorkflowOf(to), overwrite: true);
        MadeInto(Storyboard.MetaOf(from), Storyboard.MetaOf(to));
    }

    /// <summary>
    /// What one meta says of how its file was made, into another (a piece's, which keeps what it
    /// says of the piece: moved whole, a take's meta made beside it left the piece without its
    /// prompt); the first goes.
    /// </summary>
    private static void MadeInto(string from, string to)
    {
        if (!System.IO.File.Exists(from))
            return;
        var made = JsonNode.Parse(System.IO.File.ReadAllText(from))?["made"];
        JsonObject meta;
        try { meta = System.IO.File.Exists(to) ? JsonNode.Parse(System.IO.File.ReadAllText(to)) as JsonObject ?? [] : []; }
        catch (JsonException) { meta = []; }
        if (made is null)
            meta.Remove("made");
        else
            meta["made"] = made.DeepClone();
        Settings.AtomicFile.Write(to, meta.ToJsonString(ComfyWorkflow.Indented));
        System.IO.File.Delete(from);
    }

    /// <summary>Moves a take's file of a group back over the piece's: its meta gives only how it was made (see <see cref="MadeInto"/>).</summary>
    private static void Back(string take, string member, string file)
    {
        if (string.Equals(member, Storyboard.MetaOf(file), StringComparison.OrdinalIgnoreCase))
            MadeInto(take, member);
        else
        {
            Media.MediaRelease.Release(member);
            System.IO.File.Move(take, member, overwrite: true);
        }
    }

    /// <summary>
    /// What a piece is made with beside its own file: a speaking shot's line, as the voice model
    /// said it and as the shot plays it. A take put back without them played the newer line over
    /// the older lips, and read as up to date.
    /// </summary>
    private static IEnumerable<string> Sidecars(Storyboard board, FilmPiece piece) =>
        piece.Kind == FilmPieceKind.Shot && board.Shot(piece.Id) is { } shot ? [board.ShotAudio(shot), .. Group(board.ShotLine(shot))] : [];

    /// <summary>Where take <paramref name="n"/> of a file is kept: "1 Intro/Takes/Intro.v2.mp4".</summary>
    private static string TakeFile(string file, int n) =>
        Path.Combine(Path.GetDirectoryName(file)!, Storyboard.TakesName, $"{Path.GetFileNameWithoutExtension(file)}.v{n}{Path.GetExtension(file)}");

    /// <summary>
    /// Where take <paramref name="n"/> of what goes with <paramref name="file"/> is kept: its meta and
    /// workflow follow the take's file ("Intro.v2.mp4.meta", "Intro.v2.workflow.json"); its sound and
    /// line are named as takes of their own ("Intro.v2.wav", "Intro.line.v2.flac").
    /// </summary>
    private static string TakeOf(string member, string file, int n)
    {
        if (string.Equals(member, file, StringComparison.OrdinalIgnoreCase))
            return TakeFile(file, n);
        foreach (var main in new[] { file }.Concat(member.EndsWith(".line.flac" + Storyboard.MetaExtension, StringComparison.OrdinalIgnoreCase) ||
                                                   member.EndsWith(".line.workflow.json", StringComparison.OrdinalIgnoreCase)
                                                       ? [Path.Combine(Path.GetDirectoryName(file)!, Path.GetFileNameWithoutExtension(file) + ".line.flac")] : []))
        {
            if (string.Equals(member, Storyboard.MetaOf(main), StringComparison.OrdinalIgnoreCase))
                return Storyboard.MetaOf(TakeFile(main, n));
            if (string.Equals(member, Storyboard.WorkflowOf(main), StringComparison.OrdinalIgnoreCase))
                return Storyboard.WorkflowOf(TakeFile(main, n));
        }
        return TakeFile(member, n);
    }

    /// <summary>The takes kept of a piece, the first first.</summary>
    public static IReadOnlyList<FilmTake> List(Storyboard board, FilmPiece piece)
    {
        if (piece.File(board) is not { } file)
            return [];
        var dir = Path.Combine(Path.GetDirectoryName(file)!, Storyboard.TakesName);
        if (!Directory.Exists(dir))
            return [];
        var stem = Path.GetFileNameWithoutExtension(file) + ".v";
        var takes = new List<FilmTake>();
        foreach (var take in Directory.EnumerateFiles(dir, stem + "*" + Path.GetExtension(file)))
            if (int.TryParse(Path.GetFileNameWithoutExtension(take)[stem.Length..], out var n) && n > 0)
                takes.Add(new FilmTake(n, take, Storyboard.MetaOf(take), System.IO.File.GetLastWriteTimeUtc(take), SettingsIn(Storyboard.MetaOf(take))));
        return takes.OrderBy(t => t.Number).ToList();
    }

    private static JsonObject? SettingsIn(string meta)
    {
        try
        {
            return System.IO.File.Exists(meta) ? JsonNode.Parse(System.IO.File.ReadAllText(meta))?["made"]?["settings"]?.DeepClone() as JsonObject : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary>Keeps the piece as it is now as a take of its own, before it is made again.</summary>
    public static FilmTake? Keep(Storyboard board, FilmPiece piece)
    {
        if (piece.File(board) is not { } file || !System.IO.File.Exists(file))
            return null;
        var n = List(board, piece).Select(t => t.Number).DefaultIfEmpty(0).Max() + 1;
        Directory.CreateDirectory(Path.GetDirectoryName(TakeFile(file, n))!);
        foreach (var member in Group(file).Concat(Sidecars(board, piece)).Where(System.IO.File.Exists))
            System.IO.File.Copy(member, TakeOf(member, file, n), overwrite: true);
        return new FilmTake(n, TakeFile(file, n), Storyboard.MetaOf(TakeFile(file, n)), System.IO.File.GetLastWriteTimeUtc(file), SettingsIn(Storyboard.MetaOf(file)));
    }

    /// <summary>
    /// Moves a file made beside the piece (one take of several, of which another was kept) in among
    /// its takes, with its meta and workflow, so the user can put it back.
    /// </summary>
    public static FilmTake? Add(Storyboard board, FilmPiece piece, string made)
    {
        if (piece.File(board) is not { } file || !System.IO.File.Exists(made))
            return null;
        var n = List(board, piece).Select(t => t.Number).DefaultIfEmpty(0).Max() + 1;
        MoveGroup(made, TakeFile(file, n));
        return new FilmTake(n, TakeFile(file, n), Storyboard.MetaOf(TakeFile(file, n)), System.IO.File.GetLastWriteTimeUtc(TakeFile(file, n)), SettingsIn(Storyboard.MetaOf(TakeFile(file, n))));
    }

    /// <summary>
    /// Puts back what <see cref="Keep"/> kept, for a make that failed or was stopped: the piece as it
    /// was, with its meta and workflow, which the make had begun to write over. Dropped instead, the
    /// piece lost its settings and look, and read as changed.
    /// </summary>
    public static void Restore(Storyboard board, FilmPiece piece, FilmTake take)
    {
        if (piece.File(board) is not { } file)
            return;
        foreach (var member in Group(file).Concat(Sidecars(board, piece)))
            try
            {
                if (System.IO.File.Exists(TakeOf(member, file, take.Number)))
                    Back(TakeOf(member, file, take.Number), member, file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                // In use (a player has the picture open): kept as a take, which the user can put back.
            }
    }

    /// <summary>Takes back a take <see cref="Keep"/> kept for a make that then made nothing: it is the piece as it still is.</summary>
    public static void Drop(FilmTake take)
    {
        var stem = Path.GetFileNameWithoutExtension(take.File); // "Intro.v2"
        var dir = Path.GetDirectoryName(take.File)!;
        var at = stem.LastIndexOf(".v", StringComparison.Ordinal);
        // With it, what went with it: its meta and workflow, and a shot's line ("Intro.v2.wav", "Intro.line.v2.flac" and theirs).
        var line = at > 0 ? Path.Combine(dir, $"{stem[..at]}.line.v{take.Number}.flac") : null;
        foreach (var file in Group(take.File).Append(Path.Combine(dir, stem + ".wav")).Concat(line is null ? [] : Group(line)))
            if (System.IO.File.Exists(file))
                System.IO.File.Delete(file);
    }

    /// <summary>
    /// Puts a take back in the film: the piece as it is now becomes a take, and the take's file and
    /// the settings it was made with take its place (so it is up to date again).
    /// </summary>
    public static Storyboard Use(Storyboard board, FilmPiece piece, FilmTake take)
    {
        if (piece.File(board) is not { } file || !System.IO.File.Exists(take.File))
            return board;
        Keep(board, piece);
        Media.MediaRelease.Release(file);
        System.IO.File.Copy(take.File, file, overwrite: true);
        // Made now, as far as what is made from it goes: with the take's own time, the film joined
        // since with the piece it replaced still read as made after everything in it.
        System.IO.File.SetLastWriteTimeUtc(file, DateTime.UtcNow);
        foreach (var member in Group(file).Skip(1).Concat(Sidecars(board, piece)))
            if (System.IO.File.Exists(TakeOf(member, file, take.Number)))
                Back(TakeOf(member, file, take.Number), member, file);
        System.IO.File.Delete(take.File);
        if (take.Settings is not { } settings)
            return board;
        var item = (JsonObject)settings.DeepClone();
        // What was at its default then (no last frame, no model: null, false, 0) wasn't written with
        // the take: put back as such too, or the piece's settings since stayed and it read as changed.
        if (piece.Settings(board) is JsonObject now)
            foreach (var (key, value) in now)
                if (key != "id" && !item.ContainsKey(key))
                    item[key] = value is JsonValue v ? v.GetValueKind() switch
                    {
                        JsonValueKind.True or JsonValueKind.False => JsonValue.Create(false),
                        JsonValueKind.Number => JsonValue.Create(0),
                        _ => null,
                    } : null;
        if (piece.Kind != FilmPieceKind.Song)
            item["id"] = piece.Id;
        var change = piece.Kind switch
        {
            FilmPieceKind.Frame => new JsonObject { ["frames"] = new JsonArray(item) },
            FilmPieceKind.Shot => new JsonObject { ["shots"] = new JsonArray(item) },
            _ => new JsonObject { ["sound"] = item },
        };
        var merged = Storyboard.Merge(board, change);
        merged.Save();
        return merged;
    }
}
