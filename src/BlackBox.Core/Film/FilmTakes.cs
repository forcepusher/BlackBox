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

    /// <summary>The workflow it was made with, beside it.</summary>
    public string? Workflow(Storyboard board) => Kind switch
    {
        FilmPieceKind.Frame => board.Frame(Id) is { } f ? board.FrameWorkflow(f) : null,
        FilmPieceKind.Shot => board.Shot(Id) is { } s ? board.ShotWorkflow(s) : null,
        _ => board.SongWorkflow,
    };

    /// <summary>Its settings in the storyboard, as JSON.</summary>
    public JsonNode? Settings(Storyboard board) => Kind switch
    {
        FilmPieceKind.Frame => board.Frame(Id) is { } f ? JsonSerializer.SerializeToNode(f, StoryboardJson.Default.FilmFrame) : null,
        FilmPieceKind.Shot => board.Shot(Id) is { } s ? JsonSerializer.SerializeToNode(s, StoryboardJson.Default.FilmShot) : null,
        _ => JsonSerializer.SerializeToNode(board.Sound, StoryboardJson.Default.FilmSound),
    };

    public bool IsCurrent(Storyboard board) => Kind switch
    {
        FilmPieceKind.Frame => board.Frame(Id) is { } f && FilmMaker.IsCurrent(board, f),
        FilmPieceKind.Shot => board.Shot(Id) is { } s && FilmMaker.IsCurrent(board, s),
        _ => FilmMaker.SongIsCurrent(board),
    };
}

/// <summary>An earlier version of a piece, kept when it was made again.</summary>
/// <param name="Settings">The piece's settings it was made with, to put back with it.</param>
public sealed record FilmTake(int Number, string File, string Workflow, DateTime Made, JsonObject? Settings);

