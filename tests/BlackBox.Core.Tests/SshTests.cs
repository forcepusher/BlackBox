using BlackBox.Core.Ssh;
using BlackBox.Core.Tools;
using Xunit;

namespace BlackBox.Core.Tests;

public class SshTests
{
    [Fact]
    public void Terminal_output_reads_as_the_screen_shows_it()
    {
        // Colours go, a progress bar redrawn with \r keeps only its last state, backspace erases.
        Assert.Equal("Progress 100%\ndone", TerminalText.Clean("\x1b[32mProgress 10%\rProgress 55%\rProgress 100%\x1b[0m\r\ndone"));
        Assert.Equal("xycdef", TerminalText.Clean("abcdef\rxy"));
        Assert.Equal("ab ", TerminalText.Clean("abc\b \b")); // erased with a space, as a terminal does
        Assert.Equal("title gone", TerminalText.Clean("\x1b]0;admin@lab01: ~\x07title gone"));
        Assert.Equal("bell", TerminalText.Clean("be\x07ll"));
        Assert.True(TerminalText.EntersFullScreen("\x1b[?1049h\x1b[22;0;0t"));
        Assert.False(TerminalText.EntersFullScreen("plain output"));
    }

    [Fact]
    public void Binary_output_is_left_out_with_a_note()
    {
        // An image printed as text (curl without -o): random bytes read as UTF-8.
        var bytes = new byte[20_000];
        new Random(3).NextBytes(bytes);
        var image = System.Text.Encoding.UTF8.GetString(bytes);
        var text = "<h1>Lab Bakery</h1>\n---\nbody { font-family: serif; }\n" + image + "\nimg/logo.png: 200 150000 bytes";
        var kept = TerminalText.DropBinary(text);
        Assert.StartsWith("<h1>Lab Bakery</h1>\n---\nbody { font-family: serif; }\n", kept);
        Assert.EndsWith("img/logo.png: 200 150000 bytes", kept);
        Assert.Contains("[binary data left out:", kept);
        Assert.True(kept.Length < 400, kept);
        Assert.Equal(1, kept.Split('\n').Count(l => l.StartsWith("[binary data")));

        // Ordinary text stays as it is: colours, tabs, other alphabets, short lines.
        foreach (var line in new[] { "\x1b[32mPASS\x1b[0m two players\x1b[0m see each other move", "name\tsize\tdate\tpath\towner\tmode", "Привет, это обычный текст на сервере", "ab�cd" })
            Assert.False(TerminalText.LooksBinary(line), line);
        Assert.Equal("plain\ntext", TerminalText.DropBinary("plain\ntext"));

        // Russian in code page 866 read as UTF-8 ("Ошибка: файл не найден"): all undecodable letters, but no
        // control characters, so run_shell (which asks for them) keeps it; real binary has plenty.
        var cp866 = System.Text.Encoding.UTF8.GetString([0x8E, 0xE8, 0xA8, 0xA1, 0xAA, 0xA0, 0x3A, 0x20, 0xE4, 0xA0, 0xA9, 0xAB, 0x20, 0xAD, 0xA5, 0x20, 0xAD, 0xA0, 0xA9, 0xA4, 0xA5, 0xAD]);
        Assert.False(TerminalText.LooksBinary(cp866, needControl: true));
        Assert.True(TerminalText.LooksBinary(image.Split('\n').First(l => l.Length > 100), needControl: true));
    }

