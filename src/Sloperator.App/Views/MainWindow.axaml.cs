using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Sloperator.App.ViewModels;
using Sloperator.Core.Settings;

namespace Sloperator.App.Views;

public partial class MainWindow : Window
{
    private bool _stickToBottom = true;
    private bool _pinning;

    public MainWindow()
    {
        InitializeComponent();

        // Enter sends, Shift+Enter inserts a newline. Tunnel so we see the key before the TextBox.
        InputBox.AddHandler(KeyDownEvent, OnInputKeyDown, RoutingStrategies.Tunnel);

        // Files and pictures go with a message: dropped anywhere on the window, or pasted into the box.
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragEnterEvent, OnDragOver);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DragLeaveEvent, OnDragLeave);
        AddHandler(DragDrop.DropEvent, OnDrop);
        InputBox.AddHandler(TextBox.PastingFromClipboardEvent, OnPasting);

        // The context size box takes what was typed when focus leaves it: Enter does that, Escape
        // puts the size back first. Afterwards it shows the size in use in the usual form ("65536"
        // becomes "64k"), or the old one if the text wasn't a size; the binding doesn't refresh
        // a value that didn't change, so that is set here.
        ContextBox.LostFocus += (_, _) => Dispatcher.UIThread.Post(() => ContextBox.Text = Vm.ContextInput);
        ContextBox.KeyDown += (_, e) =>
        {
            if (e.Key is not (Key.Enter or Key.Escape))
                return;
            if (e.Key == Key.Escape)
                ContextBox.Text = Vm.ContextInput;
            e.Handled = true;
            InputBox.Focus();
        };

        // Opening the model dropdown asks the server again, for a model loaded there meanwhile.
        ModelBox.DropDownOpened += (_, _) => Vm.RecheckModels();

        // Follow new output while the user is at the bottom; stop following when they scroll up.
        // After every layout pass (streamed text, a growing thinking block, a new screenshot)
        // pin the offset to the true end. Doing it per layout pass rather than per scroll event
        // is what keeps the last line from being clipped while text is still arriving.
        ChatScroll.LayoutUpdated += (_, _) => PinToBottom();
        ChatScroll.TemplateApplied += (_, e) =>
        {
            _chatPresenter = e.NameScope.Find<ScrollContentPresenter>("PART_ContentPresenter");
            FitIntroHeading();
        };
        ChatScroll.SizeChanged += (_, _) => FitIntroHeading();
        IntroHeading.SizeChanged += (_, _) => FitIntroHeading();
        OfferRow.SizeChanged += (_, _) => FitIntroHeading();
        OfferRow.PropertyChanged += (_, e) => { if (e.Property == IsVisibleProperty) FitIntroHeading(); };
        InputRow.SizeChanged += (_, _) => FitIntroHeading();
        ChatScroll.AddHandler(PointerWheelChangedEvent, (_, e) =>
        {
            if (e.Delta.Y > 0) _stickToBottom = false; // wheel up: the user wants to read back
            else Dispatcher.UIThread.Post(UpdateStickiness, DispatcherPriority.Background);
        }, RoutingStrategies.Tunnel);
        // The heading and the offer of text recognition lie over the chat's bottom, outside what
        // scrolls, and so do the gaps around them (they reach the window): the wheel anywhere over
        // the chat scrolls it as over the cards (the user, 2026-10-30: it did nothing over the
        // heading), handed to what scrolls so the step is the same.
        PointerWheelChanged += OnWheelOutsideChat;
        ChatScroll.ScrollChanged += (_, e) =>
        {
            // Scrollbar drags and keyboard paging: re-evaluate once the user stops at a position.
            if (!_pinning && e.OffsetDelta.Y != 0 && e.ExtentDelta.Y == 0 && e.ViewportDelta.Y == 0)
                UpdateStickiness();
        };
        // Other windows add chats while this one is open, and have some open themselves.
        ((Avalonia.Controls.Primitives.PopupFlyoutBase)HistoryButton.Flyout!).Opening += (_, _) => Vm.RefreshPastChats();

        // The chat's own right-click menu, before a text block's (Copy alone) can open.
        ChatScroll.AddHandler(ContextRequestedEvent, OnChatMenu, RoutingStrategies.Tunnel);

        // What Tab or the arrow keys move to is scrolled into view; what a click focuses stays where
        // it is under the mouse (the chat's ScrollViewer does neither by itself, see its markup).
        ChatScroll.AddHandler(GotFocusEvent, (_, e) =>
        {
            if (e.NavigationMethod is NavigationMethod.Tab or NavigationMethod.Directional && e.Source is Control focused)
                focused.BringIntoView();
        });

        // The side panel gives way to the chat as the window is made narrower, and what the
        // splitter is dragged to is the width it then goes back to.
        Columns.SizeChanged += (_, e) =>
        {
            if (e.WidthChanged)
                FitColumns(e.NewSize.Width);
        };
        SidePanelSplitter.DragCompleted += (_, _) =>
        {
            if (_docked > 0)
                _docked = SidePanelWidth;
        };
    }

    private double MaxOffset => Math.Max(0, ChatScroll.Extent.Height - ChatScroll.Viewport.Height);

    private void UpdateStickiness() => _stickToBottom = ChatScroll.Offset.Y >= MaxOffset - 24;

    private void PinToBottom()
    {
        // The intro of an unused chat is read from the top.
        if (!_stickToBottom || _pinning || DataContext is MainWindowViewModel { ShowIntro: true }
            || Math.Abs(ChatScroll.Offset.Y - MaxOffset) < 0.5)
            return;
        _pinning = true;
        try { ChatScroll.Offset = new Vector(ChatScroll.Offset.X, MaxOffset); }
        finally { _pinning = false; }
    }

    /// <summary>How tall the intro's heading is, last seen on screen.</summary>
    private double _headingHeight;

    /// <summary>
    /// The intro's heading over the chat box gives the cards its room when they would be left less
    /// than a card's: in the smallest window with a banner up, or with a long message typed (the box
    /// grows to 200), the heading took what was left and the cards were scrolled into a strip.
    /// Whether it shows is worked out from the room the two share, from the top of the chat to the
    /// top of the box, so showing or hiding it doesn't change the answer. (Counted as the chat's
    /// height plus the heading's, a heading just shown and not yet laid out counted 0, hid itself
    /// again, and the two went back and forth until the layout gave up, leaving a blank strip.)
    /// </summary>
    private void FitIntroHeading()
    {
        if (IntroHeading.IsVisible && IntroHeading.Bounds.Height > 0)
            _headingHeight = IntroHeading.Bounds.Height;
        var room = InputRow.Bounds.Top - ChatScroll.Bounds.Top;
        IntroHeading.IsVisible = room - _headingHeight >= 64;
        // The chat runs over the heading's row, so its bar goes down to the chat box on the start
        // screen as in a chat (the user, 2026-10-30: it stopped at the heading); what scrolls stops
        // at the heading's top, the cards never going under it. The same for the offer of text
        // recognition, in that row too: the bar keeps its length while the offer is up.
        var below = Math.Max(IntroHeading.IsVisible ? IntroHeading.Bounds.Height : 0, OfferRow.IsVisible ? OfferRow.Bounds.Height : 0);
        if (_chatPresenter is not null)
            _chatPresenter.Margin = new Thickness(0, 0, 0, below);
    }

    /// <summary>What scrolls in the chat, inside its bar (see FitIntroHeading).</summary>
    private ScrollContentPresenter? _chatPresenter;

    private void OnWheelOutsideChat(object? sender, PointerWheelEventArgs e)
    {
        // What scrolls had the wheel already (this one handed on included), even if it was at the end.
        if (e.Handled || _chatPresenter is null || e.Source is Visual source && _chatPresenter.IsVisualAncestorOf(source)
            || ReferenceEquals(e.Source, _chatPresenter) || !new Rect(ChatScroll.Bounds.Size).Contains(e.GetPosition(ChatScroll)))
            return;
        e.Handled = true;
        _chatPresenter.RaiseEvent(new PointerWheelEventArgs(_chatPresenter, e.Pointer, this, e.GetPosition(this),
            e.Timestamp, e.GetCurrentPoint(this).Properties, e.KeyModifiers, e.Delta) { RoutedEvent = PointerWheelChangedEvent });
    }

    private MainWindowViewModel Vm => (MainWindowViewModel)DataContext!;

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        InputBox.Focus();
    }

    // ---- size and place, kept between runs -----------------------------------------------------

    /// <summary>The last size and place while the window was neither maximized nor minimized.</summary>
    private (PixelPoint Position, double Width, double Height)? _normal;

    /// <summary>Opened a step from another window: closed where it opened, it doesn't save its place.</summary>
    private (PixelPoint Position, double Width, double Height)? _cascaded;

    /// <summary>
    /// Puts the window where it was when last closed (it used to open in the middle at its default
    /// size every time), unless that place is no longer on any screen: a monitor unplugged since.
    /// With another window open already (<paramref name="beside"/>), it opens a step down and to the
    /// right of that one, at its size, as Windows cascades windows: right on top of it, a second
    /// window looked as if nothing had opened. Called before the window is first shown.
    /// </summary>
    public void Restore(Core.Settings.WindowPlacement? placement, MainWindow? beside = null)
    {
        // Read once the move or resize is through: a window being minimized moves off screen
        // before it reports being minimized.
        PositionChanged += (_, _) => Dispatcher.UIThread.Post(Remember);
        SizeChanged += (_, _) => Dispatcher.UIThread.Post(Remember);
        if (Screens.Primary is not { } primary)
            return;
        FitTo(primary);
        beside?.Remember();
        if (beside?._normal is { } other)
            placement = new Core.Settings.WindowPlacement { X = other.Position.X, Y = other.Position.Y, Width = other.Width, Height = other.Height, WithTitleBar = true };
        // Saved under Windows' title bar: taller by the app's own, so the content is as tall as it was.
        if (placement is { WithTitleBar: false } old)
            placement = new Core.Settings.WindowPlacement { X = old.X, Y = old.Y, Width = old.Width, Height = old.Height + TitleBar.Room, Maximized = old.Maximized, WithTitleBar = true };
        // Some of the title bar must be on a screen, or the window can't even be dragged back.
        if (placement is not { } p || p.Width < MinWidth || p.Height < MinHeight
            || Screens.All.FirstOrDefault(s => s.WorkingArea.Intersects(new PixelRect(p.X + 40, p.Y + 4, 160, 24))) is not { } screen)
            return;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Position = new PixelPoint(p.X, p.Y);
        Width = p.Width;
        Height = p.Height;
        FitTo(screen);
        if (beside is not null)
            Cascade(screen);
        else if (p.Maximized)
            WindowState = WindowState.Maximized;
        _normal = (Position, Width, Height);
        if (beside is not null)
            _cascaded = _normal;
    }

    /// <summary>A step down and to the right, kept on the screen.</summary>
    private void Cascade(Avalonia.Platform.Screen screen)
    {
        var area = screen.WorkingArea;
        var (width, height) = ((int)(Width * screen.Scaling), (int)(Height * screen.Scaling));
        var step = (int)Math.Round(32 * screen.Scaling);
        Position = new PixelPoint(Math.Max(area.X, Math.Min(Position.X + step, area.Right - width)),
                                  Math.Max(area.Y, Math.Min(Position.Y + step, area.Bottom - height)));
    }

    /// <summary>
    /// No taller or wider than the screen's working area. The default 900x760 is more than a
    /// 1920x1080 laptop screen at 150% has (688 of height above the taskbar): centred, the title
    /// bar went above the screen and the message box under the taskbar.
    /// </summary>
    private void FitTo(Avalonia.Platform.Screen screen)
    {
        var area = screen.WorkingArea.Size.ToSize(screen.Scaling);
        // Width and Height are the client area, the title bar in it; Windows' frame takes a few pixels
        // more (40 was Windows' title bar and borders, kept as room to spare).
        Width = Math.Max(MinWidth, Math.Min(Width, area.Width - 16));
        Height = Math.Max(MinHeight, Math.Min(Height, area.Height - 40));
    }

    /// <summary>The chat's place and size: without the side panel's column on its left, which isn't open the next time.</summary>
    private void Remember()
    {
        if (WindowState != WindowState.Normal || !IsVisible)
            return;
        var (position, width) = ChatAlone();
        _normal = (position, width, ClientSize.Height);
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        // Let go of first: closing, it would bring this window forward and hold its videos again.
        var viewer = _viewer;
        _viewer = null;
        viewer?.Close();
        InView.Hold(this, false);
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        Remember();
        // A window opened beside another and neither moved nor resized: the place where Sloperator
        // opens stays where it was, rather than creeping down a step with every second window.
        if (_cascaded is not null && _normal == _cascaded && WindowState != WindowState.Maximized)
            return;
        if (!e.Cancel && _normal is { } normal)
            Vm.SavePlacement(new Core.Settings.WindowPlacement
            {
                X = normal.Position.X,
                Y = normal.Position.Y,
                Width = normal.Width,
                Height = normal.Height,
                Maximized = WindowState == WindowState.Maximized,
                WithTitleBar = true,
            });
    }

    // ---- files going with a message: dropped, pasted or picked ------------------------------------

    private static bool CarriesFiles(IDataTransfer data) => data.Contains(DataFormat.File) || data.Contains(DataFormat.Bitmap);

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        var data = e.DataTransfer;
        // Files and pictures are attached; text (a link dragged from a browser) goes into the box.
        e.DragEffects = CarriesFiles(data) || data.Contains(DataFormat.Text) ? DragDropEffects.Copy : DragDropEffects.None;
        Vm.IsDropping = CarriesFiles(data) && !OverSidePanel(e);
        e.Handled = true;
    }

    private void OnDragLeave(object? sender, DragEventArgs e)
    {
        // Moving from one part of the window to another leaves that part, not the window.
        var at = e.GetPosition(this);
        if (at.X > 1 && at.Y > 1 && at.X < Bounds.Width - 1 && at.Y < Bounds.Height - 1)
            return;
        Vm.IsDropping = false;
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        Vm.IsDropping = false;
        var data = e.DataTransfer;
        if (data.TryGetFiles() is { Length: > 0 } files)
        {
            var paths = files.Select(f => f.TryGetLocalPath()).OfType<string>().ToList();
            if (!OverSidePanel(e) || !Vm.DropOnSidePanel(paths))
                Vm.Attach(paths);
        }
        else if (data.TryGetBitmap() is { } bitmap)
            using (bitmap)
                Vm.AttachPicture(bitmap);
        else if (data.TryGetText() is { Length: > 0 } text)
            InsertText(text);
        e.Handled = true;
        Activate();
        InputBox.Focus();
    }

    /// <summary>Puts text where the caret is in the box, over what is selected there.</summary>
    private void InsertText(string text)
    {
        var current = InputBox.Text ?? "";
        var start = Math.Min(InputBox.SelectionStart, InputBox.SelectionEnd);
        var end = Math.Max(InputBox.SelectionStart, InputBox.SelectionEnd);
        start = Math.Clamp(start, 0, current.Length);
        end = Math.Clamp(end, start, current.Length);
        InputBox.Text = current[..start] + text + current[end..];
        InputBox.CaretIndex = start + text.Length;
    }

    /// <summary>Set while the box pastes text itself: <see cref="OnPasting"/> let it.</summary>
    private bool _pastingText;

    /// <summary>
    /// Ctrl+V (or the box's own Paste) in the message box. Files copied in Explorer are attached; text
    /// is pasted as usual, also when a picture comes with it (cells copied from Excel bring one);
    /// a picture alone (a screenshot, a picture copied in a browser) is saved and attached.
    /// </summary>
    private async void OnPasting(object? sender, RoutedEventArgs e)
    {
        if (_pastingText || Clipboard is not { } clipboard)
            return;
        e.Handled = true;
        try
        {
            if (await clipboard.TryGetFilesAsync() is { Length: > 0 } files)
            {
                Vm.Attach(files.Select(f => f.TryGetLocalPath()).OfType<string>());
                return;
            }
            var formats = await clipboard.GetDataFormatsAsync();
            if (!formats.Contains(DataFormat.Text) && await clipboard.TryGetBitmapAsync() is { } bitmap)
            {
                using (bitmap)
                    Vm.AttachPicture(bitmap);
                return;
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Another program holds the clipboard, or its picture can't be read: paste text, if any.
        }
        _pastingText = true;
        try { InputBox.Paste(); }
        finally { _pastingText = false; }
    }

    // ---- right-click in the chat ---------------------------------------------------------------------

    /// <summary>
    /// The chat's right-click menu, one for the whole log: Copy for selected text, Undo message and
    /// everything below on a message of the user's, then Compact context. It opens before a text
    /// block's own menu (Copy alone) could. A picture or file keeps its own (Open in Inspector, Copy
    /// image or Copy file, see <see cref="FileMenu"/>).
    /// </summary>
    private void OnChatMenu(object? sender, ContextRequestedEventArgs e)
    {
        if (e.Source is not Control source)
            return;
        var between = source.GetSelfAndVisualAncestors().OfType<Control>().TakeWhile(c => !ReferenceEquals(c, ChatScroll)).ToList();
        if (between.Any(c => c.ContextMenu is not null || FileMenu.Has(c)))
            return;
        e.Handled = true;
        var menu = new ContextMenu { ItemsSource = ChatMenuItems(source) };
        menu.Open(source);
    }

    /// <summary>The chat menu's items for a right-click on <paramref name="source"/>.</summary>
    private List<Control> ChatMenuItems(Control source)
    {
        // Read now: the text block loses its selection with the focus as the menu opens.
        var selected = SelectedTexts(source);
        var text = string.Join(Environment.NewLine, selected.Select(t => t.SelectedText));
        var copy = new MenuItem { Header = "Copy", IsEnabled = selected.Count > 0, InputGesture = new KeyGesture(Key.C, KeyModifiers.Control) };
        copy.Click += async (_, _) =>
        {
            try
            {
                if (Clipboard is { } clipboard)
                    await clipboard.SetTextAsync(text);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // Another program holds the clipboard; the next try will do.
            }
        };
        List<Control> items = [copy];
        // On a message of the user's (its words or anywhere in its bubble); greyed out while a task runs.
        if (source.GetSelfAndVisualAncestors().OfType<Control>().FirstOrDefault(c => c.DataContext is UserMessageItem)?.DataContext is UserMessageItem message)
        {
            var undo = new MenuItem { Header = "Undo message and everything below", DataContext = message, IsEnabled = Vm.CanUndo };
            undo.Click += OnUndoMessage;
            items.Add(undo);
        }
        var compact = new MenuItem { Header = "Compact context", IsEnabled = Vm.CanCompactContext };
        ToolTip.SetTip(compact, "Start a fresh context from a handoff note");
        compact.Click += (_, _) => Vm.CompactContext();
        items.Add(new Separator());
        items.Add(compact);
        return items;
    }

    /// <summary>
    /// The text selected where the right-click was: in a reply, every block of it the selection
    /// spans (see <see cref="MarkdownView"/>); elsewhere the block clicked.
    /// </summary>
    private static List<SelectableTextBlock> SelectedTexts(Control source)
    {
        IEnumerable<SelectableTextBlock> texts = source.FindAncestorOfType<MarkdownView>(includeSelf: true) is { } reply
            ? reply.GetVisualDescendants().OfType<SelectableTextBlock>()
            : source.FindAncestorOfType<SelectableTextBlock>(includeSelf: true) is { } block ? [block] : [];
        return texts.Where(t => t.SelectionStart != t.SelectionEnd).ToList();
    }

    private async void OnUndoMessage(object? sender, RoutedEventArgs e)
    {
        if ((sender as StyledElement)?.DataContext is UserMessageItem message)
            await Vm.UndoFromAsync(message);
    }

    private async void OnAttachClick(object? sender, RoutedEventArgs e)
    {
        var picked = await StorageProvider.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions
        {
            Title = "Attach files to your message",
            AllowMultiple = true,
        });
        Vm.Attach(picked.Select(f => f.TryGetLocalPath()).OfType<string>());
        InputBox.Focus();
    }

    private void OnInputKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || e.KeyModifiers.HasFlag(KeyModifiers.Shift))
            return;
        e.Handled = true;
        _stickToBottom = true;
        // Ctrl+Enter during a run: read at once, as Interrupt does.
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
            Vm.SendNow();
        else if (Vm.SendCommand.CanExecute(null))
            Vm.SendCommand.Execute(null);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is not MainWindowViewModel vm)
            return;
        // Once the chat is used it follows its end, however far the intro was scrolled. A tool
        // picked in the Side panel dropdown opens on the left of the chat.
        vm.PropertyChanged += (_, change) =>
        {
            if (change.PropertyName == nameof(MainWindowViewModel.ShowIntro) && !vm.ShowIntro)
                _stickToBottom = true;
            if (change.PropertyName == nameof(MainWindowViewModel.SidePanel))
                Dock();
        };
        // The click marker is drawn outside Avalonia, so it is handed the accent colour.
        vm.ShowInputOverlay(this.FindResource("BbAccent") is Avalonia.Media.ISolidColorBrush accent
            ? (accent.Color.R, accent.Color.G, accent.Color.B)
            : null);
    }

    /// <summary>A saved server picked: the dropdown closes (afterwards, as History's: its row's command comes after the click).</summary>
    private void OnSavedServerClick(object? sender, RoutedEventArgs e) =>
        Dispatcher.UIThread.Post(() => SavedServersButton.Flyout?.Hide());

    /// <summary>A chat picked in the History dropdown (its command opens it): close the list and get back to typing.</summary>
    private void OnPastChatClick(object? sender, RoutedEventArgs e)
    {
        _stickToBottom = true;
        var row = (sender as Control)?.DataContext as PastChat;
        // Closed afterwards: the Click event comes before the command, and closing detaches the
        // row, which unbinds its command.
        Dispatcher.UIThread.Post(() =>
        {
            HistoryButton.Flyout?.Hide();
            // Open in another window, which came forward: the focus stays there.
            if (row is not { IsElsewhere: true })
                InputBox.Focus();
        });
    }

    // ---- the full-screen viewer ---------------------------------------------------------------------

    /// <summary>The full-screen viewer, while it is open.</summary>
    private MediaViewer? _viewer;

    /// <summary>A picture or video preview in the chat, or a document's peek: full screen.</summary>
    private void OnPictureClick(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is FilePreview preview)
            OpenViewer(preview);
    }

    /// <summary>A file in a sent message's bubble: a picture or video full screen, anything else in its own program.</summary>
    private void OnAttachmentClick(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not AttachmentItem attachment)
            return;
        if (!OpenViewer(attachment))
            attachment.OpenCommand.Execute(null);
    }

    private void OnScreenshotClick(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is ToolCallItem tool)
            OpenViewer(tool);
    }

    /// <summary>A picture under the box, waiting to go with the next message: full screen on its own.</summary>
    private void OnComposerPictureReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton == MouseButton.Left && (sender as Control)?.DataContext is AttachmentItem attachment)
            OpenViewer(attachment);
    }

    /// <summary>
    /// Shows <paramref name="source"/> full screen on the chat's screen, with the chat's other pictures
    /// and videos to go through (a picture not yet sent, on its own); false when it is no picture or
    /// video. The chat's videos hold still meanwhile.
    /// </summary>
    private bool OpenViewer(object source)
    {
        var entries = Vm.MediaInChat();
        var index = entries.FindIndex(e => ReferenceEquals(e.Source, source));
        if (index < 0 && source is AttachmentItem { Path: var path } attachment && MainWindowViewModel.Viewable(path))
            (entries, index) = ([new MediaEntry(attachment, path, null, attachment.FileName)], 0);
        if (index < 0 || (Screens.ScreenFromWindow(this) ?? Screens.Primary) is not { } screen)
            return false;
        _viewer?.Close();
        var viewer = _viewer = MediaViewer.On(screen, entries, index, Vm.Previews, Vm); // the chat's screen
        viewer.Closed += (_, _) =>
        {
            if (ReferenceEquals(_viewer, viewer))
            {
                _viewer = null;
                HoldVideos();
                Activate();
            }
        };
        HoldVideos();
        viewer.Show();
        return true;
    }

    /// <summary>The chat's videos play while they can be seen: not under the viewer, nor with the window minimized.</summary>
    private void HoldVideos() => InView.Hold(this, _viewer is not null || WindowState == WindowState.Minimized);

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == WindowStateProperty)
            HoldVideos();
    }

    // ---- the side panel, on the left of the chat ----------------------------------------------------

    /// <summary>How wide the side panel's column is to be, splitter included, as it opened or was dragged to; 0 with none.</summary>
    private double _docked;

    /// <summary>How narrow the open panel may get, splitter included (<see cref="SidePanelOption.MinWidth"/>).</summary>
    private double _least;

    /// <summary>Where the window was and how wide before the side panel opened, and after: closed with the window as it was left, it goes back there.</summary>
    private (PixelPoint Position, double Width)? _beforeDock, _afterDock;

    /// <summary>
    /// A panel picked in the Side panel dropdown opens on the left of the chat: the window grows to the
    /// left by the panel's width, so the chat stays where it was on the screen (as far as the screen
    /// has room; else the window takes the screen's width, the panel and the chat sharing it). None
    /// gives the room back. A maximized window keeps its size, and the two share it. Made narrower,
    /// the panel gives way down to its least before the chat does (<see cref="SidePanelColumns"/>).
    /// </summary>
    private void Dock()
    {
        var option = Vm.HasSidePanel ? Vm.SelectedSidePanel : null;
        var width = option?.Width ?? 0;
        var least = Math.Min(width, option?.MinWidth ?? 0);
        var tall = Math.Max(ChatMinHeight, option?.MinHeight ?? 0);
        // Set here, not bound: a binding could take it after this lays the window out, which then
        // showed the panel's place empty for a frame and drew the panel in the next.
        SidePanelContent.Content = Vm.SidePanel;
        if (width <= 0 && _docked <= 0)
            return;
        // Picked, the dropdown's list goes before the window changes: left to the dropdown, it went
        // after, over the resized window for a frame.
        SidePanelBox.IsDropDownOpen = false;
        var columns = Columns.ColumnDefinitions;
        var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
        var scaling = screen?.Scaling ?? 1;
        // Where the chat alone is: one panel for another goes straight from the one's width to the
        // other's in one step. Closed and opened again, the window shrank to the chat for a frame and
        // grew back with the new panel's place empty (captured at 60 fps, 2026-10-25).
        var chat = WindowState == WindowState.Normal && _docked > 0 ? ChatAlone() : (Position, Width);
        if (width > 0)
        {
            (PixelPoint Position, double Width)? place = null;
            if (WindowState == WindowState.Normal && screen is not null)
            {
                var area = screen.WorkingArea;
                // As tall as the panel needs (Video generator's viewer over its timeline), kept on the screen.
                if (Height < tall && tall <= area.Height / scaling - 40)
                {
                    Height = tall;
                    Position = Position.WithY(Math.Max(area.Y, Math.Min(Position.Y, area.Bottom - (int)((tall + 40) * scaling))));
                    chat = (chat.Position.WithY(Position.Y), chat.Width);
                }
                var most = area.Width / scaling - 16;
                var wide = Math.Min(most, chat.Width + width);
                var add = SidePanelColumns.Panel(wide, width, least);
                var outer = (int)((wide + 16) * scaling);
                var x = Math.Max(area.X, Math.Min(chat.Position.X - (int)(add * scaling), area.Right - outer));
                place = (new PixelPoint(x, chat.Position.Y), wide);
            }
            _beforeDock = place is null ? null : chat;
            _afterDock = null;
            // As narrow as the panel and the chat may both get, if the screen is as wide: lowered
            // before the window is resized, which it couldn't be below the least of the panel before.
            MinWidth = screen is null ? SidePanelColumns.WindowMin(least) : Math.Min(SidePanelColumns.WindowMin(least), screen.WorkingArea.Width / scaling - 16);
            MinHeight = screen is null ? tall : Math.Min(tall, screen.WorkingArea.Height / scaling - 40);
            // The panel and its column first, laid out at the window's new size before the window
            // grows: the panel took a tenth of a second to lay out the first time, and laid out
            // after, the screen showed the window's new part black meanwhile.
            SidePanelHost.IsVisible = SidePanelSplitter.IsVisible = true;
            columns[0].MinWidth = least - SidePanelColumns.Splitter;
            columns[1].Width = new GridLength(SidePanelColumns.Splitter);
            _docked = width;
            _least = least;
            var size = place?.Width ?? Columns.Bounds.Width;
            FitColumns(size);
            LayOut(size);
            if (place is { } p)
            {
                Place(p.Position, p.Width);
                _afterDock = (Position, Width);
            }
        }
        else
        {
            SidePanelHost.IsVisible = SidePanelSplitter.IsVisible = false;
            // Before the window is resized: FitColumns would give the column back its width.
            _docked = 0;
            columns[0].MinWidth = columns[2].MinWidth = 0;
            columns[0].Width = new GridLength(0);
            columns[1].Width = new GridLength(0);
            // Lowered first: the window can't be made narrower than it while it is resized.
            MinWidth = ChatMinWidth;
            MinHeight = ChatMinHeight;
            // Neither moved nor resized since it opened: back where it was (the screen's edge may have
            // kept it from growing to the left by all of the panel's width, and the chat moved). Else
            // the chat stays where it is.
            if (WindowState == WindowState.Normal)
            {
                LayOut(chat.Width);
                Place(chat.Position, chat.Width);
            }
            _beforeDock = _afterDock = null;
        }
    }

    /// <summary>
    /// Lays the window's content out <paramref name="width"/> across, as it is about to be, before
    /// the window is: all of it, the new panel and the Side panel dropdown's new name too. Measure skips
    /// what was laid out before at the same size, so the new panel was laid out (and drawn) only at
    /// the window's next layout, a frame later, and the dropdown was cut to its old name's width.
    /// </summary>
    private void LayOut(double width)
    {
        foreach (var changed in Root.GetVisualDescendants().OfType<Layoutable>().Where(c => !c.IsMeasureValid || !c.IsArrangeValid).ToList())
            for (Visual? v = changed; v is not null && v != Root; v = v.GetVisualParent())
                (v as Layoutable)?.InvalidateMeasure();
        Root.InvalidateMeasure();
        var bounds = Root.Bounds.WithWidth(width);
        Root.Measure(bounds.Size);
        Root.Arrange(bounds);
    }

    /// <summary>
    /// Where the chat alone is on the screen and how wide: where it was before the side panel opened if
    /// the window is as the side panel left it, else the window without the side panel's column.
    /// </summary>
    private (PixelPoint Position, double Width) ChatAlone()
    {
        if (_beforeDock is { } before && _afterDock is { } after && Position == after.Position && Math.Abs(Width - after.Width) < 1)
            return before;
        var sidePanel = SidePanelWidth;
        var scaling = (Screens.ScreenFromWindow(this) ?? Screens.Primary)?.Scaling ?? 1;
        return (new PixelPoint(Position.X + (int)(sidePanel * scaling), Position.Y), Math.Max(ChatMinWidth, Width - sidePanel));
    }

    /// <summary>
    /// Moves the window and sets its width in one step, laid out at once for the new size. Set one
    /// after the other, Position moved it at once and Width only at the next layout, which the
    /// Video generator panel took a tenth of a second over: the chat jumped sideways with the window, then
    /// the window grew with nothing drawn in it, and then the chat jumped back with the panel beside it.
    /// </summary>
    private void Place(PixelPoint position, double width)
    {
        if (position == Position && Math.Abs(width - Width) < 0.5)
            return;
#if WINDOWS
        var fill = this.FindResource("BbBackground") is Avalonia.Media.ISolidColorBrush brush ? brush.Color.ToUInt32() & 0xFFFFFF : 0;
        if (OperatingSystem.IsWindows() && TryGetPlatformHandle()?.Handle is { } hwnd && hwnd != 0
            && Platform.Windows.WindowsPlatform.MoveAndWiden(hwnd, position.X - Position.X, (int)Math.Round((width - Width) * RenderScaling), fill, Shot(fill, out var across), across))
        {
            // The window told Avalonia its new size as it changed; Width follows it, or the next
            // layout would size the window back.
            Width = width;
            UpdateLayout();
            Uncover(3);
            return;
        }
#endif
        Position = position;
        Width = width;
    }

