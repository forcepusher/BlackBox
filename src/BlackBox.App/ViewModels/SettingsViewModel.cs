using BlackBox.Core.Llm;
using BlackBox.Core.Settings;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BlackBox.App.ViewModels;

/// <summary>Edits a copy of the settings; the window hands it back to the main view model on Save.</summary>
public sealed partial class SettingsViewModel(AppSettings settings, AppPaths paths, LlamaServerHost server) : ObservableObject
{
    public AppSettings Settings { get; } = settings;
    public AppPaths Paths { get; } = paths;

    public LocalModelSettings Local => Settings.Local;
    public RemoteApiSettings Remote => Settings.Remote;
    public AgentSettings Agent => Settings.Agent;

    public string[] KvCacheTypes { get; } = ["f16", "q8_0", "q4_0"];
    public string[] ApprovalModes { get; } =
    [
        "Ask before every action",
        "Ask before shell commands and file changes",
        "Never ask",
    ];

    public bool IsLocal
    {
        get => Settings.Backend == BackendKind.Local;
        set
        {
            if (!value) return;
            Settings.Backend = BackendKind.Local;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsRemote));
        }
    }

    public bool IsRemote
    {
        get => Settings.Backend == BackendKind.Remote;
        set
        {
            if (!value) return;
            Settings.Backend = BackendKind.Remote;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsLocal));
        }
    }

    public int ApprovalIndex
    {
        get => (int)Agent.Approval;
        set => Agent.Approval = (ApprovalMode)Math.Clamp(value, 0, 2);
    }

    public string ServerPath
    {
        get => Local.ServerPath;
        set { Local.ServerPath = Paths.ToPortable(value); OnPropertyChanged(); OnPropertyChanged(nameof(ServerInfo)); }
    }

    public string ModelPath
    {
        get => Local.ModelPath;
        set { Local.ModelPath = Paths.ToPortable(value); MarkCustom(); OnPropertyChanged(); OnPropertyChanged(nameof(ModelInfo)); }
    }

    public string MmprojPath
    {
        get => Local.MmprojPath;
        set { Local.MmprojPath = Paths.ToPortable(value); MarkCustom(); OnPropertyChanged(); OnPropertyChanged(nameof(ModelInfo)); }
    }

    // ---- model picker ---------------------------------------------------------------

    public sealed record ModelChoice(string Id, string Title, string Subtitle);

    public IReadOnlyList<ModelChoice> ModelChoices =>
    [
        .. ModelCatalog.All.Select(m => new ModelChoice(m.Id, m.DisplayName,
            $"{m.Description} {(File.Exists(Paths.Resolve(m.ModelPath)) ? "Downloaded." : $"Download: {m.DownloadBytes / 1e9:0.0} GB.")}")),
        new ModelChoice(ModelCatalog.CustomId, "Custom GGUF file", "Pick your own model and vision files under Advanced."),
    ];

    public ModelChoice? SelectedModelChoice
    {
        get => ModelChoices.FirstOrDefault(c => c.Id == Local.SelectedModel) ?? ModelChoices[^1];
        set
        {
            if (value is null || value.Id == Local.SelectedModel) return;
            if (ModelCatalog.Find(value.Id) is { } model)
                ModelCatalog.Apply(model, Local);
            else
                Local.SelectedModel = ModelCatalog.CustomId;
            OnPropertyChanged();
            foreach (var name in new[] { nameof(ModelPath), nameof(MmprojPath), nameof(ModelInfo), nameof(ModelName), nameof(Local) })
                OnPropertyChanged(name);
        }
    }

    private void MarkCustom()
    {
        var match = ModelCatalog.All.FirstOrDefault(m => m.ModelPath == Local.ModelPath && m.MmprojPath == Local.MmprojPath);
        var id = match?.Id ?? ModelCatalog.CustomId;
        if (id == Local.SelectedModel) return;
        Local.SelectedModel = id;
        OnPropertyChanged(nameof(SelectedModelChoice));
    }

    public string WorkingDirectory
    {
        get => Agent.WorkingDirectory;
        set { Agent.WorkingDirectory = Paths.ToPortable(value); OnPropertyChanged(); }
    }

    public string RemoteModel
    {
        get => Remote.Model;
        set { Remote.Model = value.Trim(); OnPropertyChanged(); }
    }

    public string ModelName => Path.GetFileNameWithoutExtension(Local.ModelPath);

    /// <summary>Resets everything on the Advanced tab, keeping the choices made on General.</summary>
    public void RestoreAdvancedDefaults()
    {
        var oldLocal = Settings.Local;
        var oldAgent = Settings.Agent;
        Settings.Local = new LocalModelSettings { AutoStart = oldLocal.AutoStart };
        if (ModelCatalog.Find(oldLocal.SelectedModel) is { } chosen)
            ModelCatalog.Apply(chosen, Settings.Local); // keep the model picked on General
        Settings.Agent = new AgentSettings
        {
            Approval = oldAgent.Approval,
            FailsafeCorner = oldAgent.FailsafeCorner,
            HideFromScreenshots = oldAgent.HideFromScreenshots,
            CustomInstructions = oldAgent.CustomInstructions,
        };
    }

    public string ServerInfo => server.LocateServer(Local) is { } exe
        ? "Using " + exe
        : "llama-server not found. Put llama.cpp in the 'llama' folder or pick llama-server.exe.";

    public string ModelInfo
    {
        get
        {
            var model = Paths.Resolve(Local.ModelPath);
            if (!File.Exists(model))
                return "Not downloaded yet. After saving, press Download in the main window.";
            var gb = new FileInfo(model).Length / 1024d / 1024 / 1024;
            var vision = string.IsNullOrWhiteSpace(Local.MmprojPath)
                ? "Vision is off (no projector file), so it cannot see the screen."
                : File.Exists(Paths.Resolve(Local.MmprojPath)) ? "Can see the screen." : "Vision file missing; press Download in the main window.";
            return $"Installed ({gb:0.0} GB). {vision}";
        }
    }

    [ObservableProperty]
    private string _remoteTestResult = "";

    /// <summary>Models the server reported; filled by Test connection.</summary>
    public System.Collections.ObjectModel.ObservableCollection<string> RemoteModels { get; } = [];

    [RelayCommand]
    private async Task TestRemoteAsync()
    {
        RemoteTestResult = "Connecting…";
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            var models = await new OpenAiChatClient(http, Remote.BaseUrl, Remote.ApiKey).ListModelsAsync(CancellationToken.None);
            RemoteTestResult = models.Count == 0
                ? "Connected, but the server listed no models."
                : $"Connected. The server offers {models.Count} model(s); pick one in the Model box.";
            RemoteModels.Clear();
            foreach (var m in models.Where(m => !m.Contains("embed", StringComparison.OrdinalIgnoreCase)))
                RemoteModels.Add(m);
            if (RemoteModels.Count > 0 && !RemoteModels.Contains(Remote.Model))
                RemoteModel = RemoteModels[0];
        }
        catch (Exception ex)
        {
            RemoteTestResult = "Failed: " + ex.Message;
        }
    }
}
