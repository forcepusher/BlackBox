using Sloperator.Core.Mcp;
using Sloperator.Core.Platform;
using Sloperator.Core.Settings;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Sloperator.App.ViewModels;

/// <summary>Edits a copy of the settings; the window hands it back to the main view model on Save.</summary>
/// <param name="screen">The primary screen, to show what each screenshot size makes of it; null when it can't be read.</param>
public sealed partial class SettingsViewModel(AppSettings settings, AppPaths paths, string mcpConfigText, IReadOnlyList<McpServerStatus> mcpStatuses,
    ScreenRect? screen = null) : ObservableObject
{
    public AppSettings Settings { get; } = settings;

    /// <summary>The settings as they were when the dialog opened: what differs from them in <see cref="Settings"/> is what the user changed.</summary>
    public AppSettings Opened { get; } = settings.Clone();

    public AppPaths Paths { get; } = paths;

    public AgentSettings Agent => Settings.Agent;

    public PanelSettings Panels => Settings.Panels;

    public NotifySettings Notify => Settings.Notify;

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

    // ---- Screenshot size ---------------------------------------------------------------

    /// <summary>The choices in the order the dropdown lists them.</summary>
    private static readonly ScreenshotSize[] Sizes = [ScreenshotSize.UpTo1080, ScreenshotSize.Full, ScreenshotSize.UpTo720];

    /// <summary>Each choice with the size its screenshots of the primary screen come out at, so it's never a guess.</summary>
    public string[] ScreenshotSizes { get; } = Sizes.Select(size =>
    {
        var name = size switch
        {
            ScreenshotSize.Full => "Full resolution, more context per screenshot",
            ScreenshotSize.UpTo720 => "720p tall, fewer tokens but clicks miss more",
            _ => "Up to 1080p tall (recommended)",
        };
        if (screen is not { } s)
            return name;
        var space = new ScreenSpace(s, new AgentSettings { ScreenshotSize = size }.ScreenshotBox);
        return $"{name}: {space.ImageWidth} × {space.ImageHeight}";
    }).ToArray();

    public int ScreenshotSizeIndex
    {
        get => Math.Max(0, Array.IndexOf(Sizes, Agent.ScreenshotSize));
        set => Agent.ScreenshotSize = Sizes[Math.Clamp(value, 0, Sizes.Length - 1)];
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
