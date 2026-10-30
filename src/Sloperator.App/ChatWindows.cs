using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Sloperator.App.ViewModels;
using Sloperator.App.Views;
using Sloperator.Core.Chats;
using Sloperator.Core.Platform;
using Sloperator.Core.Settings;

namespace Sloperator.App;

/// <summary>
/// Sloperator's chat windows: one for each task the user has going side by side, each with its own
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
        // A ComfyUI an earlier Sloperator started and left at work is closed once its jobs are done.
        Core.Comfy.ComfyHub.For(paths.ComfyDir).CloseLeftover();
        Core.Media.MediaRelease.Releasing += LetGo;
#if WINDOWS
        Updater = new Updater(AnyBusy, paths.UpdateCheckFile, paths.UpdateRejectedFile, Sloperator.Platform.Windows.WindowsSignature.Of);
#else
        Updater = new Updater(AnyBusy, paths.UpdateCheckFile, paths.UpdateRejectedFile, _ => Sloperator.Core.Web.Signature.None);
#endif
    }

    /// <summary>
    /// A file about to be written over (a shot made again, the video joined again): every window's
    /// players of it stop, and the writer waits until they have let go of it. From the writer's
    /// thread; the players are found on the window's.
    /// </summary>
    private void LetGo(string path)
    {
        var playing = new List<IDisposable>();
        void Find()
        {
            foreach (var (vm, _) in _open)
                playing.AddRange(vm.LetGo(path));
        }
        if (Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
            Find();
        else
            Avalonia.Threading.Dispatcher.UIThread.Invoke(Find);
        foreach (var playback in playing)
            playback.Dispose();
    }

    public IPlatformServices Platform { get; }

    /// <summary>Whose turn it is with the mouse and keyboard.</summary>
    public DesktopTurn DesktopTurn { get; } = new();

    /// <summary>Writes every window's chats, so one opening a chat another just left waits for its last version.</summary>
    public ChatSaver Saver { get; }

    /// <summary>Sloperator updating itself, for every window.</summary>
    public Updater Updater { get; }

    /// <summary>
    /// Opens another chat window: where Sloperator was last closed for the first one, a step down and
    /// to the right of the window in front for the next. With <paramref name="chatId"/>, that saved
    /// chat opens in it.
    /// </summary>
    public MainWindow Open(string? chatId = null)
    {
        // Settings as they are now: a model another window picked meanwhile.
        var store = new SettingsStore(_paths);
        var settings = store.Load();
        var vm = new MainWindowViewModel(_paths, store, settings, this);
        var window = new MainWindow { DataContext = vm };
        window.Restore(settings.Window, Front()?.Window);
        _open = [.. _open, (vm, window)];
        _notifyTags[vm] = $"window-{++_opened}";
        // In front, the window says what its notification said: that one has nothing more to tell.
        window.Activated += (_, _) => HideNotification(vm);
        window.Closed += (_, _) => Closed(vm);
        window.Show();
        window.Activate();
        vm.OnStartup();
        if (chatId is { Length: > 0 })
            vm.OpenSaved(chatId);
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

    // ---- Notify me when done ------------------------------------------------------------

    /// <summary>The tag each window's notification goes under, so its next one replaces it. On the windows' thread.</summary>
    private readonly Dictionary<MainWindowViewModel, string> _notifyTags = [];

    private int _opened;

    /// <summary>The notifications shown and taken down, one after another, off the windows' thread.</summary>
    private Task _notifying = Task.CompletedTask;

    private string? _notifyIcon;

    /// <summary>
    /// <paramref name="vm"/>'s run ended or waits for the user's OK: a Windows notification says so,
    /// with <paramref name="title"/> (the chat's) over <paramref name="line"/>, unless the user is
    /// looking at that window already. A click on it brings the window forward. One per window: its
    /// next one replaces it, and it goes once the window is in front.
    /// </summary>
    public void Notify(MainWindowViewModel vm, string title, string line)
    {
        if (!_notifyTags.TryGetValue(vm, out var tag) || _open.FirstOrDefault(o => o.Vm == vm).Window is not { } window)
            return;
        if (window.IsActive && window.IsVisible && window.WindowState != WindowState.Minimized)
            return;
        var icon = NotifyIcon();
        Notifications(n => n.Show(tag, title, line, icon, () => Avalonia.Threading.Dispatcher.UIThread.Post(() => BringForward(vm))));
    }

    private void HideNotification(MainWindowViewModel vm)
    {
        if (_notifyTags.TryGetValue(vm, out var tag))
            Notifications(n => n.Hide(tag));
    }

    /// <summary>"Notify me when done" turned off: the notifications still up go, and what they left in the registry.</summary>
    public void NotificationsOff() => Notifications(n => n.Forget());

    private void Notifications(Action<INotifications> act)
    {
        if (Platform.Notifications is { } notifications)
            _notifying = _notifying.ContinueWith(_ => act(notifications), TaskScheduler.Default);
    }

    /// <summary>Sloperator's picture for the notifications, as a file Windows can read: Data\sloperator.png, written once a start.</summary>
    private string? NotifyIcon()
    {
        if (_notifyIcon is not null)
            return _notifyIcon;
        var file = Path.Combine(_paths.DataDir, "sloperator.png");
        try
        {
            using var asset = Avalonia.Platform.AssetLoader.Open(new Uri("avares://Sloperator/Assets/sloperator.png"));
            using var bytes = new MemoryStream();
            asset.CopyTo(bytes);
            if (!File.Exists(file) || !File.ReadAllBytes(file).AsSpan().SequenceEqual(bytes.GetBuffer().AsSpan(0, (int)bytes.Length)))
                File.WriteAllBytes(file, bytes.ToArray());
            return _notifyIcon = file;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null; // Windows' blank picture then
        }
    }

    /// <summary>Settings saved in <paramref name="from"/>'s Settings dialog: the other windows take them too.</summary>
    public void SettingsApplied(MainWindowViewModel from, AppSettings settings, bool mcpChanged)
    {
        Updater.Automatic = settings.Updates.Automatic;
        foreach (var (vm, _) in _open)
            if (vm != from)
                vm.TakeSettings(settings, mcpChanged);
    }

    /// <summary>The user is writing a message to one of the agents, in any window: every agent's clicks and key presses wait.</summary>
    public bool UserComposing() => _open.Any(o => o.Vm.TypingHere());

    // ---- Updates ----------------------------------------------------------------------

    /// <summary>The argument a restart into an update gives the new exe: the chats the windows had open, in the order they opened.</summary>
    private const string ReopenArgument = "--reopen";

    /// <summary>A run is going in one of the windows.</summary>
    public bool AnyBusy() => _open.Any(o => o.Vm.IsBusy);

    /// <summary>A window's run started or ended: Restart is for when none runs, and a check that came due waits for that.</summary>
    public void BusyChanged()
    {
        foreach (var (vm, _) in _open)
            vm.RestartCommand.NotifyCanExecuteChanged();
        if (!AnyBusy())
            Updater.Idle();
    }

    /// <summary>
    /// The chats a restart into an update asked to open again, one window each, in its arguments
    /// (empty for a window that had none); empty for any other start.
    /// </summary>
    public static IReadOnlyList<string> ChatsToReopen(string[]? args) =>
        args is [ReopenArgument, var chats, ..] ? chats.Split(',') : [];

    /// <summary>
    /// Restarts into the version the update downloaded: it goes in place of this exe, starts with
    /// the windows' chats to open again, and this Sloperator ends. The two never talk: the new build
    /// isn't this one, so it doesn't hand its start to this one (see <see cref="SingleInstance"/>).
    /// </summary>
    public void Restart(MainWindowViewModel from)
    {
        if (AnyBusy() || Environment.ProcessPath is not { } exe)
            return;
        var chats = string.Join(",", _open.Select(o => o.Vm.ChatId ?? ""));
        // The new one reads the chats as this one last saved them.
        Saver.Flush(TimeSpan.FromSeconds(10));
        if (Updater.Swap() is { } failed)
        {
            from.AddNotice($"The update couldn't be put in place, so Sloperator goes on as it is: {failed}", isError: true);
            return;
        }
        try
        {
            var start = new System.Diagnostics.ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe)! };
            start.ArgumentList.Add(ReopenArgument);
            start.ArgumentList.Add(chats);
            System.Diagnostics.Process.Start(start)?.Dispose();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            Updater.Unswap();
            from.AddNotice($"The new version didn't start, so Sloperator goes on as it is: {ex.Message}", isError: true, ex);
            return;
        }
        _desktop.Shutdown();
    }

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
            return; // shut down with Sloperator already
        _open = [.. open.Where(o => o.Vm != vm)];
        // A click on it would find no window to bring forward.
        HideNotification(vm);
        _notifyTags.Remove(vm);
        _ending.Add(vm.Shutdown());
        if (_open.Length == 0)
        {
            SingleInstance.Ending();
            _desktop.Shutdown();
        }
    }

    /// <summary>Sloperator ends: what the windows handed over is written, and their servers stopped, for a few seconds at most.</summary>
    internal void Exit()
    {
        SingleInstance.Ending();
        var open = _open;
        _open = [];
        foreach (var (vm, _) in open)
        {
            HideNotification(vm);
            _ending.Add(vm.Shutdown());
        }
        _ending.Add(_notifying);
        Saver.Flush(TimeSpan.FromSeconds(10));
        // Not restarted into the version an update downloaded: it is in place for the next start.
        Updater.SwapOnExit();
        // A server that would not stop doesn't keep the process; it ends anyway.
        try { Task.WaitAll([.. _ending], TimeSpan.FromSeconds(5)); }
        catch (AggregateException) { }
        // The ComfyUI a task ended by closing its window had started, when it has nothing to do.
        Core.Comfy.ComfyHub.For(_paths.ComfyDir).CloseOnExit(TimeSpan.FromSeconds(5));
    }
}
