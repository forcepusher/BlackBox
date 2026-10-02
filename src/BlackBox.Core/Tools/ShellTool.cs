using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;

namespace BlackBox.Core.Tools;

public sealed class ShellTool : Tool
{
    private const int MaxOutput = 30_000;

    public override string Name => "run_shell";
    public override string Description =>
        "Run a shell command and return its output (PowerShell on Windows, sh elsewhere). " +
        "Use for files, launching apps (e.g. Start-Process notepad), system info, scripts. No interactive commands.";
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
        void Append(string? line)
        {
            if (line is null) return;
            lock (gate)
                if (output.Length < MaxOutput) output.AppendLine(line);
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

        string Snapshot() { lock (gate) return output.ToString(); }
    }
}
