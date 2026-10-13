using System.Text.Json.Nodes;
using BlackBox.Core.Settings;
using BlackBox.Core.Ssh;
using BlackBox.Core.Tools;
using Xunit;

namespace BlackBox.Core.Tests;

/// <summary>
/// The ssh tool and the file tools' host argument against a real server, such as the lab VM of
/// scripts/sysadmin-vm.ps1. Opt-in:
/// <code>
/// $env:BB_SSH_TEST_HOST = "admin@127.0.0.2"; $env:BB_SSH_TEST_KEY = "E:\Repos\BlackBox\.dev\sysadmin-lab\key\admin_ed25519"
/// dotnet test --filter SshIntegrationTests
/// </code>
/// The server's user needs sudo without a password. The tests write only under /tmp and /etc/blackbox-test.
/// </summary>
public class SshIntegrationTests : IDisposable
{
    private static readonly string? Host = Environment.GetEnvironmentVariable("BB_SSH_TEST_HOST");
    private static readonly string? Key = Environment.GetEnvironmentVariable("BB_SSH_TEST_KEY");
    private readonly string _knownHosts = Path.Combine(Path.GetTempPath(), $"bb-known-{Guid.NewGuid():N}");
    private readonly SshHosts _hosts;
    private readonly ToolContext _ctx;

    public SshIntegrationTests()
    {
        _hosts = new SshHosts(_knownHosts);
        _ctx = new ToolContext { Platform = new FakePlatform(), Settings = new AgentSettings(), WorkingDirectory = Path.GetTempPath(), Ssh = _hosts };
    }

    public void Dispose()
    {
        _hosts.Dispose();
        File.Delete(_knownHosts);
    }

    private Task<ToolResult> Ssh(string? command = null, string? input = null, bool interrupt = false, int wait = 30)
    {
        var args = new JsonObject { ["host"] = Host, ["key_file"] = Key, ["wait_seconds"] = wait };
        if (command is not null) args["command"] = command;
        if (input is not null) args["input"] = input;
        if (interrupt) args["interrupt"] = true;
        return new SshTool().ExecuteAsync(args, _ctx, CancellationToken.None);
    }

    [Fact]
    public async Task The_terminal_keeps_its_state_and_survives_exit_and_set_e()
    {
        if (Host is null) return;
        var first = await Ssh("cd /etc && export BB_X=42 && greet() { echo hi-$1; }");
        Assert.Contains("Connected to", first.Text);
        Assert.Contains("[exit code 0 · /etc]", first.Text);
        var again = await Ssh("pwd; echo $BB_X; greet there");
        Assert.Contains("/etc\n42\nhi-there", again.Text);

        // set -e and exit end only the command, not the terminal.
        var failed = await Ssh("set -euo pipefail\nfalse\necho not reached");
        Assert.DoesNotContain("not reached", failed.Text);
        Assert.True(failed.IsError);
        Assert.Contains("[exit code 1 · /etc]", failed.Text);
        var exited = await Ssh("cd /tmp; exit 7");
        Assert.Contains("[exit code 7 · /tmp]", exited.Text);
        var still = await Ssh("echo alive $BB_X; false | true; echo $?");
        Assert.Contains("alive 42\n0", still.Text);

        // Quotes, dollars and heredocs arrive as written.
        var heredoc = await Ssh("cat <<'EOF'\nlocation / { try_files $uri $uri/ =404; }\nit's \"quoted\"\nEOF");
        Assert.Contains("location / { try_files $uri $uri/ =404; }\nit's \"quoted\"", heredoc.Text);
    }

    [Fact]
    public async Task Prompts_long_commands_and_interrupts()
    {
        if (Host is null) return;
        var asked = await Ssh("read -p 'Continue? [y/N] ' a; echo answer=$a");
        Assert.Contains("waits for input", asked.Text);
        Assert.Contains("Continue? [y/N]", asked.Text);
        var answered = await Ssh(input: "y");
        Assert.Contains("answer=y", answered.Text);
        Assert.Contains("[exit code 0", answered.Text);

        var slow = await Ssh("for i in 1 2 3 4 5 6; do echo tick $i; sleep 1; done", wait: 2);
        Assert.Contains("still running", slow.Text);
        var busy = await Ssh("echo next");
        Assert.True(busy.IsError);
        Assert.Contains("still running", busy.Text);
        var rest = await Ssh(wait: 20);
        Assert.Contains("tick 6", rest.Text);
        Assert.Contains("[exit code 0", rest.Text);

        await Ssh("sleep 300", wait: 2);
        var stopped = await Ssh(interrupt: true);
        Assert.Contains("[exit code 130", stopped.Text);
        Assert.Contains("[exit code 0", (await Ssh("true")).Text);

        // A pager or editor that slips through is stopped instead of hanging the terminal.
        var pager = await Ssh("seq 1 500 | less", wait: 20);
        Assert.Contains("full-screen", pager.Text);
        Assert.Contains("[exit code 0", (await Ssh("echo after")).Text);
    }

    [Fact]
    public async Task File_tools_work_on_the_host_with_sudo_where_needed()
    {
        if (Host is null) return;
        var dir = "/etc/blackbox-test";
        await Ssh($"sudo rm -rf {dir}");
        var path = dir + "/site.conf";
        var conf = "server {\n    listen 80;\n    root /var/www/html;\n    location / { try_files $uri $uri/ =404; }\n}\n";
        var write = await new WriteFileTool().ExecuteAsync(new JsonObject { ["path"] = path, ["content"] = conf, ["host"] = Host }, _ctx, CancellationToken.None);
        Assert.False(write.IsError, write.Text);
        Assert.Contains("with sudo", write.Text);
        Assert.Contains("root root 644", (await Ssh($"stat -c '%U %G %a' {path}")).Text);

        var edit = await new EditFileTool().ExecuteAsync(new JsonObject { ["path"] = path, ["old_text"] = "listen 80;", ["new_text"] = "listen 8080;", ["host"] = Host }, _ctx, CancellationToken.None);
        Assert.False(edit.IsError, edit.Text);
        var read = await new ReadFileTool().ExecuteAsync(new JsonObject { ["path"] = path, ["host"] = Host }, _ctx, CancellationToken.None);
        Assert.Contains("2\t    listen 8080;", read.Text);
        Assert.Contains("4\t    location / { try_files $uri $uri/ =404; }", read.Text);

        var list = await new ListDirectoryTool().ExecuteAsync(new JsonObject { ["path"] = dir, ["host"] = Host }, _ctx, CancellationToken.None);
        Assert.Contains("site.conf", list.Text);
        var shadow = await new ReadFileTool().ExecuteAsync(new JsonObject { ["path"] = "/etc/shadow", ["host"] = Host, ["limit"] = 1 }, _ctx, CancellationToken.None);
        Assert.Contains("read with sudo", shadow.Text);
        var missing = await new ReadFileTool().ExecuteAsync(new JsonObject { ["path"] = dir + "/nope", ["host"] = Host }, _ctx, CancellationToken.None);
        Assert.True(missing.IsError);
        await Ssh($"sudo rm -rf {dir}");
    }
}
