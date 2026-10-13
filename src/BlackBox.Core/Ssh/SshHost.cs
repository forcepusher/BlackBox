using System.Text;
using System.Text.RegularExpressions;
using Renci.SshNet;

namespace BlackBox.Core.Ssh;

/// <summary>What became of a command in the terminal by the time the call returned.</summary>
public enum ShellState
{
    /// <summary>Finished; <see cref="ShellResult.ExitCode"/> and <see cref="ShellResult.Directory"/> are set.</summary>
    Done,
    /// <summary>Still running when the wait was up; it goes on.</summary>
    Running,
    /// <summary>Quiet, with a question on its last line: it waits for an answer.</summary>
    WaitingForInput,
    /// <summary>The shell or the connection ended (exit, logout, reboot); the next command gets a new one.</summary>
    Ended,
}

/// <param name="Output">What the command printed since the previous call, as a terminal shows it.</param>
/// <param name="Prompt">For <see cref="ShellState.WaitingForInput"/>: the question.</param>
/// <param name="Note">Something BlackBox did on its own (typed the sudo password, stopped a full-screen program).</param>
public sealed record ShellResult(ShellState State, string Output, int? ExitCode = null, string? Directory = null, string? Prompt = null, string? Note = null);

/// <summary>A command run on a side channel, outside the terminal.</summary>
public sealed record ExecResult(int ExitCode, string Output, string Error);

/// <summary>
/// One logged-in server: a terminal that stays open between commands, like the one a person
/// keeps open (the directory, exported variables, functions and aliases carry over), and side
/// channels on the same connection for files and quick looks.
/// <para>
/// Each command is written to a file in a private folder on the server and run from there in a
/// subshell of the terminal's bash. So its text never goes through the terminal (no echo, no
/// line-length limit, no quoting), nothing it reads from the terminal can swallow the next
/// command, and an <c>exit</c> or <c>set -e</c> in it can't end the terminal. When the subshell
/// ends, it leaves its directory and exports behind, and before its next prompt the terminal
/// takes them over and prints a marker with the exit code (from PROMPT_COMMAND, because bash
/// drops the rest of a command line cut short by Ctrl+C).
/// </para>
/// </summary>
public sealed partial class SshHost : IDisposable
{
    private readonly SshClient _client;
    private readonly object _gate = new();
    private readonly StringBuilder _raw = new();
    private readonly SemaphoreSlim _signal = new(0);
    private ShellStream? _shell;
    private Thread? _reader;
    private bool _shellClosed;
    private DateTime _lastData = DateTime.UtcNow;
    private string _dir = "";
    private int _commandId;
    private Running? _running;
    private bool _disposed;

    private sealed class Running(int id)
    {
        public int Id { get; } = id;
        public int SudoAnswers { get; set; }
        public bool FullScreen { get; set; }
        /// <summary>The output (by its time) after which the command's processes were found idle.</summary>
        public DateTime IdleAfter { get; set; }
        public DateTime NextIdleCheck { get; set; }
    }

    internal SshHost(SshClient client, SshTarget target, string? password)
    {
        _client = client;
        Target = target;
        Password = password;
    }

    public SshTarget Target { get; }

    /// <summary>The key files (full paths) offered when logging in.</summary>
    internal IReadOnlyList<string> LoginKeys { get; init; } = [];

    /// <summary>The user's password for this server, if they gave one: typed into sudo's prompt.</summary>
    public string? Password { get; set; }

    public bool IsConnected => !_disposed && _client.IsConnected;

    /// <summary>Whether a command started in the terminal hasn't finished yet.</summary>
    public bool IsBusy => _running is not null && !_shellClosed;

    // ---- the terminal --------------------------------------------------------------------

