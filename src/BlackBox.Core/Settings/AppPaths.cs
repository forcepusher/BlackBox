namespace BlackBox.Core.Settings;

/// <summary>
/// Portable layout: everything BlackBox owns lives next to the executable.
/// <code>
/// BlackBox/
///   BlackBox.exe
///   Data/settings.json
///   Data/mcp.json  (MCP servers, if any)
///   Data/chats/    (saved chats, one file each)
///   Data/browser/  (profile of the headless browser web search falls back on)
///   Data/runtime/  (native libraries unpacked by the single-file build)
///   Data/known_hosts (host keys of the servers the agent logged in to over SSH)
///   Data/tools/    (portable programs the agent downloaded for its tasks)
///   Data/scratch/  (helper files the agent made for its tasks: scripts and the like)
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

    /// <summary>The SSH host keys BlackBox has seen, in OpenSSH's known_hosts format (see <see cref="Ssh.SshHosts"/>).</summary>
    public string KnownHostsFile => Path.Combine(DataDir, "known_hosts");

    /// <summary>Portable programs the agent downloads to open what nothing on the PC opens (a PDF reader's text extractor, say), kept for later tasks.</summary>
    public string ToolsDir => Path.Combine(DataDir, "tools");

    /// <summary>Helper files the agent makes only to get a task done (scripts, a reminder's script), instead of the user's folders.</summary>
    public string ScratchDir => Path.Combine(DataDir, "scratch");

    public void EnsureCreated() => Directory.CreateDirectory(DataDir);

    /// <summary>
    /// The portable root: BLACKBOX_HOME if set, otherwise the folder of the executable. With
    /// <paramref name="useRepositoryDevFolder"/>, a build running inside the repository uses
    /// <c>.dev/</c> there instead, so the debug app and the CLI share one Data folder.
    /// </summary>
    public static string FindRoot(bool useRepositoryDevFolder)
    {
        if (Environment.GetEnvironmentVariable("BLACKBOX_HOME") is { Length: > 0 } home)
            return home;
        if (useRepositoryDevFolder)
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
                if (dir.EnumerateFiles("BlackBox.sln*").Any())
                    return Path.Combine(dir.FullName, ".dev");
        return AppContext.BaseDirectory;
    }
}
