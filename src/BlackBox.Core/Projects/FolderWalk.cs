using System.Text;
using System.Text.RegularExpressions;

namespace BlackBox.Core.Projects;

/// <summary>A file the walk found: its path under the root (with '/'), size and last write time.</summary>
internal readonly record struct WalkedFile(string Path, long Size, long Modified);

/// <summary>
/// The files of a folder that belong to it, as a developer sees it: what its .gitignore files
/// leave out is left out, and so are the folders tools fill by themselves (.git, node_modules,
/// a .NET project's bin and obj, a Unity project's Library, an Unreal project's Intermediate…),
/// which can hold a hundred times the files the project itself has.
/// </summary>
internal sealed class FolderWalk
{
    /// <summary>Folders left out wherever they are: version control, package caches, editors' own state.</summary>
    internal static readonly HashSet<string> AlwaysLeftOut = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".hg", ".svn", "node_modules", "bower_components", "__pycache__", ".vs", ".idea", ".gradle",
        ".next", ".nuxt", ".svelte-kit", ".pytest_cache", ".mypy_cache", ".ruff_cache", ".tox", ".cache",
        ".terraform", ".dart_tool", ".godot", ".import", ".parcel-cache", ".turbo",
    };

    /// <summary>Most entries one walk looks at: a drive or a home folder isn't a project.</summary>
    internal const int MaxEntries = 200_000;

    private readonly string? _skip;
    private readonly CancellationToken _ct;

    /// <summary>What was left out, by why: "bin, obj (build output)", for the overview.</summary>
    public SortedDictionary<string, int> LeftOut { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Whether the walk stopped at <see cref="MaxEntries"/>.</summary>
    public bool Stopped { get; private set; }

    private int _entries;

    private FolderWalk(string? skip, CancellationToken ct)
    {
        _skip = skip is null ? null : Path.GetFullPath(skip).TrimEnd('\\', '/');
        _ct = ct;
    }

    /// <summary>The files under <paramref name="root"/> that belong to the project, in no particular order.</summary>
    /// <param name="skip">A folder left out wherever it is: BlackBox's own index folder.</param>
    public static (List<WalkedFile> Files, FolderWalk Walk) Run(string root, CancellationToken ct, string? skip = null)
    {
        var walk = new FolderWalk(skip, ct);
        var files = new List<WalkedFile>();
        walk.Walk(new DirectoryInfo(root), "", [], files);
        return (files, walk);
    }

    private void Walk(DirectoryInfo dir, string rel, List<IgnoreFile> rules, List<WalkedFile> files)
    {
        _ct.ThrowIfCancellationRequested();
        FileSystemInfo[] entries;
        try { entries = dir.GetFileSystemInfos(); }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException) { return; }

        if (rel.Length > 0 && entries.Any(e => e is FileInfo && e.Name.Equals("pyvenv.cfg", StringComparison.OrdinalIgnoreCase)))
        {
            Count($"{dir.Name} (a Python environment)");
            return;
        }
        if (entries.Any(e => e is FileInfo && e.Name.Equals(".gitignore", StringComparison.OrdinalIgnoreCase)) &&
            IgnoreFile.Read(Path.Combine(dir.FullName, ".gitignore"), rel) is { } here)
            rules = [.. rules, here];
        var generated = GeneratedFolders(entries);

        foreach (var entry in entries)
        {
            if (++_entries > MaxEntries)
            {
                Stopped = true;
                return;
            }
            var path = rel.Length == 0 ? entry.Name : rel + "/" + entry.Name;
            var isDir = entry is DirectoryInfo;
            if (isDir)
            {
                // A link or junction to elsewhere would walk another folder, or the same one forever.
                if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    continue;
                if (AlwaysLeftOut.Contains(entry.Name))
                {
                    Count(entry.Name);
                    continue;
                }
                if (_skip is not null && entry.FullName.Equals(_skip, StringComparison.OrdinalIgnoreCase))
                {
                    Count($"{entry.Name} (BlackBox's indexes)");
                    continue;
                }
                if (generated.TryGetValue(entry.Name, out var why))
                {
                    Count($"{entry.Name} ({why})");
                    continue;
                }
            }
            if (IgnoreFile.Ignored(rules, path, isDir))
            {
                Count(".gitignore");
                continue;
            }
            if (entry is DirectoryInfo sub)
                Walk(sub, path, rules, files);
            else if (entry is FileInfo f && !f.Attributes.HasFlag(FileAttributes.System))
                files.Add(new WalkedFile(path, f.Length, f.LastWriteTimeUtc.Ticks));
            if (Stopped)
                return;
        }
    }

    private void Count(string why) => LeftOut[why] = LeftOut.GetValueOrDefault(why) + 1;

    /// <summary>
    /// The folders next to a project file that its tools make: bin and obj beside a .csproj,
    /// Library and Temp beside a Unity project's Assets and ProjectSettings, target beside
    /// Cargo.toml… Only beside the file that makes them: elsewhere a "build" or "bin" folder can
    /// hold sources (a Rails app's bin has its scripts).
    /// </summary>
    internal static Dictionary<string, string> GeneratedFolders(FileSystemInfo[] entries)
    {
        var names = entries.Select(e => e.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var extensions = entries.OfType<FileInfo>().Select(f => f.Extension.ToLowerInvariant()).ToHashSet();
        var found = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        void Add(string why, params string[] folders)
        {
            foreach (var folder in folders)
                found.TryAdd(folder, why);
        }
        // A Unity project has the .csproj files Unity writes, and its Packages folder is its own.
        var unity = names.Contains("Assets") && names.Contains("ProjectSettings");
        if (unity)
            Add("Unity's own", "Library", "Temp", "Logs", "UserSettings", "obj", "Build", "Builds", "MemoryCaptures");
        if (extensions.Overlaps([".csproj", ".fsproj", ".vbproj", ".vcxproj", ".sln", ".slnx"]))
            Add("build output", unity ? new[] { "bin", "obj" } : new[] { "bin", "obj", "x64", "Debug", "Release", "TestResults", "packages" });
        if (extensions.Contains(".uproject") || extensions.Contains(".uplugin"))
            Add("Unreal's own", "Binaries", "Intermediate", "Saved", "DerivedDataCache");
        if (names.Contains("Cargo.toml") || names.Contains("pom.xml"))
            Add("build output", "target");
        if (names.Contains("build.gradle") || names.Contains("build.gradle.kts") || names.Contains("CMakeLists.txt"))
            Add("build output", "build", "out", "cmake-build-debug", "cmake-build-release");
        if (names.Contains("package.json"))
            Add("build output", "dist", "build", "out", "coverage", ".output");
        if (names.Contains("pubspec.yaml"))
            Add("build output", "build");
        return found;
    }
}

