using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using BlackBox.Core.Film;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BlackBox.App.ViewModels;

/// <summary>A picture of the film: a frame a shot starts or ends on, or a reference (a character, a place) others are made from.</summary>
public sealed partial class FrameItem(FilmViewModel film, FilmFrame frame) : ObservableObject
{
    public FilmFrame Frame { get; } = frame;
    public string Id => Frame.Id;

    /// <summary>"Start of shot 2", "End of shot 1, start of shot 2", or a reference's name.</summary>
    [ObservableProperty]
    private string _label = frame.Label;

    public string Prompt
    {
        get => Frame.Prompt;
        set
        {
            if (Frame.Prompt == value) return;
            Frame.Prompt = value;
            OnPropertyChanged();
            film.Changed(this);
        }
    }

    public string Name
    {
        get => Frame.Name ?? "";
        set
        {
            Frame.Name = value.Trim().Length == 0 ? null : value;
            OnPropertyChanged();
            film.Changed(this);
            film.Relabel();
        }
    }

    public decimal? Seed
    {
        get => Frame.Seed;
        set
        {
            if (value is not { } v || (long)v == Frame.Seed) return;
            Frame.Seed = Math.Max(1, (long)v);
            OnPropertyChanged();
            film.Changed(this);
        }
    }

    /// <summary>Whether the picture is one of the user's own instead of one made from the prompt.</summary>
    public bool UsesOwnPicture
    {
        get => Frame.File is not null;
        set
        {
            if (value == UsesOwnPicture) return;
            Frame.File = value ? "" : null;
            OnPropertyChanged();
            OnPropertyChanged(nameof(MakesPicture));
            OnPropertyChanged(nameof(OwnPicture));
            film.Changed(this);
        }
    }

    public bool MakesPicture
    {
        get => !UsesOwnPicture;
        set => UsesOwnPicture = !value;
    }

    public string OwnPicture => Frame.File ?? "";

    /// <summary>The user picked a picture of their own for this frame: it is taken at once, cut to the film's frame.</summary>
    public void TakeOwnPicture(string path)
    {
        Frame.File = path;
        OnPropertyChanged(nameof(UsesOwnPicture));
        OnPropertyChanged(nameof(MakesPicture));
        OnPropertyChanged(nameof(OwnPicture));
        film.Changed(this);
        Generate();
    }

    [ObservableProperty]
    private Bitmap? _thumbnail;

    [ObservableProperty]
    private Bitmap? _picture;

    /// <summary>"Not made yet", "Made", "Changed since it was made", or how a job is getting on.</summary>
    [ObservableProperty]
    private string _state = "";

    [ObservableProperty]
    private bool _isError;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MakeLabel))]
    private bool _isWorking;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MakeLabel))]
    private bool _isMade;

    [ObservableProperty]
    private bool _isSelected;

    public string MakeLabel => IsWorking ? "Stop" : UsesOwnPicture ? "Take it" : IsMade ? "Make again" : "Make it";

    /// <summary>The other frames this one can be made from, ticked when it is.</summary>
    public ObservableCollection<FromChoice> FromChoices { get; } = [];

    public bool HasFromChoices => FromChoices.Count > 0;

    public void RefreshChoices(IEnumerable<FrameItem> others)
    {
        FromChoices.Clear();
        foreach (var other in others.Where(o => o != this))
            FromChoices.Add(new FromChoice(other, Frame.From.Contains(other.Id, StringComparer.OrdinalIgnoreCase), ticked =>
            {
                Frame.From.RemoveAll(f => f.Equals(other.Id, StringComparison.OrdinalIgnoreCase));
                if (ticked)
                    Frame.From.Add(other.Id);
                film.Changed(this);
            }));
        OnPropertyChanged(nameof(HasFromChoices));
    }

    private CancellationTokenSource? _job;

    [RelayCommand]
    private void Generate()
    {
        if (IsWorking)
        {
            _job?.Cancel();
            return;
        }
        _job = new CancellationTokenSource();
        _ = film.RunFrameAsync(this, _job.Token);
    }

    [RelayCommand]
    private void NewSeed() => Seed = Storyboard.NewSeed();

    [RelayCommand]
    private void Select() => film.Select(this);

    [RelayCommand]
    private void Delete() => film.DeleteReference(this);

    public void Report(string state, bool error = false) => Dispatcher.UIThread.Post(() => (State, IsError) = (state, error));
}

