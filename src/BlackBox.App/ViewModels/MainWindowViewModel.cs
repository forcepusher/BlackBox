using System.Collections.ObjectModel;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using BlackBox.Core.Agent;
using BlackBox.Core.Llm;
using BlackBox.Core.Platform;
using BlackBox.Core.Settings;
using BlackBox.Core.Tools;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BlackBox.App.ViewModels;

public sealed partial class MainWindowViewModel : ObservableObject, IAgentObserver
{
    private readonly AppPaths _paths;
    private readonly SettingsStore _store;
    private readonly IPlatformServices _platform;
    private readonly HttpClient _http = LlmHttp.Create(Timeout.InfiniteTimeSpan);
    private readonly List<ChatMessage> _history = [];
    private AppSettings _settings;
    private CancellationTokenSource? _runCts;
    private AssistantMessageItem? _current;

    public MainWindowViewModel(AppPaths paths, SettingsStore store, AppSettings settings, IPlatformServices platform)
    {
        _paths = paths;
        _store = store;
        _settings = settings;
        _platform = platform;
    }

    public ObservableCollection<ChatItemViewModel> Items { get; } = [];

    public AppPaths Paths => _paths;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    private string _input = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand), nameof(StopCommand))]
    private bool _isBusy;

    [ObservableProperty]
    private string _backendLabel = "";

    [ObservableProperty]
    private string _serverStatusText = "Checking…";

    [ObservableProperty]
    private IBrush _statusBrush = Brushes.Gray;

    /// <summary>Shown when no server/model is configured or the server can't be reached.</summary>
    [ObservableProperty]
    private bool _showSetupBanner;

    [ObservableProperty]
    private string _setupText = "";

    public bool HasNoMessages => Items.Count == 0;

    public void AttachHostWindow(nint handle) => _platform.HostWindow.Attach(handle, _settings.Agent.HideFromScreenshots);

    public void OnStartup()
    {
        Items.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasNoMessages));
        _ = CheckServerAsync();
    }

    /// <summary>Raised by the setup banner's button; the view opens Settings.</summary>
    public event Action? OpenSettingsRequested;

    [RelayCommand]
    private void OpenSettings() => OpenSettingsRequested?.Invoke();

    [RelayCommand]
    private async Task CheckServerAsync()
    {
        var server = _settings.Server;
        BackendLabel = string.IsNullOrWhiteSpace(server.Model) ? "No model selected" : server.Model;
        if (string.IsNullOrWhiteSpace(server.BaseUrl) || string.IsNullOrWhiteSpace(server.Model))
        {
            SetStatus(Brushes.Gray, "Not set up", "Connect BlackBox to an AI model server (for example LM Studio) in Settings.");
            return;
        }

        SetStatus(BusyBrush, "Checking…", null);
        using var probeHttp = LlmHttp.Create(TimeSpan.FromSeconds(10));
        var probe = await ServerProbe.CheckAsync(probeHttp, server, CancellationToken.None);
        if (!probe.Reachable)
        {
            SetStatus(ErrorBrush, "Offline", $"{probe.Message} Check that the server is running at {server.BaseUrl}.");
            return;
        }
        if (probe.Models.Count > 0 && !probe.Models.Contains(server.Model))
        {
            SetStatus(ErrorBrush, "Model not found", $"The server doesn't offer \"{server.Model}\". Pick one of its models in Settings.");
            return;
        }
        if (probe.LoadedContext is { } context && context != server.ContextSize)
        {
            // Trust what LM Studio reports over the saved guess.
            server.ContextSize = context;
            _store.Save(_settings);
        }
        var ctxText = probe.LoadedContext is { } c ? $" · {c / 1024}k context" : "";
        SetStatus(OkBrush, $"Connected{ctxText}", null);
    }

    private void SetStatus(IBrush brush, string status, string? setup)
    {
        StatusBrush = brush;
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
                    _store.Save(_settings);
                    ServerStatusText = $"Connected · {c / 1024}k context";
                }
            }
            if (PrepareBackend() is not { } backend)
                return;

            var agent = _settings.Agent;
            var ctx = new ToolContext
            {
                Platform = _platform,
                Settings = agent,
                WorkingDirectory = WorkingDirectory(),
            };

            ScreenSpace? screen = null;
            if (backend.Vision)
            {
                try { screen = ctx.GetScreenSpace(); }
                catch (NotSupportedException) { }
            }

            var registry = ToolRegistry.CreateDefault(includeScreenTools: screen is not null, includeUiAutomation: _platform.Automation is not null);
            var prompt = SystemPrompt.Build(_platform.Name, screen, ctx.WorkingDirectory, agent.CustomInstructions);
            var session = new AgentSession(backend.Client, backend.Options, registry, ctx, prompt, _history, backend.ContextTokens);
            await Task.Run(() => session.RunAsync(text, this, ct), CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            AddNotice("Stopped.", isError: false);
        }
        finally
        {
            IsBusy = false;
            _runCts.Dispose();
            _runCts = null;
        }
    }

    [RelayCommand(CanExecute = nameof(IsBusy))]
    private void Stop() => _runCts?.Cancel();

    [RelayCommand]
    private void NewChat()
    {
        _runCts?.Cancel();
        _history.Clear();
        Items.Clear();
        _current = null;
    }

    // ---- settings -------------------------------------------------------------------

    public SettingsViewModel CreateSettingsViewModel() => new(_settings.Clone(), _paths);

    public async Task ApplySettingsAsync(AppSettings updated)
    {
        _settings = updated;
        _store.Save(updated);
        _platform.HostWindow.SetHiddenFromCapture(updated.Agent.HideFromScreenshots);
        await CheckServerAsync();
    }

    private BackendConnection? PrepareBackend()
    {
        var server = _settings.Server;
        if (string.IsNullOrWhiteSpace(server.BaseUrl) || string.IsNullOrWhiteSpace(server.Model))
        {
            AddNotice("Connect to a model server in Settings first.", isError: true);
            return null;
        }
        return BackendConnection.Create(_http, server, _settings.Agent);
    }
    private string WorkingDirectory()
    {
        var wd = string.IsNullOrWhiteSpace(_settings.Agent.WorkingDirectory) ? _paths.WorkspaceDir : _paths.Resolve(_settings.Agent.WorkingDirectory);
        Directory.CreateDirectory(wd);
        return wd;
    }

    private static readonly IBrush OkBrush = new SolidColorBrush(Color.Parse("#5FB878"));
    private static readonly IBrush BusyBrush = new SolidColorBrush(Color.Parse("#E0A64B"));
    private static readonly IBrush ErrorBrush = new SolidColorBrush(Color.Parse("#E06C6C"));

    private void AddNotice(string text, bool isError) => Items.Add(new NoticeItem(text, isError));

    // ---- IAgentObserver (called from the agent thread) -------------------------------

    void IAgentObserver.OnStepStarted(int step) => Dispatcher.UIThread.Post(() =>
    {
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
        _current.IsStreaming = false;
        if (!_current.HasText && !_current.HasReasoning)
            Items.Remove(_current);
        _current = null;
    });

    async Task<bool> IAgentObserver.RequestApprovalAsync(ToolCall call, Tool tool, string summary, CancellationToken ct)
    {
        return await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var item = FindOrAddTool(call, summary);
            return item.WaitForApprovalAsync(ct);
        });
    }

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
        if (_current is not null)
        {
            _current.IsStreaming = false;
            if (!_current.HasText && !_current.HasReasoning)
                Items.Remove(_current);
            _current = null;
        }
        foreach (var t in Items.OfType<ToolCallItem>().Where(t => t.State is ToolItemState.Running or ToolItemState.AwaitingApproval))
            t.State = ToolItemState.Denied;

        switch (reason)
        {
            case AgentStopReason.Cancelled:
                AddNotice("Stopped.", isError: false);
                break;
            case AgentStopReason.Failsafe:
            case AgentStopReason.MaxSteps:
                AddNotice(detail ?? reason.ToString(), isError: false);
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