/// <summary>One .gitignore file's rules, for the paths under its folder.</summary>
internal sealed class IgnoreFile
{
    private readonly string _base;
    private readonly List<Rule> _rules;

    /// <summary>
    /// A rule, as a regular expression, or for the usual kinds a name ("build") or an ending
    /// ("*.log") compared without one: a project of 37,000 files with 60 .gitignore files took
    /// most of two seconds to walk with a regular expression for every rule.
    /// </summary>
    private sealed record Rule(Regex? Pattern, string? Name, string? Ending, bool Negated, bool DirOnly)
    {
        public bool Matches(string local, string name) =>
            Name is not null ? name.Equals(Name, StringComparison.OrdinalIgnoreCase)
            : Ending is not null ? name.EndsWith(Ending, StringComparison.OrdinalIgnoreCase)
            : Pattern!.IsMatch(local);
    }

    private IgnoreFile(string @base, List<Rule> rules)
    {
        _base = @base;
        _rules = rules;
    }

    /// <summary>The rules of the .gitignore at <paramref name="path"/>, for the folder <paramref name="relDir"/> under the root; null when there is none.</summary>
    public static IgnoreFile? Read(string path, string relDir)
    {
        string[] lines;
        try
        {
            if (!File.Exists(path))
                return null;
            lines = File.ReadAllLines(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
        return Parse(lines, relDir);
    }

    private static readonly char[] Wildcards = ['*', '?', '[', '\\'];

    internal static IgnoreFile? Parse(IEnumerable<string> lines, string relDir)
    {
        var rules = new List<Rule>();
        foreach (var raw in lines)
        {
            var line = raw.TrimEnd();
            if (line.Length == 0 || line.StartsWith('#'))
                continue;
            var negated = line.StartsWith('!');
            if (negated)
                line = line[1..];
            if (line.StartsWith(@"\#") || line.StartsWith(@"\!"))
                line = line[1..];
            var dirOnly = line.EndsWith('/');
            line = line.TrimEnd('/');
            if (line.Length == 0)
                continue;
            // A slash at the start or in the middle ties the pattern to this folder; otherwise it
            // matches a name at any depth below it.
            var anchored = line.Contains('/');
            line = line.TrimStart('/');
            var wild = line.IndexOfAny(Wildcards);
            if (!anchored && wild < 0)
                rules.Add(new Rule(null, line, null, negated, dirOnly));
            else if (!anchored && wild == 0 && line.Length > 1 && line.IndexOfAny(Wildcards, 1) < 0)
                rules.Add(new Rule(null, null, line[1..], negated, dirOnly));
            else
            {
                var regex = (anchored ? "^" : "^(?:.*/)?") + GlobToRegex(line) + "$";
                try { rules.Add(new Rule(new Regex(regex, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), null, null, negated, dirOnly)); }
                catch (ArgumentException) { }
            }
        }
        return rules.Count == 0 ? null : new IgnoreFile(relDir, rules);
    }

    /// <summary>Whether the last rule that matches <paramref name="path"/> (under the root, with '/') leaves it out.</summary>
    public static bool Ignored(List<IgnoreFile> files, string path, bool isDir)
    {
        bool? ignored = null;
        var name = path[(path.LastIndexOf('/') + 1)..];
        foreach (var file in files)
        {
            if (file._base.Length > 0 && !(path.Length > file._base.Length && path[file._base.Length] == '/' && path.StartsWith(file._base, StringComparison.OrdinalIgnoreCase)))
                continue;
            var local = file._base.Length == 0 ? path : path[(file._base.Length + 1)..];
            foreach (var rule in file._rules)
                if ((!rule.DirOnly || isDir) && rule.Matches(local, name))
                    ignored = !rule.Negated;
        }
        return ignored == true;
    }

    internal static string GlobToRegex(string glob)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < glob.Length; i++)
        {
            var c = glob[i];
            if (c == '*' && i + 1 < glob.Length && glob[i + 1] == '*')
            {
                // "**/" is any number of folders, "/**" everything inside.
                if (i + 2 < glob.Length && glob[i + 2] == '/')
                {
                    sb.Append("(?:.*/)?");
                    i += 2;
                }
                else
                {
                    sb.Append(".*");
                    i++;
                }
            }
            else if (c == '*')
                sb.Append("[^/]*");
            else if (c == '?')
                sb.Append("[^/]");
            else if (c == '[' && glob.IndexOf(']', i + 1) is var end and > 0)
            {
                var set = glob[(i + 1)..end].Replace(@"\", @"\\");
                sb.Append('[').Append(set.StartsWith('!') ? "^" + set[1..] : set).Append(']');
                i = end;
            }
            else if (c == '\\' && i + 1 < glob.Length)
                sb.Append(Regex.Escape(glob[++i].ToString()));
            else
                sb.Append(Regex.Escape(c.ToString()));
        }
        return sb.ToString();
    }
}