    [Fact]
    public void Progress_redrawn_with_cursor_moves_keeps_its_last_state()
    {
        // docker pull on a terminal: a line per layer, each update goes up to its line, erases it,
        // writes it and comes back down (jsonmessage.DisplayJSONMessagesStream).
        string Update(int up, string text) => $"\x1b[{up}A\x1b[2K\r{text}\r\x1b[{up}B";
        var pull = "3.12-alpine: Pulling from library/python\n" +
                   "\n" + Update(1, "a1: Pulling fs layer") +
                   "\n" + Update(1, "b2: Pulling fs layer") +
                   Update(2, "a1: Downloading [=====>      ]  1.2MB/3.6MB") +
                   Update(1, "b2: Downloading [==>         ]  2.1MB/15MB") +
                   Update(2, "a1: Pull complete") +
                   Update(1, "b2: Pull complete") +
                   "Digest: sha256:0f1e\nStatus: Downloaded newer image for python:3.12-alpine\n";
        Assert.Equal("3.12-alpine: Pulling from library/python\na1: Pull complete\nb2: Pull complete\n" +
                     "Digest: sha256:0f1e\nStatus: Downloaded newer image for python:3.12-alpine\n", TerminalText.Clean(pull));

        // apt's progress bar on the bottom row, drawn between saving and restoring the cursor, is left out.
        var apt = "Setting up a ...\n\n\x1b" + "7\x1b[0;49r\x1b" + "8\x1b[1A" +
                  "\x1b" + "7\x1b[50;0f\x1b[42m\x1b[30mProgress: [ 40%]\x1b[49m\x1b[39m [#####.......] \x1b" + "8" +
                  "Setting up b ...\n";
        Assert.Equal("Setting up a ...\nSetting up b ...\n", TerminalText.Clean(apt));

        // A spinner that goes back to column 1 and erases the rest of the line; clear doesn't take the output.
        Assert.Equal("added 3 packages", TerminalText.Clean("|\x1b[1G\x1b[0K/\x1b[1G\x1b[0K-\x1b[1G\x1b[0Kadded 3 packages"));
        Assert.Equal("before\nafter", TerminalText.Clean("before\n\x1b[H\x1b[2Jafter"));
        // Erasing below the cursor after going up: a redrawn block shrinks.
        Assert.Equal("one\ntwo", TerminalText.Clean("one\nold two\nold three\n\x1b[2A\x1b[2Ktwo\x1b[J"));
    }

    [Fact]
    public void Long_output_keeps_its_start_and_its_larger_end()
    {
        var text = string.Join("\n", Enumerable.Range(1, 5000).Select(i => $"line {i}"));
        var clipped = TerminalText.Clip(text, 4000);
        Assert.True(clipped.Length < 4200);
        Assert.StartsWith("line 1\n", clipped);
        Assert.EndsWith("line 5000", clipped);
        Assert.Matches(@"\[\.\.\. [\d,]+ lines \([\d,]+ characters\) left out \.\.\.\]", clipped);
        Assert.Equal("short", TerminalText.Clip("short", 4000));
    }

    [Theory]
    [InlineData("Do you want to continue? [Y/n] ", true)]
    [InlineData("Password: ", true)]
    [InlineData("Enter passphrase for key '/root/.ssh/id_rsa':", true)]
    [InlineData("Are you sure you want to continue connecting (yes/no/[fingerprint])? ", true)]
    [InlineData("shop=> ", true)]
    [InlineData("postgres=# ", true)]
    [InlineData("Unpacking nginx (1.24.0-2ubuntu7) ...", false)]
    [InlineData("Progress: 45%", false)]
    [InlineData("", false)]
    public void Questions_are_told_from_progress_output(string lastLine, bool prompt) =>
        Assert.Equal(prompt, TerminalText.LooksLikePrompt(lastLine));

    [Fact]
    public void Sudo_prompt_is_recognised()
    {
        Assert.True(TerminalText.IsSudoPrompt("[sudo] password for admin: "));
        Assert.False(TerminalText.IsSudoPrompt("Password: "));
    }

    [Theory]
    [InlineData("admin@192.168.1.10", "admin", "192.168.1.10", 22)]
    [InlineData("admin@192.168.1.10:2222", "admin", "192.168.1.10", 2222)]
    [InlineData("ssh://deploy@web01.example.com:2200/", "deploy", "web01.example.com", 2200)]
    [InlineData("root@[fe80::1]:22", "root", "fe80::1", 22)]
    [InlineData(" pi@raspberrypi ", "pi", "raspberrypi", 22)]
    public void Targets_parse(string text, string user, string host, int port)
    {
        var t = SshTarget.Parse(text, sshConfig: "no-such-config");
        Assert.Equal((user, host, port), (t.User, t.Host, t.Port));
    }

