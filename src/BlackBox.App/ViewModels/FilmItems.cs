using Avalonia.Media.Imaging;
using BlackBox.Core.Film;
using CommunityToolkit.Mvvm.ComponentModel;

namespace BlackBox.App.ViewModels;

/// <summary>A piece of the film the storyboard plans and the film tool makes on its own: a frame, a take (a shot) or a sound.</summary>
public interface IFilmPiece
{
    FilmPiece Piece { get; }

    /// <summary>"Take 3", "Marco", "Start of take 2", "rain".</summary>
    string Title { get; }

    /// <summary>"Not made yet", "Made · looked at", or how the chat's making of it is getting on ("KSampler, step 3 of 8").</summary>
    string State { get; set; }

    bool IsError { get; set; }
    bool IsWorking { get; set; }
    bool IsSelected { get; set; }
}

/// <summary>A picture of the film: a frame a take starts or ends on, or a generated image (a reference) others are made from.</summary>
public sealed partial class FrameItem(FilmFrame frame) : ObservableObject, IFilmPiece
{
    public FilmFrame Frame { get; } = frame;
    public string Id => Frame.Id;
    public FilmPiece Piece => FilmPiece.Of(Frame);

    /// <summary>"Start of take 2", "End of take 1, start of take 2", or a reference's name.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title))]
    private string _label = frame.Label;

    public string Title => Label;

    [ObservableProperty]
    private Bitmap? _thumbnail;

    [ObservableProperty]
    private string _state = "";

    [ObservableProperty]
    private bool _isError;

    [ObservableProperty]
    private bool _isWorking;

    [ObservableProperty]
    private bool _isMade;

    [ObservableProperty]
    private bool _isSelected;
}

/// <summary>
/// A card on the References row: a generated image (a reference BlackBox makes, <see cref="Frame"/>)
/// or an image file of the user's copied into References. A new one not chosen yet offers File… or Generate.
/// </summary>
public sealed partial class ReferenceCard(string path, FrameItem? frame) : ObservableObject
{
    /// <summary>Its picture's place in References, made or not.</summary>
    public string Path { get; } = path;

    /// <summary>The generated image it is; null for an image file of the user's.</summary>
    public FrameItem? Frame { get; } = frame;

    public string Name => Frame?.Title ?? System.IO.Path.GetFileNameWithoutExtension(Path);

    /// <summary>"Generated image" or "Image file".</summary>
    public string Kind => Frame is null ? "Image file" : "Generated image";

    /// <summary>A generated image nothing is said of yet: File… or Generate decides what it is.</summary>
    public bool IsNew => Frame is { IsMade: false, Frame: { Prompt.Length: 0, File: null } };

    [ObservableProperty]
    private double _x;

    [ObservableProperty]
    private Bitmap? _thumbnail;

    [ObservableProperty]
    private string _state = "";

    [ObservableProperty]
    private bool _isSelected;

    /// <summary>A file dragged over it would go into it: it is marked.</summary>
    [ObservableProperty]
    private bool _isDropTarget;

    /// <summary>Its state, its kind and IsNew changed with the files: shown anew.</summary>
    public void Refresh()
    {
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(IsNew));
    }
}

/// <summary>A take (a shot): a block on the Video row from where it starts for as long as it lasts, its clip's frames inside once made.</summary>
public sealed partial class ShotItem(FilmViewModel film, FilmShot shot) : ObservableObject, IFilmPiece
{
    public FilmShot Shot { get; } = shot;
    public string Id => Shot.Id;
    public FilmPiece Piece => FilmPiece.Of(Shot);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title), nameof(Number))]
    private int _index;

    public string Title => $"Take {Index + 1}";
    public string Number => (Index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>What the camera does, as a name for the block ("static close-up"), else the start of what happens.</summary>
    public string Name => Shot.Camera.Trim() is { Length: > 0 } camera ? camera : Shot.Prompt.Trim() is { Length: > 0 } p ? p : "a new take";

    /// <summary>What happens in it, as BlackBox asked for it.</summary>
    public string Summary => Shot.Prompt.Trim() is { Length: > 0 } p ? p : "Nothing said yet: click it and say in the chat what happens in it.";

    public string LengthText => $"{Shot.Seconds:0.#} s";

    [ObservableProperty]
    private FrameItem? _first;

    [ObservableProperty]
    private FrameItem? _last;

    // ---- where it sits on the timeline ----------------------------------------------------------------

    [ObservableProperty]
    private double _x;

    [ObservableProperty]
    private double _blockWidth;

    /// <summary>Its clip as a row of frames, once made.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStrip))]
    private Bitmap? _strip;

    public bool HasStrip => Strip is not null;

    /// <summary>Its length changed by dragging its edge: the storyboard follows, and the timeline is laid out again.</summary>
    public void Stretch(double seconds)
    {
        seconds = Math.Clamp(Math.Round(seconds * 2) / 2, 0.5, 60);
        if (seconds == Shot.Seconds)
            return;
        Shot.Seconds = seconds;
        OnPropertyChanged(nameof(LengthText));
        film.Relayout();
        // A change waiting to be saved from the first step on: the storyboard the chat wrote
        // meanwhile is put together with it then, not read over it in the middle of the drag.
        film.Changed();
    }

    /// <summary>The drag ended: the new length is saved.</summary>
    public void Stretched() => film.Changed();

    [ObservableProperty]
    private string _state = "";

    [ObservableProperty]
    private bool _isError;

    [ObservableProperty]
    private bool _isWorking;

    [ObservableProperty]
    private bool _isMade;

    [ObservableProperty]
    private bool _isSelected;
}

