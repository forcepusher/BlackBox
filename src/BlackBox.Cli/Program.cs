using BlackBox.Core.Agent;
using BlackBox.Core.Llm;
using BlackBox.Core.Mcp;
using BlackBox.Core.Platform;
using BlackBox.Core.Settings;
using BlackBox.Core.Tools;

// Usage: blackbox-cli [--root DIR] [--url URL] [--model ID] [--yes] [--save-shots DIR] [--attach FILE]... "task"
//   --root        portable folder (default: BLACKBOX_HOME, else .dev/ when run from the repository, else the exe folder);
//                 its Data/mcp.json lists the MCP servers to start, as in the app
//   --url/--model override the server address and model saved in Data/settings.json; with no model, or one the
//                 server doesn't offer, the strongest model it offers is used
//   --yes         approve risky tools without asking
//   --save-shots  write every screenshot the model sees to DIR
//   --attach      send a file with the task, as one dropped on the app's window (once per file)

// UTF-8 also when redirected to a log file, so scripts can show the log as it is written.
Console.OutputEncoding = System.Text.Encoding.UTF8;
var argsList = args.ToList();
string? Take(string flag)
{
    var i = argsList.IndexOf(flag);
    if (i < 0 || i + 1 >= argsList.Count) return null;
    var v = argsList[i + 1];
    argsList.RemoveRange(i, 2);
    return v;
}
bool Flag(string flag) => argsList.Remove(flag);

var root = Take("--root") ?? AppPaths.FindRoot(useRepositoryDevFolder: true);
var shotsDir = Take("--save-shots");
var urlOverride = Take("--url");
var modelOverride = Take("--model");
var autoYes = Flag("--yes");
var attached = new List<string>();
while (Take("--attach") is { } file)
    attached.Add(Path.GetFullPath(file));
var task = string.Join(' ', argsList);
if (string.IsNullOrWhiteSpace(task))
{
    Console.Error.WriteLine("Usage: blackbox-cli [--root DIR] [--url URL] [--model ID] [--yes] [--save-shots DIR] [--attach FILE]... \"task\"");
    return 2;
}
if (attached.FirstOrDefault(f => !File.Exists(f) && !Directory.Exists(f)) is { } missing)
{
    Console.Error.WriteLine($"[attach] there is no {missing}");
    return 2;
}

var paths = new AppPaths(root);
paths.EnsureCreated();
var settings = new SettingsStore(paths).Load();
#if WINDOWS
IPlatformServices platform = new BlackBox.Platform.Windows.WindowsPlatform();
#else
IPlatformServices platform = new UnsupportedPlatform();
#endif
using var http = LlmHttp.Create(Timeout.InfiniteTimeSpan);
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
using var stopKey = platform.StopKey.Watch(() =>
{
    if (cts.IsCancellationRequested) return false;
    Console.WriteLine($"\n[stop] {DateTime.Now:HH:mm:ss} Pause/Break pressed");
    cts.Cancel();
    return true;
});

