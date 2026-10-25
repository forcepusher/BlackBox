using System.Text.Json;
using System.Text.Json.Nodes;
using BlackBox.Core.Comfy;

namespace BlackBox.Core.Video;

public enum VideoPieceKind { Frame, Shot, Sound }

/// <summary>A piece of a video that is made on its own: a frame (a reference too), a shot's clip, or a sound (a song once made).</summary>
public sealed record VideoPiece(VideoPieceKind Kind, string Id)
{
    public static VideoPiece Of(VideoFrame frame) => new(VideoPieceKind.Frame, frame.Id);

    public static VideoPiece Of(VideoShot shot) => new(VideoPieceKind.Shot, shot.Id);

    public static VideoPiece Of(VideoSound sound) => new(VideoPieceKind.Sound, sound.Id);

    /// <summary>Where it is once made.</summary>
    public string? File(Storyboard board) => Kind switch
    {
        VideoPieceKind.Frame => board.Frame(Id) is { } f ? board.FramePicture(f) : null,
        VideoPieceKind.Shot => board.Shot(Id) is { } s ? board.ShotVideo(s) : null,
        _ => board.Sound(Id) is { } a ? board.SoundFile(a) : null,
    };

    /// <summary>Its meta: what it is, and how it was made.</summary>
    public string? Meta(Storyboard board) => File(board) is { } file ? Storyboard.MetaOf(file) : null;

    /// <summary>Its settings in the storyboard, as JSON (without its place, which is the folder's).</summary>
    public JsonNode? Settings(Storyboard board)
    {
        var settings = Kind switch
        {
            VideoPieceKind.Frame => board.Frame(Id) is { } f ? JsonSerializer.SerializeToNode(f, StoryboardJson.Default.VideoFrame) : null,
            VideoPieceKind.Shot => board.Shot(Id) is { } s ? JsonSerializer.SerializeToNode(s, StoryboardJson.Default.VideoShot) : null,
            _ => board.Sound(Id) is { } a ? JsonSerializer.SerializeToNode(a, StoryboardJson.Default.VideoSound) : null,
        };
        (settings as JsonObject)?.Remove("place");
        return settings;
    }

    public bool IsCurrent(Storyboard board) => Kind switch
    {
        VideoPieceKind.Frame => board.Frame(Id) is { } f && VideoMaker.IsCurrent(board, f),
        VideoPieceKind.Shot => board.Shot(Id) is { } s && VideoMaker.IsCurrent(board, s),
        _ => board.Sound(Id) is { } a && VideoMaker.IsCurrent(board, a),
    };
}

/// <summary>An earlier version of a piece, kept when it was made again.</summary>
/// <param name="Meta">Its meta as it was then: what it was made with, and what its look found.</param>
/// <param name="Settings">The piece's settings it was made with, to put back with it.</param>
public sealed record VideoVersion(int Number, string File, string Meta, DateTime Made, JsonObject? Settings);