/// <summary>Another frame, ticked when a frame is made from it.</summary>
public sealed partial class FromChoice(FrameItem frame, bool ticked, Action<bool> changed) : ObservableObject
{
    public FrameItem Frame { get; } = frame;

    [ObservableProperty]
    private bool _isChecked = ticked;

    partial void OnIsCheckedChanged(bool value) => changed(value);
}

/// <summary>A shot: from its first frame to its last in so many seconds, with what happens and what the camera does.</summary>
public sealed partial class ShotItem(FilmViewModel film, FilmShot shot) : ObservableObject
{
    public FilmShot Shot { get; } = shot;
    public string Id => Shot.Id;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title), nameof(IsFirst), nameof(StartsAfterText))]
    private int _index;

    public string Title => $"Shot {Index + 1}";
    public bool IsFirst => Index == 0;
    public string StartsAfterText => Index == 0 ? "" : $"the end of shot {Index}, the camera going on";

    [ObservableProperty]
    private FrameItem? _first;

    [ObservableProperty]
    private FrameItem? _last;

    public decimal? Seconds
    {
        get => (decimal)Shot.Seconds;
        set
        {
            if (value is not { } v || (double)v == Shot.Seconds) return;
            Shot.Seconds = Math.Clamp((double)v, 0.5, 60);
            OnPropertyChanged();
            OnPropertyChanged(nameof(LengthText));
            film.Changed(this);
        }
    }

    public string LengthText => $"{Shot.Seconds:0.#} s";

    public string Prompt
    {
        get => Shot.Prompt;
        set
        {
            if (Shot.Prompt == value) return;
            Shot.Prompt = value;
            OnPropertyChanged();
            film.Changed(this);
        }
    }

    public string Camera
    {
        get => Shot.Camera;
        set
        {
            if (Shot.Camera == value) return;
            Shot.Camera = value;
            OnPropertyChanged();
            film.Changed(this);
        }
    }

    public decimal? Seed
    {
        get => Shot.Seed;
        set
        {
            if (value is not { } v || (long)v == Shot.Seed) return;
            Shot.Seed = Math.Max(1, (long)v);
            OnPropertyChanged();
            film.Changed(this);
        }
    }

    public bool LipSync
    {
        get => Shot.LipSync;
        set
        {
            if (Shot.LipSync == value) return;
            Shot.LipSync = value;
            OnPropertyChanged();
            film.Changed(this);
        }
    }

    /// <summary>What is said in it, lip-synced in the film's voice; a line makes the shot lip-synced.</summary>
    public string Line
    {
        get => Shot.Line;
        set
        {
            if (Shot.Line == value) return;
            Shot.Line = value;
            if (value.Trim().Length > 0 && !Shot.LipSync)
            {
                Shot.LipSync = true;
                OnPropertyChanged(nameof(LipSync));
            }
            OnPropertyChanged();
            film.Changed(this);
        }
    }

    public static IReadOnlyList<string> Transitions { get; } = ["Cut", "Dissolve", "Fade through black"];

    public int TransitionIndex
    {
        get => Shot.Transition switch { FilmShot.Dissolve => 1, FilmShot.Fade => 2, _ => 0 };
        set
        {
            Shot.Transition = value switch { 1 => FilmShot.Dissolve, 2 => FilmShot.Fade, _ => FilmShot.Cut };
            OnPropertyChanged();
            OnPropertyChanged(nameof(JoinText));
            film.Changed(this);
        }
    }

    /// <summary>Whether it starts on the frame the shot before ends on: the same camera going on, rather than a cut.</summary>
    public bool Continues
    {
        get => film.ContinuesFromBefore(this);
        set => film.SetContinues(this, value);
    }

    public bool StartsNew
    {
        get => !Continues;
        set => Continues = !value;
    }

    public bool HasLastFrame
    {
        get => Shot.Last is not null;
        set => film.SetLastFrame(this, value);
    }

    /// <summary>What lies between it and the shot before, on the storyboard.</summary>
    public string JoinText => Index == 0 ? "" : Continues ? "goes on" : Shot.Transition switch { FilmShot.Dissolve => "dissolve", FilmShot.Fade => "fade", _ => "cut" };

    public void Refresh(params string[] properties)
    {
        foreach (var p in properties.Length > 0 ? properties : [nameof(Continues), nameof(StartsNew), nameof(HasLastFrame), nameof(JoinText), nameof(TransitionIndex)])
            OnPropertyChanged(p);
    }

    [ObservableProperty]
    private FilePreview? _clip;

    [ObservableProperty]
    private string _state = "";

    [ObservableProperty]
    private bool _isError;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MakeLabel))]
    private bool _isWorking;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MakeLabel))]
    private bool _isMade;

    [ObservableProperty]
    private bool _isSelected;

    public string MakeLabel => IsWorking ? "Stop" : IsMade ? "Make the clip again" : "Make the clip";

    private CancellationTokenSource? _job;

    [RelayCommand]
    private void MakeClip()
    {
        if (IsWorking)
        {
            _job?.Cancel();
            return;
        }
        _job = new CancellationTokenSource();
        _ = film.RunShotAsync(this, _job.Token);
    }

    [RelayCommand]
    private void NewSeed() => Seed = Storyboard.NewSeed();

    [RelayCommand]
    private void Select() => film.Select(this);

    [RelayCommand]
    private void SelectFirst()
    {
        if (First is { } f) film.Select(f);
    }

    [RelayCommand]
    private void SelectLast()
    {
        if (Last is { } l) film.Select(l);
    }

    [RelayCommand]
    private void MoveLeft() => film.Move(this, -1);

    [RelayCommand]
    private void MoveRight() => film.Move(this, 1);

    [RelayCommand]
    private void Delete() => film.DeleteShot(this);

    public void Report(string state, bool error = false) => Dispatcher.UIThread.Post(() => (State, IsError) = (state, error));
}