var agent = settings.Agent;
platform.InputOverlay.Duration = TimeSpan.FromSeconds(AgentSettings.InputLogSeconds);
platform.InputOverlay.Enabled = true;
if (urlOverride is not null) settings.Server.BaseUrl = urlOverride;
if (modelOverride is not null) settings.Server.Model = modelOverride;
using (var probeHttp = LlmHttp.Create(TimeSpan.FromMinutes(10))) // loading a model can take a while
{
    var probe = await ServerProbe.CheckAsync(probeHttp, settings.Server, cts.Token);
    if (!probe.Reachable)
    {
        Console.Error.WriteLine("[server] " + probe.Message);
        return 1;
    }
    settings.Server.BaseUrl = probe.BaseUrl!;
    if (probe.Model is null)
    {
        Console.Error.WriteLine("[server] the server offers no model to chat with");
        return 1;
    }
    if (probe.AutoPicked)
    {
        // As in the app: the strongest model the server offers, until the picked one is back.
        settings.Server.AutoModel = probe.Model;
        var details = probe.ModelDetails is { } known ? $" ({known})" : "";
        Console.WriteLine(settings.Server.Model.Length > 0
            ? $"[server] the server doesn't offer {settings.Server.Model}: using {probe.Model}{details}, the strongest it offers"
            : probe.Model == KiloGateway.Model && KiloGateway.Is(probe.BaseUrl)
                ? $"[server] no model picked: using {probe.Model}, the free model on Kilo that clicked best in BlackBox's tests"
                : $"[server] no model picked: using {probe.Model}{details}, the strongest the server offers");
    }
    settings.Server.SupportsVision = probe.Vision ?? true;
    var facts = await ServerProbe.EnsureLoadedAsync(probeHttp, settings.Server, () => Console.WriteLine("[server] loading the model…"), cts.Token);
    if (facts.Context is { } c)
        settings.Server.SetDetectedContext(c, facts.Hosted);
    Console.WriteLine($"[server] {settings.Server.BaseUrl} · {settings.Server.ModelInUse} · {ContextSizes.Format(settings.Server.ContextSize)} context"
        + (settings.Server.AutoContext ? "" : " (set by hand)"));
}
// MCP servers from Data/mcp.json, as in the app; a broken list is reported and skipped.
await using var mcp = new McpHost();
try
{
    var mcpServers = McpConfig.Parse(McpConfig.Load(paths.McpConfigFile)).Servers;
    if (mcpServers.Count > 0)
    {
        Console.WriteLine($"[mcp] starting {mcpServers.Count} server(s)…");
        await mcp.ConnectAsync(mcpServers, cts.Token);
        foreach (var status in mcp.Statuses)
            Console.WriteLine("[mcp] " + status.Summary);
    }
}
catch (McpConfigException ex)
{
    Console.Error.WriteLine($"[mcp] {paths.McpConfigFile} could not be read, no MCP tools: {ex.Message}");
}
catch (OperationCanceledException)
{
    Console.WriteLine("[mcp] stopped while the servers were starting");
    return 1;
}

using var ssh = new BlackBox.Core.Ssh.SshHosts(paths.KnownHostsFile);
var session = AgentSetup.Create(BackendConnection.Create(http, settings.Server), platform, agent, paths, mcp, ssh);
var observer = new ConsoleObserver(autoYes, shotsDir);

var t0 = DateTime.UtcNow;
if (attached.Count > 0)
    Console.WriteLine($"[attach] {string.Join(" | ", attached)}");
await session.RunAsync(new UserInput(task, attached), observer, cts.Token);
Console.WriteLine($"\n[done] {observer.Reason} in {(DateTime.UtcNow - t0).TotalSeconds:0}s, {observer.Steps} model calls, {observer.ToolCalls} tool calls, {observer.ToolErrors} tool errors");
if (observer.Detail is { } d) Console.WriteLine("[done] " + d);
// A ComfyUI the task started is closed after its jobs, which may still be running.
if (BlackBox.Core.Comfy.ComfyHub.For(paths.ComfyDir).Closing is { IsCompleted: false } closing)
{
    Console.WriteLine("[comfyui] closing ComfyUI once the jobs in its queue are done…");
    try { await closing.WaitAsync(cts.Token); }
    catch (OperationCanceledException) { }
}
return observer.Reason == AgentStopReason.Completed ? 0 : 1;

/// <summary>
/// Prints the run as it happens. Every line carries the time, so a log
/// shows where the seconds go between steps: waiting for the server, writing the reply, tools.
/// </summary>
internal sealed class ConsoleObserver(bool autoYes, string? shotsDir) : IAgentObserver
{
    private StreamChannel? _channel;
    private int _shot;
    private DateTime _requestStart = DateTime.Now;
    private DateTime? _firstToken;
    private DateTime _toolStart;

    public int Steps { get; private set; }
    public int ToolCalls { get; private set; }
    public int ToolErrors { get; private set; }
    public AgentStopReason Reason { get; private set; }
    public string? Detail { get; private set; }

    private static string Now => DateTime.Now.ToString("HH:mm:ss");