    [Fact]
    public void Targets_need_a_user_unless_ssh_config_names_one()
    {
        var ex = Assert.Throws<ArgumentException>(() => SshTarget.Parse("192.168.1.10", sshConfig: "no-such-config"));
        Assert.Contains("user@192.168.1.10", ex.Message);

        var config = Path.GetTempFileName();
        File.WriteAllText(config, """
            # work
            Host web web.lab !old
                HostName 10.0.0.5
                User deploy
                Port 2222
                IdentityFile ~/.ssh/work_key
            Host *
                User fallback
            """);
        try
        {
            var t = SshTarget.Parse("web", config);
            Assert.Equal(("deploy", "10.0.0.5", 2222), (t.User, t.Host, t.Port));
            Assert.EndsWith(Path.Combine(".ssh", "work_key"), t.IdentityFile);
            Assert.Equal("root", SshTarget.Parse("root@web", config).User); // the text wins over the config
            Assert.Equal(("fallback", "other"), (SshTarget.Parse("other", config).User, SshTarget.Parse("other", config).Host));
        }
        finally
        {
            File.Delete(config);
        }
    }

    [Theory]
    [InlineData("sudo -i")]
    [InlineData("sudo su")]
    [InlineData("sudo su -")]
    [InlineData("su - postgres")]
    [InlineData("sudo -iu postgres")]
    [InlineData("sudo bash")]
    [InlineData("bash")]
    [InlineData("nano /etc/nginx/nginx.conf")]
    [InlineData("sudo vim /etc/ssh/sshd_config")]
    [InlineData("less /var/log/syslog")]
    [InlineData("top")]
    [InlineData("sudo visudo")]
    [InlineData("crontab -e")]
    [InlineData("sudo systemctl edit nginx")]
    public void Commands_that_would_wait_for_keys_are_refused_with_advice(string command) =>
        Assert.NotNull(SshTool.Refusal(command));

    [Theory]
    [InlineData("sudo apt-get install -y nginx")]
    [InlineData("sudo -u postgres psql -c 'select 1'")]
    [InlineData("sudo bash -c 'echo hi > /etc/motd'")]
    [InlineData("bash ./deploy.sh")]
    [InlineData("top -bn1 | head")]
    [InlineData("top -b -n 1")]
    [InlineData("sudo visudo -cf /etc/sudoers.d/devs")]
    [InlineData("crontab -l")]
    [InlineData("sudo systemctl restart nginx")]
    [InlineData("cat /etc/os-release; less_is_more=1")]
    public void Ordinary_commands_run(string command) =>
        Assert.Null(SshTool.Refusal(command));

    [Theory]
    [InlineData("admin@127.0.0.2:/var/www/html", "admin@127.0.0.2", "/var/www/html", false)]
    [InlineData("deploy@web01:2222:/srv/app/", "deploy@web01:2222", "/srv/app/", false)]
    [InlineData("admin@host:/tmp/site/*", "admin@host", "/tmp/site", true)]
    [InlineData(@"C:\Users\me\site-v2", null, @"C:\Users\me\site-v2", false)]
    [InlineData(@"C:\Users\me\site-v2\*", null, @"C:\Users\me\site-v2", true)]
    [InlineData("admin@host:", "admin@host", ".", false)]
    public void Copy_sides_parse_like_scp(string text, string? host, string path, bool contents)
    {
        var side = CopyTool.Side.Parse(text);
        Assert.Equal((host, path, contents), (side.Host, side.Path, side.ContentsOnly));
    }

