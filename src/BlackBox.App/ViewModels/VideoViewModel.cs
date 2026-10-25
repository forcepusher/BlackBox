using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using BlackBox.Core.Video;
using BlackBox.Core.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BlackBox.App.ViewModels;

/// <summary>
/// Video generator, a tool of the chat window's toolkit, in a panel on the left of the chat: a viewer,
/// and the video on one timeline under one horizontal scroll, where all work on it is done (the
/// user's call, 2026-10-24: "work on the film should be done entirely in the timeline"): the
/// References row (image files and generated images), the clips on the Video clips row, the lines, and the
/// sound lines. What is picked on it goes into the chat's box, where the user says what to do with
/// it: the agent writes the prompts, picks the models and makes each piece with the video tool in the
/// chat, in view. The video is its folder (References and Generated, each file with its .json), and
/// what the agent changes and makes there shows here as it happens.
/// </summary>
public sealed partial class VideoViewModel : ObservableObject, IToolkit
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

    /// <summary>Where videos go, a folder each: BlackBox's Data\videos, next to the exe like the rest of its data.</summary>
    private readonly string _videosRoot;

    /// <param name="chat">The chat the panel is a tool of, where what is said about the video runs.</param>
    /// <param name="videosRoot">Where videos go (BlackBox's Data\videos).</param>
    /// <param name="folder">The video to open; the latest changed one when null.</param>
    public VideoViewModel(PreviewServices previews, MainWindowViewModel chat, string videosRoot, string? folder)
    {
        _videosRoot = videosRoot;
        _previews = previews;
        _chat = chat;
        _saveTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(400), DispatcherPriority.Background, (_, _) => SaveNow());
        _refreshTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(300), DispatcherPriority.Background, (_, _) =>
        {
            _refreshTimer!.Stop();
            OnFolderChanged();
        });
        // What the chat is doing, shown here: on the piece the video tool is making.
        _chatTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(500), DispatcherPriority.Background, (_, _) => WatchChat());
        _chatTimer.Start();
        // A pick taken off the box (its ×) is no longer picked here either.
        _chat.Attachments.CollectionChanged += OnBoxChanged;
        var open = folder ?? Storyboard.Recent(_videosRoot).FirstOrDefault();
        if (open is not null && Storyboard.IsVideo(open))
            Open(open);
        else
            Untitled();
        Watch();
    }

    // ---- the video --------------------------------------------------------------------------------------

    public string Folder => _board.Folder;

    /// <summary>The video's name, on the button at the top that opens the videos' menu.</summary>
    public string VideoName => Path.GetFileName(Path.TrimEndingDirectorySeparator(_board.Folder));

    /// <summary>Opens the video in a folder (or of a file in it).</summary>
    public void Open(string path)
    {
        SaveNow();
        StopPlaying(); // the video left: its sounds played on over the new one's frames
        try
        {
            _board = Storyboard.Load(path);
            _savedJson = _board.ToJson();
            _dirty = false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or VideoException)
        {
            ShowError($"That video can't be opened: {ex.Message}", ex, path);
            return;
        }
        Opened();
    }

    /// <summary>
    /// A new video in BlackBox's Data\videos, its name typed as a new folder's is in Explorer. Its folder
    /// is made with the first change: opening the panel to look leaves nothing behind. The user says
    /// in the chat what it is to be.
    /// </summary>
    [RelayCommand]
    private void NewVideo()
    {
        Untitled();
        RenameVideoAsked?.Invoke();
    }

    /// <summary>The video's name is to be typed anew (a new video, or Rename in its menu): the panel shows the box.</summary>
    public event Action? RenameVideoAsked;

    /// <summary>A new video, named "Untitled video" (or "Untitled video 2"), not saved yet. Not "New
    /// video": that is the menu's item that makes it (the user, 2026-10-25).</summary>
    private void Untitled()
    {
        SaveNow();
        StopPlaying();
        var root = _videosRoot;
        var name = "Untitled video";
        for (var n = 2; Directory.Exists(Path.Combine(root, name)); n++)
            name = $"Untitled video {n}";
        _board = Storyboard.New(Path.Combine(root, name), name);
        _savedJson = null;
        _dirty = false;
        Opened();
    }

    /// <summary>Another video is shown: what was picked of the one before goes from the chat's box.</summary>
    private void Opened()
    {
        Status = "";
        Rebuild();
        OnPropertyChanged(nameof(Folder));
        OnPropertyChanged(nameof(VideoName));
        _chat.Pick([]);
        Clear();
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

    /// <summary>How long the video is, and in how many clips (its size and frame rate are at the top).</summary>
    public string LengthText
    {
        get
        {
            var (_, length) = _board.Timing();
            var made = _board.Shots.Count(s => VideoMaker.IsCurrent(_board, s));
            return $"{length:0.#} s in {_board.Shots.Count} clip{(_board.Shots.Count == 1 ? "" : "s")}" +
                   (_board.Shots.Count == 0 ? "" : made == _board.Shots.Count ? " · all made" : $" · {made} made");
        }
    }

    // ---- the timeline ------------------------------------------------------------------------------------

    /// <summary>Where the video's time starts on each row, in pixels.</summary>
    public const double Lead = 12;

    /// <summary>A card on the References row, and the room between two.</summary>
    public const double CardWidth = 96, CardGap = 6;

    /// <summary>How tall a sound line is.</summary>
    public const double SoundLineHeight = 48;

    /// <summary>How many pixels a second takes on the timeline: the zoom.</summary>
    [ObservableProperty]
    private double _pps = 64;

    partial void OnPpsChanged(double value) => Relayout();

    public ObservableCollection<ReferenceCard> References { get; } = [];
    public ObservableCollection<ShotItem> Shots { get; } = [];
    public ObservableCollection<SoundItem> Sounds { get; } = [];
    public ObservableCollection<SoundLineItem> SoundLines { get; } = [];

    /// <summary>How tall the sound lines are together.</summary>
    public double SoundLinesHeight => Math.Max(1, SoundLines.Count) * SoundLineHeight;

    /// <summary>How wide the rows are: the video, the sounds and cards past it, and room to make the last clip longer.</summary>
    [ObservableProperty]
    private double _timelineWidth = 800;

    [ObservableProperty]
    private double _length;

    /// <summary>Where the next clip's place is on the Video clips row, after the last: a click there adds it.</summary>
    [ObservableProperty]
    private double _addShotX = Lead;

    /// <summary>Where the next card's place is on the References row: a click there makes one.</summary>
    [ObservableProperty]
    private double _addReferenceX = Lead;

    public double PlayheadX => Lead + Math.Max(0, PlayPosition) * Pps;

    /// <summary>Lays the timeline out again: each card, clip and sound where it is, the width.</summary>
    public void Relayout()
    {
        var (starts, length) = _board.Timing();
        Length = length;
        for (var i = 0; i < References.Count; i++)
            References[i].X = Lead + i * (CardWidth + CardGap);
        AddReferenceX = Lead + References.Count * (CardWidth + CardGap);
        for (var i = 0; i < Shots.Count && i < starts.Count; i++)
        {
            Shots[i].X = Lead + starts[i] * Pps;
            Shots[i].BlockWidth = Math.Max(8, Shots[i].Shot.Seconds * Pps - 2);
        }
        var end = length;
        foreach (var sound in Sounds)
        {
            var seconds = sound.PlaySeconds(length);
            sound.X = Lead + sound.Sound.Start * Pps;
            sound.Y = sound.Sound.SoundLine * SoundLineHeight + 4;
            sound.BlockWidth = Math.Max(8, seconds * Pps - 2);
            end = Math.Max(end, sound.Sound.Start + seconds);
            sound.Peaks = PeaksOf(sound);
        }
        AddShotX = Lead + length * Pps + 4;
        TimelineWidth = Math.Max(Lead + end * Pps, AddReferenceX + CardWidth) + 240;
        OnPropertyChanged(nameof(PlayheadX));
        OnPropertyChanged(nameof(LengthText));
        OnPropertyChanged(nameof(SoundLinesHeight));
    }

    [RelayCommand]
    private void ZoomIn() => Pps = Math.Min(240, Pps * 1.25);

    [RelayCommand]
    private void ZoomOut() => Pps = Math.Max(12, Pps / 1.25);

    /// <summary>The time under a point of the timeline, in seconds.</summary>
    public double SecondsAt(double x) => Math.Max(0, (x - Lead) / Math.Max(1, Pps));

    // ---- the sounds' audio, read once: for their waveforms and for playing the video ---------------------------

    /// <summary>Each sound file's samples, mono, with their rate, by its path and time.</summary>
    private readonly Dictionary<string, (DateTime Stamp, float[] Samples, int Rate)> _audio = new(StringComparer.OrdinalIgnoreCase);

    private readonly HashSet<string> _reading = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>A sound's samples, once read; null while they are read (it is laid out again then) or can't be.</summary>
    private (float[] Samples, int Rate)? AudioOf(string file)
    {
        var stamp = File.Exists(file) ? File.GetLastWriteTimeUtc(file) : DateTime.MinValue;
        if (stamp == DateTime.MinValue)
            return null;
        if (_audio.TryGetValue(file, out var known) && known.Stamp == stamp)
            return (known.Samples, known.Rate);
        if (_previews.Decoder is not { } decoder || !_reading.Add(file))
            return null;
        _ = Task.Run(() =>
        {
            (float[], int)? read = null;
            try
            {
                using var reader = decoder.OpenAudio(file);
                if (reader is not null)
                    read = MediaPreview.ReadMono(reader, TimeSpan.FromMinutes(20));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or NotSupportedException) { }
            Dispatcher.UIThread.Post(() =>
            {
                _reading.Remove(file);
                if (_disposing)
                    return;
                _audio[file] = read is var (samples, rate) ? (stamp, samples, rate) : (stamp, [], 1);
                foreach (var sound in Sounds.Where(s => string.Equals(_board.SoundFile(s.Sound), file, StringComparison.OrdinalIgnoreCase)))
                    sound.FileSeconds = read is var (all, r) ? all.Length / (double)Math.Max(1, r) : 0;
                Relayout();
            });
        });
        return null;
    }

    /// <summary>The waveform of the part of a sound that plays, a peak each tenth of a second, 0 to 1.</summary>
    private float[]? PeaksOf(SoundItem sound)
    {
        if (AudioOf(_board.SoundFile(sound.Sound)) is not var (samples, rate) || samples.Length == 0)
            return null;
        sound.FileSeconds = samples.Length / (double)rate;
        var seconds = sound.PlaySeconds(Length);
        // Worked out once for each trim and length: the timeline is laid out at every step of a drag.
        var key = (samples, sound.Sound.Trim, seconds);
        if (_peaks.TryGetValue(sound.Id, out var known) && known.Key == key)
            return known.Peaks;
        var peaks = new float[Math.Max(1, (int)Math.Ceiling(seconds * 10))];
        var per = Math.Max(1, rate / 10);
        var from = (long)(sound.Sound.Trim * rate);
        for (var i = 0; i < peaks.Length; i++)
        {
            var peak = 0f;
            for (long j = from + (long)i * per; j < Math.Min(samples.Length, from + (long)(i + 1) * per); j++)
                peak = Math.Max(peak, Math.Abs(samples[j]));
            peaks[i] = peak;
        }
        var top = peaks.Max();
        if (top > 0)
            for (var i = 0; i < peaks.Length; i++)
                peaks[i] /= top;
        _peaks[sound.Id] = (key, peaks);
        return peaks;
    }

    private readonly Dictionary<string, ((float[] Samples, double Trim, double Seconds) Key, float[] Peaks)> _peaks = new(StringComparer.OrdinalIgnoreCase);

    // ---- playing the video: its sounds mixed, with each clip's frames -------------------------------------

    /// <summary>Where the playhead is, in seconds from the video's start.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PlayheadX), nameof(PlayTime))]
    private double _playPosition;

    public string PlayTime => $"{TimeSpan.FromSeconds(Math.Max(0, PlayPosition)):m\\:ss\\.f}";

    /// <summary>The storyboard frame under the playhead: a clip's first frame, its last for the second half.</summary>
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

    /// <summary>Plays the sound lines mixed from the playhead with the frames switching as the clips do, or pauses it.</summary>
    [RelayCommand]
    private void PlayStrip()
    {
        if (IsPlaying)
        {
            StopPlaying();
            return;
        }
        var end = Math.Max(Length, Sounds.Select(s => s.Sound.Start + s.PlaySeconds(Length)).DefaultIfEmpty(0).Max());
        StartPlaying(PlayPosition >= 0 && PlayPosition < end - 0.05 ? PlayPosition : 0);
    }

    /// <summary>The timeline was clicked: the playhead goes there (and plays on from there if it was playing).</summary>
    public void SeekStrip(double seconds)
    {
        var playing = IsPlaying;
        StopPlaying();
        PlayPosition = Math.Max(0, seconds);
        ShowFrameAt(PlayPosition);
        if (playing)
            StartPlaying(PlayPosition);
    }

    private void StartPlaying(double from)
    {
        StopPlaying();
        // The sounds that are read, mixed as the video joins them; with none, the frames go by the clock.
        var parts = new List<VideoMix.Part>();
        foreach (var sound in Sounds)
            if (AudioOf(_board.SoundFile(sound.Sound)) is var (samples, rate) && samples.Length > 0)
                parts.Add(new VideoMix.Part(samples, rate, sound.Sound.Start, sound.Sound.Trim, sound.PlaySeconds(Length), sound.Sound.Volume));
        _playEnd = Math.Max(Length, parts.Select(p => p.Start + p.Seconds).DefaultIfEmpty(0).Max());
        if (parts.Count > 0 && _previews.NewAudioOutput() is { } output)
        {
            var mix = new VideoMix(parts, _playEnd);
            mix.Seek(TimeSpan.FromSeconds(from));
            try
            {
                output.Play(mix);
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

    private double _playEnd;

    private void PlayTick()
    {
        var at = _playFrom + (_playOutput?.Played ?? _playClock.Elapsed).TotalSeconds;
        if (at >= _playEnd)
        {
            StopPlaying();
            PlayPosition = _playEnd;
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

    /// <summary>The frame of the clip at a time: its first frame, its last for the second half (when it has one).</summary>
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

/// <summary>
/// The video's sounds mixed into one, as joining the video lays them over each other: each from where
/// it starts, its trim cut off, as long as it plays, at its volume; mono at 48 kHz.
/// </summary>
internal sealed class VideoMix(IReadOnlyList<VideoMix.Part> parts, double length) : IAudioReader
{
    public sealed record Part(float[] Samples, int Rate, double Start, double Trim, double Seconds, double Volume);

    private const int Rate = 48000;
    private long _at;

    public int SampleRate => Rate;
    public int Channels => 1;
    public TimeSpan Duration => TimeSpan.FromSeconds(length);

    public int Read(float[] buffer, int offset, int count)
    {
        var n = (int)Math.Min(count, (long)(length * Rate) - _at);
        if (n <= 0)
            return 0;
        Array.Clear(buffer, offset, n);
        foreach (var part in parts)
            for (var i = 0; i < n; i++)
            {
                var local = (_at + i) / (double)Rate - part.Start;
                if (local < 0 || local >= part.Seconds)
                    continue;
                var pos = (local + part.Trim) * part.Rate;
                var j = (long)pos;
                if (j + 1 >= part.Samples.Length)
                    continue;
                var f = (float)(pos - j);
                buffer[offset + i] += (part.Samples[j] * (1 - f) + part.Samples[j + 1] * f) * (float)part.Volume;
            }
        for (var i = 0; i < n; i++)
            buffer[offset + i] = Math.Clamp(buffer[offset + i], -1f, 1f);
        _at += n;
        return n;
    }

    public void Seek(TimeSpan time) => _at = Math.Max(0, (long)(time.TotalSeconds * Rate));

    public void Dispose() { }
}
