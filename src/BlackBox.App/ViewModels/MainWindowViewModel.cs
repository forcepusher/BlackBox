using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using BlackBox.Core.Agent;
using BlackBox.Core.Llm;
using BlackBox.Core.Mcp;
using BlackBox.Core.Platform;
using BlackBox.Core.Settings;
using BlackBox.Core.Tools;
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
    private readonly List<ChatMessage> _history = [];
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
        _ = CheckServerAsync();
        _mcpConnect = ConnectMcpAsync();
    }

    /// <summary>Stops the run and the MCP servers; called when the window closes.</summary>
    public void Shutdown()
    {
        _runCts?.Cancel();
        _stopKey?.Dispose();
        try { _mcp.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(5)); }
        catch (AggregateException) { /* a server that would not stop; the process ends anyway */ }
    }

    // ---- server and model (top bar) ---------------------------------------------------

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

        // Trust what the server reports. The context size is not saved; image support is.
        server.ContextSize = probe.LoadedContext ?? ServerSettings.FallbackContextSize;
        if (probe.Vision is { } v && v != server.SupportsVision)
        {
            server.SupportsVision = v;
            _store.Save(_settings);
        }
        var details = (probe.LoadedContext is { } c ? $" · {c / 1024}k context" : "") + (probe.Vision == false ? " · no vision" : "");
        SetStatus(ServerState.Connected, "Connected" + details, null);
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
        IsBusy = true;
        _runCts = new CancellationTokenSource();
        var ct = _runCts.Token;

        try
        {
            if (_settings.Server.BaseUrl.Length > 0 && _settings.Server.Model.Length > 0)
            {
                // LM Studio loads models on first use; do that now so we know the real context size.
                using var probeHttp = LlmHttp.Create(TimeSpan.FromMinutes(10));
                var context = await ServerProbe.EnsureLoadedAsync(probeHttp, _settings.Server,
                    () => Dispatcher.UIThread.Post(() => AddNotice("Loading the model on the server…", isError: false)), ct);
                if (context is { } c && c != _settings.Server.ContextSize)
                {
                    _settings.Server.ContextSize = c;
                    ServerStatusText = $"Connected · {c / 1024}k context";
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
            };

            IReadOnlyList<ScreenSpace>? screens = null;
            if (backend.Vision)
            {
                try { screens = ctx.ScreenSpaces(); }
                catch (NotSupportedException) { }
            }

            // The MCP tools are a snapshot: the tool list stays the same for the whole run.
            var registry = ToolRegistry.CreateDefault(includeScreenTools: screens is not null, includeUiAutomation: _platform.Automation is not null, extra: _mcp.Tools);
            var prompt = SystemPrompt.Build(_platform.Name, screens, ctx.WorkingDirectory, agent.CustomInstructions, _mcp.Connected);
            var session = new AgentSession(backend.Client, backend.Options, registry, ctx, prompt, _history, backend.ContextTokens);
            await Task.Run(() => session.RunAsync(text, this, ct), CancellationToken.None);
            // A server error may have revealed the real context size; keep it for the next run.
            _settings.Server.ContextSize = session.ContextTokens;
        }
        catch (OperationCanceledException)
        {
            AddNotice(StoppedText(), isError: false);
        }
        finally
        {
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
        _runCts?.Cancel();
        _history.Clear();
        Items.Clear();
        _current = null;
    }

    // ---- settings -------------------------------------------------------------------

    public SettingsViewModel CreateSettingsViewModel() => new(_settings.Clone(), _paths, McpConfig.Load(_paths.McpConfigFile), _mcp.Statuses);

    public async Task ApplySettingsAsync(AppSettings updated, string mcpConfigText)
    {
        // The address and model live in the top bar, not in the Settings dialog.
        updated.Server.BaseUrl = _settings.Server.BaseUrl;
        updated.Server.Model = _settings.Server.Model;
        updated.Server.ContextSize = _settings.Server.ContextSize; // runtime value, re-detected below
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

    void IAgentObserver.OnStepStarted(int step) => Dispatcher.UIThread.Post(() =>
    {
        TrimChat();
        _current = new AssistantMessageItem();
        Items.Add(_current);
    });

    void IAgentObserver.OnDelta(StreamDelta delta) => Dispatcher.UIThread.Post(() =>
    {
        if (_current is null) return;
        if (delta.Channel == StreamChannel.Reasoning)
            _current.Reasoning += delta.Text;
        else
            _current.Text += delta.Text;
    });

    void IAgentObserver.OnAssistantMessage(ChatMessage message) => Dispatcher.UIThread.Post(() =>
    {
        if (_current is null) return;
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
            var item = FindOrAddTool(call, summary);
            return item.WaitForApprovalAsync(ct);
        });
    }

    void IAgentObserver.OnNotice(string text) => Dispatcher.UIThread.Post(() => AddNotice(text, isError: false));

    void IAgentObserver.OnToolStarted(ToolCall call, string summary) => Dispatcher.UIThread.Post(() =>
    {
        var item = FindOrAddTool(call, summary);
        item.Summary = summary;
        item.State = ToolItemState.Running;
    });

    void IAgentObserver.OnToolFinished(ToolCall call, ToolResult result) => Dispatcher.UIThread.Post(() =>
    {
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
        FinishCurrent();
        foreach (var t in Items.OfType<ToolCallItem>().Where(t => t.State is ToolItemState.Running or ToolItemState.AwaitingApproval))
            t.State = ToolItemState.Denied;

        switch (reason)
        {
            case AgentStopReason.Cancelled:
                AddNotice(StoppedText(), isError: false);
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
