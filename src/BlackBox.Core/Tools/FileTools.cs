using System.Text;
using System.Text.Json.Nodes;

namespace BlackBox.Core.Tools;

public sealed class ReadFileTool : Tool
{
    private const int MaxChars = 60_000;

    public override string Name => "read_file";
    public override string Description => "Read a text file. Relative paths are resolved against the working directory. Returns numbered lines.";
    public override ToolRisk Risk => ToolRisk.ReadOnly;
    public override JsonObject Parameters => Schema(
        ("path", Str("File path"), true),
        ("offset", Int("First line to read, 1-based (default 1)"), false),
        ("limit", Int("Max lines to read (default 400)"), false));
    public override string Describe(JsonObject args) => $"Read {GetString(args, "path")}";

    public override async Task<ToolResult> ExecuteAsync(JsonObject args, ToolContext ctx, CancellationToken ct)
    {
        var path = ctx.ResolvePath(RequireString(args, "path"));
        if (!File.Exists(path))
            return ToolResult.Error($"File not found: {path}");

        var offset = Math.Max(1, (int)(GetNumber(args, "offset") ?? 1));
        var limit = Math.Clamp((int)(GetNumber(args, "limit") ?? 400), 1, 5000);
        var lines = await File.ReadAllLinesAsync(path, ct).ConfigureAwait(false);

        var sb = new StringBuilder();
        var end = Math.Min(lines.Length, offset - 1 + limit);
        for (var i = offset - 1; i < end && sb.Length < MaxChars; i++)
            sb.Append(i + 1).Append('\t').AppendLine(lines[i]);
        if (end < lines.Length)
            sb.AppendLine($"[showing lines {offset}-{end} of {lines.Length}; use offset to read more]");
        return new ToolResult(sb.Length > 0 ? sb.ToString() : "(empty file)");
    }
}

public sealed class WriteFileTool : Tool
{
    public override string Name => "write_file";
    public override string Description =>
        "Create or overwrite a text file with the given content, or add to its end with append=true. Creates parent folders. " +
        "Write long files in parts of at most about 100 lines: the first call creates the file, each next part is appended.";
    public override ToolRisk Risk => ToolRisk.Risky;
    public override JsonObject Parameters => Schema(
        ("path", Str("File path"), true),
        ("content", Str("File content, or the part to add when appending"), true),
        ("append", Bool("Add the content to the end of the file instead of replacing it (default false)"), false));
    public override string Describe(JsonObject args) =>
        $"{(GetBool(args, "append") ? "Append to" : "Write")} {GetString(args, "path")} ({GetString(args, "content")?.Length ?? 0} chars)";

    public override async Task<ToolResult> ExecuteAsync(JsonObject args, ToolContext ctx, CancellationToken ct)
    {
        var path = ctx.ResolvePath(RequireString(args, "path"));
        var content = RequireString(args, "content");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var existed = File.Exists(path);
        if (GetBool(args, "append"))
        {
            await File.AppendAllTextAsync(path, content, ct).ConfigureAwait(false);
            return new ToolResult($"Appended {content.Length} chars to {path} (now {new FileInfo(path).Length} bytes).");
        }
        await File.WriteAllTextAsync(path, content, ct).ConfigureAwait(false);
        return new ToolResult($"{(existed ? "Overwrote" : "Created")} {path} ({content.Length} chars).");
    }
}

public sealed class EditFileTool : Tool
{
    public override string Name => "edit_file";
    public override string Description => "Replace an exact, unique snippet of text in a file with new text.";
    public override ToolRisk Risk => ToolRisk.Risky;
    public override JsonObject Parameters => Schema(
        ("path", Str("File path"), true),
        ("old_text", Str("Exact text to find; must occur exactly once"), true),
        ("new_text", Str("Replacement text"), true));
    public override string Describe(JsonObject args) => $"Edit {GetString(args, "path")}";

    public override async Task<ToolResult> ExecuteAsync(JsonObject args, ToolContext ctx, CancellationToken ct)
    {
        var path = ctx.ResolvePath(RequireString(args, "path"));
        if (!File.Exists(path))
            return ToolResult.Error($"File not found: {path}");
        var oldText = RequireString(args, "old_text");
        var newText = RequireString(args, "new_text");
        if (oldText.Length == 0)
            return ToolResult.Error("old_text must not be empty.");

        var text = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
        var first = text.IndexOf(oldText, StringComparison.Ordinal);
        if (first < 0)
            return ToolResult.Error("old_text was not found in the file. Read the file again and copy the text exactly.");
        if (text.IndexOf(oldText, first + 1, StringComparison.Ordinal) >= 0)
            return ToolResult.Error("old_text occurs more than once. Include more surrounding text to make it unique.");

        await File.WriteAllTextAsync(path, text[..first] + newText + text[(first + oldText.Length)..], ct).ConfigureAwait(false);
        return new ToolResult($"Edited {path}.");
    }
}

public sealed class ListDirectoryTool : Tool
{
    public override string Name => "list_directory";
    public override string Description => "List files and folders in a directory (non-recursive), or search recursively with a glob pattern like *.txt.";
    public override ToolRisk Risk => ToolRisk.ReadOnly;
    public override JsonObject Parameters => Schema(
        ("path", Str("Directory path (default: working directory)"), false),
        ("pattern", Str("Optional glob; when given, searches subfolders too"), false));
    public override string Describe(JsonObject args) => $"List {GetString(args, "path") ?? "."}{(GetString(args, "pattern") is { } p ? " " + p : "")}";

    public override Task<ToolResult> ExecuteAsync(JsonObject args, ToolContext ctx, CancellationToken ct)
    {
        var dir = ctx.ResolvePath(GetString(args, "path") ?? ".");
        if (!Directory.Exists(dir))
            return Task.FromResult(ToolResult.Error($"Directory not found: {dir}"));

        var sb = new StringBuilder().AppendLine(dir);
        const int max = 500;
        var count = 0;
        var pattern = GetString(args, "pattern");
        var options = new EnumerationOptions { RecurseSubdirectories = pattern is not null, IgnoreInaccessible = true };

        foreach (var entry in new DirectoryInfo(dir).EnumerateFileSystemInfos(pattern ?? "*", options))
        {
            if (++count > max)
            {
                sb.AppendLine($"[stopped after {max} entries]");
                break;
            }
            var rel = Path.GetRelativePath(dir, entry.FullName);
            sb.AppendLine(entry is DirectoryInfo ? rel + Path.DirectorySeparatorChar : $"{rel}  ({((FileInfo)entry).Length:N0} bytes)");
        }
        return Task.FromResult(new ToolResult(sb.ToString()));
    }
}
