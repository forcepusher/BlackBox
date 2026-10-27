using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Sloperator.Core.Projects;

/// <summary>
/// What the model is told about a folder once it is indexed, in about 1,400 tokens: what kind of
/// project it is and how it builds, its README and its notes for AI agents, its languages and
/// folders, and its main files with what they define (a "repo map": the files whose definitions
/// most other files use come first).
/// </summary>
internal static partial class ProjectOverview
{
    /// <summary>The longest overview, about 1,400 tokens: an orientation, which a small model reads along with everything after it.</summary>
    internal const int MaxChars = 5_500;

    public static string Build(FolderIndex index, IndexChanges changes)
    {
        var files = index.Files.Values.ToList();
        var read = files.Where(f => f.Kind is FileKind.Text or FileKind.Document).ToList();
        var sb = new StringBuilder();
        var symbols = read.Sum(f => f.Symbols.Count(s => s.Kind != "heading"));
        sb.Append($"Indexed {index.Root}: {files.Count:N0} files, {read.Count:N0} of them read ({Size(read.Sum(f => f.Size))}), {symbols:N0} definitions. ");
        sb.AppendLine(changes.Built
            ? $"Built in {Seconds(changes.Took)}."
            : changes.Total == 0 ? $"Up to date ({Seconds(changes.Took)})." : $"Brought up to date: {Changed(changes)} since the last time ({Seconds(changes.Took)}).");
        // An empty folder said only "0 files", and qwen3.8 listed it to be sure.
        if (files.Count == 0)
            sb.AppendLine(index.LeftOut.Count > 0 ? "Everything in it is left out, as below." : "The folder is empty.");
        else
            sb.AppendLine("Paths below are under that folder.");

        Section(sb, "What it is", Kind(index, files), 700);
        Section(sb, "README", Readme(index, files), 400);
        Section(sb, "Notes for AI agents in the project", AgentNotes(index, files), 600);
        Section(sb, "Languages", Languages(read), 200);
        Section(sb, "Folders (files in each)", Folders(files), 700);
        var footer = Footer(index, files);
        var room = MaxChars - sb.Length - footer.Length - 100;
        if (room > 300)
            Section(sb, "Main files and what they define", Map(index, read, room), room);
        sb.Append(footer);
        return sb.ToString();
    }

    private static void Section(StringBuilder sb, string title, string? body, int max)
    {
        if (string.IsNullOrWhiteSpace(body))
            return;
        body = body.TrimEnd();
        if (body.Length > max)
        {
            var cut = body.LastIndexOf('\n', max);
            body = body[..(cut > 0 ? cut : max)] + "\n…";
        }
        sb.AppendLine().Append(title).AppendLine(":").AppendLine(body);
    }

    private static string Changed(IndexChanges c)
    {
        var parts = new List<string>();
        if (c.Added > 0) parts.Add($"{c.Added:N0} new");
        if (c.Changed > 0) parts.Add($"{c.Changed:N0} changed");
        if (c.Removed > 0) parts.Add($"{c.Removed:N0} gone");
        return string.Join(", ", parts) + (c.Total == 1 ? " file" : " files");
    }

