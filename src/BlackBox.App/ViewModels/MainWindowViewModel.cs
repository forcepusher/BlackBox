using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using BlackBox.Core.Agent;
using BlackBox.Core.Chats;
using BlackBox.Core.Llm;
using BlackBox.Core.Mcp;
using BlackBox.Core.Platform;
using BlackBox.Core.Settings;
using BlackBox.Core.Tools;
using BlackBox.Core.Web;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BlackBox.App.ViewModels;

/// <summary>What the last server check found; drawn as the coloured dot in the top bar.</summary>
public enum ServerState { Unknown, Checking, Connected, Failed }

public sealed partial class MainWindowViewModel : ObservableObject, IAgentObserver
{
    private readonly AppPaths _paths;
    private readonly SettingsStore _store;
    private readonly IPlatformServices _platform;
    private readonly HttpClient _http = LlmHttp.Create(Timeout.InfiniteTimeSpan);
    private List<ChatMessage> _history = [];
    private readonly ChatStore _chats;
    private readonly McpHost _mcp = new();
    private Task _mcpConnect = Task.CompletedTask;
    private bool _mcpRestartPending;
    private AppSettings _settings;
    private CancellationTokenSource? _runCts;
    private AssistantMessageItem? _current;
    private IDisposable? _stopKey;
    private volatile bool _stoppedByKey;

    public MainWindowViewModel(AppPaths paths, SettingsStore store, AppSettings settings, IPlatformServices platform)
    {
        _paths = paths;
        _store = store;
        _settings = settings;
        _platform = platform;
        _chats = new ChatStore(paths.ChatsDir);
        // The saved model is in the list before the view binds to it, so the dropdown starts
        // with it selected; a selection the ComboBox once lost is not restored by rebinding.
        if (!string.IsNullOrEmpty(settings.Server.Model))
            Models.Add(settings.Server.Model);
    }

