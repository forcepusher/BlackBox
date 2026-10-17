using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using BlackBox.Core.Media;
using BlackBox.Core.Platform;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SkiaSharp;

namespace BlackBox.App.ViewModels;

/// <summary>What the chat's previews need from the platform: decoders for video and sound, and the speakers.</summary>
public sealed class PreviewServices(IMediaDecoder? decoder, Func<IAudioOutput?> audio)
{
    public IMediaDecoder? Decoder { get; } = decoder;
    public IAudioOutput? NewAudioOutput() => audio();
}

/// <summary>
/// A file the agent made or showed, previewed in the chat: a picture, an animation, a video or a
/// sound with a player, a 3D model to turn around, or a card for anything else. Each opens in its
/// own program or shows in its folder.
/// </summary>
public abstract partial class FilePreview(string path) : ObservableObject, IDisposable
{
    /// <summary>How wide previews are drawn in the chat.</summary>
    public const int Width = 480;

    public string Path { get; } = path;
    public string FileName { get; } = System.IO.Path.GetFileName(path);

    /// <summary>"video 640x640, 5.1 s, 1.4 MB", filled in once the file has been read.</summary>
    [ObservableProperty]
    private string _details = "";

    // Both off the UI thread: Windows' opening of a file on a share that is off waits for the share.
    [RelayCommand]
    private void Open() => Task.Run(() =>
    {
        try { Process.Start(new ProcessStartInfo(Path) { UseShellExecute = true })?.Dispose(); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Dispatcher.UIThread.Post(() => Details = "Nothing on this PC opens it.");
        }
    });

    [RelayCommand]
    private void ShowInFolder() => Task.Run(() =>
    {
        try
        {
            if (OperatingSystem.IsWindows())
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{Path}\"") { UseShellExecute = true })?.Dispose();
            else
                Process.Start(new ProcessStartInfo(System.IO.Path.GetDirectoryName(Path)!) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { }
    });

    /// <summary>Set once the preview has left the chat: what a load still under way brings then is let go at once.</summary>
    protected bool Disposed { get; private set; }

    public void Dispose()
    {
        Disposed = true;
        Release();
    }

    /// <summary>Lets go of what the preview holds (decoded pictures, a player).</summary>
    protected virtual void Release() { }

    /// <summary>
    /// Runs <paramref name="show"/> on the UI thread, unless the preview has left the chat by then:
    /// then <paramref name="drop"/> lets go of what was loaded for it. A chat left while a GIF
    /// loaded kept its frames, and their timer went on turning them for as long as BlackBox ran.
    /// </summary>
    protected void Show(Action show, Action? drop = null) => Dispatcher.UIThread.Post(() =>
    {
        if (Disposed) drop?.Invoke();
        else show();
    });

    /// <summary>The preview that suits a file, by its kind; it reads the file in the background.</summary>
    public static FilePreview For(string path, PreviewServices services)
    {
        FilePreview preview = MediaPreview.KindOf(path) switch
        {
            "image" => new ImagePreview(path),
            "animation" => new AnimationPreview(path),
            "video" => new VideoPreview(path, services),
            "audio" => new AudioPreview(path, services),
            "3d" => new ModelPreview(path),
            _ => new FileCardPreview(path),
        };
        preview.Load();
        return preview;
    }

    protected abstract void Load();

    protected static string Size(string path)
    {
        try { return Sized(new FileInfo(path).Length); }
        catch (IOException) { return ""; }
    }

    private static string Sized(long bytes) => bytes >= 1 << 20 ? $"{bytes / 1048576.0:0.0} MB" : bytes >= 1024 ? $"{bytes / 1024.0:0} KB" : $"{bytes} bytes";

    /// <summary>Runs <paramref name="work"/> off the UI thread; a file that can't be read says so in <see cref="Details"/>.</summary>
    protected void Background(Action work) => Task.Run(() =>
    {
        try
        {
            work();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Looked at here, not on the UI thread: a share that just went away is the usual cause.
            var details = File.Exists(Path) ? $"Can't be shown: {ex.Message}" : "The file is no longer there.";
            Dispatcher.UIThread.Post(() => Details = details);
        }
    });