    internal static string Seconds(TimeSpan t) => t.TotalSeconds < 10
        ? t.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture) + " s"
        : t.TotalSeconds.ToString("0", CultureInfo.InvariantCulture) + " s";

    internal static string Size(long bytes) => bytes switch
    {
        < 1_000_000 => (bytes / 1000.0).ToString("0", CultureInfo.InvariantCulture) + " KB of text",
        _ => (bytes / 1_000_000.0).ToString("0.0", CultureInfo.InvariantCulture) + " MB of text",
    };

    private static string Show(string path) => path.Replace('/', Path.DirectorySeparatorChar);

    // --- what kind of project ----------------------------------------------------------------

    /// <summary>One line per thing that says what the folder is: git, a solution, a package, an engine's project…</summary>
    private static string Kind(FolderIndex index, List<IndexedFile> files)
    {
        var lines = new List<string>();
        var head = Path.Combine(index.Root, ".git", "HEAD");
        if (File.Exists(head))
        {
            var branch = Read(head)?.Trim() is { } h && h.StartsWith("ref: refs/heads/") ? h["ref: refs/heads/".Length..] : null;
            lines.Add("A git repository" + (branch is null ? "." : $", on branch {branch}."));
        }
        // Shallow ones first, and only so many of each: a solution of 40 projects is said in a line.
        foreach (var group in files.Where(f => Depth(f.Path) <= 3 && Manifest(f.Path) is not null)
                     .OrderBy(f => Depth(f.Path)).ThenBy(f => f.Path, StringComparer.OrdinalIgnoreCase)
                     .GroupBy(f => Manifest(f.Path)))
        {
            var shown = 0;
            foreach (var f in group)
            {
                if (++shown > 6)
                {
                    lines.Add($"(and {group.Count() - 6} more {group.Key} files)");
                    break;
                }
                if (Describe(group.Key!, f, Read(index.FullPath(f))) is { } line)
                    lines.Add(line);
            }
        }
        var workflows = files.Count(f => f.Path.StartsWith(".github/workflows/", StringComparison.OrdinalIgnoreCase));
        if (workflows > 0)
            lines.Add($"{workflows} GitHub Actions workflow{(workflows == 1 ? "" : "s")} in .github{Path.DirectorySeparatorChar}workflows.");
        return string.Join("\n", lines);
    }

    private static int Depth(string path) => path.Count(c => c == '/');

    /// <summary>The kind of manifest a file is, or null.</summary>
    private static string? Manifest(string path)
    {
        var name = Path.GetFileName(path).ToLowerInvariant();
        var ext = Path.GetExtension(name);
        return name switch
        {
            "package.json" when !path.Contains("node_modules", StringComparison.OrdinalIgnoreCase) => "package.json",
            "pyproject.toml" or "requirements.txt" or "setup.py" => "Python",
            "cargo.toml" => "Cargo.toml",
            "go.mod" => "go.mod",
            "projectversion.txt" when path.EndsWith("ProjectSettings/ProjectVersion.txt", StringComparison.OrdinalIgnoreCase) => "Unity",
            "manifest.json" when path.EndsWith("Packages/manifest.json", StringComparison.OrdinalIgnoreCase) => "Unity packages",
            "project.godot" => "Godot",
            "pom.xml" => "Maven",
            "build.gradle" or "build.gradle.kts" => "Gradle",
            "cmakelists.txt" => "CMake",
            "composer.json" => "Composer",
            "gemfile" => "Ruby",
            "pubspec.yaml" => "Flutter",
            "dockerfile" or "docker-compose.yml" or "docker-compose.yaml" or "compose.yaml" => "Docker",
            _ => ext switch
            {
                ".sln" or ".slnx" => ".NET solution",
                ".csproj" or ".fsproj" or ".vbproj" => ".NET project",
                ".vcxproj" => "C++ project",
                ".uproject" => "Unreal",
                ".uplugin" => "Unreal plugin",
                _ => null,
            },
        };
    }

    private static string? Describe(string kind, IndexedFile f, string? text)
    {
        var path = Show(f.Path);
        text ??= "";
        switch (kind)
        {
            case ".NET solution":
                return $"{path}: a .NET solution.";
            case ".NET project":
            {
                var output = Tag(text, "OutputType") ?? (text.Contains("Microsoft.NET.Sdk.Web") ? "web app" : "library");
                var frameworks = Tag(text, "TargetFramework") ?? Tag(text, "TargetFrameworks");
                var packages = PackageRef().Matches(text).Select(m => m.Groups[1].Value).Distinct().ToList();
                var refs = ProjectRef().Matches(text).Select(m => Path.GetFileNameWithoutExtension(m.Groups[1].Value.Replace('\\', '/'))).ToList();
                return $"{path}: {output}" + (frameworks is null ? "" : $", {frameworks}") +
                       (packages.Count > 0 ? $"; packages {List(packages, 8)}" : "") + (refs.Count > 0 ? $"; uses {List(refs, 6)}" : "") + ".";
            }
            case "package.json":
            {
                try
                {
                    using var doc = JsonDocument.Parse(text, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
                    var root = doc.RootElement;
                    if (root.ValueKind != JsonValueKind.Object)
                        return $"{path}: not a package's description (its JSON is no object).";
                    var name = root.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null;
                    var scripts = Keys(root, "scripts");
                    var deps = Keys(root, "dependencies").Concat(Keys(root, "devDependencies")).ToList();
                    // Unity's packages are described the way npm's are.
                    var kindOf = root.TryGetProperty("unity", out _) ? "Unity package" : "Node package";
                    return $"{path}: {kindOf}{(name is null ? "" : $" {name}")}" + (scripts.Count > 0 ? $"; npm scripts {List(scripts, 8)}" : "") +
                           (deps.Count > 0 ? $"; depends on {List(deps, 12)}" : "") + ".";
                }
                catch (JsonException)
                {
                    return $"{path}: Node package.";
                }
            }
            case "Python":
            {
                var deps = Path.GetFileName(f.Path).Equals("requirements.txt", StringComparison.OrdinalIgnoreCase)
                    ? text.Split('\n').Select(l => Regex.Match(l.Trim(), @"^[A-Za-z0-9_.\-\[\]]+").Value).Where(l => l.Length > 0).ToList()
                    : TomlArray(text, "dependencies");
                return $"{path}: Python" + (TomlValue(text, "name") is { } name ? $" project {name}" : "") + (deps.Count > 0 ? $"; needs {List(deps, 12)}" : "") + ".";
            }
            case "Cargo.toml":
                return $"{path}: Rust crate" + (TomlValue(text, "name") is { } crate ? $" {crate}" : "") + ".";
            case "go.mod":
                return $"{path}: Go module " + (Regex.Match(text, @"^module\s+(\S+)", RegexOptions.Multiline) is { Success: true } m ? m.Groups[1].Value : "") +
                       (Regex.Match(text, @"^go\s+(\S+)", RegexOptions.Multiline) is { Success: true } g ? $", Go {g.Groups[1].Value}" : "") + ".";
            case "Unity":
                return $"A Unity project, editor version {Regex.Match(text, @"m_EditorVersion:\s*(\S+)").Groups[1].Value} ({path}).";
            case "Unity packages":
            {
                var packages = Regex.Matches(text, @"""(com\.[\w.\-]+)""").Select(m => m.Groups[1].Value).Where(p => !p.StartsWith("com.unity.modules.")).ToList();
                return packages.Count == 0 ? null : $"{path}: Unity packages {List(packages, 12)}.";
            }
            case "Godot":
                return $"A Godot project" + (Regex.Match(text, @"^config/name\s*=\s*""([^""]*)""", RegexOptions.Multiline) is { Success: true } gn ? $" \"{gn.Groups[1].Value}\"" : "") +
                       (Regex.Match(text, @"^run/main_scene\s*=\s*""([^""]*)""", RegexOptions.Multiline) is { Success: true } ms ? $", main scene {ms.Groups[1].Value}" : "") + $" ({path}).";
            case "Unreal":
                return $"An Unreal project" + (Regex.Match(text, @"""EngineAssociation""\s*:\s*""([^""]*)""") is { Success: true } ue ? $", engine {ue.Groups[1].Value}" : "") +
                       $" ({path}).";
            case "CMake":
                return $"{path}: CMake" + (Regex.Match(text, @"project\s*\(\s*([\w\-]+)", RegexOptions.IgnoreCase) is { Success: true } cm ? $" project {cm.Groups[1].Value}" : "") + ".";
            default:
                return $"{path}: {kind}.";
        }
    }

    private static string List(List<string> items, int most) =>
        string.Join(", ", items.Take(most)) + (items.Count > most ? $" and {items.Count - most} more" : "");

    private static List<string> Keys(JsonElement root, string property) =>
        root.TryGetProperty(property, out var o) && o.ValueKind == JsonValueKind.Object ? o.EnumerateObject().Select(p => p.Name).ToList() : [];

    private static string? Tag(string xml, string name) =>
        Regex.Match(xml, $@"<{name}>\s*([^<]+?)\s*</{name}>") is { Success: true } m ? m.Groups[1].Value : null;

    private static string? TomlValue(string toml, string key) =>
        Regex.Match(toml, $@"^\s*{key}\s*=\s*""([^""]+)""", RegexOptions.Multiline) is { Success: true } m ? m.Groups[1].Value : null;

    private static List<string> TomlArray(string toml, string key) =>
        Regex.Match(toml, $@"^\s*{key}\s*=\s*\[(.*?)\]", RegexOptions.Multiline | RegexOptions.Singleline) is { Success: true } m
            ? Regex.Matches(m.Groups[1].Value, @"""([A-Za-z0-9_.\-]+)").Select(d => d.Groups[1].Value).ToList()
            : [];

    [GeneratedRegex(@"<PackageReference\s+Include=""([^""]+)""")]
    private static partial Regex PackageRef();

    [GeneratedRegex(@"<ProjectReference\s+Include=""([^""]+)""")]
    private static partial Regex ProjectRef();

    private static string? Read(string path)
    {
        try { return new FileInfo(path).Length > 1_000_000 ? null : File.ReadAllText(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    // --- README and notes for agents -------------------------------------------------------

    private static string? Readme(FolderIndex index, List<IndexedFile> files)
    {
        var readme = files.Where(f => Depth(f.Path) == 0 && Path.GetFileNameWithoutExtension(f.Path).Equals("README", StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f.Path.EndsWith(".md", StringComparison.OrdinalIgnoreCase) ? 0 : 1).FirstOrDefault();
        return readme is null ? null : Opening(index.FullPath(readme), 6, Show(readme.Path));
    }

    /// <summary>The files a project keeps for AI agents (CLAUDE.md, AGENTS.md, .cursorrules…): its conventions, said for exactly this.</summary>
    private static string? AgentNotes(FolderIndex index, List<IndexedFile> files)
    {
        var notes = files.Where(f => Depth(f.Path) == 0 && Path.GetFileName(f.Path).ToLowerInvariant() is "claude.md" or "agents.md" or ".cursorrules" or "gemini.md" or ".windsurfrules"
                                     || f.Path.Equals(".github/copilot-instructions.md", StringComparison.OrdinalIgnoreCase)
                                     || f.Path.StartsWith(".cursor/rules/", StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase).Take(3).ToList();
        return notes.Count == 0 ? null : string.Join("\n", notes.Select(f => Opening(index.FullPath(f), 16, Show(f.Path))));
    }

    /// <summary>The first lines of a text that say something (badges, pictures and blank lines left out), with where they are from.</summary>
    private static string? Opening(string path, int lines, string shown)
    {
        if (Read(path) is not { } text)
            return null;
        var kept = text.ReplaceLineEndings("\n").Split('\n').Select(l => l.TrimEnd())
            .Where(l => l.Trim().Length > 0 && !l.TrimStart().StartsWith("![") && !l.TrimStart().StartsWith("[![") && !l.TrimStart().StartsWith("<img") && !l.TrimStart().StartsWith("<!--"))
            .Select(l => l.Length > 220 ? l[..220] + " …" : l).Take(lines).ToList();
        var total = text.TrimEnd('\r', '\n').Count(c => c == '\n') + 1;
        return kept.Count == 0 ? null : $"({shown}, {total:N0} line{(total == 1 ? "" : "s")}; the start of it)\n" + string.Join("\n", kept);
    }

    // --- languages and folders ---------------------------------------------------------------

    private static string Languages(List<IndexedFile> read) =>
        string.Join(", ", read.Where(f => f.Language is not null).GroupBy(f => f.Language!)
            .Select(g => (Language: g.Key, Files: g.Count(), Lines: g.Sum(f => f.Lines)))
            .OrderByDescending(l => l.Lines).Take(5)
            .Select(l => $"{l.Language} {l.Files:N0} file{(l.Files == 1 ? "" : "s")} ({l.Lines:N0} lines)"));

    /// <summary>The folder tree two levels down with the files in each, and the files at the top.</summary>
    private static string Folders(List<IndexedFile> files)
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in files)
        {
            var parts = f.Path.Split('/');
            for (var depth = 1; depth < parts.Length && depth <= 2; depth++)
            {
                var dir = string.Join('/', parts.Take(depth));
                counts[dir] = counts.GetValueOrDefault(dir) + 1;
            }
        }
        var sb = new StringBuilder();
        var top = counts.Keys.Where(d => !d.Contains('/')).OrderBy(d => d, StringComparer.OrdinalIgnoreCase).ToList();
        foreach (var dir in top.Take(30))
        {
            sb.Append(Show(dir)).Append(Path.DirectorySeparatorChar).Append(' ').Append(counts[dir].ToString("N0", CultureInfo.InvariantCulture)).AppendLine();
            var subs = counts.Keys.Where(d => d.StartsWith(dir + "/", StringComparison.OrdinalIgnoreCase)).OrderByDescending(d => counts[d]).ThenBy(d => d).ToList();
            var shown = subs.Take(top.Count > 12 ? 4 : 10).OrderBy(d => d, StringComparer.OrdinalIgnoreCase);
            if (subs.Count > 0)
                sb.Append("  ").AppendJoin(", ", shown.Select(d => $"{d[(dir.Length + 1)..]}{Path.DirectorySeparatorChar} {counts[d]:N0}"))
                    .Append(subs.Count > (top.Count > 12 ? 4 : 10) ? $", and {subs.Count - (top.Count > 12 ? 4 : 10)} more" : "").AppendLine();
        }
        if (top.Count > 30)
            sb.AppendLine($"and {top.Count - 30} more folders");
        var atTop = files.Where(f => !f.Path.Contains('/')).Select(f => f.Path).OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();
        if (atTop.Count > 0)
            sb.Append("Files at the top: ").AppendLine(List(atTop, 20));
        return sb.ToString();
    }

    // --- the map -----------------------------------------------------------------------------

    /// <summary>
    /// The files whose definitions the most other files use, each with what it defines, in path
    /// order, as many as fit in <paramref name="room"/> characters.
    /// </summary>
    internal static string Map(FolderIndex index, List<IndexedFile> read, int room)
    {
        var used = Uses(index, read);
        var ranked = read.Where(f => f.Symbols.Length > 0)
            .Select(f => (File: f, Score: Importance(f, used)))
            .OrderByDescending(r => r.Score).ThenBy(r => r.File.Path, StringComparer.OrdinalIgnoreCase).ToList();
        // Each part of the project (its folders two levels down) gets lines in proportion to the
        // code in it, at first: a library kept in the project uses its own names over and over,
        // and in a Unity game of 1,530 scripts of its own the map was half the Spine runtime's
        // 152 files and DOTween.
        // Its share of the room, and a line at least; then what is left goes by rank alone. The share
        // goes by files and lines together: by files alone a folder of 112 small lab scripts took a
        // third of this repository's map, by lines alone Spine's long files took a third of the game's.
        var weight = ranked.GroupBy(r => Area(r.File.Path)).ToDictionary(g => g.Key, g => Math.Sqrt(g.Count() * (double)g.Sum(r => (long)r.File.Lines)));
        var all = Math.Max(1, weight.Values.Sum());
        var share = weight.ToDictionary(w => w.Key, w => (int)(room * w.Value / all));
        var taken = share.ToDictionary(s => s.Key, _ => 0);
        var chosen = new List<(IndexedFile File, string Line)>();
        var length = 0;
        foreach (var byShare in new[] { true, false })
        {
            foreach (var (file, _) in ranked)
            {
                if (chosen.Any(c => c.File == file))
                    continue;
                var area = Area(file.Path);
                if (byShare && taken[area] > 0 && taken[area] >= share[area])
                    continue;
                var line = MapLine(file, used);
                if (line is null)
                    continue;
                if (length + line.Length + 1 > room)
                {
                    if (room - length < 80)
                        break;
                    continue;
                }
                chosen.Add((file, line));
                length += line.Length + 1;
                taken[area] += line.Length + 1;
            }
        }
        var left = ranked.Count - chosen.Count;
        return string.Join("\n", chosen.OrderBy(c => c.File.Path, StringComparer.OrdinalIgnoreCase).Select(c => c.Line)) +
               (left > 0 ? $"\n({left:N0} more files define things; search finds them)" : "");
    }

    /// <summary>The part of the project a file is in: its first two folders ("Assets/Source", "src/Sloperator.Core").</summary>
    private static string Area(string path)
    {
        var parts = path.Split('/');
        return string.Join('/', parts.Take(Math.Min(2, parts.Length - 1)));
    }

    /// <summary>In how many files each defined name (lower-cased) is used, its own files left out.</summary>
    private static Dictionary<string, int> Uses(FolderIndex index, List<IndexedFile> read)
    {
        // A name is a term of its own (see Terms.Of), so a file that has the term uses the name.
        var names = new Dictionary<int, string>();
        foreach (var s in read.SelectMany(f => f.Symbols))
            if (s.Kind != "heading" && s.Name.Length >= 3 && index.TermId(s.Name.ToLowerInvariant()) is var id and >= 0)
                names[id] = s.Name.ToLowerInvariant();
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var f in read)
        {
            var own = f.Symbols.Select(s => s.Name.ToLowerInvariant()).ToHashSet();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var c in f.Chunks)
                foreach (var id in c.Ids)
                    if (names.TryGetValue(id, out var name) && !own.Contains(name))
                        seen.Add(name);
            foreach (var name in seen)
                counts[name] = counts.GetValueOrDefault(name) + 1;
        }
        return counts;
    }

    private static double Importance(IndexedFile f, Dictionary<string, int> used)
    {
        // A name of a few words (SettingsStore) used elsewhere is that file's; a one-word name
        // (Run, Load, connect) turns up in files that have nothing to do with it.
        var score = f.Symbols.Where(s => s.Kind != "heading")
            .Select(s => Math.Log2(1 + used.GetValueOrDefault(s.Name.ToLowerInvariant())) * (Terms.Parts(s.Name).Count > 1 || s.Name.Length >= 10 ? 1 : 0.2))
            .OrderByDescending(x => x).Take(12).Sum();
        // What a person works on before its tests and the scripts around it.
        if (IndexSearch.IsTest(f.Path))
            score *= 0.5;
        var name = Path.GetFileNameWithoutExtension(f.Path).ToLowerInvariant();
        // Where a program starts, and what a person reads first.
        if (name is "program" or "main" or "app" or "index" or "__main__" or "manage" or "startup" or "server" or "gamemanager" or "game" or "readme")
            score += 4;
        if (f.Symbols.Any(s => s.Name is "Main" or "main" && s.Kind is "method" or "function"))
            score += 4;
        return score - Depth(f.Path) * 0.1;
    }

    /// <summary>A file and what it defines: its types with their most used members, or its functions; a document's top headings.</summary>
    private static string? MapLine(IndexedFile f, Dictionary<string, int> used)
    {
        int Uses(Symbol s) => used.GetValueOrDefault(s.Name.ToLowerInvariant());
        string Of(IEnumerable<Symbol> symbols, int most)
        {
            var list = symbols.ToList();
            var picked = list.OrderByDescending(Uses).Take(most).OrderBy(s => s.Line).Select(s => s.Name).Distinct().ToList();
            return string.Join(", ", picked) + (list.Count > most ? ", …" : "");
        }

        // Short lines, more files: a small model needs where things are more than every member.
        var head = $"{Show(f.Path)} ({f.Lines:N0} lines)";
        if (f.Language == "Markdown")
        {
            var headings = f.Symbols.Where(s => s.Kind == "heading" && s.Depth <= 2).ToList();
            return headings.Count == 0 ? null : $"{head}: {Of(headings, 4)}";
        }
        var types = f.Symbols.Where(s => s.IsType && s.Kind != "namespace").DistinctBy(s => (s.Kind, s.Name)).ToList();
        if (types.Count == 0)
        {
            var functions = f.Symbols.Where(s => s.Kind is "function" or "method").ToList();
            return functions.Count == 0 ? null : $"{head}: {Of(functions, 5)}";
        }
        var parts = new List<string>();
        var inTypes = new HashSet<Symbol>();
        foreach (var type in types)
        {
            var next = types.FirstOrDefault(t => t.Line > type.Line && t.Depth <= type.Depth);
            var members = f.Symbols.Where(s => s.Line > type.Line && (next == default || s.Line < next.Line) && !s.IsType && s.Depth > type.Depth).ToList();
            inTypes.UnionWith(members);
            if (parts.Count < 2 && types.OrderByDescending(Uses).ThenBy(t => t.Line).Take(2).Contains(type))
                parts.Add($"{type.Kind} {type.Name}" + (members.Count > 0 ? $" ({Of(members, 3)})" : ""));
        }
        var more = types.Count > 2 ? $"; {types.Count - 2} more types" : "";
        // Functions of their own beside the types: a module's exports.
        var loose = f.Symbols.Where(s => s.Kind is "function" or "method" && !inTypes.Contains(s)).ToList();
        return $"{head}: {string.Join("; ", parts)}{more}" + (loose.Count > 0 ? $"; functions {Of(loose, 4)}" : "");
    }

    // --- what was left out -------------------------------------------------------------------

    private static string Footer(FolderIndex index, List<IndexedFile> files)
    {
        var sb = new StringBuilder();
        var notRead = files.Where(f => f.Kind is not (FileKind.Text or FileKind.Document)).GroupBy(f => f.Kind).ToDictionary(g => g.Key, g => g.Count());
        var parts = new List<string>();
        if (notRead.GetValueOrDefault(FileKind.Binary) is > 0 and var binary) parts.Add($"{binary:N0} pictures, sounds, programs and other files that aren't text");
        if (notRead.GetValueOrDefault(FileKind.Large) is > 0 and var large) parts.Add($"{large:N0} text files over {FolderIndex.MaxTextFile / 1_000_000} MB");
        if (notRead.GetValueOrDefault(FileKind.Generated) is > 0 and var generated) parts.Add($"{generated:N0} made by tools (lock files, minified code, Unity's scene and asset files)");
        if (notRead.GetValueOrDefault(FileKind.Locked) is > 0 and var locked) parts.Add($"{locked:N0} that couldn't be opened (in use or not allowed; tried again next time)");
        sb.AppendLine();
        if (parts.Count > 0)
            sb.AppendLine("Listed but not read: " + string.Join("; ", parts) + ".");
        if (index.LeftOut.Count > 0)
            sb.AppendLine("Left out: " + string.Join(", ", index.LeftOut.OrderByDescending(l => l.Value).Take(10).Select(l => l.Value > 1 ? $"{l.Key} ×{l.Value}" : l.Key)) + ".");
        if (notRead.GetValueOrDefault(FileKind.Unread) is > 0 and var unread)
            sb.AppendLine($"{unread:N0} files weren't read: an index reads at most {FolderIndex.MaxFiles:N0} files ({FolderIndex.MaxText / 1_000_000} MB). Index the folder you work in for them.");
        if (index.WalkStopped)
            sb.AppendLine($"The folder has more than {FolderWalk.MaxEntries:N0} entries; the walk stopped there. Index the folder you work in.");
        sb.Append($"To find anything in it: index_folder path=\"{index.Root}\" query=\"...\" (names, words of an error, or what the code does in a few words).");
        return sb.ToString();
    }
}
