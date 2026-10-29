using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Sloperator.Core.Documents;
using Sloperator.Core.Media;
using Sloperator.Core.Platform;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SkiaSharp;

namespace Sloperator.App.ViewModels;

/// <summary>What the chat's previews need from the platform: decoders for video and sound, and the speakers.</summary>
public sealed class PreviewServices(IMediaDecoder? decoder, Func<IAudioOutput?> audio)
{
    public IMediaDecoder? Decoder { get; } = decoder;
    public IAudioOutput? NewAudioOutput() => audio();
}

/// <summary>
/// A file the agent made or showed, previewed in the chat: a picture, an animation, a video or a
/// sound with a player, a 3D model to turn around, or a card for anything else (a document, a
/// Markdown file) with a peek at its start. Each opens in the File inspector panel or shows in its folder.
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

    [RelayCommand]
    private void ShowInFolder() => ShowInFolder(Path);

    /// <summary>
    /// Opens the folder of <paramref name="path"/> with it selected; off the UI thread, as Windows'
    /// showing of a file on a share that is off waits for the share.
    /// </summary>
    internal static void ShowInFolder(string path) => Task.Run(() =>
    {
        try
        {
            if (OperatingSystem.IsWindows())
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true })?.Dispose();
            else
                Process.Start(new ProcessStartInfo(System.IO.Path.GetDirectoryName(path)!) { UseShellExecute = true })?.Dispose();
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
    /// loaded kept its frames, and their timer went on turning them for as long as Sloperator ran.
    /// </summary>
    protected void Show(Action show, Action? drop = null) => Dispatcher.UIThread.Post(() =>
    {
        if (Disposed) drop?.Invoke();
        else show();
    });

    /// <summary>The preview that suits a file, by its kind; it reads the file in the background.</summary>
    /// <param name="width">How wide pictures and frames are decoded: the chat's width, or a screen's for the full-screen viewer.</param>
    public static FilePreview For(string path, PreviewServices services, int width = Width)
    {
        var kind = MediaPreview.KindOf(path);
        FilePreview preview = kind switch
        {
            "image" => new ImagePreview(path),
            "animation" => new AnimationPreview(path, width),
            "video" => new VideoPreview(path, services, width),
            "audio" => new AudioPreview(path, services),
            "3d" => new ModelPreview(path),
            _ => new FileCardPreview(path),
        };
        preview.Load();
        return preview;
    }

    protected abstract void Load();

    internal static string Size(string path)
    {
        try { return Core.Strings.Bytes(new FileInfo(path).Length); }
        catch (IOException) { return ""; }
    }


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
        using var probe = PictureFile.Codec(Path);
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
/// <param name="width">How wide its frames are kept: the chat's width, or a screen's.</param>
public sealed partial class AnimationPreview(string path, int width = FilePreview.Width) : FilePreview(path)
{
    private readonly List<(Bitmap Frame, int Ms)> _frames = [];
    private DispatcherTimer? _timer;
    private int _at;

    [ObservableProperty]
    private Bitmap? _image;

    protected override void Load() => Background(() =>
    {
        using var codec = PictureFile.Codec(Path) ?? throw new InvalidDataException("not a picture");
        var info = codec.FrameInfo;
        var count = Math.Max(1, Math.Min(codec.FrameCount, 400));
        // Every frame is kept, so all of them together within 256 MB: full screen, a 16 s 1080p
        // animation of 384 frames took 3.2 GB.
        var fits = Math.Sqrt(256.0 * 1024 * 1024 / (4.0 * count * codec.Info.Width * Math.Max(1, codec.Info.Height)));
        var scale = Math.Min(1.0, Math.Min((double)width / codec.Info.Width, fits));
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

/// <summary>
/// A document, a Markdown file, a table, code: its name and size on a card, over a peek at its start
/// as the File inspector panel draws it (a PDF's first page, an Office file's own picture of itself, or
/// its first lines). A click on the peek opens the file in the full-screen viewer, as a picture's
/// does (the user's request, 2026-10-26); Inspect opens it in the panel.
/// </summary>
public sealed partial class FileCardPreview(string path) : FilePreview(path)
{
    /// <summary>
    /// How wide and tall the peek is at most, what is beyond cut off: the box of a picture's thumbnail
    /// in a message (the user found 480x240 too big, 2026-10-26).
    /// </summary>
    public const int PeekWidth = 300, PeekHeight = 180;

    /// <summary>A PDF's first page (its top, as wide as the card) or an Office file's picture of its first page or slide.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPeek))]
    private Bitmap? _thumbnail;

    /// <summary>Its start drawn as a reply is: Markdown, a table, a Word or PowerPoint file's text.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPeek), nameof(PeekMarkdownWidth))]
    private string? _peekMarkdown;

    /// <summary>Its start in a fixed-width font: code, text, an Excel file's cells, a PDF's text.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPeek))]
    private string? _peekText;

    /// <summary>
    /// How wide the Markdown is laid out: a table (a CSV's, an Excel sheet's) wider than the peek, so its
    /// rows are cut off on the right rather than wrapped; anything else to the peek's width (NaN).
    /// </summary>
    public double PeekMarkdownWidth => PeekMarkdown?.StartsWith('|') == true ? PeekWidth * 2 : double.NaN;

    /// <summary>There is something of the file to show, and so to open full screen.</summary>
    public bool HasPeek => Thumbnail is not null || PeekMarkdown is not null || PeekText is not null;

    protected override void Load() => Background(() =>
    {
        if (!File.Exists(Path))
        {
            Show(() => Details = "The file is no longer there.");
            return;
        }
        var size = Size(Path);
        Show(() => Details = size);
        var kind = FileInspector.KindOf(Path);
        if (kind == InspectorKind.Document && Picture() is { } picture)
        {
            Show(() => Thumbnail = picture, picture.Dispose);
            return;
        }
        var (markdown, text) = FileInspector.Peek(Path, kind);
        if (markdown is not null || text is not null)
            Show(() => (PeekMarkdown, PeekText) = (markdown, text));
    });

    /// <summary>A PDF's first page drawn by Windows' PDF renderer, or the picture an Office or OpenDocument file keeps of itself; null for neither.</summary>
    private Bitmap? Picture()
    {
        if (System.IO.Path.GetExtension(Path).Equals(".pdf", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                using var pdf = PdfPages.Open(Path);
                // Twice the peek's width, for a sharp page on a scaled screen; its top only when it
                // is taller than the peek.
                if (pdf.Render(0, PeekWidth * 2) is { } page)
                    return PdfPageItem.ToBitmap(page.Bgra, page.Width, Math.Min(page.Height, page.Width * PeekHeight / PeekWidth));
            }
            catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or IOException or UnauthorizedAccessException) { }
            return null; // its text, as in the panel
        }
        if (FileInspector.SavedThumbnail(Path) is not { } bytes)
            return null;
        try
        {
            using var stream = new MemoryStream(bytes);
            return new Bitmap(stream);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return null; }
    }

    protected override void Release()
    {
        Thumbnail?.Dispose();
        Thumbnail = null;
    }
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

    /// <param name="width">How wide the frames are decoded.</param>
    /// <param name="sound">False for a video playing without its sound (in the chat): the frames go by the clock.</param>
    public Playback(string path, PreviewServices services, TimeSpan from, bool video, Action<TimeSpan, RawImage>? onFrame, Action<TimeSpan> onPosition, Action onEnded,
        int width = FilePreview.Width, bool sound = true)
    {
        _task = Task.Run(() =>
        {
            IVideoReader? reader = null;
            try
            {
                if (video)
                {
                    reader = services.Decoder?.OpenVideo(path, width);
                    reader?.Seek(from);
                }
                // The sound, if there is one and the PC can play it; the frames follow its clock.
                if (sound && services.Decoder?.OpenAudio(path) is { } track)
                {
                    track.Seek(from);
                    var output = _stop.IsCancellationRequested ? null : services.NewAudioOutput();
                    if (output is null)
                        track.Dispose();
                    else
                        try
                        {
                            output.Play(track); // the output has the sound from here on
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
                        onFrame?.Invoke(frame.Time, frame.Frame);
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
/// <param name="width">How wide a video's frames are decoded: the chat's width, or a screen's.</param>
public abstract partial class PlayerPreview(string path, PreviewServices services, int width = FilePreview.Width) : FilePreview(path)
{
    protected PreviewServices Services { get; } = services;

    /// <summary>How wide a video's frames are decoded.</summary>
    protected int FrameWidth { get; } = width;

    private Playback? _playback;
    private bool _reporting;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PlayIcon))]
    private bool _isPlaying;

    private static readonly Avalonia.Media.Geometry PlayShape = Avalonia.Media.StreamGeometry.Parse("M8 5v14l11-7z");
    private static readonly Avalonia.Media.Geometry PauseShape = Avalonia.Media.StreamGeometry.Parse("M6 4h4v16H6zM14 4h4v16h-4z");

    /// <summary>The play or pause icon for the button.</summary>
    public Avalonia.Media.Geometry PlayIcon => IsPlaying ? PauseShape : PlayShape;

    /// <summary>Plays without its sound: a video in the chat, or one the user muted.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SoundIcon))]
    private bool _muted;

    private static readonly Avalonia.Media.Geometry SoundShape = Avalonia.Media.StreamGeometry.Parse(
        "M3 9v6h4l5 5V4L7 9H3zm13.5 3c0-1.77-1.02-3.29-2.5-4.03v8.05c1.48-.73 2.5-2.25 2.5-4.02zM14 3.23v2.06c2.89.86 5 3.54 5 6.71s-2.11 5.85-5 6.71v2.06c4.01-.91 7-4.49 7-8.77s-2.99-7.86-7-8.77z");
    private static readonly Avalonia.Media.Geometry MutedShape = Avalonia.Media.StreamGeometry.Parse(
        "M16.5 12c0-1.77-1.02-3.29-2.5-4.03v2.21l2.45 2.45c.03-.2.05-.41.05-.63zm2.5 0c0 .94-.2 1.82-.54 2.64l1.51 1.51C20.63 14.91 21 13.5 21 12c0-4.28-2.99-7.86-7-8.77v2.06c2.89.86 5 3.54 5 6.71zM4.27 3L3 4.27 7.73 9H3v6h4l5 5v-6.73l4.25 4.25c-.67.52-1.42.93-2.25 1.18v2.06c1.38-.31 2.63-.95 3.69-1.81L19.73 21 21 19.73l-9-9L4.27 3zM12 4L9.91 6.09 12 8.18V4z");

    /// <summary>The sound button's icon: a speaker, crossed out while muted.</summary>
    public Avalonia.Media.Geometry SoundIcon => Muted ? MutedShape : SoundShape;

    /// <summary>Muted or unmuted while it plays: it goes on from where it is, with or without its sound.</summary>
    partial void OnMutedChanged(bool value)
    {
        if (IsPlaying)
            Start(TimeSpan.FromSeconds(Position));
    }

    [RelayCommand]
    private void ToggleSound() => Muted = !Muted;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TimeText))]
    private double _length = 1;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TimeText))]
    private double _position;

    public string TimeText => $"{Core.Chats.ChatTimes.Stopwatch(TimeSpan.FromSeconds(Position))} / {Core.Chats.ChatTimes.Stopwatch(TimeSpan.FromSeconds(Length))}";

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
            UserPlayed(false);
            return;
        }
        Start(TimeSpan.FromSeconds(Position >= Length - 0.05 ? 0 : Position));
        UserPlayed(true);
    }

    /// <summary>The user pressed play or pause.</summary>
    protected virtual void UserPlayed(bool playing) { }

    protected void Start(TimeSpan from)
    {
        End(_playback);
        IsPlaying = true;
        _playback = new Playback(Path, Services, from, HasVideo, HasVideo ? (_, frame) => ShowFrame(frame) : null,
            at => Dispatcher.UIThread.Post(() => Report(at.TotalSeconds)),
            () => Dispatcher.UIThread.Post(Ended), FrameWidth, sound: !Muted);
    }

    /// <summary>It played to its end.</summary>
    protected virtual void Ended()
    {
        IsPlaying = false;
        Report(Length);
    }

    public void Stop()
    {
        End(_playback);
        _playback = null;
        IsPlaying = false;
    }

    /// <summary>
    /// Stops playing and hands over the playback still holding the file, for the caller to end
    /// before it writes over the file (see <see cref="Core.Media.MediaRelease"/>); null when it isn't playing.
    /// </summary>
    internal IDisposable? LetGo()
    {
        var playback = _playback;
        _playback = null;
        IsPlaying = false;
        return playback;
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

/// <summary>
/// A video: its first frame, then playing in place. In the chat it plays by itself while it is on
/// screen, over and over and without its sound, and stops when scrolled away (see
/// <see cref="Views.InView"/>); the full-screen viewer plays it with its sound.
/// </summary>
public sealed partial class VideoPreview(string path, PreviewServices services, int width = FilePreview.Width) : PlayerPreview(path, services, width)
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

    /// <summary>Its first frame and length are in: it can play.</summary>
    private bool _loaded;

    protected override void Load() => Background(() =>
    {
        var decoder = Services.Decoder;
        var info = decoder?.Probe(Path);
        if (info is not { HasVideo: true })
        {
            Dispatcher.UIThread.Post(() => Details = $"{Size(Path)} (nothing on this PC can play it here)");
            return;
        }
        using var reader = decoder!.OpenVideo(Path, FrameWidth);
        var first = reader?.Next();
        var details = $"{info.Width}x{info.Height}, {info.Duration.TotalSeconds:0.0} s" + (info.FrameRate > 0 ? $" at {info.FrameRate:0.##} fps" : "") +
                      (info.HasAudio ? ", with sound" : ", no sound") + ", " + Size(Path);
        Show(() =>
        {
            Details = details;
            Length = Math.Max(0.1, info.Duration.TotalSeconds);
            if (first is { } f)
                ShowFrameNow(f.Frame);
            _loaded = true;
            if (_playWhenLoaded || PlaysInView && _inView && !_userPaused)
                Start(TimeSpan.Zero);
        });
    });

    // ---- playing by itself while on screen ------------------------------------------------------

    /// <summary>A preview in the chat: it plays by itself while on screen, without its sound.</summary>
    public bool PlaysInView { get; private set; }

    private bool _inView, _userPaused, _playWhenLoaded;

    /// <summary>Makes this a preview that plays by itself while on screen, muted; set by the chat's template.</summary>
    public void PlayInView()
    {
        PlaysInView = true;
        Muted = true;
    }

    /// <summary>Plays from the start as soon as it can (the full-screen viewer).</summary>
    public void PlayWhenLoaded()
    {
        if (_loaded) Start(TimeSpan.Zero);
        else _playWhenLoaded = true;
    }

    /// <summary>
    /// It came into view or left it (scrolled, the window minimized, the full-screen viewer over it).
    /// On screen it plays on from where it was, unless the user paused it; off screen it pauses, so
    /// a long chat doesn't decode videos nobody sees.
    /// </summary>
    public void SetInView(bool inView)
    {
        if (_inView == inView || !PlaysInView)
        {
            _inView = inView;
            return;
        }
        _inView = inView;
        if (inView && _loaded && !_userPaused && !IsPlaying)
            Start(TimeSpan.FromSeconds(Position >= Length - 0.05 ? 0 : Position));
        else if (!inView && IsPlaying)
            Stop();
    }

    protected override void UserPlayed(bool playing) => _userPaused = !playing;

    /// <summary>Frames shown since it last started again by itself: none, and it doesn't start again.</summary>
    private int _shown;

    /// <summary>
    /// On screen in the chat it starts again, as a GIF would; unless nothing played (the file
    /// deleted or no longer readable), when it started again at once, for ever, opening the
    /// decoder (and ffmpeg) each time.
    /// </summary>
    protected override void Ended()
    {
        if (PlaysInView && _inView && !_userPaused && !Disposed && Interlocked.Exchange(ref _shown, 0) > 0)
            Start(TimeSpan.Zero);
        else
            base.Ended();
    }

    protected override void ShowFrame(RawImage frame)
    {
        Interlocked.Increment(ref _shown);
        Show(() => ShowFrameNow(frame));
    }

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
            using var reader = Services.Decoder?.OpenVideo(Path, FrameWidth);
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
            Dispatcher.UIThread.Post(() => Details = $"{Size(Path)} (nothing on this PC can play it here)");
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
        using var paint = new SKPaint { Color = new SKColor(0xFC, 0xC2, 0x1B), StrokeWidth = 1, IsAntialias = false };
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