/// <summary>
/// A sound on a sound track, as a clip in a video editor: from where it starts for as long as it
/// plays, dragged along and between tracks, trimmed at either edge. A sound file of the user's, or
/// a song BlackBox makes; a new one not chosen yet offers File… or Generate.
/// </summary>
public sealed partial class SoundItem(FilmSound sound) : ObservableObject, IFilmPiece
{
    public FilmSound Sound { get; } = sound;
    public string Id => Sound.Id;
    public FilmPiece Piece => FilmPiece.Of(Sound);
    public string Title => Sound.Label;

    /// <summary>"Sound file" or "Generated sound".</summary>
    public string Kind => Sound.Source == FilmSound.FromFile ? "Sound file" : "Generated sound";

    /// <summary>A song nothing is said of yet: File… or Generate decides what it is.</summary>
    public bool IsNew => Sound.Source == FilmSound.Song && !IsMade && Sound.Tags.Trim().Length == 0;

    /// <summary>How long its file is, in seconds; 0 while it isn't known.</summary>
    public double FileSeconds { get; set; }

    /// <summary>How long it plays: its own length, else what its file has after the trim, else (a song not made) to the film's end.</summary>
    public double PlaySeconds(double filmLength) =>
        Sound.Seconds > 0 ? Sound.Seconds : FileSeconds > 0 ? Math.Max(0.1, FileSeconds - Sound.Trim) : Math.Max(2, filmLength - Sound.Start);

    [ObservableProperty]
    private double _x;

    [ObservableProperty]
    private double _y;

    [ObservableProperty]
    private double _blockWidth;

    /// <summary>Its loudness over the part that plays, 0 to 1, ten a second.</summary>
    [ObservableProperty]
    private float[]? _peaks;

    [ObservableProperty]
    private string _state = "";

    [ObservableProperty]
    private bool _isError;

    [ObservableProperty]
    private bool _isWorking;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNew))]
    private bool _isMade;

    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    private bool _isDropTarget;

    public void Refresh()
    {
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Kind));
        OnPropertyChanged(nameof(IsNew));
    }
}

/// <summary>A sound track's name at the left of the timeline: "Sound 1".</summary>
public sealed record TrackItem(int Index)
{
    public string Name => $"Sound {Index + 1}";
}

/// <summary>
/// What is picked on the timeline and shown in the viewer: a card, a take, a frame of it, a sound,
/// or an earlier version of one; it goes into the chat's box, for the user to say what to do with it.
/// </summary>
public sealed class FilmPick(string path, string caption)
{
    public string Path { get; } = path;

    /// <summary>How the chat's box names it: "Take 2 · s2.mp4".</summary>
    public string Caption { get; } = caption;

    /// <summary>The piece of the storyboard it is, made or not.</summary>
    public IFilmPiece? Piece { get; init; }

    /// <summary>An earlier version of a piece (<see cref="VersionOf"/>), kept when it was made again.</summary>
    public FilmTake? Version { get; init; }

    public IFilmPiece? VersionOf { get; init; }

    /// <summary>Planned in the storyboard and not made yet: no file there.</summary>
    public bool IsPlanned { get; init; }
}

/// <summary>A line said in a take, where it is heard on the timeline.</summary>
public sealed record LineItem(ShotItem Shot, double X, double Width, string Text);

/// <summary>What the viewer shows for something with no picture to play: a piece not made yet, the film not joined.</summary>
public sealed record PictureView(Bitmap? Picture, string Caption, string? Missing);

/// <summary>An entry of a right-click menu in the panel: what it says, whether it can be done now, and what it does; with <see cref="Items"/>, a submenu.</summary>
public sealed record FilmMenuItem(string Header, bool Enabled, Action Do, string? Key = null)
{
    public IReadOnlyList<FilmMenuItem?>? Items { get; init; }
}
