using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using BlackBox.App.ViewModels;

namespace BlackBox.App.Views;

public partial class FilmWindow : Window
{
    public FilmWindow()
    {
        InitializeComponent();
        // A click or a drag on the seconds moves the playhead there; a click on the song's row picks it.
        Ruler.Seeked += seconds => (DataContext as FilmViewModel)?.SeekStrip(seconds);
        Wave.Picked += () => (DataContext as FilmViewModel)?.PickSongCommand.Execute(null);
    }

    private FilmViewModel Vm => (FilmViewModel)DataContext!;

    protected override void OnClosed(EventArgs e)
    {
        _viewer?.Close();
        base.OnClosed(e);
        // What it holds goes: players, the watch on the film's folder and on the chat. What the chat
        // is doing for the film goes on there.
        (DataContext as FilmViewModel)?.Dispose();
    }

    // ---- the timeline ----------------------------------------------------------------------------------

    /// <summary>
    /// A click away from every piece (empty timeline, around the viewer, the bar at the top) picks
    /// the whole film again, as Esc does, so what is said next is about the film. Clicks on a piece,
    /// a button or the ruler are handled before they get here; the panel on the right, which acts
    /// on what is picked, isn't watched.
    /// </summary>
    private void OnAwayPressed(object? sender, PointerPressedEventArgs e)
    {
        // On the picked piece's own view, which isn't all buttons: the place of a frame or shot not
        // made yet (its full-screen button is disabled, and the click falls through to here), the
        // song's card, a video's time. The pick stays; what is said next is about it.
        if (e.Source is Visual source && (source == ViewerBox || ViewerBox.IsVisualAncestorOf(source)))
            return;
        if (!e.Handled && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            Vm.Select(null);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        // Not while typing: Esc in the box to BlackBox would change what the words are about.
        if (!e.Handled && e.Key == Key.Escape && FocusManager?.GetFocusedElement() is not TextBox && !Vm.IsFilmPicked)
        {
            Vm.Select(null);
            e.Handled = true;
        }
    }

    private void OnShotClick(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is ShotItem shot)
            Vm.Select(shot);
    }

