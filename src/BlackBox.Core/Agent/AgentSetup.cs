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
    /// <param name="desktopTurn">Shared by the agents of BlackBox's windows, for one at a time with the mouse and keyboard; null for one agent alone.</param>
    public static AgentSession Create(BackendConnection backend, IPlatformServices platform, AgentSettings agent, AppPaths paths,
        McpHost mcp, SshHosts ssh, List<ChatMessage>? history = null, Func<bool>? userComposing = null, DesktopTurn? desktopTurn = null)
    {
        var mcpSetup = new McpSetup(mcp, paths.McpConfigFile);
        var comfy = Comfy.ComfyHub.For(paths.ComfyDir);
        var ctx = new ToolContext
        {
            Platform = platform,
            Settings = agent,
            WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            CoordinateGrid = ModelCoordinates.GridFor(backend.Options.Model),
            MaxScreenshotSide = OpenAiChatClient.IsAnthropic(backend.Client.BaseUrl) ? ClaudeModel.FromName(backend.Options.Model).ScreenshotSide : null,
            SeesImages = backend.Vision,
            UserComposing = userComposing,
            DesktopTurn = desktopTurn,
            Browser = HeadlessBrowser.Find(paths.BrowserProfileDir),
            Ssh = ssh,
            Mcp = mcpSetup,
            Comfy = comfy,
            Media = Media.MediaDecoders.Create(platform, paths, comfy),
            ScratchDirectory = paths.ScratchDir,
            FilmsDirectory = paths.FilmsDir,
            ToolsDirectory = paths.ToolsDir,
            IndexDirectory = paths.IndexDir,
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
        var registry = ToolRegistry.CreateDefault(includeScreenTools: screens is not null, extra: [new McpServersTool(), .. mcp.Tools]);
        mcpSetup.Registry = registry;
        string Prompt() => SystemPrompt.Build(platform.Name, screens, ctx.WorkingDirectory, agent.CustomInstructions, mcp.Connected, webSearch: true, paths.ToolsDir, paths.ScratchDir);
        var session = new AgentSession(backend.Client, backend.Options, registry, ctx, Prompt(), history, backend.ContextTokens);
        // A server the agent adds or removes: the prompt then lists the servers as the next
        // message's will, in the same request whose tools change anyway.
        mcpSetup.ToolsChanged = () => session.UsePrompt(Prompt());
        return session;
    }
}
