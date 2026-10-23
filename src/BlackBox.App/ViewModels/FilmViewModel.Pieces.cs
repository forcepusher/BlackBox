using BlackBox.Core.Film;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BlackBox.App.ViewModels;

public sealed partial class FilmViewModel
{
    // ---- the pieces --------------------------------------------------------------------------------------------

    /// <summary>Builds the pieces from the storyboard, and the timeline with them, keeping what was picked and shown.</summary>
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
        Sounds.Clear();
        Tracks.Clear();
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
        foreach (var sound in _board.Sounds)
            Sounds.Add(new SoundItem(sound));
        for (var i = 0; i < _board.Tracks; i++)
            Tracks.Add(new TrackItem(i));
        _loading = false;
        Relabel();
        RefreshReferences();
        Relayout();
        RefreshStates();
        foreach (var p in new[] { nameof(FrameWidth), nameof(FrameHeight), nameof(Fps), nameof(LengthText), nameof(HasFilm), nameof(FilmName) })
            OnPropertyChanged(p);
    }

    /// <summary>
    /// The References row as the folder has it: the generated images (references of the storyboard,
    /// made or not) and the user's image files, by name, each a card. Built anew only when that
    /// changes: a job writes into the folder every second.
    /// </summary>
    private void RefreshReferences()
    {
        var cards = _board.Frames.Where(f => f.Reference).Select(f => (Path: Path.GetFullPath(_board.FramePicture(f)), Frame: _frames.GetValueOrDefault(f.Id)))
            .Concat(_board.ReferenceFiles().Where(p => string.Equals(Path.GetDirectoryName(p), Path.GetFullPath(_board.ReferencesFolder), StringComparison.OrdinalIgnoreCase))
                .Select(p => (Path: p, Frame: (FrameItem?)null)))
            .DistinctBy(c => c.Path, StringComparer.OrdinalIgnoreCase)
            .OrderBy(c => Path.GetFileName(c.Path), ByName).ToList();
        if (cards.Count == References.Count && cards.Zip(References).All(p => string.Equals(p.First.Path, p.Second.Path, StringComparison.OrdinalIgnoreCase) && ReferenceEquals(p.First.Frame, p.Second.Frame)))
            return;
        foreach (var old in References)
            if (old.Frame is null)
                old.Thumbnail?.Dispose();
        References.Clear();
        foreach (var (path, frame) in cards)
            References.Add(new ReferenceCard(path, frame) { IsSelected = Viewing is { } shown && SamePath(shown.Path, path) });
        Relayout();
    }

    /// <summary>Names each frame by where it is used: "Start of take 2", "End of take 1, start of take 2", or its name.</summary>
    public void Relabel()
    {
        foreach (var (id, item) in _frames)
        {
            var uses = new List<string>();
            for (var i = 0; i < _board.Shots.Count; i++)
            {
                if (string.Equals(_board.Shots[i].First, id, StringComparison.OrdinalIgnoreCase)) uses.Add($"start of take {i + 1}");
                if (string.Equals(_board.Shots[i].Last, id, StringComparison.OrdinalIgnoreCase)) uses.Add($"end of take {i + 1}");
            }
            item.Label = item.Frame.Name is { Length: > 0 } name ? name : uses.Count > 0 ? Capital(string.Join(", ", uses)) : item.Frame.Reference ? $"Reference {id}" : $"Frame {id}";
        }
    }

    /// <summary>A new take after the last one, going on from where it ends; the user then says in the chat what happens in it.</summary>
    [RelayCommand]
    private void AddShot()
    {
        SaveNow();
        var before = _board.Shots.LastOrDefault();
        // New frames and the take are made with what the ones before are: a copy, so changing one changes only it.
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

    /// <summary>What a new frame is made with: what the frames of the take it comes after are made with.</summary>
    private string? FrameModelNear(FilmShot? shot) =>
        _board.Frame(shot?.Last)?.Model ?? _board.Frame(shot?.First)?.Model ?? _board.Frames.LastOrDefault(f => f.Model is not null && !f.Reference)?.Model;

    /// <summary>What a new frame is made from: what the frames of the take it comes after are made from, so the same people stay the same.</summary>
    private List<string> FromNear(FilmShot? shot) =>
        [.. (_board.Frame(shot?.Last) ?? _board.Frame(shot?.First))?.From ?? []];

    // ---- versions ------------------------------------------------------------------------------------------------

    /// <summary>Puts the version shown in the viewer back in the film, with the settings it was made with.</summary>
    [RelayCommand]
    private void UseVersion()
    {
        if (Viewing is not { Version: { } kept, VersionOf: { Piece: var piece, IsWorking: false } })
            return;
        // Not while the chat makes anything of this film: a take makes its frames first, which don't
        // show as being made, and the frame it finished wrote over the version put back.
        if (_chat.FilmCall() is { } call && IsThisFilm(call.Args))
        {
            Status = "BlackBox is making a piece of this film in the chat: put a version back once it is done.";
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
            ShowError($"That version couldn't be put back: {ex.Message}", ex);
            return;
        }
        Status = $"Put version {kept.Number} back in the film; the one before it is a version now.";
        Rebuild();
        Show(ItemOf(piece));
    }

    // ---- how the piece shown was made ----------------------------------------------------------------------------

    /// <summary>How BlackBox asked for the piece shown: the prompt and settings it wrote, to read, not to edit.</summary>
    [ObservableProperty]
    private string _details = "";

    [ObservableProperty]
    private string _madeWith = "";

    /// <summary>What the last fresh look at the piece found (a song: how it sounds to its judge); empty when it wasn't looked at since it was made.</summary>
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
        var shown = Viewing?.Piece ?? Viewing?.VersionOf;
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
                if (Storyboard.Speaks(s)) lines.Add($"Says: {s.Line.Trim()} (in the voice: {(_board.Voice.Trim() is { Length: > 0 } v ? v : "picked by the voice model")})");
                lines.Add($"{s.Seconds:0.#} s · seed {s.Seed}");
                break;
            case SoundItem { Sound: var a }:
                if (a.Source == FilmSound.FromFile)
                    lines.Add($"Your sound file: {Path.GetFileName(_board.SoundFile(a))}");
                else
                {
                    lines.Add($"Style: {(a.Tags.Trim().Length > 0 ? a.Tags.Trim() : "(none yet)")}");
                    lines.Add($"Lyrics: {(a.Lyrics.Trim().Length > 0 ? a.Lyrics.Trim().ReplaceLineEndings(" / ") : "none (an instrumental)")}");
                    lines.Add($"Seed: {a.Seed}");
                }
                lines.Add($"Sound {a.Track + 1}, from {a.Start:0.0} s" + (a.Trim > 0 ? $", its first {a.Trim:0.0} s cut" : "") + (Math.Abs(a.Volume - 1) > 0.001 ? $", volume {a.Volume:0.##}" : ""));
                break;
        }
        // A version: what it was made with then, as kept with it.
        if (Viewing?.Version is { } take)
        {
            lines = [$"Version {take.Number} of {Lower(shown?.Title ?? "the piece")}, kept {take.Made.ToLocalTime():d MMM, HH:mm} when it was made again."];
            foreach (var (key, name) in new[] { ("prompt", "Prompt"), ("camera", "Camera"), ("tags", "Style"), ("seed", "Seed") })
                if (take.Settings?[key] is System.Text.Json.Nodes.JsonValue value && value.ToString().Trim() is { Length: > 0 } said)
                    lines.Add($"{name}: {said}");
        }
        Details = string.Join("\n", lines);
        var meta = Viewing?.Version?.Meta ?? (shown is { Piece: var piece } && piece.File(_board) is { } made && File.Exists(made) ? piece.Meta(_board) : null);
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
        _ => _board.Sound(piece.Id)?.Model,
    };

    private IFilmPiece? ItemOf(FilmPiece piece) => piece.Kind switch
    {
        FilmPieceKind.Frame => _frames.GetValueOrDefault(piece.Id),
        FilmPieceKind.Shot => Shots.FirstOrDefault(s => s.Id == piece.Id),
        _ => Sounds.FirstOrDefault(s => s.Id == piece.Id),
    };
}
