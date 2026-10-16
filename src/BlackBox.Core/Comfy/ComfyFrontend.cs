using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace BlackBox.Core.Comfy;

/// <summary>
/// ComfyUI's own web page, loaded in the PC's browser run headless, to turn a workflow as the
/// editor saves it (nodes, links, subgraphs, bypassed nodes, reroutes, promoted widgets) into the
/// API format /prompt runs: exactly what the page sends when the user presses Run. Templates are
/// kept in the editor's format, and their features change with every release of the page, so no
/// copy of its conversion could keep up. The browser stays up for a few minutes between uses.
/// </summary>
/// <remarks>
/// Every start of the browser gets a profile folder of its own under <c>profileRoot</c>, and the
/// browsers earlier starts left running are closed first, so none stands in a later start's way.
/// </remarks>
public sealed partial class ComfyFrontend(string profileRoot) : IAsyncDisposable
{
    private static readonly TimeSpan IdleTime = TimeSpan.FromMinutes(3);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private Process? _browser;
    private ClientWebSocket? _socket;
    private ActivePort? _port; // where the browser in use listens, to close it whoever started it
    private string? _profile;
    private bool _handedOver; // the browser started has handed over to a copy of itself, which is the one in use
    private Uri? _page;
    private int _generation = -1;
    private int _nextId;
    private int _launches;
    private int _exitHooked;
    private Timer? _idle;

    /// <summary>The browsers to try, best first, instead of the ones on the PC.</summary>
    internal IReadOnlyList<string>? BrowsersForTests { get; set; }

