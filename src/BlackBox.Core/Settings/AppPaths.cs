namespace BlackBox.Core.Settings;

/// <summary>
/// Portable layout: everything BlackBox owns lives next to the executable.
/// <code>
/// BlackBox/
///   BlackBox.exe
///   Data/settings.json
///   Data/logs/
///   Data/runtime/  (native libraries unpacked by the single-file build)
///   Workspace/     (default working directory for file and shell tools)
/// </code>
/// </summary>
public sealed class AppPaths
{
    public AppPaths(string root)
    {
        Root = Path.GetFullPath(root);
    }

    /// <summary>Uses the folder of the running executable.</summary>
    public static AppPaths ForCurrentApp() => new(AppContext.BaseDirectory);

    public string Root { get; }
    public string DataDir => Path.Combine(Root, "Data");
    public string SettingsFile => Path.Combine(DataDir, "settings.json");
    public string LogsDir => Path.Combine(DataDir, "logs");

    /// <summary>Where the single-file build unpacks its native libraries.</summary>
    public string RuntimeDir => Path.Combine(DataDir, "runtime");
    public string WorkspaceDir => Path.Combine(Root, "Workspace");

    public void EnsureCreated()
    {
        Directory.CreateDirectory(DataDir);
        Directory.CreateDirectory(LogsDir);
        Directory.CreateDirectory(WorkspaceDir);
    }

    /// <summary>
    /// Stores paths inside the app folder as relative so the folder can be moved or copied
    /// to another drive or PC and keep working.
    /// </summary>
    public string ToPortable(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return "";
        var full = Path.GetFullPath(path, Root);
        var rel = Path.GetRelativePath(Root, full);
        return rel.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(rel) ? full : rel;
    }

    public string Resolve(string path) =>
        string.IsNullOrWhiteSpace(path) ? "" : Path.GetFullPath(Environment.ExpandEnvironmentVariables(path), Root);
}
