using BlackBox.Core.Llm;
using BlackBox.Core.Mcp;
using BlackBox.Core.Platform;
using BlackBox.Core.Settings;
using BlackBox.Core.Ssh;
using BlackBox.Core.Tools;
using BlackBox.Core.Web;

namespace BlackBox.Core.Agent;

/// <summary>Puts together a run on the desktop the same way for the app and the CLI.</summary>
public static class AgentSetup
{
    /// <param name="history">The conversation to carry on; a new one when null.</param>
    /// <param name="ssh">The chat's SSH logins, kept by the caller across the chat's runs and disposed with it.</param>
    /// <param name="userComposing">Whether the user is typing a message to the agent right now (see <see cref="ToolContext.UserComposing"/>).</param>
    public static AgentSession Create(BackendConnection backend, IPlatformServices platform, AgentSettings agent, AppPaths paths,
        McpHost mcp, SshHosts ssh, List<ChatMessage>? history = null, Func<bool>? userComposing = null)
    {
        var mcpSetup = new McpSetup(mcp, paths.McpConfigFile);
        var ctx = new ToolContext
        {
            Platform = platform,
            Settings = agent,
            WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            CoordinateGrid = ModelCoordinates.GridFor(backend.Options.Model),
            SeesImages = backend.Vision,
            UserComposing = userComposing,
            Browser = agent.WebSearch ? HeadlessBrowser.Find(paths.BrowserProfileDir) : null,
            Ssh = ssh,
            Mcp = mcpSetup,
        };

        // A model that can't see gets no screen tools, nor does an OS without desktop control.
        IReadOnlyList<ScreenSpace>? screens = null;
        if (backend.Vision)
        {
            try { screens = ctx.ScreenSpaces(); }
            catch (NotSupportedException) { }
        }

        // Made up front: a script written there from the shell fails when the folder isn't there.
        try { Directory.CreateDirectory(paths.ScratchDir); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }

        // The MCP tools are a snapshot: the tool list stays the same for the whole run, unless the
        // agent sets up a server itself (mcp_servers), whose tools then join it.
        var registry = ToolRegistry.CreateDefault(includeScreenTools: screens is not null, extra: [new McpServersTool(), .. mcp.Tools], includeWebTools: agent.WebSearch);
        mcpSetup.Registry = registry;
        var prompt = SystemPrompt.Build(platform.Name, screens, ctx.WorkingDirectory, agent.CustomInstructions, mcp.Connected, agent.WebSearch, paths.ToolsDir, paths.ScratchDir);
        return new AgentSession(backend.Client, backend.Options, registry, ctx, prompt, history, backend.ContextTokens);
    }
}