    internal static Bitmap ToBitmap(SKBitmap source)
    {
        using var bgra = source.ColorType == SKColorType.Bgra8888 ? null : source.Copy(SKColorType.Bgra8888);
        var b = bgra ?? source;
        return new Bitmap(PixelFormat.Bgra8888, AlphaFormat.Premul, b.GetPixels(), new PixelSize(b.Width, b.Height), new Vector(96, 96), b.RowBytes);
    }

    internal static string Clock(TimeSpan t) => t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");
}

/// <summary>A picture, at the chat's width.</summary>
public sealed partial class ImagePreview(string path) : FilePreview(path)
{
    [ObservableProperty]
    private Bitmap? _image;

    protected override void Load() => Background(() =>
    {
        using var stream = File.OpenRead(Path);
        var bitmap = Bitmap.DecodeToWidth(stream, Width, BitmapInterpolationMode.HighQuality);
        var size = Size(Path);
        using var probe = SKCodec.Create(Path);
        var details = probe is null ? size : $"{probe.Info.Width}x{probe.Info.Height}, {size}";
        Show(() => (Image, Details) = (bitmap, details), bitmap.Dispose);
    });

    protected override void Release()
    {
        Image?.Dispose();
        Image = null;
    }
}

/// <summary>An animated GIF or WebP, playing over and over.</summary>
public sealed partial class AnimationPreview(string path) : FilePreview(path)
{
    private readonly List<(Bitmap Frame, int Ms)> _frames = [];
    private DispatcherTimer? _timer;
    private int _at;

    [ObservableProperty]
    private Bitmap? _image;

    protected override void Load() => Background(() =>
    {
        using var codec = SKCodec.Create(Path) ?? throw new InvalidDataException("not a picture");
        var info = codec.FrameInfo;
        var count = Math.Max(1, Math.Min(codec.FrameCount, 400));
        var scale = Math.Min(1.0, (double)Width / codec.Info.Width);
        var size = new SKImageInfo(Math.Max(1, (int)(codec.Info.Width * scale)), Math.Max(1, (int)(codec.Info.Height * scale)), SKColorType.Bgra8888, SKAlphaType.Premul);
        using var full = new SKBitmap(new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Bgra8888, SKAlphaType.Premul));
        var frames = new List<(Bitmap, int)>();
        var total = 0;
        for (var i = 0; i < count; i++)
        {
            codec.GetPixels(full.Info, full.GetPixels(), new SKCodecOptions(i, codec.FrameCount > 1 ? info[i].RequiredFrame : -1));
            using var small = full.Resize(size, SKFilterQuality.Medium);
            var ms = codec.FrameCount > 1 ? Math.Max(info[i].Duration, 20) : 100;
            total += ms;
            frames.Add((ToBitmap(small), ms));
        }
        var details = codec.FrameCount > 1 ? $"{codec.Info.Width}x{codec.Info.Height}, {codec.FrameCount} frames ({total / 1000.0:0.0} s), {Size(Path)}" : Size(Path);
        Show(() =>
        {
            _frames.AddRange(frames);
            Details = details;
            Image = _frames[0].Frame;
            if (_frames.Count > 1)
            {
                _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(_frames[0].Ms), DispatcherPriority.Background, (_, _) => Advance());
                _timer.Start();
            }
        }, () =>
        {
            foreach (var (frame, _) in frames) frame.Dispose();
        });
    });

    private void Advance()
    {
        _at = (_at + 1) % _frames.Count;
        Image = _frames[_at].Frame;
        _timer!.Interval = TimeSpan.FromMilliseconds(_frames[_at].Ms);
    }

    protected override void Release()
    {
        _timer?.Stop();
        Image = null;
        foreach (var (frame, _) in _frames) frame.Dispose();
        _frames.Clear();
    }
}

/// <summary>A file with nothing to show inside the chat: its name and size, to open or find.</summary>
public sealed class FileCardPreview(string path) : FilePreview(path)
{
    protected override void Load() => Background(() =>
    {
        var details = File.Exists(Path) ? Size(Path) : "The file is no longer there.";
        Show(() => Details = details);
    });
}

