using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using BlackBox.Core.Film;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BlackBox.App.ViewModels;

public sealed partial class FilmViewModel
{
    // ---- the pieces --------------------------------------------------------------------------------------------

    public SoundItem Sound { get; private set; } = null!;

    /// <summary>Builds the items from the storyboard, keeping what was picked.</summary>
    private void Rebuild()
    {
        var picked = Selected switch { FrameItem f => "f:" + f.Id, ShotItem s => "s:" + s.Id, SoundItem => "song", _ => null };
        _loading = true;
        foreach (var shot in Shots)
            shot.Clip?.Dispose();
        Sound?.Player?.Dispose();
        foreach (var old in _frames.Values)
        {
            var (thumbnail, picture) = (old.Thumbnail, old.Picture);
            (old.Thumbnail, old.Picture) = (null, null);
            thumbnail?.Dispose();
            picture?.Dispose();
        }
        _frames.Clear();
        _thumbStamps.Clear();
        _clipStamps.Clear();
        Shots.Clear();
        RefLanes.Clear();
        foreach (var frame in _board.Frames)
            _frames[frame.Id] = new FrameItem(this, frame);
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
        // A row for each reference (a frame left over in no shot isn't shown: nothing uses it).
        foreach (var frame in _board.Frames.Where(f => f.Reference))
            RefLanes.Add(new RefLane(_frames[frame.Id]));
        Sound = new SoundItem(this, _board.Sound);
        _loading = false;
        Relabel();
        Relayout();
        RefreshStates();
        foreach (var p in new[] { nameof(Title), nameof(FrameWidth), nameof(FrameHeight), nameof(Fps), nameof(SoundVolume), nameof(ShotVolume), nameof(Sound), nameof(LengthText), nameof(HasFilm) })
            OnPropertyChanged(p);
        Select(picked switch
        {
            ['f', ':', .. var id] => _frames.GetValueOrDefault(id),
            ['s', ':', .. var id] => Shots.FirstOrDefault(s => s.Id == id),
            "song" => Sound,
            _ => null,
        });
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

    /// <summary>A new shot after the last one, going on from where it ends; the user then says what happens in it.</summary>
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
        Select(Shots.FirstOrDefault(s => s.Id == shot.Id));
    }

    /// <summary>A new reference (a character, a place, a thing); the user then says what it is.</summary>
    [RelayCommand]
    private void AddReference()
    {
        SaveNow();
        var frame = new FilmFrame
        {
            Id = _board.NewId("f"), Reference = true, Name = $"Reference {RefLanes.Count(r => r.Frame.Frame.Reference) + 1}", Seed = Storyboard.NewSeed(),
            Model = _board.Frames.LastOrDefault(f => f.Reference && f.Model is not null)?.Model ?? FrameModelNear(null),
        };
        _board.Frames.Add(frame);
        SaveNow(force: true);
        Rebuild();
        Select(_frames.GetValueOrDefault(frame.Id));
    }

    /// <summary>What a new frame is made with: what the frames of the shot it comes after are made with.</summary>
    private string? FrameModelNear(FilmShot? shot) =>
        _board.Frame(shot?.Last)?.Model ?? _board.Frame(shot?.First)?.Model ?? _board.Frames.LastOrDefault(f => f.Model is not null && !f.Reference)?.Model;

    /// <summary>What a new frame is made from: what the frames of the shot it comes after are made from, so the same people stay the same.</summary>
    private List<string> FromNear(FilmShot? shot) =>
        [.. (_board.Frame(shot?.Last) ?? _board.Frame(shot?.First))?.From ?? []];

    private bool Used(string id) =>
        _board.Shots.Any(s => string.Equals(s.First, id, StringComparison.OrdinalIgnoreCase) || string.Equals(s.Last, id, StringComparison.OrdinalIgnoreCase)) ||
        _board.Frames.Any(f => f.From.Contains(id, StringComparer.OrdinalIgnoreCase));

