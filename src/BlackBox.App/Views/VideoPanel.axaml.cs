using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using BlackBox.App.ViewModels;

namespace BlackBox.App.Views;

/// <summary>Video generator's panel, on the left of the chat window: a viewer, and the timeline where the video is worked on.</summary>
public partial class VideoPanel : UserControl
{
    public VideoPanel()
    {
        InitializeComponent();
        // A click or a drag on the seconds moves the playhead there.
        Ruler.Seeked += seconds => (DataContext as VideoViewModel)?.SeekStrip(seconds);
        // The timeline is dragged sideways: seen before the clips' buttons, which then don't click.
        Timeline.AddHandler(PointerPressedEvent, OnPanPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        Timeline.AddHandler(PointerMovedEvent, OnPanMoved, RoutingStrategies.Tunnel, handledEventsToo: true);
        Timeline.AddHandler(PointerReleasedEvent, OnPanReleased, RoutingStrategies.Tunnel, handledEventsToo: true);
        Timeline.PointerCaptureLost += (_, _) => EndPan();
        // Files dragged over a card, a sound or a row mark where they would go; the window takes the
        // drop itself (MainWindow.OnDrop) and hands it here.
        AddHandler(DragDrop.DragEnterEvent, OnDragOver);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DragLeaveEvent, OnDragLeave);
        AddHandler(DragDrop.DropEvent, (_, _) => Dispatcher.UIThread.Post(() => (DataContext as VideoViewModel)?.DragOver(null)), handledEventsToo: true);
    }

