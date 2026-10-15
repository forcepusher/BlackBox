using System.IO.Enumeration;
using System.Text;
using System.Text.Json.Nodes;
using BlackBox.Core.Ssh;

namespace BlackBox.Core.Tools;

/// <summary>The file tools' host argument: the same work on an SSH host instead of this PC (see <see cref="RemoteFiles"/>).</summary>
internal static class RemoteHost
{
    public const string Description = "Do it on this SSH host instead of this PC: user@host, as for ssh (files the login user can't reach are handled with sudo)";

    public static string? Of(JsonObject args) => args["host"] is JsonValue v && v.TryGetValue<string>(out var s) && s.Trim().Length > 0 ? s.Trim() : null;

    public static string On(JsonObject args) => Of(args) is { } h ? $" on {h}" : "";

    /// <summary>Runs <paramref name="work"/> on the logged-in host, with what logging in said in front of its result.</summary>
    public static async Task<ToolResult> RunAsync(string target, ToolContext ctx, Func<SshHost, Task<(string Text, bool IsError)>> work, CancellationToken ct)
    {
        if (ctx.Ssh is null)
            return ToolResult.Error("SSH is not available in this run.");
        try
        {
            var (host, notice) = await ctx.Ssh.GetAsync(target, null, null, ct).ConfigureAwait(false);
            var (text, isError) = await work(host).ConfigureAwait(false);
            return new ToolResult(notice is null ? text : $"[{notice}]\n{text}", IsError: isError);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or Renci.SshNet.Common.SshException or System.Net.Sockets.SocketException or ObjectDisposedException)
        {
            return ToolResult.Error(ex.Message);
        }
    }
}

public sealed class ReadFileTool : Tool
{
    private const int MaxChars = 60_000;

    public override string Name => "read_file";
    public override string Description =>
        "Read a file. Text files come back as numbered lines; PDF, Word (.docx), Excel (.xlsx) and PowerPoint (.pptx) files as their text " +
        "(Excel cell by cell, with formulas); a picture (PNG, JPEG, GIF, BMP, WebP) is shown to you as an image. " +
        "Relative paths are resolved against the working directory.";
    public override ToolRisk Risk => ToolRisk.ReadOnly;
    public override JsonObject Parameters => Schema(
        ("path", Str("File path"), true),
        ("offset", Int("First line to read, 1-based (default 1)"), false),
        ("limit", Int("Max lines to read (default 400)"), false),
        ("host", Str(RemoteHost.Description), false));
    public override string Describe(JsonObject args) => $"Read {GetString(args, "path")}{RemoteHost.On(args)}";

    public override async Task<ToolResult> ExecuteAsync(JsonObject args, ToolContext ctx, CancellationToken ct)
    {
        if (RemoteHost.Of(args) is { } remote)
        {
            var (file, from, count) = (RequireString(args, "path"), Math.Max(1, (int)(GetNumber(args, "offset") ?? 1)), Math.Clamp((int)(GetNumber(args, "limit") ?? 400), 1, 5000));
            return await RemoteHost.RunAsync(remote, ctx, h => RemoteFiles.ReadAsync(h, file, from, count, MaxChars, ct), ct).ConfigureAwait(false);
        }
        var path = ctx.ResolvePath(RequireString(args, "path"));
        if (!File.Exists(path))
            return ToolResult.Error($"File not found: {path}");
        var offset = Math.Max(1, (int)(GetNumber(args, "offset") ?? 1));
        var limit = Math.Clamp((int)(GetNumber(args, "limit") ?? 400), 1, 5000);

        if (Picture.Handles(path))
        {
            // A photo or chart asked about took six minutes and a stuck "you'll need a new app"
            // box when the model had to find a program to show it on screen.
            if (!ctx.SeesImages)
                return ToolResult.Error($"{path} is a picture, and the model in use can't see images.");
            return Picture.Read(path);
        }
        if (DocumentText.Handles(path))
        {
            string text;
            try { text = DocumentText.Read(path); }
            catch (InvalidDataException ex) { return ToolResult.Error(ex.Message); }
            using var lines = new StringReader(text);
            return await NumberedAsync(lines, offset, limit, ct).ConfigureAwait(false);
        }
        if (IsBinary(path))
            return ToolResult.Error($"{path} is not a text file ({new FileInfo(path).Length:N0} bytes); read_file reads text, PDF and Office documents, and pictures.");

        // Line by line, so a log of gigabytes doesn't have to fit in memory to read a part of it.
        using var reader = new StreamReader(path, detectEncodingFromByteOrderMarks: true);
        return await NumberedAsync(reader, offset, limit, ct).ConfigureAwait(false);
    }

