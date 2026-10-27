using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using Sloperator.Core.Comfy;
using Sloperator.Core.Web;
using Xunit;

namespace Sloperator.Core.Tests;

/// <summary>
/// The headless browser ComfyUI's page runs in, with the PC's own Edge (or Chrome) and a stand-in
/// for the page: a window.app whose graphToPrompt says how many nodes it was given.
/// </summary>
public class ComfyFrontendTests
{
    private const string Page = """
        <!doctype html><html><body><script>
        window.app = {
          graph: {},
          async loadGraphData(workflow) { this.loaded = workflow; },
          async graphToPrompt() { return { output: { "1": { class_type: "Echo", inputs: { nodes: this.loaded.nodes.length } } } }; },
        };
        </script></body></html>
        """;

    private static readonly JsonNode Workflow = JsonNode.Parse("""{"nodes": [{"id": 1}, {"id": 2}, {"id": 3}], "links": []}""")!;

    [Fact]
    public async Task Turns_a_workflow_into_the_api_format_in_a_profile_of_its_own_and_closes_the_browser_after()
    {
        if (HeadlessBrowser.FindExecutable() is null)
            Assert.Skip("No Edge or Chrome on this PC.");
        var ct = TestContext.Current.CancellationToken;
        using var site = new FileServer();
        site.Page("/", Page);
        var root = Directory.CreateTempSubdirectory("bb-frontend-").FullName;
        var ports = new List<int>();
        try
        {
            var frontend = new ComfyFrontend(root);
            var output = await frontend.ConvertAsync(site.Url, 1, Workflow, ct);
            Assert.Equal(3, (int)output["1"]!["inputs"]!["nodes"]!);
            var profile = Assert.Single(Directory.GetDirectories(root));
            Assert.Matches($@"^{Environment.ProcessId}-\d+$", Path.GetFileName(profile));
            ports.Add(PortOf(profile));
            Assert.True(await AnswersAsync(ports[0]));

            await frontend.DisposeAsync();
            Assert.False(await AnswersAsync(ports[0]));
            Assert.Empty(Directory.GetFileSystemEntries(root));
        }
        finally
        {
            await CleanUpAsync(root, ports);
        }
    }

    [Fact]
    public async Task A_browser_that_hands_over_to_a_copy_of_itself_and_quits_is_found_by_the_copys_port()
    {
        var browser = HeadlessBrowser.FindExecutable()!;
        if (browser is null)
            Assert.Skip("No Edge or Chrome on this PC.");
        var ct = TestContext.Current.CancellationToken;
        using var site = new FileServer();
        site.Page("/", Page);
        var root = Directory.CreateTempSubdirectory("bb-frontend-").FullName;
        var ports = new List<int>();
        try
        {
            // As Edge does when Sloperator runs as administrator: it starts a copy of itself, with the
            // same arguments, and quits with 0 before the copy has opened its port.
            var launcher = Path.Combine(root, "..", Path.GetFileName(root) + "-launcher.cmd");
            await File.WriteAllTextAsync(launcher, $"@start \"\" \"{browser}\" %*\r\n", ct);
            var frontend = new ComfyFrontend(root) { BrowsersForTests = [launcher] };

            var output = await frontend.ConvertAsync(site.Url, 1, Workflow, ct);
            Assert.Equal(3, (int)output["1"]!["inputs"]!["nodes"]!);
            ports.Add(PortOf(Assert.Single(Directory.GetDirectories(root))));

            // Used again without starting another, and closed over its port: it isn't Sloperator's child.
            output = await frontend.ConvertAsync(site.Url, 1, Workflow, ct);
            Assert.Equal(3, (int)output["1"]!["inputs"]!["nodes"]!);
            Assert.Single(Directory.GetDirectories(root));
            await frontend.DisposeAsync();
            Assert.False(await AnswersAsync(ports[0]));
            File.Delete(launcher);
        }
        finally
        {
            await CleanUpAsync(root, ports);
        }
    }

