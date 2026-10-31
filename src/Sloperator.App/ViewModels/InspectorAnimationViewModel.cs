using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Sloperator.Core.Media;
using Sloperator.Core.Platform;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SkiaSharp;

namespace Sloperator.App.ViewModels;

/// <summary>
/// An animation (a GIF, an animated WebP or PNG) in the File inspector panel, as a video is there:
/// frame by frame (the buttons, or Left and Right), playing over and over at its own frames' times,
/// zoomed, and an area marked on a frame cut from it at its own size (the user's request,
/// 2026-10-26). Its frames are all decoded at once, as each builds on the ones before it. Frames
/// are counted from 0 here, shown and told to the model from 1.
/// </summary>
public sealed partial class InspectorAnimationViewModel : ObservableObject, IDisposable
{
    /// <summary>Frames kept at most, and the memory they may take together.</summary>
    private const int MaxFrames = 2000;

    private const long MaxBytes = 512L * 1024 * 1024;

    public string Path { get; }

    private readonly List<(Bitmap Frame, TimeSpan Start, int Ms)> _frames = [];
    private readonly DispatcherTimer _timer;

    public InspectorAnimationViewModel(string path)
    {
        Path = path;
        // Made off the UI thread (the panel loads the file there): the timer is told whose it is, else it never ticks.
        _timer = new DispatcherTimer(DispatcherPriority.Render, Dispatcher.UIThread) { Interval = TimeSpan.FromMilliseconds(100) };
        _timer.Tick += (_, _) => Advance();
    }

    [ObservableProperty]
    private Bitmap? _frame;

    /// <summary>The frame showing, from 0.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FrameText))]
    private int _frameIndex;

    [ObservableProperty]
    private double _sliderFrame;

    public int FrameCount => Math.Max(1, _frames.Count);

    public int LastFrame => FrameCount - 1;

    /// <summary>The animation's own size, which an area is told in.</summary>
    public PixelSize Size { get; private set; }

    /// <summary>The frames in the file: more than <see cref="FrameCount"/> when only the first <see cref="MaxFrames"/> are kept.</summary>
    public int FileFrames { get; private set; }

    /// <summary>When frame <paramref name="index"/> comes on.</summary>
    public TimeSpan TimeOf(int index) => _frames.Count == 0 ? TimeSpan.Zero : _frames[Math.Clamp(index, 0, _frames.Count - 1)].Start;

    /// <summary>"Frame 3 / 24 · 0.300 s".</summary>
    public string FrameText => $"Frame {FrameIndex + 1:N0} / {FrameCount:N0} · {TimeOf(FrameIndex).TotalSeconds:0.000} s";

    /// <summary><see cref="FrameText"/> at its longest, which sizes its slot: the slider beside it keeps still.</summary>
    public string LongestFrameText => $"Frame {FrameCount:N0} / {FrameCount:N0} · {TimeOf(LastFrame).TotalSeconds:0.000} s";

    /// <summary>"480x270, 24 frames, 2.4 s"; once read.</summary>
    public string Details { get; private set; } = "";