    /// <summary>Lines <paramref name="offset"/> on, numbered, up to <paramref name="limit"/> of them and <see cref="MaxChars"/> in all.</summary>
    private static async Task<ToolResult> NumberedAsync(TextReader reader, int offset, int limit, CancellationToken ct)
    {
        var sb = new StringBuilder();
        int total = 0, last = 0;
        while (await reader.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
        {
            if (++total >= offset && total < offset + limit && sb.Length < MaxChars)
            {
                sb.Append(total).Append('\t').AppendLine(line);
                last = total;
            }
        }
        if (total == 0)
            return new ToolResult("(empty file)");
        if (last == 0)
            return ToolResult.Error($"The file has only {total} lines.");
        if (offset > 1 || last < total)
            sb.AppendLine($"[showing lines {offset}-{last} of {total}; use offset to read more]");
        return new ToolResult(sb.ToString());
    }

    /// <summary>A zero byte near the start, outside UTF-16 text: an image, an exe, an archive.</summary>
    private static bool IsBinary(string path)
    {
        Span<byte> head = stackalloc byte[8000];
        int read;
        using (var stream = File.OpenRead(path))
            read = stream.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
        head = head[..read];
        return !head.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xFE]) && !head.StartsWith((ReadOnlySpan<byte>)[0xFE, 0xFF]) && head.Contains((byte)0);
    }
}

public sealed class WriteFileTool : Tool
{
    public override string Name => "write_file";
    public override string Description =>
        "Create or overwrite a text file with the given content, or add to its end with append=true. Creates parent folders. " +
        "Write long files in parts of at most about 100 lines: the first call creates the file, each next part is appended. " +
        "A path ending in .docx, .xlsx, .pptx or .pdf makes a real Word, Excel, PowerPoint or PDF document from Markdown content: " +
        "headings, paragraphs, lists, tables, **bold**, *italic*, links, pictures as ![caption](C:\\path\\picture.png), a line \\newpage for a new page. " +
        "In .xlsx each heading starts a sheet named after it and tables fill it; a cell starting with = is a formula, and numbers, 12%, $1,200 and " +
        "2026-10-16 become values. In .pptx a line --- starts the next slide, and a slide's first heading is its title. " +
        "To change an existing document, use edit_file, which keeps its formatting.";
    public override ToolRisk Risk => ToolRisk.Risky;
    public override JsonObject Parameters => Schema(
        ("path", Str("File path"), true),
        ("content", Str("File content, or the part to add when appending"), true),
        ("append", Bool("Add the content to the end of the file instead of replacing it (default false)"), false),
        ("host", Str(RemoteHost.Description), false));
    public override string Describe(JsonObject args) =>
        $"{(GetBool(args, "append") ? "Append to" : "Write")} {GetString(args, "path")}{RemoteHost.On(args)} ({GetString(args, "content")?.Length ?? 0} chars)";

    public override async Task<ToolResult> ExecuteAsync(JsonObject args, ToolContext ctx, CancellationToken ct)
    {
        if (RemoteHost.Of(args) is { } remote)
        {
            var (file, text, add) = (RequireString(args, "path"), RequireString(args, "content"), GetBool(args, "append"));
            if (Documents.DocumentFiles.WriteKind(file) is not null)
                return ToolResult.Error("Documents are made on this PC: write it here without host, then copy it to the server with the copy tool.");
            return await RemoteHost.RunAsync(remote, ctx, h => RemoteFiles.WriteAsync(h, file, text, add, ct), ct).ConfigureAwait(false);
        }
        var path = ctx.ResolvePath(RequireString(args, "path"));
        var content = RequireString(args, "content");
        if (Documents.DocumentFiles.WriteKind(path) is not null)
        {
            try
            {
                return new ToolResult(Documents.DocumentFiles.Write(path, content, GetBool(args, "append"), ctx.ResolvePath));
            }
            catch (InvalidOperationException ex)
            {
                return ToolResult.Error(ex.Message);
            }
        }
        var existed = File.Exists(path);
        if (GetBool(args, "append"))
        {
            // Appending only continues a file; a typo in the path must not start a new one.
            if (!existed)
                return ToolResult.Error($"Cannot append: {path} does not exist. Check the path, or create the file first with append=false.");
            await File.AppendAllTextAsync(path, content, ct).ConfigureAwait(false);
            return new ToolResult($"Appended {content.Length} chars to {path} (now {new FileInfo(path).Length} bytes).");
        }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, content, ct).ConfigureAwait(false);
        return new ToolResult($"{(existed ? "Overwrote" : "Created")} {path} ({content.Length} chars).");
    }
}

