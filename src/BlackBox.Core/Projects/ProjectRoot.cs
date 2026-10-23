namespace BlackBox.Core.Projects;

/// <summary>
/// The project a file or folder belongs to: the nearest folder above it with a .git, or else
/// with a solution or an engine's project (Unity, Unreal, Godot), or else with a package or build
/// file (package.json, Cargo.toml, a .csproj…). The home folder and drives are never one.
/// </summary>
internal static class ProjectRoot
{
    /// <summary>The project <paramref name="path"/> is in and what makes it one ("a git repository"); null when it isn't in one.</summary>
    public static (string Root, string Why)? Find(string path)
    {
        string? dir;
        try
        {
            dir = FolderIndex.Normalize(path);
            if (!Directory.Exists(dir))
                dir = Path.GetDirectoryName(dir);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
        var home = FolderIndex.Normalize(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        (string, string)? solution = null, package = null;
        for (; dir is not null; dir = Path.GetDirectoryName(dir))
        {
            if (Path.GetPathRoot(dir) == dir || string.Equals(dir, home, StringComparison.OrdinalIgnoreCase))
                break;
            // Inside node_modules or .git, the project is the one around it.
            if (FolderWalk.AlwaysLeftOut.Contains(Path.GetFileName(dir)))
            {
                solution = package = null;
                continue;
            }
            try
            {
                if (Directory.Exists(Path.Combine(dir, ".git")) || File.Exists(Path.Combine(dir, ".git")))
                    return (dir, "a git repository");
                if (solution is null && SolutionKind(dir) is { } s)
                    solution = (dir, s);
                if (package is null && PackageKind(dir) is { } p)
                    package = (dir, p);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
        return solution ?? package;
    }

    private static string? SolutionKind(string dir)
    {
        if (Directory.EnumerateFiles(dir, "*.sln").Any() || Directory.EnumerateFiles(dir, "*.slnx").Any()) return "a .NET solution";
        if (Directory.Exists(Path.Combine(dir, "Assets")) && Directory.Exists(Path.Combine(dir, "ProjectSettings"))) return "a Unity project";
        if (Directory.EnumerateFiles(dir, "*.uproject").Any()) return "an Unreal project";
        if (File.Exists(Path.Combine(dir, "project.godot"))) return "a Godot project";
        return null;
    }

    private static readonly string[] PackageFiles =
    [
        "package.json", "Cargo.toml", "go.mod", "pyproject.toml", "setup.py", "pom.xml", "build.gradle", "build.gradle.kts",
        "CMakeLists.txt", "composer.json", "Gemfile", "pubspec.yaml", "deno.json",
    ];

    private static string? PackageKind(string dir)
    {
        foreach (var name in PackageFiles)
            if (File.Exists(Path.Combine(dir, name)))
                return $"it has {name}";
        if (Directory.EnumerateFiles(dir, "*.csproj").Any() || Directory.EnumerateFiles(dir, "*.fsproj").Any()) return "a .NET project";
        return null;
    }

    /// <summary>Whether the folder has at least <paramref name="count"/> files of its own (generated folders left out), counting no further.</summary>
    public static bool HasFiles(string root, int count)
    {
        var found = 0;
        var pending = new Stack<string>([root]);
        var looked = 0;
        while (pending.Count > 0 && looked++ < 2_000)
        {
            var dir = pending.Pop();
            try
            {
                foreach (var entry in new DirectoryInfo(dir).EnumerateFileSystemInfos())
                {
                    if (entry is FileInfo && ++found >= count)
                        return true;
                    if (entry is DirectoryInfo d && !FolderWalk.AlwaysLeftOut.Contains(d.Name) && !FolderWalk.IsLink(d))
                        pending.Push(d.FullName);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
        return false;
    }
}