/// <summary>A 3D model, turned around with the mouse (see <see cref="Views.ModelViewer"/>).</summary>
public sealed partial class ModelPreview(string path) : FilePreview(path)
{
    [ObservableProperty]
    private Mesh? _mesh;

    protected override void Load() => Background(() =>
    {
        var mesh = Mesh.Load(Path);
        var details = mesh.Describe().Replace("3D model: ", "") + ", " + Size(Path);
        Show(() => (Mesh, Details) = (mesh, details), mesh.Dispose);
    });

    protected override void Release()
    {
        Mesh?.Dispose();
        Mesh = null;
    }
}

/// <summary>Plays a video or a sound from a point: frames on time with the sound (or the clock, without one).</summary>
internal sealed class Playback : IDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _task;
    private IAudioOutput? _output;

    public Playback(string path, PreviewServices services, TimeSpan from, bool video, Action<RawImage>? onFrame, Action<TimeSpan> onPosition, Action onEnded)
    {
        _task = Task.Run(() =>
        {
            IVideoReader? reader = null;
            try
            {
                if (video)
                {
                    reader = services.Decoder?.OpenVideo(path, FilePreview.Width);
                    reader?.Seek(from);
                }
                // The sound, if there is one and the PC can play it; the frames follow its clock.
                if (services.Decoder?.OpenAudio(path) is { } sound)
                {
                    sound.Seek(from);
                    var output = _stop.IsCancellationRequested ? null : services.NewAudioOutput();
                    if (output is null)
                        sound.Dispose();
                    else
                        try
                        {
                            output.Play(sound); // the output has the sound from here on
                            _output = output;
                        }
                        catch (Exception ex) when (ex is not OutOfMemoryException)
                        {
                            // No speakers to open (a remote session without sound, a headset just
                            // unplugged): the video didn't play at all; its frames go by the clock now.
                            output.Dispose();
                        }
                }
                var clock = Stopwatch.StartNew();
                TimeSpan Now() => _output is { } o ? o.Played : clock.Elapsed;
                var lastReport = TimeSpan.FromSeconds(-1);
                while (!_stop.IsCancellationRequested)
                {
                    if (reader is not null)
                    {
                        if (reader.Next() is not { } frame)
                            break;
                        var due = frame.Time - from;
                        var wait = due - Now();
                        if (wait < TimeSpan.FromMilliseconds(-250))
                            continue; // late: drop it and catch up
                        if (wait > TimeSpan.Zero)
                            _stop.Token.WaitHandle.WaitOne(wait > TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1) : wait);
                        if (_stop.IsCancellationRequested)
                            break;
                        onFrame?.Invoke(frame.Frame);
                    }
                    else
                    {
                        if (_output is null || _output.Ended)
                            break;
                        _stop.Token.WaitHandle.WaitOne(50);
                    }
                    if ((Now() - lastReport).Duration() >= TimeSpan.FromMilliseconds(200))
                    {
                        lastReport = Now();
                        onPosition(from + lastReport);
                    }
                }
                // A video's sound may run a little past its last frame.
                while (!_stop.IsCancellationRequested && _output is { Ended: false })
                {
                    onPosition(from + Now());
                    _stop.Token.WaitHandle.WaitOne(100);
                }
            }
            finally
            {
                reader?.Dispose();
                if (!_stop.IsCancellationRequested)
                    onEnded();
                else
                {
                    // Stopped: here too, as Dispose may have given up waiting before the sound was
                    // open (ffmpeg starting, a file on a share), and the sound then played to its
                    // end with nothing to stop it.
                    Interlocked.Exchange(ref _output, null)?.Dispose();
                }
            }
        });
    }

    public void Dispose()
    {
        _stop.Cancel();
        try
        {
            if (!_task.Wait(2000))
                return; // the task lets go of the sound when it ends
        }
        catch (AggregateException) { }
        Interlocked.Exchange(ref _output, null)?.Dispose();
    }
}

