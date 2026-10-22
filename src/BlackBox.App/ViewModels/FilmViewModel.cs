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
/// The Filmmaking window, an add-on of a chat: a film on one timeline (its references, its shots,
/// its sound) under one horizontal scroll, a viewer, and a panel to tell BlackBox in plain words
/// what to make or change in the piece picked, or in the whole film. What is told, and what the
/// buttons ask for, goes to the chat as a message: the agent writes the prompts, picks the models
/// and makes each piece with the film tool there, where the user watches it and can stop it, and
/// a piece made again is kept as a take. Everything is kept in the film folder's storyboard.json,
/// and what the agent changes there and makes beside it shows here as it happens.
/// </summary>
public sealed partial class FilmViewModel : ObservableObject, IDisposable
{
    private readonly PreviewServices _previews;
    private readonly MainWindowViewModel _chat;
    private Storyboard _board = new();
    private readonly Dictionary<string, FrameItem> _frames = new(StringComparer.OrdinalIgnoreCase);
    private readonly DispatcherTimer _saveTimer;
    private readonly DispatcherTimer _refreshTimer;
    private readonly DispatcherTimer _chatTimer;
    private FileSystemWatcher? _watcher;
    private DateTime _ownWrite;
    private bool _loading;

    /// <summary>Where films go, a folder each: BlackBox's Data\films, next to the exe like the rest of its data.</summary>
    private readonly string _filmsRoot;

    /// <param name="chat">The chat the window belongs to, where what is said in it runs.</param>
    /// <param name="filmsRoot">Where films go (BlackBox's Data\films).</param>
    /// <param name="folder">The film to open; the latest changed one when null.</param>
    public FilmViewModel(PreviewServices previews, MainWindowViewModel chat, string filmsRoot, string? folder)
    {
        _filmsRoot = filmsRoot;
        _previews = previews;
        _chat = chat;
        _saveTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(400), DispatcherPriority.Background, (_, _) => SaveNow());
        _refreshTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(300), DispatcherPriority.Background, (_, _) =>
        {
            _refreshTimer!.Stop();
            OnFolderChanged();
        });
        // What the chat is doing, shown here: in a line, and on the piece the film tool is making.
        _chatTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(500), DispatcherPriority.Background, (_, _) => WatchChat());
        _chatTimer.Start();
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
            Changed();
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
            Changed();
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
            Changed();
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
            Changed();
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
            Changed();
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
            Changed();
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
}
