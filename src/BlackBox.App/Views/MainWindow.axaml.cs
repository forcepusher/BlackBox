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

    public MainWindow()
    {
        InitializeComponent();

        // Enter sends, Shift+Enter inserts a newline. Tunnel so we see the key before the TextBox.
        InputBox.AddHandler(KeyDownEvent, OnInputKeyDown, RoutingStrategies.Tunnel);

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

    private bool _pinning;

    private double MaxOffset => Math.Max(0, ChatScroll.Extent.Height - ChatScroll.Viewport.Height);

    private void UpdateStickiness() => _stickToBottom = ChatScroll.Offset.Y >= MaxOffset - 24;

    private void PinToBottom()
    {
        if (!_stickToBottom || _pinning || Math.Abs(ChatScroll.Offset.Y - MaxOffset) < 0.5)
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
        vm.OpenSettingsRequested += () => OnSettingsClick(this, new RoutedEventArgs());
        // Stay visible while the agent works, but invisible to its screenshots. Applied before the
        // window is first shown: changing capture exclusion on an already-painted window left
        // parts of it unpainted (the window behind showed through).
        if (TryGetPlatformHandle()?.Handle is { } handle)
            vm.AttachHostWindow(handle);
    }

    private async void OnSettingsClick(object? sender, RoutedEventArgs e)
    {
        var settingsVm = Vm.CreateSettingsViewModel();
        var window = new SettingsWindow { DataContext = settingsVm };
        if (await window.ShowDialog<bool>(this))
            await Vm.ApplySettingsAsync(settingsVm.Settings);
    }

}