#if WINDOWS
    /// <summary>
    /// A picture of the window's content as laid out now (for the size it is about to get), over its
    /// background <paramref name="fill"/> (0xRRGGBB): BGRA rows from the top, <paramref name="across"/>
    /// pixels wide, for <see cref="Platform.Windows.WindowCover"/>; null if it couldn't be made.
    /// </summary>
    private byte[]? Shot(uint fill, out int across)
    {
        across = 0;
        var scaling = RenderScaling;
        // The title bar too: it is in the client area, which the cover goes over.
        var size = new PixelSize((int)Math.Ceiling(Root.Bounds.Width * scaling), (int)Math.Ceiling(Root.Bounds.Height * scaling));
        if (size.Width <= 0 || size.Height <= 0)
            return null;
        try
        {
            using var bitmap = new Avalonia.Media.Imaging.RenderTargetBitmap(size, new Vector(96 * scaling, 96 * scaling));
            bitmap.Render(Root);
            var pixels = new byte[size.Width * size.Height * 4];
            var handle = System.Runtime.InteropServices.GCHandle.Alloc(pixels, System.Runtime.InteropServices.GCHandleType.Pinned);
            try { bitmap.CopyPixels(new PixelRect(size), handle.AddrOfPinnedObject(), pixels.Length, size.Width * 4); }
            finally { handle.Free(); }
            // Premultiplied: what isn't opaque shows the window's background through.
            byte r = (byte)(fill >> 16), g = (byte)(fill >> 8), b = (byte)fill;
            for (var i = 0; i < pixels.Length; i += 4)
            {
                var clear = 255 - pixels[i + 3];
                if (clear == 0)
                    continue;
                pixels[i] = (byte)(pixels[i] + b * clear / 255);
                pixels[i + 1] = (byte)(pixels[i + 1] + g * clear / 255);
                pixels[i + 2] = (byte)(pixels[i + 2] + r * clear / 255);
                pixels[i + 3] = 255;
            }
            across = size.Width;
            return pixels;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Takes the cover away once Avalonia has drawn the window at its new size, <paramref name="frames"/> frames on.</summary>
    private void Uncover(int frames)
    {
        if (frames > 0)
            RequestAnimationFrame(_ => Uncover(frames - 1));
        else if (OperatingSystem.IsWindows())
            Platform.Windows.WindowCover.Hide();
    }
#endif

    /// <summary>
    /// The panel's column in a window <paramref name="width"/> across: as wide as it is to be, as
    /// far as the chat keeps its least. The chat's column keeps it too while the splitter is dragged
    /// (on a screen narrower than both, what is left).
    /// </summary>
    private void FitColumns(double width)
    {
        if (_docked <= 0 || width <= 0)
            return;
        var columns = Columns.ColumnDefinitions;
        columns[2].MinWidth = Math.Clamp(width - _least, 0, ChatMinWidth);
        var panel = SidePanelColumns.Panel(width, _docked, _least) - SidePanelColumns.Splitter;
        if (Math.Abs(columns[0].Width.Value - panel) > 0.5)
            columns[0].Width = new GridLength(panel);
    }

    private const double ChatMinWidth = SidePanelColumns.ChatMin;

    /// <summary>How short the chat may get: the window's own least height, as set in its markup.</summary>
    private const double ChatMinHeight = 513;

    /// <summary>The part of the window the side panel takes now, in pixels across; 0 with none.</summary>
    private double SidePanelWidth => _docked > 0 ? Columns.ColumnDefinitions[0].ActualWidth + Columns.ColumnDefinitions[1].ActualWidth : 0;

    /// <summary>Whether a drag or a drop is over the side panel.</summary>
    private bool OverSidePanel(DragEventArgs e) =>
        Vm.HasSidePanel && e.GetPosition(this).X < SidePanelWidth;

    private async void OnSettingsClick(object? sender, RoutedEventArgs e)
    {
        var settingsVm = Vm.CreateSettingsViewModel();
        var window = new SettingsWindow { DataContext = settingsVm };
        // An MCP server the agent adds while the dialog is open shows up in it, and so does how each one is doing.
        var refresh = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => Vm.RefreshMcp(settingsVm));
        refresh.Start();
        bool saved;
        try { saved = await window.ShowDialog<bool>(this); }
        finally { refresh.Stop(); }
        if (saved)
            await Vm.ApplySettingsAsync(settingsVm);
    }
}
