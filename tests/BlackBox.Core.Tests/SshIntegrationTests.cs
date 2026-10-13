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

        // A question answered through a pipe, then quiet work (echo y | ufw enable): not a wait for input.
        var piped = await Ssh("echo y | { printf 'Proceed with operation (y|n)? '; read a; end=$((SECONDS + 6)); while [ $SECONDS -lt $end ]; do :; done; echo; echo got=$a; }");
        Assert.DoesNotContain("waits for input", piped.Text);
        Assert.Contains("got=y", piped.Text);
        Assert.Contains("[exit code 0", piped.Text);

        // An answer that comes after the question went away isn't typed: bash would run it.
        var timed = await Ssh("read -t 8 -p 'Name? ' a; echo; echo timed-out");
        Assert.Contains("waits for input", timed.Text);
        await Task.Delay(TimeSpan.FromSeconds(8));
        var late = await Ssh(input: "late-answer");
        Assert.Contains("nothing was typed", late.Text);
        Assert.Contains("timed-out", late.Text);
        Assert.DoesNotContain("late-answer", (await Ssh("echo clean")).Text);

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
    public async Task Copy_moves_folders_both_ways_byte_for_byte_with_sudo_where_needed()
    {
        if (Host is null) return;
        var local = Path.Combine(Path.GetTempPath(), "bb-copy-" + Guid.NewGuid().ToString("N"));
        var site = Path.Combine(local, "site");
        Directory.CreateDirectory(Path.Combine(site, "img"));
        var image = new byte[200_000];
        new Random(5).NextBytes(image);
        await File.WriteAllBytesAsync(Path.Combine(site, "img", "a.png"), image);
        await File.WriteAllTextAsync(Path.Combine(site, "index.html"), "v2\n");
        try
        {
            await Ssh("sudo rm -rf /srv/bb-copy; sudo mkdir -p /srv/bb-copy");
            var up = await new CopyTool().ExecuteAsync(new() { ["from"] = site, ["to"] = $"{Host}:/srv/bb-copy" }, _ctx, CancellationToken.None);
            Assert.False(up.IsError, up.Text);
            Assert.Contains("with sudo", up.Text); // /srv/bb-copy belongs to root
            var sum = await Ssh("sha256sum /srv/bb-copy/site/img/a.png; stat -c '%U %a' /srv/bb-copy/site/index.html");
            Assert.Contains(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(image)).ToLowerInvariant(), sum.Text);

            var back = Path.Combine(local, "back");
            var down = await new CopyTool().ExecuteAsync(new() { ["from"] = $"{Host}:/srv/bb-copy/site", ["to"] = back }, _ctx, CancellationToken.None);
            Assert.False(down.IsError, down.Text);
            Assert.Equal(image, await File.ReadAllBytesAsync(Path.Combine(back, "img", "a.png")));

            // A root-only file comes down with sudo.
            var shadow = await new CopyTool().ExecuteAsync(new() { ["from"] = $"{Host}:/etc/shadow", ["to"] = Path.Combine(local, "shadow") }, _ctx, CancellationToken.None);
            Assert.False(shadow.IsError, shadow.Text);
            Assert.Contains("root:", await File.ReadAllTextAsync(Path.Combine(local, "shadow")));
        }
        finally
        {
            Directory.Delete(local, recursive: true);
            await Ssh("sudo rm -rf /srv/bb-copy");
        }
    }

    [Fact]
    public async Task A_key_the_connection_did_not_use_is_tested_with_a_fresh_login()
    {
        if (Host is null) return;
        var local = Path.Combine(Path.GetTempPath(), "bb-key-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(local);
        Task<ToolResult> SshWithKey(string name, string command) => new SshTool().ExecuteAsync(
            new JsonObject { ["host"] = Host, ["key_file"] = Path.Combine(local, name), ["command"] = command }, _ctx, CancellationToken.None);
        try
        {
            await Ssh("cd /tmp; rm -f bb-k bb-k.pub bb-j bb-j.pub; ssh-keygen -q -t ed25519 -N '' -f bb-k -C bb-test-key && " +
                      "ssh-keygen -q -t ed25519 -N '' -f bb-j -C bb-other-key && cat bb-k.pub >> ~/.ssh/authorized_keys");
            foreach (var name in new[] { "bb-k", "bb-j" })
            {
                var down = await new CopyTool().ExecuteAsync(new() { ["from"] = $"{Host}:/tmp/{name}", ["to"] = Path.Combine(local, name) }, _ctx, CancellationToken.None);
                Assert.False(down.IsError, down.Text);
            }

            // A key the server doesn't take is refused, and the first connection goes on (still in /tmp).
            var refused = await SshWithKey("bb-j", "pwd");
            Assert.True(refused.IsError);
            Assert.Contains("still works", refused.Text);
            Assert.Contains("[exit code 0 · /tmp]", (await Ssh("pwd")).Text);

            // The installed key: a new login with it alone, in a new terminal; then that one is used.
            var fresh = await SshWithKey("bb-k", "pwd");
            Assert.Contains("Logged in again", fresh.Text);
            Assert.Contains("[exit code 0 · /home/", fresh.Text);
            Assert.DoesNotContain("Logged in again", (await SshWithKey("bb-k", "true")).Text);
        }
        finally
        {
            await Ssh("sed -i '/bb-test-key/d' ~/.ssh/authorized_keys; rm -f /tmp/bb-k /tmp/bb-k.pub /tmp/bb-j /tmp/bb-j.pub");
            Directory.Delete(local, recursive: true);
        }
    }

    [Fact]
    public async Task After_a_reboot_the_next_call_logs_in_again()
    {
        if (Host is null || Environment.GetEnvironmentVariable("BB_SSH_TEST_REBOOT") != "1") return;
        await Ssh("cd /tmp");
        await Ssh("sudo systemctl reboot", wait: 20); // returns at once, or as the connection drops
        await Task.Delay(TimeSpan.FromSeconds(10));
        SshHosts.RetryFor = TimeSpan.FromMinutes(4); // software-emulated VMs boot slowly
        var back = await Ssh("pwd; uptime", wait: 30);
        Assert.Contains("The earlier connection was lost", back.Text);
        Assert.Contains("/home/", back.Text);
        Assert.Contains("[exit code 0", back.Text);
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
