using System.Text;

namespace BlackBox.Core.Ssh;

/// <summary>
/// The file tools on an SSH host: the same reads, writes, edits and listings, done with small
/// shell commands on a side channel of the connection. What the login user can't read or write
/// (most of /etc) is done with sudo, as an admin would; a file that already exists keeps its
/// owner and permissions, a new one written with sudo belongs to root.
/// </summary>
public static class RemoteFiles
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    /// <summary>Lines <paramref name="offset"/>.. of a text file, numbered as read_file numbers them.</summary>
    public static async Task<(string Text, bool IsError)> ReadAsync(SshHost host, string path, int offset, int limit, int maxChars, CancellationToken ct)
    {
        var end = offset + limit - 1;
        var script = $$"""
            f={{SshHost.Quote(path)}}
            [ -e "$f" ] || { [ -x "$(dirname "$f")" ] && echo "BB-ERR missing" || echo "BB-ERR denied"; exit 0; }
            [ -d "$f" ] && { echo "BB-ERR directory"; exit 0; }
            [ -r "$f" ] || { echo "BB-ERR denied"; exit 0; }
            n=$(head -c 8000 "$f" | wc -c); m=$(head -c 8000 "$f" | tr -d '\000' | wc -c)
            [ "$n" = "$m" ] || { echo "BB-ERR binary $(stat -c %s "$f")"; exit 0; }
            echo "BB-LINES $(wc -l < "$f") $(tail -c 1 "$f" | wc -l) $(stat -c %s "$f")"
            sed -n '{{offset}},{{end}}p' "$f"
            """;
        var (r, sudo) = await RunAsync(host, script, null, "BB-ERR denied", ct).ConfigureAwait(false);
        var output = r.Output;
        if (output.StartsWith("BB-ERR missing")) return ($"File not found on {host.Target}: {path}", true);
        if (output.StartsWith("BB-ERR directory")) return ($"{path} on {host.Target} is a directory; use list_directory.", true);
        if (output.StartsWith("BB-ERR denied")) return (Denied(host, path, "read"), true);
        if (output.StartsWith("BB-ERR binary")) return ($"{path} on {host.Target} is not a text file ({output.Split(' ')[2].Trim()} bytes); read_file reads text only.", true);
        if (!output.StartsWith("BB-LINES "))
            return ($"Could not read {path} on {host.Target}: {(r.Error + output).Trim()}", true);
        var header = output[..output.IndexOf('\n')].Split(' ');
        var total = int.Parse(header[1]) + (header[2] == "0" && header[3] != "0" ? 1 : 0);
        var body = output[(output.IndexOf('\n') + 1)..];
        if (total == 0) return ("(empty file)", false);
        if (offset > total) return ($"The file has only {total} lines.", true);
        var sb = new StringBuilder();
        var n = offset;
        var lines = body.Split('\n');
        var count = body.EndsWith('\n') ? lines.Length - 1 : lines.Length;
        var last = offset - 1;
        for (var i = 0; i < count && sb.Length < maxChars; i++, n++)
        {
            sb.Append(n).Append('\t').Append(lines[i]).Append('\n');
            last = n;
        }
        if (offset > 1 || last < total)
            sb.Append($"[showing lines {offset}-{last} of {total}; use offset to read more]\n");
        if (sudo) sb.Append("[read with sudo]\n");
        return (sb.ToString(), false);
    }

    /// <summary>Creates, replaces or appends to a file; parent folders are created.</summary>
    /// <param name="checkConfig">Check the file afterwards if it is a config a program verifies (see <see cref="ConfigChecks"/>).</param>
    public static async Task<(string Text, bool IsError)> WriteAsync(SshHost host, string path, string content, bool append, CancellationToken ct, bool checkConfig = true)
    {
        var p = SshHost.Quote(path);
        var check = $"[ -e {p} ] && echo exists; [ -d {p} ] && echo directory; true";
        var state = (await host.ExecAsync(check, null, Timeout, ct).ConfigureAwait(false)).Output;
        if (state.Contains("directory")) return ($"{path} on {host.Target} is a directory.", true);
        var existed = state.Contains("exists");
        if (append && !existed)
            return ($"Cannot append: {path} does not exist on {host.Target}. Check the path, or create the file first with append=false.", true);
        var script = $"mkdir -p \"$(dirname {p})\" 2>/dev/null; cat {(append ? ">>" : ">")} {p} 2>/dev/null || {{ echo BB-DENIED; exit 1; }}";
        var (r, sudo) = await RunAsync(host, script, content, "BB-DENIED", ct).ConfigureAwait(false);
        if (r.Output.Contains("BB-DENIED") || r.ExitCode != 0)
            return (r.Output.Contains("BB-DENIED") ? Denied(host, path, "write") : $"Could not write {path} on {host.Target}: {r.Error.Trim()}", true);
        var how = sudo ? " with sudo" : "";
        var done = append ? $"Appended {content.Length} chars to {path} on {host.Target}{how}."
                          : $"{(existed ? "Overwrote" : "Created")} {path} on {host.Target}{how} ({content.Length} chars).";
        return (done + (checkConfig ? await CheckAsync(host, path, ct).ConfigureAwait(false) : ""), false);
    }

    /// <summary>A line "[Checked after writing: ...]" for a config file, or nothing.</summary>
    private static async Task<string> CheckAsync(SshHost host, string path, CancellationToken ct) =>
        ConfigChecks.KindOf(path) is null ? "" : await ConfigChecks.RunAsync(host, [path], ct).ConfigureAwait(false) is { } note ? $"\n[{note}]" : "";

    /// <summary>Replaces an exact snippet (or every occurrence) and writes the file back in place.</summary>
    public static async Task<(string Text, bool IsError)> EditAsync(SshHost host, string path, string oldText, string newText, bool all, CancellationToken ct)
    {
        var p = SshHost.Quote(path);
        var read = $"[ -f {p} ] || {{ [ -x \"$(dirname {p})\" ] && echo BB-MISSING || echo BB-DENIED; exit 0; }}; [ -r {p} ] || {{ echo BB-DENIED; exit 0; }}; printf 'BB-OK\\n'; cat {p}";
        var (r, sudoRead) = await RunAsync(host, read, null, "BB-DENIED", ct).ConfigureAwait(false);
        if (r.Output.StartsWith("BB-MISSING")) return ($"File not found on {host.Target}: {path}", true);
        if (r.Output.StartsWith("BB-DENIED")) return (Denied(host, path, "read"), true);
        if (!r.Output.StartsWith("BB-OK\n")) return ($"Could not read {path} on {host.Target}: {r.Error.Trim()}", true);
        var text = r.Output["BB-OK\n".Length..];
        var count = Count(text, oldText);
        if (count == 0 && text.Contains("\r\n") && oldText.Contains('\n') && !oldText.Contains('\r'))
        {
            oldText = oldText.Replace("\n", "\r\n");
            newText = newText.ReplaceLineEndings("\r\n");
            count = Count(text, oldText);
        }
        if (count == 0)
            return ("old_text was not found in the file. Read the file again and copy the text exactly.", true);
        if (count > 1 && !all)
            return ($"old_text occurs {count} times. Include more surrounding text to make it unique, or set replace_all to change every one.", true);
        var written = await WriteAsync(host, path, text.Replace(oldText, newText, StringComparison.Ordinal), append: false, ct, checkConfig: false).ConfigureAwait(false);
        if (written.IsError)
            return written;
        var how = sudoRead || written.Text.Contains("with sudo") ? " with sudo" : "";
        var done = count > 1 ? $"Edited {path} on {host.Target}{how}: replaced {count} occurrences." : $"Edited {path} on {host.Target}{how}.";
        return (done + await CheckAsync(host, path, ct).ConfigureAwait(false), false);
    }

    /// <summary>A folder's entries, or with a pattern a search through its subfolders.</summary>
    public static async Task<(string Text, bool IsError)> ListAsync(SshHost host, string path, string? pattern, CancellationToken ct)
    {
        var p = SshHost.Quote(path);
        var script = pattern is null
            ? $"[ -d {p} ] || {{ echo BB-MISSING; exit 0; }}; [ -r {p} ] && [ -x {p} ] || {{ echo BB-DENIED; exit 0; }}; ls -la --time-style=+%Y-%m-%d.%H:%M {p} | head -501"
            : $"[ -d {p} ] || {{ echo BB-MISSING; exit 0; }}; timeout 20 find {p} -name {SshHost.Quote(pattern)} -printf '%p  (%s bytes)\\n' 2>/dev/null | head -501";
        var (r, sudo) = await RunAsync(host, script, null, "BB-DENIED", ct).ConfigureAwait(false);
        if (r.Output.StartsWith("BB-MISSING")) return ($"Directory not found on {host.Target}: {path}", true);
        if (r.Output.StartsWith("BB-DENIED")) return (Denied(host, path, "list"), true);
        var lines = r.Output.TrimEnd('\n').Split('\n');
        var sb = new StringBuilder().Append(host.Target).Append(':').Append(path).Append('\n');
        foreach (var line in lines.Take(500))
            sb.Append(line).Append('\n');
        if (lines.Length > 500) sb.Append("[stopped after 500 entries]\n");
        if (sudo) sb.Append("[listed with sudo]\n");
        return (sb.ToString(), false);
    }

    /// <summary>
    /// Runs a script as the login user; if it reports <paramref name="denied"/>, again with sudo.
    /// Returns the result and whether sudo was used.
    /// </summary>
    private static async Task<(ExecResult Result, bool Sudo)> RunAsync(SshHost host, string script, string? stdin, string denied, CancellationToken ct)
    {
        var r = await host.ExecAsync(script, stdin, Timeout, ct).ConfigureAwait(false);
        if (!r.Output.Contains(denied))
            return (r, false);
        var s = await host.SudoAsync(script, stdin, Timeout, ct).ConfigureAwait(false);
        // sudo refusing (it wants a password BlackBox doesn't have) reads as "denied" too.
        return s.ExitCode == 0 || !s.Error.Contains("password") ? (s, true) : (r, false);
    }

    private static string Denied(SshHost host, string path, string what) =>
        host.SudoNeedsPassword == true && host.Password is null
            ? $"Permission denied to {what} {path} on {host.Target}, and sudo there asks for a password. If the user gave you one, pass it to ssh as password once; then this works with sudo."
            : $"Permission denied to {what} {path} on {host.Target}, even with sudo.";

    private static int Count(string text, string part)
    {
        var count = 0;
        for (var i = text.IndexOf(part, StringComparison.Ordinal); i >= 0; i = text.IndexOf(part, i + part.Length, StringComparison.Ordinal))
            count++;
        return count;
    }
}
