using System.Diagnostics;
using Avalonia.Threading;
using BlackBox.Core.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BlackBox.App.ViewModels;

/// <summary>
/// A 3D model in the File inspector panel (see <see cref="Views.ModelViewer"/>): a left drag turns it,
/// the wheel comes closer, a right or middle drag moves it about, and buttons show it from the front,
/// a side, the back or above. Where it is seen from is said under it and told to the model; with
/// Area on, an area of the view is marked to ask about (the user's request, 2026-10-26). A model with
/// animations (an FBX file can hold several) plays them over and over and steps a frame at a time.
/// </summary>
public sealed partial class InspectorModelViewModel : ObservableObject, IDisposable
{
    public string Path { get; }

    private readonly DispatcherTimer _timer;
    private readonly Stopwatch _clock = new();
    private double _playFrom;

    public InspectorModelViewModel(string path)
    {
        Path = path;
        _timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(33) };
        _timer.Tick += (_, _) => Tick();
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Animations), nameof(HasAnimations), nameof(HasSeveralAnimations))]
    private Mesh? _mesh;

    public IReadOnlyList<MeshAnimation> Animations => Mesh?.CanMove == true ? Mesh.Animations : [];

    public bool HasAnimations => Animations.Count > 0;

    /// <summary>More than one animation: a list to pick one from.</summary>
    public bool HasSeveralAnimations => Animations.Count > 1;

    partial void OnMeshChanged(Mesh? value) => Animation = Animations.FirstOrDefault();

    /// <summary>The animation playing or shown; null for a model that keeps still.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Duration), nameof(TimeText), nameof(LongestTimeText))]
    private MeshAnimation? _animation;

    partial void OnAnimationChanged(MeshAnimation? value)
    {
        Pause();
        Time = 0;
    }

    public double Duration => Animation?.Duration ?? 0;

    /// <summary>Seconds into the animation.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TimeText), nameof(FrameIndex))]
    private double _time;

    /// <summary>The frame showing, from 0, at the file's frame rate.</summary>
    public int FrameIndex => Animation is { } a ? Math.Clamp((int)Math.Round(Time * a.FrameRate), 0, a.FrameCount - 1) : 0;

    /// <summary>"Frame 13 / 25 · 0.500 s".</summary>
    public string TimeText => Animation is { } a ? $"Frame {FrameIndex + 1:N0} / {a.FrameCount:N0} · {Time:0.000} s" : "";

    /// <summary><see cref="TimeText"/> at its longest, which sizes its slot: the slider beside it keeps still.</summary>
    public string LongestTimeText => Animation is { } a ? $"Frame {a.FrameCount:N0} / {a.FrameCount:N0} · {a.Duration:0.000} s" : "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PlayIcon))]
    private bool _isPlaying;

    private static readonly Avalonia.Media.Geometry PlayShape = Avalonia.Media.StreamGeometry.Parse("M8 5v14l11-7z");
    private static readonly Avalonia.Media.Geometry PauseShape = Avalonia.Media.StreamGeometry.Parse("M6 4h4v16H6zM14 4h4v16h-4z");

    public Avalonia.Media.Geometry PlayIcon => IsPlaying ? PauseShape : PlayShape;

    [RelayCommand]
    private void Play()
    {
        if (IsPlaying)
        {
            Pause();
            return;
        }
        if (Animation is null)
            return;
        if (Time >= Duration - 1e-6)
            Time = 0;
        (_playFrom, IsPlaying) = (Time, true);
        _clock.Restart();
        _timer.Start();
    }

    /// <summary>On the clock, over and over as games play an animation.</summary>
    private void Tick()
    {
        if (Animation is not { Duration: > 0 } a)
        {
            Pause();
            return;
        }
        _ticking = true;
        Time = (_playFrom + _clock.Elapsed.TotalSeconds) % a.Duration;
        _ticking = false;
    }

    private bool _ticking;

    /// <summary>The slider dragged while it plays: it plays on from there.</summary>
    partial void OnTimeChanged(double value)
    {
        if (_ticking || !IsPlaying)
            return;
        _playFrom = value;
        _clock.Restart();
    }

    public void Pause()
    {
        _timer.Stop();
        IsPlaying = false;
    }

    [RelayCommand]
    private void StepBack() => Step(-1);

    [RelayCommand]
    private void StepForward() => Step(1);

    /// <summary>One frame or more back or on, at the file's frame rate.</summary>
    public void Step(int frames)
    {
        if (Animation is not { } a)
            return;
        Pause();
        Time = Math.Clamp(FrameIndex + frames, 0, a.FrameCount - 1) / a.FrameRate;
    }

    /// <summary>What the model is told of the animation shown: "at 0.500 s (frame 13 of 25) of its animation Scene, paused"; null for a model that keeps still.</summary>
    public string? TakeText => Animation is { } a ? $"at {Time:0.000} s (frame {FrameIndex + 1} of {a.FrameCount}, {a.FrameRate:0.##} fps) of its animation \"{a.Name}\" ({a.Duration:0.###} s long){(IsPlaying ? ", playing" : ", paused there")}" : null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ViewText))]
    private double _yaw = Views.ModelViewer.StartYaw;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ViewText))]
    private double _pitch = Views.ModelViewer.StartPitch;

    /// <summary>1 fits the whole model, up to 8.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ViewText))]
    private double _zoom = 1;

    [ObservableProperty]
    private double _panX;

    [ObservableProperty]
    private double _panY;

    public const double MaxZoom = 8;

    /// <summary>"From the front left, 30° round, 15° above".</summary>
    public string ViewText => Describe(Yaw, Pitch);

    /// <summary>
    /// Where a view is from, in words and degrees: 0 round is the front (the model's +Z), 90 its right
    /// (+X), as <see cref="MeshRenderer"/> turns it.
    /// </summary>
    internal static string Describe(double yaw, double pitch)
    {
        var y = (yaw % 360 + 540) % 360 - 180; // -180..180
        var side = Math.Abs(y) switch
        {
            <= 22.5 => "the front",
            >= 157.5 => "the back",
            >= 67.5 and <= 112.5 => y > 0 ? "the right" : "the left",
            < 67.5 => y > 0 ? "the front right" : "the front left",
            _ => y > 0 ? "the back right" : "the back left",
        };
        var height = pitch switch
        {
            >= 60 => "above",
            >= 10 => $"{pitch:0}° above",
            <= -60 => "below",
            <= -10 => $"{-pitch:0}° below",
            _ => "level",
        };
        return $"From {side} ({y:0}° round), {height}";
    }

    /// <summary>"12,480 triangles, 6,300 vertices, 1.2 x 0.8 x 0.4 units"; once read.</summary>
    public string Details { get; private set; } = "";

    /// <summary>Reads the model, off the UI thread.</summary>
    public Mesh Load()
    {
        var mesh = Mesh.Load(Path);
        Details = mesh.Describe().Replace("3D model: ", "");
        return mesh;
    }

    [RelayCommand]
    private void ViewFrom(string side)
    {
        (Yaw, Pitch) = side switch
        {
            "front" => (0d, 0d),
            "right" => (90d, 0d),
            "back" => (180d, 0d),
            "left" => (-90d, 0d),
            "top" => (0d, 89d),
            _ => (Views.ModelViewer.StartYaw, Views.ModelViewer.StartPitch),
        };
        (Zoom, PanX, PanY) = (1, 0, 0);
    }

    public void Dispose()
    {
        Pause();
        Mesh?.Dispose();
        Mesh = null;
    }
}
