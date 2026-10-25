using System.Globalization;
using System.Text.Json.Nodes;
using BlackBox.Core.Film;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BlackBox.App.ViewModels;

public sealed partial class FilmViewModel
{
    // ---- what a message sent while the panel is open carries ----------------------------------------

    /// <summary>
    /// What a message sent from the chat's box carries while Video editor is open: over its bubble,
    /// the film and what was picked in it ("Video editor · The Fox and the Moon · shot 2"); for the
    /// model, the film's folder, what each pick is in it (a shot, a frame, a reference, a take…),
    /// and how pieces are made and fixed, as the window's buttons used to ask. The film is saved
    /// first, so the model reads what the panel shows.
    /// </summary>
    public (string Via, string Context)? About(IReadOnlyList<string> files)
    {
        SaveNow(force: true);
        var title = _board.Title.Trim().Length > 0 ? _board.Title.Trim() : Path.GetFileName(_board.Folder);
        var picks = files.Select(Describe).ToList();
        var via = $"Video editor · {title}" + (picks.Count == 0 ? "" : " · " + string.Join(", ", picks.Select(p => p.Short)));
        var context = $"[Video editor is open beside the chat on the film \"{title}\" (its folder: {_board.Folder}; film action=storyboard path=\"{_board.Folder}\" shows it all). " +
                      "What they say about a film is about this one unless they say otherwise. " +
                      (picks.Count == 0 ? WholeFilmNote() : $"They picked in it: {string.Join("; ", picks.Select(p => p.Long))}. {PieceNote(picks.Any(p => p.Piece is not null))}") + "]";
        return (via, context);
    }

    /// <summary>A pick: in a few words for the bubble, in full for the model, and the piece it is, if one.</summary>
    private sealed record Pick(string Short, string Long, FilmPiece? Piece);