/// <summary>The film's soundtrack: none, a file of the user's, or a song made for it.</summary>
public sealed partial class SoundItem(FilmViewModel film, FilmSound sound) : ObservableObject
{
    public FilmSound Sound { get; } = sound;

    public bool IsNone
    {
        get => Sound.Source == FilmSound.None;
        set { if (value) SetSource(FilmSound.None); }
    }

    public bool IsFile
    {
        get => Sound.Source == FilmSound.FromFile;
        set { if (value) SetSource(FilmSound.FromFile); }
    }

    public bool IsSong
    {
        get => Sound.Source == FilmSound.Song;
        set { if (value) SetSource(FilmSound.Song); }
    }

    private void SetSource(string source)
    {
        if (Sound.Source == source) return;
        Sound.Source = source;
        OnPropertyChanged(nameof(IsNone));
        OnPropertyChanged(nameof(IsFile));
        OnPropertyChanged(nameof(IsSong));
        film.Changed(this);
    }

    public string File
    {
        get => Sound.File ?? "";
        set
        {
            Sound.File = value.Trim().Length == 0 ? null : value.Trim();
            OnPropertyChanged();
            film.Changed(this);
        }
    }

    public string Tags
    {
        get => Sound.Tags;
        set
        {
            if (Sound.Tags == value) return;
            Sound.Tags = value;
            OnPropertyChanged();
            film.Changed(this);
        }
    }

    public string Lyrics
    {
        get => Sound.Lyrics;
        set
        {
            if (Sound.Lyrics == value) return;
            Sound.Lyrics = value;
            OnPropertyChanged();
            film.Changed(this);
        }
    }

