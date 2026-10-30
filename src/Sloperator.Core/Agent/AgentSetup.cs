using Sloperator.Core.Llm;
using Sloperator.Core.Mcp;
using Sloperator.Core.Platform;
using Sloperator.Core.Settings;
using Sloperator.Core.Ssh;
using Sloperator.Core.Tools;
using Sloperator.Core.Web;

namespace Sloperator.Core.Agent;

/// <summary>Puts together a run on the desktop the same way for the app and the CLI.</summary>
public static class AgentSetup
{
    /// <param name="history">The conversation to carry on; a new one when null.</param>
    /// <param name="ssh">The chat's SSH logins, kept by the caller across the chat's runs and disposed with it.</param>
    /// <param name="userComposing">Whether the user is typing a message to the agent right now (see <see cref="ToolContext.UserComposing"/>).</param>
    /// <param name="desktopTurn">Shared by the agents of Sloperator's windows, for one at a time with the mouse and keyboard; null for one agent alone.</param>
    /// <param name="desktopSeat">The window the agent works in (see <see cref="ToolContext.DesktopSeat"/>).</param>
    /// <param name="folderIndex">Whether the agent has index_folder (its tool, prompt line and note); off only to measure what the index changes.</param>
    /// <param name="changes">The chat's list of the files the agent changed (see <see cref="ToolContext.Changes"/>).</param>
    /// <param name="modelServer">The app's top bar, for the agent to switch the model Sloperator runs on (model_server); null for none.</param>
    public static AgentSession Create(BackendConnection backend, IPlatformServices platform, AgentSettings agent, AppPaths paths,
        McpHost mcp, SshHosts ssh, List<ChatMessage>? history = null, Func<bool>? userComposing = null, DesktopTurn? desktopTurn = null,
        object? desktopSeat = null, bool folderIndex = true, Diffs.FileChanges? changes = null, IModelServerSwitch? modelServer = null)
    {
        var mcpSetup = new McpSetup(mcp, paths.McpConfigFile);
        var comfy = Comfy.ComfyHub.For(paths.ComfyDir);
        var ctx = new ToolContext
        {
            Platform = platform,
            Settings = agent,
            WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            MaxScreenshotSide = OpenAiChatClient.IsAnthropic(backend.Client.BaseUrl) ? ClaudeModel.FromName(backend.Options.Model).ScreenshotSide : null,
            SeesImages = backend.Vision,
            UserComposing = userComposing,
            DesktopTurn = desktopTurn,
            DesktopSeat = desktopSeat,
            Browser = HeadlessBrowser.Find(paths.BrowserProfileDir),
            Ssh = ssh,
            Mcp = mcpSetup,
            Comfy = comfy,
            Media = Media.MediaDecoders.Create(platform, paths, comfy),
            ScratchDirectory = paths.ScratchDir,
            VideosDirectory = paths.VideosDir,
            ToolsDirectory = paths.ToolsDir,
            IndexDirectory = paths.IndexDir,
            Changes = changes,
            ModelServer = modelServer,
        };

        // A model that can't see gets no screen tools, nor does an OS without desktop control, nor
        // a user who turned screenshots off in Settings.
        IReadOnlyList<ScreenSpace>? screens = null;
        if (backend.Vision && agent.EnableScreenshots)
        {
            try { screens = ctx.ScreenSpaces(); }
            catch (NotSupportedException) { }
        }

        // Made up front: a script written there from the shell fails when the folder isn't there.
        try { Directory.CreateDirectory(paths.ScratchDir); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }

        // The MCP tools are a snapshot: the tool list stays the same for the whole run, unless the
        // agent sets up a server itself (mcp_servers), whose tools then join it.
        var registry = ToolRegistry.CreateDefault(includeScreenTools: screens is not null, extra: [new McpServersTool(), .. (modelServer is null ? (Tool[])[] : [new ModelServerTool()]), .. mcp.Tools], includeIndex: folderIndex,
            includeControl: agent.Controls, includeFindText: agent.EnableTextRecognition);
        mcpSetup.Registry = registry;
        string Prompt() => SystemPrompt.Build(platform.Name, screens, ctx.WorkingDirectory, agent.CustomInstructions, mcp.Connected, webSearch: true, paths.ToolsDir, paths.ScratchDir,
            folderIndex, control: agent.Controls, modelServer: modelServer is not null, findText: agent.EnableTextRecognition);
        var session = new AgentSession(backend.Client, backend.Options, registry, ctx, Prompt(), history, backend.ContextTokens);
        // A server the agent adds or removes: the prompt then lists the servers as the next
        // message's will, in the same request whose tools change anyway.
        mcpSetup.ToolsChanged = () => session.UsePrompt(Prompt());
        return session;
    }
}
