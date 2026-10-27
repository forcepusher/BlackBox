using System.Text.Json.Nodes;
using Sloperator.Core.Projects;

namespace Sloperator.Core.Tools;

/// <summary>
/// Indexes a folder and searches it, as Cursor does a codebase: the overview tells the model what
/// the project is and where things are, and a query finds the parts of files about a name or a
/// question. The user asked for the model to reach for it by itself, eagerly: the description, the
/// system prompt and a note on the first file of an unindexed project it touches (see
/// <see cref="Agent.ProjectNudge"/>) all say to index first.
/// </summary>
public sealed class IndexFolderTool : Tool
{
    public override string Name => "index_folder";
    public override string Description =>
        "Index a folder so you know your way around it, as a code editor does, and search it. Use it first, without being asked, whenever a task " +
        "involves a project or a folder of many files you haven't indexed in this chat (code, a game, a website, a set of documents), before you list, " +
        "read or change its files: it tells you what kind of project it is and how it builds, its README, its folders, and its main files with the " +
        "classes and functions they define. To find something in it (where a class, function or setting is, every line that uses a name, " +
        "the code behind an error message or a feature), call it with query=, or search with the shell when that fits better. " +
        "It keeps itself up to date: each call reads only the files that changed.";
    public override ToolRisk Risk => ToolRisk.ReadOnly;
    public override JsonObject Parameters => Schema(
        ("path", Str("The folder: a project's top folder (where its .git, solution or package file is), not a drive or the whole home folder. " +
                     "With query, a folder or file inside an indexed folder limits the search to it"), true),
        ("query", Str("What to find: names (SettingsStore, save_game), words of an error message, or what the code does in a few words " +
                      "(\"where the settings are saved\"). Leave it out to index the folder and get its overview"), false));

    public override string Describe(JsonObject args) =>
        GetString(args, "query") is { Length: > 0 } q ? $"Search {GetString(args, "path")} for \"{q}\"" : $"Index {GetString(args, "path")}";

    public override async Task<ToolResult> ExecuteAsync(JsonObject args, ToolContext ctx, CancellationToken ct)
    {
        var path = FolderIndex.Normalize(ctx.ResolvePath(RequireString(args, "path")));
        var query = GetString(args, "query")?.Trim();
        var isFile = File.Exists(path);
        if (!isFile && !Directory.Exists(path))
            return ToolResult.Error($"Folder not found: {path}");
        var progress = ctx.Progress;

        if (string.IsNullOrEmpty(query))
        {
            // A file stands for the project it is in.
            var folder = isFile ? ProjectRoot.Find(path)?.Root ?? Path.GetDirectoryName(path)! : path;
            return await FolderIndex.UseAsync(folder, ctx.IndexDirectory, progress,
                (index, changes) => Task.FromResult(new ToolResult(ProjectOverview.Build(index, changes))), ct).ConfigureAwait(false);
        }

        // The index the path is in, if there is one, or else its project's: a search of a part of
        // a project needs no index of its own.
        var root = FolderIndex.IndexedAbove(path, ctx.IndexDirectory) ?? ProjectRoot.Find(path)?.Root ?? (isFile ? Path.GetDirectoryName(path)! : path);
        return await FolderIndex.UseAsync(root, ctx.IndexDirectory, progress, async (found, made) =>
        {
            var under = path.Length > found.Root.Length ? Path.GetRelativePath(found.Root, path).Replace('\\', '/') : "";
            var text = await IndexSearch.SearchAsync(found, query, under, ct).ConfigureAwait(false);
            if (made.Built)
                text = $"(Indexed {found.Root} for this search: {found.Files.Count:N0} files. index_folder without query gives its overview.)\n" + text;
            return new ToolResult(text);
        }, ct).ConfigureAwait(false);
    }
}
