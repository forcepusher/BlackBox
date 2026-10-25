using BlackBox.Core.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BlackBox.App.ViewModels;

/// <summary>
/// A 3D model in the File inspector panel (see <see cref="Views.ModelViewer"/>): a left drag turns it,
/// the wheel comes closer, a right or middle drag moves it about, and buttons show it from the front,
/// a side, the back or above. Where it is seen from is said under it and told to the model; with
/// Area on, an area of the view is marked to ask about (the user's request, 2026-10-26).
/// </summary>
public sealed partial class InspectorModelViewModel(string path) : ObservableObject, IDisposable
{
    public string Path { get; } = path;

    [ObservableProperty]
    private Mesh? _mesh;

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
        Mesh?.Dispose();
        Mesh = null;
    }
}
