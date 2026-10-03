using BlackBox.Core.Agent;
using BlackBox.Core.Llm;
using BlackBox.Core.Platform;
using BlackBox.Core.Settings;
using BlackBox.Core.Tools;

// Usage: blackbox-cli [--root DIR] [--url URL] [--model ID] [--yes] [--save-shots DIR] "task"
//   --root        portable folder (default: BLACKBOX_HOME, else .dev/ in the repo for dev builds, else the exe folder)
//   --url/--model override the server address and model saved in Data/settings.json
//   --yes         approve risky tools without asking
//   --save-shots  write every screenshot the model sees to DIR

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

var root = Take("--root") ?? Environment.GetEnvironmentVariable("BLACKBOX_HOME") ?? FindRoot();
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

var agent = settings.Agent;
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
    if (await ServerProbe.EnsureLoadedAsync(probeHttp, settings.Server, () => Console.WriteLine("[server] loading the model…"), cts.Token) is { } c)
        settings.Server.ContextSize = c;
    Console.WriteLine($"[server] {settings.Server.BaseUrl} · {settings.Server.Model} · {settings.Server.ContextSize / 1024}k context");
}
var backend = BackendConnection.Create(http, settings.Server, agent);
var workDir = string.IsNullOrWhiteSpace(agent.WorkingDirectory) ? paths.WorkspaceDir : paths.Resolve(agent.WorkingDirectory);
var ctx = new ToolContext { Platform = platform, Settings = agent, WorkingDirectory = workDir };
var screen = backend.Vision ? ctx.GetScreenSpace() : null;
var registry = ToolRegistry.CreateDefault(includeScreenTools: screen is not null, includeUiAutomation: platform.Automation is not null);
var session = new AgentSession(backend.Client, backend.Options, registry, ctx, SystemPrompt.Build(platform.Name, screen, workDir, agent.CustomInstructions), contextTokens: backend.ContextTokens);
var observer = new ConsoleObserver(autoYes, shotsDir);

var t0 = DateTime.UtcNow;
await session.RunAsync(task, observer, cts.Token);
Console.WriteLine($"\n[done] {observer.Reason} in {(DateTime.UtcNow - t0).TotalSeconds:0}s, {observer.Steps} model calls, {observer.ToolCalls} tool calls, {observer.ToolErrors} tool errors");
if (observer.Detail is { } d) Console.WriteLine("[done] " + d);
return observer.Reason == AgentStopReason.Completed ? 0 : 1;

static string FindRoot()
{
    for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        if (dir.EnumerateFiles("BlackBox.sln*").Any())
            return Path.Combine(dir.FullName, ".dev"); // same dev folder as the debug app
    return AppContext.BaseDirectory;
}

internal sealed class ConsoleObserver(bool autoYes, string? shotsDir) : IAgentObserver
{
    private StreamChannel? _channel;
    private int _shot;

    public int Steps { get; private set; }
    public int ToolCalls { get; private set; }
    public int ToolErrors { get; private set; }
    public AgentStopReason Reason { get; private set; }
    public string? Detail { get; private set; }

    public void OnStepStarted(int step)
    {
        Steps = step;
        _channel = null;
        Console.WriteLine($"\n── step {step} ──");
    }

    public void OnDelta(StreamDelta delta)
    {
        if (_channel != delta.Channel)
        {
            Console.Write(delta.Channel == StreamChannel.Reasoning ? "\n[think] " : "\n[say] ");
            _channel = delta.Channel;
        }
        if (delta.Channel == StreamChannel.Reasoning) Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.Write(delta.Text);
        Console.ResetColor();
    }

    public void OnAssistantMessage(ChatMessage message)
    {
        Console.WriteLine();
        foreach (var c in message.ToolCalls ?? [])
            Console.WriteLine($"[call] {c.Name} {c.Arguments}{(c.Id.StartsWith("text_") ? "   (recovered from text)" : "")}");
    }

    public Task<bool> RequestApprovalAsync(ToolCall call, Tool tool, string summary, CancellationToken ct)
    {
        if (autoYes) return Task.FromResult(true);
        Console.Write($"[approve?] {summary} [y/N] ");
        return Task.FromResult(Console.ReadLine()?.Trim().ToLowerInvariant() is "y" or "yes");
    }

    public void OnNotice(string text) => Console.WriteLine($"\n[notice] {text}");

    public void OnToolStarted(ToolCall call, string summary)
    {
        ToolCalls++;
        Console.WriteLine($"[tool] {summary}");
    }

    public void OnToolFinished(ToolCall call, ToolResult result)
    {
        if (result.IsError) ToolErrors++;
        var text = result.Text.Length > 600 ? result.Text[..600] + "…" : result.Text;
        Console.ForegroundColor = result.IsError ? ConsoleColor.Red : ConsoleColor.DarkCyan;
        Console.WriteLine($"[result] {text.TrimEnd()}");
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
