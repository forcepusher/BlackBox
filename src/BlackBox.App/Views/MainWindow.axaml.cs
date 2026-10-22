using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using BlackBox.App.ViewModels;

namespace BlackBox.App.Views;

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

        // Follow new output while the user is at the bottom; stop following when they scroll up.
        // After every layout pass (streamed text, a growing thinking block, a new screenshot)
        // pin the offset to the true end. Doing it per layout pass rather than per scroll event
        // is what keeps the last line from being clipped while text is still arriving.
        ChatScroll.LayoutUpdated += (_, _) => PinToBottom();
        ChatScroll.AddHandler(PointerWheelChangedEvent, (_, e) =>
        {
            if (e.Delta.Y > 0) _stickToBottom = false; // wheel up: the user wants to read back
            else Dispatcher.UIThread.Post(UpdateStickiness, DispatcherPriority.Background);
        }, RoutingStrategies.Tunnel);
        ChatScroll.ScrollChanged += (_, e) =>
        {
            // Scrollbar drags and keyboard paging: re-evaluate once the user stops at a position.
            if (!_pinning && e.OffsetDelta.Y != 0 && e.ExtentDelta.Y == 0 && e.ViewportDelta.Y == 0)
                UpdateStickiness();
        };
        // Other windows add chats while this one is open, and have some open themselves.
        ((Avalonia.Controls.Primitives.PopupFlyoutBase)HistoryButton.Flyout!).Opening += (_, _) => Vm.RefreshPastChats();

        // What Tab or the arrow keys move to is scrolled into view; what a click focuses stays where
        // it is under the mouse (the chat's ScrollViewer does neither by itself, see its markup).
        ChatScroll.AddHandler(GotFocusEvent, (_, e) =>
        {
            if (e.NavigationMethod is NavigationMethod.Tab or NavigationMethod.Directional && e.Source is Control focused)
                focused.BringIntoView();
        });
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
            placement = new Core.Settings.WindowPlacement { X = other.Position.X, Y = other.Position.Y, Width = other.Width, Height = other.Height };
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
        // Width and Height are the client area; the title bar and borders take about 40 more.
        Width = Math.Max(MinWidth, Math.Min(Width, area.Width - 16));
        Height = Math.Max(MinHeight, Math.Min(Height, area.Height - 40));
    }

    private void Remember()
    {
        if (WindowState == WindowState.Normal && IsVisible)
            _normal = (Position, ClientSize.Width, ClientSize.Height);
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        // Its own: BlackBox goes on with the other windows, where these were left behind.
        _film?.Close();
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
        // A window opened beside another and neither moved nor resized: the place where BlackBox
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
            });
    }

    // ---- files going with a message: dropped, pasted or picked ------------------------------------

    private static bool CarriesFiles(IDataTransfer data) => data.Contains(DataFormat.File) || data.Contains(DataFormat.Bitmap);

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        var data = e.DataTransfer;
        // Files and pictures are attached; text (a link dragged from a browser) goes into the box.
        e.DragEffects = CarriesFiles(data) || data.Contains(DataFormat.Text) ? DragDropEffects.Copy : DragDropEffects.None;
        Vm.IsDropping = CarriesFiles(data);
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
            Vm.Attach(files.Select(f => f.TryGetLocalPath()).OfType<string>());
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

    // ---- right-click → Copy on a picture in the chat ------------------------------------------------

    /// <summary>The picture last put on the clipboard: programs read it from BlackBox when they paste, so it stays until the next copy.</summary>
    private Avalonia.Media.Imaging.Bitmap? _copied;

    /// <summary>
    /// Copy, in the right-click menu of a picture in the chat: the picture at its full size, for
    /// programs that take a picture (Paint, Word, a chat app's message box), together with its file,
    /// for those that take files (Explorer, an e-mail). A video gives the frame on screen with its
    /// file, an attached file that isn't a picture the file alone, and a screenshot the picture alone.
    /// </summary>
    private async void OnCopyPicture(object? sender, RoutedEventArgs e)
    {
        if (Clipboard is not { } clipboard || (sender as StyledElement)?.DataContext is not { } source)
            return;
        var (path, shown, data) = source switch
        {
            ImagePreview p => (p.Path, p.Image, null),
            AnimationPreview p => (p.Path, p.Image, null),
            VideoPreview p => (p.Path, p.Frame, null),
            AttachmentItem a => (a.Path, a.Thumbnail, (byte[]?)null),
            ToolCallItem t => ((string?)null, t.Screenshot, t.ScreenshotData),
            _ => (null, null, null),
        };
        try
        {
            // What the chat shows is cut down to its width; the copy is the whole picture.
            var picture = source is VideoPreview ? null : await Task.Run(() => FullSize(path, data));
            picture ??= Duplicate(shown);
            var item = new DataTransferItem();
            if (picture is not null)
                item.SetBitmap(picture);
            if (path is not null &&
                (File.Exists(path) ? await StorageProvider.TryGetFileFromPathAsync(path) as IStorageItem
                    : Directory.Exists(path) ? await StorageProvider.TryGetFolderFromPathAsync(path) : null) is { } file)
                item.SetFile(file);
            if (picture is null && path is null)
                return;
            var transfer = new DataTransfer();
            transfer.Add(item);
            await clipboard.SetDataAsync(transfer);
            (_copied, picture) = (picture, _copied);
            picture?.Dispose();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Another program holds the clipboard; the next try will do.
        }
    }

    /// <summary>The picture in <paramref name="data"/> or the file at <paramref name="path"/>, decoded whole; null if neither is a picture.</summary>
    private static Avalonia.Media.Imaging.Bitmap? FullSize(string? path, byte[]? data)
    {
        try
        {
            if (data is not null)
                return new Avalonia.Media.Imaging.Bitmap(new MemoryStream(data));
            if (path is not null && File.Exists(path) && Core.Media.MediaPreview.KindOf(path) is "image" or "animation")
                return new Avalonia.Media.Imaging.Bitmap(path);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { }
        return null;
    }

    /// <summary>A copy of a picture the chat shows, which the chat may let go of (a video's frame changes as it plays).</summary>
    private static Avalonia.Media.Imaging.Bitmap? Duplicate(Avalonia.Media.Imaging.Bitmap? shown)
    {
        if (shown is null)
            return null;
        using var png = new MemoryStream();
        shown.Save(png);
        png.Position = 0;
        return new Avalonia.Media.Imaging.Bitmap(png);
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
        // Once the chat is used it follows its end, however far the intro was scrolled.
        vm.PropertyChanged += (_, change) =>
        {
            if (change.PropertyName == nameof(MainWindowViewModel.ShowIntro) && !vm.ShowIntro)
                _stickToBottom = true;
        };
        vm.ShowInputOverlay();
    }

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

    /// <summary>A picture or video preview in the chat: full screen.</summary>
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
        var viewer = _viewer = new MediaViewer { DataContext = new MediaViewerViewModel(entries, index, Vm.Previews, screen.Bounds.Width) };
        // Placed on the chat's screen, where it then goes full screen.
        viewer.Position = screen.Bounds.Position;
        viewer.Width = screen.Bounds.Width / screen.Scaling;
        viewer.Height = screen.Bounds.Height / screen.Scaling;
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

    /// <summary>The Filmmaking window, while it is open: the button brings it forward rather than opening a second one.</summary>
    private FilmWindow? _film;

    private void OnFilmmakingClick(object? sender, RoutedEventArgs e)
    {
        if (_film is { } open)
        {
            open.Activate();
            return;
        }
        // "Make the film" brings the chat forward, where the agent's work on it shows.
        _film = new FilmWindow { DataContext = Vm.CreateFilmViewModel(sent: () => Activate()) };
        _film.Closed += (_, _) => _film = null;
        // A window of its own, not one kept in front of the chat: the user watches both while the film is made.
        _film.Show();
    }

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