    /// <summary>Starts a command in the terminal and waits for it at most <paramref name="wait"/>.</summary>
    /// <param name="returnWhenQuiet">Return early once it has printed nothing for <see cref="QuietReturn"/> (a server started in the foreground).</param>
    public async Task<ShellResult> RunAsync(string command, TimeSpan wait, CancellationToken ct, bool returnWhenQuiet = true)
    {
        if (IsBusy)
            throw new InvalidOperationException("The previous command is still running.");
        await EnsureShellAsync(ct).ConfigureAwait(false);
        var id = ++_commandId;
        var f = $"{_dir}/{id}";
        await WriteRemoteAsync($"{f}.sh", command.ReplaceLineEndings("\n") + "\n", ct).ConfigureAwait(false);
        lock (_gate)
            _raw.Clear();
        _running = new Running(id);
        // The subshell runs the command and saves its state on the way out (also after an exit);
        // the terminal's PROMPT_COMMAND (set up in EnsureShellAsync) takes it over and prints the
        // marker. A prompt comes also when Ctrl+C cuts the line short, so the marker always does.
        var line = $"__bb_id={id}; ( trap 'pwd >{f}.pwd; export -p | sed \"s/^declare -x /declare -gx /\" >{f}.env; " +
                   $"declare -f >{f}.fn; alias -p >{f}.al' EXIT; . {f}.sh )\n";
        _shell!.Write(line);
        _shell.Flush();
        return await CollectAsync(wait, ct, returnWhenQuiet: returnWhenQuiet).ConfigureAwait(false);
    }

    /// <summary>Types <paramref name="text"/> and Enter into the running command, then waits for what it does.</summary>
    public async Task<ShellResult> SendInputAsync(string text, TimeSpan wait, CancellationToken ct, bool returnWhenQuiet = true)
    {
        if (!IsBusy)
            throw new InvalidOperationException("No command is running, so nothing waits for input.");
        // If the command finished meanwhile, bash would run the answer as a command line.
        bool finished;
        lock (_gate)
            finished = Marker().Match(_raw.ToString()) is { Success: true } m && m.Groups[1].Value == _running!.Id.ToString();
        if (finished)
        {
            var r = await CollectAsync(TimeSpan.Zero, ct).ConfigureAwait(false);
            return r with { Note = (r.Note is null ? "" : r.Note + " ") + "It had finished before the input came, so nothing was typed." };
        }
        _shell!.Write(text + "\r");
        _shell.Flush();
        await Task.Delay(200, ct).ConfigureAwait(false);
        return await CollectAsync(wait, ct, returnWhenQuiet: returnWhenQuiet).ConfigureAwait(false);
    }

    /// <summary>Waits for more output from the running command.</summary>
    public Task<ShellResult> WaitAsync(TimeSpan wait, CancellationToken ct, bool returnWhenQuiet = true)
    {
        if (!IsBusy)
            throw new InvalidOperationException("No command is running.");
        return CollectAsync(wait, ct, returnWhenQuiet: returnWhenQuiet);
    }

    /// <summary>Ctrl+C, and if the command doesn't stop, its processes are killed (the terminal stays).</summary>
    public async Task<ShellResult> InterruptAsync(CancellationToken ct)
    {
        if (!IsBusy)
            throw new InvalidOperationException("No command is running.");
        _shell!.Write("\x03");
        _shell.Flush();
        var r = await CollectAsync(TimeSpan.FromSeconds(3), ct, waitForPrompt: false).ConfigureAwait(false);
        if (r.State != ShellState.Running && r.State != ShellState.WaitingForInput)
            return r;
        await KillForegroundAsync(ct).ConfigureAwait(false);
        var after = await CollectAsync(TimeSpan.FromSeconds(5), ct, waitForPrompt: false).ConfigureAwait(false);
        return after with { Output = r.Output + after.Output, Note = "Ctrl+C did not stop it, so its processes were killed." };
    }

    /// <summary>How long a command may print nothing (after printing something) before the call returns anyway.</summary>
    internal static TimeSpan QuietReturn { get; set; } = TimeSpan.FromSeconds(30);

