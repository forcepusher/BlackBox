using System.Diagnostics;
using Avalonia.Threading;
using BlackBox.Core.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BlackBox.App.ViewModels;

/// <summary>
/// A sound in the File inspector panel: its waveform as wide as the panel, zoomed in to its samples
/// (see <see cref="Views.WaveformView"/>), a playhead to set or drag anywhere with the time to the
/// thousandth, and a stretch dragged over it to ask about, which goes into the chat's box as
/// "0.80-1.45 s · song.mp3" and to the model as where it is and a picture of it (the user's request,
/// 2026-10-26: the chat's sound card in the panel was no inspector). Play plays the stretch picked,
/// else from the playhead.
/// </summary>
public sealed partial class InspectorSoundViewModel : ObservableObject, IDisposable
{
    private readonly PreviewServices _services;

    public string Path { get; }

    public InspectorSoundViewModel(string path, PreviewServices services)
    {
        Path = path;
        _services = services;
        _clock = new DispatcherTimer(TimeSpan.FromMilliseconds(16), DispatcherPriority.Render, (_, _) => Tick());
    }

    /// <summary>The sound mixed to one channel, and how many samples a second it has.</summary>
    public float[] Samples { get; private set; } = [];

    public int Rate { get; private set; } = 1;

    /// <summary>
    /// Each 256 samples' lowest and highest, for drawing a long stretch quickly; and as loud as the
    /// waveform is drawn (up to ten times louder, so a quiet sound still shows its shape).
    /// </summary>
    public (float Min, float Max)[] Blocks { get; private set; } = [];

    public const int BlockSize = 256;

    public float Gain { get; private set; } = 1;

    public double Length { get; private set; } = 1;

    /// <summary>"3.4 s, 48 kHz stereo"; null when nothing on this PC can read it.</summary>
    public string? Details { get; private set; }

    /// <summary>Reads the whole sound, off the UI thread; false when nothing on this PC can.</summary>
    public bool Load()
    {
        using var reader = _services.Decoder?.OpenAudio(Path);
        if (reader is null)
            return false;
        var (mono, rate) = MediaPreview.ReadMono(reader, TimeSpan.FromMinutes(30));
        var blocks = new (float, float)[(mono.Length + BlockSize - 1) / BlockSize];
        var loudest = 0f;
        for (var b = 0; b < blocks.Length; b++)
        {
            float lo = 0, hi = 0;
            for (int i = b * BlockSize, end = Math.Min(mono.Length, i + BlockSize); i < end; i++)
            {
                lo = Math.Min(lo, mono[i]);
                hi = Math.Max(hi, mono[i]);
            }
            blocks[b] = (lo, hi);
            loudest = Math.Max(loudest, Math.Max(-lo, hi));
        }
        (Samples, Rate, Blocks) = (mono, Math.Max(1, rate), blocks);
        Gain = loudest > 0 ? Math.Min(10f, 0.95f / loudest) : 1f;
        Length = Math.Max(0.001, Math.Max(mono.Length / (double)Rate, reader.Duration.TotalSeconds));
        var channels = reader.Channels == 1 ? "mono" : reader.Channels == 2 ? "stereo" : $"{reader.Channels} channels";
        Details = $"{Length:0.0##} s, {rate / 1000.0:0.#} kHz {channels}";
        Dispatcher.UIThread.Post(() => OnPropertyChanged(nameof(LongestTimeText)));
        return true;
    }

    // ---- what is in view: the panel's zoom -----------------------------------------------------------

    /// <summary>How far in it is zoomed: 1 shows the whole sound, more a shorter stretch of it.</summary>
    [ObservableProperty]
    private double _zoom = 1;

    /// <summary>Zoomed in until some 200 samples fill the view.</summary>
    public double MaxZoom => Math.Max(1, Samples.Length / 200.0);

    /// <summary>Where in the sound the view starts, in seconds.</summary>
    [ObservableProperty]
    private double _viewStart;

    public double ViewLength => Length / Math.Max(1, Zoom);

    partial void OnZoomChanged(double oldValue, double newValue)
    {
        // About the playhead when it is in view, else the middle, as the panel's buttons do.
        var at = Position >= ViewStart && Position <= ViewStart + Length / Math.Max(1, oldValue) ? Position : ViewStart + Length / Math.Max(1, oldValue) / 2;
        ZoomAbout(at, oldValue);
    }

    /// <summary>Set before a wheel zooms: the time under the mouse, which stays under it.</summary>
    internal double? ZoomAnchor { get; set; }

    private void ZoomAbout(double at, double oldZoom)
    {
        if (ZoomAnchor is { } anchor)
        {
            at = anchor;
            ZoomAnchor = null;
        }
        var oldLength = Length / Math.Max(1, oldZoom);
        var part = oldLength > 0 ? (at - ViewStart) / oldLength : 0.5;
        ViewStart = Clamp(at - part * ViewLength);
        OnPropertyChanged(nameof(ViewLength));
    }

    /// <summary>A view start that keeps the view within the sound.</summary>
    internal double Clamp(double start) => Math.Clamp(start, 0, Math.Max(0, Length - ViewLength));

