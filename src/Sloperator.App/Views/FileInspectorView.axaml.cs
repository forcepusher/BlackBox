using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Sloperator.App.ViewModels;
using Sloperator.Core.Documents;
using RawImage = Sloperator.Core.Platform.RawImage;

namespace Sloperator.App.Views;

/// <summary>
/// A file shown whole, in the File inspector panel on the left of the chat window or in the full-screen
/// viewer: text selected in it, or an area dragged over it, goes into the chat's box (in the panel;
/// the viewer's file has no chat). Pictures and frames zoom in their <see cref="ZoomBox"/>; a PDF's
/// pages zoom here, with Ctrl+wheel (the wheel scrolls them) or the buttons, and pan with a right or
/// middle drag as a picture does.
/// </summary>
public partial class FileInspectorView : UserControl
{
    public FileInspectorView()
    {
        InitializeComponent();
        // Windows' previewer couldn't show it: the file's text instead.
        Shell.Failed += why => (DataContext as FileInspectorViewModel)?.ShellFailed(why);
        // The text takes the presses and releases for selecting: these see them anyway.
        AddHandler(PointerPressedEvent, OnPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(PointerReleasedEvent, OnReleased, handledEventsToo: true);
        AddHandler(KeyUpEvent, OnKeyUp, handledEventsToo: true);
        PictureBox.Marked += area => _vm?.PickArea(area);
        FrameBox.Marked += area => _vm?.PickArea(area);
        AnimationBox.Marked += area => _vm?.PickArea(area);
        FrozenBox.Marked += area => _vm?.PickArea(area);
        // A PDF's pages: Ctrl+wheel zooms, a right or middle drag pans, a left drag on a page marks an area.
        PdfScroll.AddHandler(PointerWheelChangedEvent, OnPdfWheel, RoutingStrategies.Tunnel);
        PdfScroll.AddHandler(PointerPressedEvent, OnPdfPressed, RoutingStrategies.Tunnel);
        PdfScroll.AddHandler(PointerMovedEvent, OnPdfMoved, RoutingStrategies.Tunnel);
        PdfScroll.AddHandler(PointerReleasedEvent, OnPdfReleased, RoutingStrategies.Tunnel);
        PdfScroll.GetObservable(ScrollViewer.ViewportProperty).Subscribe(new Watch<Size>(_ => SizePages()));
        // What the file was made with takes at most half the view: the file stays the larger part.
        SizeChanged += (_, e) => RecipeBox.MaxHeight = Math.Max(120, Math.Floor(e.NewSize.Height / 2));
    }

    private FileInspectorViewModel? _vm;

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm is not null)
        {
            _vm.Unpicked -= ClearSelection;
            _vm.PropertyChanged -= OnVmChanged;
        }
        _vm = DataContext as FileInspectorViewModel;
        if (_vm is not null)
        {
            _vm.Unpicked += ClearSelection;
            _vm.PropertyChanged += OnVmChanged;
            PagesList.Cursor = _vm.CanPick ? new Cursor(StandardCursorType.Cross) : null;
            _pdfZoom = _vm.Zoom;
        }
    }

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_vm is null)
            return;
        if (e.PropertyName == nameof(FileInspectorViewModel.Zoom) && _vm.Pages is not null && _vm.Frozen is null)
            ZoomPages();
        else if (e.PropertyName == nameof(FileInspectorViewModel.Pages))
        {
            _pdfZoom = 1;
            SizePages();
        }
        else if (e.PropertyName == nameof(FileInspectorViewModel.AreaMode) && _vm.AreaMode && _vm.Frozen is null)
            Freeze();
    }

    // ---- Area: the view frozen -------------------------------------------------------------------

    /// <summary>
    /// Area turned on: the view as it is now, frozen to drag an area over. It is read off the screen,
    /// as Windows' previewer draws from another process; a text once its selection is cleared off it
    /// (a frame later).
    /// </summary>
    private void Freeze()
    {
        if (_vm is null)
            return;
        _vm.StopPlaying();
        if (_vm.ShowsShell)
        {
            Frozen(Shell.Snapshot());
            return;
        }
        ClearSelection();
        DispatcherTimer.RunOnce(() =>
        {
            if (_vm is not { AreaMode: true, Frozen: null })
                return;
            Control? shown = _vm.Markdown is not null ? DocumentScroll : _vm.Text is not null ? TextScroll : _vm.Model is not null ? ModelView
                : _vm.Pages is not null ? PdfScroll : _vm.Video is not null ? FrameBox : _vm.Animation is not null ? AnimationBox
                : _vm.Sound is not null ? Waveform : _vm.Picture is not null ? PictureBox : null;
            Frozen(shown is null ? null : OnScreen(shown));
        }, TimeSpan.FromMilliseconds(80));
    }

    private void Frozen(RawImage? pixels)
    {
        if (_vm is null)
            return;
        if (pixels is null)
            _vm.AreaMode = false;
        else
            _vm.Freeze(pixels);
    }

    /// <summary>What <paramref name="control"/> shows on screen, its pixels; null where that can't be read.</summary>
    private static RawImage? OnScreen(Control control)
    {
#if WINDOWS
        if (!OperatingSystem.IsWindows() || TopLevel.GetTopLevel(control) is not { } top || top.TryGetPlatformHandle()?.Handle is not { } hwnd)
            return null;
        var corner = control.PointToScreen(new Point(0, 0));
        var far = control.PointToScreen(new Point(control.Bounds.Width, control.Bounds.Height));
        return Platform.Windows.WindowPixels.In(hwnd, corner.X, corner.Y, far.X - corner.X, far.Y - corner.Y);
#else
        return null;
#endif
    }
    // ---- a PDF's pages: zoom, pan, mark -----------------------------------------------------------

    /// <summary>The zoom the pages are laid out at, so a new one keeps the point it zooms about where it is.</summary>
    private double _pdfZoom = 1;

    /// <summary>Where in the scroller Ctrl+wheel zoomed about; null for the buttons, which zoom about its middle.</summary>
    private Point? _zoomAt;

    /// <summary>The list of pages as wide as the panel, times the zoom: the pages, as wide as it lets them be, follow.</summary>
    private void SizePages()
    {
        if (_vm?.Pages is null)
            return;
        var margin = PagesList.Margin.Left + PagesList.Margin.Right;
        PagesList.Width = Math.Max(0, PdfScroll.Viewport.Width - margin) * (_vm.Frozen is null ? _vm.Zoom : 1);
    }

    private void ZoomPages()
    {
        if (_vm is null)
            return;
        var at = _zoomAt ?? new Point(PdfScroll.Viewport.Width / 2, PdfScroll.Viewport.Height / 2);
        _zoomAt = null;
        var ratio = _vm.Zoom / _pdfZoom;
        _pdfZoom = _vm.Zoom;
        var point = new Vector(PdfScroll.Offset.X + at.X, PdfScroll.Offset.Y + at.Y);
        SizePages();
        PdfScroll.UpdateLayout();
        PdfScroll.Offset = new Vector(Math.Max(0, point.X * ratio - at.X), Math.Max(0, point.Y * ratio - at.Y));
    }

    private void OnPdfWheel(object? sender, PointerWheelEventArgs e)
    {
        if (_vm is null || (e.KeyModifiers & KeyModifiers.Control) == 0)
            return;
        _zoomAt = e.GetPosition(PdfScroll);
        var zoom = Math.Clamp(_vm.Zoom * Math.Pow(ZoomBox.Step, e.Delta.Y), 1, _vm.MaxZoom);
        if (Math.Abs(zoom - _vm.Zoom) < 0.0001)
            _zoomAt = null;
        _vm.Zoom = zoom;
        e.Handled = true;
    }

    private Point? _panFrom;

    /// <summary>The page an area is being dragged on, and where the drag started on it (in parts of it).</summary>
    private (PdfPageView View, PdfPageItem Page, Point From)? _marking;

    private void OnPdfPressed(object? sender, PointerPressedEventArgs e)
    {
        var point = e.GetCurrentPoint(PdfScroll);
        if (point.Properties.IsRightButtonPressed || point.Properties.IsMiddleButtonPressed)
        {
            _panFrom = point.Position;
            PdfScroll.Cursor = new Cursor(StandardCursorType.SizeAll);
            e.Pointer.Capture(PdfScroll);
            e.Handled = true;
            return;
        }
        if (!point.Properties.IsLeftButtonPressed || _vm is not { CanPick: true } || OnScrollBar(e)
            || (e.Source as Visual)?.FindAncestorOfType<PdfPageView>(includeSelf: true) is not { DataContext: PdfPageItem page } view)
            return;
        var at = e.GetPosition(view);
        _marking = (view, page, new Point(at.X / view.Bounds.Width, at.Y / view.Bounds.Height));
        e.Pointer.Capture(PdfScroll);
        e.Handled = true;
    }

    private void OnPdfMoved(object? sender, PointerEventArgs e)
    {
        if (_panFrom is { } from)
        {
            var at = e.GetPosition(PdfScroll);
            PdfScroll.Offset -= new Vector(at.X - from.X, at.Y - from.Y);
            _panFrom = at;
        }
        else if (_marking is { } marking && _vm is not null)
            _vm.Mark = MarkedOn(marking, e);
    }

    private void OnPdfReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_panFrom is not null)
        {
            _panFrom = null;
            PdfScroll.Cursor = null;
            e.Pointer.Capture(null);
            e.Handled = true;
            return;
        }
        if (_marking is not { } marking || _vm is null)
            return;
        _marking = null;
        e.Pointer.Capture(null);
        e.Handled = true;
        var area = MarkedOn(marking, e);
        var bounds = marking.View.Bounds;
        // A few DIPs or less is a click: it marks nothing, and takes off what was marked.
        if (area is not null && (area.Right - area.Left) * bounds.Width < 4 && (area.Bottom - area.Top) * bounds.Height < 4)
            area = null;
        _vm.PickArea(area);
    }

    private static DocumentArea? MarkedOn((PdfPageView View, PdfPageItem Page, Point From) marking, PointerEventArgs e)
    {
        var at = e.GetPosition(marking.View);
        var size = marking.View.Bounds.Size;
        return DocumentArea.Between(marking.From.X, marking.From.Y, at.X / size.Width, at.Y / size.Height, marking.Page.Index);
    }

    // ---- text selected --------------------------------------------------------------------------

    /// <summary>
    /// A press starts a selection in one block of the text: those in the others go, as the text keeps
    /// a selection that loses focus (so it stays marked while the user types about it).
    /// </summary>
    private void OnPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed || OnScrollBar(e))
            return;
        var pressed = (e.Source as Visual)?.FindAncestorOfType<SelectableTextBlock>(includeSelf: true);
        Document.ClearSelection(pressed);
        if (!ReferenceEquals(pressed, PlainText))
            PlainText.ClearSelection();
    }

    /// <summary>
    /// A drag over the text, a double-click on a word or a Shift+click: what is selected goes into the
    /// box. A click that selects nothing (on the text, beside it, on the bar at the top) takes it out,
    /// as Esc does; a click in the chat keeps it, as the box is clicked to ask about it. Areas are
    /// marked by their own drags (a page's, a picture's), which say when nothing was marked.
    /// </summary>
    private void OnReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton == MouseButton.Left && !OnScrollBar(e) && _vm is { Frozen: null } && (_vm.Markdown is not null || _vm.Text is not null))
            Picked();
    }

    /// <summary>A scroll bar dragged or clicked to see more of the file keeps the selection.</summary>
    private static bool OnScrollBar(RoutedEventArgs e) =>
        (e.Source as Visual)?.FindAncestorOfType<Avalonia.Controls.Primitives.ScrollBar>(includeSelf: true) is not null;

    /// <summary>Shift+arrows and Ctrl+A select too; Esc lets go (and turns Area off).</summary>
    private void OnKeyUp(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            _vm?.Unpick();
            if (_vm is not null)
                _vm.AreaMode = false;
            ClearSelection();
        }
        else if ((e.KeyModifiers & (KeyModifiers.Shift | KeyModifiers.Control)) != 0 && _vm is { Frozen: null } && (_vm.Markdown is not null || _vm.Text is not null))
            Picked();
    }

    /// <summary>
    /// A video, an animation or a 3D model's animation: Left and Right step a frame, Space plays and pauses. A sound: Left and
    /// Right move the playhead a hundredth of the view (with Shift a tenth), Home and End to its ends.
    /// </summary>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled || _vm is null)
            return;
        if (_vm.Sound is { } sound && (e.KeyModifiers & ~KeyModifiers.Shift) == KeyModifiers.None)
        {
            var step = (e.KeyModifiers & KeyModifiers.Shift) != 0 ? 0.1 : 0.01;
            switch (e.Key)
            {
                case Key.Left: sound.Nudge(-step); break;
                case Key.Right: sound.Nudge(step); break;
                case Key.Home: sound.Seek(0); break;
                case Key.End: sound.Seek(sound.Length); break;
                case Key.Space: sound.PlayCommand.Execute(null); break;
                default: return;
            }
            e.Handled = true;
            return;
        }
        if (e.KeyModifiers != KeyModifiers.None)
            return;
        Action<int>? step1 = _vm.Video is { } video ? video.Step : _vm.Animation is { } animation ? animation.Step
            : _vm.Model is { HasAnimations: true } model ? model.Step : null;
        System.Windows.Input.ICommand? play = _vm.Video?.PlayCommand ?? (System.Windows.Input.ICommand?)_vm.Animation?.PlayCommand
            ?? (_vm.Model is { HasAnimations: true } ? _vm.Model.PlayCommand : null);
        if (step1 is null || play is null)
            return;
        switch (e.Key)
        {
            case Key.Left:
                step1(-1);
                break;
            case Key.Right:
                step1(1);
                break;
            case Key.Space:
                play.Execute(null);
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    private void Picked()
    {
        if (_vm is null)
            return;
        if (_vm.Markdown is not null && Document.Selection is { } selected)
            _vm.SelectedInMarkdown(selected.Block, selected.Start, selected.End);
        else if (_vm.Text is not null && PlainText.SelectionStart != PlainText.SelectionEnd)
            _vm.SelectedInText(Math.Min(PlainText.SelectionStart, PlainText.SelectionEnd), Math.Max(PlainText.SelectionStart, PlainText.SelectionEnd));
        else
            _vm.Unpick();
    }

    private void ClearSelection()
    {
        Document.ClearSelection();
        PlainText.ClearSelection();
    }

    /// <summary>An observer of a value, for <see cref="AvaloniaObject.GetObservable{T}(AvaloniaProperty{T})"/> without Rx.</summary>
    private sealed class Watch<T>(Action<T> next) : IObserver<T>
    {
        public void OnNext(T value) => next(value);
        public void OnCompleted() { }
        public void OnError(Exception error) { }
    }
}