    /// <summary>What a path picked on the panel's timeline is in the film.</summary>
    private Pick Describe(string path)
    {
        var full = Path.GetFullPath(path);
        bool Is(string other) => string.Equals(full, Path.GetFullPath(other), StringComparison.OrdinalIgnoreCase);
        var made = File.Exists(full);
        var state = made ? "" : ", not made yet";
        foreach (var frame in _board.Frames)
            if (Is(_board.FramePicture(frame)))
            {
                var label = _frames.GetValueOrDefault(frame.Id)?.Title ?? frame.Id;
                return frame.Reference
                    ? new(Lower(label), $"the generated image {frame.Id}" + (frame.Name is { Length: > 0 } name ? $" \"{name}\"" : "") +
                                        $", a reference on the References row ({full}{state})" + (frame.Prompt.Trim().Length == 0 && !made ? ": nothing said of it yet, so lay it out from what they say" : ""), FilmPiece.Of(frame))
                    : new(Lower(label), $"frame {frame.Id}, {Lower(label)} ({full}{state})", FilmPiece.Of(frame));
            }
        var (starts, _) = _board.Timing();
        for (var i = 0; i < _board.Shots.Count; i++)
        {
            var shot = _board.Shots[i];
            if (Is(_board.ShotVideo(shot)))
                return new($"take {i + 1}", string.Create(CultureInfo.InvariantCulture,
                    $"take {i + 1}, shot {shot.Id} of the storyboard (the {i + 1}. of {_board.Shots.Count}, from {starts[i]:0.#} to {starts[i] + shot.Seconds:0.#} s), its clip ({full}{state})"), FilmPiece.Of(shot));
        }
        foreach (var sound in _board.Sounds)
            if (Is(_board.SoundFile(sound)))
                return new(sound.Label, FormattableString.Invariant($"sound {sound.Id} \"{sound.Label}\" on sound track {sound.Track + 1} from {sound.Start:0.#} s, ") +
                    (sound.Source == FilmSound.FromFile ? $"a sound file of theirs ({full})" : $"a song BlackBox makes ({full}{state})" + (sound.Tags.Trim().Length == 0 && !made ? ": nothing said of it yet" : "")), FilmPiece.Of(sound));
        if (Is(_board.Folder) || Is(_board.JoinedFile))
            return new("the whole film", $"the whole film ({full})", null);
        if (Viewing is { Version: { } take, VersionOf: { } of } && Is(take.File))
            return new($"version {take.Number} of {Lower(of.Title)}", $"version {take.Number} of {Lower(of.Title)} ({full}), an earlier version kept when it was made again: " +
                                                                       "the panel puts a version back in the film (Use this version, under it), not the film tool", null);
        if (full.StartsWith(Path.GetFullPath(_board.ReferencesFolder) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            return new(Path.GetFileName(full), $"an image file of theirs on the References row ({full}): a frame names it in from by \"{_board.InReferences(full)}\" to be made from it", null);
        return new(Path.GetFileName(full), $"a file in the film's folder ({full})", null);
    }

    /// <summary>
    /// What the model does with what is said about a piece picked: change that piece alone (a shot,
    /// its frames too) and make it again, as the window's own director did; the rest of the film stays.
    /// </summary>
    private static string PieceNote(bool pieces) =>
        pieces
            ? "What they say is about what they picked: read the storyboard first, then change only that (a take with the frames it starts and ends on and the lines' voice, " +
              "a frame with what it is made from, a sound) unless they ask for more, and make it again with the film tool (action=frame, shot or sound) unless they only ask a question. " +
              FixIt + "Say in a sentence or two what you changed and how it came out."
            : "Read the storyboard first, then do with what they picked what they say.";

    /// <summary>
    /// What the model does with what is said about the whole film: lay it out or change it, and
    /// make it when asked, each piece looked at and fixed, then joined and looked at.
    /// </summary>
    private string WholeFilmNote()
    {
        var note = "Nothing in it is picked: what they say is about the whole film. When they describe it or ask for changes, read the storyboard first, " +
                   "then lay it out or change it with action=storyboard and make nothing unless they ask. " +
                   $"When they ask to make the film: {MakeTheFilm}";
        if (_board.Shots.All(s => s.Prompt.Trim().Length == 0))
            note += " The film is new: give it a title (its folder is renamed to it), a reference for each person or place that comes back, frames made from those, shots as long as their action " +
                    "(2 to 8 seconds) from different angles, each with a name, its first frame and a last one when where it ends matters, the lines when people speak, " +
                    "and a song on a sound track when they want music. Take out the empty starting shot and frames (s1, f1, f2) if you don't use them.";
        return note;
    }

    /// <summary>How the whole film is made, as the window's Make the film asked for it.</summary>
    private const string MakeTheFilm =
        "make what isn't made yet or is out of date, in the order action=storyboard lists under To make (references and frames, the shots, then the songs), " +
        "and read each piece's fresh look: when a look finds something wrong, change what the piece is made from (its prompt, its frames, the seed) and make it again, " +
        "up to twice for a piece. Then join the film (action=join), check it (action=check without id), fix the shots that check names once, join again and check again. " +
        "At the end, say what was made and what is still wrong, if anything.";

    /// <summary>
    /// What a piece made from the panel gets after its fresh look, as the window's own director did
    /// for every piece it made: told only to say how it came out, the agent reported what the look
    /// found wrong and left the piece so.
    /// </summary>
    private const string FixIt = "When a piece's fresh look finds something wrong, change what it is made from (its prompt, its frames, the seed) " +
                                 "and make it again, up to twice. ";

    /// <summary>Makes every piece not made or out of date, each looked at and fixed, then joins the film and looks at it: in the chat.</summary>
    [RelayCommand]
    private void MakeFilm()
    {
        if (_board.Shots.Count == 0 || _board.Shots.Any(s => s.Prompt.Trim().Length == 0))
        {
            Status = _board.Shots.Count == 0 ? "The film has no takes yet: say in the chat what film you want first." : "Some takes don't say what happens in them yet: click them and say it in the chat.";
            return;
        }
        _chat.SendFromToolkit("Make the film.");
    }

    // ---- what the chat is doing -------------------------------------------------------------------------

    /// <summary>What the line over the timeline says: what the panel did, what went wrong.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusOrHint))]
    private string _status = "";

    /// <summary>The status, else how the timeline is used: nothing showed the right-click menu was there (the user, 2026-10-24).</summary>
    public string StatusOrHint => Status.Length > 0 ? Status : "Right-click a row to create, a card to rename or delete. Drop files on the timeline to copy them in.";

    /// <summary>What went wrong, on the status line and in Data/errors.log, about the film in <paramref name="folder"/> (this one's).</summary>
    private void ShowError(string text, Exception error, string? folder = null)
    {
        Status = text;
        Core.ErrorLog.Write($"Video editor, {folder ?? _board.Folder}", text, error);
    }

    /// <summary>The piece the film tool is making or looking at in the chat now, shown working on the timeline and in the tree.</summary>
    private IFilmPiece? _working;

    /// <summary>Reads what the chat is doing, twice a second: the piece being made, and how it is getting on.</summary>
    private void WatchChat()
    {
        var call = _chat.FilmCall();
        var piece = call is { } c && IsThisFilm(c.Args) ? PieceIn(c.Args) : null;
        if (!ReferenceEquals(piece, _working) && _working is { } done)
        {
            done.IsWorking = false;
            _working = null;
            RefreshStates();
        }
        if (piece is null || call is not { } running)
            return;
        _working = piece;
        piece.IsWorking = true;
        piece.State = running.Status.Length > 0 ? running.Status : Arg(running.Args, "action") == "check" ? "Looking at it…" : "Making it…";
        piece.IsError = false;
    }

    /// <summary>Whether a film tool call is about this panel's film: by its path, or none (the film the chat worked on last).</summary>
    private bool IsThisFilm(JsonObject args)
    {
        // Without a path, the film the chat named last: the one this panel showed then, or the
        // piece of the same id in the film opened since was shown as being made.
        if (Arg(args, "path") is not { Length: > 0 } path)
            return _chatFilm is null || string.Equals(_chatFilm, _board.Folder.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
        path = path.Trim().Trim('"');
        try
        {
            var full = Path.GetFullPath(File.Exists(path) ? Path.GetDirectoryName(path)! : path);
            var mine = string.Equals(full.TrimEnd('\\', '/'), _board.Folder.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase)
                       || !Path.IsPathRooted(path) && string.Equals(path, Path.GetFileName(_board.Folder), StringComparison.OrdinalIgnoreCase);
            _chatFilm = mine ? _board.Folder.TrimEnd('\\', '/') : full.TrimEnd('\\', '/');
            return mine;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    /// <summary>The film the chat's film calls named last, by its folder; null until one did.</summary>
    private string? _chatFilm;

    /// <summary>The piece a film tool call makes or looks at, from what it was given; null for the whole film.</summary>
    private IFilmPiece? PieceIn(JsonObject args)
    {
        var id = Arg(args, "id");
        return Arg(args, "action")?.ToLowerInvariant() switch
        {
            "sound" or "song" => id is null ? Sounds.FirstOrDefault(s => s.Sound.Source == FilmSound.Song) : Sounds.FirstOrDefault(x => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase)),
            "frame" when id is not null => _frames.GetValueOrDefault(id),
            "shot" when id is not null => Shots.FirstOrDefault(x => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase)),
            "check" when id is not null => (IFilmPiece?)Shots.FirstOrDefault(x => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase)) ?? _frames.GetValueOrDefault(id),
            _ => null,
        };
    }

    /// <summary>A text a tool call was given, trimmed; null when it wasn't.</summary>
    private static string? Arg(JsonObject args, string name) => args[name] is JsonValue v && v.TryGetValue<string>(out var s) && s.Trim().Length > 0 ? s.Trim() : null;
}