    public void OnStepStarted(int step)
    {
        if (step > 0) Steps = step;
        _channel = null;
        _requestStart = DateTime.Now;
        _firstToken = null;
        Console.WriteLine($"\n── step {step} · {Now} ──");
    }

    public void OnDelta(StreamDelta delta)
    {
        _firstToken ??= DateTime.Now;
        if (_channel != delta.Channel)
        {
            Console.Write($"\n{(delta.Channel == StreamChannel.Reasoning ? "[think]" : "[say]")} {Now} ");
            _channel = delta.Channel;
        }
        if (delta.Channel == StreamChannel.Reasoning) Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.Write(delta.Text);
        Console.ResetColor();
    }

    public void OnAssistantMessage(ChatMessage message)
    {
        Console.WriteLine();
        var done = DateTime.Now;
        Console.WriteLine(_firstToken is { } first
            ? $"[time] {Now} server read the prompt in {(first - _requestStart).TotalSeconds:0.0}s, wrote the reply in {(done - first).TotalSeconds:0.0}s" +
              (message.PromptTokens is { } p ? $" ({p / 1000.0:0.0}k prompt tokens, {message.CompletionTokens} reply tokens)" : "")
            : $"[time] {Now} no reply after {(done - _requestStart).TotalSeconds:0.0}s");
        foreach (var c in message.ToolCalls ?? [])
            Console.WriteLine($"[call] {Now} {c.Name} {c.Arguments}{(c.Id.StartsWith("text_") ? "   (recovered from text)" : "")}");
    }

    public Task<bool> RequestApprovalAsync(ToolCall call, Tool tool, string summary, CancellationToken ct)
    {
        if (autoYes) return Task.FromResult(true);
        Console.Write($"[approve?] {summary} [y/N] ");
        return Task.FromResult(Console.ReadLine()?.Trim().ToLowerInvariant() is "y" or "yes");
    }

    public void OnNotice(string text)
    {
        Console.WriteLine($"\n[notice] {Now} {text}");
        _requestStart = DateTime.Now; // a resend after a drop starts a new wait
        _firstToken = null;
    }

    public void OnToolStarted(ToolCall call, string summary)
    {
        ToolCalls++;
        _toolStart = DateTime.Now;
        _lastProgress = DateTime.MinValue;
        Console.WriteLine($"[tool] {Now} {summary}");
    }

    private DateTime _lastProgress;
    private string? _progressStage;

    /// <summary>A long call's progress, every 15 seconds or when it moves on to another stage.</summary>
    public void OnToolProgress(ToolCall call, string text)
    {
        var stage = text.Split(" · ")[0].Split(", step")[0];
        if (DateTime.Now - _lastProgress < TimeSpan.FromSeconds(15) && stage == _progressStage)
            return;
        (_lastProgress, _progressStage) = (DateTime.Now, stage);
        Console.WriteLine($"[progress] {Now} {text}");
    }

    public void OnToolFinished(ToolCall call, ToolResult result)
    {
        if (result.IsError) ToolErrors++;
        // The start and the end: an error or an exit code is usually at the end.
        var text = result.Text.Length > 1600 ? result.Text[..500] + " … " + result.Text[^1100..] : result.Text;
        Console.ForegroundColor = result.IsError ? ConsoleColor.Red : ConsoleColor.DarkCyan;
        Console.WriteLine($"[result] {Now} after {(DateTime.Now - _toolStart).TotalSeconds:0.0}s: {text.TrimEnd()}");
        Console.ResetColor();
        if (result.Files is { Count: > 0 } files)
            Console.WriteLine($"[files] {string.Join(" | ", files)}");
        if (result.Image is not null && shotsDir is not null)
        {
            Directory.CreateDirectory(shotsDir);
            File.WriteAllBytes(Path.Combine(shotsDir, $"shot-{++_shot:000}.jpg"), result.Image);
        }
    }

    /// <summary>A bug ended the run: its stack trace, which the one-line message leaves out.</summary>
    public void OnFailed(Exception error) => Console.Error.WriteLine($"\n[failed] {Now} {error}");

    public void OnFinished(AgentStopReason reason, string? detail)
    {
        Reason = reason;
        Detail = detail;
    }
}
