using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Sloperator.Core.Platform;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Sloperator.App.ViewModels;

/// <summary>
/// A video in the File inspector panel: a plain player, frame by frame. It plays with its sound and
/// steps one frame back or on (the buttons, or Left and Right), and says which frame shows and when;
/// an area marked on the frame goes to the model cut from the frame at the video's own size (the
/// user's request, 2026-10-25: the chat's video card in the panel was "weird"). Frames are counted
/// from 0 here, shown and told to the model from 1.
/// </summary>
public sealed partial class InspectorVideoViewModel : ObservableObject, IDisposable
{
    private readonly PreviewServices _services;

    /// <summary>How big frames are decoded to be shown: sharp in the widest panel, zoomed in a little.</summary>
    private const int ShownSide = 1920;

    public string Path { get; }

    public InspectorVideoViewModel(string path, PreviewServices services)
    {
        Path = path;
        _services = services;
    }

    [ObservableProperty]
    private Bitmap? _frame;

    /// <summary>The frame showing, from 0.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FrameText))]
    private int _frameIndex;

    /// <summary>The slider: the frame to show, set by the user or as it plays.</summary>
    [ObservableProperty]
    private double _sliderFrame;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FrameText), nameof(LastFrame), nameof(LongestFrameText))]
    private int _frameCount = 1;

    public int LastFrame => Math.Max(0, FrameCount - 1);

    public double FrameRate { get; private set; } = 25;

    /// <summary>The video's own size, which an area is told in.</summary>
    public PixelSize Size { get; private set; }

    /// <summary>The time of frame <paramref name="index"/>.</summary>
    public TimeSpan TimeOf(int index) => TimeSpan.FromSeconds(index / FrameRate);

    /// <summary>"Frame 38 / 150 · 1.233 s".</summary>
    public string FrameText => $"Frame {FrameIndex + 1:N0} / {FrameCount:N0} · {TimeOf(FrameIndex).TotalSeconds:0.000} s";

    /// <summary>
    /// <see cref="FrameText"/> at its longest for this video (its last frame), which sizes the slot it
    /// shows in: as it played, the text's width changed and the slider beside it wobbled (the user, 2026-10-25).
    /// </summary>
    public string LongestFrameText => $"Frame {FrameCount:N0} / {FrameCount:N0} · {TimeOf(LastFrame).TotalSeconds:0.000} s";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PlayIcon))]
    private bool _isPlaying;

    public Avalonia.Media.Geometry PlayIcon => PlayerIcons.Play(IsPlaying);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SoundIcon))]
    private bool _muted;

    public Avalonia.Media.Geometry SoundIcon => PlayerIcons.Sound(Muted);

    /// <summary>"776x560, 5.0 s at 30 fps, with sound", once read; null when it can't be played here.</summary>
    public string? Details { get; private set; }

    /// <summary>Reads what the video is and shows its first frame; false when nothing on this PC plays it.</summary>
    public bool Load()
    {
        if (_services.Decoder?.Probe(Path) is not { HasVideo: true } info)
            return false;
        FrameRate = info.FrameRate > 0 ? info.FrameRate : 25;
        var count = Math.Max(1, (int)Math.Round(info.Duration.TotalSeconds * FrameRate));
        Size = new PixelSize(info.Width, info.Height);
        Details = $"{info.Width}x{info.Height}, {count:N0} frames, {info.Duration.TotalSeconds:0.0} s at {FrameRate:0.##} fps" + (info.HasAudio ? ", with sound" : "");
        Dispatcher.UIThread.Post(() =>
        {
            if (_disposed) return;
            FrameCount = count;
            GoTo(0, force: true);
        });
        return true;
    }

    // ---- frame by frame ---------------------------------------------------------------------------

    private readonly object _gate = new();

    /// <summary>The frame asked for, and the one last decoded: a worker decodes until they meet.</summary>
    private int _want = -1, _decoded = -1;

    private bool _stepping;

    /// <summary>The reader frames are stepped with while paused, and the frame it read last.</summary>
    private Core.Media.IVideoReader? _reader;

    private int _readerAt = -1;

    [RelayCommand]
    private void StepBack() => Step(-1);

    [RelayCommand]
    private void StepForward() => Step(1);

    /// <summary>One frame or more back or on, from the one asked for last (a held key steps on from there).</summary>
    public void Step(int frames)
    {
        if (IsPlaying)
            Pause();
        int from;
        lock (_gate)
            from = _want >= 0 ? _want : FrameIndex;
        GoTo(from + frames);
    }

    /// <summary>Shows frame <paramref name="index"/>, decoded off the UI thread; steps asked for meanwhile are caught up with.</summary>
    public void GoTo(int index, bool force = false)
    {
        index = Math.Clamp(index, 0, LastFrame);
        lock (_gate)
        {
            if (!force && index == _want)
                return;
            _want = index;
            if (force)
                _decoded = -1;
            if (_stepping)
                return;
            _stepping = true;
        }
        Task.Run(Decode);
    }

    private void Decode()
    {
        while (true)
        {
            int want;
            lock (_gate)
            {
                if (_disposed || _want == _decoded)
                {
                    _stepping = false;
                    return;
                }
                want = _want;
            }
            RawImage? frame = null;
            try
            {
                _reader ??= _services.Decoder?.OpenVideo(Path, ShownSide);
                if (_reader is not null)
                {
                    // The next frame is read on; any other is sought (from its key frame).
                    if (want != _readerAt + 1)
                        _reader.Seek(TimeOf(want));
                    frame = _reader.Next()?.Frame;
                    _readerAt = frame is null ? -1 : want;
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _reader?.Dispose();
                _reader = null;
                _readerAt = -1;
            }
            lock (_gate)
                _decoded = want;
            if (frame is not null)
                Dispatcher.UIThread.Post(() => Show(want, frame));
        }
    }

    private bool _reporting;

    private void Show(int index, RawImage frame)
    {
        if (_disposed)
            return;
        var old = Frame;
        Frame = ToBitmap(frame);
        old?.Dispose();
        FrameIndex = index;
        _reporting = true;
        SliderFrame = index;
        _reporting = false;
    }

    /// <summary>The slider dragged: that frame, or playing on from it.</summary>
    partial void OnSliderFrameChanged(double value)
    {
        if (_reporting)
            return;
        var index = (int)Math.Round(value);
        if (IsPlaying)
            Start(index);
        else
            GoTo(index);
    }

    // ---- playing --------------------------------------------------------------------------------

    private Playback? _playback;

    [RelayCommand]
    private void Play()
    {
        if (IsPlaying)
            Pause();
        else
            Start(FrameIndex >= LastFrame ? 0 : FrameIndex);
    }

    [RelayCommand]
    private void ToggleSound()
    {
        Muted = !Muted;
        if (IsPlaying)
            Start(FrameIndex);
    }

    private void Start(int from)
    {
        End(_playback);
        IsPlaying = true;
        Playback? playing = null;
        playing = _playback = new Playback(Path, _services, TimeOf(from), video: true,
            (time, frame) => Dispatcher.UIThread.Post(() =>
            {
                if (ReferenceEquals(_playback, playing))
                    Show(Math.Clamp((int)Math.Round(time.TotalSeconds * FrameRate), 0, LastFrame), frame);
            }),
            _ => { },
            () => Dispatcher.UIThread.Post(() =>
            {
                if (ReferenceEquals(_playback, playing))
                    Pause();
            }), ShownSide, sound: !Muted);
    }

    /// <summary>Stops on the frame showing, from which it then steps.</summary>
    public void Pause()
    {
        End(_playback);
        _playback = null;
        IsPlaying = false;
        lock (_gate)
            _want = _decoded = FrameIndex;
    }

    /// <summary>Ended off the UI thread, as it waits for its thread and the speakers.</summary>
    private static void End(Playback? playback)
    {
        if (playback is not null)
            _ = Task.Run(playback.Dispose);
    }

    /// <summary>Frame <paramref name="index"/> at the video's own size, for an area cut from it; off the UI thread. Null when it can't be read.</summary>
    public RawImage? FullFrame(int index)
    {
        using var reader = _services.Decoder?.OpenVideo(Path, 0);
        if (reader is null)
            return null;
        reader.Seek(TimeOf(index));
        return reader.Next()?.Frame;
    }

    private static Bitmap ToBitmap(RawImage frame)
    {
        var pinned = GCHandle.Alloc(frame.Bgra, GCHandleType.Pinned);
        try
        {
            return new Bitmap(PixelFormat.Bgra8888, AlphaFormat.Premul, pinned.AddrOfPinnedObject(), new PixelSize(frame.Width, frame.Height), new Vector(96, 96), frame.Stride);
        }
        finally
        {
            pinned.Free();
        }
    }

    private bool _disposed;

    public void Dispose()
    {
        _disposed = true;
        End(_playback);
        _playback = null;
        Task.Run(() =>
        {
            lock (_gate)
                _want = _decoded;
            // The worker may still be reading: it ends at its next look, and the reader goes after.
            SpinWait.SpinUntil(() => { lock (_gate) return !_stepping; }, 3000);
            _reader?.Dispose();
            _reader = null;
        });
        Frame?.Dispose();
        Frame = null;
    }
}