    /// <summary>
    /// Decodes every frame, off the UI thread. False for one frame only (a still WebP or PNG named as
    /// an animation can be), which shows as a picture.
    /// </summary>
    public bool Load()
    {
        using var codec = PictureFile.Codec(Path) ?? throw new InvalidDataException("not a picture this can read");
        if (codec.FrameCount <= 1)
            return false;
        var info = codec.FrameInfo;
        var count = Math.Min(codec.FrameCount, MaxFrames);
        var (w, h) = (codec.Info.Width, Math.Max(1, codec.Info.Height));
        // Kept at their own size when they fit, smaller when not: a long 1080p GIF would take gigabytes.
        var scale = Math.Min(1.0, Math.Sqrt(MaxBytes / (4.0 * count * w * h)));
        var kept = new SKImageInfo(Math.Max(1, (int)(w * scale)), Math.Max(1, (int)(h * scale)), SKColorType.Bgra8888, SKAlphaType.Premul);
        using var full = new SKBitmap(new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul));
        var frames = new List<(Bitmap, TimeSpan, int)>();
        var at = TimeSpan.Zero;
        try
        {
            for (var i = 0; i < count; i++)
            {
                codec.GetPixels(full.Info, full.GetPixels(), new SKCodecOptions(i, info[i].RequiredFrame));
                var ms = Math.Max(info[i].Duration, 20);
                if (scale < 1)
                {
                    using var small = full.Resize(kept, Sampling.Medium);
                    frames.Add((FilePreview.ToBitmap(small), at, ms));
                }
                else
                    frames.Add((FilePreview.ToBitmap(full), at, ms));
                at += TimeSpan.FromMilliseconds(ms);
            }
        }
        catch
        {
            foreach (var (frame, _, _) in frames)
                frame.Dispose();
            throw;
        }
        FileFrames = codec.FrameCount;
        Size = new PixelSize(w, h);
        var total = codec.FrameCount == count ? at : TimeSpan.FromMilliseconds(info.Sum(f => Math.Max(f.Duration, 20)));
        Details = $"{w}x{h}, {codec.FrameCount:N0} frames, {total.TotalSeconds:0.0##} s" + (count < codec.FrameCount ? $" (the first {count:N0} shown)" : "");
        Dispatcher.UIThread.Post(() =>
        {
            if (_disposed)
            {
                foreach (var (frame, _, _) in frames)
                    frame.Dispose();
                return;
            }
            _frames.AddRange(frames);
            OnPropertyChanged(nameof(FrameCount));
            OnPropertyChanged(nameof(LastFrame));
            OnPropertyChanged(nameof(LongestFrameText));
            GoTo(0);
        });
        return true;
    }

    [RelayCommand]
    private void StepBack() => Step(-1);

    [RelayCommand]
    private void StepForward() => Step(1);

    public void Step(int frames)
    {
        Pause();
        GoTo(FrameIndex + frames);
    }

    private bool _reporting;

    public void GoTo(int index)
    {
        if (_frames.Count == 0)
            return;
        index = Math.Clamp(index, 0, _frames.Count - 1);
        Frame = _frames[index].Frame;
        FrameIndex = index;
        _reporting = true;
        SliderFrame = index;
        _reporting = false;
    }

    partial void OnSliderFrameChanged(double value)
    {
        if (_reporting)
            return;
        GoTo((int)Math.Round(value));
        if (IsPlaying)
            _timer.Interval = TimeSpan.FromMilliseconds(_frames[FrameIndex].Ms);
    }

    // ---- playing: over and over, as the file does -------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PlayIcon))]
    private bool _isPlaying;

    public Avalonia.Media.Geometry PlayIcon => PlayerIcons.Play(IsPlaying);

    [RelayCommand]
    private void Play()
    {
        if (IsPlaying)
        {
            Pause();
            return;
        }
        if (_frames.Count == 0)
            return;
        IsPlaying = true;
        _timer.Interval = TimeSpan.FromMilliseconds(_frames[FrameIndex].Ms);
        _timer.Start();
    }

    private void Advance()
    {
        if (_frames.Count == 0)
            return;
        GoTo((FrameIndex + 1) % _frames.Count);
        _timer.Interval = TimeSpan.FromMilliseconds(_frames[FrameIndex].Ms);
    }

    public void Pause()
    {
        _timer.Stop();
        IsPlaying = false;
    }

    /// <summary>Frame <paramref name="index"/> at the animation's own size, for an area cut from it; off the UI thread. Null when it can't be read.</summary>
    public RawImage? FullFrame(int index)
    {
        using var codec = PictureFile.Codec(Path);
        if (codec is null || index >= codec.FrameCount)
            return null;
        var info = codec.FrameInfo;
        using var full = new SKBitmap(new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Bgra8888, SKAlphaType.Premul));
        // Each frame is drawn over the ones it builds on: from the last that stands alone.
        var first = index;
        while (first > 0 && info[first].RequiredFrame >= 0)
            first = info[first].RequiredFrame;
        for (var i = first; i <= index; i++)
            codec.GetPixels(full.Info, full.GetPixels(), new SKCodecOptions(i, i == first ? -1 : info[i].RequiredFrame));
        var bgra = new byte[full.Width * full.Height * 4];
        for (var row = 0; row < full.Height; row++)
            System.Runtime.InteropServices.Marshal.Copy(full.GetPixels() + row * full.RowBytes, bgra, row * full.Width * 4, full.Width * 4);
        return new RawImage(full.Width, full.Height, bgra);
    }

    private bool _disposed;

    public void Dispose()
    {
        _disposed = true;
        Pause();
        Frame = null;
        foreach (var (frame, _, _) in _frames)
            frame.Dispose();
        _frames.Clear();
    }
}
