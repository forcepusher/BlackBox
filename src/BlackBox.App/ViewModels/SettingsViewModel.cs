using BlackBox.Core.Settings;
using CommunityToolkit.Mvvm.ComponentModel;

namespace BlackBox.App.ViewModels;

/// <summary>Edits a copy of the settings; the window hands it back to the main view model on Save.</summary>
public sealed class SettingsViewModel(AppSettings settings, AppPaths paths) : ObservableObject
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

    /// <summary>Labels for <see cref="ScreenshotResolution"/>, in enum order.</summary>
    public string[] ResolutionOptions { get; } =
    [
        "1280 × 720",
        "1600 × 900",
        "1920 × 1080 (default)",
        "2560 × 1440",
        "Full screen resolution",
    ];

    public int ResolutionIndex
    {
        get => (int)Agent.ScreenshotResolution;
        set => Agent.ScreenshotResolution = (ScreenshotResolution)Math.Clamp(value, 0, ResolutionOptions.Length - 1);
    }

    public int ApprovalIndex
    {
        get => (int)Agent.Approval;
        set => Agent.Approval = (ApprovalMode)Math.Clamp(value, 0, 2);
    }
}