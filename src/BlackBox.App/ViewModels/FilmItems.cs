using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using BlackBox.Core.Film;
using CommunityToolkit.Mvvm.ComponentModel;

namespace BlackBox.App.ViewModels;

/// <summary>A piece the user picks on the timeline and tells BlackBox about, in the chat: a frame, a shot or the song.</summary>
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
public sealed partial class FrameItem(FilmViewModel film, FilmFrame frame) : ObservableObject, IFilmPiece
{
    public FilmFrame Frame { get; } = frame;
    public string Id => Frame.Id;
    public FilmPiece Piece => FilmPiece.Of(Frame);

    /// <summary>"Start of shot 2", "End of shot 1, start of shot 2", or a reference's name.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title))]
    private string _label = frame.Label;

    public string Title => Label;

    /// <summary>What it shows, as BlackBox asked for it: for a tooltip, not to edit.</summary>
    public string Prompt => Frame.Prompt;

    [ObservableProperty]
    private Bitmap? _thumbnail;

    [ObservableProperty]
    private Bitmap? _picture;

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

    /// <summary>The user picked a picture of their own for this frame: it is the frame's file now, which making it cuts to the film's frame.</summary>
    public void TakeOwnPicture(string path)
    {
        Frame.File = path;
        film.Changed();
    }
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
    public string Summary => Shot.Prompt.Trim() is { Length: > 0 } p ? p : "Nothing said yet: pick it and say what happens in it.";

    public string LengthText => $"{Shot.Seconds:0.#} s";

    [ObservableProperty]
    private FrameItem? _first;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EndsOnFrame))]
    private FrameItem? _last;

    /// <summary>Whether it ends on a frame of its own: a lip-synced shot is made from its first frame alone.</summary>
    public bool EndsOnFrame => Last is not null && !Shot.LipSync;

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
    }

    /// <summary>The drag ended: the new length is saved.</summary>
    public void Stretched() => film.Changed();

    [ObservableProperty]
    private FilePreview? _clip;

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

    public void Refresh() =>
        OnPropertyChanged(string.Empty);
}

/// <summary>The film's soundtrack: none, a file of the user's, or a song made for it.</summary>
public sealed partial class SoundItem(FilmViewModel film, FilmSound sound) : ObservableObject, IFilmPiece
{
    public FilmSound Sound { get; } = sound;
    public FilmPiece Piece => FilmPiece.Song;
    public string Title => Sound.Source == FilmSound.FromFile ? "The sound file" : "The song";

    /// <summary>The user picked a sound file of their own as the soundtrack.</summary>
    public void TakeOwnSound(string path)
    {
        Sound.Source = FilmSound.FromFile;
        Sound.File = path;
        OnPropertyChanged(string.Empty);
        film.Changed();
    }

    [ObservableProperty]
    private FilePreview? _player;

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

/// <summary>A reference's row on the timeline: its picture and name, and a bar under each run of shots made from it.</summary>
public sealed class RefLane(FrameItem frame)
{
    public FrameItem Frame { get; } = frame;
    public ObservableCollection<RefSegment> Segments { get; } = [];
}

/// <summary>A run of shots a reference is in, on its row of the timeline.</summary>
public sealed record RefSegment(FrameItem Frame, double X, double Width, string Text);

/// <summary>A line said in a shot, where it is heard on the timeline.</summary>
public sealed record LineItem(ShotItem Shot, double X, double Width, string Text);

/// <summary>A take of the piece picked: one kept from before, or the one in the film.</summary>
public sealed record TakeItem(FilmTake? Take, string Label, Bitmap? Thumbnail, bool InFilm);

/// <summary>A picture the viewer shows: a frame, or what stands in for a piece not made yet.</summary>
public sealed record PictureView(Bitmap? Picture, string Caption, string? Missing);

/// <summary>A film in the list at the top of the window: its title, and where it is.</summary>
public sealed record FilmChoice(string Folder, string Title)
{
    public override string ToString() => Title;
}

/// <summary>The joined film, in the viewer while picked.</summary>
public sealed partial class JoinedFilm(string path) : ObservableObject
{
    public string Path { get; } = path;

    [ObservableProperty]
    private FilePreview? _player;
}
