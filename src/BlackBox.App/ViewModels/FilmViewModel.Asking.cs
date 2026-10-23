using System.Globalization;
using System.Text.Json.Nodes;
using BlackBox.Core.Agent;
using BlackBox.Core.Film;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BlackBox.App.ViewModels;

public sealed partial class FilmViewModel
{
    // ---- telling BlackBox what to do: in the chat the window belongs to ------------------------------

    /// <summary>What the user is typing to BlackBox about the piece picked.</summary>
    [ObservableProperty]
    private string _request = "";

    /// <summary>
    /// Sends what the user typed to the chat, about the piece picked (or the whole film): the agent
    /// changes it there and makes it again, in view. During a run it waits until the model reads it.
    /// </summary>
    [RelayCommand]
    private void Send()
    {
        var text = Request.Trim();
        if (text.Length == 0)
            return;
        Request = "";
        var piece = (Selected as IFilmPiece)?.Piece;
        ToChat(text, piece, piece is null ? WholeFilmNote() : PieceNote(piece));
    }

    /// <summary>Makes the piece picked as it is (no change asked for), or again.</summary>
    [RelayCommand]
    private void MakePicked()
    {
        if (Selected is not IFilmPiece { Piece: var piece })
            return;
        ToChat(piece.Kind == FilmPieceKind.Song ? "Make the song." : "Make it.", piece,
            $"Make it as the storyboard has it now: {Call(piece)}. {FixIt}Then say in a sentence or two how it came out.");
    }

    /// <summary>Another take of the piece picked: the same settings with a new seed, set here; the one there is kept as a take.</summary>
    [RelayCommand]
    private void AnotherTake()
    {
        if (Selected is not IFilmPiece picked)
            return;
        switch (picked)
        {
            case FrameItem f: f.Frame.Seed = Storyboard.NewSeed(); break;
            case ShotItem s: s.Shot.Seed = Storyboard.NewSeed(); break;
            case SoundItem: _board.Sound.Seed = Storyboard.NewSeed(); break;
        }
        _dirty = true;
        ToChat("Another take, please.", picked.Piece,
            $"Its seed in the storyboard is a new one already, so make it again as it is: {Call(picked.Piece)}. The one there now is kept as a take. {FixIt}Then say in a sentence or two how it came out.");
    }

    /// <summary>Looks at the piece picked again, as it is.</summary>
    [RelayCommand]
    private void CheckAgain()
    {
        if (Selected is not IFilmPiece { Piece: var piece } || piece.Kind == FilmPieceKind.Song)
            return;
        ToChat("Look at it again.", piece,
            $"Have a fresh look at it as it is: film action=check id={piece.Id} path=\"{_board.Folder}\". Say what the look found; change or make nothing unless they ask.");
    }

    /// <summary>The user picked a picture of their own for the frame picked: it goes in the storyboard, and the chat makes the frame from it.</summary>
    public void UseOwnPicture(string path)
    {
        if (Selected is not FrameItem frame)
            return;
        frame.TakeOwnPicture(path);
        ToChat("Use this picture of mine for it.", frame.Piece,
            $"The storyboard has their picture as the frame's file now: make the frame from it with {Call(frame.Piece)}, which cuts it to the film's frame. " +
            "Make again what starts or ends on it only if they ask.", [path]);
    }

    /// <summary>The user picked a sound file of their own as the film's soundtrack: nothing to make, so it is only kept in the storyboard.</summary>
    public void UseOwnSound(string path)
    {
        Sound.TakeOwnSound(path);
        SaveNow(force: true);
        Status = $"The soundtrack is your file {Path.GetFileName(path)} now.";
        RefreshStates();
        RefreshDetails();
    }

    /// <summary>Makes every piece not made or out of date, each looked at and fixed, then joins the film and looks at it: in the chat.</summary>
    [RelayCommand]
    private void MakeFilm()
    {
        if (_board.Shots.Count == 0 || _board.Shots.Any(s => s.Prompt.Trim().Length == 0))
        {
            Status = _board.Shots.Count == 0 ? "The film has no shots yet: say what film you want first." : "Some shots don't say what happens in them yet: pick them and say it.";
            return;
        }
        ToChat("Make the film.", null,
            $"Make what isn't made yet or is out of date, in the order film action=storyboard path=\"{_board.Folder}\" lists under To make (references and frames, the song, then the shots), " +
            "and read each piece's fresh look: when a look finds something wrong, change what the piece is made from (its prompt, its frames, the seed) and make it again, " +
            "up to twice for a piece. Then join the film (action=join), check it (action=check without id), fix the shots that check names once, join again and check again. " +
            "At the end, say what was made and what is still wrong, if anything.");
    }