    private async Task<ShellResult> CollectAsync(TimeSpan wait, CancellationToken ct, bool waitForPrompt = true, bool returnWhenQuiet = true)
    {
        var until = DateTime.UtcNow + wait;
        var notes = new List<string>();
        while (true)
        {
            string raw;
            DateTime last;
            lock (_gate)
            {
                raw = _raw.ToString();
                last = _lastData;
            }
            var running = _running;
            if (running is not null && Marker().Match(raw) is { Success: true } m && m.Groups[1].Value == running.Id.ToString())
            {
                lock (_gate)
                    _raw.Remove(0, Math.Min(_raw.Length, m.Index + m.Length));
                _running = null;
                return new(ShellState.Done, Tidy(raw[..m.Index]), int.Parse(m.Groups[2].Value), m.Groups[3].Value, Note: Join(notes));
            }
            if (_shellClosed || !_client.IsConnected)
            {
                lock (_gate)
                    _raw.Clear();
                _running = null;
                return new(ShellState.Ended, Tidy(raw), Note: Join(notes));
            }
            if (running is not null && !running.FullScreen && TerminalText.EntersFullScreen(raw))
            {
                // vim, less, top and the like draw a screen and wait for keys; they can't be used here.
                running.FullScreen = true;
                await KillForegroundAsync(ct).ConfigureAwait(false);
                notes.Add("It started a full-screen program, which can't be used through this terminal, so it was stopped.");
            }

            var quiet = DateTime.UtcNow - last;
            var lastLine = TerminalText.Clean(raw[(raw.LastIndexOf('\n') + 1)..]);
            // sudo prints its prompt also when it reads the password from a pipe (echo pw | sudo -S),
            // and checking a password can take seconds on a slow machine: only a prompt that stays
            // unanswered for a while is one that waits for the terminal.
            var sudoPrompt = quiet > TimeSpan.FromSeconds(3) && TerminalText.IsSudoPrompt(lastLine);
            var prompt = waitForPrompt && quiet > TimeSpan.FromSeconds(2.5) && TerminalText.LooksLikePrompt(lastLine);
            if (running is not null && (sudoPrompt || prompt) && running.IdleAfter != last)
            {
                // A question answered through a pipe (echo y | ufw enable) can be followed by quiet
                // work: it waits for the terminal only while the command's processes sit idle.
                if (DateTime.UtcNow >= running.NextIdleCheck)
                {
                    if (await IsIdleAsync(ct).ConfigureAwait(false))
                        running.IdleAfter = last;
                    running.NextIdleCheck = DateTime.UtcNow + TimeSpan.FromSeconds(3);
                    continue;
                }
            }
            else if (running is not null && sudoPrompt)
            {
                if (Password is { Length: > 0 } pw && running.SudoAnswers == 0)
                {
                    running.SudoAnswers++;
                    lock (_gate)
                        _raw.Append('\n');
                    _shell!.Write(pw + "\r");
                    _shell.Flush();
                    notes.Add("BlackBox typed the sudo password.");
                    await Task.Delay(300, ct).ConfigureAwait(false);
                    continue;
                }
                return Waiting(raw, lastLine, running.SudoAnswers > 0
                    ? "sudo asked again, so the password BlackBox has for this server is wrong."
                    : "sudo asks for the password; if the user gave you one, send it with input, or pass password to ssh.");
            }
            else if (running is not null && prompt)
                return Waiting(raw, lastLine, null);
            // Silent for long after printing something: often a server started in the foreground,
            // and the model may want to move on rather than sit out the whole wait.
            var silent = waitForPrompt && returnWhenQuiet && quiet > QuietReturn && DateTime.UtcNow - (until - wait) > QuietReturn && raw.Trim().Length > 0;
            if (silent) notes.Add($"No output for {QuietReturn.TotalSeconds:0} s.");
            if (DateTime.UtcNow >= until || silent)
            {
                // A marker may be arriving in pieces: keep a trailing fragment of one for the next call.
                var keep = raw.LastIndexOf('\x1e') is var k and >= 0 && !raw[k..].Contains('\n') ? raw.Length - k : 0;
                lock (_gate)
                    _raw.Remove(0, Math.Min(_raw.Length, raw.Length - keep));
                return new(ShellState.Running, Tidy(raw[..(raw.Length - keep)]), Note: Join(notes));
            }
            var left = until - DateTime.UtcNow;
            await _signal.WaitAsync(left < TimeSpan.FromMilliseconds(250) ? left : TimeSpan.FromMilliseconds(250), ct).ConfigureAwait(false);
        }

        ShellResult Waiting(string raw, string prompt, string? note)
        {
            lock (_gate)
                _raw.Remove(0, Math.Min(_raw.Length, raw.Length));
            if (note is not null) notes.Add(note);
            return new(ShellState.WaitingForInput, Tidy(raw), Prompt: prompt.Trim(), Note: Join(notes));
        }

        static string? Join(List<string> notes) => notes.Count == 0 ? null : string.Join(" ", notes);
    }

