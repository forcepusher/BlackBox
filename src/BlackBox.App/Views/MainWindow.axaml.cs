using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
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
