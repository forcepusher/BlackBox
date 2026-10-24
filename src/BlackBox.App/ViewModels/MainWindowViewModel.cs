using System.Collections.ObjectModel;
using Avalonia.Threading;
using BlackBox.Core.Agent;
using BlackBox.Core.Chats;
using BlackBox.Core.Llm;
using BlackBox.Core.Mcp;
using BlackBox.Core.Platform;
using BlackBox.Core.Settings;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BlackBox.App.ViewModels;

/// <summary>What the last server check found; drawn as the coloured dot in the top bar.</summary>
public enum ServerState { Unknown, Checking, Connected, Failed }

/// <summary>A level in the effort dropdown ("low" to "max"), shown as "Medium effort".</summary>
public sealed record EffortOption(string Level)
{
    public override string ToString() => Efforts.Label(Level);
}

public sealed partial class MainWindowViewModel : ObservableObject, IAgentObserver
{
    private readonly AppPaths _paths;
    private readonly SettingsStore _store;
    private readonly IPlatformServices _platform;
    private readonly ChatWindows _windows;
    private readonly HttpClient _http = LlmHttp.Create(Timeout.InfiniteTimeSpan);
    private List<ChatMessage> _history = [];
    private readonly ChatStore _chats;
    private readonly ChatSaver _chatSaver;
    private readonly McpHost _mcp = new();
    /// <summary>The chat's SSH logins: they last from one message to the next, and end with the chat.</summary>
    private Core.Ssh.SshHosts _ssh;
    private Task _mcpConnect = Task.CompletedTask;
    private bool _mcpRestartPending;

    /// <summary>
    /// The window has closed. BlackBox goes on with its other windows, so nothing here may start
    /// again: no run, no MCP servers, no chat saved over what another window opened meanwhile.
    /// </summary>
    private bool _closed;
    private AppSettings _settings;
    private CancellationTokenSource? _runCts;
    private AgentSession? _session;
    private AssistantMessageItem? _current;
    private volatile bool _stoppedByKey;

    /// <summary>Ticks once a second during a run, for the reply in progress to count the wait, and a tool call that runs long its time.</summary>
    private readonly DispatcherTimer _tick;

    /// <summary>Decoders and speakers for the previews of files in the chat.</summary>
    private readonly PreviewServices _previews;

