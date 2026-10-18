using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;

namespace BlackBox.Core.Tools;

public sealed partial class ShellTool : Tool
{
    private const int MaxOutput = 30_000;

    public override string Name => "run_shell";
    public override string Description =>
        // Five of 45 ComfyUI test runs lost a step to "cd … && python …": the token && doesn't exist there.
        "Run a shell command and return its output (Windows PowerShell 5.1 on Windows, where && and || don't exist, so chain commands with a semicolon; sh elsewhere). " +
        "Use for files, launching apps (e.g. Start-Process notepad), system info, scripts. No interactive commands. " +
        // Twice a reminder started as Start-Process powershell -ArgumentList '-Command','...("text")'
        // died at once: Windows PowerShell drops the quotes inside the arguments.
        "To start something that keeps running after the command (a second PowerShell), write its commands to a .ps1 file and start that with -File: " +
        "Windows PowerShell drops the quotes inside -ArgumentList. " +
        "Don't install software on this PC with it (pip, npm, winget) unless the task needs it here.";
    public override ToolRisk Risk => ToolRisk.Risky;
    public override JsonObject Parameters => Schema(
        ("command", Str("Command to run"), true),
        ("timeout_seconds", Int("Kill the command after this many seconds (default 60, max 600)"), false),
        ("working_directory", Str("Directory to run in (default: working directory)"), false));
    public override string Describe(JsonObject args) => $"Run: {GetString(args, "command")}";

    public override async Task<ToolResult> ExecuteAsync(JsonObject args, ToolContext ctx, CancellationToken ct)
    {
        var command = RequireString(args, "command");
        var timeout = TimeSpan.FromSeconds(Math.Clamp(GetNumber(args, "timeout_seconds") ?? 60, 1, 600));
        var cwd = GetString(args, "working_directory") is { Length: > 0 } wd ? ctx.ResolvePath(wd) : ctx.WorkingDirectory;
        if (!Directory.Exists(cwd))
            return ToolResult.Error($"Working directory not found: {cwd}");

        var psi = ctx.Platform.CreateShellStartInfo(command);
        psi.WorkingDirectory = cwd;
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        psi.RedirectStandardInput = true;
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;
        psi.StandardOutputEncoding = Encoding.UTF8;
        psi.StandardErrorEncoding = Encoding.UTF8;

        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var output = new OutputCapture(MaxOutput);
        var gate = new object();
        long binary = 0;
        var stdoutClosed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stderrClosed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Append(string? line)
        {
            if (line is null || line == ClixmlMarker) return;
            line = DecodeClixml(line);
            lock (gate)
            {
                // An image or an archive printed as text says nothing and would fill the context.
                if (Ssh.TerminalText.LooksBinary(line, afterBinary: binary > 0, needControl: binary == 0))
                {
                    binary += line.Length + 1;
                    return;
                }
                FlushBinary();
                output.Add(line);
            }
        }
        void FlushBinary()
        {
            if (binary > 0) output.Add(Ssh.TerminalText.BinaryNote(binary));
            binary = 0;
        }
        process.OutputDataReceived += (_, e) => { if (e.Data is null) stdoutClosed.TrySetResult(); else Append(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is null) stderrClosed.TrySetResult(); else Append(e.Data); };
        process.Exited += (_, _) => exited.TrySetResult();

        process.Start();
        process.StandardInput.Close();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);
        try
        {
            // Until the shell has exited, not until its output pipes close (WaitForExitAsync waits
            // for both): a program the command left running, as Start-Process -NoNewWindow does,
            // holds them open, and a command that was done waited out its timeout, was reported as
            // timed out, and then had what it started killed with it.
            await exited.Task.WaitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); }
            catch (Exception ex) when (ex is InvalidOperationException or AggregateException or System.ComponentModel.Win32Exception) { }
            ct.ThrowIfCancellationRequested();
            return ToolResult.Error($"Timed out after {timeout.TotalSeconds:0}s.\n{Snapshot()}");
        }

        // The rest of what it printed; a program it left running keeps the pipes, so not for long.
        await Task.WhenAny(Task.WhenAll(stdoutClosed.Task, stderrClosed.Task), Task.Delay(TimeSpan.FromSeconds(2), ct)).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        return new ToolResult($"Exit code {process.ExitCode}\n{Snapshot()}", IsError: process.ExitCode != 0);

        string Snapshot()
        {
            lock (gate)
            {
                FlushBinary();
                return output.ToString();
            }
        }
    }

    /// <summary>
    /// A command's output as the model gets it: the beginning and the end of a long one (errors and
    /// summaries come last; only the beginning was kept), with what was left out between them
    /// counted, and very long lines cut in the middle (a minified file or a base64 dump came back
    /// whole and filled the context). Holds no more than that, however much the command prints.
    /// </summary>
    internal sealed class OutputCapture(int max)
    {
        private const int MaxLine = 4_000;
        private readonly int _headMax = max / 4, _tailMax = max - max / 4;
        private readonly StringBuilder _head = new();
        private readonly Queue<string> _tail = new();
        private long _tailChars, _leftOutLines, _leftOutChars;

        public void Add(string line)
        {
            if (line.Length > MaxLine)
                line = line[..(MaxLine / 2)] + $" [... {line.Length - MaxLine:N0} characters of this line left out ...] " + line[^(MaxLine / 2)..];
            if (_tail.Count == 0 && _head.Length + line.Length + 1 <= _headMax)
            {
                _head.Append(line).Append('\n');
                return;
            }
            _tail.Enqueue(line);
            _tailChars += line.Length + 1;
            while (_tailChars > _tailMax && _tail.Count > 1)
            {
                var gone = _tail.Dequeue();
                _tailChars -= gone.Length + 1;
                _leftOutLines++;
                _leftOutChars += gone.Length + 1;
            }
        }

        public override string ToString()
        {
            var sb = new StringBuilder(_head.ToString());
            if (_leftOutLines > 0)
                sb.Append($"[... {_leftOutLines:N0} lines ({_leftOutChars:N0} characters) left out ...]\n");
            foreach (var line in _tail)
                sb.Append(line).Append('\n');
            return sb.ToString();
        }
    }

    private const string ClixmlMarker = "#< CLIXML";

    /// <summary>
    /// Windows PowerShell writes its error stream as CLIXML when stderr is redirected ("#&lt; CLIXML"
    /// and then &lt;Objs&gt;&lt;S S="Error"&gt;Add-Type : Cannot add type..._x000D__x000A_&lt;/S&gt;...), which
    /// buries the message in markup. Turns such a line back into the plain text PowerShell would
    /// have shown; any other line is returned as it is.
    /// </summary>
    internal static string DecodeClixml(string line)
    {
        if (!line.StartsWith("<Objs ", StringComparison.Ordinal))
            return line;
        var text = new StringBuilder();
        foreach (System.Text.RegularExpressions.Match m in ClixmlString().Matches(line))
            text.Append(System.Net.WebUtility.HtmlDecode(ClixmlEscape().Replace(m.Groups[1].Value,
                e => ((char)Convert.ToInt32(e.Groups[1].Value, 16)).ToString())));
        return text.ToString().TrimEnd('\r', '\n').Replace("\r\n", "\n");
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"<S S=""(?:Error|Warning|Verbose|Debug|Information)"">(.*?)</S>")]
    private static partial System.Text.RegularExpressions.Regex ClixmlString();

    [System.Text.RegularExpressions.GeneratedRegex(@"_x([0-9A-Fa-f]{4})_")]
    private static partial System.Text.RegularExpressions.Regex ClixmlEscape();
}
