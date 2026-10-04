using BlackBox.Core.Agent;
using BlackBox.Core.Llm;
using BlackBox.Core.Mcp;
using BlackBox.Core.Platform;
using BlackBox.Core.Settings;
using BlackBox.Core.Tools;

// Usage: blackbox-cli [--root DIR] [--url URL] [--model ID] [--yes] [--save-shots DIR] "task"
//   --root        portable folder (default: BLACKBOX_HOME, else .dev/ when run from the repository, else the exe folder);
//                 its Data/mcp.json lists the MCP servers to start, as in the app
//   --url/--model override the server address and model saved in Data/settings.json
//   --yes         approve risky tools without asking
//   --save-shots  write every screenshot the model sees to DIR

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
var task = string.Join(' ', argsList);
if (string.IsNullOrWhiteSpace(task))
{
    Console.Error.WriteLine("Usage: blackbox-cli [--root DIR] [--url URL] [--model ID] [--yes] [--save-shots DIR] \"task\"");
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
    Console.WriteLine($"\n[stop] {DateTime.Now:HH:mm:ss} Break key pressed");
    cts.Cancel();
    return true;
});

var agent = settings.Agent;
platform.InputOverlay.Duration = TimeSpan.FromSeconds(agent.InputLogSeconds);
platform.InputOverlay.Enabled = agent.ShowInputs;
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
    settings.Server.SupportsVision = probe.Vision ?? true;
    if (await ServerProbe.EnsureLoadedAsync(probeHttp, settings.Server, () => Console.WriteLine("[server] loading the model…"), cts.Token) is { } c)
        settings.Server.SetDetectedContext(c);
    Console.WriteLine($"[server] {settings.Server.BaseUrl} · {settings.Server.Model} · {ContextSizes.Format(settings.Server.ContextSize)} context"
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

var backend = BackendConnection.Create(http, settings.Server);
var workDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
var ctx = new ToolContext { Platform = platform, Settings = agent, WorkingDirectory = workDir };
var screens = backend.Vision ? ctx.ScreenSpaces() : null;
var registry = ToolRegistry.CreateDefault(includeScreenTools: screens is not null, includeUiAutomation: platform.Automation is not null, extra: mcp.Tools);
var session = new AgentSession(backend.Client, backend.Options, registry, ctx, SystemPrompt.Build(platform.Name, screens, workDir, agent.CustomInstructions, mcp.Connected), contextTokens: backend.ContextTokens);
var observer = new ConsoleObserver(autoYes, shotsDir);

var t0 = DateTime.UtcNow;
await session.RunAsync(task, observer, cts.Token);
Console.WriteLine($"\n[done] {observer.Reason} in {(DateTime.UtcNow - t0).TotalSeconds:0}s, {observer.Steps} model calls, {observer.ToolCalls} tool calls, {observer.ToolErrors} tool errors");
if (observer.Detail is { } d) Console.WriteLine("[done] " + d);
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
        Console.WriteLine($"[tool] {Now} {summary}");
    }

    public void OnToolFinished(ToolCall call, ToolResult result)
    {
        if (result.IsError) ToolErrors++;
        var text = result.Text.Length > 600 ? result.Text[..600] + "…" : result.Text;
        Console.ForegroundColor = result.IsError ? ConsoleColor.Red : ConsoleColor.DarkCyan;
        Console.WriteLine($"[result] {Now} after {(DateTime.Now - _toolStart).TotalSeconds:0.0}s: {text.TrimEnd()}");
        Console.ResetColor();
        if (result.Image is not null && shotsDir is not null)
        {
            Directory.CreateDirectory(shotsDir);
            File.WriteAllBytes(Path.Combine(shotsDir, $"shot-{++_shot:000}.jpg"), result.Image);
        }
    }

    public void OnFinished(AgentStopReason reason, string? detail)
    {
        Reason = reason;
        Detail = detail;
    }
}
