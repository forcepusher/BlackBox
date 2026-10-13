using System.Formats.Tar;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using BlackBox.Core.Ssh;

namespace BlackBox.Core.Tools;

/// <summary>
/// Copies files and folders between this PC and SSH hosts, as scp does, byte for byte: a folder
/// travels as one tar stream over the connection the ssh tool keeps, and is unpacked with sudo
/// where the login user can't write. Also copies on this PC, or on one host.
/// </summary>
public sealed partial class CopyTool : Tool
{
    public override string Name => "copy";
    public override string Description =>
        "Copy a file or a folder (with everything in it) between this PC and an SSH host, from one host to another (through this PC), or within " +
        "either. Write a host's paths as user@host:/path, " +
        "as for scp. Binary files arrive byte for byte. A folder copied to a folder that exists goes inside it; end the source with /* to copy only " +
        "what is in it. Where the login user can't write on the host, sudo is used (new files then belong to root).";
    public override ToolRisk Risk => ToolRisk.Risky;
    public override JsonObject Parameters => Schema(
        ("from", Str("What to copy: a path on this PC, or user@host:/path"), true),
        ("to", Str("Where to: a path on this PC, or user@host:/path"), true));
    public override string Describe(JsonObject args) => $"Copy {GetString(args, "from")} to {GetString(args, "to")}";

    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(30);

    public override async Task<ToolResult> ExecuteAsync(JsonObject args, ToolContext ctx, CancellationToken ct)
    {
        var from = Side.Parse(RequireString(args, "from"));
        var to = Side.Parse(RequireString(args, "to"));
        if ((from.Host is not null || to.Host is not null) && ctx.Ssh is null)
            return ToolResult.Error("SSH is not available in this run.");
        try
        {
            string? notice = null;
            async Task<SshHost> Login(string target)
            {
                var (h, n) = await ctx.Ssh!.GetAsync(target, null, null, ct).ConfigureAwait(false);
                notice ??= n;
                return h;
            }
            var src = from.Host is null ? null : await Login(from.Host).ConfigureAwait(false);
            var dst = to.Host is null ? null : await Login(to.Host).ConfigureAwait(false);

            // Pack the source as a tar stream (or read it from the host), then unpack it at the destination.
            using var tar = new MemoryStream();
            var (count, bytes) = src is null
                ? PackLocal(ctx.ResolvePath(from.Path), from.ContentsOnly, tar)
                : await PackRemoteAsync(src, from.Path, from.ContentsOnly, tar, ct).ConfigureAwait(false);
            tar.Position = 0;
            var (where, sudo) = dst is null
                ? (UnpackLocal(tar, ctx.ResolvePath(to.Path), from), false)
                : await UnpackRemoteAsync(dst, tar, to.Path, from, ct).ConfigureAwait(false);
            var text = $"Copied {count} file{(count == 1 ? "" : "s")} ({Size(bytes)}) to {(to.Host is null ? where : $"{to.Host}:{where}")}{(sudo ? " with sudo" : "")}.";
            return new ToolResult(notice is null ? text : $"[{notice}]\n{text}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or Renci.SshNet.Common.SshException or System.Net.Sockets.SocketException or InvalidDataException)
        {
            return ToolResult.Error(ex.Message);
        }
    }

    /// <summary>One end of a copy: a host (user@host) and a path there, or just a path on this PC.</summary>
    internal sealed record Side(string? Host, string Path, bool ContentsOnly)
    {
        public static Side Parse(string text)
        {
            var t = text.Trim();
            string? host = null;
            // user@host:/path, the scp form; a Windows path (C:\...) has no @ before its colon.
            if (HostPath().Match(t) is { Success: true } m)
            {
                host = m.Groups[1].Value;
                t = m.Groups[2].Value.Length == 0 ? "." : m.Groups[2].Value;
            }
            var contents = false;
            foreach (var tail in new[] { "/*", "\\*", "/.", "\\." })
                if (t.Length > tail.Length && t.EndsWith(tail, StringComparison.Ordinal))
                {
                    t = t[..^tail.Length];
                    contents = true;
                }
            return new Side(host, t, contents);
        }
    }

    [GeneratedRegex(@"^([^@\s/\\]+@[^:\s/\\]+(?::\d+)?):(.*)$")]
    private static partial Regex HostPath();

    // ---- this PC -------------------------------------------------------------------------

    private static (int Count, long Bytes) PackLocal(string path, bool contentsOnly, Stream tar)
    {
        using var writer = new TarWriter(tar, TarEntryFormat.Pax, leaveOpen: true);
        int count = 0;
        long bytes = 0;
        if (File.Exists(path) && !contentsOnly)
        {
            Add(path, Path.GetFileName(path));
            return (count, bytes);
        }
        if (!Directory.Exists(path))
            throw new IOException($"{path} does not exist on this PC.");
        var root = contentsOnly ? path : Path.GetDirectoryName(Path.GetFullPath(path).TrimEnd('\\', '/'))!;
        var top = contentsOnly ? null : Path.GetFileName(Path.GetFullPath(path).TrimEnd('\\', '/'));
        if (top is not null)
            writer.WriteEntry(new PaxTarEntry(TarEntryType.Directory, top + "/") { Mode = Dirs });
        foreach (var entry in Directory.EnumerateFileSystemEntries(path, "*", SearchOption.AllDirectories))
        {
            var name = Path.GetRelativePath(root, entry).Replace('\\', '/');
            if (Directory.Exists(entry))
                writer.WriteEntry(new PaxTarEntry(TarEntryType.Directory, name + "/") { Mode = Dirs });
            else
                Add(entry, name);
        }
        return (count, bytes);

        void Add(string file, string name)
        {
            using var data = File.OpenRead(file);
            writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, name) { Mode = Files, DataStream = data });
            count++;
            bytes += data.Length;
        }
    }

