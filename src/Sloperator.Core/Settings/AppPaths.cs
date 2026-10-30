namespace Sloperator.Core.Settings;

/// <summary>
/// Portable layout: everything Sloperator owns lives next to the executable.
/// <code>
/// Sloperator/
///   Sloperator.exe
///   Data/settings.json
///   Data/mcp.json  (MCP servers, if any)
///   Data/chats/    (saved chats, one file each)
///   Data/browser/  (profile of the headless browser web search falls back on)
///   Data/runtime/  (native libraries unpacked by the single-file build)
///   Data/known_hosts (host keys of the servers the agent logged in to over SSH)
///   Data/tools/    (portable programs the agent downloaded for its tasks)
///   Data/scratch/  (helper files the agent made for its tasks: scripts and the like)
///   Data/pasted/   (pictures pasted into the chat from the clipboard)
///   Data/videos/    (one folder per video: its storyboard, frames, shots, song and the video)
///   Data/index/    (the indexes of the folders the agent indexed, a file each)
///   Data/errors.log (every error Sloperator showed, see <see cref="ErrorLog"/>)
/// </code>
/// </summary>
public sealed class AppPaths(string root)
{
    public string Root { get; } = Path.GetFullPath(root);
    public string DataDir => Path.Combine(Root, "Data");
    public string SettingsFile => Path.Combine(DataDir, "settings.json");

    /// <summary>The MCP server list, in the format MCP READMEs show (see <see cref="Mcp.McpConfig"/>).</summary>
    public string McpConfigFile => Path.Combine(DataDir, "mcp.json");

    /// <summary>Profile of the headless browser the web tools fall back on (see <see cref="Web.HeadlessBrowser"/>).</summary>
    public string BrowserProfileDir => Path.Combine(DataDir, "browser");

    /// <summary>Saved chats, one file each (see <see cref="Chats.ChatStore"/>).</summary>
    public string ChatsDir => Path.Combine(DataDir, "chats");

    /// <summary>Where the single-file build unpacks its native libraries.</summary>
    public string RuntimeDir => Path.Combine(DataDir, "runtime");

    /// <summary>The SSH host keys Sloperator has seen, in OpenSSH's known_hosts format (see <see cref="Ssh.SshHosts"/>).</summary>
    public string KnownHostsFile => Path.Combine(DataDir, "known_hosts");

    /// <summary>When Sloperator last asked GitHub for a new version, so a start within the hour doesn't ask again (see <see cref="Web.SelfUpdate"/>).</summary>
    public string UpdateCheckFile => Path.Combine(DataDir, "update-check.txt");

    /// <summary>The version whose download wasn't kept, signed by another publisher than the running exe, so it isn't downloaded again (see <see cref="Web.SelfUpdate.Verify"/>).</summary>
    public string UpdateRejectedFile => Path.Combine(DataDir, "update-rejected.txt");

    /// <summary>Portable programs the agent downloads to open what nothing on the PC opens (a PDF reader's text extractor, say), kept for later tasks.</summary>
    public string ToolsDir => Path.Combine(DataDir, "tools");

    /// <summary>Helper files the agent makes only to get a task done (scripts, a reminder's script), instead of the user's folders.</summary>
    public string ScratchDir => Path.Combine(DataDir, "scratch");

    /// <summary>
    /// Pictures pasted into the chat (a screenshot from the clipboard), saved as files so the agent
    /// can work on them like any other; files dropped on the window stay where they are.
    /// </summary>
    public string PastedDir => Path.Combine(DataDir, "pasted");

    /// <summary>
    /// The videos planned in the Video generator panel or asked for in the chat, a folder each (see
    /// <see cref="Video.Storyboard"/>): portable, like everything else of Sloperator's (the user's
    /// choice), rather than in the PC's Videos folder.
    /// </summary>
    public string VideosDir => Path.Combine(DataDir, "videos");

    /// <summary>The indexes of the folders the agent indexed, a file each (see <see cref="Projects.FolderIndex"/>).</summary>
    public string IndexDir => Path.Combine(DataDir, "index");

    /// <summary>ComfyUI's address and folder as last used, the log of a server Sloperator started, and the browser profile its page is loaded in (see <see cref="Comfy.ComfyHub"/>).</summary>
    public string ComfyDir => Path.Combine(DataDir, "comfyui");

    public void EnsureCreated() => Directory.CreateDirectory(DataDir);

    /// <summary>
    /// The portable root: SLOPERATOR_HOME if set, otherwise the folder of the executable. With
    /// <paramref name="useRepositoryDevFolder"/>, a build running inside the repository uses
    /// <c>.dev/</c> there instead, so the debug app and the CLI share one Data folder.
    /// </summary>
    public static string FindRoot(bool useRepositoryDevFolder)
    {
        if (Environment.GetEnvironmentVariable("SLOPERATOR_HOME") is { Length: > 0 } home)
            return home;
        if (useRepositoryDevFolder)
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
                if (dir.EnumerateFiles("Sloperator.sln*").Any())
                    return Path.Combine(dir.FullName, ".dev");
        return AppContext.BaseDirectory;
    }
}
