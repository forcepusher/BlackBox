using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using BlackBox.Core.Ssh;

namespace BlackBox.Core.Tools;

/// <summary>
/// Commands on another computer over SSH, in a terminal that stays open between calls (see
/// <see cref="SshHost"/>). Long commands keep running while the model checks back, prompts can be
/// answered, and sudo's password is typed for it when the user gave one.
/// </summary>
public sealed partial class SshTool : Tool
{
    internal const int MaxOutput = 16_000;

    public override string Name => "ssh";
    public override string Description =>
        "Run a command on another computer (a Linux server) over SSH, in a terminal that stays open between calls: " +
        "the directory and exported variables carry over, as in a real terminal. The first call to a host logs in, and its result begins with " +
        "what the server is (system, hostname, the address this PC connects from, whether sudo needs a password), so make it a real step of the work; " +
        "later calls reuse the login. " +
        "Returns the output and the exit code. A command still running when wait_seconds are up goes on running: call ssh again with the same host " +
        "and no command to see more of its output, with input to answer a question it asks (y, a password), or with interrupt to stop it (Ctrl+C). " +
        "Put sudo in front of each command that needs root instead of opening a root shell. Don't start full-screen programs (nano, vim, less, top): " +
        "read and change files on the host with read_file, write_file, edit_file and list_directory and their host argument.";
    public override ToolRisk Risk => ToolRisk.Risky;
    public override JsonObject Parameters => Schema(
        ("host", Str("user@host, user@host:port, or a Host from ~/.ssh/config; leave it out to stay on the server of your last ssh call"), false),
        ("command", Str("bash command(s) to run; several lines are fine"), false),
        ("input", Str("Text to type into the running command, followed by Enter"), false),
        ("interrupt", Bool("Press Ctrl+C to stop the running command"), false),
        ("wait_seconds", Int("How long to wait for the command before returning (default 120, max 1800); it keeps running after that"), false),
        ("key_file", Str("Private key file on this PC to log in with (default: the keys in ~/.ssh). A key the open connection didn't use logs in afresh with it alone: the way to test a key you installed"), false),
        ("password", Str("The login or sudo password, if the user gave you one: pass it once and BlackBox answers sudo's prompts itself (no echo pw | sudo -S)"), false));

    public override string Describe(JsonObject args)
    {
        var host = GetString(args, "host") ?? "(same server)";
        if (GetBool(args, "interrupt")) return $"SSH {host}: Ctrl+C";
        if (GetString(args, "input") is { } input)
            return $"SSH {host}: type {(LooksSecret(input, GetString(args, "password")) ? "(password)" : input)}";
        return GetString(args, "command") is { Length: > 0 } c ? $"SSH {host}: {c}" : $"SSH {host}: wait for output";
    }