    public ObservableCollection<ChatItemViewModel> Items { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    private string _input = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand), nameof(StopCommand))]
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

    public bool HasNoMessages => Items.Count == 0;

    public void AttachHostWindow(nint handle)
    {
        _platform.HostWindow.Attach(handle, _settings.Agent.HideFromScreenshots);
        _platform.InputOverlay.Duration = TimeSpan.FromSeconds(_settings.Agent.InputLogSeconds);
        _platform.InputOverlay.Enabled = _settings.Agent.ShowInputs;
    }

    public void OnStartup()
    {
        Items.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasNoMessages));
        _stopKey = _platform.StopKey.Watch(OnStopKey);
        foreach (var header in _chats.List())
            PastChats.Add(NewRow(header));
        OnPropertyChanged(nameof(HasPastChats));
        _ = CheckServerAsync();
        _mcpConnect = ConnectMcpAsync();
    }

    /// <summary>Stops the run and the MCP servers; called when the window closes.</summary>
    public void Shutdown()
    {
        // A chat is saved at every step and when its run ends, so only one in progress needs it here.
        if (IsBusy) SaveChat(SnapshotHistory());
        _runCts?.Cancel();
        _stopKey?.Dispose();
        try { _mcp.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(5)); }
        catch (AggregateException) { /* a server that would not stop; the process ends anyway */ }
    }

    // ---- server, API key, model and context size (top bar) ----------------------------

    private CancellationTokenSource? _addressEdit;
    private int _checkVersion;
    private bool _refreshingModels;

    /// <summary>Server address typed in the top bar. Saved as you type; checked once typing pauses.</summary>
    public string ServerAddress
    {
        get => _settings.Server.BaseUrl;
        set
        {
            value = value.Trim();
            if (value == _settings.Server.BaseUrl) return;
            _settings.Server.BaseUrl = value;
            _store.Save(_settings);
            OnPropertyChanged();
            _ = CheckAfterTypingAsync();
        }
    }

    /// <summary>API key typed in the top bar, for servers that want one. Saved as you type; checked once typing pauses.</summary>
    public string ApiKey
    {
        get => _settings.Server.ApiKey;
        set
        {
            value = value.Trim();
            if (value == _settings.Server.ApiKey) return;
            _settings.Server.ApiKey = value;
            _store.Save(_settings);
            OnPropertyChanged();
            _ = CheckAfterTypingAsync();
        }
    }

    /// <summary>The eye button next to the API key: shows the key instead of dots.</summary>
    [ObservableProperty]
    private bool _showApiKey;

    /// <summary>Context size as the server reports it; the slider only shows it, greyed out.</summary>
    public bool AutoContext
    {
        get => _settings.Server.AutoContext;
        set
        {
            var server = _settings.Server;
            if (value == server.AutoContext) return;
            // The slider starts where Auto left it (on a stop), unless a size was picked by hand before.
            if (!value && server.ManualContextSize <= 0)
                server.ManualContextSize = ContextStops[ContextSizes.IndexOf(ContextStops, server.DetectedContextSize)];
            server.AutoContext = value;
            _store.Save(_settings);
            OnPropertyChanged();
            OnContextChanged();
        }
    }

    private IReadOnlyList<int> ContextStops =>
        ContextSizes.Stops(_settings.Server.ContextReported ? _settings.Server.DetectedContextSize : null);

    /// <summary>Last slider position: the server's own size when it reports one, else 1M.</summary>
    public double ContextMaxIndex => ContextStops.Count - 1;

    /// <summary>Slider position, an index into <see cref="ContextStops"/>. A typed size past the last stop sits at the end.</summary>
    public double ContextIndex
    {
        get => ContextSizes.IndexOf(ContextStops, _settings.Server.ContextSize);
        set
        {
            // Only a move to another stop is the user's: on Auto the slider is greyed out, and the
            // slider sends its value back when its range changes or a typed size pins it to the end.
            var server = _settings.Server;
            var stops = ContextStops;
            var index = Math.Clamp((int)Math.Round(value), 0, stops.Count - 1);
            if (server.AutoContext || index == (int)ContextIndex) return;
            SetManualContext(stops[index]);
        }
    }

    /// <summary>
    /// The size box next to the slider. On Auto it shows what the server reports (see
    /// <see cref="ContextText"/>) and can't be edited; otherwise anything typed ("64k", "250000",
    /// "1M") is used as typed, past the slider's end too. Taken when Enter is pressed or focus leaves.
    /// </summary>
    public string ContextInput
    {
        get => ContextText;
        set
        {
            // The box sends its text back whenever it loses focus; text it was showing is no change
            // (256000 shows as "250k", which would read back as 250000).
            if (_settings.Server.AutoContext || value.Trim() == ContextText) return;
            if (ContextSizes.Parse(value) is { } size && size != _settings.Server.ContextSize)
                SetManualContext(size);
        }
    }

    private void SetManualContext(int size)
    {
        _settings.Server.ManualContextSize = size;
        _store.Save(_settings);
        OnContextChanged();
    }

    /// <summary>LM Studio said the model isn't loaded yet; it has no context size until the first message loads it.</summary>
    private bool _modelNotLoaded;

    /// <summary>
    /// The size in use, next to the slider: "64k"; on Auto "not loaded" before LM Studio has loaded
    /// the model, and "98k?" when the server doesn't say and it is the guess.
    /// </summary>
    public string ContextText
    {
        get
        {
            var server = _settings.Server;
            if (!server.AutoContext || server.ContextReported)
                return ContextSizes.Format(server.ContextSize);
            return _modelNotLoaded ? "not loaded" : ContextSizes.Format(server.ContextSize) + "?";
        }
    }

    public string ContextToolTip
    {
        get
        {
            var server = _settings.Server;
            var reported = server.ContextReported ? $"The server runs this model with {server.DetectedContextSize:N0} tokens." : null;
            if (server.AutoContext)
                return reported is not null ? $"{reported} BlackBox uses all of it. Untick Auto to use less."
                    : _modelNotLoaded ? "The model isn't loaded on the server yet, so its context size isn't known. Your first message loads it, and the size shows here."
                    : $"The server doesn't say how large the context is, so BlackBox assumes {server.DetectedContextSize:N0} tokens and goes lower if the server objects.";
            var over = server.ContextReported && server.ContextSize > server.DetectedContextSize
                ? $" That is more than the {server.DetectedContextSize:N0} the server runs this model with: it will refuse requests longer than that, and BlackBox then drops to its size for the rest of the task."
                : "";
            return $"BlackBox uses {server.ContextSize:N0} tokens: it starts a fresh context (with a handoff note) when a fifth is left.{over} "
                   + "Drag the slider or type a size such as 48k, 100000 or 1M. The model on the server is not reloaded.";
        }
    }

    private void OnContextChanged()
    {
        // The range first: the slider clamps its value to it.
        OnPropertyChanged(nameof(ContextMaxIndex));
        OnPropertyChanged(nameof(ContextIndex));
        OnPropertyChanged(nameof(ContextText));
        OnPropertyChanged(nameof(ContextInput));
        OnPropertyChanged(nameof(ContextToolTip));
    }

    /// <summary>Models the server reports (embedding models left out). Always contains the saved one.</summary>
    public ObservableCollection<string> Models { get; } = [];

    public string? SelectedModel
    {
        get => string.IsNullOrEmpty(_settings.Server.Model) ? null : _settings.Server.Model;
        set
        {
            // Refilling the list briefly clears the selection; that is not the user choosing nothing.
            if (_refreshingModels || string.IsNullOrEmpty(value) || value == _settings.Server.Model)
                return;
            _settings.Server.Model = value;
            _store.Save(_settings);
            OnPropertyChanged();
            _ = CheckServerAsync();
        }
    }

    private async Task CheckAfterTypingAsync()
    {
        _addressEdit?.Cancel();
        var edit = _addressEdit = new CancellationTokenSource();
        try { await Task.Delay(700, edit.Token); }
        catch (OperationCanceledException) { return; }
        await CheckServerAsync();
    }

    /// <summary>Asks the server for its models and the chosen model's details; updates the top bar.</summary>
    [RelayCommand]
    private async Task CheckServerAsync()
    {
        var version = ++_checkVersion;
        var server = _settings.Server;
        if (string.IsNullOrWhiteSpace(server.BaseUrl))
        {
            SetStatus(ServerState.Unknown, "Not set up", "Enter the address of your model server (for example LM Studio at http://localhost:1234/v1) at the top.");
            return;
        }

        SetStatus(ServerState.Checking, "Checking…", null);
        using var probeHttp = LlmHttp.Create(TimeSpan.FromSeconds(10));
        var typed = server.BaseUrl;
        var probe = await ServerProbe.CheckAsync(probeHttp, server, CancellationToken.None);
        if (version != _checkVersion)
            return; // the address or model changed while this check was running
        if (server.BaseUrl != typed)
        {
            // "host:1234" became the full URL that answered; show it so there is no doubt.
            _store.Save(_settings);
            OnPropertyChanged(nameof(ServerAddress));
        }

        SetModels(probe.Models);
        if (!probe.Reachable)
        {
            SetStatus(ServerState.Failed, "Offline", $"{probe.Message} Check the address at the top and that the server is running.");
            return;
        }
        if (string.IsNullOrEmpty(server.Model) && Models.Count > 0)
        {
            server.Model = Models[0];
            _store.Save(_settings);
            OnPropertyChanged(nameof(SelectedModel));
            probe = await ServerProbe.CheckAsync(probeHttp, server, CancellationToken.None); // details for this model
        }
        if (string.IsNullOrEmpty(server.Model))
        {
            SetStatus(ServerState.Failed, "No models", "The server is running but offers no models. Load or download one in LM Studio.");
            return;
        }
        if (probe.Models.Count > 0 && !probe.Models.Contains(server.Model))
        {
            SetStatus(ServerState.Failed, "Model not found", $"The server doesn't offer \"{server.Model}\" anymore. Pick another model at the top.");
            return;
        }

        // Trust what the server reports; neither the context size (it shows by the slider) nor image support is saved.
        server.SetDetectedContext(probe.LoadedContext);
        _modelNotLoaded = probe.Loaded == false;
        OnContextChanged();
        server.SupportsVision = probe.Vision ?? true;
        SetStatus(ServerState.Connected, "Connected" + (probe.Vision == false ? " · no vision" : ""), null);
    }

    /// <summary>
    /// Updates the dropdown in place. Clearing it would drop the selection, and the ComboBox
    /// does not pick it up again when the same value is set back; so the chosen model stays in
    /// the list throughout, and only the other entries change.
    /// </summary>
    private void SetModels(IEnumerable<string> fromServer)
    {
        var wanted = fromServer.Where(m => !m.Contains("embed", StringComparison.OrdinalIgnoreCase)).ToList();
        var chosen = _settings.Server.Model;
        if (!string.IsNullOrEmpty(chosen) && !wanted.Contains(chosen))
            wanted.Insert(0, chosen);
        _refreshingModels = true;
        for (var i = Models.Count - 1; i >= 0; i--)
            if (!wanted.Contains(Models[i]))
                Models.RemoveAt(i);
        for (var i = 0; i < wanted.Count; i++)
            if (!Models.Contains(wanted[i]))
                Models.Insert(Math.Min(i, Models.Count), wanted[i]);
        _refreshingModels = false;
        OnPropertyChanged(nameof(SelectedModel));
    }

    private void SetStatus(ServerState state, string status, string? setup)
    {
        ServerState = state;
        ServerStatusText = status;
        ShowSetupBanner = setup is not null;
        SetupText = setup ?? "";
    }

    // ---- commands -------------------------------------------------------------------

    private bool CanSend() => !IsBusy && !string.IsNullOrWhiteSpace(Input);

    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task SendAsync()
    {
        var text = Input.Trim();
        Input = "";
        Items.Add(new UserMessageItem(text));
        SaveChat(SnapshotHistory());
        _runDetached = false;
        IsBusy = true;
        _runCts = new CancellationTokenSource();
        var ct = _runCts.Token;
        // This chat's conversation: picking another chat while the run starts up replaces _history.
        var history = _history;

        try
        {
            if (_settings.Server.BaseUrl.Length > 0 && _settings.Server.Model.Length > 0)
            {
                // LM Studio loads models on first use; do that now so we know the real context size.
                using var probeHttp = LlmHttp.Create(TimeSpan.FromMinutes(10));
                var context = await ServerProbe.EnsureLoadedAsync(probeHttp, _settings.Server,
                    () => Dispatcher.UIThread.Post(() => AddNotice("Loading the model on the server…", isError: false)), ct);
                if (context is not null)
                {
                    _settings.Server.SetDetectedContext(context);
                    _modelNotLoaded = false;
                    OnContextChanged();
                }
            }
            if (PrepareBackend() is not { } backend)
                return;
            if (!_mcpConnect.IsCompleted)
            {
                AddNotice("Starting the MCP servers…", isError: false);
                await _mcpConnect;
            }

            var agent = _settings.Agent;
            var ctx = new ToolContext
            {
                Platform = _platform,
                Settings = agent,
                WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                CoordinateGrid = ModelCoordinates.GridFor(backend.Options.Model),
                Browser = agent.WebSearch ? HeadlessBrowser.Find(_paths.BrowserProfileDir) : null,
            };

            IReadOnlyList<ScreenSpace>? screens = null;
            if (backend.Vision)
            {
                try { screens = ctx.ScreenSpaces(); }
                catch (NotSupportedException) { }
            }

            // The MCP tools are a snapshot: the tool list stays the same for the whole run.
            var registry = ToolRegistry.CreateDefault(includeScreenTools: screens is not null, extra: _mcp.Tools, includeWebTools: agent.WebSearch);
            var prompt = SystemPrompt.Build(_platform.Name, screens, ctx.WorkingDirectory, agent.CustomInstructions, _mcp.Connected, agent.WebSearch);
            var session = new AgentSession(backend.Client, backend.Options, registry, ctx, prompt, history, backend.ContextTokens);
            await Task.Run(() => session.RunAsync(text, this, ct), CancellationToken.None);
            // A server error may have revealed the real context size; keep it for the next run.
            _settings.Server.LearnContextLimit(session.ContextTokens);
            OnContextChanged();
        }
        catch (OperationCanceledException)
        {
            var stopped = StoppedText();
            if (!_runDetached) AddNotice(stopped, isError: false);
        }
        finally
        {
            // After the run's last posted updates (its "Stopped." notice) have reached the chat.
            var chatId = _chatId;
            if (!_runDetached)
                Dispatcher.UIThread.Post(() => { if (_chatId == chatId) SaveChat(SnapshotHistory()); }, DispatcherPriority.Background);
            IsBusy = false;
            _runCts.Dispose();
            _runCts = null;
            if (_mcpRestartPending)
            {
                // Changed in Settings during the run; restarting then would have pulled the tools from under it.
                _mcpRestartPending = false;
                _mcpConnect = ConnectMcpAsync();
            }
        }
    }

    [RelayCommand(CanExecute = nameof(IsBusy))]
    private void Stop() => _runCts?.Cancel();

    /// <summary>The Break key, from the hook's thread: stops the run in progress, if any.</summary>
    private bool OnStopKey()
    {
        var cts = _runCts;
        if (cts is null || cts.IsCancellationRequested)
            return false;
        _stoppedByKey = true;
        try { cts.Cancel(); }
        catch (ObjectDisposedException) { return false; } // the run ended at that very moment
        return true;
    }

    private string StoppedText()
    {
        var byKey = _stoppedByKey;
        _stoppedByKey = false;
        return byKey ? "Stopped with the Break key." : "Stopped.";
    }

    [RelayCommand]
    private void NewChat()
    {
        LeaveChat(save: true);
        MarkCurrentChat();
    }

    // ---- saved chats (the History dropdown) --------------------------------------------

    /// <summary>Saved chats, the most recently changed first.</summary>
    public ObservableCollection<PastChat> PastChats { get; } = [];

    public bool HasPastChats => PastChats.Count > 0;

    /// <summary>The file the chat on screen is saved in; null until its first message.</summary>
    private string? _chatId;
    private string? _chatTitle;

    /// <summary>
    /// The run in progress belongs to a chat that is no longer on screen (New chat or another chat
    /// was picked while it ran): it is being stopped, and what it still reports is dropped.
    /// </summary>
    private bool _runDetached;

    private PastChat NewRow(ChatHeader header) => new(header, OpenChat, DeleteChat);

    /// <summary>Clears the screen for another chat. A run in progress is stopped, and saved as it is unless the chat is being deleted.</summary>
    private void LeaveChat(bool save)
    {
        if (IsBusy)
        {
            if (save) SaveChat(SnapshotHistory());
            _runDetached = true;
            _runCts?.Cancel();
        }
        // The stopped run keeps its own list until it ends, so it can't write into the next chat.
        _history = [];
        Items.Clear();
        _current = null;
        _chatId = null;
        _chatTitle = null;
    }

    private void OpenChat(PastChat row)
    {
        if (row.Id == _chatId)
            return;
        if (_chats.Load(row.Id) is not { } chat)
        {
            PastChats.Remove(row);
            OnPropertyChanged(nameof(HasPastChats));
            return;
        }
        LeaveChat(save: true);
        _chatId = chat.Id;
        _chatTitle = chat.Title;
        _history = chat.History;
        foreach (var entry in chat.Entries)
            Items.Add(FromEntry(entry));
        MarkCurrentChat();
    }

    private void DeleteChat(PastChat row)
    {
        if (row.Id == _chatId)
            LeaveChat(save: false);
        _chats.Delete(row.Id);
        PastChats.Remove(row);
        OnPropertyChanged(nameof(HasPastChats));
    }

    private void MarkCurrentChat()
    {
        foreach (var row in PastChats)
            row.IsCurrent = row.Id == _chatId;
    }

    /// <summary>A copy of the model's conversation to save; null if a run changed it while it was being copied.</summary>
    private List<ChatMessage>? SnapshotHistory()
    {
        try { return ChatStore.ForSaving(_history); }
        catch (InvalidOperationException) { return null; }
    }

    /// <summary>Writes the chat on screen to its file and moves it to the top of the History list. A chat with nothing typed yet isn't saved.</summary>
    private void SaveChat(List<ChatMessage>? history)
    {
        if (history is null || Items.OfType<UserMessageItem>().FirstOrDefault() is not { } first)
            return;
        _chatId ??= ChatStore.NewId();
        _chatTitle ??= ChatStore.TitleFrom(first.Text);
        var chat = new SavedChat
        {
            Id = _chatId,
            Title = _chatTitle,
            Updated = DateTime.UtcNow,
            // A reply bubble with nothing in it yet (a step just started) is left out.
            Entries = Items.Where(i => i is not AssistantMessageItem { HasText: false, HasReasoning: false }).Select(ToEntry).ToList(),
            History = history,
        };
        try { _chats.Save(chat); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return; // a full or read-only disk: the chat goes on, just unsaved
        }

        var row = PastChats.FirstOrDefault(r => r.Id == chat.Id);
        if (row is null)
        {
            row = NewRow(new ChatHeader { Id = chat.Id, Title = chat.Title, Updated = chat.Updated });
            PastChats.Insert(0, row);
            OnPropertyChanged(nameof(HasPastChats));
        }
        else
        {
            row.Updated = chat.Updated;
            if (PastChats.IndexOf(row) != 0)
                PastChats.Move(PastChats.IndexOf(row), 0);
        }
        MarkCurrentChat();
    }

    private static ChatEntry ToEntry(ChatItemViewModel item) => item switch
    {
        UserMessageItem u => new ChatEntry { Kind = ChatEntryKind.User, Text = u.Text },
        AssistantMessageItem a => new ChatEntry { Kind = ChatEntryKind.Assistant, Text = a.Text, Reasoning = a.Reasoning },
        ToolCallItem t => new ChatEntry { Kind = ChatEntryKind.Tool, CallId = t.CallId, Name = t.Name, Summary = t.Summary, State = t.State.ToString(), Text = t.ResultText },
        NoticeItem n => new ChatEntry { Kind = ChatEntryKind.Notice, Text = n.Text, IsError = n.IsError },
        _ => throw new ArgumentOutOfRangeException(nameof(item)),
    };

    private static ChatItemViewModel FromEntry(ChatEntry e) => e.Kind switch
    {
        ChatEntryKind.User => new UserMessageItem(e.Text ?? ""),
        ChatEntryKind.Assistant => new AssistantMessageItem { Text = e.Text ?? "", Reasoning = e.Reasoning ?? "", IsStreaming = false },
        ChatEntryKind.Tool => new ToolCallItem(e.CallId ?? "", e.Name ?? "", e.Summary ?? "")
        {
            ResultText = e.Text ?? "",
            // A call that was still running or waiting when the chat was saved never finished.
            State = Enum.TryParse<ToolItemState>(e.State, out var state) && state is not (ToolItemState.Running or ToolItemState.AwaitingApproval)
                ? state : ToolItemState.Denied,
        },
        _ => new NoticeItem(e.Text ?? "", e.IsError),
    };

    // ---- settings -------------------------------------------------------------------

    public SettingsViewModel CreateSettingsViewModel() => new(_settings.Clone(), _paths, McpConfig.Load(_paths.McpConfigFile), _mcp.Statuses);

    public async Task ApplySettingsAsync(AppSettings updated, string mcpConfigText)
    {
        // The server, key, model and context size live in the top bar, not in the dialog.
        updated.Server = _settings.Server;
        _settings = updated;
        _store.Save(updated);
        _platform.HostWindow.SetHiddenFromCapture(updated.Agent.HideFromScreenshots);
        _platform.InputOverlay.Duration = TimeSpan.FromSeconds(updated.Agent.InputLogSeconds);
        _platform.InputOverlay.Enabled = updated.Agent.ShowInputs;
        if (mcpConfigText != McpConfig.Load(_paths.McpConfigFile))
        {
            McpConfig.Save(_paths.McpConfigFile, mcpConfigText);
            if (IsBusy) _mcpRestartPending = true;
            else _mcpConnect = ConnectMcpAsync();
        }
        await CheckServerAsync();
    }

    // ---- MCP servers ----------------------------------------------------------------

    /// <summary>Starts the servers in Data/mcp.json (stopping any running ones) and reports the ones that fail in the chat.</summary>
    private async Task ConnectMcpAsync()
    {
        IReadOnlyList<McpServerConfig> servers;
        try
        {
            servers = McpConfig.Parse(McpConfig.Load(_paths.McpConfigFile)).Servers;
        }
        catch (Exception ex) when (ex is McpConfigException or IOException or UnauthorizedAccessException)
        {
            AddNotice($"The MCP server list could not be read, so no MCP tools are available: {ex.Message} Fix it in Settings → Advanced.", isError: true);
            servers = [];
        }
        await Task.Run(() => _mcp.ConnectAsync(servers, CancellationToken.None));
        foreach (var s in _mcp.Statuses.Where(s => s.State == McpServerState.Failed))
            AddNotice($"MCP server \"{s.Name}\" is not available: {s.Error} (Settings → Advanced)", isError: true);
    }

    private BackendConnection? PrepareBackend()
    {
        var server = _settings.Server;
        if (string.IsNullOrWhiteSpace(server.BaseUrl) || string.IsNullOrWhiteSpace(server.Model))
        {
            AddNotice("Enter your model server's address and pick a model at the top first.", isError: true);
            return null;
        }
        return BackendConnection.Create(_http, server);
    }

    private void AddNotice(string text, bool isError) => Items.Add(new NoticeItem(text, isError));

    // ---- IAgentObserver (called from the agent thread) -------------------------------

    // A run can last hours. The model's context is kept in check by context resets; the chat
    // view is kept in check here so memory stays flat: old entries are dropped and only recent
    // screenshots keep their thumbnails.
    private const int MaxChatItems = 400;
    private const int MaxThumbnails = 30;

    private void TrimChat()
    {
        while (Items.Count > MaxChatItems)
            Items.RemoveAt(0);
        foreach (var old in Items.OfType<ToolCallItem>().Where(t => t.HasScreenshot).SkipLast(MaxThumbnails).ToList())
        {
            old.Screenshot?.Dispose();
            old.Screenshot = null;
        }
    }


    // Every callback below is dropped once the run is detached: the chat it belongs to is no longer on screen.

    void IAgentObserver.OnStepStarted(int step)
    {
        // The agent waits for this call, so its conversation holds still while it is copied.
        var history = _runDetached ? null : SnapshotHistory();
        Dispatcher.UIThread.Post(() =>
        {
            if (_runDetached) return;
            // Saved at every step, so a crash or power cut loses at most the step in progress.
            SaveChat(history);
            TrimChat();
            _current = new AssistantMessageItem();
            Items.Add(_current);
        });
    }

    void IAgentObserver.OnDelta(StreamDelta delta) => Dispatcher.UIThread.Post(() =>
    {
        if (_current is null || _runDetached) return;
        if (delta.Channel == StreamChannel.Reasoning)
            _current.Reasoning += delta.Text;
        else
            _current.Text += delta.Text;
    });

    void IAgentObserver.OnAssistantMessage(ChatMessage message) => Dispatcher.UIThread.Post(() =>
    {
        if (_current is null || _runDetached) return;
        _current.Text = (message.Content ?? "").Trim();
        _current.Reasoning = (message.Reasoning ?? _current.Reasoning).Trim();
        FinishCurrent();
    });

    /// <summary>Ends the streaming bubble; an empty one (a discarded reply) leaves no trace.</summary>
    private void FinishCurrent()
    {
        if (_current is null) return;
        _current.IsStreaming = false;
        if (!_current.HasText && !_current.HasReasoning)
            Items.Remove(_current);
        _current = null;
    }

    async Task<bool> IAgentObserver.RequestApprovalAsync(ToolCall call, Tool tool, string summary, CancellationToken ct)
    {
        return await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (_runDetached) return Task.FromResult(false);
            var item = FindOrAddTool(call, summary);
            return item.WaitForApprovalAsync(ct);
        });
    }

    void IAgentObserver.OnNotice(string text) => Dispatcher.UIThread.Post(() =>
    {
        if (!_runDetached) AddNotice(text, isError: false);
    });

    void IAgentObserver.OnToolStarted(ToolCall call, string summary) => Dispatcher.UIThread.Post(() =>
    {
        if (_runDetached) return;
        var item = FindOrAddTool(call, summary);
        item.Summary = summary;
        item.State = ToolItemState.Running;
    });

    void IAgentObserver.OnToolFinished(ToolCall call, ToolResult result) => Dispatcher.UIThread.Post(() =>
    {
        if (_runDetached) return;
        var item = FindOrAddTool(call, call.Name);
        item.ResultText = result.Text.Length > 3000 ? result.Text[..3000] + "\n…" : result.Text.TrimEnd();
        if (result.Image is not null)
        {
            using var ms = new MemoryStream(result.Image);
            item.Screenshot = Bitmap.DecodeToWidth(ms, 480);
        }
        if (item.State != ToolItemState.Denied)
            item.State = result.IsError ? ToolItemState.Error : ToolItemState.Done;
    });

    void IAgentObserver.OnFinished(AgentStopReason reason, string? detail) => Dispatcher.UIThread.Post(() =>
    {
        var stopped = StoppedText();
        if (_runDetached) return;
        FinishCurrent();
        foreach (var t in Items.OfType<ToolCallItem>().Where(t => t.State is ToolItemState.Running or ToolItemState.AwaitingApproval))
            t.State = ToolItemState.Denied;

        switch (reason)
        {
            case AgentStopReason.Cancelled:
                AddNotice(stopped, isError: false);
                break;
            case AgentStopReason.Error:
                AddNotice(detail ?? "Something went wrong.", isError: true);
                break;
        }
    });

    private ToolCallItem FindOrAddTool(ToolCall call, string summary)
    {
        var existing = Items.OfType<ToolCallItem>().LastOrDefault(t => t.CallId == call.Id);
        if (existing is not null)
            return existing;
        var item = new ToolCallItem(call.Id, call.Name, summary);
        Items.Add(item);
        return item;
    }
}