    [Fact]
    public async Task Copy_on_this_pc_keeps_bytes_and_follows_cp()
    {
        var root = Path.Combine(Path.GetTempPath(), "bb-copy-" + Guid.NewGuid().ToString("N"));
        var src = Path.Combine(root, "site");
        Directory.CreateDirectory(Path.Combine(src, "img"));
        var image = new byte[70000];
        new Random(3).NextBytes(image);
        image[1] = 0; image[2] = (byte)'\n';
        await File.WriteAllBytesAsync(Path.Combine(src, "img", "logo.png"), image);
        await File.WriteAllTextAsync(Path.Combine(src, "index.html"), "<h1>hi</h1>\n");
        var ctx = new ToolContext { Platform = new FakePlatform(), Settings = new Settings.AgentSettings(), WorkingDirectory = root };
        try
        {
            // Into an existing folder: it goes inside, as cp -r and scp -r do.
            Directory.CreateDirectory(Path.Combine(root, "www"));
            var r = await new CopyTool().ExecuteAsync(new() { ["from"] = src, ["to"] = Path.Combine(root, "www") }, ctx, CancellationToken.None);
            Assert.False(r.IsError, r.Text);
            Assert.Contains("Copied 2 files", r.Text);
            Assert.Equal(image, await File.ReadAllBytesAsync(Path.Combine(root, "www", "site", "img", "logo.png")));
            // To a new name: the folder takes it. With /*: only what is in it.
            await new CopyTool().ExecuteAsync(new() { ["from"] = src, ["to"] = Path.Combine(root, "renamed") }, ctx, CancellationToken.None);
            Assert.True(File.Exists(Path.Combine(root, "renamed", "index.html")));
            Directory.CreateDirectory(Path.Combine(root, "flat"));
            await new CopyTool().ExecuteAsync(new() { ["from"] = src + "\\*", ["to"] = Path.Combine(root, "flat") }, ctx, CancellationToken.None);
            Assert.True(File.Exists(Path.Combine(root, "flat", "img", "logo.png")));
            // A single file to a new name.
            await new CopyTool().ExecuteAsync(new() { ["from"] = Path.Combine(src, "index.html"), ["to"] = Path.Combine(root, "home.html") }, ctx, CancellationToken.None);
            Assert.Equal("<h1>hi</h1>\n", await File.ReadAllTextAsync(Path.Combine(root, "home.html")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Ssh_without_a_host_needs_one_the_first_time()
    {
        using var hosts = new SshHosts(Path.Combine(Path.GetTempPath(), "bb-known-" + Guid.NewGuid().ToString("N")));
        var ctx = new ToolContext { Platform = new FakePlatform(), Settings = new Settings.AgentSettings(), WorkingDirectory = Path.GetTempPath(), Ssh = hosts };
        var r = await new SshTool().ExecuteAsync(new() { ["command"] = "uptime" }, ctx, CancellationToken.None);
        Assert.True(r.IsError);
        Assert.Contains("Give host", r.Text);
    }

    [Fact]
    public void Results_say_what_to_do_next()
    {
        var done = SshTool.Format(new ShellResult(ShellState.Done, "active", 0, "/etc/nginx"), null, TimeSpan.FromSeconds(120));
        Assert.Equal("active\n[exit code 0 · /etc/nginx]", done.Text);
        Assert.False(done.IsError);
        Assert.True(SshTool.Format(new ShellResult(ShellState.Done, "", 3, "/"), null, TimeSpan.FromSeconds(1)).IsError);

        var waiting = SshTool.Format(new ShellResult(ShellState.WaitingForInput, "Do you want to continue? [Y/n]", Prompt: "Do you want to continue? [Y/n]"),
            "Connected to admin@lab: Ubuntu.", TimeSpan.FromSeconds(120));
        Assert.StartsWith("[Connected to admin@lab: Ubuntu.]\n", waiting.Text);
        Assert.Contains("Answer with input", waiting.Text);

        var running = SshTool.Format(new ShellResult(ShellState.Running, "Unpacking ..."), null, TimeSpan.FromSeconds(60));
        Assert.Contains("still running after 60 s", running.Text);
        Assert.Contains("no command to wait", running.Text);
    }

    [Fact]
    public void Config_files_a_command_writes_are_found()
    {
        Assert.Equal(["/etc/sudoers.d/devs"], ConfigChecks.PathsIn("echo 'devs ALL=(ALL) NOPASSWD:ALL' | sudo tee /etc/sudoers.d/devs"));
        Assert.Equal(["/etc/ssh/sshd_config.d/90-hardening.conf"], ConfigChecks.PathsIn("sudo tee /etc/ssh/sshd_config.d/90-hardening.conf >/dev/null <<'EOF'\nPasswordAuthentication no\nEOF"));
        Assert.Equal(["/etc/ssh/sshd_config"], ConfigChecks.PathsIn("sudo sed -i 's/^PermitRootLogin yes/PermitRootLogin no/' /etc/ssh/sshd_config"));
        Assert.Equal(["/etc/nginx/sites-available/shop", "/etc/nginx/sites-enabled/shop"],
            ConfigChecks.PathsIn("sudo ln -sf /etc/nginx/sites-available/shop /etc/nginx/sites-enabled/shop"));
        Assert.Equal(["/etc/systemd/system/tiny-arena.service"], ConfigChecks.PathsIn("sudo cp unit /etc/systemd/system/tiny-arena.service && sudo systemctl daemon-reload"));
        Assert.Equal(["/etc/postgresql/16/main/pg_hba.conf"], ConfigChecks.PathsIn("echo 'host shop shop 10.0.2.0/24 scram-sha-256' | sudo tee -a /etc/postgresql/16/main/pg_hba.conf"));
        Assert.Equal(["/etc/postgresql/17/main/postgresql.conf"],
            ConfigChecks.PathsIn("sudo sed -i \"s/^#listen_addresses = 'localhost'/listen_addresses = '*'/\" /etc/postgresql/17/main/postgresql.conf"));
        Assert.Equal(["/etc/postgresql/17/main/conf.d/listen.conf"], ConfigChecks.PathsIn("echo \"listen_addresses = '*'\" | sudo tee /etc/postgresql/17/main/conf.d/listen.conf"));
        Assert.Equal(1, ConfigChecks.Script(["/etc/postgresql/17/main/postgresql.conf", "/etc/postgresql/17/main/conf.d/listen.conf"])!.Split('\n').Count(l => l == "chk_pgconf"));
        // Reading isn't writing, and 2>/dev/null writes nothing that matters.
        Assert.Empty(ConfigChecks.PathsIn("sudo cat /etc/sudoers.d/devs 2>/dev/null; grep -n Port /etc/ssh/sshd_config"));
        Assert.Empty(ConfigChecks.PathsIn("sudo tee /etc/motd < banner.txt"));
        Assert.Null(ConfigChecks.Script(["/etc/motd", "/home/admin/notes.txt"]));
        Assert.Contains("chk_sshd '/etc/ssh/sshd_config.d/a.conf' '/etc/ssh/sshd_config'", ConfigChecks.Script(["/etc/ssh/sshd_config.d/a.conf", "/etc/ssh/sshd_config"]));
        Assert.Equal(1, ConfigChecks.Script(["/etc/nginx/a", "/etc/nginx/b"])!.Split('\n').Count(l => l == "chk_nginx"));
    }

    [Fact]
    public void Config_check_output_becomes_a_short_note()
    {
        Assert.Equal("Checked after writing: visudo -cf /etc/sudoers.d/devs passed. Problem: in /etc/sudoers.d/devs, devs is a group, not a user: a rule for a group needs %devs.",
            ConfigChecks.Note("OK visudo -cf /etc/sudoers.d/devs\nNOTE in /etc/sudoers.d/devs, devs is a group, not a user: a rule for a group needs %devs\n"));
        Assert.Equal("Checked after writing: Problem: nginx -t: unexpected end of file.", ConfigChecks.Note("ERR nginx -t: unexpected end of file\n"));
        Assert.Null(ConfigChecks.Note("sudo: a password is required\n"));
    }
}