    /// <summary>Joins the shots made so far, with the soundtrack, into the film: in the chat.</summary>
    [RelayCommand]
    private void Join() =>
        ToChat("Join the clips.", null, $"Join the clips made so far with the soundtrack: film action=join path=\"{_board.Folder}\". Nothing else.");

    /// <summary>Looks at the joined film again (its shots side by side and each cut) and fixes what the looks find: in the chat.</summary>
    [RelayCommand]
    private void LookAtFilm()
    {
        if (!HasFilm)
        {
            Status = "The film isn't joined yet: Make the film makes and joins it.";
            return;
        }
        ToChat("Look at the whole film.", null,
            $"Check the joined film: film action=check path=\"{_board.Folder}\" without id. Fix the shots it names once (each made again after changing what it is made from), " +
            "join again and check again; then say what is left.");
    }

    /// <summary>
    /// A message to the chat the window belongs to, where the agent does it in view: the user's
    /// words, shown there with the film and piece they are about, and for the model what they are
    /// about (the storyboard, saved first so it reads what the window shows) and what to do.
    /// </summary>
    private void ToChat(string words, FilmPiece? piece, string what, IReadOnlyList<string>? files = null)
    {
        SaveNow(force: true);
        var title = _board.Title.Trim().Length > 0 ? _board.Title.Trim() : Path.GetFileName(_board.Folder);
        var busy = _chat.IsBusy;
        _chat.SendFromFilm(new UserInput(words, files ?? [])
        {
            Via = $"Filmmaking · {title} · {(piece is null ? "the whole film" : Lower(ItemOf(piece)?.Title ?? piece.Id))}",
            Context = $"[From the Filmmaking window, about {Describe(piece)} of the film \"{title}\" (its storyboard: {_board.FilePath}). {what}]",
        });
        Status = busy ? "Sent to the chat: BlackBox reads it before its next step." : "Sent to the chat, where BlackBox does it.";
    }

    /// <summary>The piece as the model is told it: "shot s2 (the 2nd of 3, from 5 to 9 s)", "the reference f1 "Marco"", "the song".</summary>
    private string Describe(FilmPiece? piece)
    {
        switch (piece?.Kind)
        {
            case null:
                return "the whole film";
            case FilmPieceKind.Shot when _board.Shot(piece.Id) is { } shot:
                var (starts, _) = _board.Timing();
                var at = _board.Shots.IndexOf(shot);
                return string.Create(CultureInfo.InvariantCulture, $"shot {shot.Id} (shot {at + 1} of {_board.Shots.Count}, from {starts[at]:0.#} to {starts[at] + shot.Seconds:0.#} s)");
            case FilmPieceKind.Frame when _board.Frame(piece.Id) is { Reference: true } reference:
                return $"the reference {reference.Id}" + (reference.Name is { Length: > 0 } name ? $" \"{name}\"" : "");
            case FilmPieceKind.Frame:
                return $"frame {piece.Id}" + (ItemOf(piece) is { Title: var label } && label != piece.Id ? $" ({Lower(label)})" : "");
            default:
                return _board.Sound.Source == FilmSound.FromFile ? "the soundtrack" : "the song";
        }
    }

    /// <summary>The film tool call that makes a piece as the storyboard has it.</summary>
    private string Call(FilmPiece piece) => piece.Kind switch
    {
        FilmPieceKind.Frame => $"film action=frame id={piece.Id} path=\"{_board.Folder}\"",
        FilmPieceKind.Shot => $"film action=shot id={piece.Id} path=\"{_board.Folder}\", which makes the frames it starts and ends on first if they need it",
        _ => $"film action=sound path=\"{_board.Folder}\"",
    };

    /// <summary>
    /// What a piece made from the window gets after its fresh look, as the window's own director
    /// did for every piece it made: told only to say how it came out, the agent reports what the
    /// look found wrong and leaves the piece so.
    /// </summary>
    private const string FixIt = "When its fresh look finds something wrong, change what it is made from (its prompt, its frames, the seed) " +
                                 "and make it again, up to twice. ";

    /// <summary>
    /// What the model does with what is said about a piece: change that piece alone (a shot, its
    /// frames too) and make it again, as the window's own director did; the rest of the film stays.
    /// </summary>
    private string PieceNote(FilmPiece piece)
    {
        var scope = piece.Kind switch
        {
            FilmPieceKind.Shot => "that shot alone (its prompt, camera, length, line, model or seed), with the frames it starts and ends on and the lines' voice",
            FilmPieceKind.Song => "the sound alone (the song's style and lyrics, or the lines' voice)",
            _ => "that frame alone (its prompt, what it is made from, its model or seed)",
        };
        return $"What they say is about it: read the storyboard first (film action=storyboard path=\"{_board.Folder}\"), then change {scope}, unless they ask for more; " +
               $"then make it again ({Call(piece)}), unless they only asked a question. {FixIt}Say in a sentence or two what you changed and how it came out.";
    }