/// <summary>
/// The versions of a piece: each time a frame, clip or song is made again, the one before is kept in
/// its folder's Versions (Versions/video.v1.mp4, with its meta and workflow) with the settings it was made
/// with, and so are the tries not kept of several made at once, so the user can put an earlier one
/// back, settings and all.
/// </summary>
public static class VideoVersions
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

    /// <summary>Moves a file with its meta and workflow (one try made beside a piece, in among its versions).</summary>
    private static void MoveGroup(string from, string to)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(to)!);
        foreach (var (source, target) in Group(from).Zip(Group(to)))
            if (System.IO.File.Exists(source))
                System.IO.File.Move(source, target, overwrite: true);
    }

    /// <summary>
    /// Puts a file made beside a piece (the best of several tries) in its place, with its workflow;
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
    /// says of the piece: moved whole, a try's meta made beside it left the piece without its
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

    /// <summary>Moves a version's file of a group back over the piece's: its meta gives only how it was made (see <see cref="MadeInto"/>).</summary>
    private static void Back(string version, string member, string file)
    {
        if (string.Equals(member, Storyboard.MetaOf(file), StringComparison.OrdinalIgnoreCase))
            MadeInto(version, member);
        else
        {
            Media.MediaRelease.Release(member);
            System.IO.File.Move(version, member, overwrite: true);
        }
    }

    /// <summary>
    /// What a piece is made with beside its own file: a speaking shot's line, as the voice model
    /// said it and as the shot plays it. A version put back without them played the newer line over
    /// the older lips, and read as up to date.
    /// </summary>
    private static IEnumerable<string> Sidecars(Storyboard board, VideoPiece piece) =>
        piece.Kind == VideoPieceKind.Shot && board.Shot(piece.Id) is { } shot ? [board.ShotAudio(shot), .. Group(board.ShotLine(shot))] : [];

    /// <summary>Where version <paramref name="n"/> of a file is kept: "Generated/Versions/s1.v2.mp4".</summary>
    private static string VersionFile(string file, int n) =>
        Path.Combine(Path.GetDirectoryName(file)!, Storyboard.VersionsName, $"{Path.GetFileNameWithoutExtension(file)}.v{n}{Path.GetExtension(file)}");

    /// <summary>
    /// Where version <paramref name="n"/> of what goes with <paramref name="file"/> is kept: its .json and
    /// workflow follow the version's file ("Intro.v2.json", "Intro.v2.workflow.json"); its sound and
    /// line are named as versions of their own ("Intro.v2.wav", "Intro.line.v2.flac").
    /// </summary>
    private static string VersionPath(string member, string file, int n)
    {
        if (string.Equals(member, file, StringComparison.OrdinalIgnoreCase))
            return VersionFile(file, n);
        foreach (var main in new[] { file }.Concat(member.EndsWith(".line.json", StringComparison.OrdinalIgnoreCase) ||
                                                   member.EndsWith(".line.workflow.json", StringComparison.OrdinalIgnoreCase)
                                                       ? [Path.Combine(Path.GetDirectoryName(file)!, Path.GetFileNameWithoutExtension(file) + ".line.flac")] : []))
        {
            if (string.Equals(member, Storyboard.MetaOf(main), StringComparison.OrdinalIgnoreCase))
                return Storyboard.MetaOf(VersionFile(main, n));
            if (string.Equals(member, Storyboard.WorkflowOf(main), StringComparison.OrdinalIgnoreCase))
                return Storyboard.WorkflowOf(VersionFile(main, n));
        }
        return VersionFile(member, n);
    }

    /// <summary>The versions kept of a piece, the first first.</summary>
    public static IReadOnlyList<VideoVersion> List(Storyboard board, VideoPiece piece)
    {
        if (piece.File(board) is not { } file)
            return [];
        var dir = Path.Combine(Path.GetDirectoryName(file)!, Storyboard.VersionsName);
        if (!Directory.Exists(dir))
            return [];
        var stem = Path.GetFileNameWithoutExtension(file) + ".v";
        var versions = new List<VideoVersion>();
        foreach (var version in Directory.EnumerateFiles(dir, stem + "*" + Path.GetExtension(file)))
            if (int.TryParse(Path.GetFileNameWithoutExtension(version)[stem.Length..], out var n) && n > 0)
                versions.Add(new VideoVersion(n, version, Storyboard.MetaOf(version), System.IO.File.GetLastWriteTimeUtc(version), SettingsIn(Storyboard.MetaOf(version))));
        return versions.OrderBy(t => t.Number).ToList();
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

    /// <summary>Keeps the piece as it is now as a version of its own, before it is made again.</summary>
    public static VideoVersion? Keep(Storyboard board, VideoPiece piece)
    {
        if (piece.File(board) is not { } file || !System.IO.File.Exists(file))
            return null;
        var n = List(board, piece).Select(t => t.Number).DefaultIfEmpty(0).Max() + 1;
        Directory.CreateDirectory(Path.GetDirectoryName(VersionFile(file, n))!);
        foreach (var member in Group(file).Concat(Sidecars(board, piece)).Where(System.IO.File.Exists))
            System.IO.File.Copy(member, VersionPath(member, file, n), overwrite: true);
        return new VideoVersion(n, VersionFile(file, n), Storyboard.MetaOf(VersionFile(file, n)), System.IO.File.GetLastWriteTimeUtc(file), SettingsIn(Storyboard.MetaOf(file)));
    }

    /// <summary>
    /// Moves a file made beside the piece (one try of several, of which another was kept) in among
    /// its versions, with its meta and workflow, so the user can put it back.
    /// </summary>
    public static VideoVersion? Add(Storyboard board, VideoPiece piece, string made)
    {
        if (piece.File(board) is not { } file || !System.IO.File.Exists(made))
            return null;
        var n = List(board, piece).Select(t => t.Number).DefaultIfEmpty(0).Max() + 1;
        MoveGroup(made, VersionFile(file, n));
        return new VideoVersion(n, VersionFile(file, n), Storyboard.MetaOf(VersionFile(file, n)), System.IO.File.GetLastWriteTimeUtc(VersionFile(file, n)), SettingsIn(Storyboard.MetaOf(VersionFile(file, n))));
    }

    /// <summary>
    /// Puts back what <see cref="Keep"/> kept, for a make that failed or was stopped: the piece as it
    /// was, with its meta and workflow, which the make had begun to write over. Dropped instead, the
    /// piece lost its settings and look, and read as changed.
    /// </summary>
    public static void Restore(Storyboard board, VideoPiece piece, VideoVersion version)
    {
        if (piece.File(board) is not { } file)
            return;
        foreach (var member in Group(file).Concat(Sidecars(board, piece)))
            try
            {
                if (System.IO.File.Exists(VersionPath(member, file, version.Number)))
                    Back(VersionPath(member, file, version.Number), member, file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                // In use (a player has the picture open): kept as a version, which the user can put back.
            }
    }

    /// <summary>Takes back a version <see cref="Keep"/> kept for a make that then made nothing: it is the piece as it still is.</summary>
    public static void Drop(VideoVersion version)
    {
        var stem = Path.GetFileNameWithoutExtension(version.File); // "Intro.v2"
        var dir = Path.GetDirectoryName(version.File)!;
        var at = stem.LastIndexOf(".v", StringComparison.Ordinal);
        // With it, what went with it: its meta and workflow, and a shot's line ("Intro.v2.wav", "Intro.line.v2.flac" and theirs).
        var line = at > 0 ? Path.Combine(dir, $"{stem[..at]}.line.v{version.Number}.flac") : null;
        foreach (var file in Group(version.File).Append(Path.Combine(dir, stem + ".wav")).Concat(line is null ? [] : Group(line)))
            if (System.IO.File.Exists(file))
                System.IO.File.Delete(file);
    }

    /// <summary>
    /// Puts a version back in the video: the piece as it is now becomes a version, and the version's file and
    /// the settings it was made with take its place (so it is up to date again).
    /// </summary>
    public static Storyboard Use(Storyboard board, VideoPiece piece, VideoVersion version)
    {
        if (piece.File(board) is not { } file || !System.IO.File.Exists(version.File))
            return board;
        Keep(board, piece);
        Media.MediaRelease.Release(file);
        System.IO.File.Copy(version.File, file, overwrite: true);
        // Made now, as far as what is made from it goes: with the version's own time, the video joined
        // since with the piece it replaced still read as made after everything in it.
        System.IO.File.SetLastWriteTimeUtc(file, DateTime.UtcNow);
        foreach (var member in Group(file).Skip(1).Concat(Sidecars(board, piece)))
            if (System.IO.File.Exists(VersionPath(member, file, version.Number)))
                Back(VersionPath(member, file, version.Number), member, file);
        System.IO.File.Delete(version.File);
        if (version.Settings is not { } settings)
            return board;
        var item = (JsonObject)settings.DeepClone();
        // What was at its default then (no last frame, no model: null, false, 0) wasn't written with
        // the version: put back as such too, or the piece's settings since stayed and it read as changed.
        if (piece.Settings(board) is JsonObject now)
            foreach (var (key, value) in now)
                if (key != "id" && !item.ContainsKey(key))
                    item[key] = value is JsonValue v ? v.GetValueKind() switch
                    {
                        JsonValueKind.True or JsonValueKind.False => JsonValue.Create(false),
                        JsonValueKind.Number => JsonValue.Create(0),
                        _ => null,
                    } : null;
        item["id"] = piece.Id;
        // A sound's place in the video is where it is now, whatever version plays there.
        if (piece.Kind == VideoPieceKind.Sound)
            foreach (var key in new[] { "soundLine", "start", "trim", "seconds", "volume", "name" })
                item.Remove(key);
        var change = piece.Kind switch
        {
            VideoPieceKind.Frame => new JsonObject { ["frames"] = new JsonArray(item) },
            VideoPieceKind.Shot => new JsonObject { ["shots"] = new JsonArray(item) },
            _ => new JsonObject { ["sounds"] = new JsonArray(item) },
        };
        var merged = Storyboard.Merge(board, change);
        merged.Save();
        return merged;
    }
}
