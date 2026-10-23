using BlackBox.Core.Film;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BlackBox.App.ViewModels;

public sealed partial class FilmViewModel
{
    // ---- the pieces --------------------------------------------------------------------------------------------

    public SoundItem Sound { get; private set; } = null!;

    /// <summary>Builds the pieces from the storyboard, and the tree and timeline with them, keeping what was picked and shown.</summary>
    private void Rebuild()
    {
        _loading = true;
        foreach (var shot in Shots)
            shot.Strip?.Dispose();
        foreach (var old in _frames.Values)
        {
            var thumbnail = old.Thumbnail;
            old.Thumbnail = null;
            thumbnail?.Dispose();
        }
        _frames.Clear();
        _thumbStamps.Clear();
        _clipStamps.Clear();
        Shots.Clear();
        foreach (var frame in _board.Frames)
            _frames[frame.Id] = new FrameItem(frame);
        for (var i = 0; i < _board.Shots.Count; i++)
        {
            var shot = _board.Shots[i];
            Shots.Add(new ShotItem(this, shot)
            {
                Index = i,
                First = shot.First is { } first ? _frames.GetValueOrDefault(first) : null,
                Last = shot.Last is { } last ? _frames.GetValueOrDefault(last) : null,
            });
        }
        Sound = new SoundItem(_board.Sound);
        _loading = false;
        Relabel();
        Relayout();
        RefreshStates();
        foreach (var p in new[] { nameof(Title), nameof(FrameWidth), nameof(FrameHeight), nameof(Fps), nameof(SoundVolume), nameof(ShotVolume), nameof(Sound), nameof(LengthText), nameof(HasFilm) })
            OnPropertyChanged(p);
    }

    /// <summary>Names each frame by where it is used: "Start of shot 2", "End of shot 1, start of shot 2", or its name.</summary>
    public void Relabel()
    {
        foreach (var (id, item) in _frames)
        {
            var uses = new List<string>();
            for (var i = 0; i < _board.Shots.Count; i++)
            {
                if (string.Equals(_board.Shots[i].First, id, StringComparison.OrdinalIgnoreCase)) uses.Add($"start of shot {i + 1}");
                if (string.Equals(_board.Shots[i].Last, id, StringComparison.OrdinalIgnoreCase)) uses.Add($"end of shot {i + 1}");
            }
            item.Label = item.Frame.Name is { Length: > 0 } name ? name : uses.Count > 0 ? Capital(string.Join(", ", uses)) : item.Frame.Reference ? $"Reference {id}" : $"Frame {id}";
        }
    }

    /// <summary>A new shot after the last one, going on from where it ends; the user then says in the chat what happens in it.</summary>
    [RelayCommand]
    private void AddShot()
    {
        SaveNow();
        var before = _board.Shots.LastOrDefault();
        // New frames and the shot are made with what the ones before are: a copy, so changing one changes only it.
        var model = FrameModelNear(before);
        string first;
        if (before?.Last is { } end)
            first = end;
        else
        {
            first = _board.NewId("f");
            _board.Frames.Add(new FilmFrame { Id = first, Seed = Storyboard.NewSeed(), Model = model, From = FromNear(before) });
        }
        var last = _board.NewId("f");
        _board.Frames.Add(new FilmFrame { Id = last, Seed = Storyboard.NewSeed(), Model = model, From = FromNear(before) });
        var shot = new FilmShot { Id = _board.NewId("s"), First = first, Last = last, Seconds = 5, Seed = Storyboard.NewSeed(), Model = before?.Model };
        _board.Shots.Add(shot);
        SaveNow(force: true);
        Rebuild();
        Show(Shots.FirstOrDefault(s => s.Id == shot.Id));
    }

    /// <summary>What a new frame is made with: what the frames of the shot it comes after are made with.</summary>
    private string? FrameModelNear(FilmShot? shot) =>
        _board.Frame(shot?.Last)?.Model ?? _board.Frame(shot?.First)?.Model ?? _board.Frames.LastOrDefault(f => f.Model is not null && !f.Reference)?.Model;

    /// <summary>What a new frame is made from: what the frames of the shot it comes after are made from, so the same people stay the same.</summary>
    private List<string> FromNear(FilmShot? shot) =>
        [.. (_board.Frame(shot?.Last) ?? _board.Frame(shot?.First))?.From ?? []];

    // ---- takes ---------------------------------------------------------------------------------------------------

    /// <summary>Puts the take shown in the viewer back in the film, with the settings it was made with.</summary>
    [RelayCommand]
    private void UseTake()
    {
        if (Viewing is not { Take: { } kept, TakeOf: { Piece: var piece, IsWorking: false } })
            return;
        // Not while the chat makes anything of this film: a shot makes its frames first, which don't
        // show as being made, and the frame it finished wrote over the take put back.
        if (_chat.FilmCall() is { } call && IsThisFilm(call.Args))
        {
            Status = "BlackBox is making a piece of this film in the chat: put a take back once it is done.";
            return;
        }
        SaveNow(force: true);
        try
        {
            _board = FilmTakes.Use(_board, piece, kept);
            _savedJson = _board.ToJson();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FilmException)
        {
            ShowError($"That take couldn't be put back: {ex.Message}", ex);
            return;
        }
        Status = $"Put take {kept.Number} back in the film; the one before it is a take now.";
        Rebuild();
        Show(ItemOf(piece));
    }