    /// <summary>
    /// What the model does with what is said about the whole film: lay it out or change anything,
    /// and make nothing, since the user makes each piece (or the whole film) when they want it.
    /// </summary>
    private string WholeFilmNote()
    {
        var note = $"What they say is about the whole film: read the storyboard first (film action=storyboard path=\"{_board.Folder}\"), then lay it out or change anything in it " +
                   "(references, frames, shots, lines, the song) with action=storyboard. Make nothing unless they ask: they make each piece, or the whole film, from the window. " +
                   "Say in a few sentences what you changed and what is to be made.";
        if (_board.Shots.All(s => s.Prompt.Trim().Length == 0))
            note += " The film is new: give it a title, a reference for each person or place that comes back, frames made from those, shots as long as their action " +
                    "(2 to 8 seconds) from different angles, each with its first frame and a last one when where it ends matters, the lines when people speak, " +
                    "and a song as long as the film when they want music. Take out the empty starting shot and frames (s1, f1, f2) if you don't use them.";
        return note;
    }

    private IFilmPiece? ItemOf(FilmPiece piece) => piece.Kind switch
    {
        FilmPieceKind.Frame => _frames.GetValueOrDefault(piece.Id),
        FilmPieceKind.Shot => Shots.FirstOrDefault(s => s.Id == piece.Id),
        _ => Sound,
    };

    // ---- what the chat is doing -------------------------------------------------------------------------

    /// <summary>What the bottom line says: what the window did, what went wrong.</summary>
    [ObservableProperty]
    private string _status = "";

    /// <summary>A run goes on in the chat: the panel says what it is doing, with Stop.</summary>
    [ObservableProperty]
    private bool _chatBusy;

    /// <summary>What the run in the chat is doing now ("Make shot s2 · KSampler, step 3 of 8").</summary>
    [ObservableProperty]
    private string _chatActivity = "";

    /// <summary>The window's title: whose add-on it is, the chat's title.</summary>
    [ObservableProperty]
    private string _windowTitle = "Filmmaking";

    /// <summary>The piece the film tool is making or looking at in the chat now, shown working on the timeline.</summary>
    private IFilmPiece? _working;

    /// <summary>Brings the chat window forward; set by the window that opened this one.</summary>
    public Action? ShowChatWindow { get; set; }

    [RelayCommand]
    private void ShowChat() => ShowChatWindow?.Invoke();

    /// <summary>Stops the run in the chat (what the window sent, or anything else it is doing).</summary>
    [RelayCommand]
    private void Stop() => _chat.StopCommand.Execute(null);

    /// <summary>Reads what the chat is doing, twice a second: the line in the panel, and the piece being made.</summary>
    private void WatchChat()
    {
        WindowTitle = "Filmmaking - " + _chat.WindowTitle;
        var doing = _chat.Doing();
        ChatBusy = doing is not null;
        ChatActivity = doing is null ? "" : $"In the chat: {doing}";
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

    /// <summary>Whether a film tool call is about this window's film: by its path, or none (the film the chat worked on last).</summary>
    private bool IsThisFilm(JsonObject args)
    {
        if (Arg(args, "path") is not { Length: > 0 } path)
            return true;
        path = path.Trim().Trim('"');
        try
        {
            var full = Path.GetFullPath(Path.GetFileName(path).Equals(Storyboard.FileName, StringComparison.OrdinalIgnoreCase) ? Path.GetDirectoryName(path)! : path);
            return string.Equals(full.TrimEnd('\\', '/'), _board.Folder.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase)
                   || !Path.IsPathRooted(path) && string.Equals(path, Path.GetFileName(_board.Folder), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    /// <summary>The piece a film tool call makes or looks at, from what it was given; null for the whole film.</summary>
    private IFilmPiece? PieceIn(JsonObject args)
    {
        var id = Arg(args, "id");
        return Arg(args, "action")?.ToLowerInvariant() switch
        {
            "sound" or "song" => Sound,
            "frame" when id is not null => _frames.GetValueOrDefault(id),
            "shot" when id is not null => Shots.FirstOrDefault(x => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase)),
            "check" when id is not null => (IFilmPiece?)Shots.FirstOrDefault(x => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase)) ?? _frames.GetValueOrDefault(id),
            _ => null,
        };
    }

    /// <summary>A text a tool call was given, trimmed; null when it wasn't.</summary>
    private static string? Arg(JsonObject args, string name) => args[name] is JsonValue v && v.TryGetValue<string>(out var s) && s.Trim().Length > 0 ? s.Trim() : null;

    public bool HasFilm => File.Exists(_board.FilmFile);
}