public sealed class EditFileTool : Tool
{
    public override string Name => "edit_file";
    public override string Description =>
        "Replace an exact, unique snippet of text in a file with new text, or every occurrence of it with replace_all. " +
        "Also changes Word (.docx) and PowerPoint (.pptx) files in place, keeping their formatting: old_text as read_file shows it, " +
        "new_text may use **bold** and *italic*, and a line break in it starts a new paragraph (\"# \" at its start makes a heading). " +
        "In Excel (.xlsx) give cells as read_file shows them: old_text the cells to change (\"B7 =SUM(B2:B6)\") or empty, new_text the cells to set " +
        "(\"B7 =SUM(B2:B7)\", \"A8 Total\", \"Budget!C9 12.5\"); without cell addresses, text is replaced in the cells. " +
        "To add a row between others (above a total), start new_text with a line \"insert row 7\" (old_text empty): the rows below move down " +
        "with every formula pointing at them, and the new row is formatted like the one above; \"delete rows 3-4\" takes rows out.";
    public override ToolRisk Risk => ToolRisk.Risky;
    public override JsonObject Parameters => Schema(
        ("path", Str("File path"), true),
        ("old_text", Str("Exact text to find; must occur exactly once unless replace_all is set"), true),
        ("new_text", Str("Replacement text"), true),
        ("replace_all", Bool("Replace every occurrence, e.g. to rename something throughout the file (default false)"), false),
        ("host", Str(RemoteHost.Description), false));
    public override string Describe(JsonObject args) => $"Edit {GetString(args, "path")}{RemoteHost.On(args)}";

    public override async Task<ToolResult> ExecuteAsync(JsonObject args, ToolContext ctx, CancellationToken ct)
    {
        if (RemoteHost.Of(args) is { } remote)
        {
            var (file, before, after, every) = (RequireString(args, "path"), RequireString(args, "old_text"), RequireString(args, "new_text"), GetBool(args, "replace_all"));
            if (before.Length == 0)
                return ToolResult.Error("old_text must not be empty.");
            return await RemoteHost.RunAsync(remote, ctx, h => RemoteFiles.EditAsync(h, file, before, after, every, ct), ct).ConfigureAwait(false);
        }
        var path = ctx.ResolvePath(RequireString(args, "path"));
        if (!File.Exists(path))
            return ToolResult.Error($"File not found: {path}");
        var oldText = RequireString(args, "old_text");
        var newText = RequireString(args, "new_text");
        if (Documents.DocumentFiles.EditKind(path) is not null)
        {
            try
            {
                return new ToolResult(Documents.DocumentFiles.Edit(path, oldText, newText, GetBool(args, "replace_all")));
            }
            catch (InvalidOperationException ex)
            {
                return ToolResult.Error(ex.Message);
            }
        }
        if (oldText.Length == 0)
            return ToolResult.Error("old_text must not be empty.");

        // Read and written back in the file's own encoding: a UTF-8 signature or UTF-16 stays.
        string text;
        Encoding encoding;
        using (var reader = new StreamReader(path, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: true))
        {
            text = await reader.ReadToEndAsync(ct).ConfigureAwait(false);
            encoding = reader.CurrentEncoding;
        }

        var count = Count(text, oldText);
        // The model writes \n; in a file saved with Windows line endings that never matches.
        if (count == 0 && text.Contains("\r\n") && oldText.Contains('\n') && !oldText.Contains('\r'))
        {
            oldText = oldText.Replace("\n", "\r\n");
            newText = newText.ReplaceLineEndings("\r\n");
            count = Count(text, oldText);
        }
        if (count == 0)
            return ToolResult.Error("old_text was not found in the file. Read the file again and copy the text exactly.");
        var all = GetBool(args, "replace_all");
        if (count > 1 && !all)
            return ToolResult.Error($"old_text occurs {count} times. Include more surrounding text to make it unique, or set replace_all to change every one.");

        await File.WriteAllTextAsync(path, text.Replace(oldText, newText, StringComparison.Ordinal), encoding, ct).ConfigureAwait(false);
        return new ToolResult(count > 1 ? $"Edited {path}: replaced {count} occurrences." : $"Edited {path}.");
    }