    // ---- how the piece shown was made ----------------------------------------------------------------------------

    /// <summary>How BlackBox asked for the piece shown: the prompt and settings it wrote, to read, not to edit.</summary>
    [ObservableProperty]
    private string _details = "";

    [ObservableProperty]
    private string _madeWith = "";

    /// <summary>What the last fresh look at the piece found (the song: how it sounds to its judge); empty when it wasn't looked at since it was made.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLook))]
    private string _look = "";

    public bool HasLook => Look.Length > 0;

    /// <summary>The last look found something wrong, to fix or to make again.</summary>
    [ObservableProperty]
    private bool _lookFoundFault;

    private void RefreshDetails()
    {
        var lines = new List<string>();
        var shown = Viewing?.Piece ?? Viewing?.TakeOf;
        switch (shown)
        {
            case FrameItem { Frame: var f }:
                if (f.File is { Length: > 0 } file)
                    lines.Add($"Your picture: {file}");
                else
                {
                    lines.Add($"Prompt: {(f.Prompt.Trim().Length > 0 ? f.Prompt.Trim() : "(none yet)")}");
                    if (f.From.Count > 0) lines.Add($"Made from: {string.Join(", ", f.From.Select(id => _frames.GetValueOrDefault(id)?.Label ?? Path.GetFileName(id)))}");
                    lines.Add($"Seed: {f.Seed}");
                }
                break;
            case ShotItem { Shot: var s }:
                lines.Add($"Prompt: {(s.Prompt.Trim().Length > 0 ? s.Prompt.Trim() : "(none yet)")}");
                if (s.Camera.Trim().Length > 0) lines.Add($"Camera: {s.Camera.Trim()}");
                if (Storyboard.Speaks(s)) lines.Add($"Says: {s.Line.Trim()} (in the voice: {(_board.Sound.Voice.Trim() is { Length: > 0 } v ? v : "picked by the voice model")})");
                else if (s.LipSync) lines.Add("Sings the song's part under it");
                lines.Add($"{s.Seconds:0.#} s · seed {s.Seed}");
                break;
            case SoundItem:
                var sound = _board.Sound;
                if (sound.Source == FilmSound.FromFile) lines.Add($"Your sound file: {sound.File}");
                else
                {
                    lines.Add($"Style: {(sound.Tags.Trim().Length > 0 ? sound.Tags.Trim() : "(none yet)")}");
                    lines.Add($"Lyrics: {(sound.Lyrics.Trim().Length > 0 ? sound.Lyrics.Trim().ReplaceLineEndings(" / ") : "none (an instrumental)")}");
                    lines.Add($"Seed: {sound.Seed}");
                }
                if (sound.Voice.Trim().Length > 0) lines.Add($"The lines' voice: {sound.Voice.Trim()}");
                break;
        }
        // A take: what it was made with then, as kept with it.
        if (Viewing?.Take is { } take)
        {
            lines = [$"Take {take.Number} of {Lower(shown?.Title ?? "the piece")}, kept {take.Made.ToLocalTime():d MMM, HH:mm} when it was made again."];
            foreach (var (key, name) in new[] { ("prompt", "Prompt"), ("camera", "Camera"), ("tags", "Style"), ("seed", "Seed") })
                if (take.Settings?[key] is System.Text.Json.Nodes.JsonValue value && value.ToString().Trim() is { Length: > 0 } said)
                    lines.Add($"{name}: {said}");
        }
        Details = string.Join("\n", lines);
        var meta = Viewing?.Take?.Meta ?? (shown is { Piece: var piece } && File.Exists(piece.File(_board)) ? piece.Meta(_board) : null);
        MadeWith = shown is { Piece: var p } && meta is not null && FilmMaker.MadeWith(meta) is { } t
            ? "Made with " + (t.StartsWith(FilmTemplates.CheckpointPrefix, StringComparison.OrdinalIgnoreCase) ? Path.GetFileNameWithoutExtension(t) + " (checkpoint)" : Path.GetFileName(t)) +
              (ModelWords(p) is { } words ? $", for \"{words}\"" : ", picked by BlackBox")
            : "";
        var look = FilmTakes.LookIn(meta);
        Look = look is not { } l ? "" : shown is SoundItem ? $"How it sounds: {l.Said}" : $"Looked at: {l.Said}";
        LookFoundFault = look?.Verdict == "redo";
    }

    private string? ModelWords(FilmPiece piece) => piece.Kind switch
    {
        FilmPieceKind.Frame => _board.Frame(piece.Id)?.Model,
        FilmPieceKind.Shot => _board.Shot(piece.Id)?.Model,
        _ => _board.Sound.Model,
    };

    private IFilmPiece? ItemOf(FilmPiece piece) => piece.Kind switch
    {
        FilmPieceKind.Frame => _frames.GetValueOrDefault(piece.Id),
        FilmPieceKind.Shot => Shots.FirstOrDefault(s => s.Id == piece.Id),
        _ => Sound,
    };
}
