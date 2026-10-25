using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using BlackBox.Core.Platform;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BlackBox.App.ViewModels;

/// <summary>
/// A video in the Inspect file panel: a plain player, frame by frame. It plays with its sound and
/// steps one frame back or on (the buttons, or Left and Right), and says which frame shows and when;
/// an area marked on the frame goes to the model cut from the frame at the video's own size (the
/// user's request, 2026-10-25: the chat's video card in the panel was "weird"). Frames are counted
/// from 0 here, shown and told to the model from 1.
/// </summary>
public sealed partial class InspectVideoViewModel : ObservableObject, IDisposable
{
    private readonly PreviewServices _services;

    /// <summary>How big frames are decoded to be shown: sharp in the widest panel, zoomed in a little.</summary>
    private const int ShownSide = 1920;

    public string Path { get; }

    public InspectVideoViewModel(string path, PreviewServices services)
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
    [NotifyPropertyChangedFor(nameof(FrameText), nameof(LastFrame))]
    private int _frameCount = 1;

    public int LastFrame => Math.Max(0, FrameCount - 1);

    public double FrameRate { get; private set; } = 25;

    /// <summary>The video's own size, which an area is told in.</summary>
    public PixelSize Size { get; private set; }

    /// <summary>The time of frame <paramref name="index"/>.</summary>
    public TimeSpan TimeOf(int index) => TimeSpan.FromSeconds(index / FrameRate);

    /// <summary>"Frame 38 / 150 · 1.233 s".</summary>
    public string FrameText => $"Frame {FrameIndex + 1:N0} / {FrameCount:N0} · {TimeOf(FrameIndex).TotalSeconds:0.000} s";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PlayIcon))]
    private bool _isPlaying;

    private static readonly Avalonia.Media.Geometry PlayShape = Avalonia.Media.StreamGeometry.Parse("M8 5v14l11-7z");
    private static readonly Avalonia.Media.Geometry PauseShape = Avalonia.Media.StreamGeometry.Parse("M6 4h4v16H6zM14 4h4v16h-4z");

    public Avalonia.Media.Geometry PlayIcon => IsPlaying ? PauseShape : PlayShape;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SoundIcon))]
    private bool _muted;

    private static readonly Avalonia.Media.Geometry SoundShape = Avalonia.Media.StreamGeometry.Parse(
        "M3 9v6h4l5 5V4L7 9H3zm13.5 3c0-1.77-1.02-3.29-2.5-4.03v8.05c1.48-.73 2.5-2.25 2.5-4.02zM14 3.23v2.06c2.89.86 5 3.54 5 6.71s-2.11 5.85-5 6.71v2.06c4.01-.91 7-4.49 7-8.77s-2.99-7.86-7-8.77z");
    private static readonly Avalonia.Media.Geometry MutedShape = Avalonia.Media.StreamGeometry.Parse(
        "M16.5 12c0-1.77-1.02-3.29-2.5-4.03v2.21l2.45 2.45c.03-.2.05-.41.05-.63zm2.5 0c0 .94-.2 1.82-.54 2.64l1.51 1.51C20.63 14.91 21 13.5 21 12c0-4.28-2.99-7.86-7-8.77v2.06c2.89.86 5 3.54 5 6.71zM4.27 3L3 4.27 7.73 9H3v6h4l5 5v-6.73l4.25 4.25c-.67.52-1.42.93-2.25 1.18v2.06c1.38-.31 2.63-.95 3.69-1.81L19.73 21 21 19.73l-9-9L4.27 3zM12 4L9.91 6.09 12 8.18V4z");

    public Avalonia.Media.Geometry SoundIcon => Muted ? MutedShape : SoundShape;

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
