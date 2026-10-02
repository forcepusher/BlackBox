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

        // Follow new output while the user is at the bottom; stop following if they scroll up.
        // Only an offset change the user made decides "stuck or not": when content grows
        // (streaming text, a new screenshot) the offset briefly lags the new extent, and treating
        // that as "scrolled up" is what broke following before.
        ChatScroll.ScrollChanged += (_, e) =>
        {
            if (e.ExtentDelta.Y != 0 || e.ViewportDelta.Y != 0)
            {
                if (_stickToBottom)
                    Dispatcher.UIThread.Post(ChatScroll.ScrollToEnd, DispatcherPriority.Background);
                return;
            }
            if (e.OffsetDelta.Y != 0)
                _stickToBottom = ChatScroll.Offset.Y + ChatScroll.Viewport.Height >= ChatScroll.Extent.Height - 60;
        };
    }

    private MainWindowViewModel Vm => (MainWindowViewModel)DataContext!;

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is not MainWindowViewModel vm) return;

        // Get out of the way while the agent works on the desktop, come back when it is done
        // or needs an answer.
        vm.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName != nameof(MainWindowViewModel.IsBusy)) return;
            if (!vm.IsBusy) Restore();
        };
        vm.AttentionNeeded += Restore;
        vm.AttentionHandled += () =>
        {
            if (vm.IsBusy && vm.WindowHiddenForRun) WindowState = WindowState.Minimized;
        };
        vm.HideRequested += () => WindowState = WindowState.Minimized;
    }

    private void Restore()
    {
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

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

    private async void OnSettingsClick(object? sender, RoutedEventArgs e)
    {
        var settingsVm = Vm.CreateSettingsViewModel();
        var window = new SettingsWindow { DataContext = settingsVm };
        if (await window.ShowDialog<bool>(this))
            await Vm.ApplySettingsAsync(settingsVm.Settings);
    }

    private void OnLogClick(object? sender, RoutedEventArgs e) =>
        new LogWindow(Vm.Server).Show(this);
}
