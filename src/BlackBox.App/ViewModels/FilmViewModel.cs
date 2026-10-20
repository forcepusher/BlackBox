using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using BlackBox.Core.Comfy;
using BlackBox.Core.Film;
using BlackBox.Core.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BlackBox.App.ViewModels;

/// <summary>
/// The Filmmaking window: a film on one timeline (its references, its shots, its sound) under one
/// horizontal scroll, a viewer, and a panel to tell BlackBox in plain words what to make or change
/// in the piece picked, or in the whole film. BlackBox writes the prompts and picks the models
/// (<see cref="FilmDirector"/>); each piece is made on its own, looked at, and kept as a take when
/// it is made again. Everything is kept in the film folder's storyboard.json and talk.json, and what
/// the agent changes there shows here.
/// </summary>
public sealed partial class FilmViewModel : ObservableObject, IDisposable
{
    private readonly PreviewServices _previews;
    private readonly Action<string> _sendToChat;
    private readonly FilmMaker _maker;
    private readonly ComfyHub _hub;
    private Storyboard _board = new();
    private FilmTalk _talk = FilmTalk.Load("");
    private readonly Dictionary<string, FrameItem> _frames = new(StringComparer.OrdinalIgnoreCase);
    private readonly DispatcherTimer _saveTimer;
    private readonly DispatcherTimer _refreshTimer;
    private FileSystemWatcher? _watcher;
    private DateTime _ownWrite;
    private bool _loading;

    /// <summary>Where films go, a folder each: BlackBox's Data\films, next to the exe like the rest of its data.</summary>
    private readonly string _filmsRoot;

    /// <summary>Asks the chat model: what the user's words mean for a piece, and how a piece came out.</summary>
    private readonly FilmAsk? _ask;