    private const UnixFileMode Files = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead;
    private const UnixFileMode Dirs = Files | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;

    /// <summary>
    /// Unpacks into <paramref name="dest"/> if it is a folder (or only contents were sent);
    /// otherwise the one thing sent takes that name, as with cp and scp. Returns where it landed.
    /// </summary>
    private static string UnpackLocal(Stream tar, string dest, Side from)
    {
        var entries = ReadAll(tar);
        var top = Tops(entries);
        var into = Directory.Exists(dest) || from.ContentsOnly;
        if (!into && top.Count != 1)
            throw new IOException($"{dest} is not a folder, and more than one thing would go there.");
        foreach (var e in entries)
        {
            var name = e.Name.TrimEnd('/');
            if (name is "." or "./" || name.Length == 0)
                continue;
            if (name.StartsWith("./")) name = name[2..];
            if (!into)
            {
                // "site/x" lands as dest\x; a single file becomes dest itself.
                var slash = name.IndexOf('/');
                name = slash < 0 ? "" : name[(slash + 1)..];
            }
            var target = name.Length == 0 ? dest : Path.Combine(dest, name.Replace('/', Path.DirectorySeparatorChar));
            if (!Path.GetFullPath(target).StartsWith(Path.GetFullPath(dest), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Refusing to write outside {dest}: {e.Name}");
            if (e.EntryType == TarEntryType.Directory)
                Directory.CreateDirectory(target);
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                using var output = File.Create(target);
                e.DataStream?.CopyTo(output);
            }
        }
        return into && !from.ContentsOnly && top.Count == 1 ? Path.Combine(dest, top[0]) : dest;
    }

    /// <summary>The names at the top of an archive ("site" for site/, site/a.html).</summary>
    private static List<string> Tops(List<TarEntry> entries) =>
        entries.Select(e => e.Name.StartsWith("./") ? e.Name[2..] : e.Name).Select(n => n.TrimEnd('/'))
            .Where(n => n.Length > 0 && n != ".").Select(n => n.Split('/')[0]).Distinct().ToList();

    private static List<TarEntry> ReadAll(Stream tar)
    {
        var list = new List<TarEntry>();
        using var reader = new TarReader(tar, leaveOpen: true);
        while (reader.GetNextEntry(copyData: true) is { } e)
            list.Add(e);
        return list;
    }

    // ---- a host ----------------------------------------------------------------------------

    private static async Task<(int Count, long Bytes)> PackRemoteAsync(SshHost host, string path, bool contentsOnly, Stream tar, CancellationToken ct)
    {
        var p = SshHost.Quote(path);
        var probe = (await host.ExecAsync($"[ -e {p} ] && echo yes; [ -d {p} ] && echo dir; [ -r {p} ] && echo readable; true", null, TimeSpan.FromSeconds(30), ct).ConfigureAwait(false)).Output;
        if (!probe.Contains("yes"))
            throw new IOException($"{path} does not exist on {host.Target} (or its folder can't be entered).");
        var command = contentsOnly && probe.Contains("dir")
            ? $"tar -cf - -C {p} ."
            : $"tar -cf - -C \"$(dirname {p})\" \"$(basename {p})\"";
        // What the login user can't read (a log under /var/log, a key) is packed with sudo.
        var bytes = await host.ExecBytesAsync(command, sudo: !probe.Contains("readable"), Timeout, ct).ConfigureAwait(false);
        await tar.WriteAsync(bytes, ct).ConfigureAwait(false);
        tar.Position = 0;
        var entries = ReadAll(tar);
        tar.SetLength(0);
        // The same entries, written again so the caller's stream is a plain archive from the start.
        using (var writer = new TarWriter(tar, TarEntryFormat.Pax, leaveOpen: true))
            foreach (var e in entries)
                writer.WriteEntry(e);
        var files = entries.Where(e => e.EntryType is TarEntryType.RegularFile or TarEntryType.V7RegularFile).ToList();
        return (files.Count, files.Sum(e => e.Length));
    }

    private static async Task<(string Where, bool Sudo)> UnpackRemoteAsync(SshHost host, Stream tar, string dest, Side from, CancellationToken ct)
    {
        var d = SshHost.Quote(dest);
        var isDir = (await host.ExecAsync($"[ -d {d} ] && echo dir; true", null, TimeSpan.FromSeconds(30), ct).ConfigureAwait(false)).Output.Contains("dir");
        var entries = ReadAll(tar);
        tar.Position = 0;
        var top = Tops(entries);
        if (!isDir && !from.ContentsOnly && top.Count != 1)
            throw new IOException($"{dest} is not a folder on {host.Target}, and more than one thing would go there.");
        string script, where;
        if (isDir || from.ContentsOnly)
        {
            // Into the folder (made if needed), keeping the names.
            script = $"mkdir -p {d} && tar -xf - -C {d} --no-same-owner";
            where = from.ContentsOnly || top.Count != 1 ? dest : dest.TrimEnd('/') + "/" + top[0];
        }
        else
        {
            // As a new name: unpack beside it and rename the one top entry.
            var tmp = $"\"$(dirname {d})/.blackbox-copy-$$\"";
            script = $"mkdir -p \"$(dirname {d})\" && mkdir {tmp} && tar -xf - -C {tmp} --no-same-owner && mv {tmp}/{SshHost.Quote(top.Single())} {d} && rmdir {tmp}";
            where = dest;
        }
        var bytes = ((MemoryStream)tar).ToArray();
        var r = await host.ExecBytesInAsync($"{script} || {{ echo BB-DENIED >&2; exit 1; }}", bytes, sudo: false, Timeout, ct).ConfigureAwait(false);
        if (r.ExitCode == 0)
            return (where, false);
        var s = await host.ExecBytesInAsync(script, bytes, sudo: true, Timeout, ct).ConfigureAwait(false);
        if (s.ExitCode != 0)
            throw new IOException($"Copying to {host.Target}:{dest} failed: {(s.Error.Trim().Length > 0 ? s.Error.Trim() : r.Error.Trim())}");
        return (where, true);
    }

    private static string Size(long bytes) =>
        bytes < 1024 ? $"{bytes} bytes" : bytes < 1024 * 1024 ? $"{bytes / 1024.0:0.#} KB" : $"{bytes / 1048576.0:0.#} MB";
}