    private void OnRefClick(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is RefSegment segment)
            Vm.Select(segment.Frame);
    }

    private void OnRefNameClick(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is RefLane lane)
            Vm.Select(lane.Frame);
    }

    private void OnLineClick(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is LineItem line)
            Vm.Select(line.Shot);
    }

    /// <summary>A shot's right edge being dragged: the shot, where the drag started and how long the shot was.</summary>
    private (ShotItem Shot, double StartX, double StartSeconds)? _stretch;

    private void OnHandlePressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control handle || handle.DataContext is not ShotItem shot)
            return;
        _stretch = (shot, e.GetPosition(Timeline).X, shot.Shot.Seconds);
        e.Pointer.Capture(handle);
        e.Handled = true;
        Vm.Select(shot);
    }

    private void OnHandleMoved(object? sender, PointerEventArgs e)
    {
        if (_stretch is { } s)
            s.Shot.Stretch(s.StartSeconds + (e.GetPosition(Timeline).X - s.StartX) / Vm.Pps);
    }

    private void OnHandleReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_stretch is not { } s)
            return;
        _stretch = null;
        e.Pointer.Capture(null);
        s.Shot.Stretched();
    }

    /// <summary>
    /// The drag ended without a release (Alt+Tab, the timeline laid out anew): it ends here. Left
    /// going, a later move over any shot's edge went on resizing the shot of the drag.
    /// </summary>
    private void OnHandleCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        if (_stretch is not { } s)
            return;
        _stretch = null;
        s.Shot.Stretched();
    }

    /// <summary>The wheel scrolls the timeline sideways; with Ctrl it zooms.</summary>
    private void OnTimelineWheel(object? sender, PointerWheelEventArgs e)
    {
        if ((e.KeyModifiers & KeyModifiers.Control) != 0)
        {
            if (e.Delta.Y > 0) Vm.ZoomInCommand.Execute(null);
            else if (e.Delta.Y < 0) Vm.ZoomOutCommand.Execute(null);
        }
        else
        {
            var by = (Math.Abs(e.Delta.X) > Math.Abs(e.Delta.Y) ? e.Delta.X : e.Delta.Y) * 90;
            Timeline.Offset = new Vector(Math.Clamp(Timeline.Offset.X - by, 0, Math.Max(0, Timeline.Extent.Width - Timeline.Viewport.Width)), 0);
        }
        e.Handled = true;
    }

    // ---- the panel -------------------------------------------------------------------------------------

    private void OnPickFirstFrame(object? sender, RoutedEventArgs e)
    {
        if (Vm.PickedShot?.First is { } frame)
            Vm.Select(frame);
    }

    private void OnPickLastFrame(object? sender, RoutedEventArgs e)
    {
        if (Vm.PickedShot?.Last is { } frame)
            Vm.Select(frame);
    }

    /// <summary>Enter sends what was typed; Shift+Enter starts a new line.</summary>
    private void OnAskKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || (e.KeyModifiers & KeyModifiers.Shift) != 0)
            return;
        e.Handled = true;
        Vm.SendCommand.Execute(null);
    }

    // ---- the full-screen viewer ---------------------------------------------------------------------

    /// <summary>The full-screen viewer, while it is open.</summary>
    private MediaViewer? _viewer;

    /// <summary>
    /// A picture or video in the viewer, clicked: full screen on this window's screen, as the chat's
    /// are (the user's rule: a click on a picture or video always opens it full screen), with the
    /// film's other pictures and clips one arrow key away. The clip here stops meanwhile.
    /// </summary>
    private void OnMediaClick(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not { } clicked)
            return;
        var entries = Vm.MediaInFilm();
        var index = Vm.MediaIndex(entries, clicked);
        // A video the film's pieces don't hold (a music video picked as the soundtrack): on its own.
        if (index < 0 && clicked is FilePreview { Path: var path } preview && MainWindowViewModel.Viewable(path))
            (entries, index) = ([new MediaEntry(preview, path, null, preview.FileName)], 0);
        if (index < 0 || (Screens.ScreenFromWindow(this) ?? Screens.Primary) is not { } screen)
            return;
        if (clicked is PlayerPreview { IsPlaying: true } playing)
            playing.Stop();
        _viewer?.Close();
        var viewer = _viewer = new MediaViewer { DataContext = new MediaViewerViewModel(entries, index, Vm.Previews, screen.Bounds.Width) };
        viewer.Position = screen.Bounds.Position;
        viewer.Width = screen.Bounds.Width / screen.Scaling;
        viewer.Height = screen.Bounds.Height / screen.Scaling;
        viewer.Closed += (_, _) =>
        {
            if (ReferenceEquals(_viewer, viewer))
            {
                _viewer = null;
                Activate();
            }
        };
        viewer.Show();
    }

    private async void OnOpenClick(object? sender, RoutedEventArgs e)
    {
        var picked = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open a film's storyboard",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("Storyboard") { Patterns = ["storyboard.json"] }],
        });
        if (picked.FirstOrDefault()?.TryGetLocalPath() is { } path)
            Vm.Open(path);
    }

    private async void OnPickPicture(object? sender, RoutedEventArgs e)
    {
        var picked = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "A picture for this frame",
            AllowMultiple = false,
            FileTypeFilter = [FilePickerFileTypes.ImageAll],
        });
        if (picked.FirstOrDefault()?.TryGetLocalPath() is { } path)
            Vm.UseOwnPicture(path);
    }

    private async void OnPickSound(object? sender, RoutedEventArgs e)
    {
        var picked = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "A song or sound for the film",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("Sound") { Patterns = ["*.mp3", "*.wav", "*.flac", "*.ogg", "*.m4a", "*.aac", "*.opus"] }, FilePickerFileTypes.All],
        });
        if (picked.FirstOrDefault()?.TryGetLocalPath() is { } path)
            Vm.UseOwnSound(path);
    }
}
