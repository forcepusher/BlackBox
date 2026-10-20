using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using BlackBox.App.ViewModels;

namespace BlackBox.App.Views;

/// <summary>
/// Pictures and videos from the chat at the size of the screen, opened by a click on one: Esc, the
/// × or a click beside it goes back to the chat, the arrow keys go through the chat's others, Space
/// plays or pauses a video and M turns its sound on or off.
/// </summary>
public partial class MediaViewer : Window
{
    /// <summary>How long the mouse rests before the bar over the top and the arrows fade away.</summary>
    private readonly DispatcherTimer _idle = new() { Interval = TimeSpan.FromSeconds(2.5) };

    public MediaViewer()
    {
        InitializeComponent();
        // Before the slider and the buttons, which take the arrow keys and Space for themselves.
        AddHandler(KeyDownEvent, OnKey, RoutingStrategies.Tunnel);
        AddHandler(PointerMovedEvent, (_, _) => Awake(), RoutingStrategies.Tunnel, handledEventsToo: true);
        _idle.Tick += (_, _) =>
        {
            _idle.Stop();
            Classes.Set("idle", true);
        };
        Closed += (_, _) =>
        {
            _idle.Stop();
            (DataContext as IDisposable)?.Dispose();
        };
    }

    /// <summary>The mouse moved: the bar and the arrows are back, until it rests again.</summary>
    private void Awake()
    {
        Classes.Set("idle", false);
        _idle.Stop();
        _idle.Start();
    }

    private MediaViewerViewModel Vm => (MediaViewerViewModel)DataContext!;

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        // On the screen it was placed on (the chat's), covering the taskbar too.
        WindowState = WindowState.FullScreen;
        Activate();
        Awake();
    }

    private void OnKey(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                Close();
                break;
            case Key.Left when Vm.PreviousCommand.CanExecute(null):
                Vm.PreviousCommand.Execute(null);
                break;
            case Key.Right when Vm.NextCommand.CanExecute(null):
                Vm.NextCommand.Execute(null);
                break;
            case Key.Space:
                Vm.PlayOrPause();
                break;
            case Key.M when Vm.Shown is VideoPreview video:
                video.ToggleSoundCommand.Execute(null);
                break;
            case Key.P when Vm.HasRecipe:
                Vm.ToggleRecipeCommand.Execute(null);
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();

    private void OnBesideClicked(object? sender, EventArgs e) => Close();

    /// <summary>A click on the dark around an animation or a video, not on it: back to the chat.</summary>
    private void OnBackdropReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton == MouseButton.Left && ReferenceEquals(e.Source, sender))
            Close();
    }

    private void OnVideoReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton != MouseButton.Left)
            return;
        Vm.PlayOrPause();
        e.Handled = true;
    }
}