    /// <summary>Who says the shots' lines.</summary>
    public string Voice
    {
        get => Sound.Voice;
        set
        {
            if (Sound.Voice == value) return;
            Sound.Voice = value;
            OnPropertyChanged();
            film.Changed(this);
        }
    }

    public decimal? Seed
    {
        get => Sound.Seed;
        set
        {
            if (value is not { } v || (long)v == Sound.Seed) return;
            Sound.Seed = Math.Max(1, (long)v);
            OnPropertyChanged();
            film.Changed(this);
        }
    }

    [ObservableProperty]
    private FilePreview? _player;

    [ObservableProperty]
    private string _state = "";

    [ObservableProperty]
    private bool _isError;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MakeLabel))]
    private bool _isWorking;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MakeLabel))]
    private bool _isMade;

    public string MakeLabel => IsWorking ? "Stop" : IsMade ? "Make the song again" : "Make the song";

    /// <summary>The seed changed elsewhere (the best of several songs was kept).</summary>
    public void Refresh() => OnPropertyChanged(nameof(Seed));

    private CancellationTokenSource? _job;

    [RelayCommand]
    private void MakeSong()
    {
        if (IsWorking)
        {
            _job?.Cancel();
            return;
        }
        _job = new CancellationTokenSource();
        _ = film.RunSongAsync(this, _job.Token);
    }

    [RelayCommand]
    private void NewSeed() => Seed = Storyboard.NewSeed();

    public void Report(string state, bool error = false) => Dispatcher.UIThread.Post(() => (State, IsError) = (state, error));
}

/// <summary>Which ComfyUI template makes one kind of job: picked from what is installed, or one the user chose.</summary>
public sealed partial class WorkflowChoice : ObservableObject
{
    public const string Automatic = "Picked from what is installed";

    private readonly FilmViewModel _film;
    private readonly FilmWorkflows _workflows;

    public WorkflowChoice(FilmViewModel film, FilmWorkflows workflows, FilmJob job, string label)
    {
        (_film, _workflows, Job, Label) = (film, workflows, job, label);
        Options.Add(Automatic);
        if (FilmTemplates.Chosen(workflows, job) is { } chosen)
            Options.Add(chosen);
        _selected = FilmTemplates.Chosen(workflows, job) ?? Automatic;
    }

    public FilmJob Job { get; }
    public string Label { get; }
    public ObservableCollection<string> Options { get; } = [];

    [ObservableProperty]
    private string _selected;

    partial void OnSelectedChanged(string value)
    {
        if (value is null)
            return;
        FilmTemplates.Choose(_workflows, Job, value == Automatic ? null : value);
        _film.Changed(this);
    }

    /// <summary>The templates that can do the job, the ones known to do it well first.</summary>
    public void Offer(IEnumerable<string> names)
    {
        var keep = Selected;
        foreach (var name in names.Where(n => !Options.Contains(n)))
            Options.Add(name);
        Selected = keep;
    }
}

/// <summary>A shot on the film's strip: where it starts in the film and how long it lasts.</summary>
public sealed record StripShot(string Label, double Start, double Seconds, bool LipSync, bool Made);

/// <summary>What the strip under the shots shows (see <see cref="Views.FilmStrip"/>).</summary>
/// <param name="Peaks">The soundtrack's loudness from start to end, 0 to 1; null without one.</param>
/// <param name="SoundLabel">What to say where there is no waveform.</param>
public sealed record FilmStripModel(IReadOnlyList<StripShot> Shots, double Length, float[]? Peaks, double SoundSeconds, string? SoundLabel);

/// <summary>A film in the list at the top of the window: its title, and where it is.</summary>
public sealed record FilmChoice(string Folder, string Title)
{
    public override string ToString() => Title;
}

/// <summary>The joined film, shown in the details while selected.</summary>
public sealed partial class JoinedFilm(string path) : ObservableObject
{
    public string Path { get; } = path;

    [ObservableProperty]
    private FilePreview? _player;
}