/// <summary>
/// The takes of a piece: each time a frame, clip or song is made again, the one before is kept
/// beside it (frames/f2.v1.png and .v1.json…) with the settings it was made with, and so are the
/// takes not kept of several made at once, so the user can put an earlier one back, settings and all.
/// </summary>
public static class FilmTakes
{
    /// <summary>The settings a piece was made with, kept in its workflow file.</summary>
    public static void Remember(string workflowFile, JsonNode? settings)
    {
        if (settings is null)
            return;
        try
        {
            if (JsonNode.Parse(System.IO.File.ReadAllText(workflowFile)) is JsonObject json && json["blackbox"] is JsonObject head)
            {
                head["settings"] = settings.DeepClone();
                Settings.AtomicFile.Write(workflowFile, json.ToJsonString(ComfyWorkflow.Indented));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
    }

    /// <summary>What a fresh look at a piece found, kept in its workflow file: it goes with the piece, and goes when it is made again.</summary>
    public static void RememberLook(string workflowFile, string verdict, string said)
    {
        try
        {
            if (JsonNode.Parse(System.IO.File.ReadAllText(workflowFile)) is JsonObject json && json["blackbox"] is JsonObject head)
            {
                head["look"] = new JsonObject { ["verdict"] = verdict, ["said"] = said };
                Settings.AtomicFile.Write(workflowFile, json.ToJsonString(ComfyWorkflow.Indented));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
    }

    /// <summary>What the last look at a piece found (good or redo, and what it said); null when it wasn't looked at since it was made.</summary>
    public static (string Verdict, string Said)? LookIn(string? workflowFile)
    {
        try
        {
            if (workflowFile is null || !System.IO.File.Exists(workflowFile) || JsonNode.Parse(System.IO.File.ReadAllText(workflowFile))?["blackbox"]?["look"] is not JsonObject look)
                return null;
            return look["verdict"] is JsonValue v && v.TryGetValue<string>(out var verdict) ? (verdict, look["said"] is JsonValue s && s.TryGetValue<string>(out var said) ? said : "") : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// What a piece is made with beside its own file: a speaking shot's line, as the voice model
    /// said it and as the shot plays it. A take put back without them played the newer line over
    /// the older lips, and read as up to date.
    /// </summary>
    private static IEnumerable<string> Sidecars(Storyboard board, FilmPiece piece) =>
        piece.Kind == FilmPieceKind.Shot && board.Shot(piece.Id) is { } shot ? [board.ShotAudio(shot), board.ShotLine(shot)] : [];

    private static string TakeFile(string file, int n) => Path.Combine(Path.GetDirectoryName(file)!, $"{Path.GetFileNameWithoutExtension(file)}.v{n}{Path.GetExtension(file)}");

    /// <summary>The takes kept of a piece, the first first.</summary>
    public static IReadOnlyList<FilmTake> List(Storyboard board, FilmPiece piece)
    {
        if (piece.File(board) is not { } file || piece.Workflow(board) is not { } workflow)
            return [];
        var dir = Path.GetDirectoryName(file)!;
        if (!Directory.Exists(dir))
            return [];
        var stem = Path.GetFileNameWithoutExtension(file) + ".v";
        var takes = new List<FilmTake>();
        foreach (var take in Directory.EnumerateFiles(dir, stem + "*" + Path.GetExtension(file)))
            if (int.TryParse(Path.GetFileNameWithoutExtension(take)[stem.Length..], out var n) && n > 0)
                takes.Add(new FilmTake(n, take, TakeFile(workflow, n), System.IO.File.GetLastWriteTimeUtc(take), SettingsIn(TakeFile(workflow, n))));
        return takes.OrderBy(t => t.Number).ToList();
    }

    private static JsonObject? SettingsIn(string workflow)
    {
        try
        {
            return System.IO.File.Exists(workflow) ? JsonNode.Parse(System.IO.File.ReadAllText(workflow))?["blackbox"]?["settings"]?.DeepClone() as JsonObject : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary>Keeps the piece as it is now as a take of its own, before it is made again.</summary>
    public static FilmTake? Keep(Storyboard board, FilmPiece piece)
    {
        if (piece.File(board) is not { } file || !System.IO.File.Exists(file) || piece.Workflow(board) is not { } workflow)
            return null;
        var n = List(board, piece).Select(t => t.Number).DefaultIfEmpty(0).Max() + 1;
        System.IO.File.Copy(file, TakeFile(file, n), overwrite: true);
        if (System.IO.File.Exists(workflow))
            System.IO.File.Copy(workflow, TakeFile(workflow, n), overwrite: true);
        foreach (var sidecar in Sidecars(board, piece).Where(System.IO.File.Exists))
            System.IO.File.Copy(sidecar, TakeFile(sidecar, n), overwrite: true);
        return new FilmTake(n, TakeFile(file, n), TakeFile(workflow, n), System.IO.File.GetLastWriteTimeUtc(file), SettingsIn(workflow));
    }

    /// <summary>
    /// Moves a file made beside the piece (one take of several, of which another was kept) in among
    /// its takes, with its workflow file, so the user can put it back.
    /// </summary>
    public static FilmTake? Add(Storyboard board, FilmPiece piece, string made, string madeWorkflow)
    {
        if (piece.File(board) is not { } file || piece.Workflow(board) is not { } workflow || !System.IO.File.Exists(made))
            return null;
        var n = List(board, piece).Select(t => t.Number).DefaultIfEmpty(0).Max() + 1;
        System.IO.File.Move(made, TakeFile(file, n), overwrite: true);
        if (System.IO.File.Exists(madeWorkflow))
            System.IO.File.Move(madeWorkflow, TakeFile(workflow, n), overwrite: true);
        return new FilmTake(n, TakeFile(file, n), TakeFile(workflow, n), System.IO.File.GetLastWriteTimeUtc(TakeFile(file, n)), SettingsIn(TakeFile(workflow, n)));
    }

    /// <summary>
    /// Puts back what <see cref="Keep"/> kept, for a make that failed or was stopped: the piece as it
    /// was, with its workflow file, which the make had begun to write over. Dropped instead, the
    /// piece lost its settings and look, and read as changed.
    /// </summary>
    public static void Restore(Storyboard board, FilmPiece piece, FilmTake take)
    {
        try
        {
            if (piece.File(board) is { } file && System.IO.File.Exists(take.File))
                System.IO.File.Move(take.File, file, overwrite: true);
            if (piece.Workflow(board) is { } workflow && System.IO.File.Exists(take.Workflow))
                System.IO.File.Move(take.Workflow, workflow, overwrite: true);
            foreach (var sidecar in Sidecars(board, piece).Where(s => System.IO.File.Exists(TakeFile(s, take.Number))))
                System.IO.File.Move(TakeFile(sidecar, take.Number), sidecar, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // In use (a player has the picture open): kept as a take, which the user can put back.
        }
    }

    /// <summary>Takes back a take <see cref="Keep"/> kept for a make that then made nothing: it is the piece as it still is.</summary>
    public static void Drop(FilmTake take)
    {
        System.IO.File.Delete(take.File);
        if (System.IO.File.Exists(take.Workflow))
            System.IO.File.Delete(take.Workflow);
        // A shot's line kept with it (see Sidecars): "s1.v2.wav" and "s1.line.v2.flac".
        var name = Path.GetFileNameWithoutExtension(take.File);
        if (Path.GetExtension(take.File).Equals(".mp4", StringComparison.OrdinalIgnoreCase) && name.LastIndexOf(".v", StringComparison.Ordinal) is var at and > 0)
            foreach (var sidecar in new[] { $"{name}.wav", $"{name[..at]}.line.v{take.Number}.flac" }.Select(s => Path.Combine(Path.GetDirectoryName(take.File)!, s)))
                if (System.IO.File.Exists(sidecar))
                    System.IO.File.Delete(sidecar);
    }

    /// <summary>
    /// Puts a take back in the film: the piece as it is now becomes a take, and the take's file and
    /// the settings it was made with take its place (so it is up to date again).
    /// </summary>
    public static Storyboard Use(Storyboard board, FilmPiece piece, FilmTake take)
    {
        if (piece.File(board) is not { } file || piece.Workflow(board) is not { } workflow || !System.IO.File.Exists(take.File))
            return board;
        Keep(board, piece);
        System.IO.File.Copy(take.File, file, overwrite: true);
        // Made now, as far as what is made from it goes: with the take's own time, the film joined
        // since with the piece it replaced still read as made after everything in it.
        System.IO.File.SetLastWriteTimeUtc(file, DateTime.UtcNow);
        if (System.IO.File.Exists(take.Workflow))
            System.IO.File.Copy(take.Workflow, workflow, overwrite: true);
        foreach (var sidecar in Sidecars(board, piece).Where(s => System.IO.File.Exists(TakeFile(s, take.Number))))
            System.IO.File.Move(TakeFile(sidecar, take.Number), sidecar, overwrite: true);
        System.IO.File.Delete(take.File);
        if (System.IO.File.Exists(take.Workflow))
            System.IO.File.Delete(take.Workflow);
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
                        System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False => JsonValue.Create(false),
                        System.Text.Json.JsonValueKind.Number => JsonValue.Create(0),
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
