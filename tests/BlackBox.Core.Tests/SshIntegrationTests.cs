using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
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
        Assert.Matches(@"this PC connects from [0-9a-f.:]+;", first.Text);
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

        // Admin tools in the sbin folders are found without sudo (Debian leaves those off a user's PATH).
        var sbin = await Ssh("command -v sysctl swapon");
        Assert.Contains("sbin/sysctl\n", sbin.Text);
        Assert.Contains("sbin/swapon\n", sbin.Text);
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
        await Task.Delay(TimeSpan.FromSeconds(8), TestContext.Current.CancellationToken);
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
        await File.WriteAllBytesAsync(Path.Combine(site, "img", "a.png"), image, TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(site, "index.html"), "v2\n", TestContext.Current.CancellationToken);
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
            Assert.Equal(image, await File.ReadAllBytesAsync(Path.Combine(back, "img", "a.png"), TestContext.Current.CancellationToken));

            // A root-only file comes down with sudo.
            var shadow = await new CopyTool().ExecuteAsync(new() { ["from"] = $"{Host}:/etc/shadow", ["to"] = Path.Combine(local, "shadow") }, _ctx, CancellationToken.None);
            Assert.False(shadow.IsError, shadow.Text);
            Assert.Contains("root:", await File.ReadAllTextAsync(Path.Combine(local, "shadow"), TestContext.Current.CancellationToken));
        }
        finally
        {
            Directory.Delete(local, recursive: true);
            await Ssh("sudo rm -rf /srv/bb-copy");
        }
    }

    [Fact]
    public async Task A_copy_of_several_GB_streams_in_every_direction_in_little_memory()
    {
        // Opt-in: the file goes three ways (minutes on the lab VM) and needs that much free disk on
        // this PC and on the server: $env:BB_SSH_TEST_BIG_GB = "3". The copy between servers goes to
        // a login of its own on the server of $env:BB_SSH_TEST_HOST2 (same key, sudo without a
        // password) if set, else on the same server.
        if (Host is null || !double.TryParse(Environment.GetEnvironmentVariable("BB_SSH_TEST_BIG_GB"), NumberStyles.Float, CultureInfo.InvariantCulture, out var gb))
            return;
        var admin = Environment.GetEnvironmentVariable("BB_SSH_TEST_HOST2") ?? Host;
        var target = $"bbbig@{admin[(admin.IndexOf('@') + 1)..]}";
        Task<ToolResult> On(string host, string command, int wait = 30) => new SshTool().ExecuteAsync(
            new JsonObject { ["host"] = host, ["key_file"] = Key, ["command"] = command, ["wait_seconds"] = wait }, _ctx, CancellationToken.None);
        // A CPU quota on the target login's slice holds back its sshd and tar, as a slower server
        // would; set from the admin login, which the quota leaves alone.
        Task<ToolResult> Quota(string value) => On(admin, $"sudo systemctl set-property --runtime user-$(id -u bbbig).slice CPUQuota={value}");
        const string dir = "/var/tmp/bb-big", to = "/var/tmp/bb-big-to"; // not /tmp: a tmpfs on Debian 13, half the RAM
        var local = Path.Combine(Path.GetTempPath(), "bb-big-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(local);
        try
        {
            // A dump of random bytes, which nothing on the way can make smaller.
            var dump = Path.Combine(local, "dump.bin");
            var hash = WriteRandomFile(dump, (long)(gb * (1L << 30)));
            await Ssh($"rm -rf {dir} && mkdir {dir}");

            var up = await Measured("This PC to the server", () => Copy(dump, $"{Host}:{dir}/"));
            Assert.Contains(hash, (await Ssh($"sha256sum {dir}/dump.bin", wait: 1800)).Text);
            Assert.InRange(up.Memory, 0, MemoryBound);

            var back = Path.Combine(local, "back.bin");
            var down = await Measured("The server to this PC", () => Copy($"{Host}:{dir}/dump.bin", back));
            Assert.Equal(hash, HashOf(back));
            Assert.InRange(down.Memory, 0, MemoryBound);
            File.Delete(back);

            // Server to server through this PC, the target held back for the first half minute. The
            // source can't be slowed down, so what the target can't take yet waits on disk here rather
            // than in memory, in files deleted as they are read: the disk holds about what waits.
            await On(admin, "sudo useradd -m -s /bin/bash bbbig 2>/dev/null; sudo install -d -m 700 -o bbbig -g bbbig ~bbbig/.ssh && " +
                            "sudo install -m 600 -o bbbig -g bbbig ~/.ssh/authorized_keys ~bbbig/.ssh/authorized_keys");
            Assert.Contains("[exit code 0", (await On(target, $"rm -rf {to} && mkdir {to}")).Text); // logged in: its slice exists
            await Quota("5%");
            var release = Task.Delay(TimeSpan.FromSeconds(30), CancellationToken.None).ContinueWith(_ => Quota(""), TaskScheduler.Default).Unwrap();
            (ToolResult Result, long Memory, long OnDisk) across;
            try
            {
                across = await Measured("Server to server", () => Copy($"{Host}:{dir}/dump.bin", $"{target}:{to}/"));
            }
            finally
            {
                await release;
            }
            Assert.Contains(hash, (await On(target, $"sha256sum {to}/dump.bin", wait: 1800)).Text);
            Assert.InRange(across.Memory, 0, MemoryBound);
            Assert.InRange(CopyTool.LastSpilledPeak, 4 * CopyTool.MemoryLimit, long.MaxValue);
            Assert.InRange(across.OnDisk, 1, CopyTool.LastSpilledPeak + 2 * CopyPipe.DefaultSegmentSize);
            var settled = DateTime.UtcNow.AddSeconds(3); // a closed file lingers while a virus scanner has it open
            while (SpillFiles().Length > 0 && DateTime.UtcNow < settled)
                await Task.Delay(50, CancellationToken.None);
            Assert.Empty(SpillFiles());
        }
        finally
        {
            Directory.Delete(local, recursive: true);
            await Ssh($"rm -rf {dir}");
            await On(admin, $"sudo systemctl set-property --runtime user-$(id -u bbbig).slice CPUQuota=; sudo pkill -u bbbig; sudo rm -rf {to}; sudo userdel -r bbbig 2>/dev/null; true");
        }
    }

    /// <summary>The most a big copy may add to this process's memory: a few times the pipe, nothing like the file.</summary>
    private const long MemoryBound = 256L << 20;

    private Task<ToolResult> Copy(string from, string to) =>
        new CopyTool().ExecuteAsync(new() { ["from"] = from, ["to"] = to }, _ctx, CancellationToken.None);

    private static string[] SpillFiles() => Directory.GetFiles(Path.GetTempPath(), "blackbox-copy-*.tmp");

    /// <summary>
    /// Runs a copy while watching how far this process's private memory rises above where it
    /// started, and how much its temporary files hold on disk; both go to the test output.
    /// </summary>
    private static async Task<(ToolResult Result, long Memory, long OnDisk)> Measured(string what, Func<Task<ToolResult>> copy)
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        using var me = Process.GetCurrentProcess();
        var start = me.PrivateMemorySize64;
        long peak = start, onDisk = 0;
        using var done = new CancellationTokenSource();
        var watch = Task.Run(async () =>
        {
            while (!done.IsCancellationRequested)
            {
                me.Refresh();
                peak = Math.Max(peak, me.PrivateMemorySize64);
                var bytes = 0L;
                foreach (var file in SpillFiles())
                    try { bytes += new FileInfo(file).Length; } catch (IOException) { }
                onDisk = Math.Max(onDisk, bytes);
                try { await Task.Delay(100, done.Token); } catch (OperationCanceledException) { }
            }
        });
        var clock = Stopwatch.StartNew();
        var result = await copy();
        done.Cancel();
        await watch;
        TestContext.Current.TestOutputHelper?.WriteLine($"{what}: {result.Text} ({clock.Elapsed:m\\:ss}) Memory rose by {(peak - start) / 1048576.0:0} MB. " +
                                                        $"Waiting on disk: up to {CopyTool.LastSpilledPeak / 1048576.0:0} MB, in files holding up to {onDisk / 1048576.0:0} MB.");
        Assert.False(result.IsError, result.Text);
        return (result, peak - start, onDisk);
    }

    private static string WriteRandomFile(string path, long size)
    {
        using var output = File.Create(path);
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var block = new byte[1 << 20];
        for (var left = size; left > 0; left -= block.Length)
        {
            var n = (int)Math.Min(left, block.Length);
            RandomNumberGenerator.Fill(block.AsSpan(0, n));
            output.Write(block, 0, n);
            sha.AppendData(block, 0, n);
        }
        return Convert.ToHexStringLower(sha.GetHashAndReset());
    }

    private static string HashOf(string path)
    {
        using var input = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(input));
    }

    [Fact]
    public async Task Silence_ends_the_default_wait_early_but_not_a_chosen_one()
    {
        if (Host is null) return;
        var saved = SshHost.QuietReturn;
        SshHost.QuietReturn = TimeSpan.FromSeconds(3);
        try
        {
            // Without wait_seconds, a command that goes quiet (a server in the foreground, say) returns after the silence.
            var quiet = await new SshTool().ExecuteAsync(new JsonObject { ["host"] = Host, ["key_file"] = Key, ["command"] = "echo serving; sleep 8; echo finished" },
                _ctx, CancellationToken.None);
            Assert.Contains("No output for 3 s", quiet.Text);
            Assert.Matches(@"still running after [3-5] s", quiet.Text);
            Assert.Contains("finished", (await Ssh(wait: 20)).Text);
            // With wait_seconds, the model means to sit it out (sleep 60 until cron's next run).
            var chosen = await Ssh("echo waiting; sleep 8; echo finished", wait: 20);
            Assert.Contains("finished\n[exit code 0", chosen.Text);
        }
        finally
        {
            SshHost.QuietReturn = saved;
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
            Assert.Contains("still open", refused.Text);
            Assert.Contains("[exit code 0 · /tmp]", (await Ssh("pwd")).Text);

            // The installed key: a new login with it alone, in a new terminal; then that one is used.
            var fresh = await SshWithKey("bb-k", "pwd");
            Assert.Contains("Logged in afresh", fresh.Text);
            Assert.Contains("[exit code 0 · /home/", fresh.Text);
            Assert.DoesNotContain("Logged in afresh", (await SshWithKey("bb-k", "true")).Text);
        }
        finally
        {
            await Ssh("sed -i '/bb-test-key/d' ~/.ssh/authorized_keys; rm -f /tmp/bb-k /tmp/bb-k.pub /tmp/bb-j /tmp/bb-j.pub");
            Directory.Delete(local, recursive: true);
        }
    }

    [Fact]
    public async Task A_password_alone_logs_in()
    {
        if (Host is null) return;
        var server = Host[(Host.IndexOf('@') + 1)..];
        try
        {
            // A user that may log in with a password (the cloud image allows keys only).
            await Ssh("sudo useradd -m -s /bin/bash bbpw 2>/dev/null; echo 'bbpw:Pw-Test-123' | sudo chpasswd && " +
                      "printf 'Match User bbpw\\n  PasswordAuthentication yes\\n' | sudo tee /etc/ssh/sshd_config.d/10-bbpw.conf >/dev/null && sudo systemctl reload ssh");
            var wrong = await new SshTool().ExecuteAsync(new JsonObject { ["host"] = $"bbpw@{server}", ["password"] = "nope", ["command"] = "whoami" }, _ctx, CancellationToken.None);
            Assert.True(wrong.IsError);
            Assert.Contains("did not accept the login", wrong.Text);
            Assert.Contains("The server offers these login methods: publickey, password", wrong.Text);
            var right = await new SshTool().ExecuteAsync(new JsonObject { ["host"] = $"bbpw@{server}", ["password"] = "Pw-Test-123", ["command"] = "whoami" }, _ctx, CancellationToken.None);
            Assert.Contains("Connected to", right.Text);
            Assert.Contains("bbpw\n[exit code 0", right.Text);

            // Passwords turned off: a new login with the password is refused, the open terminal goes on.
            await Ssh("sudo rm /etc/ssh/sshd_config.d/10-bbpw.conf && sudo systemctl reload ssh");
            var again = await new SshTool().ExecuteAsync(new JsonObject { ["host"] = $"bbpw@{server}", ["password"] = "Pw-Test-123", ["new_login"] = true, ["command"] = "whoami" },
                _ctx, CancellationToken.None);
            Assert.True(again.IsError);
            Assert.Contains("did not accept the login", again.Text);
            Assert.Contains("still open", again.Text);
            Assert.DoesNotContain("methods: publickey, password", again.Text); // password logins are off now
            Assert.Contains("bbpw\n[exit code 0", (await new SshTool().ExecuteAsync(new JsonObject { ["host"] = $"bbpw@{server}", ["command"] = "whoami" }, _ctx, CancellationToken.None)).Text);
        }
        finally
        {
            await Ssh("sudo rm -f /etc/ssh/sshd_config.d/10-bbpw.conf; sudo systemctl reload ssh; sudo pkill -u bbpw; sudo userdel -r bbpw 2>/dev/null; true");
        }
    }

    [Fact]
    public async Task A_user_whose_login_shell_is_sh_gets_bash()
    {
        if (Host is null) return;
        var server = Host[(Host.IndexOf('@') + 1)..];
        try
        {
            await Ssh("sudo useradd -m -s /bin/sh bbsh 2>/dev/null; sudo install -d -m 700 -o bbsh -g bbsh ~bbsh/.ssh && " +
                      "sudo install -m 600 -o bbsh -g bbsh ~/.ssh/authorized_keys ~bbsh/.ssh/authorized_keys");
            Task<ToolResult> AsSh(string command) => new SshTool().ExecuteAsync(
                new JsonObject { ["host"] = $"bbsh@{server}", ["key_file"] = Key, ["command"] = command }, _ctx, CancellationToken.None);
            var first = await AsSh("cd /tmp && export BB_Y=7; echo ${BASH_VERSION:+bash}");
            Assert.Contains("bash\n[exit code 0 · /tmp]", first.Text);
            Assert.Contains("/tmp\n7", (await AsSh("pwd; echo $BB_Y")).Text);
        }
        finally
        {
            await Ssh("sudo pkill -u bbsh; sudo userdel -r bbsh 2>/dev/null; true");
        }
    }

    [Fact]
    public async Task A_login_made_after_the_chat_ended_is_closed_not_kept()
    {
        if (Host is null) return;
        // The app logs out of a chat it leaves while that chat's stopped run may still be logging in.
        var hosts = new SshHosts(_knownHosts);
        var (first, _) = await hosts.GetAsync(Host, Key, null, CancellationToken.None);
        hosts.Dispose();
        Assert.False(first.IsConnected);
        var late = await Assert.ThrowsAsync<IOException>(() => hosts.GetAsync(Host, Key, null, CancellationToken.None));
        Assert.Contains("The chat ended", late.Message);
        Assert.Empty(hosts.Connected);
    }

    [Fact]
    public async Task A_key_named_once_is_offered_to_the_next_server()
    {
        if (Host is null) return;
        var server = Host[(Host.IndexOf('@') + 1)..];
        try
        {
            // Another account that takes the same key, logged in to without naming it, as ssh-agent would.
            await Ssh("sudo useradd -m -s /bin/bash bbagent 2>/dev/null; sudo install -d -m 700 -o bbagent -g bbagent ~bbagent/.ssh && " +
                      "sudo install -m 600 -o bbagent -g bbagent ~/.ssh/authorized_keys ~bbagent/.ssh/authorized_keys");
            var other = await new SshTool().ExecuteAsync(new JsonObject { ["host"] = $"bbagent@{server}", ["command"] = "whoami" }, _ctx, CancellationToken.None);
            Assert.Contains("bbagent\n[exit code 0", other.Text);
            // And copy goes from one login to the other through this PC.
            await Ssh("echo moved > /tmp/bb-agent.txt");
            var copy = await new CopyTool().ExecuteAsync(new() { ["from"] = $"{Host}:/tmp/bb-agent.txt", ["to"] = $"bbagent@{server}:/home/bbagent/" }, _ctx, CancellationToken.None);
            Assert.False(copy.IsError, copy.Text);
            Assert.Contains("moved", (await new SshTool().ExecuteAsync(new JsonObject { ["host"] = $"bbagent@{server}", ["command"] = "cat ~/bb-agent.txt" }, _ctx, CancellationToken.None)).Text);
        }
        finally
        {
            await Ssh("rm -f /tmp/bb-agent.txt; sudo pkill -u bbagent; sudo userdel -r bbagent 2>/dev/null; true");
        }
    }

    [Fact]
    public async Task Without_bash_each_command_runs_on_its_own()
    {
        if (Host is null) return;
        var server = Host[(Host.IndexOf('@') + 1)..];
        try
        {
            // As on a router, a NAS or Alpine: the login shell is sh and no bash is on its PATH.
            await Ssh("sudo useradd -m -s /bin/sh bbnob 2>/dev/null; sudo install -d -m 700 -o bbnob -g bbnob ~bbnob/.ssh ~bbnob/bin && " +
                      "sudo install -m 600 -o bbnob -g bbnob ~/.ssh/authorized_keys ~bbnob/.ssh/authorized_keys && " +
                      "for c in stty mktemp tty sed cat rm whoami ls; do sudo ln -sf \"$(command -v $c)\" ~bbnob/bin/$c; done && " +
                      "echo 'PATH=$HOME/bin; export PATH' | sudo tee ~bbnob/.profile >/dev/null");
            Task<ToolResult> AsNob(string command) => new SshTool().ExecuteAsync(
                new JsonObject { ["host"] = $"bbnob@{server}", ["key_file"] = Key, ["command"] = command }, _ctx, CancellationToken.None);
            var first = await AsNob("cd /tmp; echo ${BASH_VERSION:-nobash}; whoami");
            Assert.Contains("no bash", first.Text);
            Assert.Contains("nobash\nbbnob", first.Text);
            var second = await AsNob("pwd");
            Assert.Contains("/home/bbnob", second.Text); // nothing carries over
            Assert.DoesNotContain("no bash", second.Text); // said once
        }
        finally
        {
            await Ssh("sudo pkill -u bbnob; sudo userdel -r bbnob 2>/dev/null; true");
        }
    }

    [Fact]
    public async Task Config_files_are_checked_right_after_they_are_written()
    {
        if (Host is null) return;
        Task<ToolResult> Write(string path, string content) => new WriteFileTool().ExecuteAsync(
            new JsonObject { ["path"] = path, ["content"] = content, ["host"] = Host }, _ctx, CancellationToken.None);
        try
        {
            // A sudoers rule for a group written without % (seen in a run): visudo passes it, sudo ignores it for the group.
            await Ssh("sudo groupadd -f bbdevs");
            var sudoers = await Write("/etc/sudoers.d/bbdevs", "bbdevs ALL=(ALL) NOPASSWD:ALL\n");
            Assert.Contains("visudo -cf /etc/sudoers.d/bbdevs passed", sudoers.Text);
            Assert.Contains("bbdevs is a group, not a user: a rule for a group needs %bbdevs", sudoers.Text);

            // An sshd drop-in that sorts after the one turning passwords on (seen in a run): sshd -t passes, the setting doesn't win.
            await Ssh("printf 'PasswordAuthentication yes\\n' | sudo tee /etc/ssh/sshd_config.d/50-bbtest.conf >/dev/null");
            var sshd = await Write("/etc/ssh/sshd_config.d/90-bbtest.conf", "PasswordAuthentication no\n");
            Assert.Contains("sshd -t passed", sshd.Text);
            Assert.Contains("sets passwordauthentication no, but sshd uses passwordauthentication yes", sshd.Text);
            // The other way round the stricter value wins: worth a note, not a problem.
            await Ssh("printf 'PasswordAuthentication no\\n' | sudo tee /etc/ssh/sshd_config.d/50-bbtest.conf >/dev/null");
            var leftover = await Write("/etc/ssh/sshd_config.d/90-bbtest.conf", "PasswordAuthentication yes\n");
            Assert.Contains("Note: /etc/ssh/sshd_config.d/90-bbtest.conf still says passwordauthentication yes, but sshd uses passwordauthentication no", leftover.Text);
            Assert.DoesNotContain("Problem", leftover.Text);

            // Through a shell command: a cron.d file cron skips (a dot in its name, group-writable).
            var cron = await Ssh("printf '*/5 * * * * root true\\n' | sudo tee /etc/cron.d/bb.job >/dev/null && sudo chmod 664 /etc/cron.d/bb.job");
            Assert.Contains("cron ignores /etc/cron.d/bb.job: names in /etc/cron.d may only have", cron.Text);
            Assert.Contains("writable by group or others (mode 664)", cron.Text);

            // A unit with a typo.
            var unit = await Write("/etc/systemd/system/bbtest.service", "[Service]\nExecStar=/bin/true\n");
            Assert.Contains("systemd-analyze verify bbtest.service", unit.Text);
            Assert.Matches("Unknown key (name )?'ExecStar'", unit.Text); // systemd 255 says "key name", 257 "key"
        }
        finally
        {
            await Ssh("sudo rm -f /etc/sudoers.d/bbdevs /etc/ssh/sshd_config.d/50-bbtest.conf /etc/ssh/sshd_config.d/90-bbtest.conf /etc/cron.d/bb.job " +
                      "/etc/systemd/system/bbtest.service; sudo groupdel bbdevs 2>/dev/null; sudo systemctl daemon-reload; true");
        }
    }

    [Fact]
    public async Task PostgreSQL_settings_are_checked_after_they_are_written()
    {
        if (Host is null) return;
        var dir = (await Ssh("ls -d /etc/postgresql/*/main/conf.d 2>/dev/null | head -1")).Text.Split('\n')[0].Trim();
        if (!dir.StartsWith("/etc/postgresql/")) return; // no PostgreSQL on this server
        var file = dir + "/bbtest.conf";
        Task<ToolResult> Write(string content) => new WriteFileTool().ExecuteAsync(
            new JsonObject { ["path"] = file, ["content"] = content, ["host"] = Host }, _ctx, CancellationToken.None);
        try
        {
            // A comment PostgreSQL doesn't know (seen in a run): the server would not start again.
            var bad = await Write("work_mem = '8MB' /* more for sorts */\n");
            Assert.Matches("PostgreSQL rejects .*bbtest.conf|invalid line 1 in .*bbtest.conf.*can't parse this line", bad.Text);
            Assert.DoesNotContain("can't log in", bad.Text);

            // listen_addresses is valid, but a reload doesn't apply it.
            var restart = await Write("listen_addresses = '*'\n");
            Assert.Contains("listen_addresses changes only when PostgreSQL restarts", restart.Text);
        }
        finally
        {
            await Ssh($"sudo rm -f {file}; sudo systemctl reload postgresql; true");
        }
    }

    [Fact]
    public async Task A_wait_without_output_says_what_is_running()
    {
        if (Host is null) return;
        // As apt behind | tail: nothing to show, so the call says which programs are at work.
        var r = await Ssh("sleep 40 | cat", wait: 4);
        Assert.Contains("still running after", r.Text);
        Assert.Contains("Running now: ", r.Text);
        Assert.Contains("sleep 40 (", r.Text);
        Assert.Contains("cat (", r.Text);
        Assert.DoesNotContain("bash (", r.Text);
        await Ssh(interrupt: true);
    }

    [Fact]
    public async Task A_search_that_could_not_see_root_only_places_says_so()
    {
        if (Host is null || Host.StartsWith("root@")) return;
        var hidden = await Ssh("grep -rl nothing-here /etc/cron.d /var/spool/cron 2>/dev/null; echo searched");
        Assert.Contains("searched", hidden.Text);
        Assert.Contains("/var/spool/cron can be read only by root", hidden.Text);
        var withSudo = await Ssh("sudo grep -rl nothing-here /var/spool/cron 2>/dev/null; echo searched");
        Assert.DoesNotContain("can be read only by root", withSudo.Text);
    }

    [Fact]
    public async Task Firewall_rule_files_are_checked_after_they_are_written()
    {
        if (Host is null) return;
        Task<ToolResult> Write(string path, string content) => new WriteFileTool().ExecuteAsync(
            new JsonObject { ["path"] = path, ["content"] = content, ["host"] = Host }, _ctx, CancellationToken.None);
        var have = (await Ssh("for c in iptables-restore nft; do sudo sh -c \"command -v $c\" >/dev/null && echo has-$c; done; " +
                              "[ -e /etc/iptables/rules.v4 ] && echo has-rules; true")).Text;
        Assert.DoesNotContain("has-rules", have); // the test would overwrite them
        var nft = false; // set once the server's own nftables.conf is moved aside
        try
        {
            if (have.Contains("has-iptables-restore"))
            {
                var bad = await Write("/etc/iptables/rules.v4", "*filter\n:INPUT ACCEPT [0:0]\n-A INPUT -p tcp --dprot 22 -j ACCEPT\nCOMMIT\n");
                Assert.Contains("Problem: iptables-restore --test /etc/iptables/rules.v4", bad.Text);
                var good = await Write("/etc/iptables/rules.v4", "*filter\n:INPUT ACCEPT [0:0]\n-A INPUT -p tcp --dport 22 -j ACCEPT\nCOMMIT\n");
                Assert.Contains("iptables-restore --test /etc/iptables/rules.v4 passed", good.Text);
            }
            if (have.Contains("has-nft"))
            {
                await Ssh("[ -e /etc/nftables.conf ] && sudo mv /etc/nftables.conf /etc/nftables.conf.bbtest; true");
                nft = true;
                // The mistake seen in a run: "ip6 protocol" is not nft syntax (it is "ip6 nexthdr" or "meta l4proto").
                var bad = await Write("/etc/nftables.conf", "table inet bbtest {\n  chain input {\n    type filter hook input priority 0; policy accept;\n    ip6 protocol icmpv6 accept\n  }\n}\n");
                Assert.Contains("Problem: nft -c -f /etc/nftables.conf", bad.Text);
                Assert.Contains("ip6 protocol icmpv6 accept", bad.Text);
            }
        }
        finally
        {
            await Ssh("sudo rm -f /etc/iptables/rules.v4; sudo rmdir /etc/iptables 2>/dev/null; " + (nft
                ? "[ -e /etc/nftables.conf.bbtest ] && sudo mv /etc/nftables.conf.bbtest /etc/nftables.conf || sudo rm -f /etc/nftables.conf; true" : "true"));
        }
    }

    [Fact]
    public async Task After_a_reboot_the_next_call_logs_in_again()
    {
        if (Host is null || Environment.GetEnvironmentVariable("BB_SSH_TEST_REBOOT") != "1") return;
        await Ssh("cd /tmp");
        await Ssh("sudo systemctl reboot", wait: 20); // returns at once, or as the connection drops
        await Task.Delay(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
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
