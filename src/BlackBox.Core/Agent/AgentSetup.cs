using BlackBox.Core.Llm;
using BlackBox.Core.Mcp;
using BlackBox.Core.Platform;
using BlackBox.Core.Settings;
using BlackBox.Core.Tools;
using BlackBox.Core.Web;

namespace BlackBox.Core.Agent;

/// <summary>Puts together a run on the desktop the same way for the app and the CLI.</summary>
public static class AgentSetup
{
    /// <param name="history">The conversation to carry on; a new one when null.</param>
    public static AgentSession Create(BackendConnection backend, IPlatformServices platform, AgentSettings agent, AppPaths paths,
        McpHost mcp, List<ChatMessage>? history = null)
    {
        var ctx = new ToolContext
        {
            Platform = platform,
            Settings = agent,
            WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            CoordinateGrid = ModelCoordinates.GridFor(backend.Options.Model),
            Browser = agent.WebSearch ? HeadlessBrowser.Find(paths.BrowserProfileDir) : null,
        };

        // A model that can't see gets no screen tools, nor does an OS without desktop control.
        IReadOnlyList<ScreenSpace>? screens = null;
        if (backend.Vision)
        {
            try { screens = ctx.ScreenSpaces(); }
            catch (NotSupportedException) { }
        }

        // The MCP tools are a snapshot: the tool list stays the same for the whole run.
        var registry = ToolRegistry.CreateDefault(includeScreenTools: screens is not null, extra: mcp.Tools, includeWebTools: agent.WebSearch);
        var prompt = SystemPrompt.Build(platform.Name, screens, ctx.WorkingDirectory, agent.CustomInstructions, mcp.Connected, agent.WebSearch);
        return new AgentSession(backend.Client, backend.Options, registry, ctx, prompt, history, backend.ContextTokens);
    }
}
