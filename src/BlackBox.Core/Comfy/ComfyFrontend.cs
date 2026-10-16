using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;

namespace BlackBox.Core.Comfy;

/// <summary>
/// ComfyUI's own web page, loaded in the PC's browser run headless, to turn a workflow as the
/// editor saves it (nodes, links, subgraphs, bypassed nodes, reroutes, promoted widgets) into the
/// API format /prompt runs: exactly what the page sends when the user presses Run. Templates are
/// kept in the editor's format, and their features change with every release of the page, so no
/// copy of its conversion could keep up. The browser stays up for a few minutes between uses.
/// </summary>
public sealed class ComfyFrontend(string profileDirectory) : IAsyncDisposable
{
    private static readonly TimeSpan IdleTime = TimeSpan.FromMinutes(3);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private Process? _browser;
    private ClientWebSocket? _socket;
    private Uri? _page;
    private int _generation = -1;
    private int _nextId;
    private Timer? _idle;

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
            if (_socket is not { State: WebSocketState.Open } || _browser is not { HasExited: false } || _page != server || _generation != generation)
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
        var exe = Web.HeadlessBrowser.FindExecutable()
                  ?? throw new ComfyApiException("Turning a saved workflow into one ComfyUI can run needs Edge or Chrome, and neither is on this PC.");
        Directory.CreateDirectory(profileDirectory);
        var portFile = Path.Combine(profileDirectory, "DevToolsActivePort");
        try { File.Delete(portFile); }
        catch (IOException) { }
        // Its output goes to pipes of its own (drained), so it doesn't hold on to BlackBox's console,
        // and it lives no longer than BlackBox.
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[]
                 {
                     "--headless=new", "--disable-gpu", "--no-first-run", "--no-default-browser-check", "--disable-extensions",
                     "--disable-sync", "--mute-audio", "--remote-debugging-port=0", "--remote-allow-origins=*", $"--user-data-dir={profileDirectory}", "about:blank",
                 })
            psi.ArgumentList.Add(arg);
        _browser = Process.Start(psi) ?? throw new ComfyApiException("The browser did not start.");
        Platform.ChildProcesses.Tie(_browser);
        _ = _browser.StandardOutput.ReadToEndAsync(CancellationToken.None);
        _ = _browser.StandardError.ReadToEndAsync(CancellationToken.None);

        int port = 0;
        var waited = Stopwatch.StartNew();
        while (port == 0)
        {
            ct.ThrowIfCancellationRequested();
            if (_browser.HasExited)
                throw new ComfyApiException($"The browser closed at once (exit code {_browser.ExitCode}).");
            if (waited.Elapsed > TimeSpan.FromSeconds(30))
                throw new ComfyApiException("The browser did not open its debugging port within 30 seconds.");
            try
            {
                var lines = await File.ReadAllLinesAsync(portFile, ct).ConfigureAwait(false);
                if (lines.Length > 0 && int.TryParse(lines[0], out var p)) port = p;
            }
            catch (IOException) { }
            if (port == 0) await Task.Delay(50, ct).ConfigureAwait(false);
        }

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        var targets = JsonNode.Parse(await http.GetStringAsync($"http://127.0.0.1:{port}/json/list", ct).ConfigureAwait(false)) as JsonArray;
        var socketUrl = (string?)targets?.FirstOrDefault(t => (string?)t?["type"] == "page")?["webSocketDebuggerUrl"]
                        ?? throw new ComfyApiException("The browser offered no page to work in.");
        _socket = new ClientWebSocket();
        await _socket.ConnectAsync(new Uri(socketUrl), ct).ConfigureAwait(false);

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

    private Task CloseBrowserAsync()
    {
        try { _socket?.Dispose(); }
        catch (ObjectDisposedException) { }
        _socket = null;
        _page = null;
        if (_browser is { } browser)
        {
            try { if (!browser.HasExited) browser.Kill(entireProcessTree: true); }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            browser.Dispose();
        }
        _browser = null;
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        _idle?.Dispose();
        await CloseBrowserAsync().ConfigureAwait(false);
    }
}
