namespace BlackBox.Core.Settings;

/// <summary>
/// Portable layout: everything BlackBox owns lives next to the executable.
/// <code>
/// BlackBox/
///   BlackBox.exe
///   Data/settings.json
///   Data/runtime/  (native libraries unpacked by the single-file build)
/// </code>
/// </summary>
public sealed class AppPaths(string root)
{
    public string Root { get; } = Path.GetFullPath(root);
    public string DataDir => Path.Combine(Root, "Data");
    public string SettingsFile => Path.Combine(DataDir, "settings.json");

    /// <summary>Where the single-file build unpacks its native libraries.</summary>
    public string RuntimeDir => Path.Combine(DataDir, "runtime");

    public void EnsureCreated() => Directory.CreateDirectory(DataDir);
}
