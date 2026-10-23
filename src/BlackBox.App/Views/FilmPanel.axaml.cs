using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using BlackBox.App.ViewModels;

namespace BlackBox.App.Views;

/// <summary>Filmmaking's panel, on the left of the chat window: the film's folder as a tree, a viewer, and the timeline.</summary>
public partial class FilmPanel : UserControl
{
    public FilmPanel()
    {
        InitializeComponent();
        // A click or a drag on the seconds moves the playhead there; a click on the song's row shows it.
        Ruler.Seeked += seconds => (DataContext as FilmViewModel)?.SeekStrip(seconds);
        Wave.Picked += () => (DataContext as FilmViewModel)?.Show((DataContext as FilmViewModel)?.Sound);
        // The timeline is dragged sideways: seen before the shots' buttons, which then don't click.
        Timeline.AddHandler(PointerPressedEvent, OnPanPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        Timeline.AddHandler(PointerMovedEvent, OnPanMoved, RoutingStrategies.Tunnel, handledEventsToo: true);
        Timeline.AddHandler(PointerReleasedEvent, OnPanReleased, RoutingStrategies.Tunnel, handledEventsToo: true);
        Timeline.PointerCaptureLost += (_, _) => EndPan();
    }

    private FilmViewModel Vm => (FilmViewModel)DataContext!;

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _viewer?.Close();
    }

    // ---- the timeline ----------------------------------------------------------------------------------

    /// <summary>
    /// A click away from every piece (empty timeline, around the viewer) shows the whole film again,
    /// as Esc does, and nothing is picked: what is said next is about the film. Clicks on a piece, a
    /// button or the ruler are handled before they get here.
    /// </summary>
    private void OnAwayPressed(object? sender, PointerPressedEventArgs e)
    {
        // On what the viewer shows, which isn't all buttons (a card, a video's time): it stays. On
        // the timeline it is a click only once let go without dragging it (see OnPanReleased).
        if (e.Source is Visual source && (source == ViewerBox || ViewerBox.IsVisualAncestorOf(source) || source == Timeline || Timeline.IsVisualAncestorOf(source)))
            return;
        if (!e.Handled && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            Vm.Clear(viewer: true);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (!e.Handled && e.Key == Key.Escape && TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is not TextBox)
        {
            Vm.Clear(viewer: true);
            e.Handled = true;
        }
    }

    private void OnShotClick(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is ShotItem shot)
            Vm.Show(shot);
    }

    private void OnLineClick(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is LineItem line)
            Vm.Show(line.Shot);
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
        Vm.Show(shot);
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

    // ---- dragging the timeline sideways --------------------------------------------------------------

    /// <summary>
    /// A press on the timeline: where, how far it was scrolled, whether it has moved enough to be a
    /// drag, and whether it was on a piece (a click there picks it; on empty space, the whole film).
    /// The user found its scrollbar very inconvenient: the timeline is dragged like a page instead.
    /// </summary>
    private (Point Start, double Offset, bool Dragging, bool OnPiece)? _pan;

    /// <summary>How far the pointer moves before a press is a drag, not a click.</summary>
    private const double PanSlop = 4;

    private void OnPanPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(Timeline).Properties.IsLeftButtonPressed || e.Source is not Visual source)
            return;
        // The seconds move the playhead, and a shot's right edge its length: no drag of the timeline there.
        if (source == Ruler || Ruler.IsVisualAncestorOf(source) || source.GetSelfAndVisualAncestors().OfType<Border>().Any(b => b.Classes.Contains("handle")))
            return;
        var onPiece = source.GetSelfAndVisualAncestors().TakeWhile(v => v != Timeline).Any(v => v is Button || v == Wave);
        _pan = (e.GetPosition(this), Timeline.Offset.X, false, onPiece);
    }

    private void OnPanMoved(object? sender, PointerEventArgs e)
    {
        if (_pan is not { } pan)
            return;
        var dx = e.GetPosition(this).X - pan.Start.X;
        if (!pan.Dragging)
        {
            if (Math.Abs(dx) < PanSlop)
                return;
            // Taken from the shot's button, which then doesn't click when let go.
            _pan = pan with { Dragging = true };
            e.Pointer.Capture(Timeline);
            Timeline.Cursor = new Cursor(StandardCursorType.SizeWestEast);
        }
        Timeline.Offset = new Vector(Math.Clamp(pan.Offset - dx, 0, Math.Max(0, Timeline.Extent.Width - Timeline.Viewport.Width)), 0);
        e.Handled = true;
    }

    private void OnPanReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_pan is not { } pan)
            return;
        EndPan();
        if (pan.Dragging)
        {
            e.Pointer.Capture(null);
            e.Handled = true;
        }
        // A click on the timeline away from every piece: the whole film again.
        else if (!pan.OnPiece)
            Vm.Clear(viewer: true);
    }

    private void EndPan()
    {
        _pan = null;
        Timeline.Cursor = null;
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

    // ---- the full-screen viewer ---------------------------------------------------------------------

    /// <summary>The full-screen viewer, while it is open.</summary>
    private MediaViewer? _viewer;

    /// <summary>
    /// The picture or video in the viewer, clicked: full screen on this window's screen, as the
    /// chat's are (the user's rule: a click on a picture or video always opens it full screen), with
    /// the film's other pictures and clips one arrow key away. The clip here stops meanwhile.
    /// </summary>
    private void OnMediaClick(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not { } clicked || TopLevel.GetTopLevel(this) is not Window window)
            return;
        var shown = clicked is FilePreview preview ? preview.Path : Vm.ShownFile;
        var entries = Vm.MediaInFilm();
        var index = shown is null ? -1 : entries.FindIndex(m => string.Equals(m.Path, shown, StringComparison.OrdinalIgnoreCase));
        // A file the film's pieces don't hold (a take, a music video picked as the soundtrack): on its own.
        if (index < 0 && shown is not null && MainWindowViewModel.Viewable(shown))
            (entries, index) = ([new MediaEntry(clicked, shown, null, Path.GetFileName(shown))], 0);
        if (index < 0 || (window.Screens.ScreenFromWindow(window) ?? window.Screens.Primary) is not { } screen)
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
                window.Activate();
            }
        };
        viewer.Show();
    }

    /// <summary>Opens a film from another folder.</summary>
    private async void OnOpenClick(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage)
            return;
        var picked = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Open a film's folder", AllowMultiple = false });
        if (picked.FirstOrDefault()?.TryGetLocalPath() is { } path)
            Vm.Open(path);
    }
}