    /// <summary>The terminal's text without the escape codes and without the lines BlackBox itself typed.</summary>
    private static string Tidy(string raw)
    {
        var text = TerminalText.Clean(raw);
        // If a command turned echo back on, the next command line shows once; it isn't output.
        var lines = text.Split('\n').Where(l => !l.StartsWith("__bb_id=", StringComparison.Ordinal));
        return string.Join('\n', lines).Trim('\n');
    }

    private async Task EnsureShellAsync(CancellationToken ct)
    {
        if (_shell is not null && !_shellClosed)
            return;
        _shell?.Dispose();
        _shellClosed = false;
        _running = null;
        lock (_gate)
            _raw.Clear();
        // A dumb terminal: programs print plain text, without colours, pagers or progress bars drawn with cursor moves.
        var shell = _client.CreateShellStream("dumb", 200, 50, 0, 0, 1 << 16);
        shell.Closed += (_, _) => { _shellClosed = true; _signal.Release(); };
        _shell = shell;
        _reader = new Thread(() => ReadLoop(shell)) { IsBackground = true, Name = "ssh terminal " + Target };
        _reader.Start();
        // What follows is bash: a login shell that is another one (sh, zsh, fish) makes way for it.
        shell.Write("[ -n \"$BASH_VERSION\" ] || exec bash -l\n");
        // No echo and no line editing, no prompt, no history; programs told not to page. Before
        // each prompt, __bb_done takes over the state the last command's subshell left (its
        // directory, exports, functions, aliases) and prints the marker with its exit code.
        shell.Write("stty -echo 2>/dev/null; set +o emacs +o vi 2>/dev/null; unset HISTFILE; " +
                    "export TERM=dumb PAGER=cat SYSTEMD_PAGER=cat GIT_PAGER=cat MANPAGER=cat SYSTEMD_COLORS=0 LESS=FRX; PS1=; PS2=; " +
                    "__bb_d=$(mktemp -d /tmp/.blackbox-XXXXXX) && echo $$ >\"$__bb_d/pid\" && tty | sed 's|^/dev/||' >\"$__bb_d/tty\"; " +
                    // Typed-ahead input no program read (an answer sent just after its question went away) is
                    // dropped here, or bash would run it as the next command line.
                    "__bb_done() { local rc=$? f; [ -n \"$__bb_id\" ] || return 0; f=$__bb_d/$__bb_id; stty -echo 2>/dev/null; " +
                    "while read -r -t 0.05 __bb_junk; do :; done; " +
                    "cd \"$(cat \"$f.pwd\" 2>/dev/null || pwd)\" 2>/dev/null; . \"$f.env\" 2>/dev/null; . \"$f.fn\" 2>/dev/null; . \"$f.al\" 2>/dev/null; " +
                    "printf '\\036BB-%s-%s-%s\\036\\n' \"$__bb_id\" \"$rc\" \"$PWD\"; rm -f \"$f\".*; __bb_id=; }; " +
                    "PROMPT_COMMAND=__bb_done; printf '\\036BB-INIT-%s-%s\\036\\n' \"${BASH_VERSION:+bash}\" \"$__bb_d\"\n");
        shell.Flush();
        var until = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (DateTime.UtcNow < until)
        {
            string raw;
            lock (_gate)
                raw = _raw.ToString();
            if (InitMarker().Match(raw) is { Success: true } m)
            {
                _dir = m.Groups[2].Value;
                lock (_gate)
                    _raw.Clear();
                if (m.Groups[1].Value == "bash")
                    return;
                shell.Dispose();
                _shell = null;
                throw new IOException($"{Target} has no bash, which the ssh tool's terminal needs (its login shell is another one). " +
                                      "Use the file tools with host, and copy.");
            }
            if (_shellClosed)
                break;
            await _signal.WaitAsync(TimeSpan.FromMilliseconds(250), ct).ConfigureAwait(false);
        }
        throw new IOException($"The terminal on {Target} did not start (no bash?).");
    }