    /// <param name="filmsRoot">Where films go (BlackBox's Data\films).</param>
    /// <param name="toolsDir">BlackBox's Data\tools, where the song judge is set up.</param>
    /// <param name="folder">The film to open; the latest changed one when null.</param>
    /// <param name="ask">Asks the chat model; without it the panel can only make what is there.</param>
    public FilmViewModel(PreviewServices previews, ComfyHub hub, Action<string> sendToChat, string filmsRoot, string toolsDir, string? folder, FilmAsk? ask = null)
    {
        _filmsRoot = filmsRoot;
        _previews = previews;
        _sendToChat = sendToChat;
        _ask = ask;
        _maker = new FilmMaker(hub, previews.Decoder) { Judge = new SongJudge(Path.Combine(toolsDir, "songeval")) };
        _hub = hub;
        _saveTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(400), DispatcherPriority.Background, (_, _) => SaveNow());
        _refreshTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(300), DispatcherPriority.Background, (_, _) =>
        {
            _refreshTimer!.Stop();
            OnFolderChanged();
        });
        RefreshFilms();
        var open = folder ?? Films.FirstOrDefault()?.Folder;
        if (open is not null && File.Exists(Path.Combine(open, Storyboard.FileName)))
            Open(open);
        else
            NewFilm();
    }

    // ---- the film --------------------------------------------------------------------------------------

    public ObservableCollection<FilmChoice> Films { get; } = [];

    [ObservableProperty]
    private FilmChoice? _selectedFilm;

    partial void OnSelectedFilmChanged(FilmChoice? value)
    {
        if (value is not null && !_loading && !string.Equals(value.Folder, _board.Folder, StringComparison.OrdinalIgnoreCase))
            Open(value.Folder);
    }

    private void RefreshFilms()
    {
        var films = Storyboard.Recent(_filmsRoot).ToList();
        if (_board.Folder.Length > 0 && !films.Contains(_board.Folder, StringComparer.OrdinalIgnoreCase))
            films.Insert(0, _board.Folder);
        _loading = true;
        Films.Clear();
        foreach (var f in films)
            Films.Add(new FilmChoice(f, TitleOf(f)));
        SelectedFilm = Films.FirstOrDefault(f => string.Equals(f.Folder, _board.Folder, StringComparison.OrdinalIgnoreCase));
        _loading = false;

        static string TitleOf(string folder)
        {
            try { return Storyboard.Load(folder, upgrade: false).Title is { Length: > 0 } t ? t : Path.GetFileName(folder); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FilmException) { return Path.GetFileName(folder); }
        }
    }

    public string Folder => _board.Folder;

    /// <summary>Opens the film in a folder (or the storyboard.json given).</summary>
    public void Open(string path)
    {
        SaveNow();
        try
        {
            _board = Storyboard.Load(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FilmException)
        {
            Status = $"That film can't be opened: {ex.Message}";
            return;
        }
        _talk = FilmTalk.Load(_board.Folder);
        Watch();
        Rebuild();
        RefreshFilms();
        OnPropertyChanged(nameof(Folder));
        Select(null);
    }

    /// <summary>
    /// A new film in BlackBox's Data\films. Its folder is made with the first change: opening the
    /// window to look leaves nothing behind. The user says what it is to be in the panel.
    /// </summary>
    [RelayCommand]
    private void NewFilm()
    {
        SaveNow();
        var root = _filmsRoot;
        var name = "New film";
        for (var n = 2; Directory.Exists(Path.Combine(root, name)); n++)
            name = $"New film {n}";
        _board = Storyboard.New(Path.Combine(root, name), name);
        _talk = FilmTalk.Load("");
        _ownWrite = default;
        _watcher?.Dispose();
        _watcher = null;
        Rebuild();
        RefreshFilms();
        OnPropertyChanged(nameof(Folder));
        Select(null);
    }

    [RelayCommand]
    private void ShowFolder()
    {
        if (Directory.Exists(_board.Folder))
            _ = Task.Run(() =>
            {
                try { Process.Start(new ProcessStartInfo(_board.Folder) { UseShellExecute = true })?.Dispose(); }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { }
            });
    }

    public string Title
    {
        get => _board.Title;
        set
        {
            if (_board.Title == value) return;
            _board.Title = value;
            OnPropertyChanged();
            Changed(this);
        }
    }

    public decimal? FrameWidth
    {
        get => _board.Width;
        set
        {
            if (value is not { } v) return;
            _board.Width = Math.Clamp((int)v / 2 * 2, 64, 4096);
            OnPropertyChanged();
            Changed(this);
        }
    }

    public decimal? FrameHeight
    {
        get => _board.Height;
        set
        {
            if (value is not { } v) return;
            _board.Height = Math.Clamp((int)v / 2 * 2, 64, 4096);
            OnPropertyChanged();
            Changed(this);
        }
    }

    public decimal? Fps
    {
        get => _board.Fps;
        set
        {
            if (value is not { } v) return;
            _board.Fps = Math.Clamp((int)v, 1, 120);
            OnPropertyChanged();
            Changed(this);
        }
    }

    /// <summary>How loud the soundtrack is, 0 to 100.</summary>
    public double SoundVolume
    {
        get => _board.SoundVolume * 100;
        set
        {
            _board.SoundVolume = Math.Clamp(value / 100, 0, 2);
            OnPropertyChanged();
            Changed(this);
        }
    }

    /// <summary>How loud the shots' own sound is under the soundtrack, 0 to 100.</summary>
    public double ShotVolume
    {
        get => (_board.ShotVolume ?? (_board.Sound.Source == FilmSound.None ? 1 : 0.5)) * 100;
        set
        {
            _board.ShotVolume = Math.Clamp(value / 100, 0, 2);
            OnPropertyChanged();
            Changed(this);
        }
    }

    /// <summary>How long the film is, and in how many shots.</summary>
    public string LengthText
    {
        get
        {
            var (_, length) = _board.Timing();
            var made = _board.Shots.Count(s => FilmMaker.IsCurrent(_board, s));
            return $"{_board.Width} × {_board.Height} · {_board.Fps} fps · {length:0.#} s in {_board.Shots.Count} shot{(_board.Shots.Count == 1 ? "" : "s")}" +
                   (_board.Shots.Count == 0 ? "" : made == _board.Shots.Count ? " · all made" : $" · {made} made");
        }
    }

    // ---- the timeline ------------------------------------------------------------------------------------

    /// <summary>Where the film's time starts on each row, in pixels.</summary>
    public const double Lead = 12;

    /// <summary>How many pixels a second takes on the timeline: the zoom.</summary>
    [ObservableProperty]
    private double _pps = 64;

    partial void OnPpsChanged(double value) => Relayout();

    public ObservableCollection<ShotItem> Shots { get; } = [];
    public ObservableCollection<RefLane> RefLanes { get; } = [];
    public ObservableCollection<LineItem> Lines { get; } = [];

    /// <summary>How wide the rows are: the film, the song where it runs past it, and room to make the last shot longer.</summary>
    [ObservableProperty]
    private double _timelineWidth = 800;

    [ObservableProperty]
    private double _length;

    /// <summary>Where the lines are heard, for the song's row to show it quieter there.</summary>
    [ObservableProperty]
    private IReadOnlyList<(double X, double Width)> _ducks = [];

    public double PlayheadX => Lead + Math.Max(0, PlayPosition) * Pps;

    /// <summary>Lays the timeline out again: each shot where it starts, each reference's runs, each line, the width.</summary>
    public void Relayout()
    {
        var (starts, length) = _board.Timing();
        Length = length;
        for (var i = 0; i < Shots.Count && i < starts.Count; i++)
        {
            Shots[i].X = Lead + starts[i] * Pps;
            Shots[i].BlockWidth = Math.Max(8, Shots[i].Shot.Seconds * Pps - 2);
        }
        foreach (var lane in RefLanes)
        {
            lane.Segments.Clear();
            var runs = new List<(int From, int To)>();
            for (var i = 0; i < _board.Shots.Count; i++)
                if (Uses(_board.Shots[i], lane.Frame.Id))
                {
                    if (runs.Count > 0 && runs[^1].To == i - 1)
                        runs[^1] = (runs[^1].From, i);
                    else
                        runs.Add((i, i));
                }
            foreach (var (from, to) in runs)
            {
                var x = Lead + starts[from] * Pps;
                var end = Lead + (starts[to] + _board.Shots[to].Seconds) * Pps;
                lane.Segments.Add(new RefSegment(lane.Frame, x, Math.Max(8, end - x - 2), $"{lane.Frame.Label} · " + (from == to ? $"shot {from + 1}" : $"shots {from + 1} to {to + 1}")));
            }
        }
        Lines.Clear();
        var ducks = new List<(double, double)>();
        for (var i = 0; i < Shots.Count && i < starts.Count; i++)
        {
            var shot = Shots[i].Shot;
            if (!Storyboard.Speaks(shot))
                continue;
            var seconds = Math.Min(LineSeconds(shot), Math.Max(0.3, shot.Seconds - FilmMaker.LineLead));
            var x = Lead + (starts[i] + FilmMaker.LineLead) * Pps;
            Lines.Add(new LineItem(Shots[i], x, Math.Max(12, seconds * Pps), $"“{shot.Line.Trim()}”"));
            ducks.Add((x, seconds * Pps));
        }
        Ducks = ducks;
        TimelineWidth = Lead + Math.Max(length, Math.Min(_sound.Seconds, length + 10)) * Pps + 240;
        OnPropertyChanged(nameof(PlayheadX));
        OnPropertyChanged(nameof(LengthText));
    }

    /// <summary>Whether a shot shows a reference: a frame it is made from is the reference, or is made from it.</summary>
    private bool Uses(FilmShot shot, string reference)
    {
        foreach (var id in new[] { shot.First, shot.LipSync ? null : shot.Last }.OfType<string>())
            if (MadeFrom(id, reference, 0))
                return true;
        return false;

        bool MadeFrom(string id, string target, int depth) =>
            id.Equals(target, StringComparison.OrdinalIgnoreCase) ||
            depth < 6 && _board.Frame(id) is { } f && f.From.Any(from => MadeFrom(from, target, depth + 1));
    }

    /// <summary>How long a line takes: as said, when it is; else about 2.5 words a second.</summary>
    private double LineSeconds(FilmShot shot)
    {
        var file = _board.ShotLine(shot);
        if (File.Exists(file))
        {
            var stamp = File.GetLastWriteTimeUtc(file);
            if (_lineLengths.TryGetValue(file, out var known) && known.Stamp == stamp)
                return known.Seconds;
            if (_previews.Decoder?.Probe(file) is { Duration.TotalSeconds: > 0 } info)
            {
                _lineLengths[file] = (stamp, info.Duration.TotalSeconds);
                return info.Duration.TotalSeconds;
            }
        }
        return Math.Max(0.8, shot.Line.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length / 2.5);
    }

    private readonly Dictionary<string, (DateTime Stamp, double Seconds)> _lineLengths = new(StringComparer.OrdinalIgnoreCase);

    [RelayCommand]
    private void ZoomIn() => Pps = Math.Min(240, Pps * 1.25);

    [RelayCommand]
    private void ZoomOut() => Pps = Math.Max(12, Pps / 1.25);

    // ---- the song under the shots ---------------------------------------------------------------------------

    /// <summary>The soundtrack's waveform (0 to 1 from start to end), and how long it is.</summary>
    [ObservableProperty]
    private float[]? _peaks;

    /// <summary>What the song's row says.</summary>
    [ObservableProperty]
    private string _soundLabel = "";

    private (string? File, DateTime Stamp, float[]? Peaks, double Seconds) _sound;
    private bool _readingSound;

    private void RefreshSound()
    {
        var file = _board.SoundtrackFile;
        var stamp = file is not null && File.Exists(file) ? File.GetLastWriteTimeUtc(file) : DateTime.MinValue;
        if (stamp == DateTime.MinValue)
            _sound = (null, DateTime.MinValue, null, 0);
        else if (!string.Equals(_sound.File, file, StringComparison.OrdinalIgnoreCase) || _sound.Stamp != stamp)
            ReadSound(file!, stamp);
        Peaks = _sound.Peaks;
        SoundLabel = _board.Sound.Source switch
        {
            FilmSound.Song => (File.Exists(_board.SongFile) ? "Song" : "Song · not made yet") + (_board.Sound.Tags.Trim() is { Length: > 0 } tags ? $" · {tags}" : "") +
                              (Ducks.Count > 0 ? " · quieter under the lines" : ""),
            FilmSound.FromFile => file is null ? "A sound file of yours · none picked yet" : $"Your sound file · {Path.GetFileName(file)}",
            _ => "No soundtrack: each shot keeps its own sound. Pick this row and say what music you want.",
        };
    }

    private void ReadSound(string file, DateTime stamp)
    {
        if (_readingSound || _previews.Decoder is not { } decoder)
            return;
        _readingSound = true;
        _ = Task.Run(() =>
        {
            float[]? peaks = null;
            double seconds = 0;
            try
            {
                using var reader = decoder.OpenAudio(file);
                if (reader is not null)
                {
                    var (mono, rate) = MediaPreview.ReadMono(reader, TimeSpan.FromMinutes(20));
                    seconds = mono.Length / (double)Math.Max(1, rate);
                    // A peak each tenth of a second, so the row is drawn the same at any zoom.
                    peaks = new float[Math.Max(1, (int)Math.Ceiling(seconds * 10))];
                    var per = Math.Max(1, mono.Length / peaks.Length);
                    for (var i = 0; i < peaks.Length; i++)
                    {
                        var peak = 0f;
                        for (var j = i * per; j < Math.Min(mono.Length, (i + 1) * per); j++)
                            peak = Math.Max(peak, Math.Abs(mono[j]));
                        peaks[i] = peak;
                    }
                    var top = peaks.DefaultIfEmpty(0).Max();
                    if (top > 0)
                        for (var i = 0; i < peaks.Length; i++)
                            peaks[i] /= top;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or NotSupportedException) { }
            Dispatcher.UIThread.Post(() =>
            {
                _readingSound = false;
                _sound = (file, stamp, peaks, seconds);
                SoundSeconds = seconds;
                RefreshSound();
                Relayout();
            });
        });
    }

    [ObservableProperty]
    private double _soundSeconds;

    // ---- playing the storyboard: the soundtrack with each shot's frames ---------------------------------------

    /// <summary>Where the playhead is, in seconds from the film's start.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PlayheadX), nameof(PlayTime))]
    private double _playPosition;

    public string PlayTime => $"{TimeSpan.FromSeconds(Math.Max(0, PlayPosition)):m\\:ss\\.f}";

    /// <summary>The storyboard frame under the playhead: a shot's clip frame or first frame, its last for the second half.</summary>
    [ObservableProperty]
    private Bitmap? _playFrame;

    [ObservableProperty]
    private string _playLabel = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PlayIcon))]
    private bool _isPlaying;

    private static readonly Avalonia.Media.Geometry PlayShape = Avalonia.Media.StreamGeometry.Parse("M8 5v14l11-7z");
    private static readonly Avalonia.Media.Geometry PauseShape = Avalonia.Media.StreamGeometry.Parse("M6 4h4v16H6zM14 4h4v16h-4z");

    public Avalonia.Media.Geometry PlayIcon => IsPlaying ? PauseShape : PlayShape;

    private IAudioOutput? _playOutput;
    private DispatcherTimer? _playTimer;
    private Stopwatch _playClock = new();
    private double _playFrom;

    /// <summary>Plays the soundtrack from the playhead with the frames switching as the shots do, or pauses it.</summary>
    [RelayCommand]
    private void PlayStrip()
    {
        if (IsPlaying)
        {
            StopPlaying();
            return;
        }
        var (_, length) = _board.Timing();
        StartPlaying(PlayPosition >= 0 && PlayPosition < length - 0.05 ? PlayPosition : 0);
    }

    /// <summary>The timeline was clicked: the playhead goes there (and plays on from there if it was playing).</summary>
    public void SeekStrip(double seconds)
    {
        var playing = IsPlaying;
        StopPlaying();
        PlayPosition = Math.Clamp(seconds, 0, Math.Max(0, Length));
        ShowFrameAt(PlayPosition);
        if (playing)
            StartPlaying(PlayPosition);
    }

    private void StartPlaying(double from)
    {
        StopPlaying();
        // The soundtrack from there, if there is one and the PC can play it; otherwise the frames go by the clock.
        if (_board.SoundtrackFile is { } file && File.Exists(file) && _previews.Decoder?.OpenAudio(file) is { } reader)
        {
            reader.Seek(TimeSpan.FromSeconds(from));
            var output = _previews.NewAudioOutput();
            if (output is null)
                reader.Dispose();
            else
                try
                {
                    output.Play(reader);
                    _playOutput = output;
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    output.Dispose(); // no speakers to open
                }
        }
        _playFrom = from;
        _playClock = Stopwatch.StartNew();
        IsPlaying = true;
        PlayPosition = from;
        ShowFrameAt(from);
        _playTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(40), DispatcherPriority.Render, (_, _) => PlayTick());
        _playTimer.Start();
    }

    private void PlayTick()
    {
        var (_, length) = _board.Timing();
        var at = _playFrom + (_playOutput?.Played ?? _playClock.Elapsed).TotalSeconds;
        if (at >= length)
        {
            StopPlaying();
            PlayPosition = length;
            return;
        }
        PlayPosition = at;
        ShowFrameAt(at);
    }

    private void StopPlaying()
    {
        _playTimer?.Stop();
        _playTimer = null;
        Interlocked.Exchange(ref _playOutput, null)?.Dispose();
        IsPlaying = false;
    }

    /// <summary>The frame of the shot at a time: its first frame, its last for the second half (when it has one).</summary>
    private void ShowFrameAt(double seconds)
    {
        var (starts, _) = _board.Timing();
        var at = -1;
        for (var i = 0; i < Shots.Count && i < starts.Count; i++)
            if (starts[i] <= seconds + 1e-6)
                at = i;
        if (at < 0)
        {
            PlayFrame = null;
            return;
        }
        var shot = Shots[at];
        var late = seconds - starts[at] >= shot.Shot.Seconds / 2 && shot.Last is not null && !shot.Shot.LipSync;
        var frame = late ? shot.Last : shot.First ?? shot.Last;
        PlayFrame = frame?.Thumbnail;
        PlayLabel = $"{shot.Title}, {(late ? "end" : "start")} · {seconds:0.0} s";
    }

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

    // ---- telling BlackBox what to do ----------------------------------------------------------------------------

    /// <summary>What the user is typing to BlackBox about the piece picked.</summary>
    [ObservableProperty]
    private string _request = "";

    /// <summary>Sends what the user typed: BlackBox changes the piece (or the film) and makes what it should.</summary>
    [RelayCommand]
    private async Task Send()
    {
        var text = Request.Trim();
        if (text.Length == 0)
            return;
        if (IsWorking)
        {
            Status = "BlackBox is still busy with the last thing: wait for it, or stop it.";
            return;
        }
        if (_ask is null)
        {
            Status = "BlackBox's chat model isn't set up, so it can't be asked: set a server in the main window first.";
            return;
        }
        var ask = _ask;
        Request = "";
        var picked = Selected as IFilmPiece;
        var piece = picked?.Piece;
        var key = piece?.Id ?? FilmTalk.Film;
        _talk.Add(key, FilmTalk.You, text);
        Say(key, "Thinking it over…");
        SaveNow(force: true);
        SaveTalk();
        await RunJobAsync(key, picked, async (progress, ct) =>
        {
            IReadOnlyList<FilmOption> installed = [];
            if (piece is not null)
            {
                progress("Looking at what ComfyUI has installed…");
                installed = await InstalledAsync(piece, progress, ct);
            }
            var picture = await Task.Run(() => PictureOf(piece), ct);
            progress("Thinking it over…");
            var direction = await FilmDirector.DirectAsync(_board, piece, text, _talk, installed, picture, ask, ct);
            var changed = direction.Board != _board;
            if (changed)
            {
                var title = _board.Title;
                _board = direction.Board;
                _dirty = true;
                SaveNow(force: true);
                Rebuild();
                if (_board.Title != title)
                    RefreshFilms();
            }
            var reply = direction.Reply + (direction.Notes.Count > 0 ? " " + string.Join(" ", direction.Notes.Distinct()) : "");
            if (piece is null && changed && Missing() is not null)
                reply += " Pick a piece to make it, or press Make what's missing.";
            Say(key, reply, replace: true);
            SaveTalk();
            foreach (var next in direction.Make)
                await MakePieceAsync(next, key, ct);
        });
    }

    /// <summary>Makes the piece picked as it is (no change asked for), or again.</summary>
    [RelayCommand]
    private async Task MakePicked()
    {
        if (Selected is not IFilmPiece picked || IsWorking)
            return;
        await RunJobAsync(picked.Piece.Id, picked, (_, ct) => MakePieceAsync(picked.Piece, picked.Piece.Id, ct));
    }

    /// <summary>Another take of the piece picked: the same settings with a new seed.</summary>
    [RelayCommand]
    private async Task AnotherTake()
    {
        if (Selected is not IFilmPiece picked || IsWorking)
            return;
        switch (picked)
        {
            case FrameItem f: f.Frame.Seed = Storyboard.NewSeed(); break;
            case ShotItem s: s.Shot.Seed = Storyboard.NewSeed(); break;
            case SoundItem: _board.Sound.Seed = Storyboard.NewSeed(); break;
        }
        _talk.Add(picked.Piece.Id, FilmTalk.You, "Another take, please.");
        SaveNow(force: true);
        await RunJobAsync(picked.Piece.Id, picked, (_, ct) => MakePieceAsync(picked.Piece, picked.Piece.Id, ct));
    }

    /// <summary>The user picked a picture of their own for the frame picked: it is taken at once, cut to the film's frame.</summary>
    public async Task UseOwnPicture(string path)
    {
        if (Selected is not FrameItem frame || IsWorking)
            return;
        frame.TakeOwnPicture(path);
        _talk.Add(frame.Id, FilmTalk.You, $"Use my picture {Path.GetFileName(path)}.");
        SaveNow(force: true);
        await RunJobAsync(frame.Id, frame, (_, ct) => MakePieceAsync(frame.Piece, frame.Id, ct));
    }

    /// <summary>The user picked a sound file of their own as the film's soundtrack.</summary>
    public void UseOwnSound(string path)
    {
        Sound.TakeOwnSound(path);
        _talk.Add(FilmTalk.Song, FilmTalk.You, $"Use my sound file {Path.GetFileName(path)}.");
        SaveNow(force: true);
        SaveTalk();
        RefreshStates();
        RefreshTalk();
        RefreshDetails();
    }

    /// <summary>Looks at the piece picked again, as it is.</summary>
    [RelayCommand]
    private async Task CheckAgain()
    {
        if (Selected is not IFilmPiece picked || IsWorking || _ask is null)
            return;
        var key = picked.Piece.Id;
        Say(key, "Looking at it…");
        await RunJobAsync(key, picked, async (_, ct) =>
        {
            var check = await FilmDirector.CheckAsync(_board, picked.Piece, _previews.Decoder, _ask, ct);
            if (check is { } c && picked.Piece.Workflow(_board) is { } wf)
                FilmTakes.RememberLook(wf, c.Verdict, c.Said);
            if (check?.Reason is not null)
                Say(key, check.Value.Said + " Say what to change, or ask for another take.", replace: true);
            else
                Say(key, check?.Said ?? "It couldn't be looked at: it isn't made yet, or the chat model doesn't see pictures.", replace: true);
            SaveTalk();
        });
    }

    /// <summary>Looks at the joined film again (its shots side by side and each cut) and fixes what the looks find.</summary>
    [RelayCommand]
    private async Task LookAtFilm()
    {
        if (IsWorking || _ask is null)
            return;
        if (!HasFilm)
        {
            Status = "The film isn't joined yet: Make what's missing makes and joins it.";
            return;
        }
        await RunJobAsync(FilmTalk.Film, null, async (_, ct) =>
        {
            await LookAtFilmAsync(ct);
            SaveTalk();
        });
    }

    /// <summary>How many times a piece the look finds wrong is fixed and made again before the user is told what is left.</summary>
    private const int Fixes = 2;

    /// <summary>
    /// Makes a piece: the one there kept as a take, the new one made as the storyboard says, and a
    /// fresh look at it, all said in the piece's talk (and <paramref name="key"/>'s, where the request was).
    /// What the look finds wrong is fixed and made again, up to <see cref="Fixes"/> times: the user
    /// found a pass the look should have failed dismissive, and a flagged piece left for them lenient.
    /// </summary>
    /// <param name="round">How many fixes came before this make.</param>
    private async Task MakePieceAsync(FilmPiece piece, string key, CancellationToken ct, int round = 0)
    {
        // A shot starts on a picture a picture model makes: one without gets a first frame, and its frames are made first.
        if (piece.Kind == FilmPieceKind.Shot && _board.Shot(piece.Id) is { } shot)
        {
            if (_board.Frame(shot.First) is null && _board.AddFirstFrames().Count > 0)
            {
                _dirty = true;
                SaveNow(force: true);
                Rebuild();
            }
            foreach (var id in new[] { shot.First, shot.LipSync ? null : shot.Last }.OfType<string>().Distinct())
                if (_board.Frame(id) is { } frame && !FilmMaker.IsCurrent(_board, frame) && (frame.File is { Length: > 0 } || frame.Prompt.Trim().Length > 0))
                    await MakePieceAsync(FilmPiece.Of(frame), key, ct, Fixes);
        }
        string? fix = null;
        var item = ItemOf(piece);
        var label = Lower(item?.Title ?? piece.Id);
        if (item is not null)
            item.IsWorking = true;
        try
        {
            FilmTakes.Keep(_board, piece);
            Say(key, $"Making {label}…");
            var clock = Stopwatch.StartNew();
            void Progress(string s) => Dispatcher.UIThread.Post(() =>
            {
                if (item is not null)
                    item.State = s;
                _talk.ReplaceLast(key, $"Making {label}: {s}");
                if (key == TalkKey && Talk.Count > 0)
                    Talk[^1] = new TalkItem($"Making {label}: {s} · {FilePreview.Clock(clock.Elapsed)}", false, true);
            });
            FilmMade made = piece.Kind switch
            {
                FilmPieceKind.Frame => await _maker.MakeFrameAsync(_board, _board.Frame(piece.Id)!, Progress, CancellationToken.None, ct),
                FilmPieceKind.Shot => await _maker.MakeShotAsync(_board, _board.Shot(piece.Id)!, Progress, CancellationToken.None, ct),
                _ => await _maker.MakeSongAsync(_board, Progress, CancellationToken.None, ct, takes: 3),
            };
            if (piece.Kind == FilmPieceKind.Song)
            {
                _songScore = (made.File, File.GetLastWriteTimeUtc(made.File), made.Score);
                _dirty = true;
                SaveNow(force: true); // the seed of the song kept
            }
            var said = $"Made {label} in {FilePreview.Clock(made.Took)}" + (made.Template is { } t ? $" with {Short(t)}" : "") + "." +
                       (made.Score is { } score ? $" {Capital(score.Describe())}." : "") +
                       string.Concat(made.Notes.Where(Worth).Take(3).Select(n => " " + n));
            Say(key, said, replace: true);
            if (_ask is not null && piece.Kind != FilmPieceKind.Song)
            {
                Say(key, said + " Looking at it…", replace: true);
                if (item is not null)
                    item.State = "Looking at it…";
                var check = await FilmDirector.CheckAsync(_board, piece, _previews.Decoder, _ask, ct);
                if (check is { } c)
                {
                    if (piece.Workflow(_board) is { } wf)
                        FilmTakes.RememberLook(wf, c.Verdict, c.Said);
                    fix = c.Reason;
                    Say(key, said + " " + c.Said + (fix is null ? "" : round < Fixes ? $" Fixing it ({round + 1} of {Fixes})…" :
                        $" It still isn't right after {Fixes} fixes: say what to change, or pick an earlier take."), replace: true);
                }
                else
                    Say(key, said, replace: true);
            }
            if (key != piece.Id)
                _talk.Add(piece.Id, FilmTalk.BlackBox, _talk.About(key)[^1].Text);
        }
        finally
        {
            if (item is not null)
                item.IsWorking = false;
            SaveTalk();
            RefreshStates();
            if (Selected is IFilmPiece { Piece: var picked } && picked == piece)
            {
                RefreshTakes();
                RefreshDetails();
            }
        }
        if (fix is not null && round < Fixes && !ct.IsCancellationRequested)
            await FixAsync(piece, key, fix, ct, round);
    }

    /// <summary>
    /// What the look found wrong with a piece, handed to BlackBox to change what it is made from
    /// (its prompt, its frames, a new seed), then made again and looked at again.
    /// </summary>
    /// <param name="seen">What the look saw it on, when not the piece alone (the film's cuts), and what that picture is.</param>
    private async Task FixAsync(FilmPiece piece, string key, string found, CancellationToken ct, int round, (byte[] Picture, string Text)? seen = null)
    {
        if (_ask is not { } ask)
            return;
        var installed = await InstalledAsync(piece, _ => { }, ct);
        var picture = seen?.Picture ?? await Task.Run(() => PictureOf(piece), ct);
        // A shot between two frames that disagree on what stays put moves it: the fox film's frames put
        // the moon "in the upper left" and "directly above", and three takes of rewording the action
        // couldn't hold it still.
        var frames = piece.Kind == FilmPieceKind.Shot && _board.Shot(piece.Id) is { LipSync: false } shot && _board.Frame(shot.First) is { } first && _board.Frame(shot.Last) is { } last
            ? $" Its first frame ({first.Id}) is: \"{first.Prompt.Trim()}\". Its last frame ({last.Id}) is: \"{last.Prompt.Trim()}\". Compare where the things that stay put " +
              "(the moon, the trees, the horizon, where someone stands) and the camera's angle are in the two: the shot moves whatever differs, so if they differ, " +
              $"that is the cause: change the last frame ({last.Id}) to agree with the first, keeping only the change the shot is for."
            // A shot's last frame made afresh from the references lost the first's framing: a girl's
            // mailbox moved and grew, and the fix that dropped the first frame from "from" made it worse.
            : piece.Kind == FilmPieceKind.Frame && _board.Shots.FirstOrDefault(s => !s.LipSync && s.Last == piece.Id && s.First is not null && s.First != piece.Id) is { } ending
            ? $" It is the last frame of {ending.Id}, which moves from {ending.First} to it: keep it made from {ending.First} (in its \"from\", with the references), " +
              $"so it keeps {ending.First}'s framing and everything that stays put, and say in its prompt only what the shot changes."
            : "";
        var direction = await FilmDirector.DirectAsync(_board, piece,
            $"A fresh look at it as just made found this wrong: {found}. Change what it is made from so that it doesn't happen again, keeping what was asked for." + (seen is { Text: var text } ? " " + text : "") + frames +
            " Otherwise write the action so a video model can't get it wrong, or only give it a new seed if nothing else would help. Keep the model it is made with unless the user asked for another.",
            _talk, installed, picture, ask, ct, think: true);
        var board = direction.Board;
        // Made again whatever was changed: with no change, at least another seed.
        if (piece.IsCurrent(board))
        {
            switch (piece.Kind)
            {
                case FilmPieceKind.Frame when board.Frame(piece.Id) is { } f: f.Seed = Storyboard.NewSeed(); break;
                case FilmPieceKind.Shot when board.Shot(piece.Id) is { } s: s.Seed = Storyboard.NewSeed(); break;
            }
        }
        _board = board;
        _dirty = true;
        SaveNow(force: true);
        Rebuild();
        Say(key, direction.Reply);
        SaveTalk();
        foreach (var other in direction.Make.Where(p => p != piece && !p.IsCurrent(_board)))
            await MakePieceAsync(other, key, ct, Fixes);
        await MakePieceAsync(piece, key, ct, round + 1);
    }

    /// <summary>A note from making a piece worth telling the user: not what is so by design (a lip-synced shot made from its first frame alone).</summary>
    private static bool Worth(string note) =>
        !note.Contains("no place for the size", StringComparison.Ordinal) && !note.Contains("made from its first frame only", StringComparison.Ordinal) &&
        !note.StartsWith("Using the ComfyUI", StringComparison.Ordinal) && !note.StartsWith("Started ComfyUI", StringComparison.Ordinal);

    /// <summary>The next piece not made or out of date, in the order they have to be made: references, frames, the song, shots.</summary>
    private FilmPiece? Missing()
    {
        bool Stale(FilmFrame f) => !FilmMaker.IsCurrent(_board, f);
        var needed = _board.Shots.SelectMany(s => new[] { s.First, s.LipSync ? null : s.Last }).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
        // The frames shots need, and what those are made from, a reference before what is made from it.
        var frames = new List<FilmFrame>();
        void Need(string id, int depth)
        {
            if (depth > 6 || _board.Frame(id) is not { } f || frames.Contains(f))
                return;
            foreach (var from in f.From)
                Need(from, depth + 1);
            frames.Add(f);
        }
        foreach (var id in needed)
            Need(id, 0);
        foreach (var f in _board.Frames.Where(f => f.Reference))
            Need(f.Id, 0);
        if (frames.FirstOrDefault(f => Stale(f) && (f.File is { Length: > 0 } || f.Prompt.Trim().Length > 0) && f.From.All(id => _board.Frame(id) is not { } src || !Stale(src))) is { } frame)
            return FilmPiece.Of(frame);
        if (_board.Sound.Source == FilmSound.Song && _board.Sound.Tags.Trim().Length > 0 && !FilmMaker.SongIsCurrent(_board))
            return FilmPiece.Song;
        return _board.Shots.FirstOrDefault(s => !FilmMaker.IsCurrent(_board, s) && s.Prompt.Trim().Length > 0 &&
                                                new[] { s.First, s.LipSync ? null : s.Last }.OfType<string>().All(id => _board.Frame(id) is not { } f || !Stale(f))) is { } shot
            ? FilmPiece.Of(shot) : null;
    }

    /// <summary>Makes every piece not made or out of date, one after another, each looked at, then joins the film.</summary>
    [RelayCommand]
    private async Task MakeMissing()
    {
        if (IsWorking)
            return;
        if (_board.Shots.Any(s => s.Prompt.Trim().Length == 0))
        {
            Status = "Some shots don't say what happens in them yet: pick them and say it.";
            return;
        }
        // Every shot starts on a picture a picture model makes.
        if (_board.AddFirstFrames() is { Count: > 0 } added)
        {
            _dirty = true;
            SaveNow(force: true);
            Rebuild();
            Say(FilmTalk.Film, $"{(added.Count == 1 ? "A shot" : "Some shots")} had no first frame, so I gave {(added.Count == 1 ? "it one" : "each one")} " +
                               $"({string.Join(", ", added.Select(f => f.Id))}) from what happens in {(added.Count == 1 ? "it" : "them")}: a picture model makes what a shot starts on.");
        }
        if (Missing() is null && Joined())
        {
            Status = "Everything is made and joined.";
            return;
        }
        Say(FilmTalk.Film, Missing() is null ? "Everything is made: joining the film and looking at it…" : "Making what's missing, one piece after another…");
        await RunJobAsync(FilmTalk.Film, null, async (_, ct) =>
        {
            var made = 0;
            var tried = new HashSet<FilmPiece>();
            while (Missing() is { } next && tried.Add(next))
            {
                Say(FilmTalk.Film, $"Making what's missing: {Lower(ItemOf(next)?.Title ?? next.Id)} now ({made} made so far). Pick it to watch it being made.", replace: true);
                await MakePieceAsync(next, next.Id, ct);
                made++;
            }
            Say(FilmTalk.Film, $"Made {made} piece{(made == 1 ? "" : "s")}." + (Missing() is { } left ? $" {Capital(Lower(ItemOf(left)?.Title ?? left.Id))} couldn't be made: pick it to see why." : " Joining the film…"), replace: true);
            if (Missing() is null)
            {
                await JoinAsync(ct);
                await LookAtFilmAsync(ct);
            }
            SaveTalk();
        });
    }

    /// <summary>Whether the film is joined from the clips and soundtrack there are now.</summary>
    private bool Joined()
    {
        if (!HasFilm)
            return false;
        var newest = _board.Shots.Select(s => _board.ShotVideo(s)).Append(_board.SoundtrackFile).Where(p => p is not null && File.Exists(p))
            .Select(p => File.GetLastWriteTimeUtc(p!)).DefaultIfEmpty(DateTime.MinValue).Max();
        return File.GetLastWriteTimeUtc(_board.FilmFile) >= newest;
    }

    /// <summary>
    /// Fresh looks at the joined film: a frame from each shot side by side, and each cut, the frame
    /// before beside the frame after. They see what no look at one shot can: a fix that moved shot 2
    /// of the fox film to an open clearing with the moon on the other side passed all of shot 2's own
    /// looks. The shots they name are fixed and made again and the film joined again, once; then
    /// looked at again, to say what is left.
    /// </summary>
    private async Task LookAtFilmAsync(CancellationToken ct, bool fix = true)
    {
        if (_ask is not { } ask || _previews.Decoder is not { } decoder || !HasFilm)
            return;
        Say(FilmTalk.Film, fix ? "Looking at the whole film…" : "Looking at the whole film again…");
        var look = await Task.Run(() => FilmCheck.Film(_board, _board.FilmFile, decoder), ct);
        var verdict = await FilmCheck.JudgeFilmAsync(_board, look,
            (pictures, question, c) => ask("You look at pictures carefully and say plainly what you see, faults included. You don't praise what isn't good.", question, pictures, true, c), ct);
        if (verdict is not { Reason: { } reason })
        {
            Say(FilmTalk.Film, verdict is null ? "The whole film couldn't be looked at."
                : "Looked at the whole film: the same people and places all through and across every cut, and each shot shows what it should.", replace: true);
            return;
        }
        var named = verdict.Redo.Select(_board.Shot).OfType<FilmShot>().Select(FilmPiece.Of).ToList();
        if (named.Count == 0 || !fix)
        {
            Say(FilmTalk.Film, $"The look at the whole film found: {reason}." + (fix ? " Say what to change." : $" It still isn't right after fixing: say what to change, or pick a shot and say it."), replace: true);
            return;
        }
        Say(FilmTalk.Film, $"The look at the whole film found: {reason}. Fixing {string.Join(", ", named.Select(p => Lower(ItemOf(p)?.Title ?? p.Id)))}…", replace: true);
        foreach (var piece in named)
        {
            var at = _board.Shots.FindIndex(s => s.Id == piece.Id);
            var cut = look.Cuts?.FirstOrDefault(c => c.At == at) ?? look.Cuts?.FirstOrDefault(c => c.At == at + 1);
            await FixAsync(piece, piece.Id, $"(seen with the other shots of the film) {reason}", ct, 0, cut is null ? null : (cut.Picture,
                "The picture shows a cut of the film: on top the last frame of the shot before, below it the first frame of the shot after. " +
                $"Make {piece.Id}'s frames agree with the shots next to it unless the story moves on: the same kind of place, the moon, sun or light on the same side and the same size, " +
                "the same time and weather, the same people looking the same, framed as the story needs; and keep its last frame made from its first, so the shot stays put."));
        }
        if (Missing() is null)
        {
            await JoinAsync(ct);
            await LookAtFilmAsync(ct, fix: false);
        }
    }

    /// <summary>What is installed for the kinds of job a piece can be, for the model to name.</summary>
    private async Task<IReadOnlyList<FilmOption>> InstalledAsync(FilmPiece piece, Action<string> progress, CancellationToken ct)
    {
        try
        {
            _maker.Use();
            var api = await _hub.ConnectAsync(null, [], progress, ct);
            var jobs = piece.Kind switch
            {
                FilmPieceKind.Frame => new[] { FilmJob.Frame, FilmJob.Edit },
                FilmPieceKind.Song => [FilmJob.Song],
                _ => [FilmJob.Shot, FilmJob.ShotFromFirst, FilmJob.ShotFromText, FilmJob.LipSync],
            };
            var all = new List<FilmOption>();
            foreach (var job in jobs)
                all.AddRange((await FilmTemplates.OptionsAsync(_hub, api, job, ct)).Where(o => all.All(a => a.Name != o.Name)));
            return all;
        }
        catch (Exception ex) when (ex is ComfyApiException or IOException or FilmException)
        {
            return [];
        }
    }

    /// <summary>The piece as it is, for the model to see: a frame's picture, a shot's sheet (or its first frame).</summary>
    private byte[]? PictureOf(FilmPiece? piece)
    {
        try
        {
            switch (piece?.Kind)
            {
                case FilmPieceKind.Frame when _board.Frame(piece.Id) is { } f && File.Exists(_board.FramePicture(f)):
                    return FilmCheck.Picture(_board.FramePicture(f), 768);
                case FilmPieceKind.Shot when _board.Shot(piece.Id) is { } s:
                    if (File.Exists(_board.ShotVideo(s)) && _previews.Decoder is { } decoder)
                        return FilmCheck.Shot(_board, s, _board.ShotVideo(s), decoder).Sheet;
                    return _board.Frame(s.First) is { } first && File.Exists(_board.FramePicture(first)) ? FilmCheck.Picture(_board.FramePicture(first), 768) : null;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FilmException or InvalidOperationException) { }
        return null;
    }

    private IFilmPiece? ItemOf(FilmPiece piece) => piece.Kind switch
    {
        FilmPieceKind.Frame => _frames.GetValueOrDefault(piece.Id),
        FilmPieceKind.Shot => Shots.FirstOrDefault(s => s.Id == piece.Id),
        _ => Sound,
    };

    private static string Short(string template) =>
        template.StartsWith(FilmTemplates.CheckpointPrefix, StringComparison.OrdinalIgnoreCase) ? Path.GetFileNameWithoutExtension(template) : Path.GetFileName(template);

    // ---- jobs ------------------------------------------------------------------------------------------------

    /// <summary>What the bottom line says: what the window is doing, what went wrong.</summary>
    [ObservableProperty]
    private string _status = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsWorking))]
    private int _working;

    public bool IsWorking => Working > 0;

    private CancellationTokenSource? _job;

    /// <summary>The talk the running job writes to, so the panel shows it working.</summary>
    private string? _jobKey;

    /// <summary>Runs one job at a time: what BlackBox does for a request, or a piece being made; what goes wrong is said in the talk.</summary>
    private async Task RunJobAsync(string key, IFilmPiece? item, Func<Action<string>, CancellationToken, Task> job)
    {
        SaveNow(force: true);
        using var cts = new CancellationTokenSource();
        _job = cts;
        _jobKey = key;
        Working++;
        RefreshTalk();
        void Progress(string s) => Dispatcher.UIThread.Post(() =>
        {
            Status = s;
            if (key == TalkKey && Talk.Count > 0 && !Talk[^1].Mine)
                Talk[^1] = Talk[^1] with { Text = s, Working = true };
        });
        try
        {
            await job(Progress, cts.Token);
            Status = "";
        }
        catch (OperationCanceledException)
        {
            Say(key, "Stopped.", replace: true);
        }
        catch (Exception ex) when (ex is FilmException or FilmStillRunningException or ComfyApiException or ComfyWorkflowException or IOException or UnauthorizedAccessException)
        {
            Say(key, ex.Message, replace: true);
            if (item is not null)
                item.Report(ex.Message, true);
        }
        finally
        {
            Working--;
            _job = null;
            _jobKey = null;
            SaveTalk();
            RefreshTalk();
            RefreshStates();
        }
    }

    [RelayCommand]
    private void Stop() => _job?.Cancel();

    public bool HasFilm => File.Exists(_board.FilmFile);

    /// <summary>Joins the shots made so far, with the soundtrack, into the film, and shows it.</summary>
    [RelayCommand]
    private async Task Join()
    {
        if (IsWorking)
            return;
        await RunJobAsync(FilmTalk.Film, null, (_, ct) => JoinAsync(ct));
    }

    private async Task JoinAsync(CancellationToken ct)
    {
        Say(FilmTalk.Film, "Joining the clips…");
        var made = await FilmMontage.JoinAsync(_board, _previews.Decoder?.Ffmpeg, _previews.Decoder, s => Dispatcher.UIThread.Post(() => Status = s), ct);
        Say(FilmTalk.Film, $"Joined the film ({Path.GetFileName(made.File)}): it plays in the viewer, and it is in the film's folder." +
                           (made.Notes.Count > 0 ? " " + string.Join(" ", made.Notes) : ""), replace: true);
        SaveTalk();
        _filmView?.Player?.Dispose();
        _filmView = null;
        OnPropertyChanged(nameof(HasFilm));
        Select(null);
    }

    /// <summary>
    /// Hands the storyboard to the agent in the chat: it makes what isn't made yet, checks each shot
    /// against what it should show, fixes what is wrong, joins the film and checks it.
    /// </summary>
    [RelayCommand]
    private void MakeFilm()
    {
        SaveNow(force: true);
        _sendToChat($"Make the film from my storyboard: {_board.FilePath}\n" +
                    "Make what isn't made yet or is out of date (frames, the song, each shot's clip), check every shot and fix the ones that don't show what they should, " +
                    "then join them and check the whole film.");
        Status = "BlackBox is making the film in the chat; what it makes shows here as it goes.";
    }

    // ---- saving --------------------------------------------------------------------------------------------

    /// <summary>Something was changed in the window: saved a moment later, and what it changes shown.</summary>
    public void Changed(object source)
    {
        if (_loading)
            return;
        _dirty = true;
        _saveTimer.Stop();
        _saveTimer.Start();
        OnPropertyChanged(nameof(LengthText));
        // What the change makes out of date shows once it is saved (the stamps are computed from the files).
        _refreshTimer.Stop();
        _refreshTimer.Start();
    }

    /// <summary>Whether the storyboard has changes not saved yet.</summary>
    private bool _dirty;

    /// <summary>Saves what changed (with <paramref name="force"/>, saves anyway: before a job, which needs the film's folder).</summary>
    private void SaveNow(bool force = false)
    {
        _saveTimer.Stop();
        if (_board.Folder.Length == 0 || !_dirty && !force)
            return;
        try
        {
            _board.Save();
            _dirty = false;
            _ownWrite = File.GetLastWriteTimeUtc(_board.FilePath);
            if (_watcher is null)
                Watch();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Status = $"The storyboard couldn't be saved: {ex.Message}";
        }
    }

    // ---- what is made --------------------------------------------------------------------------------------


    private string StateOf(IFilmPiece item, bool made, bool current)
    {
        if (!made)
            return item is ShotItem s && s.Shot.Prompt.Trim().Length == 0 ? "Say what happens in it" : "Not made yet";
        if (!current)
            return "Changed: make it again";
        return FilmTakes.LookIn(item.Piece.Workflow(_board)) is { } look
            ? look.Verdict == "redo" ? "Made · the look found a problem" : "Made · looked at"
            : "Made";
    }

    /// <summary>Thumbnails, clips, strips and each piece's state, from the files in the film's folder.</summary>
    public void RefreshStates()
    {
        foreach (var frame in _frames.Values)
        {
            var path = _board.FramePicture(frame.Frame);
            var made = File.Exists(path);
            frame.IsMade = made;
            if (!frame.IsWorking)
                (frame.State, frame.IsError) = (StateOf(frame, made, FilmMaker.IsCurrent(_board, frame.Frame)), false);
            LoadThumbnail(frame, path);
            if (frame == Selected || Selected is ShotItem { First: var first } && first == frame)
                LoadPicture(frame);
        }
        foreach (var shot in Shots)
        {
            var path = _board.ShotVideo(shot.Shot);
            var made = File.Exists(path);
            shot.IsMade = made;
            if (!shot.IsWorking)
                (shot.State, shot.IsError) = (StateOf(shot, made, FilmMaker.IsCurrent(_board, shot.Shot)), false);
            var stamp = made ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue;
            if (made && (shot.Clip is null || shot.Clip.Path != path || _clipStamps.GetValueOrDefault(shot.Id) != stamp))
            {
                shot.Clip?.Dispose();
                shot.Clip = FilePreview.For(path, _previews);
                _clipStamps[shot.Id] = stamp;
                LoadStrip(shot, path, stamp);
            }
            else if (!made && shot.Clip is not null)
            {
                shot.Clip.Dispose();
                shot.Clip = null;
                shot.Strip = null;
            }
        }
        var song = _board.Sound.Source switch { FilmSound.Song => _board.SongFile, FilmSound.FromFile => _board.Sound.File, _ => null };
        var songMade = song is not null && File.Exists(song);
        Sound.IsMade = songMade;
        var heard = songMade && _songScore.File == song && _songScore.Stamp == File.GetLastWriteTimeUtc(song!) && _songScore.Score is { } s ? " · " + s.Describe() : "";
        if (!Sound.IsWorking)
            Sound.State = _board.Sound.Source switch
            {
                FilmSound.Song => !songMade ? "Not made yet" : FilmMaker.SongIsCurrent(_board) ? "Made" + heard : "Changed: make it again",
                FilmSound.FromFile => song is null ? "Pick a sound file" : songMade ? "Your file" : "The file isn't there",
                _ => "No soundtrack",
            };
        var songStamp = songMade ? File.GetLastWriteTimeUtc(song!) : DateTime.MinValue;
        if (songMade && (Sound.Player is null || Sound.Player.Path != song || _clipStamps.GetValueOrDefault("\nsong") != songStamp))
        {
            Sound.Player?.Dispose();
            Sound.Player = FilePreview.For(song!, _previews);
            _clipStamps["\nsong"] = songStamp;
        }
        else if (!songMade && Sound.Player is not null)
        {
            Sound.Player.Dispose();
            Sound.Player = null;
        }
        if (_filmView is not null && (!HasFilm || _filmStamp != File.GetLastWriteTimeUtc(_board.FilmFile)))
        {
            _filmView.Player?.Dispose();
            _filmView = null;
        }
        _filmStamp = HasFilm ? File.GetLastWriteTimeUtc(_board.FilmFile) : default;
        OnPropertyChanged(nameof(HasFilm));
        OnPropertyChanged(nameof(ShotVolume));
        OnPropertyChanged(nameof(LengthText));
        OnPropertyChanged(nameof(Viewer));
        RefreshSound();
        Relayout();
    }

    private DateTime _filmStamp;

    /// <summary>How the song made last sounded to the judge, while it is the song in the folder.</summary>
    private (string? File, DateTime Stamp, SongScore? Score) _songScore;

    private static string Capital(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

    /// <summary>A title in a sentence: "shot 3", "the song", "start of shot 2"; a name ("Marco") as it is.</summary>
    private static string Lower(string s) =>
        new[] { "Shot ", "The ", "Start of", "End of", "Frame ", "Reference ", "A " }.Any(p => s.StartsWith(p, StringComparison.Ordinal)) ? char.ToLowerInvariant(s[0]) + s[1..] : s;

    private static string Cut(string s, int max) => s.Length <= max ? s : s[..max].TrimEnd() + "…";

    private readonly Dictionary<string, DateTime> _clipStamps = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<FrameItem, DateTime> _thumbStamps = [];

    private void LoadThumbnail(FrameItem frame, string path)
    {
        var stamp = File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue;
        if (_thumbStamps.TryGetValue(frame, out var had) && had == stamp)
            return;
        _thumbStamps[frame] = stamp;
        if (stamp == DateTime.MinValue)
        {
            frame.Thumbnail = null;
            return;
        }
        _ = Task.Run(() =>
        {
            try
            {
                using var stream = File.OpenRead(path);
                var bitmap = Bitmap.DecodeToWidth(stream, 320, BitmapInterpolationMode.HighQuality);
                Dispatcher.UIThread.Post(() =>
                {
                    var old = frame.Thumbnail;
                    frame.Thumbnail = bitmap;
                    old?.Dispose();
                });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException) { }
        });
    }

    /// <summary>A frame's picture, bigger than its thumbnail, for the viewer.</summary>
    private void LoadPicture(FrameItem frame)
    {
        var path = _board.FramePicture(frame.Frame);
        if (!File.Exists(path))
        {
            frame.Picture = null;
            return;
        }
        _ = Task.Run(() =>
        {
            try
            {
                using var stream = File.OpenRead(path);
                var bitmap = Bitmap.DecodeToWidth(stream, 1280, BitmapInterpolationMode.HighQuality);
                Dispatcher.UIThread.Post(() =>
                {
                    var old = frame.Picture;
                    frame.Picture = bitmap;
                    old?.Dispose();
                });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException) { }
        });
    }

    /// <summary>A shot's clip as a row of frames, for its block on the timeline.</summary>
    private void LoadStrip(ShotItem shot, string path, DateTime stamp)
    {
        if (_previews.Decoder is not { } decoder)
            return;
        _ = Task.Run(() =>
        {
            byte[]? jpeg = null;
            try { jpeg = FilmCheck.Strip(path, decoder, 64); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or FilmException) { }
            if (jpeg is null)
                return;
            using var stream = new MemoryStream(jpeg);
            var bitmap = new Bitmap(stream);
            Dispatcher.UIThread.Post(() =>
            {
                if (_clipStamps.GetValueOrDefault(shot.Id) != stamp)
                {
                    bitmap.Dispose();
                    return;
                }
                var old = shot.Strip;
                shot.Strip = bitmap;
                old?.Dispose();
            });
        });
    }

    // ---- the folder, as the agent and the jobs change it ----------------------------------------------------

    private void Watch()
    {
        _watcher?.Dispose();
        _watcher = null;
        try
        {
            var watcher = new FileSystemWatcher(_board.Folder) { IncludeSubdirectories = true, NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size };
            FileSystemEventHandler changed = (_, _) => Dispatcher.UIThread.Post(() =>
            {
                _refreshTimer.Stop();
                _refreshTimer.Start();
            });
            watcher.Changed += changed;
            watcher.Created += changed;
            watcher.Deleted += changed;
            watcher.Renamed += (s, e) => changed(s, e);
            watcher.EnableRaisingEvents = true;
            _watcher = watcher;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException) { }
    }

    /// <summary>
    /// Something in the film's folder changed: the agent changed the storyboard (it is read again),
    /// or a frame, clip or song was made (it is shown).
    /// </summary>
    private void OnFolderChanged()
    {
        try
        {
            var written = File.GetLastWriteTimeUtc(_board.FilePath);
            if (written != _ownWrite && !_saveTimer.IsEnabled && !IsWorking && File.Exists(_board.FilePath))
            {
                var fresh = Storyboard.Load(_board.Folder);
                if (fresh.ToJson() != _board.ToJson())
                {
                    _board = fresh;
                    _ownWrite = written;
                    Rebuild();
                    return;
                }
                _ownWrite = written;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FilmException) { }
        RefreshStates();
    }

    public void Dispose()
    {
        StopPlaying();
        SaveNow();
        SaveTalk();
        _watcher?.Dispose();
        _job?.Cancel();
        foreach (var shot in Shots)
        {
            shot.Clip?.Dispose();
            shot.Strip?.Dispose();
        }
        Sound?.Player?.Dispose();
        _filmView?.Player?.Dispose();
        foreach (var take in Takes)
            take.Thumbnail?.Dispose();
        _ = _maker.ReleaseAsync();
    }
}
