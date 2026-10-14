using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;

namespace BlackBox.Core.Tools;

public sealed partial class ShellTool : Tool
{
    private const int MaxOutput = 30_000;

    public override string Name => "run_shell";
    public override string Description =>
        "Run a shell command and return its output (PowerShell on Windows, sh elsewhere). " +
        "Use for files, launching apps (e.g. Start-Process notepad), system info, scripts. No interactive commands. " +
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

        using var process = new Process { StartInfo = psi };
        var output = new StringBuilder();
        var gate = new object();
        long binary = 0;
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
                if (output.Length < MaxOutput) output.AppendLine(line);
            }
        }
        void FlushBinary()
        {
            if (binary > 0 && output.Length < MaxOutput) output.AppendLine(Ssh.TerminalText.BinaryNote(binary));
            binary = 0;
        }
        process.OutputDataReceived += (_, e) => Append(e.Data);
        process.ErrorDataReceived += (_, e) => Append(e.Data);

        process.Start();
        process.StandardInput.Close();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            ct.ThrowIfCancellationRequested();
            return ToolResult.Error($"Timed out after {timeout.TotalSeconds:0}s.\n{Snapshot()}");
        }

        // Let the async readers drain.
        process.WaitForExit();
        var text = Snapshot();
        if (text.Length >= MaxOutput) text += "\n[output truncated]";
        return new ToolResult($"Exit code {process.ExitCode}\n{text}", IsError: process.ExitCode != 0);

        string Snapshot()
        {
            lock (gate)
            {
                FlushBinary();
                return output.ToString();
            }
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
