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
    private readonly ChatSaver _chatSaver;
    private readonly McpHost _mcp = new();
    /// <summary>The chat's SSH logins: they last from one message to the next, and end with the chat.</summary>
    private Core.Ssh.SshHosts _ssh;
    private Task _mcpConnect = Task.CompletedTask;
    private bool _mcpRestartPending;
    private AppSettings _settings;
    private CancellationTokenSource? _runCts;
    private AgentSession? _session;
    private AssistantMessageItem? _current;
    private IDisposable? _stopKey;
    private volatile bool _stoppedByKey;

    /// <summary>Ticks once a second during a run, for the reply in progress to count the wait, and a tool call that runs long its time.</summary>
    private readonly DispatcherTimer _tick;

    /// <summary>Decoders and speakers for the previews of files in the chat.</summary>
    private readonly PreviewServices _previews;

    public MainWindowViewModel(AppPaths paths, SettingsStore store, AppSettings settings, IPlatformServices platform)
    {
        _paths = paths;
        _store = store;
        _settings = settings;
        _platform = platform;
        _previews = new PreviewServices(Core.Media.MediaDecoders.Create(platform, paths, Core.Comfy.ComfyHub.For(paths.ComfyDir)), platform.CreateAudioOutput);
        // A ComfyUI an earlier BlackBox started and left at work is closed once its jobs are done.
        Core.Comfy.ComfyHub.For(paths.ComfyDir).CloseLeftover();
        _chats = new ChatStore(paths.ChatsDir);
        _chatSaver = new ChatSaver(_chats);
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
    }

    public ObservableCollection<ChatItemViewModel> Items { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    private string _input = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand), nameof(StopCommand))]
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

    public void AttachHostWindow(nint handle)
    {
        _platform.HostWindow.Attach(handle);
        _platform.InputOverlay.Duration = TimeSpan.FromSeconds(AgentSettings.InputLogSeconds);
        _platform.InputOverlay.Enabled = true;
    }

    public void OnStartup()
    {
        Items.CollectionChanged += (_, _) => ShowIntro = Items.All(i => i is NoticeItem);
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
        _chatSaver.Flush(TimeSpan.FromSeconds(10));
        if (_settingsSave?.IsEnabled == true)
        {
            _settingsSave.Stop();
            SaveSettings(); // what was typed just before closing
        }
        _runCts?.Cancel();
        _stopKey?.Dispose();
        // Logging out of the SSH servers and stopping the MCP ones, at the same time and for five
        // seconds at most: one after the other, a server that didn't answer kept the process
        // alive five seconds a server after the window had gone.
        var ssh = Task.Run(_ssh.Dispose);
        var mcp = _mcp.DisposeAsync().AsTask();
        try { Task.WaitAll([ssh, mcp], TimeSpan.FromSeconds(5)); }
        catch (AggregateException) { /* a server that would not stop; the process ends anyway */ }
    }

    // ---- server, API key, model and context size (top bar) ----------------------------

    private CancellationTokenSource? _addressEdit;
    private int _checkVersion;
    private bool _refreshingModels;

    /// <summary>Server address typed in the top bar. Saved and checked once typing pauses.</summary>
    public string ServerAddress
    {
        get => _settings.Server.BaseUrl;
        set
        {
            value = value.Trim();
            if (value == _settings.Server.BaseUrl) return;
            _settings.Server.BaseUrl = value;
            SaveSettingsSoon();
            OnPropertyChanged();
            _ = CheckAfterTypingAsync();
        }
    }

    /// <summary>API key typed in the top bar, for servers that want one. Saved and checked once typing pauses.</summary>
    public string ApiKey
    {
        get => _settings.Server.ApiKey;
        set
        {
            value = value.Trim();
            if (value == _settings.Server.ApiKey) return;
            _settings.Server.ApiKey = value;
            SaveSettingsSoon();
            OnPropertyChanged();
            _ = CheckAfterTypingAsync();
        }
    }

    private DispatcherTimer? _settingsSave;

    /// <summary>
    /// Saves the settings once typing pauses: written at every key press, each one waited on the
    /// disk (and on an antivirus holding the file just written) on the UI thread, while typing.
    /// </summary>
    private void SaveSettingsSoon()
    {
        _settingsSave ??= new DispatcherTimer(TimeSpan.FromMilliseconds(700), DispatcherPriority.Background, (_, _) =>
        {
            _settingsSave!.Stop();
            SaveSettings();
        });
        _settingsSave.Stop();
        _settingsSave.Start();
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
                server.ManualContextSize = ContextStops[ContextSizes.IndexOf(ContextStops, server.ContextSize)];
            server.AutoContext = value;
            SaveSettings();
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
        SaveSettings();
        OnContextChanged();
    }

    /// <summary>LM Studio said the model isn't loaded yet; it has no context size until the first message loads it.</summary>
    private bool _modelNotLoaded;

    /// <summary>The server and model that refused pictures in a run though nothing said it takes none: the next runs go without them.</summary>
    private string? _imagesRefused;

    private static string ImagesKey(ServerSettings server) => server.BaseUrl + " " + server.ModelInUse;

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
            if (server.AutoContext && server.ContextHosted && server.ContextSize < server.DetectedContextSize)
                return $"The service takes up to {server.DetectedContextSize:N0} tokens for this model. BlackBox uses {server.ContextSize:N0} of them, "
                       + "because every step sends the whole conversation, screenshots and all, again. Untick Auto to use more.";
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

    /// <summary>Models the server offers that can chat. Always contains the one shown, even while the server doesn't offer it.</summary>
    public ObservableCollection<string> Models { get; } = [];

    /// <summary>
    /// The model picked by hand; until there is one, the strongest the server offers, which BlackBox
    /// picks anew at every check. A picked model stays shown while the server doesn't offer it, and
    /// the setup banner says which model stands in for it meanwhile.
    /// </summary>
    public string? SelectedModel
    {
        get => _settings.Server.Model is { Length: > 0 } picked ? picked : _settings.Server.AutoModel;
        set
        {
            // Refilling the list briefly clears the selection; that is not the user choosing nothing.
            if (_refreshingModels || string.IsNullOrEmpty(value) || value == SelectedModel)
                return;
            _settings.Server.Model = value;
            _settings.Server.AutoModel = null;
            SaveSettings();
            OnPropertyChanged();
            OnPropertyChanged(nameof(ModelToolTip));
            _ = CheckServerAsync();
        }
    }

    /// <summary>Size, quantization and benchmark score of the model BlackBox picked ("27B, 6-bit, 79.8% on Terminal-Bench 2.1"), as far as known.</summary>
    private string? _autoModelDetails;

    private string AutoModelDetails => _autoModelDetails is { } details ? $" ({details})" : "";

    /// <summary>Why the model shown is the one, when BlackBox picked it.</summary>
    public string? ModelToolTip => !string.IsNullOrEmpty(_settings.Server.Model) || _settings.Server.AutoModel is not { } auto ? null
        : auto == KiloGateway.Model && KiloGateway.Is(_settings.Server.BaseUrl)
            ? "Picked by BlackBox: of Kilo's free models, the one that clicked best in BlackBox's tests. Pick one yourself to keep using it."
            : $"Picked by BlackBox as the strongest model the server offers{AutoModelDetails}: the best score on an agent benchmark, " +
              "else the most parameters, then the most bits per weight. Pick one yourself to keep using it.";

    private async Task CheckAfterTypingAsync()
    {
        _addressEdit?.Cancel();
        var edit = _addressEdit = new CancellationTokenSource();
        try { await Task.Delay(700, edit.Token); }
        catch (OperationCanceledException) { return; }
        await CheckServerAsync();
    }

    /// <summary>Asks the server for its models and the details of the one to use; updates the top bar.</summary>
    [RelayCommand]
    private Task CheckServerAsync() => ProbeServerAsync(quiet: false, CancellationToken.None);

    /// <summary>
    /// The check behind <see cref="CheckServerCommand"/>. Quiet, before a task: no "Checking…" in the
    /// top bar meanwhile, and the context size known for the model stays (a run may have learnt it
    /// from the server's "too long" error) unless the model to use changed.
    /// </summary>
    private async Task ProbeServerAsync(bool quiet, CancellationToken ct)
    {
        var version = ++_checkVersion;
        var server = _settings.Server;
        if (string.IsNullOrWhiteSpace(server.BaseUrl))
        {
            SetStatus(ServerState.Unknown, "Not set up", "Enter the address of your model server (for example LM Studio at http://localhost:1234/v1) at the top, " +
                                                         "or use Kilo's free online models, which need no account.");
            return;
        }

        if (!quiet)
            SetStatus(ServerState.Checking, "Checking…", null);
        using var probeHttp = LlmHttp.Create(TimeSpan.FromSeconds(10));
        var probe = await ServerProbe.CheckAsync(probeHttp, server, ct);
        if (version != _checkVersion)
            return; // the address or model changed while this check was running
        if (probe.BaseUrl is { } found && found != server.BaseUrl)
        {
            // "host:1234" became the full URL that answered; show it so there is no doubt.
            server.BaseUrl = found;
            SaveSettings();
            OnPropertyChanged(nameof(ServerAddress));
        }

        // With no model picked by hand, or one the server doesn't offer, the strongest one it offers
        // is used; it isn't saved, so the picked one is back as soon as the server lists it again.
        // A server that can't be asked keeps the model it last said to use.
        var before = server.ModelInUse;
        if (probe.Reachable)
        {
            server.AutoModel = probe.AutoPicked ? probe.Model : null;
            _autoModelDetails = probe.ModelDetails;
        }
        SetModels(probe.Models);
        OnPropertyChanged(nameof(ModelToolTip));
        var kilo = KiloGateway.Is(server.BaseUrl);
        if (probe.KeyRefused)
        {
            SetStatus(ServerState.Failed, "Key refused", probe.Message + (kilo ? "" : " Or use Kilo's free online models, which need no account."));
            return;
        }
        if (!probe.Reachable)
        {
            SetStatus(ServerState.Failed, "Offline", $"{probe.Message}{ServerAppHint(server.BaseUrl)} Check the address at the top and that the server is running" +
                                                     (kilo ? "." : ", or use Kilo's free online models, which need no account."));
            return;
        }
        if (probe.Model is null)
        {
            SetStatus(ServerState.Failed, "No models", kilo
                ? "Kilo offers no free model to chat with right now. Try again later, or enter your own server's address at the top."
                : string.IsNullOrEmpty(server.Model)
                    ? "The server is running but offers no models. Load or download one in LM Studio."
                    : $"The server doesn't offer \"{server.Model}\" anymore, nor any other model to chat with. Load or download one in LM Studio.");
            return;
        }

        if (!quiet || server.ModelInUse != before)
        {
            // Trust what the server reports; neither the context size (it shows by the slider) nor image support is saved.
            server.SetDetectedContext(probe.LoadedContext, probe.ContextHosted);
            _modelNotLoaded = probe.Loaded == false;
            OnContextChanged();
        }
        server.SupportsVision = (probe.Vision ?? true) && _imagesRefused != ImagesKey(server);
        OnPropertyChanged(nameof(CanSeeScreen));
        var status = "Connected" + (!server.SupportsVision ? " · no vision" : "");
        if (probe.AutoPicked && server.Model.Length > 0)
            SetStatus(ServerState.Connected, status,
                $"The server doesn't offer \"{server.Model}\" right now, so BlackBox uses {probe.Model}{AutoModelDetails}, the strongest model it offers, " +
                "and goes back to yours as soon as the server lists it again. To stop waiting for it, pick another model at the top.",
                "Your model isn't on the server");
        else
            SetStatus(ServerState.Connected, status, null);
    }

    /// <summary>
    /// Updates the dropdown in place, so the ComboBox never loses its selection: once lost, it does
    /// not pick it up again when the same value is set back. The model shown stays in the list
    /// throughout; new entries go in, then the selection moves, and only then do old ones go.
    /// </summary>
    private void SetModels(IReadOnlyList<string> fromServer)
    {
        var wanted = fromServer.ToList();
        if (SelectedModel is { } shown && !wanted.Contains(shown))
            wanted.Insert(0, shown);
        _refreshingModels = true;
        for (var i = 0; i < wanted.Count; i++)
            if (!Models.Contains(wanted[i]))
                Models.Insert(Math.Min(i, Models.Count), wanted[i]);
        OnPropertyChanged(nameof(SelectedModel));
        for (var i = Models.Count - 1; i >= 0; i--)
            if (!wanted.Contains(Models[i]))
                Models.RemoveAt(i);
        _refreshingModels = false;
    }

    /// <summary>
    /// For a server that doesn't answer on LM Studio's or Ollama's own port, how to turn it on:
    /// LM Studio installed and a model downloaded, its server still stays off until switched on.
    /// </summary>
    private static string ServerAppHint(string address) =>
        Core.Llm.ServerAddress.Candidates(address).FirstOrDefault() is { } url && Uri.TryCreate(url, UriKind.Absolute, out var uri)
            ? uri.Port switch
            {
                1234 => " If it is LM Studio, its server has to be switched on: in LM Studio's Developer tab, set Status to Running" +
                        (uri.IsLoopback ? "." : ", and turn on Serve on Local Network for other PCs to reach it."),
                11434 => " If it is Ollama, start the Ollama app.",
                _ => "",
            }
            : "";

    private const string NotConnectedTitle = "BlackBox isn't connected to a model yet";

    /// <summary>The setup banner's heading.</summary>
    [ObservableProperty]
    private string _setupTitle = NotConnectedTitle;

    private void SetStatus(ServerState state, string status, string? setup, string title = NotConnectedTitle)
    {
        ServerState = state;
        ServerStatusText = status;
        ShowSetupBanner = setup is not null;
        SetupText = setup ?? "";
        SetupTitle = title;
        OffersKilo = setup is not null && state is ServerState.Failed or ServerState.Unknown && !KiloGateway.Is(_settings.Server.BaseUrl);
    }

    /// <summary>The setup banner's button for Kilo's free models, while BlackBox has no server that answers.</summary>
    [ObservableProperty]
    private bool _offersKilo;

    /// <summary>
    /// Switches back to Kilo's free models (see <see cref="KiloGateway"/>), where BlackBox starts: its
    /// address and no key, since a key meant for another server is none of Kilo's business. No model
    /// is picked, so Kilo's tested one is used there and another server's strongest one after it. The
    /// context goes back to Auto, which on Kilo is 100k of the model's million.
    /// </summary>
    [RelayCommand]
    private void UseKilo()
    {
        var server = _settings.Server;
        server.BaseUrl = KiloGateway.Address;
        server.ApiKey = "";
        server.Model = "";
        server.AutoModel = KiloGateway.Model; // until the check finds it, or another free one
        server.AutoContext = true;
        SaveSettings();
        // In the list before the dropdown is told, as at startup: a selection the ComboBox can't find it doesn't take.
        _refreshingModels = true;
        if (!Models.Contains(KiloGateway.Model))
            Models.Insert(0, KiloGateway.Model);
        _refreshingModels = false;
        OnPropertyChanged(nameof(ServerAddress));
        OnPropertyChanged(nameof(ApiKey));
        OnPropertyChanged(nameof(SelectedModel));
        OnPropertyChanged(nameof(AutoContext));
        OnContextChanged();
        _ = CheckServerAsync();
    }

    // ---- filmmaking ------------------------------------------------------------------

    /// <summary>
    /// The Filmmaking window's model. Its "Make the film" sends a message to this chat as if the user
    /// had typed it, so the agent's work on the film shows here, with every clip it makes.
    /// </summary>
    /// <param name="sent">Called once such a message is in the chat (the window brings the chat forward).</param>
    public FilmViewModel CreateFilmViewModel(Action? sent = null) =>
        new(_previews, Core.Comfy.ComfyHub.For(_paths.ComfyDir), text => Dispatcher.UIThread.Post(() =>
        {
            _ = SendAsync(new UserInput(text));
            sent?.Invoke();
        }), _paths.FilmsDir, null);

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
            AddNotice($"The pasted picture could not be saved: {ex.Message}", isError: true);
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
        var input = new UserInput(Input.Trim(), Attachments.Select(a => a.Path).ToList());
        Input = "";
        ClearAttachments();
        await SendAsync(input);
    }

    private async Task SendAsync(UserInput input)
    {
        Items.Add(new UserMessageItem(input.Text, input.Files));
        if (IsBusy)
        {
            if (_runDetached) _forNextRun.Add(input);
            else if (_session is { } running) running.Steer(input);
            else _steerPending.Add(input);
            SaveChat(SnapshotHistory());
            return;
        }
        SaveChat(SnapshotHistory());
        // What arrived just as a run ended, too late for it, starts the next one.
        for (UserInput? next = input; next is not null;)
            next = await RunAsync(next);
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
    private void AddStopNotice(string text, bool isError, UserInput? unsent = null) =>
        Items.Add(unsent is null
            ? new NoticeItem(text, isError, CarryOn)
            : new NoticeItem(text, isError, () => _ = RunAgainAsync(unsent), unsent: true));

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
        OnPropertyChanged(nameof(TypingPauseText));
    }

    /// <summary>How long after the last key press in the box the agent carries on by itself.</summary>
    private static readonly TimeSpan TypingQuiet = TimeSpan.FromSeconds(15);

    /// <summary>The agent's clicks and key presses are on hold because the user is typing to it (see <see cref="UserComposing"/>).</summary>
    public bool TypingPause => IsBusy && UserComposing();

    /// <summary>
    /// What the pause line says: that the agent waits, and the three ways it goes on again, with
    /// the seconds left until it does by itself. Someone who started typing and changed their mind
    /// had no way to tell how to let it carry on.
    /// </summary>
    public string TypingPauseText =>
        $"Paused while you type: my clicks and key presses wait. I carry on when you send this (Enter), click Resume, or in {Math.Max(1, (int)Math.Ceiling((TypingQuiet - (DateTime.UtcNow - _lastTyped)).TotalSeconds))} s without a key press.";

    /// <summary>Lets the agent carry on at once; what was typed stays in the box, to finish and send later.</summary>
    [RelayCommand]
    private void Resume()
    {
        _lastTyped = DateTime.MinValue;
        OnTypingPauseChanged();
    }

    /// <summary>
    /// The user is writing a message in the box: text in it, typed in the last 15 seconds. The
    /// agent's clicks and key presses wait meanwhile, so neither ends up in the other's window.
    /// Read from the agent's thread.
    /// </summary>
    private bool UserComposing() => Input.Length > 0 && DateTime.UtcNow - _lastTyped < TypingQuiet;

    /// <summary>The hint in the empty box: what Enter does now, and that pictures and files can go in too.</summary>
    public string InputHint => IsBusy
        ? "Tell me something while I work… (Enter to send; I pause while you type)"
        : "Describe a task, paste a screenshot or drop files… (Enter to send, Shift+Enter for a new line)";

    /// <summary>When the run in progress started: from the message that started it, for how long the run took.</summary>
    private DateTime _runStarted;

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

        try
        {
            var server = _settings.Server;
            // The server may offer other models than at the last check: the picked one back, or the one in use gone.
            if (Core.Llm.ServerAddress.Candidates(server.BaseUrl).Count > 0)
                await ProbeServerAsync(quiet: true, ct);
            if (Core.Llm.ServerAddress.Candidates(server.BaseUrl).Count == 0 || string.IsNullOrWhiteSpace(server.ModelInUse))
            {
                AddStopNotice("Enter your model server's address and pick a model at the top first, then press Continue.", isError: true, input);
                return null;
            }
            // LM Studio loads models on first use; do that now so we know the real context size.
            using (var probeHttp = LlmHttp.Create(TimeSpan.FromMinutes(10)))
            {
                var facts = await ServerProbe.EnsureLoadedAsync(probeHttp, server,
                    () => Dispatcher.UIThread.Post(() => AddNotice("Loading the model on the server…", isError: false)), ct,
                    note => Dispatcher.UIThread.Post(() => AddNotice(note, isError: false)));
                if (facts.Context is { } context)
                {
                    server.SetDetectedContext(context, facts.Hosted);
                    _modelNotLoaded = false;
                    OnContextChanged();
                    // Ollama and llama-server start models with 4k unless told otherwise; the run
                    // then resets after every step, or fails as too long. Not for a size near 32k
                    // (LM Studio's 32000 is 31k), which works.
                    if (context < ServerProbe.MinimumContext * 3 / 4 && !facts.Hosted)
                        AddNotice($"The server runs {server.ModelInUse} with a {context / 1024}k context, and BlackBox needs about {ServerProbe.MinimumContext / 1024}k: " +
                                  "its instructions and tools alone take about 9k tokens, and each screenshot about 3k. Load the model with a larger context on the server " +
                                  "(LM Studio: Context Length when you load it; Ollama: the Context length setting; llama-server: -c).", isError: false);
                }
            }
            if (!_mcpConnect.IsCompleted)
            {
                AddNotice("Starting the MCP servers…", isError: false);
                await _mcpConnect.WaitAsync(ct);
            }

            var session = _session = AgentSetup.Create(BackendConnection.Create(_http, server), _platform, _settings.Agent, _paths, _mcp, _ssh, history, UserComposing);
            // Taken now: another model can be picked at the top while the run goes on.
            var imagesKey = session.SeesImages ? ImagesKey(server) : null;
            foreach (var pending in _steerPending)
                session.Steer(pending);
            _steerPending.Clear();
            _tick.Start();
            delivered = true;
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
        catch (OperationCanceledException)
        {
            var stopped = StoppedText();
            if (!_runDetached) AddStopNotice(stopped, isError: false, delivered ? null : input);
        }
        catch (Exception ex)
        {
            // The run itself never throws; this is the preparation (an address that isn't one, a
            // server that doesn't answer), so Continue tries the message again.
            if (!_runDetached) AddStopNotice(ex.Message, isError: true, delivered ? null : input);
        }
        finally
        {
            late = [.. _session?.TakeUndelivered() ?? [], .. _steerPending];
            _steerPending.Clear();
            // After the run's last posted updates (its "Stopped." notice) have reached the chat.
            var chatId = _chatId;
            if (!_runDetached)
                Dispatcher.UIThread.Post(() => { if (_chatId == chatId) SaveChat(SnapshotHistory()); }, DispatcherPriority.Background);
            _tick.Stop();
            _session = null;
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

        if (_runDetached)
        {
            if (_forNextRun.Count == 0)
                return null;
            var next = UserInput.Join(_forNextRun);
            _forNextRun.Clear();
            return next;
        }
        if (late.Count == 0)
            return null;
        if (ct.IsCancellationRequested)
        {
            // Stopped: what the agent hadn't read yet comes off the chat and goes back in the box,
            // with its files under it, to send again or not.
            foreach (var unread in late)
                if (Items.LastOrDefault(i => i is UserMessageItem u && u.Text == unread.Text && u.Files.SequenceEqual(unread.Files)) is { } item)
                {
                    Items.Remove(item);
                    item.DisposePreviews();
                }
            Input = string.Join("\n\n", late.Select(l => l.Text).Append(Input).Where(t => t.Length > 0));
            Attach(late.SelectMany(l => l.Files));
            return null;
        }
        return UserInput.Join(late);
    }

    [RelayCommand(CanExecute = nameof(IsBusy))]
    private void Stop() => _runCts?.Cancel();

    /// <summary>Pause/Break, from the hook's thread: stops the run in progress, if any.</summary>
    private bool OnStopKey()
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
        _steerPending.Clear();
        // Left before they could run: back in the box, with their files, to send again or not.
        if (_forNextRun.Count > 0)
        {
            Input = string.Join("\n\n", _forNextRun.Select(m => m.Text).Append(Input).Where(t => t.Length > 0));
            Attach(_forNextRun.SelectMany(m => m.Files));
            _forNextRun.Clear();
        }
        // Logins belong to the chat: the next one starts logged out (the stopped run's tools fail).
        // Logging out talks to the servers, so it happens off the UI thread.
        var ended = _ssh;
        _ = Task.Run(ended.Dispose);
        _ssh = new Core.Ssh.SshHosts(_paths.KnownHostsFile);
        foreach (var item in Items)
            item.DisposePreviews();
        Items.Clear();
        _current = null;
        _chatId = null;
        _chatTitle = null;
        ContextUsage = "";
    }

    private void OpenChat(PastChat row)
    {
        if (row.Id == _chatId)
            return;
        // A chat left a moment ago may still be on its way to the disk.
        _chatSaver.Flush(TimeSpan.FromSeconds(10));
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
        var withFiles = new List<(ChatItemViewModel, Func<IReadOnlyList<string>>)>();
        foreach (var entry in chat.Entries)
        {
            var item = FromEntry(entry);
            Items.Add(item);
            // The files come back with the chat, as long as they are still there.
            if (entry.Kind == ChatEntryKind.Tool && entry.Files is { Count: > 0 } files)
            {
                // Kept for a save before they are looked up, which would otherwise drop them.
                ((ToolCallItem)item).SavedFiles = files;
                withFiles.Add((item, () => files.Where(Core.Media.FileMentions.Exists).ToList()));
            }
            else if (item is AssistantMessageItem { HasText: true, Text: var text })
                withFiles.Add((item, () => Core.Media.FileMentions.Find(text)));
        }
        ShowFilesLater(withFiles);
        // A chat that ended stopped can be carried on from where it stopped; an earlier stop it went on from can't.
        foreach (var notice in Items.OfType<NoticeItem>())
            notice.CanContinue = notice.Stopped && ReferenceEquals(notice, Items[^1]);
        MarkCurrentChat();
    }

    private void DeleteChat(PastChat row)
    {
        // Its last version on the disk, for its pictures; and none still on its way there afterwards.
        _chatSaver.Flush(TimeSpan.FromSeconds(10));
        // The pictures pasted into it go with it; files dropped on the window were the user's own and stay.
        var pasted = (_chats.Load(row.Id)?.Entries ?? []).Where(e => e.Kind == ChatEntryKind.User).SelectMany(e => e.Files ?? [])
            .Where(f => string.Equals(Path.GetDirectoryName(f), _paths.PastedDir, StringComparison.OrdinalIgnoreCase)).ToList();
        if (row.Id == _chatId)
            LeaveChat(save: false);
        _chatSaver.Forget(row.Id);
        _chats.Delete(row.Id);
        PastChats.Remove(row);
        OnPropertyChanged(nameof(HasPastChats));
        foreach (var file in pasted.Where(f => !Attachments.Any(a => string.Equals(a.Path, f, StringComparison.OrdinalIgnoreCase))))
        {
            try { File.Delete(file); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
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
        // A chat with nothing typed yet isn't saved. One whose first message the chat has dropped
        // to stay small (TrimChat, some 200 steps into a long run) still is: it stopped being
        // saved there, and the rest of the run was lost to History and to a crash.
        var first = Items.OfType<UserMessageItem>().FirstOrDefault();
        if (history is null || first is null && _chatTitle is null)
            return;
        _chatId ??= ChatStore.NewId();
        // A message that is only a file is named after it.
        _chatTitle ??= ChatStore.TitleFrom(first!.HasText ? first.Text : string.Join(", ", first.Attachments.Select(a => a.FileName)));
        var chat = new SavedChat
        {
            Id = _chatId,
            Title = _chatTitle,
            Updated = DateTime.UtcNow,
            // A reply bubble with nothing in it yet (a step just started) is left out.
            Entries = Items.Where(i => i is not AssistantMessageItem { IsStreaming: true, HasText: false, HasReasoning: false }).Select(ToEntry).ToList(),
            History = history,
        };
        // Written in the background: a long run's chat is megabytes, at every step.
        _chatSaver.Save(chat);

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

    private static ChatEntry ToEntry(ChatItemViewModel item)
    {
        var entry = item switch
        {
            UserMessageItem u => new ChatEntry { Kind = ChatEntryKind.User, Text = u.Text, Files = u.HasAttachments ? u.Files : null },
            AssistantMessageItem a => new ChatEntry
            {
                Kind = ChatEntryKind.Assistant, Text = a.Text, Reasoning = a.Reasoning, Ended = a.Ended, Sent = a.SentAt, FirstWords = a.FirstWords,
            },
            ToolCallItem t => new ChatEntry
            {
                Kind = ChatEntryKind.Tool, CallId = t.CallId, Name = t.Name, Summary = t.Summary, State = t.State.ToString(), Text = t.ResultText,
                Files = t.HasPreviews ? t.Previews.Select(p => p.Path).ToList() : t.SavedFiles?.ToList(), Ended = t.Ended,
            },
            NoticeItem n => new ChatEntry { Kind = ChatEntryKind.Notice, Text = n.Text, IsError = n.IsError, Stopped = n.Stopped, Unsent = n.Unsent },
            _ => throw new ArgumentOutOfRangeException(nameof(item)),
        };
        (entry.At, entry.RunTook) = (item.At, item.RunTook);
        return entry;
    }

    private ChatItemViewModel FromEntry(ChatEntry e)
    {
        ChatItemViewModel item = e.Kind switch
        {
            ChatEntryKind.User => new UserMessageItem(e.Text ?? "", e.Files),
            ChatEntryKind.Assistant => new AssistantMessageItem
            {
                Text = e.Text ?? "", Reasoning = e.Reasoning ?? "", IsStreaming = false, Ended = e.Ended, SentAt = e.Sent, FirstWords = e.FirstWords,
            },
            ChatEntryKind.Tool => new ToolCallItem(e.CallId ?? "", e.Name ?? "", e.Summary ?? "")
            {
                ResultText = e.Text ?? "",
                // A call that was still running or waiting when the chat was saved never finished.
                State = Enum.TryParse<ToolItemState>(e.State, out var state) && state is not (ToolItemState.Running or ToolItemState.AwaitingApproval)
                    ? state : ToolItemState.Stopped,
                Ended = e.Ended,
            },
            _ => NoticeFrom(e),
        };
        // Lines saved before the chat kept times show none.
        (item.At, item.RunTook) = (e.At, e.RunTook);
        return item;
    }

    /// <summary>
    /// A saved notice. One a stopped run left gets its Continue back; if the model never had the
    /// message before it, that is the message the chat shows last so far, which Continue sends again.
    /// </summary>
    private NoticeItem NoticeFrom(ChatEntry e)
    {
        if (!e.Stopped)
            return new NoticeItem(e.Text ?? "", e.IsError);
        if (e.Unsent && Items.OfType<UserMessageItem>().LastOrDefault() is { } message)
        {
            var again = new UserInput(message.Text, message.Files);
            return new NoticeItem(e.Text ?? "", e.IsError, () => _ = RunAgainAsync(again), unsent: true);
        }
        return new NoticeItem(e.Text ?? "", e.IsError, CarryOn);
    }

    // ---- settings -------------------------------------------------------------------

    public SettingsViewModel CreateSettingsViewModel() => new(_settings.Clone(), _paths, LoadMcpConfig(), _mcp.Statuses);

    /// <summary>
    /// mcp.json as it is, read again a moment later when caught while the agent replaces it:
    /// Settings didn't open then, and the window showed an error instead.
    /// </summary>
    private string LoadMcpConfig()
    {
        for (var attempt = 1; ; attempt++)
        {
            try { return McpConfig.Load(_paths.McpConfigFile); }
            catch (Exception ex) when (attempt < 5 && ex is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(50);
            }
        }
    }

    /// <summary>Shows an open Settings dialog the MCP servers as they are now: one the agent added or removed, and how each one is doing.</summary>
    public void RefreshMcp(SettingsViewModel dialog)
    {
        try { dialog.ShowCurrentMcp(McpConfig.Load(_paths.McpConfigFile), _mcp.Statuses); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* caught mid-save: next time */ }
    }

    public async Task ApplySettingsAsync(SettingsViewModel dialog)
    {
        var updated = dialog.Settings;
        // The server, key, model and context size live in the top bar, not in the dialog.
        updated.Server = _settings.Server;
        updated.Window = _settings.Window;
        _settings = updated;
        OnPropertyChanged(nameof(ApprovalNote));
        SaveSettings();
        // Written only when the user changed the list: a dialog left open while the agent added a
        // server would otherwise save the list without it.
        if (dialog.McpConfigText != dialog.McpConfigBase)
        {
            try
            {
                // What changed in the file meanwhile stays, unless the user changed the same server.
                var text = McpConfig.Merge(dialog.McpConfigBase, McpConfig.Load(_paths.McpConfigFile), dialog.McpConfigText);
                McpConfig.Save(_paths.McpConfigFile, text);
                if (IsBusy) _mcpRestartPending = true;
                else _mcpConnect = ConnectMcpAsync();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                AddNotice($"The MCP server list could not be saved: {ex.Message}", isError: true);
            }
        }
        await CheckServerAsync();
    }

    /// <summary>Keeps the window's size and place for the next start (see <see cref="Views.MainWindow.Restore"/>).</summary>
    public void SavePlacement(WindowPlacement placement)
    {
        _settings.Window = placement;
        SaveSettings();
    }

    private bool _saveFailed;

    /// <summary>Writes the settings; a disk that refuses (read-only, full) is reported once, and BlackBox goes on with them in memory.</summary>
    private void SaveSettings()
    {
        try
        {
            _store.Save(_settings);
            _saveFailed = false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (!_saveFailed) AddNotice($"Settings could not be saved, so they last only until BlackBox closes: {ex.Message}", isError: true);
            _saveFailed = true;
        }
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
            AddNotice($"The MCP server list could not be read, so no MCP tools are available: {ex.Message} Fix it in Settings.", isError: true);
            servers = [];
        }
        await Task.Run(() => _mcp.ConnectAsync(servers, CancellationToken.None));
        foreach (var s in _mcp.Statuses.Where(s => s.State == McpServerState.Failed))
            AddNotice($"MCP server \"{s.Name}\" is not available: {s.Error} (Settings)", isError: true);
    }

    private void AddNotice(string text, bool isError) => Items.Add(new NoticeItem(text, isError));

    private readonly HashSet<string> _problemsShown = [];

    /// <summary>Something no code caught (see <see cref="CrashLog"/>), said in the chat once, with where to read more.</summary>
    public void ShowProblem(string text)
    {
        if (_problemsShown.Add(text))
            AddNotice(text + @" Details are in Data\crash.log.", isError: true);
    }

    // ---- IAgentObserver (called from the agent thread) -------------------------------

    // A run can last hours. The model's context is kept in check by context resets; the chat
    // view is kept in check here so memory stays flat: old entries are dropped and only recent
    // screenshots keep their thumbnails.
    private const int MaxChatItems = 400;
    private const int MaxThumbnails = 30;

    private void TrimChat()
    {
        while (Items.Count > MaxChatItems)
        {
            Items[0].DisposePreviews();
            Items.RemoveAt(0);
        }
        foreach (var old in Items.OfType<ToolCallItem>().Where(t => t.HasScreenshot).SkipLast(MaxThumbnails).ToList())
        {
            old.Screenshot?.Dispose();
            old.Screenshot = null;
            old.ScreenshotData = null;
        }
    }


    // Every callback below is dropped once the run is detached: the chat it belongs to is no longer on screen.

    /// <summary>The reply bubble the agent streams into; only touched on the agent's thread.</summary>
    private AssistantMessageItem? _streaming;

    /// <summary>
    /// Hands an update from the agent's thread to the window with the time it happened: the window
    /// can get to it a moment later, and the chat shows how long things took.
    /// </summary>
    private static void PostTimed(Action<DateTime> update)
    {
        var at = DateTime.UtcNow;
        Dispatcher.UIThread.Post(() => update(at));
    }

    void IAgentObserver.OnStepStarted(int step)
    {
        // The agent waits for this call, so its conversation holds still while it is copied and measured.
        var history = _runDetached ? null : SnapshotHistory();
        var usage = MeasureContext();
        var at = DateTime.UtcNow;
        var bubble = _streaming = new AssistantMessageItem { At = at };
        Dispatcher.UIThread.Post(() =>
        {
            if (_runDetached) return;
            // Saved at every step, so a crash or power cut loses at most the step in progress.
            SaveChat(history);
            TrimChat();
            FinishCurrent(at); // a bubble nothing streamed into (a request that failed) leaves no "…" behind
            _current = bubble;
            Items.Add(bubble);
            ShowContextUsage(usage);
        });
    }

    void IAgentObserver.OnDelta(StreamDelta delta) => _streaming?.Append(delta);

    void IAgentObserver.OnSending(long sent, long total) => _streaming?.Sending(sent, total);

    void IAgentObserver.OnAssistantMessage(ChatMessage message)
    {
        // A reply carries the server's own token count, so the top bar is exact after each one.
        var usage = MeasureContext();
        PostTimed(at =>
        {
            if (_runDetached) return;
            FinishCurrent(at, message);
            ShowContextUsage(usage);
        });
    }

    /// <summary>How full the context is; on the agent's thread, while it waits and its conversation holds still.</summary>
    private (int Used, int Size, int Reset) MeasureContext() =>
        _session is { } session ? (session.TokensInUse(), session.ContextTokens, session.ResetThreshold) : default;

    private void ShowContextUsage((int Used, int Size, int Reset) usage)
    {
        if (usage.Size <= 0) return;
        ContextUsage = $"{usage.Used / 1000.0:0}k of {ContextSizes.Format(usage.Size)} used";
        ContextUsageTip = $"The conversation fills about {usage.Used:N0} of the model's {usage.Size:N0} tokens of context. " +
                          $"At {usage.Reset:N0} the model writes a handoff note and BlackBox starts a fresh context with it.";
    }

    /// <summary>
    /// Ends the streaming bubble, whose step ended <paramref name="at"/>. An empty one (a discarded
    /// reply) leaves no trace; one that only called tools keeps its line with the step's time.
    /// </summary>
    private void FinishCurrent(DateTime at, ChatMessage? message = null)
    {
        if (_current is null) return;
        _current.Finish(at, message);
        if (!_current.HasText && !_current.HasReasoning && message is not { HasToolCalls: true })
            Items.Remove(_current);
        else if (message is not null && _current.HasText)
        {
            // The files the reply names (what the agent made) are shown under it, to look at, play or open.
            var text = _current.Text;
            ShowFilesLater([(_current, () => Core.Media.FileMentions.Find(text))]);
        }
        _current = null;
    }

    /// <summary>
    /// Shows under each line the files it names, looked for off the UI thread: a path on a server
    /// that is off (\\nas\…, a mapped drive whose server is gone) holds up the look for half a
    /// minute, and the window froze for as long when a reply or a reopened chat named one.
    /// </summary>
    private async void ShowFilesLater(List<(ChatItemViewModel Item, Func<IReadOnlyList<string>> Files)> lines)
    {
        try
        {
            var found = await Task.Run(() => lines.Select(l => (l.Item, Files: l.Files())).Where(l => l.Files.Count > 0).ToList());
            foreach (var (item, files) in found)
                if (Items.Contains(item))
                    item.AddPreviews(files, _previews);
        }
        catch (Exception ex)
        {
            CrashLog.Write("the files a chat names", ex);
        }
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

    void IAgentObserver.OnNotice(string text) => PostTimed(at =>
    {
        if (_runDetached) return;
        var notice = new NoticeItem(text, isError: false) { At = at };
        // One line for a wait that goes on (a busy or unreachable server), not one per try: no
        // step starts meanwhile to trim the chat, and an hour of tries filled it.
        if (text.EndsWith(WaitNoticeEnd, StringComparison.Ordinal) && Items.Count > 0 && Items[^1] is NoticeItem { Text: var last } && last.EndsWith(WaitNoticeEnd, StringComparison.Ordinal))
            Items[^1] = notice;
        else
            Items.Add(notice);
    });

    /// <summary>How AgentSession's notices of a wait before trying again end.</summary>
    private const string WaitNoticeEnd = "(Stop ends the wait)…";

    void IAgentObserver.OnToolStarted(ToolCall call, string summary) => PostTimed(at =>
    {
        if (_runDetached) return;
        var item = FindOrAddTool(call, summary);
        item.Summary = summary;
        // Its time counts from here: a wait for the user's OK before it is not the call's.
        item.At = at;
        item.State = ToolItemState.Running;
    });

    void IAgentObserver.OnToolProgress(ToolCall call, string text) => Dispatcher.UIThread.Post(() =>
    {
        if (_runDetached) return;
        if (Items.OfType<ToolCallItem>().LastOrDefault(t => t.CallId == call.Id) is { State: ToolItemState.Running } item)
            item.Progress = text;
    });

    void IAgentObserver.OnToolFinished(ToolCall call, ToolResult result) => PostTimed(at =>
    {
        if (_runDetached) return;
        var item = FindOrAddTool(call, call.Name);
        item.ResultText = result.Text.Length > 3000 ? result.Text[..3000] + "\n…" : result.Text.TrimEnd();
        item.Progress = "";
        // Files the call made or showed get a preview (a player, a model to turn), which says more
        // than the picture made for the model; anything else keeps its screenshot.
        if (result.Files is { Count: > 0 } files)
        {
            // Looked for in the background, as a reply's: a ComfyUI save_to or a document on a share.
            item.SavedFiles = files;
            ShowFilesLater([(item, () => files.Where(Core.Media.FileMentions.Exists).ToList())]);
        }
        else if (result.Image is not null)
        {
            // An MCP server's picture can be one the window doesn't read (an SVG, a broken file):
            // the call's card goes without it rather than the window closing.
            try
            {
                using var ms = new MemoryStream(result.Image);
                item.Screenshot = Bitmap.DecodeToWidth(ms, 480);
                item.ScreenshotData = result.Image;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException) { }
        }
        if (item.State == ToolItemState.AwaitingApproval)
            item.State = ToolItemState.Stopped; // the run was stopped while the call waited for an OK; Deny would have said Declined
        else
        {
            item.Ended = at;
            if (item.State is not (ToolItemState.Denied or ToolItemState.Stopped))
                item.State = result.IsError ? ToolItemState.Error : ToolItemState.Done;
        }
    });

    /// <summary>Set when the run is ending on a bug (see <see cref="IAgentObserver.OnFailed"/>), whose stack trace went into the crash log.</summary>
    private volatile bool _failureLogged;

    void IAgentObserver.OnFailed(Exception error)
    {
        CrashLog.Write("the agent", error);
        _failureLogged = true;
    }

    void IAgentObserver.OnFinished(AgentStopReason reason, string? detail)
    {
        // Read here: by the time the window gets to this, a run of what came too late for this one may have started.
        var started = _runStarted;
        var logged = _failureLogged;
        _failureLogged = false;
        PostTimed(at =>
        {
            var stopped = StoppedText();
            if (_runDetached) return;
            FinishCurrent(at);
            // A call cut short by the stop says so; it used to read "Declined", as if the user had said no to it.
            foreach (var t in Items.OfType<ToolCallItem>().Where(t => t.State is ToolItemState.Running or ToolItemState.AwaitingApproval))
            {
                if (t.State == ToolItemState.Running) t.Ended = at;
                t.State = ToolItemState.Stopped;
            }

            switch (reason)
            {
                case AgentStopReason.Cancelled:
                    AddStopNotice(stopped, isError: false);
                    break;
                case AgentStopReason.Error:
                    AddStopNotice((detail ?? "Something went wrong.") + (logged ? @" Details are in Data\crash.log." : ""), isError: true);
                    break;
            }

            // How long the run took, under the line it ended with: its last reply, or the notice it
            // stopped with. A message of the user's that came too late for it is the next run's.
            if (Items.LastOrDefault(i => i is not UserMessageItem && i.At >= started) is { } end)
                end.RunTook = at - started;
        });
    }

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
