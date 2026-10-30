using Sloperator.Core.Mcp;
using Sloperator.Core.Settings;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Sloperator.App.ViewModels;

/// <summary>Edits a copy of the settings; the window hands it back to the main view model on Save.</summary>
public sealed partial class SettingsViewModel(AppSettings settings, AppPaths paths, string mcpConfigText, IReadOnlyList<McpServerStatus> mcpStatuses) : ObservableObject
{
    public AppSettings Settings { get; } = settings;

    /// <summary>The settings as they were when the dialog opened: what differs from them in <see cref="Settings"/> is what the user changed.</summary>
    public AppSettings Opened { get; } = settings.Clone();

    public AppPaths Paths { get; } = paths;

    public AgentSettings Agent => Settings.Agent;

    public PanelSettings Panels => Settings.Panels;

    public NotifySettings Notify => Settings.Notify;

    public UpdateSettings Updates => Settings.Updates;

    /// <summary>Sloperator updating itself: Check for updates asks it now, and the line beside the button says what it found.</summary>
    public required Updater Updater { get; init; }

    [RelayCommand]
    private Task CheckForUpdatesAsync() => Updater.CheckAsync();

    public string[] ApprovalModes { get; } =
    [
        "Ask before every action",
        "Ask before shell commands and file changes",
        "Never ask",
    ];

    public int ApprovalIndex
    {
        get => (int)Agent.Approval;
        set => Agent.Approval = (ApprovalMode)Math.Clamp(value, 0, ApprovalModes.Length - 1);
    }

    // ---- Text recognition --------------------------------------------------------------

    /// <summary>The languages Windows reads the screen's text in, for find_text; null where the OS has no text recognition.</summary>
    public IReadOnlyList<string>? TextLanguages { get; init; }

    public bool ShowsTextRecognition => TextLanguages is not null;

    public bool HasTextRecognition => TextLanguages is { Count: > 0 };

    public string TextRecognition => HasTextRecognition ? string.Join(", ", TextLanguages!) : "Not installed";

    /// <summary>Windows' language settings, where a language's text recognition is added with it.</summary>
    [RelayCommand]
    private static void OpenLanguageSettings()
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ms-settings:regionlanguage") { UseShellExecute = true })?.Dispose(); }
        catch (System.ComponentModel.Win32Exception) { }
    }

    // ---- Screenshot size ---------------------------------------------------------------

    /// <summary>In <see cref="ScreenshotSize"/> order.</summary>
    public string[] ScreenshotSizes { get; } = ["Up to 720p tall", "Up to 1080p tall", "Native"];

    public int ScreenshotSizeIndex
    {
        get => (int)Agent.ScreenshotSize;
        set => Agent.ScreenshotSize = (ScreenshotSize)Math.Clamp(value, 0, ScreenshotSizes.Length - 1);
    }

    // ---- MCP servers -------------------------------------------------------------------

    /// <summary>The text of Data/mcp.json as the user edits it; saved as typed.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(McpError), nameof(HasMcpError))]
    private string _mcpConfigText = mcpConfigText;

    /// <summary>The file's text as the box was last filled from it; Save writes the box only if the user changed it from this.</summary>
    public string McpConfigBase { get; private set; } = mcpConfigText;

    /// <summary>
    /// The file and the servers as they are now, while the dialog is open: a box the user hasn't
    /// touched shows a server the agent just added; one they are editing keeps their edit, and
    /// Save merges the two.
    /// </summary>
    public void ShowCurrentMcp(string fileText, IReadOnlyList<McpServerStatus> statuses)
    {
        if (fileText != McpConfigBase && McpConfigText == McpConfigBase)
        {
            McpConfigBase = fileText;
            McpConfigText = fileText;
        }
        McpStatusText = Summarize(statuses);
    }

    /// <summary>What is wrong with the text, checked as the user types; Save is refused while it is set.</summary>
    public string McpError
    {
        get
        {
            try { McpConfig.Parse(McpConfigText); return ""; }
            catch (McpConfigException ex) { return ex.Message; }
        }
    }

    public bool HasMcpError => McpError.Length > 0;

    /// <summary>How each configured server is doing right now, one line each.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMcpStatus))]
    private string _mcpStatusText = Summarize(mcpStatuses);

    public bool HasMcpStatus => McpStatusText.Length > 0;

    private static string Summarize(IReadOnlyList<McpServerStatus> statuses) => string.Join("\n", statuses.Select(s => s.Summary));

    public string McpExample { get; } =
        "{\n  \"mcpServers\": {\n    \"filesystem\": {\n      \"command\": \"npx\",\n      \"args\": [\"-y\", \"@modelcontextprotocol/server-filesystem\", \"C:\\\\Users\\\\me\\\\Documents\"]\n    },\n" +
        "    \"remote\": {\n      \"url\": \"https://example.com/mcp\",\n      \"headers\": { \"Authorization\": \"Bearer …\" }\n    }\n  }\n}";
}