    private VideoViewModel? _vm;

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm is not null)
        {
            _vm.RenameAsked -= OnRenameAsked;
            _vm.RenameVideoAsked -= OnRenameVideoAsked;
            _vm.PickFiles = null;
        }
        _vm = DataContext as VideoViewModel;
        if (_vm is not null)
        {
            _vm.RenameAsked += OnRenameAsked;
            _vm.RenameVideoAsked += OnRenameVideoAsked;
            _vm.PickFiles = PickFilesAsync;
        }
    }

    private VideoViewModel Vm => (VideoViewModel)DataContext!;

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _viewer?.Close();
    }

    // ---- where on the timeline: a row and its time, a card or a sound -----------------------------------------

    /// <summary>The row (References, Video clips or the sound lines) a visual is in, by its Tag.</summary>
    private static Control? RowOf(Visual? visual) =>
        visual?.GetSelfAndVisualAncestors().OfType<Control>().FirstOrDefault(c => c.Tag is "references" or "video" or "sounds");

    /// <summary>Where on the timeline a pointer is: its row, the time there, the sound line, and the card or sound under it.</summary>
    private VideoDropPlace? PlaceAt(Visual? source, Func<Visual, Point> at)
    {
        if (RowOf(source) is not { } row)
            return null;
        var p = at(row);
        var card = source!.GetSelfAndVisualAncestors().OfType<StyledElement>().Select(v => v.DataContext).FirstOrDefault(d => d is ReferenceCard or SoundItem);
        return new VideoDropPlace((string)row.Tag!, Vm.SecondsAt(p.X), Math.Clamp((int)(p.Y / VideoViewModel.SoundLineHeight), 0, Math.Max(0, Vm.SoundLines.Count - 1)), card);
    }

    // ---- files dragged onto the timeline -------------------------------------------------------------------

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        if (DataContext is not VideoViewModel vm)
            return;
        var files = e.DataTransfer.TryGetFiles() is { } items && items.Select(f => f.TryGetLocalPath()).OfType<string>().Any();
        vm.DragOver(files ? PlaceAt(e.Source as Visual, v => e.GetPosition(v)) : null);
    }

    /// <summary>Out of the panel: nothing is marked. Moving from one card to another leaves the card, not the panel.</summary>
    private void OnDragLeave(object? sender, DragEventArgs e)
    {
        var at = e.GetPosition(this);
        if (at.X <= 1 || at.Y <= 1 || at.X >= Bounds.Width - 1 || at.Y >= Bounds.Height - 1)
            (DataContext as VideoViewModel)?.DragOver(null);
    }

    /// <summary>The Open dialog for pictures or sounds of the user's, to copy in.</summary>
    private async Task<IReadOnlyList<string>> PickFilesAsync(string kind)
    {
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage)
            return [];
        var extensions = kind == "image" ? Core.Video.Storyboard.PictureExtensions : Core.Video.Storyboard.SoundExtensions;
        var picked = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = kind == "image" ? "A picture to copy in" : "A sound to copy in",
            FileTypeFilter = [new FilePickerFileType(kind == "image" ? "Pictures" : "Sounds") { Patterns = extensions.Select(x => "*" + x).ToList() }],
        });
        return picked.Select(f => f.TryGetLocalPath()).OfType<string>().ToList();
    }

    // ---- the viewer ----------------------------------------------------------------------------------------

    /// <summary>A click around what the viewer shows (not on it) shows the whole video again, as Esc does: what is said next is about the video.</summary>
    private void OnAwayPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source is Visual source && (source == ViewerBox || ViewerBox.IsVisualAncestorOf(source)))
            return;
        if (!e.Handled && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            Vm.Clear();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled || TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is TextBox)
            return;
        if (e.Key == Key.Escape)
        {
            Vm.Clear();
            e.Handled = true;
        }
    }

    // ---- menus: the videos, a row, a card, a clip, a sound, a sound line ---------------------------------------------

    private void OnVideoMenu(object? sender, RoutedEventArgs e) => OpenMenu(Vm.VideoMenu(), VideoButton);

    /// <summary>Right-click on a row away from its cards: Create, there.</summary>
    private void OnRowMenu(object? sender, ContextRequestedEventArgs e)
    {
        if (e.Handled || PlaceAt(e.Source as Visual, v => e.TryGetPosition(v as Control, out var p) ? p : default) is not { } place)
            return;
        OpenMenu(Vm.MenuForRow(place.Row, place.Seconds, place.SoundLine), e.Source as Control);
        e.Handled = true;
    }

    private void OnCardMenu(object? sender, ContextRequestedEventArgs e)
    {
        if ((sender as Control)?.DataContext is ReferenceCard card)
        {
            OpenMenu(Vm.MenuFor(card), sender as Control);
            e.Handled = true;
        }
    }

    private void OnShotMenu(object? sender, ContextRequestedEventArgs e)
    {
        if ((sender as Control)?.DataContext is ShotItem shot)
        {
            OpenMenu(Vm.MenuFor(shot), sender as Control);
            e.Handled = true;
        }
    }

    private void OnSlotMenu(object? sender, ContextRequestedEventArgs e)
    {
        if ((sender as Control)?.DataContext is FrameSlot slot)
        {
            OpenMenu(Vm.MenuFor(slot), sender as Control);
            e.Handled = true;
        }
    }

    private void OnSoundMenu(object? sender, ContextRequestedEventArgs e)
    {
        if ((sender as Control)?.DataContext is SoundItem sound)
        {
            OpenMenu(Vm.MenuFor(sound), sender as Control);
            e.Handled = true;
        }
    }

    private void OnSoundLineMenu(object? sender, ContextRequestedEventArgs e)
    {
        if ((sender as Control)?.DataContext is SoundLineItem soundLine)
        {
            OpenMenu(Vm.MenuForRow("sounds", Vm.PlayPosition, soundLine.Index), sender as Control);
            e.Handled = true;
        }
    }

    private static void OpenMenu(IReadOnlyList<VideoMenuItem?> items, Control? at)
    {
        if (items.Count == 0)
            return;
        var menu = new ContextMenu();
        foreach (var item in Entries(items))
            menu.Items.Add(item);
        menu.Open(at);

        static IEnumerable<Control> Entries(IReadOnlyList<VideoMenuItem?> items)
        {
            foreach (var item in items)
            {
                if (item is null)
                {
                    yield return new Separator();
                    continue;
                }
                var entry = new MenuItem { Header = item.Header, IsEnabled = item.Enabled, InputGesture = item.Key is { } key ? KeyGesture.Parse(key) : null };
                if (item.Items is { } inner)
                    foreach (var sub in Entries(inner))
                        entry.Items.Add(sub);
                else
                {
                    var act = item.Do;
                    entry.Click += (_, _) => act();
                }
                yield return entry;
            }
        }
    }

    // ---- renaming: a box with the name, as Explorer's ----------------------------------------------------------

    private void OnRenameVideoAsked() => Dispatcher.UIThread.Post(() => AskName(VideoButton, Vm.VideoName, name => Vm.Rename(null, name)), DispatcherPriority.Background);

    private void OnRenameAsked(object target, string name)
    {
        var anchor = Timeline.GetVisualDescendants().OfType<Control>().FirstOrDefault(c => ReferenceEquals(c.DataContext, target) && c is Button or Border) ?? (Control)Timeline;
        Dispatcher.UIThread.Post(() => AskName(anchor, name, typed => Vm.Rename(target, typed)), DispatcherPriority.Background);
    }

    /// <summary>A box over <paramref name="anchor"/> with the name selected: Enter or a click elsewhere keeps what is typed, Esc doesn't.</summary>
    private static void AskName(Control anchor, string name, Action<string> done)
    {
        var box = new TextBox { Text = name, MinWidth = 220 };
        AutomationProperties.SetName(box, "New name");
        var flyout = new Flyout { Content = box, Placement = PlacementMode.Top };
        var kept = false;
        void Keep()
        {
            if (kept)
                return;
            kept = true;
            done(box.Text ?? "");
        }
        box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                Keep();
                flyout.Hide();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                kept = true;
                flyout.Hide();
                e.Handled = true;
            }
        };
        flyout.Closed += (_, _) => Keep();
        flyout.Opened += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            box.Focus();
            box.SelectAll();
        }, DispatcherPriority.Background);
        flyout.ShowAt(anchor);
    }

    // ---- clicks on the timeline -----------------------------------------------------------------------------

    private void OnCardClick(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is ReferenceCard card)
            Vm.Show(card);
    }

    private void OnCardFile(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is ReferenceCard card && Vm.MenuFor(card).FirstOrDefault() is { Enabled: true } file)
            file.Do();
        e.Handled = true;
    }

    private void OnCardGenerate(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is ReferenceCard card && Vm.MenuFor(card).ElementAtOrDefault(1) is { Enabled: true } generate)
            generate.Do();
        e.Handled = true;
    }

    private void OnSoundFile(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is SoundItem sound && Vm.MenuFor(sound).FirstOrDefault() is { Enabled: true } file)
            file.Do();
        e.Handled = true;
    }

    private void OnSoundGenerate(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is SoundItem sound && Vm.MenuFor(sound).ElementAtOrDefault(1) is { Enabled: true } generate)
            generate.Do();
        e.Handled = true;
    }

    private void OnCreateReference(object? sender, RoutedEventArgs e)
    {
        if (Vm.MenuForRow("references", 0, 0).FirstOrDefault() is { Enabled: true } create)
            create.Do();
    }

    private void OnAddSoundLine(object? sender, RoutedEventArgs e) => Vm.AddSoundLine();

    private void OnShotClick(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is ShotItem shot)
            Vm.Show(shot);
    }

    // ---- a clip's right edge dragged: its length ------------------------------------------------------------

    /// <summary>A clip's right edge being dragged: the clip, where the drag started and how long the clip was.</summary>
    private (ShotItem Shot, double StartX, double StartSeconds)? _stretch;

    private void OnHandlePressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control handle || handle.DataContext is not ShotItem shot)
            return;
        _stretch = (shot, e.GetPosition(Timeline).X, shot.Shot.Seconds);
        e.Pointer.Capture(handle);
        e.Handled = true;
        Vm.Show(shot);
    private void OnSlotClick(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is FrameSlot slot)
            Vm.Show(slot);
        e.Handled = true;
    }

    private void OnSlotFile(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is FrameSlot slot && Vm.MenuFor(slot).FirstOrDefault() is { Enabled: true } file)
            file.Do();
        e.Handled = true;
    }

    private void OnSlotGenerate(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is FrameSlot slot && Vm.MenuFor(slot).ElementAtOrDefault(1) is { Enabled: true } generate)
            generate.Do();
        e.Handled = true;
    }

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
    /// going, a later move over any clip's edge went on resizing the clip of the drag.
    /// </summary>
    private void OnHandleCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        if (_stretch is not { } s)
            return;
        _stretch = null;
        s.Shot.Stretched();
    }

    // ---- a sound dragged: moved along or between sound lines, or trimmed at an edge -------------------------------

    /// <summary>A sound being dragged: how ("move", "left", "right"), where from, and where it was.</summary>
    private (SoundItem Sound, string Mode, Point From, double Start, int SoundLine, double Trim, double Seconds, bool Moved)? _sound;

    private void OnSoundPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control clip || clip.DataContext is not SoundItem sound || !e.GetCurrentPoint(clip).Properties.IsLeftButtonPressed)
            return;
        var mode = (e.Source as Visual)?.GetSelfAndVisualAncestors().OfType<Border>().FirstOrDefault(b => b.Classes.Contains("handle"))?.Tag as string ?? "move";
        var a = sound.Sound;
        _sound = (sound, mode, e.GetPosition(SoundRows), a.Start, a.SoundLine, a.Trim, sound.PlaySeconds(Vm.Length), false);
        e.Pointer.Capture(clip);
        e.Handled = true;
    }

    private void OnSoundMoved(object? sender, PointerEventArgs e)
    {
        if (_sound is not { } s)
            return;
        var at = e.GetPosition(SoundRows);
        if (!s.Moved && Point.Distance(at, s.From) < 4)
            return;
        _sound = s with { Moved = true };
        var dx = (at.X - s.From.X) / Vm.Pps;
        var file = s.Sound.FileSeconds;
        switch (s.Mode)
        {
            case "left":
                // Its start cut: no further back than the file's start, and a tenth of a second kept.
                dx = Math.Clamp(dx, -Math.Min(s.Trim, s.Start), s.Seconds - 0.2);
                Vm.MoveSound(s.Sound, s.Start + dx, s.SoundLine, s.Trim + dx, s.Seconds - dx);
                break;
            case "right":
                var most = file > 0 ? file - s.Trim : double.MaxValue;
                Vm.MoveSound(s.Sound, s.Start, s.SoundLine, s.Trim, Math.Clamp(s.Seconds + dx, 0.2, most));
                break;
            default:
                var soundLine = s.SoundLine + (int)Math.Round((at.Y - s.From.Y) / VideoViewModel.SoundLineHeight);
                Vm.MoveSound(s.Sound, s.Start + dx, soundLine, s.Trim, s.Sound.Sound.Seconds);
                break;
        }
    }

    private void OnSoundReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_sound is not { } s)
            return;
        _sound = null;
        e.Pointer.Capture(null);
        // Let go where it was pressed: a click, which picks it.
        if (!s.Moved)
            Vm.Show(s.Sound);
    }

    private void OnSoundCaptureLost(object? sender, PointerCaptureLostEventArgs e) => _sound = null;

    // ---- dragging the timeline sideways --------------------------------------------------------------

    /// <summary>
    /// A press on the timeline: where, how far it was scrolled, whether it has moved enough to be a
    /// drag, and whether it was on a piece (a click there picks it; on empty space, the whole video).
    /// The user found its scrollbar very inconvenient: the timeline is dragged like a page instead.
    /// </summary>
    private (Point Start, double Offset, bool Dragging, bool OnPiece)? _pan;

    /// <summary>How far the pointer moves before a press is a drag, not a click.</summary>
    private const double PanSlop = 4;

    private void OnPanPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(Timeline).Properties.IsLeftButtonPressed || e.Source is not Visual source)
            return;
        // The seconds move the playhead, a clip's right edge its length, and a sound is dragged itself: no drag of the timeline there.
        if (source == Ruler || Ruler.IsVisualAncestorOf(source) ||
            source.GetSelfAndVisualAncestors().OfType<Border>().Any(b => b.Classes.Contains("handle") || b.Classes.Contains("clip")))
            return;
        var onPiece = source.GetSelfAndVisualAncestors().TakeWhile(v => v != Timeline).Any(v => v is Button);
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
            // Taken from the clip's button, which then doesn't click when let go.
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
        // A click on the timeline away from every piece: the whole video again.
        else if (!pan.OnPiece)
            Vm.Clear();
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
    /// the video's other pictures and clips one arrow key away. The clip here stops meanwhile.
    /// </summary>
    private void OnMediaClick(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not { } clicked || TopLevel.GetTopLevel(this) is not Window window)
            return;
        var shown = clicked is FilePreview preview ? preview.Path : Vm.ShownFile;
        var entries = Vm.MediaInVideo();
        var index = shown is null ? -1 : entries.FindIndex(m => string.Equals(m.Path, shown, StringComparison.OrdinalIgnoreCase));
        // A file the video's pieces don't hold (a version): on its own.
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
}
