using System.Diagnostics;
using System.Formats.Tar;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using BlackBox.Core.Ssh;

namespace BlackBox.Core.Tools;

/// <summary>
/// Copies files and folders between this PC and SSH hosts, as scp does, byte for byte: a folder
/// travels as one tar stream over the connection the ssh tool keeps, and is unpacked with sudo
/// where the login user can't write. Also copies on this PC, or on one host. The archive streams
/// through a <see cref="CopyPipe"/> while it is made, so a copy of any size takes little memory.
/// </summary>
public sealed partial class CopyTool : Tool
{
    public override string Name => "copy";
    public override string Description =>
        "Copy a file or a folder (with everything in it) between this PC and an SSH host, straight from one host to another (from user@a:/path " +
        "to user@b:/path; nothing is kept on this PC on the way), or within either. Write a host's paths as user@host:/path, " +
        "as for scp. Binary files arrive byte for byte, and size doesn't matter (it streams). A folder copied to a folder that exists goes inside it; " +
        "end the source with /* to copy only what is in it. Where the login user can't write on the host, sudo is used (new files then belong to root).";
    public override ToolRisk Risk => ToolRisk.Risky;
    public override JsonObject Parameters => Schema(
        ("from", Str("What to copy: a path on this PC, or user@host:/path"), true),
        ("to", Str("Where to: a path on this PC, or user@host:/path"), true));
    public override string Describe(JsonObject args) => $"Copy {GetString(args, "from")} to {GetString(args, "to")}";

    /// <summary>How much of a copy may wait in memory between its two ends.</summary>
    internal static long MemoryLimit { get; set; } = 32 << 20;

