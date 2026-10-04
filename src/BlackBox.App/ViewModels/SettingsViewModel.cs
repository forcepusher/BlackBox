using BlackBox.Core.Mcp;
using BlackBox.Core.Settings;
using CommunityToolkit.Mvvm.ComponentModel;

namespace BlackBox.App.ViewModels;

/// <summary>Edits a copy of the settings; the window hands it back to the main view model on Save.</summary>
public sealed partial class SettingsViewModel(AppSettings settings, AppPaths paths, string mcpConfigText, IReadOnlyList<McpServerStatus> mcpStatuses) : ObservableObject
{
    public AppSettings Settings { get; } = settings;
    public AppPaths Paths { get; } = paths;

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

    /// <summary>NumericUpDown works in decimals.</summary>
    public decimal? InputLogSeconds
    {
        get => Agent.InputLogSeconds;
        set => Agent.InputLogSeconds = (int)Math.Clamp(value ?? AgentSettings.DefaultInputLogSeconds, 1, 300);
    }

    public int ApprovalIndex
    {
        get => (int)Agent.Approval;
        set => Agent.Approval = (ApprovalMode)Math.Clamp(value, 0, ApprovalModes.Length - 1);
    }

    // ---- MCP servers (Advanced) -------------------------------------------------------

    /// <summary>The text of Data/mcp.json as the user edits it; saved as typed.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(McpError), nameof(HasMcpError))]
    private string _mcpConfigText = mcpConfigText;

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
    public string McpStatusText { get; } = string.Join("\n", mcpStatuses.Select(s => s.Summary));

    public bool HasMcpStatus => McpStatusText.Length > 0;

    public string McpExample { get; } =
        "{\n  \"mcpServers\": {\n    \"filesystem\": {\n      \"command\": \"npx\",\n      \"args\": [\"-y\", \"@modelcontextprotocol/server-filesystem\", \"C:\\\\Users\\\\me\\\\Documents\"]\n    },\n" +
        "    \"remote\": {\n      \"url\": \"https://example.com/mcp\",\n      \"headers\": { \"Authorization\": \"Bearer …\" }\n    }\n  }\n}";
}