    private static int Count(string text, string part)
    {
        var count = 0;
        for (var i = text.IndexOf(part, StringComparison.Ordinal); i >= 0; i = text.IndexOf(part, i + part.Length, StringComparison.Ordinal))
            count++;
        return count;
    }
}

public sealed class ListDirectoryTool : Tool
{
    public override string Name => "list_directory";
    public override string Description => "List files and folders in a directory (non-recursive), or search recursively with a glob pattern like *.txt.";
    public override ToolRisk Risk => ToolRisk.ReadOnly;
    public override JsonObject Parameters => Schema(
        ("path", Str("Directory path (default: working directory)"), false),
        ("pattern", Str("Optional glob; when given, searches subfolders too"), false),
        ("host", Str(RemoteHost.Description), false));
    public override string Describe(JsonObject args) => $"List {GetString(args, "path") ?? "."}{(GetString(args, "pattern") is { } p ? " " + p : "")}{RemoteHost.On(args)}";

    private const int MaxEntries = 500;
    private static readonly TimeSpan SearchTime = TimeSpan.FromSeconds(20);

    public override Task<ToolResult> ExecuteAsync(JsonObject args, ToolContext ctx, CancellationToken ct)
    {
        if (RemoteHost.Of(args) is { } remote)
        {
            var (folder, glob) = (GetString(args, "path") is { Length: > 0 } d ? d : ".", GetString(args, "pattern"));
            return RemoteHost.RunAsync(remote, ctx, h => RemoteFiles.ListAsync(h, folder, glob, ct), ct);
        }
        var dir = ctx.ResolvePath(GetString(args, "path") ?? ".");
        if (!Directory.Exists(dir))
            return Task.FromResult(ToolResult.Error($"Directory not found: {dir}"));

        var pattern = GetString(args, "pattern");
        var giveUp = DateTime.UtcNow + SearchTime;
        var entries = new FileSystemEnumerable<string>(dir,
            (ref FileSystemEntry e) => e.IsDirectory
                ? Path.GetRelativePath(dir, e.ToFullPath()) + Path.DirectorySeparatorChar
                : $"{Path.GetRelativePath(dir, e.ToFullPath())}  ({e.Length:N0} bytes)",
            new EnumerationOptions { RecurseSubdirectories = pattern is not null, IgnoreInaccessible = true })
        {
            // Asked for every entry looked at, matching or not, so a search through a whole drive
            // for something that isn't there still ends on time, or at once on Stop.
            ShouldIncludePredicate = (ref FileSystemEntry e) =>
            {
                ct.ThrowIfCancellationRequested();
                if (DateTime.UtcNow > giveUp)
                    throw new TimeoutException();
                return pattern is null || FileSystemName.MatchesSimpleExpression(pattern, e.FileName);
            },
        };

        var sb = new StringBuilder().AppendLine(dir);
        var count = 0;
        try
        {
            foreach (var entry in entries)
            {
                if (++count > MaxEntries)
                {
                    sb.AppendLine($"[stopped after {MaxEntries} entries]");
                    break;
                }
                sb.AppendLine(entry);
            }
        }
        catch (TimeoutException)
        {
            sb.AppendLine($"[stopped after searching for {SearchTime.TotalSeconds:0} seconds; give a narrower path or pattern]");
        }
        return Task.FromResult(new ToolResult(sb.ToString()));
    }
}
