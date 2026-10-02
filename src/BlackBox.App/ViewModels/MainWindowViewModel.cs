using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.Json.Nodes;
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
    private readonly LlamaServerHost _server;
    private readonly HttpClient _http = new() { Timeout = Timeout.InfiniteTimeSpan };
    private readonly List<ChatMessage> _history = [];
    private AppSettings _settings;
    private CancellationTokenSource? _runCts;
    private AssistantMessageItem? _current;

    public MainWindowViewModel(AppPaths paths, SettingsStore store, AppSettings settings, IPlatformServices platform, LlamaServerHost server)
    {
        _paths = paths;
        _store = store;
        _settings = settings;
        _platform = platform;
        _server = server;
        _server.StateChanged += _ => Dispatcher.UIThread.Post(RefreshServerStatus);
        RefreshServerStatus();
    }

    public ObservableCollection<ChatItemViewModel> Items { get; } = [];

    public AppPaths Paths => _paths;
    public LlamaServerHost Server => _server;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    private string _input = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand), nameof(StopCommand))]
    private bool _isBusy;

    [ObservableProperty]
    private string _backendLabel = "";

    [ObservableProperty]
    private string _serverStatusText = "";

    [ObservableProperty]
    private IBrush _statusBrush = Brushes.Gray;

    [ObservableProperty]
    private bool _isLocalBackend;

    [ObservableProperty]
    private bool _canStartServer;

    [ObservableProperty]
    private bool _canStopServer;

    public bool HasNoMessages => Items.Count == 0;

    // ---- first-run model download ----------------------------------------------------

    private CancellationTokenSource? _downloadCts;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowSetupBanner))]
    private bool _modelPresent;

    [ObservableProperty]
    private bool _isDownloading;

    [ObservableProperty]
    private double _downloadPercent;

    [ObservableProperty]
    private string _downloadText = "";

    public bool ShowSetupBanner => IsLocalBackend && !ModelPresent;

    partial void OnIsLocalBackendChanged(bool value) => OnPropertyChanged(nameof(ShowSetupBanner));

    public string SetupText =>
        $"BlackBox needs its AI model ({Path.GetFileName(_settings.Local.ModelPath)}, about 8.7 GB with vision). " +
        "It is downloaded once into the Models folder next to the app.";

    [RelayCommand]
    private async Task DownloadModelAsync()
    {
        if (IsDownloading) return;
        IsDownloading = true;
        _downloadCts = new CancellationTokenSource();
        var downloader = new ModelDownloader(_http);
        var progress = new Progress<DownloadProgress>(p =>
        {
            DownloadPercent = p.Total is > 0 ? 100.0 * p.Done / p.Total.Value : 0;
            var total = p.Total is { } t ? $" of {t / 1e9:0.0} GB" : "";
            var speed = p.BytesPerSecond > 0 ? $" · {p.BytesPerSecond / 1e6:0} MB/s" : "";
            DownloadText = $"{p.FileName}: {p.Done / 1e9:0.00} GB{total}{speed}";
        });

        try
        {
            var local = _settings.Local;
            var model = _paths.Resolve(local.ModelPath);
            var mmproj = string.IsNullOrWhiteSpace(local.MmprojPath) ? null : _paths.Resolve(local.MmprojPath);
            if (mmproj is not null && !File.Exists(mmproj) && !string.IsNullOrWhiteSpace(local.MmprojUrl))
                await downloader.DownloadAsync(local.MmprojUrl, mmproj, progress, _downloadCts.Token);
            if (!File.Exists(model))
                await downloader.DownloadAsync(local.ModelUrl, model, progress, _downloadCts.Token);

            RefreshModelPresent();
            AddNotice("Model downloaded.", isError: false);
            if (ModelPresent)
                await StartServerAsync();
        }
        catch (OperationCanceledException)
        {
            DownloadText = "Paused. Press Download to continue where it stopped.";
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            DownloadText = "Download failed: " + ex.Message + " Press Download to retry; it resumes where it stopped.";
        }
        finally
        {
            IsDownloading = false;
            _downloadCts.Dispose();
            _downloadCts = null;
        }
    }

    [RelayCommand]
    private void CancelDownload() => _downloadCts?.Cancel();

    private void RefreshModelPresent()
    {
        ModelPresent = File.Exists(_paths.Resolve(_settings.Local.ModelPath));
        RefreshServerStatus();
    }

    public void OnStartup()
    {
        Items.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasNoMessages));
        RefreshModelPresent();
        if (_settings.Backend == BackendKind.Local && ModelPresent && _settings.Local.AutoStart)
            _ = StartServerAsync();
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
            if (await PrepareBackendAsync(ct) is not { } backend)
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

            var registry = ToolRegistry.CreateDefault(includeScreenTools: screen is not null);
            var prompt = SystemPrompt.Build(_platform.Name, screen, ctx.WorkingDirectory, agent.CustomInstructions);
            var session = new AgentSession(backend.Client, backend.Options, registry, ctx, prompt, _history);
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

    [RelayCommand]
    private async Task StartServerAsync()
    {
        try
        {
            await _server.StartAsync(_settings.Local, CancellationToken.None);
        }
        catch (Exception ex)
        {
            AddNotice("Could not start the local model: " + ex.Message, isError: true);
        }
        if (_server.State == ServerState.Failed && _server.LastError is { } err)
            AddNotice(err, isError: true);
        else if (_server.Warning is { } warning)
            AddNotice(warning, isError: false);
    }

    [RelayCommand]
    private Task StopServerAsync() => _server.StopAsync();

    // ---- settings -------------------------------------------------------------------

    public SettingsViewModel CreateSettingsViewModel() => new(_settings.Clone(), _paths, _server);

    public async Task ApplySettingsAsync(AppSettings updated)
    {
        var old = _settings;
        _settings = updated;
        _store.Save(updated);

        var localChanged = JsonSerializer.Serialize(old.Local) != JsonSerializer.Serialize(updated.Local);
        var wasRunning = _server.State is ServerState.Running or ServerState.Starting;
        RefreshModelPresent();
        OnPropertyChanged(nameof(SetupText));

        if (updated.Backend == BackendKind.Remote && wasRunning)
        {
            await _server.StopAsync(); // free the GPU when switching to a remote API
        }
        else if (updated.Backend == BackendKind.Local && (localChanged && wasRunning || old.Backend == BackendKind.Remote && updated.Local.AutoStart))
        {
            AddNotice("Restarting the local model with the new settings…", isError: false);
            await StartServerAsync();
        }
        RefreshServerStatus();
    }

    // ---- backend --------------------------------------------------------------------

    private async Task<BackendConnection?> PrepareBackendAsync(CancellationToken ct)
    {
        if (_settings.Backend == BackendKind.Remote)
        {
            var remote = _settings.Remote;
            if (string.IsNullOrWhiteSpace(remote.BaseUrl) || string.IsNullOrWhiteSpace(remote.Model))
            {
                AddNotice("Set the API address and model name in Settings first.", isError: true);
                return null;
            }
            return BackendConnection.ForRemote(_http, remote, _settings.Agent);
        }

        if (!ModelPresent)
        {
            AddNotice("The model isn't downloaded yet. Press Download at the top first.", isError: true);
            return null;
        }

        if (_server.State != ServerState.Running)
        {
            if (_server.State != ServerState.Starting)
                AddNotice("Starting the local model…", isError: false);
            await StartServerAsync();
            ct.ThrowIfCancellationRequested();
            if (_server.State != ServerState.Running)
                return null;
        }
        return BackendConnection.ForLocal(_http, _server, _settings.Agent);
    }

    private string WorkingDirectory()
    {
        var wd = string.IsNullOrWhiteSpace(_settings.Agent.WorkingDirectory) ? _paths.WorkspaceDir : _paths.Resolve(_settings.Agent.WorkingDirectory);
        Directory.CreateDirectory(wd);
        return wd;
    }

    private void RefreshServerStatus()
    {
        IsLocalBackend = _settings.Backend == BackendKind.Local;
        if (!IsLocalBackend)
        {
            BackendLabel = string.IsNullOrWhiteSpace(_settings.Remote.Model) ? "Remote API" : _settings.Remote.Model;
            ServerStatusText = "Remote API";
            StatusBrush = OkBrush;
            CanStartServer = CanStopServer = false;
            return;
        }

        BackendLabel = Path.GetFileNameWithoutExtension(_settings.Local.ModelPath);
        var state = _server.State;
        StatusBrush = state switch
        {
            ServerState.Running => OkBrush,
            ServerState.Starting => BusyBrush,
            ServerState.Failed => ErrorBrush,
            _ => Brushes.Gray,
        };
        ServerStatusText = state switch
        {
            ServerState.Running => $"Ready · {(_server.HasVision ? "vision" : "text only")} · {_server.EffectiveContextSize / 1024}k context",
            ServerState.Starting => "Loading model…",
            ServerState.Failed => "Failed to start",
            _ => "Stopped",
        };
        CanStartServer = state is ServerState.Stopped or ServerState.Failed && File.Exists(_paths.Resolve(_settings.Local.ModelPath));
        CanStopServer = state is ServerState.Running or ServerState.Starting;
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
