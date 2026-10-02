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
        set { Local.ModelPath = Paths.ToPortable(value); OnPropertyChanged(); OnPropertyChanged(nameof(ModelInfo)); }
    }

    public string MmprojPath
    {
        get => Local.MmprojPath;
        set { Local.MmprojPath = Paths.ToPortable(value); OnPropertyChanged(); OnPropertyChanged(nameof(ModelInfo)); }
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
        Settings.Agent = new AgentSettings
        {
            Approval = oldAgent.Approval,
            FailsafeCorner = oldAgent.FailsafeCorner,
            HideWindowWhileWorking = oldAgent.HideWindowWhileWorking,
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
                return "Not downloaded yet. Use the Download button in the main window.";
            var gb = new FileInfo(model).Length / 1024d / 1024 / 1024;
            var vision = string.IsNullOrWhiteSpace(Local.MmprojPath)
                ? "Vision is off (no projector file), so it cannot see the screen."
                : File.Exists(Paths.Resolve(Local.MmprojPath)) ? "Can see the screen." : "Vision file missing; press Download in the main window.";
            return $"Installed ({gb:0.0} GB). {vision}";
        }
    }

    [ObservableProperty]
    private string _remoteTestResult = "";

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
                : $"Connected. {models.Count} model(s): {string.Join(", ", models.Take(8))}{(models.Count > 8 ? ", …" : "")}";
            if (string.IsNullOrWhiteSpace(Remote.Model) && models.Count > 0)
                RemoteModel = models[0];
        }
        catch (Exception ex)
        {
            RemoteTestResult = "Failed: " + ex.Message;
        }
    }
}