    /// <summary>
    /// The API-format workflow for <paramref name="editorWorkflow"/>, as the page at
    /// <paramref name="server"/> makes it. <paramref name="generation"/> changes when the server
    /// restarts, so the page loads again with its new node types.
    /// </summary>
    public async Task<JsonObject> ConvertAsync(Uri server, int generation, JsonNode editorWorkflow, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _idle?.Dispose();
            if (_socket is not { State: WebSocketState.Open } || _browser is null || (_browser.HasExited && !_handedOver) || _page != server || _generation != generation)
                await OpenAsync(server, generation, ct).ConfigureAwait(false);
            var script = "(async () => { const wf = " + JsonValue.Create(editorWorkflow.ToJsonString()).ToJsonString() + ";" +
                         " await window.app.loadGraphData(JSON.parse(wf), true, false, null, { showMissingNodesDialog: false, showMissingModelsDialog: false });" +
                         " const p = await window.app.graphToPrompt(); return JSON.stringify(p.output); })()";
            var result = await EvaluateAsync(script, TimeSpan.FromSeconds(60), ct).ConfigureAwait(false);
            if (result is not JsonValue v || !v.TryGetValue<string>(out var text) || JsonNode.Parse(text) is not JsonObject output)
                throw new ComfyApiException("ComfyUI's page gave no workflow back.");
            return output;
        }
        finally
        {
            _idle = new Timer(_ => _ = CloseAsync(), null, IdleTime, Timeout.InfiniteTimeSpan);
            _gate.Release();
        }
    }

    private async Task OpenAsync(Uri server, int generation, CancellationToken ct)
    {
        await CloseBrowserAsync().ConfigureAwait(false);
        await CloseLeftoversAsync(ct).ConfigureAwait(false);
        var browsers = BrowsersForTests ?? Web.HeadlessBrowser.FindExecutables();
        if (browsers.Count == 0)
            throw new ComfyApiException("Turning a saved workflow into one ComfyUI can run needs Edge or Chrome, and neither is on this PC.");
        // Edge first, Chrome when Edge can't be started and the PC has it too; each headless first,
        // then as a normal browser with its window hidden.
        var failures = new List<string>();
        foreach (var exe in browsers)
        {
            foreach (var headless in HeadlessRefused ? [false] : new[] { true, false })
            {
                try
                {
                    await LaunchAsync(exe, headless, ct).ConfigureAwait(false);
                    break;
                }
                catch (ComfyApiException ex)
                {
                    failures.Add($"{Path.GetFileNameWithoutExtension(exe)} {(headless ? "headless" : "in a hidden window")}: {ex.Message}");
                    await CloseBrowserAsync().ConfigureAwait(false);
                }
            }
            if (_socket is not null)
                break;
        }
        if (_socket is null)
            throw new ComfyApiException(string.Join(" ", failures));

        await CallAsync("Page.navigate", new JsonObject { ["url"] = server.ToString() }, TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
        var ready = Stopwatch.StartNew();
        while (true)
        {
            var up = await EvaluateAsync("!!(window.app && window.app.graph && typeof window.app.graphToPrompt === 'function' && typeof window.app.loadGraphData === 'function')",
                TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
            if (up is JsonValue v && v.TryGetValue<bool>(out var b) && b)
                break;
            if (ready.Elapsed > TimeSpan.FromSeconds(90))
                throw new ComfyApiException($"ComfyUI's page at {server} did not finish loading in the browser within 90 seconds.");
            await Task.Delay(150, ct).ConfigureAwait(false);
        }
        _page = server;
        _generation = generation;
    }

    /// <summary>
    /// The browser won't run headless on this PC: it quit saying "Headless mode is disallowed by the
    /// system admin." (Edge's and Chrome's HeadlessModeEnabled policy turned off). Later starts go
    /// straight to a hidden window.
    /// </summary>
    internal bool HeadlessRefused { get; set; }

    /// <summary>
    /// Starts <paramref name="exe"/> in a profile folder of its own, headless or as a normal browser
    /// whose window is hidden from the start (no window shows, nor a taskbar button, and the page's
    /// scripts run at full speed), and connects to its page.
    /// </summary>
    /// <remarks>
    /// Edge and Chrome can quit with 0 at once without anything wrong with them: with headless mode
    /// turned off by policy (then a hidden window does), and started by a program run as
    /// administrator, when they start an un-elevated copy of themselves (--do-not-de-elevate keeps
    /// them as they are). A browser that hands over to a copy of itself (after an update, say) is
    /// found all the same, by the debugging port the copy writes into the profile.
    /// </remarks>
    private async Task LaunchAsync(string exe, bool headless, CancellationToken ct)
    {
        var profile = _profile = Path.Combine(profileRoot, $"{Environment.ProcessId}-{Interlocked.Increment(ref _launches)}");
        Directory.CreateDirectory(profile);
        // Its output goes to pipes of its own (drained), so it doesn't hold on to BlackBox's console,
        // and it lives no longer than BlackBox.
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            WindowStyle = headless ? ProcessWindowStyle.Normal : ProcessWindowStyle.Hidden,
        };
        IEnumerable<string> arguments = headless
            ? ["--headless=new"]
            // Hidden windows are throttled like background tabs unless told otherwise; off the screen, should one show.
            : ["--window-position=-32000,-32000", "--window-size=1280,800", "--disable-backgrounding-occluded-windows",
               "--disable-renderer-backgrounding", "--disable-background-timer-throttling"];
        foreach (var arg in arguments.Concat(
                 [
                     "--disable-gpu", "--no-first-run", "--no-default-browser-check", "--disable-extensions",
                     "--disable-sync", "--mute-audio", "--do-not-de-elevate", "--remote-debugging-port=0", "--remote-allow-origins=*",
                     $"--user-data-dir={profile}", "about:blank",
                 ]))
            psi.ArgumentList.Add(arg);
        Process browser;
        try
        {
            browser = _browser = Process.Start(psi) ?? throw new ComfyApiException("The browser did not start.");
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new ComfyApiException($"The browser could not be started: {ex.Message}");
        }
        Platform.ChildProcesses.Tie(browser);
        var said = new StringBuilder();
        _ = KeepTailAsync(browser.StandardError, said);
        _ = browser.StandardOutput.ReadToEndAsync(CancellationToken.None);

        var waited = Stopwatch.StartNew();
        TimeSpan? quitAt = null;
        ActivePort? port;
        while ((port = ActivePort.Read(profile)) is null)
        {
            ct.ThrowIfCancellationRequested();
            if (quitAt is null && browser.HasExited)
            {
                if (browser.ExitCode != 0)
                    throw new ComfyApiException($"The browser closed at once (exit code {browser.ExitCode}).{Tail(said)}");
                // Quitting with 0 that soon, it handed over to a copy of itself: that one opens the port.
                quitAt = waited.Elapsed;
            }
            if (quitAt is not null && headless && Tail(said).Contains("Headless mode is disallowed", StringComparison.OrdinalIgnoreCase))
            {
                HeadlessRefused = true;
                throw new ComfyApiException($"The browser won't run headless on this PC (its HeadlessModeEnabled policy is off).{Tail(said)}");
            }
            if (quitAt is { } at && waited.Elapsed - at > TimeSpan.FromSeconds(10))
                throw new ComfyApiException($"The browser closed at once (exit code 0), and no copy of it opened its debugging port.{Tail(said)}");
            if (waited.Elapsed > TimeSpan.FromSeconds(30))
                throw new ComfyApiException($"The browser did not open its debugging port within 30 seconds.{Tail(said)}");
            await Task.Delay(50, ct).ConfigureAwait(false);
        }
        _port = port;
        _handedOver = browser.HasExited;
        // A copy isn't in BlackBox's job object, so it doesn't end with BlackBox by itself.
        if (_handedOver && Interlocked.Exchange(ref _exitHooked, 1) == 0)
            AppDomain.CurrentDomain.ProcessExit += (_, _) =>
            {
                if (_handedOver && _port is { } open)
                    open.CloseBrowserAsync().Wait(TimeSpan.FromSeconds(3));
            };

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            var targets = JsonNode.Parse(await http.GetStringAsync($"http://127.0.0.1:{port.Port}/json/list", ct).ConfigureAwait(false)) as JsonArray;
            var socketUrl = (string?)targets?.FirstOrDefault(t => (string?)t?["type"] == "page")?["webSocketDebuggerUrl"]
                            ?? throw new ComfyApiException("The browser offered no page to work in.");
            var socket = new ClientWebSocket();
            try
            {
                await socket.ConnectAsync(new Uri(socketUrl), ct).ConfigureAwait(false);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
            _socket = socket;
        }
        catch (Exception ex) when (ex is HttpRequestException or WebSocketException or System.Text.Json.JsonException || (ex is TaskCanceledException && !ct.IsCancellationRequested))
        {
            throw new ComfyApiException($"The browser's debugging port did not answer: {ex.Message}");
        }
    }

    /// <summary>The last lines the browser wrote to its error output, to go with a failure.</summary>
    private static string Tail(StringBuilder said)
    {
        string text;
        lock (said) text = said.ToString();
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return lines.Length == 0 ? "" : " It said: " + string.Join(" / ", lines.TakeLast(3)).Clip(400);
    }

    private static async Task KeepTailAsync(StreamReader output, StringBuilder said)
    {
        try
        {
            while (await output.ReadLineAsync().ConfigureAwait(false) is { } line)
                lock (said)
                {
                    said.AppendLine(line);
                    if (said.Length > 8192) said.Remove(0, said.Length - 4096);
                }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException) { }
    }

    private async Task<JsonNode?> EvaluateAsync(string expression, TimeSpan timeout, CancellationToken ct)
    {
        var reply = await CallAsync("Runtime.evaluate", new JsonObject
        {
            ["expression"] = expression,
            ["awaitPromise"] = true,
            ["returnByValue"] = true,
        }, timeout, ct).ConfigureAwait(false);
        if (reply["result"]?["exceptionDetails"] is JsonObject error)
        {
            var message = (string?)error["exception"]?["description"] ?? (string?)error["text"] ?? error.ToJsonString();
            throw new ComfyApiException("ComfyUI's page could not read the workflow: " + message.Split('\n')[0].Clip(400));
        }
        return reply["result"]?["result"]?["value"];
    }

    private async Task<JsonObject> CallAsync(string method, JsonObject parameters, TimeSpan timeout, CancellationToken ct)
    {
        var socket = _socket ?? throw new ComfyApiException("The browser isn't open.");
        var id = Interlocked.Increment(ref _nextId);
        var message = new JsonObject { ["id"] = id, ["method"] = method, ["params"] = parameters };
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            await socket.SendAsync(Encoding.UTF8.GetBytes(message.ToJsonString()), WebSocketMessageType.Text, true, cts.Token).ConfigureAwait(false);
            var buffer = new byte[64 * 1024];
            while (true)
            {
                using var received = new MemoryStream();
                WebSocketReceiveResult r;
                do
                {
                    r = await socket.ReceiveAsync(buffer, cts.Token).ConfigureAwait(false);
                    if (r.MessageType == WebSocketMessageType.Close)
                        throw new ComfyApiException("The browser closed its connection.");
                    received.Write(buffer, 0, r.Count);
                } while (!r.EndOfMessage);
                if (JsonNode.Parse(received.ToArray()) is JsonObject reply && (int?)reply["id"] == id)
                {
                    if (reply["error"] is JsonObject e)
                        throw new ComfyApiException($"The browser refused {method}: {(string?)e["message"]}");
                    return reply;
                }
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new ComfyApiException($"ComfyUI's page did not answer within {timeout.TotalSeconds:0} seconds.");
        }
        catch (WebSocketException ex)
        {
            throw new ComfyApiException("The connection to the browser broke: " + ex.Message);
        }
    }

    private async Task CloseAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await CloseBrowserAsync().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task CloseBrowserAsync()
    {
        try { _socket?.Dispose(); }
        catch (ObjectDisposedException) { }
        _socket = null;
        _page = null;
        // BlackBox's own child is ended with what it started (its crash reporter would hold on to the
        // profile for seconds after it); a copy it handed over to isn't BlackBox's child, and is asked
        // to close over its port.
        if (_browser is { } browser)
        {
            try
            {
                if (!browser.HasExited)
                {
                    browser.Kill(entireProcessTree: true);
                    using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    await browser.WaitForExitAsync(wait.Token).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or OperationCanceledException) { }
            browser.Dispose();
        }
        if (_handedOver && _port is { } port)
            await port.CloseBrowserAsync().ConfigureAwait(false);
        _browser = null;
        _port = null;
        _handedOver = false;
        if (_profile is { } profile)
            await DeleteAsync([profile]).ConfigureAwait(false);
        _profile = null;
    }

    /// <summary>
    /// Closes the browsers earlier starts left running, and deletes their profile folders: a copy a
    /// browser handed over to, or one a BlackBox that ended without closing it left behind. The
    /// folders of another BlackBox running now are left alone.
    /// </summary>
    private async Task CloseLeftoversAsync(CancellationToken ct)
    {
        if (!Directory.Exists(profileRoot))
            return;
        var closing = new List<Task>();
        var doomed = new List<string>();
        // Found by their command line: some left no port to close them by (the BlackBox before
        // deleted the file as it started the next browser on the shared profile).
        foreach (var stray in StrayBrowsers())
            closing.Add(EndAsync(stray));
        // Before every start had a folder of its own, they all used this one.
        if (ActivePort.Read(profileRoot) is { } shared)
            closing.Add(shared.CloseBrowserAsync());
        foreach (var entry in Directory.EnumerateFileSystemEntries(profileRoot))
        {
            if (LaunchFolder().Match(Path.GetFileName(entry)) is { Success: true } m)
            {
                if (OtherBlackBox(int.Parse(m.Groups[1].Value)))
                    continue;
                if (ActivePort.Read(entry) is { } left)
                    closing.Add(left.CloseBrowserAsync());
            }
            doomed.Add(entry);
        }
        await Task.WhenAll(closing).WaitAsync(ct).ConfigureAwait(false);
        await DeleteAsync(doomed).ConfigureAwait(false);
    }

    /// <summary>A start's profile folder: the BlackBox process's id and the start's number.</summary>
    [GeneratedRegex(@"^(\d+)-\d+$")]
    private static partial Regex LaunchFolder();

    /// <summary>
    /// Edge and Chrome browsers (not their helper processes) whose profile is the folder every start
    /// used to share, or a start's folder that isn't another BlackBox's running now.
    /// </summary>
    private List<Process> StrayBrowsers()
    {
        var strays = new List<Process>();
        string root;
        try { root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(profileRoot)); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return strays; }
        foreach (var process in new[] { "msedge", "chrome" }.SelectMany(Process.GetProcessesByName))
        {
            var stray = false;
            try
            {
                if (Platform.ProcessCommandLine.Of(process.Id) is { } line && !line.Contains("--type=", StringComparison.Ordinal) &&
                    Platform.ProcessCommandLine.Switch(line, "--user-data-dir") is { Length: > 0 } dir)
                {
                    var profile = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dir));
                    stray = string.Equals(profile, root, StringComparison.OrdinalIgnoreCase) ||
                            (string.Equals(Path.GetDirectoryName(profile), root, StringComparison.OrdinalIgnoreCase) &&
                             LaunchFolder().Match(Path.GetFileName(profile)) is { Success: true } m && !OtherBlackBox(int.Parse(m.Groups[1].Value)));
                }
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or InvalidOperationException) { }
            if (stray) strays.Add(process);
            else process.Dispose();
        }
        return strays;
    }

    /// <summary>Ends a stray browser with what it started, and waits a few seconds for it to go.</summary>
    private static async Task EndAsync(Process browser)
    {
        using (browser)
        {
            try
            {
                browser.Kill(entireProcessTree: true);
                using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await browser.WaitForExitAsync(wait.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or OperationCanceledException) { }
        }
    }

    /// <summary>Whether <paramref name="pid"/> is another BlackBox (app or command line) running now.</summary>
    private static bool OtherBlackBox(int pid)
    {
        if (pid == Environment.ProcessId)
            return false;
        try
        {
            using var process = Process.GetProcessById(pid);
            return process.ProcessName.StartsWith("BlackBox", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return false; // not running any more
        }
    }

    /// <summary>
    /// Deletes profile folders (and the files of the old shared one). A browser that has just closed
    /// lets go of its files over a second or two; what is still in use after that is left for the next start.
    /// </summary>
    private static async Task DeleteAsync(List<string> paths)
    {
        var waited = Stopwatch.StartNew();
        while (paths.RemoveAll(TryDelete) >= 0 && paths.Count > 0 && waited.Elapsed < TimeSpan.FromSeconds(3))
            await Task.Delay(200).ConfigureAwait(false);
    }

    private static bool TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
            else File.Delete(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public async ValueTask DisposeAsync()
    {
        _idle?.Dispose();
        await CloseBrowserAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// What a browser started with --remote-debugging-port=0 writes into its profile's
    /// DevToolsActivePort: its port, and the path of its own target, which tells it apart from
    /// anything else that may listen on that port later.
    /// </summary>
    private sealed record ActivePort(int Port, string BrowserPath)
    {
        public static ActivePort? Read(string profile)
        {
            try
            {
                using var stream = new FileStream(Path.Combine(profile, "DevToolsActivePort"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                var lines = new StreamReader(stream).ReadToEnd().Split('\n', StringSplitOptions.TrimEntries);
                // Both lines: a file read while it is written may hold part of the port only.
                return lines.Length >= 2 && int.TryParse(lines[0], out var port) && port > 0 && lines[1].StartsWith("/devtools/browser/", StringComparison.Ordinal)
                    ? new ActivePort(port, lines[1])
                    : null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }

        /// <summary>Asks the browser to close (Browser.close) and waits a few seconds for its port to go quiet. Nothing happens when it is gone already.</summary>
        public async Task CloseBrowserAsync()
        {
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(1) };
                var url = (string?)JsonNode.Parse(await http.GetStringAsync($"http://127.0.0.1:{Port}/json/version").ConfigureAwait(false))?["webSocketDebuggerUrl"];
                if (url is null || !url.EndsWith(BrowserPath, StringComparison.Ordinal))
                    return; // something else listens there now
                using (var socket = new ClientWebSocket())
                using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3)))
                {
                    await socket.ConnectAsync(new Uri(url), cts.Token).ConfigureAwait(false);
                    var close = new JsonObject { ["id"] = 1, ["method"] = "Browser.close" };
                    await socket.SendAsync(Encoding.UTF8.GetBytes(close.ToJsonString()), WebSocketMessageType.Text, true, cts.Token).ConfigureAwait(false);
                }
                for (var i = 0; i < 40; i++)
                {
                    await Task.Delay(100).ConfigureAwait(false);
                    await http.GetStringAsync($"http://127.0.0.1:{Port}/json/version").ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException or WebSocketException
                                           or System.Text.Json.JsonException or UriFormatException or InvalidOperationException)
            {
                // Gone, or never there.
            }
        }
    }
}