    /// <summary>Takes the picked shot or reference out of the film (a shot's frames go with it unless something else uses them).</summary>
    [RelayCommand]
    private void DeletePicked()
    {
        SaveNow();
        switch (Selected)
        {
            case ShotItem item:
                var at = _board.Shots.IndexOf(item.Shot);
                _board.Shots.Remove(item.Shot);
                foreach (var id in new[] { item.Shot.First, item.Shot.Last }.OfType<string>())
                    if (!Used(id))
                        _board.Frames.RemoveAll(f => f.Id.Equals(id, StringComparison.OrdinalIgnoreCase) && !f.Reference);
                SaveNow(force: true);
                Rebuild();
                Select(Shots.ElementAtOrDefault(Math.Min(at, Shots.Count - 1)));
                break;
            case FrameItem { Frame.Reference: true } reference:
                _board.Frames.Remove(reference.Frame);
                foreach (var f in _board.Frames)
                    f.From.RemoveAll(id => id.Equals(reference.Id, StringComparison.OrdinalIgnoreCase));
                SaveNow(force: true);
                Rebuild();
                Select(null);
                break;
        }
    }

    /// <summary>Moves the picked shot one place earlier or later.</summary>
    public void Move(int by)
    {
        if (Selected is not ShotItem item)
            return;
        var at = _board.Shots.IndexOf(item.Shot);
        var to = at + by;
        if (at < 0 || to < 0 || to >= _board.Shots.Count)
            return;
        SaveNow();
        _board.Shots.RemoveAt(at);
        _board.Shots.Insert(to, item.Shot);
        SaveNow(force: true);
        Rebuild();
        Select(Shots.FirstOrDefault(s => s.Id == item.Id));
    }

    [RelayCommand]
    private void MoveEarlier() => Move(-1);

    [RelayCommand]
    private void MoveLater() => Move(1);

    // ---- what is picked, and the panel to talk about it ---------------------------------------------------------

