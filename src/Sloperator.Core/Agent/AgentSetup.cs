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
        // The screens as the model points at them now: in pixels, or on its grid once measured.
        string Prompt() => SystemPrompt.Build(platform.Name, screens is null ? null : ctx.ScreenSpaces(), ctx.WorkingDirectory, agent.CustomInstructions, mcp.Connected, webSearch: true,
            paths.ToolsDir, paths.ScratchDir, folderIndex, control: agent.Controls, modelServer: modelServer is not null, findText: agent.EnableTextRecognition);
        var session = new AgentSession(backend.Client, backend.Options, registry, ctx, Prompt(), history, backend.ContextTokens);
        // A server the agent adds or removes: the prompt then lists the servers as the next
        // message's will, in the same request whose tools change anyway.
        mcpSetup.ToolsChanged = () => session.UsePrompt(Prompt());
        // Before a chat's first click: does this model point in pixels or on a grid? Measured once
        // a chat (and again after a switch to another model), before its first request is made.
        if (screens is not null && agent.Controls)
            session.BeforeRun = async (observer, ct) =>
            {
                var model = backend.Options.Model ?? "";
                if (!Pointing.TryGetValue(session.History, out var measured) || measured.Model != model)
                {
                    var space = ctx.ScreenSpaces()[0];
                    var answer = await new FreshLook(backend.Client, backend.Options)
                        .AskAsync([PointingCalibration.Picture(space.ImageWidth, space.ImageHeight)], PointingCalibration.Question(space.ImageWidth, space.ImageHeight), ct)
                        .ConfigureAwait(false);
                    measured = new(model, PointingCalibration.Read(answer, space.ImageWidth, space.ImageHeight));
                    Pointing.AddOrUpdate(session.History, measured);
                    if (measured.Grid is { } grid)
                        observer.OnNotice($"This model points on a 0-{grid} grid rather than in pixels, so its clicks are read on that grid.");
                }
                if (ctx.CoordinateGrid != measured.Grid)
                {
                    ctx.CoordinateGrid = measured.Grid;
                    session.UsePrompt(Prompt());
                }
            };
        return session;
    }

    /// <summary>How a chat's model points, by the chat's history: a run per message shares it.</summary>
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<List<ChatMessage>, Measured> Pointing = new();

    private sealed record Measured(string Model, int? Grid);
}
