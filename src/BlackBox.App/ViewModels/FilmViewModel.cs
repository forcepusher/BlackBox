using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using BlackBox.Core.Film;
using BlackBox.Core.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BlackBox.App.ViewModels;

/// <summary>
/// Filmmaking, a tool of the chat window's toolkit, in a panel on the left of the chat: the film's
/// folder as a tree (its references, frames, shots and sound, made or planned), a viewer, and the
/// film on one timeline (its shots and sound) under one horizontal scroll. What is picked in the
/// tree or on the timeline goes into the chat's box, where the user says what to do with it: the
/// agent writes the prompts, picks the models and makes each piece with the film tool in the chat,
/// in view. The film is its folder (a folder per shot, each file with its .json), and what the agent
/// changes and makes there shows here as it happens.
/// </summary>
public sealed partial class FilmViewModel : ObservableObject, IToolkit
{
    private readonly PreviewServices _previews;
    private readonly MainWindowViewModel _chat;
    private Storyboard _board = new();
    private readonly Dictionary<string, FrameItem> _frames = new(StringComparer.OrdinalIgnoreCase);
    private readonly DispatcherTimer _saveTimer;
    private readonly DispatcherTimer _refreshTimer;
    private readonly DispatcherTimer _chatTimer;
    private FileSystemWatcher? _watcher;
    private bool _loading;

    /// <summary>Where films go, a folder each: BlackBox's Data\films, next to the exe like the rest of its data.</summary>
    private readonly string _filmsRoot;

    /// <param name="chat">The chat the panel is a tool of, where what is said about the film runs.</param>
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
        // What the chat is doing, shown here: on the piece the film tool is making.
        _chatTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(500), DispatcherPriority.Background, (_, _) => WatchChat());
        _chatTimer.Start();
        // A pick taken off the box (its ×) is no longer picked here either.
        _chat.Attachments.CollectionChanged += OnBoxChanged;
        var open = folder ?? Storyboard.Recent(_filmsRoot).FirstOrDefault();
        if (open is not null && Storyboard.IsFilm(open))
            Open(open);
        else
            Untitled();
        Watch();
    }

    // ---- the film --------------------------------------------------------------------------------------

    public string Folder => _board.Folder;

    /// <summary>Opens the film in a folder (or of a file in it).</summary>
    public void Open(string path)
    {
        SaveNow();
        StopPlaying(); // the film left: its soundtrack played on over the new one's frames
        try
        {
            _board = Storyboard.Load(path);
            _savedJson = _board.ToJson();
            _dirty = false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FilmException)
        {
            ShowError($"That film can't be opened: {ex.Message}", ex, path);
            return;
        }
        Opened();
    }

    /// <summary>
    /// A new film in BlackBox's Data\films, its name typed in the tree as a new folder's is in
    /// Explorer. Its folder is made with the first change: opening the panel to look leaves
    /// nothing behind. The user says in the chat what it is to be.
    /// </summary>
    [RelayCommand]
    private void NewFilm()
    {
        Untitled();
        if (Find(_board.Folder) is { } node)
            StartRename(node);
    }

    /// <summary>A new film, named "New film" (or "New film 2"), not saved yet.</summary>
    private void Untitled()
    {
        SaveNow();
        StopPlaying();
        var root = _filmsRoot;
        var name = "New film";
        for (var n = 2; Directory.Exists(Path.Combine(root, name)); n++)
            name = $"New film {n}";
        _board = Storyboard.New(Path.Combine(root, name), name);
        _savedJson = null;
        _dirty = false;
        Opened();
    }

    /// <summary>Another film is shown: what was picked of the one before goes from the chat's box, and its folder is picked in the tree.</summary>
    private void Opened()
    {
        Status = "";
        Rebuild();
        OnPropertyChanged(nameof(Folder));
        Clear(viewer: true);
        _chat.Pick([]);
        PickFilm();
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

    /// <summary>How long the film is, and in how many shots (its size and frame rate are at the top).</summary>
    public string LengthText
    {
        get
        {
            var (_, length) = _board.Timing();
            var made = _board.Shots.Count(s => FilmMaker.IsCurrent(_board, s));
            return $"{length:0.#} s in {_board.Shots.Count} shot{(_board.Shots.Count == 1 ? "" : "s")}" +
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
    public ObservableCollection<LineItem> Lines { get; } = [];

    /// <summary>How wide the rows are: the film, the song where it runs past it, and room to make the last shot longer.</summary>
    [ObservableProperty]
    private double _timelineWidth = 800;

    [ObservableProperty]
    private double _length;

    /// <summary>Where the next shot's place is on the Video row, after the last: a click there adds it.</summary>
    [ObservableProperty]
    private double _addShotX = Lead;

    /// <summary>Where the lines are heard, for the song's row to show it quieter there.</summary>
    [ObservableProperty]
    private IReadOnlyList<(double X, double Width)> _ducks = [];

    public double PlayheadX => Lead + Math.Max(0, PlayPosition) * Pps;

    /// <summary>Lays the timeline out again: each shot where it starts, each line, the width.</summary>
    public void Relayout()
    {
        var (starts, length) = _board.Timing();
        Length = length;
        for (var i = 0; i < Shots.Count && i < starts.Count; i++)
        {
            Shots[i].X = Lead + starts[i] * Pps;
            Shots[i].BlockWidth = Math.Max(8, Shots[i].Shot.Seconds * Pps - 2);
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
        AddShotX = Lead + length * Pps + 4;
        TimelineWidth = Lead + Math.Max(length, Math.Min(_sound.Seconds, length + 10)) * Pps + 240;
        OnPropertyChanged(nameof(PlayheadX));
        OnPropertyChanged(nameof(LengthText));
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
            _ => "No soundtrack: each shot keeps its own sound. Pick this row and say in the chat what music you want.",
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
                if (_disposing)
                    return;
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

    /// <summary>The storyboard frame under the playhead: a shot's first frame, its last for the second half.</summary>
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