    // ---- the playhead -------------------------------------------------------------------------

    /// <summary>Where the playhead is, in seconds.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TimeText))]
    private double _position;

    /// <summary>"0.812 s / 3.400 s".</summary>
    public string TimeText => $"{SoundRange.Clock(Position)} / {SoundRange.Clock(Length)}";

    /// <summary><see cref="TimeText"/> at its longest, which sizes its slot: the text beside it keeps still as it plays.</summary>
    public string LongestTimeText => $"{SoundRange.Clock(Length)} / {SoundRange.Clock(Length)}";

    /// <summary>The playhead set (a click, a drag on the ruler, a key): playing goes on from there.</summary>
    public void Seek(double seconds)
    {
        Position = Math.Clamp(seconds, 0, Length);
        FollowPlayhead();
        if (IsPlaying)
            Start(Position, _playTo);
    }

    /// <summary>Moves the playhead by <paramref name="parts"/> of the view's width.</summary>
    public void Nudge(double parts) => Seek(Position + parts * ViewLength);

    /// <summary>The playhead off the view (playing, or moved by a key): the view goes on to show it.</summary>
    private void FollowPlayhead()
    {
        if (Position < ViewStart || Position > ViewStart + ViewLength)
            ViewStart = Clamp(Position - (IsPlaying ? 0.05 : 0.5) * ViewLength);
    }

    // ---- the stretch picked -----------------------------------------------------------------------

    /// <summary>The stretch dragged over, in seconds; null with none. Set by the waveform as it is dragged.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RangeText), nameof(HasRange))]
    private (double From, double To)? _range;

    public bool HasRange => Range is not null;

    /// <summary>"0.800-1.450 s (0.650 s)".</summary>
    public string RangeText => Range is { } r ? $"{SoundRange.Clock(r.From)[..^2]}-{SoundRange.Clock(r.To)} ({SoundRange.Clock(r.To - r.From)})" : "";

    /// <summary>The waveform was let go: the stretch dragged (null for a click) is the one picked.</summary>
    public event Action<(double From, double To)?>? Picked;

    internal void Pick((double From, double To)? range)
    {
        Range = range;
        Picked?.Invoke(range);
    }

    // ---- playing --------------------------------------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PlayIcon))]
    private bool _isPlaying;

    private static readonly Avalonia.Media.Geometry PlayShape = Avalonia.Media.StreamGeometry.Parse("M8 5v14l11-7z");
    private static readonly Avalonia.Media.Geometry PauseShape = Avalonia.Media.StreamGeometry.Parse("M6 4h4v16H6zM14 4h4v16h-4z");

    public Avalonia.Media.Geometry PlayIcon => IsPlaying ? PauseShape : PlayShape;

    private Playback? _playback;

    /// <summary>Where the playing stops (a stretch's end); null: the end of the sound.</summary>
    private double? _playTo;

    private readonly Stopwatch _played = new();

    /// <summary>The playback's own time last reported, which the clock goes on from between reports.</summary>
    private double _reported;

    private readonly DispatcherTimer _clock;

    [RelayCommand]
    private void Play()
    {
        if (IsPlaying)
        {
            Pause();
            return;
        }
        if (Range is { } r)
            Start(Position >= r.From && Position < r.To - 0.01 ? Position : r.From, r.To);
        else
            Start(Position >= Length - 0.01 ? 0 : Position, null);
    }

    private void Start(double from, double? to)
    {
        End(_playback);
        IsPlaying = true;
        (_playTo, _reported) = (to, from);
        Position = from;
        // The clock runs from the playback's first report: before it the sound is still opening.
        _played.Reset();
        Playback? playing = null;
        playing = _playback = new Playback(Path, _services, TimeSpan.FromSeconds(from), video: false, null,
            at => Dispatcher.UIThread.Post(() =>
            {
                if (ReferenceEquals(_playback, playing))
                {
                    _reported = at.TotalSeconds;
                    _played.Restart();
                }
            }),
            () => Dispatcher.UIThread.Post(() =>
            {
                if (!ReferenceEquals(_playback, playing))
                    return;
                Pause();
                Position = _playTo ?? Length;
            }));
        _clock.Start();
    }

    /// <summary>The playhead between the playback's reports (five a second), on the clock; a stretch's end stops it.</summary>
    private void Tick()
    {
        if (!IsPlaying)
            return;
        var now = Math.Min(Length, _reported + _played.Elapsed.TotalSeconds);
        if (_playTo is { } to && now >= to)
        {
            Pause();
            Position = to;
            return;
        }
        Position = now;
        FollowPlayhead();
    }

    public void Pause()
    {
        End(_playback);
        _playback = null;
        IsPlaying = false;
        _clock.Stop();
    }

    /// <summary>Ended off the UI thread, as it waits for its thread and the speakers.</summary>
    private static void End(Playback? playback)
    {
        if (playback is not null)
            _ = Task.Run(playback.Dispose);
    }

    public void Dispose()
    {
        Pause();
        Picked = null;
    }
}