/// <summary>Shared by the video and sound players: play, pause, a position to drag, and the time.</summary>
public abstract partial class PlayerPreview(string path, PreviewServices services) : FilePreview(path)
{
    protected PreviewServices Services { get; } = services;
    private Playback? _playback;
    private bool _reporting;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PlayIcon))]
    private bool _isPlaying;

    private static readonly Avalonia.Media.Geometry PlayShape = Avalonia.Media.StreamGeometry.Parse("M8 5v14l11-7z");
    private static readonly Avalonia.Media.Geometry PauseShape = Avalonia.Media.StreamGeometry.Parse("M6 4h4v16H6zM14 4h4v16h-4z");

    /// <summary>The play or pause icon for the button.</summary>
    public Avalonia.Media.Geometry PlayIcon => IsPlaying ? PauseShape : PlayShape;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TimeText))]
    private double _length = 1;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TimeText))]
    private double _position;

    public string TimeText => $"{Clock(TimeSpan.FromSeconds(Position))} / {Clock(TimeSpan.FromSeconds(Length))}";

    private int _seek;

    /// <summary>The slider moved: by the user (go there) or by the playback reporting (nothing to do).</summary>
    partial void OnPositionChanged(double value)
    {
        if (_reporting)
            return;
        if (!IsPlaying)
        {
            ShowStill(TimeSpan.FromSeconds(value));
            return;
        }
        // Once the slider rests, not at every step of a drag: each start opens the file afresh
        // (ffmpeg twice), and the window stuttered through the drag.
        var seek = ++_seek;
        DispatcherTimer.RunOnce(() =>
        {
            if (seek == _seek && IsPlaying)
                Start(TimeSpan.FromSeconds(Position));
        }, TimeSpan.FromMilliseconds(150));
    }

    [RelayCommand]
    private void Play()
    {
        if (IsPlaying)
        {
            Stop();
            return;
        }
        Start(TimeSpan.FromSeconds(Position >= Length - 0.05 ? 0 : Position));
    }

    private void Start(TimeSpan from)
    {
        End(_playback);
        IsPlaying = true;
        _playback = new Playback(Path, Services, from, HasVideo, HasVideo ? ShowFrame : null,
            at => Dispatcher.UIThread.Post(() => Report(at.TotalSeconds)),
            () => Dispatcher.UIThread.Post(() =>
            {
                IsPlaying = false;
                Report(Length);
            }));
    }

    public void Stop()
    {
        End(_playback);
        _playback = null;
        IsPlaying = false;
    }

    /// <summary>
    /// Ends a playback off the UI thread: it waits up to two seconds for its thread, and its
    /// speakers' for as long again, which held the window still at every pause or new position.
    /// </summary>
    private static void End(Playback? playback)
    {
        if (playback is not null)
            _ = Task.Run(playback.Dispose);
    }

    private void Report(double seconds)
    {
        _reporting = true;
        Position = Math.Clamp(seconds, 0, Length);
        _reporting = false;
    }

    protected abstract bool HasVideo { get; }

    /// <summary>A frame from the playback's thread, to show.</summary>
    protected virtual void ShowFrame(RawImage frame) { }

    /// <summary>Shows the still at a point when the slider is moved while paused.</summary>
    protected virtual void ShowStill(TimeSpan at) { }

    protected override void Release() => Stop();
}

/// <summary>A video: its first frame, then playing in place with its sound.</summary>
public sealed partial class VideoPreview(string path, PreviewServices services) : PlayerPreview(path, services)
{
    private int _stillRequest;

    protected override void Release()
    {
        base.Release();
        Frame?.Dispose();
        Frame = null;
    }

    [ObservableProperty]
    private Bitmap? _frame;

    protected override bool HasVideo => true;

    protected override void Load() => Background(() =>
    {
        var decoder = Services.Decoder;
        var info = decoder?.Probe(Path);
        if (info is not { HasVideo: true })
        {
            Dispatcher.UIThread.Post(() => Details = $"{Size(Path)} (nothing on this PC can play it here; Open uses your player)");
            return;
        }
        using var reader = decoder!.OpenVideo(Path, Width);
        var first = reader?.Next();
        var details = $"{info.Width}x{info.Height}, {info.Duration.TotalSeconds:0.0} s" + (info.FrameRate > 0 ? $" at {info.FrameRate:0.##} fps" : "") +
                      (info.HasAudio ? ", with sound" : ", no sound") + ", " + Size(Path);
        Show(() =>
        {
            Details = details;
            Length = Math.Max(0.1, info.Duration.TotalSeconds);
            if (first is { } f)
                ShowFrameNow(f.Frame);
        });
    });

