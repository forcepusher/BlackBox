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