    /// <summary>A copy in which no byte moved for this long is stopped (a server gone, a command hanging).</summary>
    internal static TimeSpan IdleLimit { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>The most bytes of the last copy that waited on disk at once (for tests).</summary>
    internal static long LastSpilledPeak { get; private set; }

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

            // What goes (its shape and size), and where it lands (into a folder, or as a new name).
            var source = src is null ? LocalSource(ctx.ResolvePath(from.Path), from.ContentsOnly, ct) : await RemoteSourceAsync(src, from.Path, from.ContentsOnly, ct).ConfigureAwait(false);
            var target = dst is null ? LocalTarget(ctx.ResolvePath(to.Path), source) : await RemoteTargetAsync(dst, to.Path, source, ct).ConfigureAwait(false);

            var clock = Stopwatch.StartNew();
            var sudo = await TransferAsync(source, target, ct).ConfigureAwait(false);
            var took = clock.Elapsed;
            var where = to.Host is null ? target.Where : $"{to.Host}:{target.Where}";
            var speed = took > TimeSpan.FromSeconds(10) && source.Bytes > 0
                ? $" in {Duration(took)} ({Size((long)(source.Bytes / took.TotalSeconds))}/s)" : "";
            var text = $"Copied {source.Files} file{(source.Files == 1 ? "" : "s")} ({Size(source.Bytes)}) to {where}{(sudo ? " with sudo" : "")}{speed}.";
            if (target.Skipped > 0)
                text += $" {target.Skipped} link{(target.Skipped == 1 ? "" : "s")} or special file{(target.Skipped == 1 ? "" : "s")} left out (this PC keeps only files and folders).";
            // A config file copied into place (sudoers, sshd, nginx, ...) is checked as if written.
            if (dst is not null && ConfigChecks.KindOf(target.Where) is not null && await ConfigChecks.RunAsync(dst, [target.Where], ct).ConfigureAwait(false) is { } checkedNote)
                text += $"\n[{checkedNote}]";
            return new ToolResult(notice is null ? text : $"[{notice}]\n{text}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or Renci.SshNet.Common.SshException or System.Net.Sockets.SocketException or InvalidDataException)
        {
            return ToolResult.Error(ex is CopyStoppedException { InnerException: { } reason } ? reason.Message : ex.Message);
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

    /// <summary>
    /// What a copy sends: a path on this PC or a host; only what is in it, or the thing itself
    /// under its name (<see cref="Top"/>); whether reading it needs sudo; its files and bytes.
    /// </summary>
    private sealed record Source(SshHost? Host, string Path, bool ContentsOnly, string? Top, bool Sudo, int Files, long Bytes);

    /// <summary>Where a copy lands: unpacked into a folder, or as a new name; whether writing there needs sudo.</summary>
    private sealed record Target(SshHost? Host, string Path, bool Into, string Where, bool Sudo)
    {
        public int Skipped { get; set; }
    }

    /// <summary>
    /// Streams the archive from the source to the target, both ends at once; returns whether sudo
    /// was used to write. A host target the login user turns out not to be able to write to after
    /// all gets the whole copy again with sudo (the source can be read twice; the pipe can't).
    /// </summary>
    private static async Task<bool> TransferAsync(Source source, Target target, CancellationToken ct)
    {
        try
        {
            await TryTransferAsync(source, target, target.Sudo, ct).ConfigureAwait(false);
            return target.Sudo;
        }
        catch (TargetFailedException) when (target.Host is not null && !target.Sudo)
        {
            await TryTransferAsync(source, target, true, ct).ConfigureAwait(false);
            return true;
        }
    }

    private static async Task TryTransferAsync(Source source, Target target, bool sudo, CancellationToken ct)
    {
        // From a host to a host the pipe may spill to temporary files: the source can't be slowed
        // down, and the target may take bytes more slowly than the source sends them.
        using var pipe = new CopyPipe(MemoryLimit, spillToDisk: source.Host is not null && target.Host is not null);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        // The ends on this PC know nothing of Stop: a copy within this PC went on to the end.
        using var stopped = ct.Register(() => pipe.Fail(new OperationCanceledException(ct)));
        var produce = Task.Run(async () =>
        {
            try
            {
                if (source.Host is null)
                    PackLocal(source, pipe.Writer);
                else
                {
                    var (code, error) = await source.Host.ExecStreamAsync(SourceCommand(source), source.Sudo, null, pipe.Writer, stop.Token).ConfigureAwait(false);
                    if (code != 0)
                        throw new IOException($"Reading {source.Path} on {source.Host.Target} failed: {Clip(error)}");
                }
                pipe.Complete();
            }
            catch (Exception e)
            {
                pipe.Fail(e);
                throw;
            }
        }, CancellationToken.None);
        var consume = Task.Run(async () =>
        {
            try
            {
                if (target.Host is null)
                    target.Skipped = UnpackLocal(pipe.Reader, target);
                else
                {
                    var (code, error) = await target.Host.ExecStreamAsync(TargetCommand(target, source), sudo, pipe.Reader, null, stop.Token).ConfigureAwait(false);
                    if (code != 0)
                        throw new TargetFailedException($"Copying to {target.Host.Target}:{target.Path} failed: {Clip(error)}");
                }
            }
            catch (Exception e)
            {
                pipe.Fail(e);
                throw;
            }
        }, CancellationToken.None);

        // A copy in which nothing moves for long is stopped rather than waited for forever.
        var stalled = false;
        var watch = Task.Run(async () =>
        {
            var last = -1L;
            var since = DateTime.UtcNow;
            while (!produce.IsCompleted || !consume.IsCompleted)
            {
                await Task.Delay(500, CancellationToken.None).ConfigureAwait(false);
                var moved = pipe.BytesWritten + pipe.BytesRead;
                if (moved != last)
                    (last, since) = (moved, DateTime.UtcNow);
                else if (DateTime.UtcNow - since > IdleLimit)
                {
                    stalled = true;
                    pipe.Fail(new IOException($"The copy stalled: nothing moved for {Duration(IdleLimit)}."));
                    stop.Cancel();
                    return;
                }
            }
        }, CancellationToken.None);

        try
        {
            // The first end to fail stops the other at once: a source on a host would otherwise go on
            // sending into SSH.NET's buffer until it is done.
            var first = await Task.WhenAny(produce, consume).ConfigureAwait(false);
            if (!first.IsCompletedSuccessfully)
            {
                stop.Cancel();
                pipe.Fail(first.Exception?.InnerException ?? new OperationCanceledException());
            }
            await Task.WhenAll(produce, consume).ConfigureAwait(false);
        }
        catch
        {
            stop.Cancel();
            pipe.Fail(new OperationCanceledException());
            await Task.WhenAll(produce.ContinueWith(_ => { }), consume.ContinueWith(_ => { })).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if (stalled)
                throw new IOException($"The copy stalled: nothing moved for {Duration(IdleLimit)}.");
            // The end that failed on its own says why; the other one only saw the copy stop.
            var own = new[] { produce, consume }.Select(t => t.Exception?.InnerException)
                .FirstOrDefault(e => e is not null and not CopyStoppedException and not OperationCanceledException);
            if (own is not null)
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(own);
            throw;
        }
        finally
        {
            LastSpilledPeak = pipe.SpilledPeak;
            await watch.ConfigureAwait(false);
        }
    }

    /// <summary>The target refused the data (no right to write there, a full disk): worth a try with sudo.</summary>
    private sealed class TargetFailedException(string message) : IOException(message);

    private static string Clip(string error)
    {
        var e = error.Trim();
        return e.Length == 0 ? "no message" : e.Length > 400 ? e[..400] + "…" : e;
    }

    // ---- this PC -------------------------------------------------------------------------

    private static Source LocalSource(string path, bool contentsOnly, CancellationToken ct)
    {
        if (File.Exists(path) && !contentsOnly)
            return new Source(null, path, false, Path.GetFileName(path), false, 1, new FileInfo(path).Length);
        if (!Directory.Exists(path))
            throw new IOException($"{path} does not exist on this PC.");
        var name = Path.GetFileName(Path.GetFullPath(path).TrimEnd('\\', '/'));
        if (name.Length == 0) contentsOnly = true; // a drive's root has no name to land under
        var files = 0;
        var bytes = 0L;
        foreach (var file in new DirectoryInfo(path).EnumerateFiles("*", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();
            files++;
            bytes += file.Length;
        }
        return new Source(null, path, contentsOnly, contentsOnly ? null : name, false, files, bytes);
    }

    private static Target LocalTarget(string dest, Source source)
    {
        var into = Directory.Exists(dest) || source.ContentsOnly;
        return new Target(null, dest, into, into && !source.ContentsOnly ? Path.Combine(dest, source.Top!) : dest, false);
    }

    private static void PackLocal(Source source, Stream tar)
    {
        using var writer = new TarWriter(tar, TarEntryFormat.Pax, leaveOpen: true);
        var path = source.Path;
        if (File.Exists(path) && !source.ContentsOnly)
        {
            Add(path, Path.GetFileName(path));
            return;
        }
        var root = source.ContentsOnly ? path : Path.GetDirectoryName(Path.GetFullPath(path).TrimEnd('\\', '/'))!;
        if (source.Top is not null)
            writer.WriteEntry(new PaxTarEntry(TarEntryType.Directory, source.Top + "/") { Mode = Dirs });
        foreach (var entry in Directory.EnumerateFileSystemEntries(path, "*", SearchOption.AllDirectories))
        {
            var name = Path.GetRelativePath(root, entry).Replace('\\', '/');
            if (Directory.Exists(entry))
                writer.WriteEntry(new PaxTarEntry(TarEntryType.Directory, name + "/") { Mode = Dirs });
            else
                Add(entry, name);
        }

        void Add(string file, string name)
        {
            using var data = File.OpenRead(file);
            writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, name) { Mode = Files, DataStream = data });
        }
    }

    private const UnixFileMode Files = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead;
    private const UnixFileMode Dirs = Files | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;

    /// <summary>
    /// Unpacks the archive as it arrives: into the target folder, or (as a new name) with the one
    /// thing sent taking that name, as with cp and scp. Returns how many entries that aren't files
    /// or folders (links, devices) were left out.
    /// </summary>
    private static int UnpackLocal(Stream tar, Target target)
    {
        var dest = target.Path;
        var skipped = 0;
        using var reader = new TarReader(tar, leaveOpen: true);
        while (reader.GetNextEntry(copyData: false) is { } e)
        {
            var name = e.Name.StartsWith("./") ? e.Name[2..] : e.Name;
            name = name.TrimEnd('/');
            if (name.Length == 0 || name == ".")
                continue;
            if (!target.Into)
            {
                // "site/x" lands as dest\x; a single file becomes dest itself.
                var slash = name.IndexOf('/');
                name = slash < 0 ? "" : name[(slash + 1)..];
            }
            var path = name.Length == 0 ? dest : Path.Combine(dest, name.Replace('/', Path.DirectorySeparatorChar));
            if (!Path.GetFullPath(path).StartsWith(Path.GetFullPath(dest), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Refusing to write outside {dest}: {e.Name}");
            switch (e.EntryType)
            {
                case TarEntryType.Directory:
                    Directory.CreateDirectory(path);
                    break;
                case TarEntryType.RegularFile or TarEntryType.V7RegularFile or TarEntryType.ContiguousFile:
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    using (var output = File.Create(path))
                        e.DataStream?.CopyTo(output);
                    break;
                default:
                    skipped++;
                    break;
            }
        }
        // Whatever follows the archive's end (tar pads it to a full record) is read away.
        tar.CopyTo(Stream.Null);
        return skipped;
    }

    // ---- a host ----------------------------------------------------------------------------

    private static async Task<Source> RemoteSourceAsync(SshHost host, string path, bool contentsOnly, CancellationToken ct)
    {
        var p = SshHost.Quote(path);
        // Readable only if nothing inside is closed to the login user either (a log under /var/log, a key).
        var size = $"n=$(find {p} -type f 2>/dev/null | wc -l); s=$(du -sb {p} 2>/dev/null | cut -f1); " +
                   $"[ -n \"$s\" ] || s=$(( $(du -sk {p} 2>/dev/null | cut -f1) * 1024 )); echo \"BB-SIZE $n $s\"";
        var probe = (await host.ExecAsync($"[ -e {p} ] && echo BB-EXISTS; [ -d {p} ] && echo BB-DIR; " +
                                          $"[ -r {p} ] && [ -z \"$(find {p} ! -readable -print -quit 2>/dev/null)\" ] && echo BB-READABLE; {size}; true",
            null, TimeSpan.FromSeconds(60), ct).ConfigureAwait(false)).Output;
        if (!probe.Contains("BB-EXISTS"))
            throw new IOException($"{path} does not exist on {host.Target} (or its folder can't be entered).");
        var sudo = !probe.Contains("BB-READABLE");
        if (sudo && (await host.SudoAsync(size, null, TimeSpan.FromSeconds(60), ct).ConfigureAwait(false)).Output is { } counted && counted.Contains("BB-SIZE"))
            probe = counted;
        var m = SizeLine().Match(probe);
        var files = m.Success ? int.Parse(m.Groups[1].Value) : 0;
        var bytes = m.Success && long.TryParse(m.Groups[2].Value, out var b) ? b : 0;
        var dir = probe.Contains("BB-DIR");
        // "." (the login folder) or "/" has no name to land under: only what is in it goes.
        var name = path.TrimEnd('/');
        name = name[(name.LastIndexOf('/') + 1)..];
        contentsOnly = dir && (contentsOnly || name is "" or "." or "..");
        return new Source(host, path, contentsOnly, contentsOnly ? null : name, sudo, files, bytes);
    }

    [GeneratedRegex(@"BB-SIZE (\d+) (\d+)")]
    private static partial Regex SizeLine();

    private static string SourceCommand(Source s)
    {
        var p = SshHost.Quote(s.Path);
        return s.ContentsOnly ? $"tar -cf - -C {p} ." : $"tar -cf - -C \"$(dirname {p})\" \"$(basename {p})\"";
    }

    private static async Task<Target> RemoteTargetAsync(SshHost host, string dest, Source source, CancellationToken ct)
    {
        // Whether the target is a folder, and whether the login user can write where the copy lands
        // (the folder itself, or the nearest folder that exists above a new name).
        var d = SshHost.Quote(dest);
        var probe = (await host.ExecAsync($"d={d}; if [ -d \"$d\" ]; then echo BB-DIR; e=$d; else e=$(dirname \"$d\"); " +
                                          "while [ ! -e \"$e\" ]; do e=$(dirname \"$e\"); done; fi; [ -w \"$e\" ] && [ -x \"$e\" ] && echo BB-WRITABLE; true",
            null, TimeSpan.FromSeconds(30), ct).ConfigureAwait(false)).Output;
        var into = probe.Contains("BB-DIR") || source.ContentsOnly;
        var where = into && !source.ContentsOnly ? dest.TrimEnd('/') + "/" + source.Top : dest;
        return new Target(host, dest, into, where, !probe.Contains("BB-WRITABLE"));
    }

    private static string TargetCommand(Target t, Source s)
    {
        var d = SshHost.Quote(t.Path);
        if (t.Into)
            return $"mkdir -p {d} && tar -xf - -C {d} --no-same-owner";
        // As a new name: unpacked beside it, then the one top entry renamed.
        return $"t=\"$(dirname {d})/.blackbox-copy-$$\"; mkdir -p \"$(dirname {d})\" && mkdir \"$t\" && tar -xf - -C \"$t\" --no-same-owner && " +
               $"mv \"$t\"/{SshHost.Quote(s.Top!)} {d} && rmdir \"$t\" || {{ rm -rf \"$t\"; exit 1; }}";
    }

    private static string Size(long bytes) =>
        bytes < 1024 ? $"{bytes} bytes" : bytes < 1024 * 1024 ? $"{bytes / 1024.0:0.#} KB" : bytes < 1L << 30 ? $"{bytes / 1048576.0:0.#} MB" : $"{bytes / (double)(1L << 30):0.##} GB";

    private static string Duration(TimeSpan t) =>
        t.TotalSeconds < 60 ? $"{t.TotalSeconds:0} s" : t.TotalMinutes < 60 ? $"{(int)t.TotalMinutes} min {t.Seconds} s" : $"{(int)t.TotalHours} h {t.Minutes} min";
}
