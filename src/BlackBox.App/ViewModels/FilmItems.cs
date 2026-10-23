using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using BlackBox.Core.Film;
using CommunityToolkit.Mvvm.ComponentModel;

namespace BlackBox.App.ViewModels;

/// <summary>A piece of the film the storyboard plans and the film tool makes on its own: a frame, a shot or the song.</summary>
public interface IFilmPiece
{
    FilmPiece Piece { get; }

    /// <summary>"Shot 3", "Marco", "Start of shot 2", "The song".</summary>
    string Title { get; }

    /// <summary>"Not made yet", "Made · looked at", or how the chat's making of it is getting on ("KSampler, step 3 of 8").</summary>
    string State { get; set; }

    bool IsError { get; set; }
    bool IsWorking { get; set; }
    bool IsSelected { get; set; }
}

/// <summary>A picture of the film: a frame a shot starts or ends on, or a reference (a character, a place) others are made from.</summary>
public sealed partial class FrameItem(FilmFrame frame) : ObservableObject, IFilmPiece
{
    public FilmFrame Frame { get; } = frame;
    public string Id => Frame.Id;
    public FilmPiece Piece => FilmPiece.Of(Frame);

    /// <summary>"Start of shot 2", "End of shot 1, start of shot 2", or a reference's name.</summary>
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

/// <summary>A shot: a block on the timeline from where it starts for as long as it lasts, its clip's frames inside once made.</summary>
public sealed partial class ShotItem(FilmViewModel film, FilmShot shot) : ObservableObject, IFilmPiece
{
    public FilmShot Shot { get; } = shot;
    public string Id => Shot.Id;
    public FilmPiece Piece => FilmPiece.Of(Shot);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title), nameof(Number))]
    private int _index;

    public string Title => $"Shot {Index + 1}";
    public string Number => (Index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>What the camera does, as a name for the block ("static close-up"), else the start of what happens.</summary>
    public string Name => Shot.Camera.Trim() is { Length: > 0 } camera ? camera : Shot.Prompt.Trim() is { Length: > 0 } p ? p : "a new shot";

    /// <summary>What happens in it, as BlackBox asked for it.</summary>
    public string Summary => Shot.Prompt.Trim() is { Length: > 0 } p ? p : "Nothing said yet: pick it and say in the chat what happens in it.";

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

/// <summary>The film's soundtrack: none, a file of the user's, or a song made for it.</summary>
public sealed partial class SoundItem(FilmSound sound) : ObservableObject, IFilmPiece
{
    public FilmSound Sound { get; } = sound;
    public FilmPiece Piece => FilmPiece.Song;
    public string Title => Sound.Source == FilmSound.FromFile ? "The sound file" : "The song";

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
/// A file or folder of the film in the Filmmaking panel's tree: what is in the film's folder, and
/// what its storyboard plans that isn't made yet, where it will be. Picking one puts it in the
/// chat's box, for the user to say what to do with it.
/// </summary>
public sealed partial class FilmNode : ObservableObject
{
    public FilmNode(string path, bool isFolder, string? name = null)
    {
        Path = path;
        IsFolder = isFolder;
        Name = name ?? System.IO.Path.GetFileName(path.TrimEnd('\\', '/'));
        Kind = isFolder ? "folder" : Core.Media.MediaPreview.KindOf(path);
    }

    public string Path { get; }
    public bool IsFolder { get; }

    /// <summary>Its file or folder name.</summary>
    public string Name { get; }

    /// <summary>"folder", "image", "video", "audio", or another kind of file.</summary>
    public string Kind { get; }

    /// <summary>What it is in the film, beside its name: "start of shot 1", "shot 2 · 5 s", "your picture".</summary>
    [ObservableProperty]
    private string _label = "";

    /// <summary>The piece of the storyboard it is, made or not: its state shows beside it.</summary>
    public IFilmPiece? Piece { get; init; }

    /// <summary>An earlier version of a piece (<see cref="TakeOf"/>), kept when it was made again.</summary>
    public FilmTake? Take { get; init; }

    public IFilmPiece? TakeOf { get; init; }

    /// <summary>Planned in the storyboard and not made yet: no file there.</summary>
    public bool IsPlanned { get; init; }

    [ObservableProperty]
    private Bitmap? _thumbnail;

    [ObservableProperty]
    private bool _isExpanded;

    public ObservableCollection<FilmNode> Children { get; } = [];

    /// <summary>How the chat's box names it: "Shot 2 · s2.mp4", "References".</summary>
    public string Caption => Label.Length == 0 ? Name : $"{Capital(Label)} · {Name}";

    private static string Capital(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

    private static readonly Avalonia.Media.Geometry FolderShape = Avalonia.Media.StreamGeometry.Parse("M10 4H4c-1.1 0-1.99.9-1.99 2L2 18c0 1.1.9 2 2 2h16c1.1 0 2-.9 2-2V8c0-1.1-.9-2-2-2h-8l-2-2z");
    private static readonly Avalonia.Media.Geometry PictureShape = Avalonia.Media.StreamGeometry.Parse("M21 19V5c0-1.1-.9-2-2-2H5c-1.1 0-2 .9-2 2v14c0 1.1.9 2 2 2h14c1.1 0 2-.9 2-2zM8.5 13.5l2.5 3.01L14.5 12l4.5 6H5l3.5-4.5z");
    private static readonly Avalonia.Media.Geometry VideoShape = Avalonia.Media.StreamGeometry.Parse("M17 10.5V7c0-.55-.45-1-1-1H4c-.55 0-1 .45-1 1v10c0 .55.45 1 1 1h12c.55 0 1-.45 1-1v-3.5l4 4v-11l-4 4z");
    private static readonly Avalonia.Media.Geometry SoundShape = Avalonia.Media.StreamGeometry.Parse("M12 3v10.55c-.59-.34-1.27-.55-2-.55-2.21 0-4 1.79-4 4s1.79 4 4 4 4-1.79 4-4V7h4V3h-6z");
    private static readonly Avalonia.Media.Geometry FileShape = Avalonia.Media.StreamGeometry.Parse("M14 2H6c-1.1 0-1.99.9-1.99 2L4 20c0 1.1.89 2 1.99 2H18c1.1 0 2-.9 2-2V8l-6-6zm2 16H8v-2h8v2zm0-4H8v-2h8v2zm-3-5V3.5L18.5 9H13z");

    public Avalonia.Media.Geometry Icon => Kind switch
    {
        "folder" => FolderShape,
        "image" or "animation" => PictureShape,
        "video" => VideoShape,
        "audio" => SoundShape,
        _ => FileShape,
    };
}

/// <summary>A line said in a shot, where it is heard on the timeline.</summary>
public sealed record LineItem(ShotItem Shot, double X, double Width, string Text);

/// <summary>What the viewer shows for something with no picture to play: a folder, a piece not made yet, the film not joined.</summary>
public sealed record PictureView(Bitmap? Picture, string Caption, string? Missing);

/// <summary>A film in the list at the top of the panel: its title, and where it is.</summary>
public sealed record FilmChoice(string Folder, string Title)
{
    public override string ToString() => Title;
}
