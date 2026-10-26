using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using BlackBox.Core.Llm;
using BlackBox.Core.Projects;
using BlackBox.Core.Tools;

namespace BlackBox.Core.Agent;

/// <summary>
/// The note that says to index a project first, on the first file or folder of it the model
/// reads, lists, changes or runs a command in, when it hasn't indexed it in this chat. The user
/// asked for the model to index eagerly, by itself; the system prompt and index_folder's
/// description ask for it too, and this catches the times it goes straight to the files. A note
/// once per project, never a block: the user's rule for steering the model.
/// </summary>
internal sealed partial class ProjectNudge
{
    /// <summary>A project with fewer files than this is read faster than indexed: one the model is making from scratch, say.</summary>
    internal const int MinFiles = 25;

    private readonly List<string> _indexed = [];
    private readonly HashSet<string> _said = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The project each path seen was in, as looking it up reads every folder above it.</summary>
    private readonly Dictionary<string, (string Root, string Why)?> _projects = new(StringComparer.OrdinalIgnoreCase);

    internal static string Note(string root, string why) =>
        $"[BlackBox] {root} is a project ({why}) that you haven't indexed in this chat. Index it now: index_folder path=\"{root}\" " +
        "tells you what is in it and what its files define, and index_folder with query= finds where anything is in it " +
        "far faster than listing folders and opening files one by one.";

    /// <summary>After a context reset the overview is gone from the conversation, so a project is worth indexing again.</summary>
    public void Forget()
    {
        _indexed.Clear();
        _said.Clear();
    }

    /// <summary>
    /// The projects the conversation already indexed or was told to index. The app starts a
    /// session for every message the user sends, with the chat so far: without this, every
    /// message said again to index the project the model had indexed for the one before.
    /// </summary>
    public void Recall(IEnumerable<ChatMessage> history, ToolContext ctx)
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var message in history)
        {
            foreach (var call in message.ToolCalls ?? [])
            {
                if (!call.Name.Equals("index_folder", StringComparison.OrdinalIgnoreCase))
                    continue;
                try
                {
                    if (JsonNode.Parse(call.Arguments) is JsonObject args && Arg(args, "path") is { } path)
                        paths.Add(FolderIndex.Normalize(ctx.ResolvePath(path)));
                }
                catch (Exception ex) when (ex is System.Text.Json.JsonException or ArgumentException or NotSupportedException or PathTooLongException) { }
            }
            var text = message.Content ?? string.Join("\n", message.Parts?.Select(p => p.Text) ?? []);
            foreach (Match said in SaidNote().Matches(text))
                _said.Add(said.Groups["root"].Value);
        }
        // Only an index that was made: one stopped while it was built, or that failed, is none.
        foreach (var path in paths)
            if (FolderIndex.IndexedAbove(path, ctx.IndexDirectory) is not null)
                Indexed(path, ctx);
    }

    private void Indexed(string full, ToolContext ctx)
    {
        // The index the call used, as the tool finds it: a search of src goes to the project's,
        // and a file stands for the project it is in.
        var root = FolderIndex.IndexedAbove(full, ctx.IndexDirectory)
                   ?? (File.Exists(full) ? ProjectRoot.Find(full)?.Root ?? Path.GetDirectoryName(full)! : full);
        // An index of the home folder or a drive is no index of each project in it: it reads at
        // most 20,000 files of all of them, and a search goes to a project's own index.
        if (Path.GetPathRoot(root) == root ||
            root.Equals(FolderIndex.Normalize(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)), StringComparison.OrdinalIgnoreCase))
            return;
        if (!_indexed.Contains(root, StringComparer.OrdinalIgnoreCase))
            _indexed.Add(root);
    }

    /// <summary>The note for a call, or null.</summary>
    public string? Check(Tool? tool, string arguments, ToolResult result, ToolContext ctx)
    {
        if (tool is null)
            return null;
        JsonObject args;
        try { args = ToolCallRepair.NormalizeArguments(ToolCallRepair.ParseArguments(arguments), tool); }
        catch (ToolArgumentException) { return null; }
        if (tool is IndexFolderTool)
        {
            if (!result.IsError && Arg(args, "path") is { } indexed)
                Indexed(FolderIndex.Normalize(ctx.ResolvePath(indexed)), ctx);
            return null;
        }
        // Work on another computer: its folders are no business of the index.
        if (Arg(args, "host") is not null)
            return null;
        IEnumerable<string> paths = tool switch
        {
            ReadFileTool or WriteFileTool or EditFileTool => Arg(args, "path") is { } p ? [p] : [],
            ListDirectoryTool => [Arg(args, "path") ?? "."],
            ShellTool => [.. (Arg(args, "working_directory") is { } wd ? [wd] : Array.Empty<string>()), .. PathsIn(Arg(args, "command") ?? "")],
            _ => [],
        };
        foreach (var path in paths)
        {
            string full;
            try { full = FolderIndex.Normalize(ctx.ResolvePath(path)); }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { continue; }
            if (_indexed.Any(i => Under(full, i)) || InOwnData(full, ctx))
                continue;
            var dir = Directory.Exists(full) ? full : Path.GetDirectoryName(full) ?? full;
            if (!_projects.TryGetValue(dir, out var project))
                _projects[dir] = project = ProjectRoot.Find(dir);
            if (project is not var (root, why) || _said.Contains(root) || _indexed.Any(i => Under(root, i)))
                continue;
            _said.Add(root);
            if (!ProjectRoot.HasFiles(root, MinFiles))
                continue;
            return Note(root, why);
        }
        return null;
    }

    /// <summary>
    /// Whether a path is in BlackBox's own Data folder (the one holding its scratch and tools), which is
    /// no project of the user's. Run from a checkout, BlackBox keeps it inside its own repository: a
    /// download into Data\scratch had the model told to index E:\Repos\BlackBox.
    /// </summary>
    private static bool InOwnData(string path, ToolContext ctx) =>
        new[] { ctx.ScratchDirectory, ctx.ToolsDirectory }
            .OfType<string>().Select(d => Path.GetDirectoryName(FolderIndex.Normalize(d))).OfType<string>()
            .Any(data => Under(path, data));

    private static bool Under(string path, string folder) =>
        path.Equals(folder, StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith(folder.EndsWith(Path.DirectorySeparatorChar) ? folder : folder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private static string? Arg(JsonObject args, string key) =>
        args[key] is JsonValue v && v.TryGetValue<string>(out var s) && s.Trim().Length > 0 ? s.Trim() : null;

    /// <summary>The full paths a command names (C:\Projects\game, "D:\My Game\src").</summary>
    internal static IEnumerable<string> PathsIn(string command) =>
        WindowsPath().Matches(command).Select(m => (m.Groups["quoted"].Success ? m.Groups["quoted"].Value : m.Groups["bare"].Value).TrimEnd('\\', '/', '.', ','));

    [GeneratedRegex(@"[""'](?<quoted>[A-Za-z]:[\\/][^""'\r\n]*)[""']|(?<![\w""'])(?<bare>[A-Za-z]:[\\/][^\s""'|;<>,()]*)")]
    private static partial Regex WindowsPath();

    /// <summary>The project a <see cref="Note"/> named.</summary>
    [GeneratedRegex(@"^\[BlackBox\] (?<root>[^\r\n]+?) is a project \(", RegexOptions.Multiline)]
    private static partial Regex SaidNote();
}