    public override async Task<ToolResult> ExecuteAsync(JsonObject args, ToolContext ctx, CancellationToken ct)
    {
        if (ctx.Ssh is null)
            return ToolResult.Error("SSH is not available in this run.");
        // Models often leave the host out once they are "in" a server, as a person at a terminal would.
        var target = GetString(args, "host") is { Length: > 0 } h ? h : ctx.Ssh.LastTarget;
        if (target is null)
            return ToolResult.Error("Give host as user@host (with key_file if the user named a key) for the first ssh call.");
        var command = GetString(args, "command");
        var input = GetString(args, "input");
        var interrupt = GetBool(args, "interrupt");
        var wait = TimeSpan.FromSeconds(Math.Clamp(GetNumber(args, "wait_seconds") ?? 120, 1, 1800));
        if (command is { Length: > 0 } && Refusal(command) is { } refused)
            return ToolResult.Error(refused);

        SshHost host;
        string? notice;
        try
        {
            (host, notice) = await ctx.Ssh.GetAsync(target, GetString(args, "key_file") is { Length: > 0 } k ? ctx.ResolvePath(k) : null,
                GetString(args, "password"), ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or Renci.SshNet.Common.SshException or System.Net.Sockets.SocketException)
        {
            return ToolResult.Error(ex.Message);
        }

        ShellResult result;
        try
        {
            if (interrupt)
                result = await host.InterruptAsync(ct).ConfigureAwait(false);
            else if (input is not null && command is not { Length: > 0 })
                result = await host.SendInputAsync(input, wait, ct).ConfigureAwait(false);
            else if (command is { Length: > 0 })
            {
                if (host.IsBusy)
                    return ToolResult.Error(Prefix(notice) + "The previous command is still running in this terminal. Call ssh with no command to wait for it, " +
                                            "with input to answer it, or with interrupt to stop it; then run the next one.");
                result = await host.RunAsync(command, wait, ct).ConfigureAwait(false);
            }
            else if (host.IsBusy)
                result = await host.WaitAsync(wait, ct).ConfigureAwait(false);
            else
                return new ToolResult(Prefix(notice) + "Nothing is running in the terminal; give a command.");
        }
        catch (InvalidOperationException ex)
        {
            return ToolResult.Error(Prefix(notice) + ex.Message + (input is not null ? " Run the command again if it still needs the answer." : ""));
        }
        catch (Exception ex) when (ex is IOException or Renci.SshNet.Common.SshException or ObjectDisposedException)
        {
            return ToolResult.Error(Prefix(notice) + $"The connection to {host.Target} failed: {ex.Message}. If the server is rebooting, wait a little and try again.");
        }
        return Format(result, notice, wait);
    }

    internal static ToolResult Format(ShellResult r, string? notice, TimeSpan wait)
    {
        var sb = new StringBuilder(Prefix(notice));
        var output = TerminalText.Clip(r.Output, MaxOutput);
        if (output.Length > 0)
            sb.Append(output).Append('\n');
        if (r.Note is { } note)
            sb.Append('[').Append(note).Append("]\n");
        switch (r.State)
        {
            case ShellState.Done:
                sb.Append($"[exit code {r.ExitCode} · {r.Directory}]");
                return new ToolResult(sb.ToString(), IsError: r.ExitCode != 0);
            case ShellState.Running:
                sb.Append($"[still running after {wait.TotalSeconds:0} s. Call ssh with the same host and no command to wait for more output, " +
                          "input to answer it, or interrupt to stop it.]");
                break;
            case ShellState.WaitingForInput:
                sb.Append($"[it waits for input: \"{r.Prompt}\". Answer with input (Enter is added), or stop it with interrupt.]");
                break;
            case ShellState.Ended:
                sb.Append("[the terminal ended (exit, logout or a reboot); the next command gets a new one, in the home folder]");
                break;
        }
        return new ToolResult(sb.ToString());
    }

    private static string Prefix(string? notice) => notice is null ? "" : $"[{notice}]\n";

    private const string UseFileTools = "Read files with read_file and change them with edit_file or write_file, with host set; or use cat, grep, sed -n.";

    /// <summary>
    /// A one-line command that would leave the terminal waiting for keys, with what to do
    /// instead: an interactive shell as root or another user, or a full-screen program.
    /// </summary>
    internal static string? Refusal(string command)
    {
        var c = command.Trim();
        if (c.Contains('\n') || c.Contains(';') || c.Contains('|') || c.Contains('&'))
            return null;
        var words = Words().Matches(c).Select(m => m.Value).ToList();
        if (words.Count == 0)
            return null;
        var i = 0;
        var loginFlag = false;
        if (words[0] == "sudo")
        {
            for (i = 1; i < words.Count && words[i].StartsWith('-'); i++)
            {
                var w = words[i];
                if (w.StartsWith("--"))
                    continue;
                if (w.Contains('i') || w.Contains('s')) loginFlag = true;
                // -u USER, -g GROUP and the like take the next word (also at the end of a cluster: -iu postgres).
                if (w[^1] is 'u' or 'g' or 'h' or 'p' or 'C' or 'D' or 'r' or 't' or 'T') i++;
            }
            if (i >= words.Count && loginFlag)
                return Shell();
        }
        if (i >= words.Count)
            return null;
        var program = Path.GetFileName(words[i]);
        var rest = words.Skip(i + 1).ToList();
        if (program == "su" && rest.All(w => w is "-" or "-l" or "--login" || !w.StartsWith('-')) && rest.Count <= 2)
            return Shell();
        if (program is "bash" or "sh" or "zsh" && (i > 0 || rest.Count == 0) && rest.All(w => w.StartsWith('-') && w != "-c"))
            return Shell();
        if (program is "nano" or "vim" or "vi" or "emacs" or "less" or "more" or "pico" or "joe" or "mcedit" or "mc" or "htop")
            return $"{program} is a full-screen program and can't be used through this terminal. " + (program == "htop" ? "Use top -bn1 | head -30." : UseFileTools);
        if (program == "top" && !rest.Any(w => w.StartsWith("-b") || w.Contains("b") && w.StartsWith('-')))
            return "top without -b redraws the screen and waits for keys; use top -bn1 | head -30, or ps aux --sort=-%cpu | head.";
        if (program == "visudo" && !rest.Any(w => w.StartsWith('-') && w.Contains('c')))
            return "visudo opens an editor. Write the rule to a file in /etc/sudoers.d/ with write_file (host set), then check it with sudo visudo -cf FILE.";
        if (program == "crontab" && rest.Contains("-e"))
            return "crontab -e opens an editor. Read the table with crontab -l, and write it with (crontab -l; echo '30 2 * * * ...') | crontab -, " +
                   "or put a file in /etc/cron.d/ with write_file.";
        if (program == "systemctl" && rest.FirstOrDefault(w => !w.StartsWith('-')) == "edit")
            return "systemctl edit opens an editor. Write the drop-in with write_file (host set), e.g. /etc/systemd/system/NAME.service.d/override.conf, " +
                   "then run sudo systemctl daemon-reload.";
        return null;

        static string Shell() => "That opens an interactive shell, which waits for typed commands. Put sudo in front of each command instead " +
                                 "(sudo systemctl restart nginx), sudo -u USER for another user, or sudo bash -c '...' for several.";
    }

    private static bool LooksSecret(string input, string? password) =>
        password is { Length: > 0 } && input.Contains(password, StringComparison.Ordinal);

    [GeneratedRegex(@"[^\s]+")]
    private static partial Regex Words();
}
