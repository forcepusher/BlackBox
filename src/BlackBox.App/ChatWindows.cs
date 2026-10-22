using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using BlackBox.App.ViewModels;
using BlackBox.App.Views;
using BlackBox.Core.Chats;
using BlackBox.Core.Platform;
using BlackBox.Core.Settings;

namespace BlackBox.App;

/// <summary>
/// BlackBox's chat windows: one for each task the user has going side by side, each with its own
/// chat and agent, in one process (see <see cref="SingleInstance"/>). What they share is here: the
/// mouse and keyboard, which their agents take turns with; Pause/Break, which stops them all; the
/// user's typing, which holds them all; and the chats, each open in one window at a time.
/// </summary>
public sealed class ChatWindows
{
    private readonly AppPaths _paths;
    private readonly IClassicDesktopStyleApplicationLifetime _desktop;

    /// <summary>The windows open, oldest first. Replaced whole, never changed: the agents' threads read it.</summary>
    private volatile (MainWindowViewModel Vm, MainWindow Window)[] _open = [];

    /// <summary>Closed windows still stopping their MCP and SSH servers.</summary>
    private readonly List<Task> _ending = [];

    internal ChatWindows(AppPaths paths, IPlatformServices platform, IClassicDesktopStyleApplicationLifetime desktop)
    {
        _paths = paths;
        _desktop = desktop;
        Platform = platform;
        Saver = new ChatSaver(new ChatStore(paths.ChatsDir));
        // One hook for every window: one press stops every run. A hook each, the first that stopped
        // its run swallowed the press, and the others never saw it.
        platform.StopKey.Watch(StopAll);
        // A ComfyUI an earlier BlackBox started and left at work is closed once its jobs are done.
        Core.Comfy.ComfyHub.For(paths.ComfyDir).CloseLeftover();
    }

    public IPlatformServices Platform { get; }

    /// <summary>Whose turn it is with the mouse and keyboard.</summary>
    public DesktopTurn DesktopTurn { get; } = new();

    /// <summary>Writes every window's chats, so one opening a chat another just left waits for its last version.</summary>
    public ChatSaver Saver { get; }

    /// <summary>The windows' chats and agents, oldest first.</summary>
    public IEnumerable<MainWindowViewModel> All => _open.Select(o => o.Vm);

    /// <summary>
    /// Opens another chat window: where BlackBox was last closed for the first one, a step down and
    /// to the right of the window in front for the next.
    /// </summary>
    public MainWindow Open()
    {
        // Settings as they are now: a model another window picked meanwhile.
        var store = new SettingsStore(_paths);
        var settings = store.Load();
        var vm = new MainWindowViewModel(_paths, store, settings, this);
        var window = new MainWindow { DataContext = vm };
        window.Restore(settings.Window, Front()?.Window);
        _open = [.. _open, (vm, window)];
        window.Closed += (_, _) => Closed(vm);
        window.Show();
        window.Activate();
        vm.OnStartup();
        return window;
    }

    /// <summary>The window the user was in last: the active one, or else the one opened last.</summary>
    public (MainWindowViewModel Vm, MainWindow Window)? Front()
    {
        var open = _open;
        return open.Length == 0 ? null : open.FirstOrDefault(o => o.Window.IsActive) is { Window: not null } active ? active : open[^1];
    }

    /// <summary>The window that has <paramref name="chatId"/> open, but for <paramref name="except"/>.</summary>
    public MainWindowViewModel? WithChat(string chatId, MainWindowViewModel except) =>
        _open.FirstOrDefault(o => o.Vm != except && o.Vm.ChatId == chatId).Vm;

    /// <summary>Brings <paramref name="vm"/>'s window to the front, restored if it was minimized.</summary>
    public void BringForward(MainWindowViewModel vm)
    {
        if (_open.FirstOrDefault(o => o.Vm == vm).Window is not { } window)
            return;
        if (window.WindowState == WindowState.Minimized)
            window.WindowState = WindowState.Normal;
        window.Activate();
    }

    /// <summary>The user is writing a message to one of the agents, in any window: every agent's clicks and key presses wait.</summary>
    public bool UserComposing() => _open.Any(o => o.Vm.TypingHere());

    /// <summary>Pause/Break, from the hook's thread: stops every run; false when none was going.</summary>
    private bool StopAll()
    {
        var stopped = false;
        foreach (var (vm, _) in _open)
            stopped |= vm.StopByKey();
        return stopped;
    }

    private void Closed(MainWindowViewModel vm)
    {
        var open = _open;
        if (open.All(o => o.Vm != vm))
            return; // shut down with BlackBox already
        _open = [.. open.Where(o => o.Vm != vm)];
        _ending.Add(vm.Shutdown());
        if (_open.Length == 0)
        {
            SingleInstance.Ending();
            _desktop.Shutdown();
        }
    }

    /// <summary>BlackBox ends: what the windows handed over is written, and their servers stopped, for a few seconds at most.</summary>
    internal void Exit()
    {
        SingleInstance.Ending();
        var open = _open;
        _open = [];
        foreach (var (vm, _) in open)
            _ending.Add(vm.Shutdown());
        Saver.Flush(TimeSpan.FromSeconds(10));
        // A server that would not stop doesn't keep the process; it ends anyway.
        try { Task.WaitAll([.. _ending], TimeSpan.FromSeconds(5)); }
        catch (AggregateException) { }
    }
}