    [Fact]
    public async Task Browsers_earlier_starts_left_running_are_closed_and_their_profiles_deleted()
    {
        var browser = HeadlessBrowser.FindExecutable()!;
        if (browser is null)
            Assert.Skip("No Edge or Chrome on this PC.");
        var ct = TestContext.Current.CancellationToken;
        using var site = new FileServer();
        site.Page("/", Page);
        var root = Directory.CreateTempSubdirectory("bb-frontend-").FullName;
        var ports = new List<int>();
        var started = new List<Process>();
        try
        {
            // Left running: one on the profile every start used to share (the folder itself), its
            // port file gone as the Sloperator before deleted it, and one in the folder of a start by a
            // Sloperator that has ended.
            var ended = Path.Combine(root, $"{UnusedProcessId()}-1");
            foreach (var profile in new[] { root, ended })
            {
                started.Add(StartHeadless(browser, profile));
                ports.Add(await WaitForPortAsync(profile, ct));
            }
            File.Delete(Path.Combine(root, "DevToolsActivePort"));
            Assert.True(await AnswersAsync(ports[0]) && await AnswersAsync(ports[1]));

            var frontend = new ComfyFrontend(root);
            var output = await frontend.ConvertAsync(site.Url, 1, Workflow, ct);
            Assert.Equal(3, (int)output["1"]!["inputs"]!["nodes"]!);
            Assert.All(started, p => Assert.True(p.HasExited));
            Assert.False(await AnswersAsync(ports[1]));
            // Only this start's folder is left.
            var left = Assert.Single(Directory.GetFileSystemEntries(root));
            Assert.Matches($@"^{Environment.ProcessId}-\d+$", Path.GetFileName(left));
            ports.Add(PortOf(left));
            await frontend.DisposeAsync();
        }
        finally
        {
            foreach (var p in started)
                try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            await CleanUpAsync(root, ports);
        }
    }