    /// <summary>What is picked: a frame, a shot, the song, the joined film, or nothing (the whole film).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFilmPicked), nameof(IsShotPicked), nameof(IsFramePicked), nameof(IsSongPicked), nameof(PickedShot), nameof(PickedFrame),
        nameof(PanelTitle), nameof(PanelSubtitle), nameof(AskLabel), nameof(AskHint), nameof(CanDelete), nameof(Viewer), nameof(DetailsVisible))]
    private object? _selected;

    public bool IsFilmPicked => Selected is null or JoinedFilm;
    public bool IsShotPicked => Selected is ShotItem;
    public bool IsFramePicked => Selected is FrameItem;
    public bool IsSongPicked => Selected is SoundItem;
    public ShotItem? PickedShot => Selected as ShotItem;
    public FrameItem? PickedFrame => Selected as FrameItem;
    public bool CanDelete => Selected is ShotItem or FrameItem { Frame.Reference: true };

    public string PanelTitle => Selected is IFilmPiece piece ? piece.Title : "Whole film";

    public string PanelSubtitle => Selected switch
    {
        ShotItem s => $"{Cut(s.Name, 48)} · {s.LengthText}" + (s.Index == 0 ? "" : s.Shot.First is not null && s.Shot.First == _board.Shots[s.Index - 1].Last ? $" · goes on from shot {s.Index}" : $" · {s.Shot.Transition} from shot {s.Index}"),
        FrameItem { Frame.Reference: true } f => "a reference · " + (_board.Shots.Where(s => Uses(s, f.Id)).Select(s => $"{_board.Shots.IndexOf(s) + 1}").ToList() is { Count: > 0 } used ? $"in shots {string.Join(", ", used)}" : "in no shot yet"),
        FrameItem f => f.Frame.From.Count > 0 ? $"made from {string.Join(", ", f.Frame.From.Select(id => _frames.GetValueOrDefault(id)?.Label ?? id))}" : "a picture",
        SoundItem => _board.Sound.Source == FilmSound.Song ? "made a minute long; the film uses its start" : "",
        _ => "nothing picked: what you say is about the film",
    };

    public string AskLabel => Selected is IFilmPiece piece ? $"Tell BlackBox what to change in {Lower(piece.Title)}" : "Tell BlackBox about the whole film";

    public string AskHint => Selected switch
    {
        ShotItem => "Slower, and he should smile at the end",
        FrameItem { Frame.Reference: true } => "Make him a little older, with grey in his stubble",
        FrameItem => "Closer on her face, and the street wet with rain",
        SoundItem => "Softer, just piano, and a little faster",
        _ => _board.Shots.All(s => s.Prompt.Trim().Length == 0)
            ? "A 20 second clip: a fox chases the moon's reflection across a frozen lake, gentle piano"
            : "Make it 30 seconds, and add a waiter who looks shocked",
    };

    /// <summary>Picks a piece (null: the whole film): the viewer shows it and the panel what was said about it.</summary>
    public void Select(object? item)
    {
        if (Selected is IFilmPiece old)
            old.IsSelected = false;
        if (item is IFilmPiece piece)
            piece.IsSelected = true;
        if (item is FrameItem frame)
            LoadPicture(frame);
        if (item is ShotItem shot && shot.First is { } first)
            LoadPicture(first);
        Selected = item;
        RefreshTalk();
        RefreshTakes();
        RefreshDetails();
    }

    [RelayCommand]
    private void PickFilm() => Select(null);

    [RelayCommand]
    private void PickSong() => Select(Sound);

    /// <summary>What the viewer shows: the piece picked (a frame's picture, a shot's clip, the song), else the film.</summary>
    public object? Viewer => Selected switch
    {
        null when HasFilm => _filmView ??= new JoinedFilm(_board.FilmFile) { Player = FilePreview.For(_board.FilmFile, _previews) },
        null => new PictureView(null, "Whole film", _board.Shots.All(s => s.Prompt.Trim().Length == 0)
            ? "Say what film you want in the panel: BlackBox lays out its shots, references and sound on the timeline. Nothing is made until you make it."
            : "Pick a piece on the timeline to see it and make it, or press Make what's missing."),
        _ => Selected,
    };

    private JoinedFilm? _filmView;

    /// <summary>The services the film's previews play with, for the full-screen viewer.</summary>
    public PreviewServices Previews => _previews;

    /// <summary>
    /// The film's pictures and videos as the full-screen viewer goes through them: the references,
    /// the frames, the shots' clips in order, then the joined film; only what is made.
    /// </summary>
    public List<MediaEntry> MediaInFilm()
    {
        var all = new List<MediaEntry>();
        foreach (var frame in _board.Frames.Where(f => f.Reference).Concat(_board.Frames.Where(f => !f.Reference)))
            if (_frames.TryGetValue(frame.Id, out var item) && File.Exists(_board.FramePicture(frame)))
                all.Add(new MediaEntry(item, _board.FramePicture(frame), null, item.Title));
        foreach (var shot in Shots)
            if (File.Exists(_board.ShotVideo(shot.Shot)))
                all.Add(new MediaEntry(shot, _board.ShotVideo(shot.Shot), null, shot.Title));
        if (HasFilm)
            all.Add(new MediaEntry((object?)_filmView ?? _board.FilmFile, _board.FilmFile, null, _board.Title.Trim().Length > 0 ? _board.Title.Trim() : "The film"));
        return all;
    }

    /// <summary>Where a picture or video clicked in the window is among <see cref="MediaInFilm"/>: by the piece it shows, or its file.</summary>
    public int MediaIndex(List<MediaEntry> entries, object clicked) => clicked switch
    {
        FilePreview preview => entries.FindIndex(e => e.Path is { } p && string.Equals(Path.GetFullPath(p), Path.GetFullPath(preview.Path), StringComparison.OrdinalIgnoreCase)),
        _ => entries.FindIndex(e => ReferenceEquals(e.Source, clicked)),
    };

    /// <summary>What was said about the piece picked, oldest first.</summary>
    public ObservableCollection<TalkItem> Talk { get; } = [];

    private string TalkKey => (Selected as IFilmPiece)?.Piece.Id ?? FilmTalk.Film;

    /// <summary>Nothing was said about the piece picked yet: the panel says what to do.</summary>
    public bool NoTalk => Talk.Count == 0;

    private void RefreshTalk()
    {
        Talk.Clear();
        foreach (var line in _talk.About(TalkKey))
            Talk.Add(new TalkItem(line.Text, line.Who == FilmTalk.You, false));
        if (_jobKey == TalkKey && Talk.Count > 0 && !Talk[^1].Mine)
            Talk[^1] = Talk[^1] with { Working = true };
        OnPropertyChanged(nameof(NoTalk));
    }

    private void Say(string key, string text, bool replace = false)
    {
        if (replace)
            _talk.ReplaceLast(key, text);
        else
            _talk.Add(key, FilmTalk.BlackBox, text);
        if (key == TalkKey)
            RefreshTalk();
    }

    private void SaveTalk()
    {
        try
        {
            if (_board.Folder.Length > 0 && Directory.Exists(_board.Folder))
                _talk.Save(_board.Folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>The takes of the piece picked: those kept from before, and the one in the film.</summary>
    public ObservableCollection<TakeItem> Takes { get; } = [];

    [ObservableProperty]
    private bool _hasTakes;

    private void RefreshTakes()
    {
        foreach (var take in Takes)
            take.Thumbnail?.Dispose();
        Takes.Clear();
        if (Selected is IFilmPiece { Piece: var piece } && piece.File(_board) is { } file && _board.Folder.Length > 0)
        {
            foreach (var take in FilmTakes.List(_board, piece))
                Takes.Add(new TakeItem(take, $"{take.Number}", Small(piece, take.File), false));
            if (File.Exists(file) && Takes.Count > 0)
                Takes.Add(new TakeItem(null, $"{Takes.Count + 1} · in the film", Small(piece, file), true));
        }
        HasTakes = Takes.Count > 1;

        // A frame as it is, a clip by a frame from its middle; a song has no picture.
        Bitmap? Small(FilmPiece piece, string path)
        {
            try
            {
                if (piece.Kind == FilmPieceKind.Frame)
                {
                    using var stream = File.OpenRead(path);
                    return Bitmap.DecodeToWidth(stream, 160, BitmapInterpolationMode.MediumQuality);
                }
                if (piece.Kind == FilmPieceKind.Shot && _previews.Decoder is { } decoder && FilmCheck.Poster(path, decoder, 160) is { } jpeg)
                {
                    using var stream = new MemoryStream(jpeg);
                    return new Bitmap(stream);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException) { }
            return null;
        }
    }

    /// <summary>Puts an earlier take back in the film, with the settings it was made with.</summary>
    [RelayCommand]
    private void UseTake(TakeItem? take)
    {
        if (take?.Take is not { } kept || Selected is not IFilmPiece { Piece: var piece } || IsWorking)
            return;
        SaveNow(force: true);
        try
        {
            _board = FilmTakes.Use(_board, piece, kept);
            _ownWrite = File.GetLastWriteTimeUtc(_board.FilePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FilmException)
        {
            Status = $"That take couldn't be put back: {ex.Message}";
            return;
        }
        _talk.Add(piece.Id, FilmTalk.BlackBox, $"Put take {kept.Number} back in the film; the one before it is a take now.");
        SaveTalk();
        Rebuild();
    }

    /// <summary>"How BlackBox asked for it": the prompt and settings it wrote for the piece, and what made it.</summary>
    [ObservableProperty]
    private string _details = "";

    /// <summary>Whether the panel shows how BlackBox asked for the piece.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DetailsVisible))]
    private bool _showDetails;

    public bool DetailsVisible => ShowDetails && Selected is IFilmPiece;

    [RelayCommand]
    private void ToggleDetails() => ShowDetails = !ShowDetails;

    [ObservableProperty]
    private string _madeWith = "";

    private void RefreshDetails()
    {
        var lines = new List<string>();
        switch (Selected)
        {
            case FrameItem { Frame: var f }:
                if (f.File is { Length: > 0 } file)
                    lines.Add($"Your picture: {file}");
                else
                {
                    lines.Add($"Prompt: {(f.Prompt.Trim().Length > 0 ? f.Prompt.Trim() : "(none yet)")}");
                    if (f.From.Count > 0) lines.Add($"Made from: {string.Join(", ", f.From.Select(id => _frames.GetValueOrDefault(id)?.Label ?? id))}");
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
        Details = string.Join("\n", lines);
        MadeWith = Selected is IFilmPiece { Piece: var piece } && Template(piece) is { } template
            ? $"Made with {template}" + (ModelWords(piece) is { } words ? $", for \"{words}\"" : ", picked by BlackBox")
            : "";
    }

    /// <summary>The template a piece was made with, from its workflow file, shortened to its name.</summary>
    private string? Template(FilmPiece piece)
    {
        try
        {
            return piece.Workflow(_board) is { } wf && File.Exists(wf) && System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(wf))?["blackbox"]?["template"]?.GetValue<string>() is { Length: > 0 } t
                ? t.StartsWith(FilmTemplates.CheckpointPrefix, StringComparison.OrdinalIgnoreCase) ? Path.GetFileNameWithoutExtension(t) + " (checkpoint)" : Path.GetFileName(t)
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    private string? ModelWords(FilmPiece piece) => piece.Kind switch
    {
        FilmPieceKind.Frame => _board.Frame(piece.Id)?.Model,
        FilmPieceKind.Shot => _board.Shot(piece.Id)?.Model,
        _ => _board.Sound.Model,
    };
}