    /// <param name="windows">BlackBox's chat windows, this one among them, with what they share.</param>
    public MainWindowViewModel(AppPaths paths, SettingsStore store, AppSettings settings, ChatWindows windows)
    {
        _paths = paths;
        _store = store;
        _settings = settings;
        _windows = windows;
        _platform = windows.Platform;
        _previews = new PreviewServices(Core.Media.MediaDecoders.Create(_platform, paths, Core.Comfy.ComfyHub.For(paths.ComfyDir)), _platform.CreateAudioOutput);
        _chats = new ChatStore(paths.ChatsDir);
        _chatSaver = windows.Saver;
        _ssh = new Core.Ssh.SshHosts(paths.KnownHostsFile);
        _tick = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) =>
        {
            _current?.Tick();
            foreach (var tool in Items.OfType<ToolCallItem>())
                tool.Tick();
            // The pause ends a while after the last key press, with no change to the text.
            OnTypingPauseChanged();
        });
        // The saved model is in the list before the view binds to it, so the dropdown starts
        // with it selected; a selection the ComboBox once lost is not restored by rebinding.
        if (!string.IsNullOrEmpty(settings.Server.Model))
            Models.Add(settings.Server.Model);
        Attachments.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasAttachments));
            SendCommand.NotifyCanExecuteChanged();
        };
        Queued.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasQueued));
            OnPropertyChanged(nameof(QueuedTitle));
            InterruptCommand.NotifyCanExecuteChanged();
        };
        _selectedToolkit = ToolkitOptions[0];
        UseChanges(new Core.Diffs.FileChanges());
    }

    public ObservableCollection<ChatItemViewModel> Items { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    private string _input = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand), nameof(StopCommand), nameof(InterruptCommand))]
    [NotifyPropertyChangedFor(nameof(InputHint), nameof(TypingPause), nameof(TypingPauseText))]
    private bool _isBusy;

    [ObservableProperty]
    private string _serverStatusText = "Checking…";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsChecking), nameof(IsConnected), nameof(IsFailed))]
    private ServerState _serverState;

    public bool IsChecking => ServerState == ServerState.Checking;
    public bool IsConnected => ServerState == ServerState.Connected;
    public bool IsFailed => ServerState == ServerState.Failed;

    /// <summary>Shown when no server/model is configured or the server can't be reached.</summary>
    [ObservableProperty]
    private bool _showSetupBanner;

    [ObservableProperty]
    private string _setupText = "";

    /// <summary>How full the model's context is, next to the server status: "35k of 100k used". Empty until a run's first step.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasContextUsage))]
    private string _contextUsage = "";

    [ObservableProperty]
    private string _contextUsageTip = "";

    public bool HasContextUsage => ContextUsage.Length > 0;

    /// <summary>
    /// The intro on what BlackBox can do, shown until the chat is used: a message sent or a saved
    /// chat opened. Notices don't count (an MCP server that failed to start): they show above it.
    /// </summary>
    [ObservableProperty]
    private bool _showIntro = true;

    /// <summary>Whether the model in use takes images; without them the agent has no screen tools, which the intro says.</summary>
    public bool CanSeeScreen => _settings.Server.SupportsVision;

    /// <summary>The intro's line on what BlackBox asks before it acts, after "Ask me first" in Settings.</summary>
    public string ApprovalNote => _settings.Agent.Approval switch
    {
        ApprovalMode.AskAll => "I ask for your OK before every click, key press, command and file change.",
        ApprovalMode.AskRisky => "I ask for your OK before I run a command or change a file.",
        _ => "I don't ask before I act. \"Ask me first\" in Settings changes that.",
    };

    /// <summary>The window is up: from now on the agent's inputs show in the screen corner, and a ring where it clicks.</summary>
    public void ShowInputOverlay()
    {
        _platform.InputOverlay.Duration = TimeSpan.FromSeconds(AgentSettings.InputLogSeconds);
        _platform.InputOverlay.Enabled = true;
    }

    // ---- the full-screen viewer -------------------------------------------------------

    /// <summary>Decoders and speakers, for the full-screen viewer's videos.</summary>
    public PreviewServices Previews => _previews;

    /// <summary>
    /// The chat's pictures and videos from the top, for the full-screen viewer to go through: those
    /// that came with messages, tools' screenshots, and what tools made or replies named.
    /// </summary>
    public List<MediaEntry> MediaInChat()
    {
        var all = new List<MediaEntry>();
        foreach (var item in Items)
        {
            if (item is UserMessageItem message)
                all.AddRange(message.Attachments.Where(a => Viewable(a.Path)).Select(a => new MediaEntry(a, a.Path, null, a.FileName)));
            if (item is ToolCallItem { ScreenshotData: { } shot } tool)
                all.Add(new MediaEntry(tool, null, shot, $"Screenshot, {tool.TimeText}"));
            all.AddRange(item.Previews.Where(p => Viewable(p.Path)).Select(p => new MediaEntry(p, p.Path, null, p.FileName)));
        }
        return all;
    }

    /// <summary>A picture, animation or video the full-screen viewer shows.</summary>
    public static bool Viewable(string path) => Core.Media.MediaPreview.KindOf(path) is "image" or "animation" or "video" && !LocalFolder(path);

    /// <summary>
    /// A folder named like a picture, looked for on this PC's own drives only: on the window's
    /// thread, a path on a share whose server was off held the window for half a minute.
    /// </summary>
    private static bool LocalFolder(string path)
    {
        try
        {
            return !path.StartsWith(@"\\", StringComparison.Ordinal) && Path.GetPathRoot(path) is { Length: > 0 } root
                   && new DriveInfo(root).DriveType is DriveType.Fixed or DriveType.Removable && Directory.Exists(path);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public void OnStartup()
    {
        Items.CollectionChanged += (_, _) => ShowIntro = Items.All(i => i is NoticeItem);
        foreach (var header in _chats.List())
            PastChats.Add(NewRow(header));
        OnPropertyChanged(nameof(HasPastChats));
        _ = CheckServerAsync();
        _mcpConnect = ConnectMcpAsync();
    }

    /// <summary>
    /// Stops the run, and the SSH and MCP servers; called when the window closes. The chat goes to
    /// the shared <see cref="ChatSaver"/>, which BlackBox waits for before it ends.
    /// </summary>
    /// <returns>The servers stopping, all at the same time: one after the other, each one that
    /// didn't answer kept the process alive five seconds more.</returns>
    public Task Shutdown()
    {
        if (_closed)
            return Task.CompletedTask;
        _closed = true;
        // A chat is saved at every step and when its run ends, so only one in progress needs it here;
        // what the stopped run still reports is dropped, as the chat may be opened in another window.
        // Not a run that is ending for a chat this window has left: the chat on screen never ran.
        if (IsBusy && !_runDetached)
        {
            // What was still waiting above the box: kept in the chat's words, not lost with the box.
            // Not one the agent has just taken: it is in the conversation, read.
            foreach (var queued in Queued.Where(q => _session is not { } running || _steerPending.Contains(q.Input) || running.Unsteer(q.Input)))
                AddNotice($"Not sent, as the window was closed first: \"{queued.Input.Text}\"" + (queued.Input.Via is { } via ? $" ({via})." : "."), isError: false);
            // Said in the chat, with Continue: opened from History, it ended without a word. Before
            // the model had the message, Continue sends it again: "Continue." alone has no task.
            AddStopNotice("Stopped: the window was closed.", isError: false, _unread);
            SaveChat(SnapshotHistory());
            _runDetached = true;
        }
        if (_settingsSave?.IsEnabled == true)
        {
            _settingsSave.Stop();
            SaveSettings(); // what was typed just before closing
        }
        _runCts?.Cancel();
        _tick.Stop();
        // The toolkit's panel goes with the window: its players, the watch on its folder.
        Toolkit?.Dispose();
        // A sound playing in the chat stops with it.
        foreach (var item in Items)
            item.DisposePreviews();
        ClearAttachments();
        return Task.WhenAll(Task.Run(_ssh.Dispose), _mcp.DisposeAsync().AsTask());
    }

    // ---- files going with the next message ------------------------------------------

    /// <summary>Files for the next message: dropped on the window, pasted or picked with the paperclip. Shown under the box until it is sent.</summary>
    public ObservableCollection<AttachmentItem> Attachments { get; } = [];

    public bool HasAttachments => Attachments.Count > 0;

    /// <summary>Files are being dragged over the window: it shows where they will go.</summary>
    [ObservableProperty]
    private bool _isDropping;

    /// <summary>Adds files (or folders) to the next message; one that is there already isn't added twice.</summary>
    public void Attach(IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            string full;
            try { full = Path.GetFullPath(path); }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { continue; }
            if (!Attachments.Any(a => string.Equals(a.Path, full, StringComparison.OrdinalIgnoreCase)))
                Attachments.Add(new AttachmentItem(full, RemoveAttachment));
        }
    }

    private void RemoveAttachment(AttachmentItem item)
    {
        Attachments.Remove(item);
        item.Dispose();
    }

    private void ClearAttachments()
    {
        foreach (var item in Attachments)
            item.Dispose();
        Attachments.Clear();
    }

    /// <summary>
    /// A picture from the clipboard or dragged in without a file (a screenshot, a picture copied in
    /// a browser): saved as a PNG in Data/pasted, since the agent works on files, and attached like a
    /// dropped one.
    /// </summary>
    public void AttachPicture(Avalonia.Media.Imaging.Bitmap picture) => SavePasted(path => picture.Save(path));

    /// <inheritdoc cref="AttachPicture(Avalonia.Media.Imaging.Bitmap)"/>
    public void AttachPicture(byte[] png) => SavePasted(path => File.WriteAllBytes(path, png));

    private void SavePasted(Action<string> write)
    {
        try
        {
            Directory.CreateDirectory(_paths.PastedDir);
            var stamp = DateTime.Now.ToString("yyyy-MM-dd HH-mm-ss", System.Globalization.CultureInfo.InvariantCulture);
            var path = Path.Combine(_paths.PastedDir, $"Pasted {stamp}.png");
            for (var n = 2; File.Exists(path); n++)
                path = Path.Combine(_paths.PastedDir, $"Pasted {stamp} ({n}).png");
            write(path);
            Attach([path]);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AddNotice($"The pasted picture could not be saved: {ex.Message}", isError: true, ex);
        }
    }

    // ---- commands -------------------------------------------------------------------

    private bool CanSend() => !string.IsNullOrWhiteSpace(Input) || Attachments.Count > 0;

    /// <summary>Messages sent while a run was still starting up (no session yet); handed to it once it has one.</summary>
    private readonly List<UserInput> _steerPending = [];

    /// <summary>
    /// Messages sent in the chat on screen while the run of a chat just left (New chat, or another
    /// one opened) was still ending: they start this chat's run once it has. Handed to the ending
    /// run, they were thrown away with it, the bubble left in the chat and nothing run.
    /// </summary>
    private readonly List<UserInput> _forNextRun = [];

    /// <summary>
    /// Sends what is in the box, with the files under it. With no run going it starts one; during a
    /// run it goes to the agent, which reads it before its next step (see <see cref="AgentSession.Steer(UserInput)"/>).
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanSend), AllowConcurrentExecutions = true)]
    private async Task SendAsync()
    {
        // A piece picked in the toolkit's panel that isn't made yet has no file: the tool says what it is.
        var files = Attachments.Where(a => !a.Picked || File.Exists(a.Path) || Directory.Exists(a.Path)).Select(a => a.Path).ToList();
        var input = WithToolkit(new UserInput(Input.Trim(), files), Picked);
        Input = "";
        ClearAttachments();
        await SendAsync(input);
    }

    private async Task SendAsync(UserInput input)
    {
        if (_closed)
            return;
        if (IsBusy)
        {
            // Above the box until the model reads it; then into the chat, where it reads it.
            Queued.Add(new QueuedMessageItem(input, CancelQueued));
            if (_runDetached) _forNextRun.Add(input);
            else if (_session is { } running) running.Steer(input);
            else _steerPending.Add(input);
            return;
        }
        Items.Add(new UserMessageItem(input.Text, input.Files) { Via = input.Via, Context = input.Context });
        SaveChat(SnapshotHistory());
        // What arrived just as a run ended, too late for it, starts the next one.
        for (UserInput? next = input; next is not null;)
            next = await RunAsync(next);
    }

    // ---- messages sent during a run, until the model reads them ---------------------

    /// <summary>
    /// Messages sent during the run that the model hasn't read yet, above the box: × takes one back,
    /// Interrupt has them read at once. Each joins the chat when the model reads it. In the chat from
    /// the moment they were sent, they stood above the work the model went on with before it read them.
    /// </summary>
    public ObservableCollection<QueuedMessageItem> Queued { get; } = [];

    public bool HasQueued => Queued.Count > 0;

    public string QueuedTitle => Queued.Count == 1 ? "I read this before my next step:" : $"I read these {Queued.Count} before my next step:";

    /// <summary>A queued message the model has now, or one that starts the next run: from above the box into the chat.</summary>
    private void MoveIntoChat(UserInput input)
    {
        var queued = Queued.FirstOrDefault(q => ReferenceEquals(q.Input, input));
        if (queued is not null)
            Queued.Remove(queued);
        Items.Add(new UserMessageItem(input.Text, input.Files) { At = queued?.SentAt ?? DateTime.UtcNow, Via = input.Via, Context = input.Context });
    }

    /// <summary>× on a queued message: taken back unsent, unless the model has just read it, and then it joins the chat as any other.</summary>
    private void CancelQueued(QueuedMessageItem item)
    {
        var input = item.Input;
        var taken = _steerPending.RemoveAll(i => ReferenceEquals(i, input)) > 0
                    || _forNextRun.RemoveAll(i => ReferenceEquals(i, input)) > 0
                    || _session?.Unsteer(input) == true;
        if (taken)
            Queued.Remove(item);
    }

    private bool CanInterrupt() => IsBusy && HasQueued;

    /// <summary>
    /// Interrupt, over the queued messages (or Ctrl+Enter): the model stops thinking, writing or
    /// running a tool, and reads them now. While the run is still starting there is nothing to stop:
    /// its first step reads them.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanInterrupt))]
    private void Interrupt() => _session?.Interrupt();

    /// <summary>Ctrl+Enter: sends what is in the box, and during a run has it read at once.</summary>
    public void SendNow()
    {
        if (!SendCommand.CanExecute(null))
            return;
        SendCommand.Execute(null);
        if (InterruptCommand.CanExecute(null))
            InterruptCommand.Execute(null);
    }

    /// <summary>What Continue sends after a stop: the model reads the conversation up to the stop, and carries on from there.</summary>
    private const string ContinueText = "Continue.";

    /// <summary>
    /// The Continue button on the notice a stopped run leaves: the task goes on where it stopped, as if
    /// "Continue." had been typed. After a stop there was no way back to the task but writing that by
    /// hand. What is in the box stays there.
    /// </summary>
    private void CarryOn()
    {
        if (IsBusy)
            return;
        _ = SendAsync(new UserInput(ContinueText));
    }

    /// <summary>
    /// The notice a run leaves when it ends before the task is done, with Continue on it. Ended
    /// before the model had the message at all (stopped while the model loaded, no model picked, a
    /// server that didn't answer), Continue runs that message again instead: "Continue." alone
    /// would reach the model without the task.
    /// </summary>
    private void AddStopNotice(string text, bool isError, UserInput? unsent = null, Exception? error = null)
    {
        if (isError)
            Core.ErrorLog.Write(ChatPlace(), text, error);
        Items.Add(unsent is null
            ? new NoticeItem(text, isError, CarryOn)
            : new NoticeItem(text, isError, () => _ = RunAgainAsync(unsent), unsent: true));
    }

    private async Task RunAgainAsync(UserInput input)
    {
        if (IsBusy)
            return;
        for (UserInput? next = input; next is not null;)
            next = await RunAsync(next);
    }

    /// <summary>Takes Continue off every notice: the chat has gone on.</summary>
    private void HideContinue()
    {
        foreach (var notice in Items.OfType<NoticeItem>())
            notice.CanContinue = false;
    }

    /// <summary>When the user last typed in the box; while they are typing, the agent's mouse and keyboard wait.</summary>
    private DateTime _lastTyped;

    partial void OnInputChanged(string value)
    {
        _lastTyped = DateTime.UtcNow;
        OnTypingPauseChanged();
    }

    private void OnTypingPauseChanged()
    {
        OnPropertyChanged(nameof(TypingPause));
        OnPropertyChanged(nameof(TypingPauseHere));
        OnPropertyChanged(nameof(TypingPauseText));
    }

    /// <summary>How long after the last key press in the box the agent carries on by itself.</summary>
    private static readonly TimeSpan TypingQuiet = TimeSpan.FromSeconds(15);

    /// <summary>The agent's clicks and key presses are on hold because the user is typing to an agent (see <see cref="ChatWindows.UserComposing"/>).</summary>
    public bool TypingPause => IsBusy && _windows.UserComposing();

    /// <summary>The pause is for typing in this window's box, which Resume can end; typing in another window it can't.</summary>
    public bool TypingPauseHere => TypingHere();

    /// <summary>
    /// What the pause line says: that the agent waits, and the three ways it goes on again, with
    /// the seconds left until it does by itself. Someone who started typing and changed their mind
    /// had no way to tell how to let it carry on.
    /// </summary>
    public string TypingPauseText => TypingHere()
        ? $"Paused while you type: my clicks and key presses wait. I carry on when you send this (Enter), click Resume, or in {Math.Max(1, (int)Math.Ceiling((TypingQuiet - (DateTime.UtcNow - _lastTyped)).TotalSeconds))} s without a key press."
        : "Paused while you type in another BlackBox window: my clicks and key presses wait until you send it there, or stop typing for 15 s.";

    /// <summary>Lets the agent carry on at once; what was typed stays in the box, to finish and send later.</summary>
    [RelayCommand]
    private void Resume()
    {
        _lastTyped = DateTime.MinValue;
        OnTypingPauseChanged();
    }

    /// <summary>
    /// The user is writing a message in this window's box: text in it, typed in the last 15
    /// seconds. Every window's agent waits with its clicks and key presses meanwhile, so neither
    /// ends up in the other's window. Read from the agents' threads.
    /// </summary>
    internal bool TypingHere() => Input.Length > 0 && DateTime.UtcNow - _lastTyped < TypingQuiet;

    /// <summary>The hint in the empty box: what Enter does now, and that pictures and files can go in too.</summary>
    public string InputHint => IsBusy
        ? "Tell me something while I work… (Enter to send; I pause while you type)"
        : "Describe a task, paste a screenshot or drop files… (Enter to send, Shift+Enter for a new line)";

    /// <summary>When the run in progress started: from the message that started it, for how long the run took.</summary>
    private DateTime _runStarted;

    /// <summary>The message the run in progress started with, until the model has it.</summary>
    private UserInput? _unread;

    /// <summary>
    /// A run's last save is waiting for its last updates to reach the chat. Another chat picked
    /// meanwhile (a click comes before it) saves it first: it was skipped, and the chat lost its end.
    /// </summary>
    private bool _lastSavePending;

    /// <summary>One run of the agent on <paramref name="input"/>. Returns what the user sent too late for it, to run next, or null.</summary>
    private async Task<UserInput?> RunAsync(UserInput input)
    {
        _runStarted = DateTime.UtcNow;
        _runDetached = false;
        IsBusy = true;
        HideContinue();
        // A Pause/Break that came as the last run was ending stopped nothing, and its mark called
        // this run's next stop with the Stop button "Stopped with Pause/Break".
        _stoppedByKey = false;
        _runCts = new CancellationTokenSource();
        var ct = _runCts.Token;
        // This chat's conversation: picking another chat while the run starts up replaces _history.
        var history = _history;
        List<UserInput> late = [];
        // Whether the model has the message: from the moment the session starts, it is in the conversation.
        var delivered = false;
        _unread = input;

        try
        {
            var server = _settings.Server;
            // The check and the load on one connection: a new one is a round trip more before LM Studio starts loading.
            using (var probeHttp = LlmHttp.Create(TimeSpan.FromMinutes(10)))
            {
                // The server may offer other models than at the last check: the picked one back, or the one in use gone.
                var probe = Core.Llm.ServerAddress.Candidates(server.BaseUrl).Count > 0 ? await ProbeServerAsync(quiet: true, ct, probeHttp) : null;
                // Said with Continue by the catch below, which also gives back what was sent meanwhile:
                // returning here left those messages above the box for good.
                if (Core.Llm.ServerAddress.Candidates(server.BaseUrl).Count == 0 || string.IsNullOrWhiteSpace(server.ModelInUse))
                    throw new InvalidOperationException("Enter your model server's address and pick a model at the top first, then press Continue.");
                // LM Studio loads models on first use; do that now so we know the real context size.
                // What the check just said of the model isn't asked again. Not into a chat opened
                // since: the run is then one ending for the chat left.
                var facts = await ServerProbe.EnsureLoadedAsync(probeHttp, server,
                    () => Dispatcher.UIThread.Post(() => { if (!_runDetached) AddNotice("Loading the model on the server…", isError: false); }), ct,
                    note => Dispatcher.UIThread.Post(() => { if (!_runDetached) AddNotice(note, isError: false); }),
                    known: probe is not null && probe.Model == server.ModelInUse ? probe.Facts : null);
                if (facts.Context is { } context)
                {
                    server.SetDetectedContext(context, facts.Hosted);
                    _modelNotLoaded = false;
                    OnContextChanged();
                    // Ollama and llama-server start models with 4k unless told otherwise; the run
                    // then resets after every step, or fails as too long. Not for a size near 32k
                    // (LM Studio's 32000 is 31k), which works.
                    if (context < ServerProbe.MinimumContext * 3 / 4 && !facts.Hosted && !_runDetached)
                        AddNotice($"The server runs {server.ModelInUse} with a {context / 1024}k context, and BlackBox needs about {ServerProbe.MinimumContext / 1024}k: " +
                                  "its instructions and tools alone take about 9k tokens, and each screenshot about 3k. Load the model with a larger context on the server " +
                                  "(LM Studio: Context Length when you load it; Ollama: the Context length setting; llama-server: -c).", isError: false);
                }
            }
            if (!_mcpConnect.IsCompleted)
            {
                if (!_runDetached) AddNotice("Starting the MCP servers…", isError: false);
                await _mcpConnect.WaitAsync(ct);
            }

            var session = _session = AgentSetup.Create(BackendConnection.Create(_http, server), _platform, _settings.Agent, _paths, _mcp, _ssh, history,
                _windows.UserComposing, _windows.DesktopTurn, desktopSeat: this, changes: Changes);
            // Taken now: another model can be picked at the top while the run goes on.
            var imagesKey = session.SeesImages ? ImagesKey(server) : null;
            foreach (var pending in _steerPending)
                session.Steer(pending);
            _steerPending.Clear();
            _tick.Start();
            delivered = true;
            _unread = null;
            await Task.Run(() => session.RunAsync(input, this, ct), CancellationToken.None);
            // A server error may have revealed the real context size; keep it for the next run.
            _settings.Server.LearnContextLimit(session.ContextTokens);
            OnContextChanged();
            // Its server refused pictures: the next runs go without screen tools, as with a model
            // the server says can't see.
            if (imagesKey is not null && !session.SeesImages)
            {
                _imagesRefused = imagesKey;
                if (ImagesKey(server) == imagesKey)
                {
                    server.SupportsVision = false;
                    OnPropertyChanged(nameof(CanSeeScreen));
                }
            }
        }
        // Stop, Pause/Break or leaving the chat; a request that timed out is an error like any other.
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            var stopped = StoppedText();
            if (!_runDetached) AddStopNotice(stopped, isError: false, delivered ? null : input);
        }
        catch (Exception ex)
        {
            // The run itself never throws; this is the preparation (an address that isn't one, a
            // server that doesn't answer), so Continue tries the message again.
            if (!_runDetached) AddStopNotice(ex.Message, isError: true, delivered ? null : input, ex);
        }
        finally
        {
            late = [.. _session?.TakeUndelivered() ?? [], .. _steerPending];
            _steerPending.Clear();
            // After the run's last posted updates (its "Stopped." notice) have reached the chat.
            var chatId = _chatId;
            if (!_runDetached)
            {
                _lastSavePending = true;
                Dispatcher.UIThread.Post(() =>
                {
                    _lastSavePending = false;
                    if (_chatId == chatId) SaveChat(SnapshotHistory());
                }, DispatcherPriority.Background);
            }
            _tick.Stop();
            _session = null;
            IsBusy = false;
            _runCts.Dispose();
            _runCts = null;
            if (_mcpRestartPending && !_closed)
            {
                // Changed in Settings during the run; restarting then would have pulled the tools from under it.
                _mcpRestartPending = false;
                _mcpConnect = ConnectMcpAsync();
            }
        }

        if (_runDetached)
        {
            if (_forNextRun.Count == 0 || _closed)
                return null;
            var next = UserInput.Join(_forNextRun);
            foreach (var message in _forNextRun)
                MoveIntoChat(message);
            _forNextRun.Clear();
            return next;
        }
        if (late.Count == 0)
            return null;
        // Stopped, or ended before the model had the message (the server didn't answer): run as the
        // next task, what was sent meanwhile went to the model without the message it followed,
        // whose Continue that run took away.
        if (ct.IsCancellationRequested || !delivered)
        {
            // Stopped: what the agent hadn't read yet goes back in the box, with its files under it,
            // to send again or not.
            foreach (var unread in late)
                if (Queued.FirstOrDefault(q => ReferenceEquals(q.Input, unread)) is { } item)
                    Queued.Remove(item);
            BackInTheBox(late);
            return null;
        }
        foreach (var message in late)
            MoveIntoChat(message);
        return UserInput.Join(late);
    }

    /// <summary>
    /// Messages the model never read, back in the box with their files, to send again or not. Sent
    /// again while a tool is open, they carry what it says about them again.
    /// </summary>
    private void BackInTheBox(IReadOnlyList<UserInput> unread)
    {
        Input = string.Join("\n\n", unread.Select(u => u.Text).Append(Input).Where(t => t.Length > 0));
        Attach(unread.SelectMany(u => u.Files));
    }

    [RelayCommand(CanExecute = nameof(IsBusy))]
    private void Stop() => _runCts?.Cancel();

    /// <summary>Pause/Break, from the hook's thread: stops the run in progress, if any; false when there is none.</summary>
    internal bool StopByKey()
    {
        var cts = _runCts;
        if (cts is null || cts.IsCancellationRequested)
            return false;
        _stoppedByKey = true;
        // What the stop sets off (a tool's cleanup, the end of the run) runs on the thread pool.
        // Cancel() ran it on the hook's thread, which held up every key press on the PC meanwhile;
        // and Windows drops a hook that takes too long, which would leave Pause/Break dead.
        try { _ = cts.CancelAsync(); }
        catch (ObjectDisposedException) { return false; } // the run ended at that very moment
        return true;
    }

    private string StoppedText()
    {
        var byKey = _stoppedByKey;
        _stoppedByKey = false;
        return byKey ? "Stopped with Pause/Break." : "Stopped.";
    }

    [RelayCommand]
    private void NewChat()
    {
        LeaveChat(save: true);
        MarkCurrentChat();
    }

    /// <summary>
    /// New window (or starting BlackBox again): a window of its own with its own chat and agent, for
    /// another task at the same time. The agents take turns with the mouse and keyboard.
    /// </summary>
    [RelayCommand]
    private void NewWindow() => _windows.Open();
}