    [Fact]
    public async Task Where_headless_mode_is_refused_a_hidden_window_does_the_work_and_no_window_shows()
    {
        var browser = HeadlessBrowser.FindExecutable()!;
        if (browser is null || !OperatingSystem.IsWindows())
            Assert.Skip("No Edge or Chrome on this PC, or not Windows.");
        var ct = TestContext.Current.CancellationToken;
        using var site = new FileServer();
        site.Page("/", Page);
        var root = Directory.CreateTempSubdirectory("bb-frontend-").FullName;
        var ports = new List<int>();
        try
        {
            // As on a PC whose HeadlessModeEnabled policy is off, once that has been found out.
            var frontend = new ComfyFrontend(root) { HeadlessRefused = true };
            var output = await frontend.ConvertAsync(site.Url, 1, Workflow, ct);
            Assert.Equal(3, (int)output["1"]!["inputs"]!["nodes"]!);
            var profile = Assert.Single(Directory.GetDirectories(root));
            ports.Add(PortOf(profile));

            var browsers = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(browser))
                .Where(p => Platform.ProcessCommandLine.Of(p.Id) is { } line && line.Contains(profile, StringComparison.OrdinalIgnoreCase) && !line.Contains("--type="))
                .ToList();
            var main = Assert.Single(browsers);
            Assert.DoesNotContain("--headless", Platform.ProcessCommandLine.Of(main.Id));
            Assert.Equal(0, VisibleWindows(main.Id));
            await frontend.DisposeAsync();
            Assert.False(await AnswersAsync(ports[0]));
        }
        finally
        {
            await CleanUpAsync(root, ports);
        }
    }

    [Fact]
    public async Task A_browser_that_refuses_headless_mode_is_remembered_and_its_words_reported()
    {
        if (!OperatingSystem.IsWindows())
            Assert.Skip("Windows only.");
        var ct = TestContext.Current.CancellationToken;
        var root = Directory.CreateTempSubdirectory("bb-frontend-").FullName;
        var log = Path.Combine(root + "-launches.txt");
        // Refuses headless as Edge does under that policy, and fails otherwise too, so no window opens.
        // Windows' own find.exe: run from Git Bash, the PATH finds GNU find first.
        var launcher = root + "-refuses.cmd";
        await File.WriteAllTextAsync(launcher, $$"""
            @echo off
            echo %*>>"{{log}}"
            echo %* | "%SystemRoot%\System32\find.exe" "--headless" >nul && (echo Headless mode is disallowed by the system admin. 1>&2 & exit /b 0)
            exit /b 3
            """, ct);
        try
        {
            var frontend = new ComfyFrontend(root) { BrowsersForTests = [launcher] };
            var refused = await Assert.ThrowsAsync<ComfyApiException>(() => frontend.ConvertAsync(new Uri("http://127.0.0.1:9/"), 1, Workflow, ct));
            Assert.Contains("won't run headless on this PC", refused.Message);
            Assert.Contains("Headless mode is disallowed by the system admin.", refused.Message);
            Assert.Contains("in a hidden window: The browser closed at once (exit code 3).", refused.Message);
            Assert.True(frontend.HeadlessRefused);

            // The next start goes straight to a window.
            await Assert.ThrowsAsync<ComfyApiException>(() => frontend.ConvertAsync(new Uri("http://127.0.0.1:9/"), 1, Workflow, ct));
            var starts = await File.ReadAllLinesAsync(log, ct);
            Assert.Equal([true, false, false], starts.Select(s => s.Contains("--headless")));
        }
        finally
        {
            File.Delete(launcher);
            File.Delete(log);
            await CleanUpAsync(root, []);
        }
    }

    private static int VisibleWindows(int pid)
    {
        var count = 0;
        EnumWindows((window, _) =>
        {
            GetWindowThreadProcessId(window, out var owner);
            if (owner == pid && IsWindowVisible(window)) count++;
            return true;
        }, 0);
        return count;
    }

    private delegate bool EnumWindowsProc(nint window, nint parameter);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, nint parameter);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool IsWindowVisible(nint window);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint window, out int processId);

    [Theory]
    [InlineData(@"""C:\Edge\msedge.exe"" --headless=new ""--user-data-dir=C:\Data\comfy ui\browser"" about:blank", @"C:\Data\comfy ui\browser")]
    [InlineData(@"msedge.exe --user-data-dir=""C:\Data\comfy ui\browser"" about:blank", @"C:\Data\comfy ui\browser")]
    [InlineData(@"msedge.exe --headless=new --user-data-dir=C:\Data\comfyui\browser\4242-1 about:blank", @"C:\Data\comfyui\browser\4242-1")]
    [InlineData(@"msedge.exe --user-data-dir=C:\Data\browser", @"C:\Data\browser")]
    [InlineData(@"msedge.exe --type=renderer about:blank", null)]
    public void A_switch_is_read_from_a_command_line_quoted_or_not(string commandLine, string? value) =>
        Assert.Equal(value, Platform.ProcessCommandLine.Switch(commandLine, "--user-data-dir"));

    [Fact]
    public void Another_process_command_line_is_read()
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux())
            Assert.Skip("Windows and Linux only.");
        using var child = Process.Start(new ProcessStartInfo(OperatingSystem.IsWindows() ? "cmd.exe" : "sleep")
        {
            ArgumentList = { OperatingSystem.IsWindows() ? "/k" : "5" }, // cmd waits on its input meanwhile
            UseShellExecute = false,
            RedirectStandardInput = true,
            CreateNoWindow = true,
        })!;
        try
        {
            if (OperatingSystem.IsWindows())
                Assert.Contains("cmd.exe", Platform.ProcessCommandLine.Of(child.Id) ?? "", StringComparison.OrdinalIgnoreCase);
            else
                Assert.Equal("sleep 5", Platform.ProcessCommandLine.Of(child.Id));
        }
        finally
        {
            try { child.Kill(); } catch (InvalidOperationException) { }
        }
    }

    private static Process StartHeadless(string browser, string profile)
    {
        Directory.CreateDirectory(profile);
        var psi = new ProcessStartInfo(browser) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { "--headless=new", "--disable-gpu", "--no-first-run", "--no-default-browser-check", "--remote-debugging-port=0", $"--user-data-dir={profile}", "about:blank" })
            psi.ArgumentList.Add(arg);
        var process = Process.Start(psi)!;
        _ = process.StandardOutput.ReadToEndAsync();
        _ = process.StandardError.ReadToEndAsync();
        return process;
    }

    private static int UnusedProcessId()
    {
        for (var pid = 4_000_000; ; pid += 4)
            try { Process.GetProcessById(pid).Dispose(); }
            catch (ArgumentException) { return pid; }
    }

    private static int PortOf(string profile) => int.Parse(File.ReadAllLines(Path.Combine(profile, "DevToolsActivePort"))[0]);

    private static async Task<int> WaitForPortAsync(string profile, CancellationToken ct)
    {
        for (var waited = 0; waited < 15_000; waited += 100)
        {
            try
            {
                var lines = await File.ReadAllLinesAsync(Path.Combine(profile, "DevToolsActivePort"), ct);
                if (lines.Length >= 2 && int.TryParse(lines[0], out var port)) return port;
            }
            catch (IOException) { }
            await Task.Delay(100, ct);
        }
        throw new TimeoutException("The browser opened no debugging port.");
    }

    private static async Task<bool> AnswersAsync(int port)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        try
        {
            await http.GetStringAsync($"http://127.0.0.1:{port}/json/version");
            return true;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return false;
        }
    }

    /// <summary>Whatever a test left: its browsers closed over their ports, and its folder.</summary>
    private static async Task CleanUpAsync(string root, List<int> ports)
    {
        foreach (var port in ports.Where(p => p > 0))
        {
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
                var url = (string?)JsonNode.Parse(await http.GetStringAsync($"http://127.0.0.1:{port}/json/version"))?["webSocketDebuggerUrl"];
                if (url is null) continue;
                using var socket = new ClientWebSocket();
                await socket.ConnectAsync(new Uri(url), CancellationToken.None);
                await socket.SendAsync(Encoding.UTF8.GetBytes("""{"id": 1, "method": "Browser.close"}"""), WebSocketMessageType.Text, true, CancellationToken.None);
            }
            catch (Exception) { }
        }
        for (var attempt = 0; attempt < 10 && Directory.Exists(root); attempt++)
        {
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) { await Task.Delay(300); }
            catch (UnauthorizedAccessException) { await Task.Delay(300); }
        }
    }
}
