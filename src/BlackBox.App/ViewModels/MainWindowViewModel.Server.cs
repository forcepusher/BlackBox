using System.Collections.ObjectModel;
using Avalonia.Threading;
using BlackBox.Core.Llm;
using BlackBox.Core.Settings;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BlackBox.App.ViewModels;

public sealed partial class MainWindowViewModel
{
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

    /// <summary>Last slider position: 1M, or a server's own size when it reports more.</summary>
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
            : OpenAiChatClient.IsAnthropic(_settings.Server.BaseUrl)
                ? auto == AnthropicMessages.DefaultModel
                    ? "Picked by BlackBox: Claude Opus 5.5, Anthropic's recommended model. Pick one yourself to keep using it."
                    : "Picked by BlackBox: the newest Opus, Sonnet or Haiku the key may use. Pick one yourself to keep using it."
            : $"Picked by BlackBox as the strongest model the server offers{AutoModelDetails}: the best score on an agent benchmark, " +
              "else the most parameters, then the most bits per weight. Pick one yourself to keep using it.";

    /// <summary>
    /// The effort dropdown's levels: those the model in use takes, least to most; all of them,
    /// greyed out, for a model that takes none, so the one picked still shows.
    /// </summary>
    public ObservableCollection<EffortOption> EffortOptions { get; } = new(Efforts.Levels.Select(l => new EffortOption(l)));

    private bool _refreshingEfforts;

    /// <summary>The effort picked (medium until another is), or the nearest one the model takes. Saved; used from the next message on.</summary>
    public EffortOption? SelectedEffort
    {
        get => new(_settings.Server.EffortInUse ?? _settings.Server.Effort);
        set
        {
            if (_refreshingEfforts || value is null || value == SelectedEffort) return;
            _settings.Server.Effort = value.Level;
            SaveSettings();
            OnPropertyChanged();
            OnPropertyChanged(nameof(EffortToolTip));
        }
    }

    /// <summary>The server says the model in use takes an effort; otherwise none is sent and the dropdown is greyed out.</summary>
    public bool CanSetEffort => _settings.Server.EffortLevels.Count > 0;

    public string EffortToolTip
    {
        get
        {
            var server = _settings.Server;
            var model = server.ModelInUse is { Length: > 0 } m ? m : "The model";
            if (CanSetEffort)
                return $"How hard {model} thinks before it acts: more effort is slower and, on a paid API, costs more. "
                       + "Medium unless you pick another; a change applies from your next message.";
            var lmStudio = !OpenAiChatClient.IsAnthropic(server.BaseUrl) && Uri.TryCreate(OpenAiChatClient.NormalizeBaseUrl(server.BaseUrl), UriKind.Absolute, out var uri) && uri.Port == 1234;
            return lmStudio
                ? $"{model} doesn't take an effort setting here: LM Studio lists none for it, so how much it thinks is its model setting in LM Studio."
                : $"{model} doesn't take an effort setting, or the server doesn't say it does, so BlackBox sends none.";
        }
    }

    /// <summary>The levels of the model in use, after a check: the list changes in place, so the dropdown keeps its selection.</summary>
    private void OnEffortsChanged()
    {
        var wanted = CanSetEffort ? _settings.Server.EffortLevels : Efforts.Levels;
        _refreshingEfforts = true;
        for (var i = 0; i < wanted.Count; i++)
            if (EffortOptions.All(o => o.Level != wanted[i]))
                EffortOptions.Insert(Math.Min(i, EffortOptions.Count), new EffortOption(wanted[i]));
        OnPropertyChanged(nameof(SelectedEffort));
        for (var i = EffortOptions.Count - 1; i >= 0; i--)
            if (!wanted.Contains(EffortOptions[i].Level))
                EffortOptions.RemoveAt(i);
        _refreshingEfforts = false;
        OnPropertyChanged(nameof(SelectedEffort));
        OnPropertyChanged(nameof(CanSetEffort));
        OnPropertyChanged(nameof(EffortToolTip));
    }

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
        server.EffortLevels = probe.Efforts ?? [];
        OnEffortsChanged();
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
        // Once each time the server stops answering or says why not, rather than at every check while it stays so.
        var problem = state == ServerState.Failed ? $"{status}: {setup}" : null;
        if (problem is not null && problem != _problemLogged)
            Core.ErrorLog.Write($"the server at {_settings.Server.BaseUrl}", problem);
        if (state != ServerState.Checking)
            _problemLogged = problem;
        ServerState = state;
        ServerStatusText = status;
        ShowSetupBanner = setup is not null;
        SetupText = setup ?? "";
        SetupTitle = title;
        OffersKilo = setup is not null && state is ServerState.Failed or ServerState.Unknown && !KiloGateway.Is(_settings.Server.BaseUrl);
    }

    /// <summary>What the banner said last about a server that failed, as written to the error log.</summary>
    private string? _problemLogged;

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
}