    protected override void ShowFrame(RawImage frame) => Show(() => ShowFrameNow(frame));

    /// <summary>A bitmap of its own for each frame (the Image redraws when its source changes); the one before is let go.</summary>
    private void ShowFrameNow(RawImage frame)
    {
        Bitmap next;
        var pinned = GCHandle.Alloc(frame.Bgra, GCHandleType.Pinned);
        try
        {
            next = new Bitmap(PixelFormat.Bgra8888, AlphaFormat.Premul, pinned.AddrOfPinnedObject(), new PixelSize(frame.Width, frame.Height), new Vector(96, 96), frame.Stride);
        }
        finally
        {
            pinned.Free();
        }
        var old = Frame;
        Frame = next;
        old?.Dispose();
    }

    protected override void ShowStill(TimeSpan at)
    {
        var request = ++_stillRequest;
        Background(() =>
        {
            if (request != _stillRequest) return;
            using var reader = Services.Decoder?.OpenVideo(Path, Width);
            if (reader is null) return;
            reader.Seek(at);
            if (reader.Next() is { } f && request == _stillRequest)
                Show(() => ShowFrameNow(f.Frame));
        });
    }
}

/// <summary>A sound: its waveform, playing with a moving position.</summary>
public sealed partial class AudioPreview(string path, PreviewServices services) : PlayerPreview(path, services)
{
    [ObservableProperty]
    private Bitmap? _waveform;

    protected override bool HasVideo => false;

    protected override void Load() => Background(() =>
    {
        using var reader = Services.Decoder?.OpenAudio(Path);
        if (reader is null)
        {
            Dispatcher.UIThread.Post(() => Details = $"{Size(Path)} (nothing on this PC can play it here; Open uses your player)");
            return;
        }
        var (mono, rate) = MediaPreview.ReadMono(reader, TimeSpan.FromMinutes(20));
        var seconds = Math.Max(mono.Length / (double)rate, reader.Duration.TotalSeconds);
        using var wave = DrawWaveform(mono);
        var bitmap = ToBitmap(wave);
        var channels = reader.Channels == 1 ? "mono" : reader.Channels == 2 ? "stereo" : $"{reader.Channels} channels";
        var details = $"{seconds:0.0} s, {rate / 1000.0:0.#} kHz {channels}, {Size(Path)}";
        Show(() => (Waveform, Details, Length) = (bitmap, details, Math.Max(0.1, seconds)), bitmap.Dispose);
    });

    private static SKBitmap DrawWaveform(float[] mono)
    {
        const int height = 56;
        var bitmap = new SKBitmap(Width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Transparent);
        using var paint = new SKPaint { Color = new SKColor(0xE0, 0xA6, 0x4B), StrokeWidth = 1, IsAntialias = false };
        var per = Math.Max(1, mono.Length / (double)Width);
        // As tall as the strip allows (up to ten times louder), so a quiet sound still shows its shape.
        var loudest = 0f;
        foreach (var s in mono) loudest = Math.Max(loudest, Math.Abs(s));
        var gain = loudest > 0 ? Math.Min(10f, 0.95f / loudest) : 1f;
        for (var x = 0; x < Width; x++)
        {
            var from = (int)(x * per);
            var to = Math.Min(mono.Length, (int)((x + 1) * per));
            float peak = 0;
            for (var i = from; i < to; i++) peak = Math.Max(peak, Math.Abs(mono[i]));
            var h = Math.Max(1, peak * gain * (height / 2f - 2));
            canvas.DrawLine(x + 0.5f, height / 2f - h, x + 0.5f, height / 2f + h, paint);
        }
        return bitmap;
    }

    protected override void Release()
    {
        base.Release();
        Waveform?.Dispose();
        Waveform = null;
    }
}
