using System.Collections.ObjectModel;
using BlackBox.Core.Llm;
using BlackBox.Core.Settings;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BlackBox.App.ViewModels;

/// <summary>Edits a copy of the settings; the window hands it back to the main view model on Save.</summary>
public sealed partial class SettingsViewModel(AppSettings settings, AppPaths paths) : ObservableObject
{
    public AppSettings Settings { get; } = settings;
    public AppPaths Paths { get; } = paths;

    public ServerSettings Server => Settings.Server;
    public AgentSettings Agent => Settings.Agent;

    public string[] ApprovalModes { get; } =
    [
        "Ask before every action",
        "Ask before shell commands and file changes",
        "Never ask",
    ];

    public int ApprovalIndex
    {
        get => (int)Agent.Approval;
        set => Agent.Approval = (ApprovalMode)Math.Clamp(value, 0, 2);
    }

    public string WorkingDirectory
    {
        get => Agent.WorkingDirectory;
        set { Agent.WorkingDirectory = Paths.ToPortable(value); OnPropertyChanged(); }
    }

    public string Model
    {
        get => Server.Model;
        set { Server.Model = value.Trim(); OnPropertyChanged(); }
    }

    public bool SupportsVision
    {
        get => Server.SupportsVision;
        set { Server.SupportsVision = value; OnPropertyChanged(); }
    }

    public int ContextSize
    {
        get => Server.ContextSize;
        set { Server.ContextSize = value; OnPropertyChanged(); }
    }

    /// <summary>Resets everything on the Advanced tab, keeping the choices made on General.</summary>
    public void RestoreAdvancedDefaults()
    {
        var old = Settings.Agent;
        Settings.Agent = new AgentSettings
        {
            Approval = old.Approval,
            FailsafeCorner = old.FailsafeCorner,
            HideFromScreenshots = old.HideFromScreenshots,
            CustomInstructions = old.CustomInstructions,
        };
        Settings.Server.MaxPromptTokens = new ServerSettings().MaxPromptTokens;
    }

    [ObservableProperty]
    private string _testResult = "";

    /// <summary>Models the server reported; filled by Test connection.</summary>
    public ObservableCollection<string> Models { get; } = [];

    [RelayCommand]
    private async Task TestConnectionAsync()
    {
        TestResult = "Connecting…";
        using var http = LlmHttp.Create(TimeSpan.FromSeconds(20));
        var probe = await ServerProbe.CheckAsync(http, Server, CancellationToken.None);
        if (!probe.Reachable)
        {
            TestResult = probe.Message;
            return;
        }

        Models.Clear();
        foreach (var m in probe.Models.Where(m => !m.Contains("embed", StringComparison.OrdinalIgnoreCase)))
            Models.Add(m);
        if (Models.Count > 0 && !Models.Contains(Server.Model))
            Model = Models[0];

        // Ask again now that a model is chosen, to learn the context it runs with.
        var facts = await ServerProbe.TryContextAsync(http, Server, CancellationToken.None);
        if (facts.Context is { } c)
            ContextSize = c;
        if (facts.Vision is { } v)
            SupportsVision = v;
        TestResult = $"Connected. {Models.Count} model(s) available." +
                     (facts.Context is { } k ? $" \"{Server.Model}\" has a {k / 1024}k context." : "") +
                     (facts.Loaded == false ? " It isn't loaded yet; it will load on first use." : "") +
                     (facts.Vision == false ? " This model can't see images, so screen control is off." : "");
    }
}
