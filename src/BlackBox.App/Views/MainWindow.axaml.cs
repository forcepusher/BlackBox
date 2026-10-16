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

    /// <summary>
    /// Puts the window where it was when last closed (it used to open in the middle at its default
    /// size every time), unless that place is no longer on any screen: a monitor unplugged since.
    /// Called before the window is first shown.
    /// </summary>
    public void Restore(Core.Settings.WindowPlacement? placement)
    {
        // Read once the move or resize is through: a window being minimized moves off screen
        // before it reports being minimized.
        PositionChanged += (_, _) => Dispatcher.UIThread.Post(Remember);
        SizeChanged += (_, _) => Dispatcher.UIThread.Post(Remember);
        if (placement is null || placement.Width < MinWidth || placement.Height < MinHeight)
            return;
        // Some of the title bar must be on a screen, or the window can't even be dragged back.
        var titleBar = new PixelRect(placement.X + 40, placement.Y + 4, 160, 24);
        if (!Screens.All.Any(s => s.WorkingArea.Intersects(titleBar)))
            return;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Position = new PixelPoint(placement.X, placement.Y);
        Width = placement.Width;
        Height = placement.Height;
        _normal = (Position, Width, Height);
        if (placement.Maximized)
            WindowState = WindowState.Maximized;
    }

    private void Remember()
    {
        if (WindowState == WindowState.Normal && IsVisible)
            _normal = (Position, ClientSize.Width, ClientSize.Height);
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        Remember();
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
        if (Vm.SendCommand.CanExecute(null))
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
        // Stay visible while the agent works, but invisible to its screenshots. Applied before the
        // window is first shown: changing capture exclusion on an already-painted window left
        // parts of it unpainted (the window behind showed through).
        if (TryGetPlatformHandle()?.Handle is { } handle)
            vm.AttachHostWindow(handle);
    }

    /// <summary>A chat picked in the History dropdown (its command opens it): close the list and get back to typing.</summary>
    private void OnPastChatClick(object? sender, RoutedEventArgs e)
    {
        _stickToBottom = true;
        // Closed afterwards: the Click event comes before the command, and closing detaches the
        // row, which unbinds its command.
        Dispatcher.UIThread.Post(() =>
        {
            HistoryButton.Flyout?.Hide();
            InputBox.Focus();
        });
    }

    private async void OnSettingsClick(object? sender, RoutedEventArgs e)
    {
        var settingsVm = Vm.CreateSettingsViewModel();
        var window = new SettingsWindow { DataContext = settingsVm };
        if (await window.ShowDialog<bool>(this))
            await Vm.ApplySettingsAsync(settingsVm.Settings, settingsVm.McpConfigText);
    }
}