    private void ReadLoop(ShellStream shell)
    {
        var buffer = new byte[16384];
        var decoder = Encoding.UTF8.GetDecoder();
        var chars = new char[Encoding.UTF8.GetMaxCharCount(buffer.Length)];
        try
        {
            while (!_disposed)
            {
                var n = shell.Read(buffer, 0, buffer.Length);
                if (n == 0)
                {
                    if (_shellClosed || !_client.IsConnected) break;
                    Thread.Sleep(20);
                    continue;
                }
                var c = decoder.GetChars(buffer, 0, n, chars, 0);
                lock (_gate)
                {
                    _raw.Append(chars, 0, c);
                    _lastData = DateTime.UtcNow;
                }
                _signal.Release();
            }
        }
        catch (Exception) when (!_disposed)
        {
            // The connection went away; the state says so.
        }
        catch (Exception)
        {
        }
        _shellClosed = true;
        _signal.Release();
    }

    /// <summary>
    /// Kills what runs in the terminal: every process on its tty except its bash. What runs as
    /// root (under sudo) is killed with sudo.
    /// </summary>
    private async Task KillForegroundAsync(CancellationToken ct)
    {
        var list = $"t=$(cat {_dir}/tty); s=$(cat {_dir}/pid); pgrep -t \"$t\" | grep -vx \"$s\" | tr '\\n' ' '";
        var r = await ExecAsync($"for p in $({list}); do kill -9 $p 2>/dev/null; done; sleep 0.3; {list}", null, TimeSpan.FromSeconds(15), ct).ConfigureAwait(false);
        if (r.Output.Trim() is { Length: > 0 } left)
            await SudoAsync($"kill -9 {left}", null, TimeSpan.FromSeconds(15), ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Whether the command's processes (everything under the terminal's bash, also what sudo runs
    /// on a terminal of its own) used no CPU time for a second, and none started or ended.
    /// </summary>
    private async Task<bool> IsIdleAsync(CancellationToken ct)
    {
        // CPU ticks (also of finished children), count and pid sum of the descendants of the terminal's bash.
        const string snapshot = "cat /proc/[0-9]*/stat 2>/dev/null | awk -v s=\"$s\" '{ p = $1; sub(/^.*\\) /, \"\"); pp[p] = $2; t[p] = $12 + $13 + $14 + $15 } " +
                                "END { d[s] = 1; do { n = 0; for (p in pp) if (!(p in d) && (pp[p] in d)) { d[p] = 1; n++ } } while (n); " +
                                "for (p in d) if (p != s) { c += t[p]; k++; q += p } printf \"%.0f %d %.0f\\n\", c, k, q }'";
        try
        {
            var r = await ExecAsync($"s=$(cat {_dir}/pid); a=$({snapshot}); sleep 1; b=$({snapshot}); [ \"$a\" = \"$b\" ]", null, TimeSpan.FromSeconds(15), ct).ConfigureAwait(false);
            return r.ExitCode == 0;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return true; // as before: the question counts
        }
    }

    // ---- side channels -------------------------------------------------------------------

    /// <summary>Runs a command outside the terminal; <paramref name="stdin"/> is sent to it, then end of input.</summary>
    public async Task<ExecResult> ExecAsync(string command, string? stdin, TimeSpan timeout, CancellationToken ct)
    {
        var (code, output, error) = await RunExecAsync(command, stdin is null ? null : Encoding.UTF8.GetBytes(stdin), timeout, ct).ConfigureAwait(false);
        return new(code, Encoding.UTF8.GetString(output), error);
    }

    /// <summary>A command's output as bytes (an archive, say); with <paramref name="sudo"/> run as root. Throws when it fails.</summary>
    public async Task<byte[]> ExecBytesAsync(string command, bool sudo, TimeSpan timeout, CancellationToken ct)
    {
        var (text, prefix) = await SudoWrapAsync(command, sudo, ct).ConfigureAwait(false);
        var (code, output, error) = await RunExecAsync(text, prefix, timeout, ct).ConfigureAwait(false);
        if (code != 0)
            throw new IOException($"{command} failed on {Target}: {error.Trim()}");
        return output;
    }

    /// <summary>A command that reads bytes (an archive, say) from its input; with <paramref name="sudo"/> run as root.</summary>
    public async Task<ExecResult> ExecBytesInAsync(string command, byte[] stdin, bool sudo, TimeSpan timeout, CancellationToken ct)
    {
        var (text, prefix) = await SudoWrapAsync(command, sudo, ct).ConfigureAwait(false);
        var input = prefix is null ? stdin : [.. prefix, .. stdin];
        var (code, output, error) = await RunExecAsync(text, input, timeout, ct).ConfigureAwait(false);
        return new(code, Encoding.UTF8.GetString(output), error);
    }

    /// <summary>The command wrapped in sudo when asked, and the password line sudo -S reads first, if one is needed.</summary>
    private async Task<(string Command, byte[]? Prefix)> SudoWrapAsync(string command, bool sudo, CancellationToken ct)
    {
        if (!sudo)
            return (command, null);
        if (await CheckSudoAsync(ct).ConfigureAwait(false) && Password is { Length: > 0 } pw)
            return ($"sudo -S -p '' sh -c {Quote(command)}", Encoding.UTF8.GetBytes(pw + "\n"));
        return ($"sudo -n sh -c {Quote(command)}", null);
    }

    private async Task<(int Code, byte[] Output, string Error)> RunExecAsync(string command, byte[]? stdin, TimeSpan timeout, CancellationToken ct)
    {
        using var cmd = _client.CreateCommand(command);
        cmd.CommandTimeout = timeout;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout + TimeSpan.FromSeconds(5));
        var run = cmd.ExecuteAsync(cts.Token);
        using (var input = cmd.CreateInputStream())
        {
            if (stdin is { Length: > 0 })
                await input.WriteAsync(stdin, cts.Token).ConfigureAwait(false);
        }
        await run.ConfigureAwait(false);
        // As SshCommand.Result does, but as bytes: the stream ends with the command.
        using var output = new MemoryStream();
        await cmd.OutputStream.CopyToAsync(output, cts.Token).ConfigureAwait(false);
        return (cmd.ExitStatus ?? -1, output.ToArray(), cmd.Error);
    }

    /// <summary>Whether sudo asks this user for a password; null until <see cref="CheckSudoAsync"/> ran.</summary>
    public bool? SudoNeedsPassword { get; private set; }

    public async Task<bool> CheckSudoAsync(CancellationToken ct)
    {
        SudoNeedsPassword ??= (await ExecAsync("sudo -n true", null, TimeSpan.FromSeconds(15), ct).ConfigureAwait(false)).ExitCode != 0;
        return SudoNeedsPassword.Value;
    }

    /// <summary>
    /// Runs a command as root with sudo, on a side channel: with the user's password when sudo
    /// wants one and there is one (sudo reads it from the first line of the input, the command
    /// gets the rest), else only if sudo needs none.
    /// </summary>
    public async Task<ExecResult> SudoAsync(string command, string? stdin, TimeSpan timeout, CancellationToken ct) =>
        await ExecBytesInAsync(command, Encoding.UTF8.GetBytes(stdin ?? ""), sudo: true, timeout, ct).ConfigureAwait(false);

    private async Task WriteRemoteAsync(string path, string content, CancellationToken ct)
    {
        // The folder is made again if a command cleaned out /tmp.
        var r = await ExecAsync($"mkdir -p -m 700 \"$(dirname {Quote(path)})\" && cat > {Quote(path)}", content, TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
        if (r.ExitCode != 0)
            throw new IOException($"Could not write {path} on {Target}: {r.Error.Trim()}");
    }

    /// <summary>A string as one single-quoted word for sh.</summary>
    public static string Quote(string s) => "'" + s.Replace("'", "'\\''") + "'";

    public void Dispose()
    {
        if (_disposed) return;
        try
        {
            // The private folder goes with the terminal; a server that doesn't answer gets 5 seconds.
            if (_client.IsConnected && _dir.Length > 0)
            {
                using var rm = _client.CreateCommand($"rm -rf {Quote(_dir)}");
                rm.CommandTimeout = TimeSpan.FromSeconds(5);
                rm.Execute();
            }
        }
        catch (Exception) { }
        _disposed = true;
        _shell?.Dispose();
        _client.Dispose();
        _signal.Release();
    }

    [GeneratedRegex(@"\x1eBB-(\d+)-(-?\d+)-([^\x1e]*)\x1e\r?\n?")]
    private static partial Regex Marker();

    [GeneratedRegex(@"\x1eBB-INIT-(bash)?-([^\x1e]+)\x1e")]
    private static partial Regex InitMarker();
}
