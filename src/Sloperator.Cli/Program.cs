using Sloperator.Core.Agent;
using Sloperator.Core.Llm;
using Sloperator.Core.Mcp;
using Sloperator.Core.Platform;
using Sloperator.Core.Settings;
using Sloperator.Core.Tools;

// Usage: sloperator-cli [--root DIR] [--url URL] [--model ID] [--effort LEVEL] [--yes] [--save-shots DIR] [--full-results] [--attach FILE]... [--then TEXT]... [--no-index] "task"
//   --root        portable folder (default: SLOPERATOR_HOME, else .dev/ when run from the repository, else the exe folder);
//                 its Data/mcp.json lists the MCP servers to start, as in the app
//   --url/--model override the server address and model saved in Data/settings.json; with no model, or one the
//                 server doesn't offer, the strongest model it offers is used
//   --effort      how hard the model thinks: low, medium, high, xhigh or max (saved: medium), for a model that takes it
//   --yes         approve risky tools without asking
//   --save-shots  write every screenshot the model sees to DIR
//   --full-results  log each tool result whole, not just its start and end
//   --attach     send a file with the task, as one dropped on the app's window (once per file)
//   --then        a later message of the same chat, sent when the run before it is over (once per message, in
//                 order); "@N text" is typed N seconds into that run instead, as a message sent while the agent
//                 works, so a recorded chat can be played again
//   --no-index    leave index_folder out (the tool, its prompt line and its note), to measure what the index changes

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
var effortOverride = Take("--effort");
var autoYes = Flag("--yes");
var fullResults = Flag("--full-results");
var noIndex = Flag("--no-index");
var attached = new List<string>();
while (Take("--attach") is { } file)
    attached.Add(Path.GetFullPath(file));
var followUps = new Queue<string>();
while (Take("--then") is { } later)
    followUps.Enqueue(later);
var task = string.Join(' ', argsList);
if (string.IsNullOrWhiteSpace(task))
{
    Console.Error.WriteLine("Usage: sloperator-cli [--root DIR] [--url URL] [--model ID] [--effort LEVEL] [--yes] [--save-shots DIR] [--attach FILE]... [--then TEXT]... [--no-index] \"task\"");
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
IPlatformServices platform = new Sloperator.Platform.Windows.WindowsPlatform();
#else
IPlatformServices platform = new UnsupportedPlatform();
#endif
using var http = LlmHttp.Create(Timeout.InfiniteTimeSpan);
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
using var stopKey = platform.StopKey.Watch(() =>
{
    if (cts.IsCancellationRequested) return false;
    // Off the hook's thread, which holds up every key press on the PC: what the stop sets off, and
    // the line, which waits while text is selected in the console.
    var pressed = DateTime.Now;
    _ = cts.CancelAsync();
    _ = Task.Run(() => Console.WriteLine($"\n[stop] {pressed:HH:mm:ss} Pause/Break pressed"));
    return true;
});

var agent = settings.Agent;
platform.InputOverlay.Duration = TimeSpan.FromSeconds(AgentSettings.InputLogSeconds);
platform.InputOverlay.Enabled = true;
if (urlOverride is not null) settings.Server.BaseUrl = urlOverride;
if (modelOverride is not null) settings.Server.Model = modelOverride;
if (effortOverride is not null) settings.Server.Effort = effortOverride.Trim().ToLowerInvariant();
using (var probeHttp = LlmHttp.Create(TimeSpan.FromMinutes(10))) // loading a model can take a while
try
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
                ? $"[server] no model picked: using {probe.Model}, the free model on Kilo that clicked best in Sloperator's tests"
                : OpenAiChatClient.IsAnthropic(probe.BaseUrl)
                    ? $"[server] no model picked: using {probe.Model}" + (probe.Model == AnthropicMessages.DefaultModel ? ", Anthropic's recommended model" : ", the newest Opus, Sonnet or Haiku the key may use")
                    : $"[server] no model picked: using {probe.Model}{details}, the strongest the server offers");
    }
    settings.Server.SupportsVision = probe.Vision ?? true;
    settings.Server.EffortLevels = probe.Efforts ?? [];
    var facts = await ServerProbe.EnsureLoadedAsync(probeHttp, settings.Server, () => Console.WriteLine("[server] loading the model…"), cts.Token,
        note => Console.WriteLine("[server] " + note), known: probe.Model == settings.Server.ModelInUse ? probe.Facts : null);
    if (facts.Context is { } c)
        settings.Server.SetDetectedContext(c, facts.Hosted);
    Console.WriteLine($"[server] {settings.Server.BaseUrl} · {settings.Server.ModelInUse} · {ContextSizes.Format(settings.Server.ContextSize)} context"
        + (settings.Server.AutoContext ? "" : " (set by hand)")
        + (settings.Server.EffortInUse is { } effort ? $" · {effort} effort" : " · no effort setting"));
}
catch (OperationCanceledException) when (cts.IsCancellationRequested)
{
    // Stopped while the model loaded, which can take minutes: a stack trace said so before.
    Console.WriteLine("[server] stopped while connecting to the server");
    return 1;
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

using var ssh = new Sloperator.Core.Ssh.SshHosts(paths.KnownHostsFile);
var session = AgentSetup.Create(BackendConnection.Create(http, settings.Server), platform, agent, paths, mcp, ssh, folderIndex: !noIndex);
var observer = new ConsoleObserver(autoYes, shotsDir, fullResults);

var t0 = DateTime.UtcNow;
if (attached.Count > 0)
    Console.WriteLine($"[attach] {string.Join(" | ", attached)}");
var input = new UserInput(task, attached);
for (var turn = 1; ; turn++)
{
    // The messages typed while this run goes on ("@N text"), each N seconds in. One the run ends
    // before is sent after it, as the app sends a message that came too late for the run.
    using var runEnded = new CancellationTokenSource();
    var typedMeanwhile = new List<Task<UserInput?>>();
    while (followUps.TryPeek(out var next) && next.StartsWith('@') && next.IndexOf(' ') is > 1 and var space
        && int.TryParse(next[1..space], out var seconds))
    {
        followUps.Dequeue();
        var text = next[(space + 1)..];
        typedMeanwhile.Add(Task.Run(async () =>
        {
            try { await Task.Delay(TimeSpan.FromSeconds(seconds), runEnded.Token); }
            catch (OperationCanceledException) { return new UserInput(text); }
            Console.WriteLine($"\n[user] {DateTime.Now:HH:mm:ss} (while the agent works) {text}");
            session.Steer(text);
            return (UserInput?)null;
        }));
    }
    var turnStart = DateTime.UtcNow;
    await session.RunAsync(input, observer, cts.Token);
    runEnded.Cancel();
    var notTyped = (await Task.WhenAll(typedMeanwhile)).OfType<UserInput>();
    var late = session.TakeUndelivered();
    late.AddRange(notTyped);
    Console.WriteLine($"\n[turn {turn}] {observer.Reason} in {(DateTime.UtcNow - turnStart).TotalSeconds:0}s");
    if (cts.IsCancellationRequested || observer.Reason != AgentStopReason.Completed)
        break;
    if (late.Count > 0)
        input = UserInput.Join(late);
    else if (followUps.TryDequeue(out var then))
        input = new UserInput(then);
    else
        break;
    Console.WriteLine($"\n[user] {DateTime.Now:HH:mm:ss} {input.Text}");
}
Console.WriteLine($"\n[done] {observer.Reason} in {(DateTime.UtcNow - t0).TotalSeconds:0}s, {observer.Steps} model calls, {observer.ToolCalls} tool calls, {observer.ToolErrors} tool errors");
if (observer.Detail is { } d) Console.WriteLine("[done] " + d);
// A ComfyUI the task started is closed after its jobs, which may still be running.
if (Sloperator.Core.Comfy.ComfyHub.For(paths.ComfyDir).Closing is { IsCompleted: false } closing)
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
internal sealed class ConsoleObserver(bool autoYes, string? shotsDir, bool fullResults) : IAgentObserver
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
        if (step > 0) Steps++; // over all the runs of a chat played with --then
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
        // The counts also for a reply of tool calls alone, which streams no text: a benchmark adding
        // up the tokens missed those steps.
        var tokens = message.PromptTokens is { } p ? $" ({p / 1000.0:0.0}k prompt tokens, {message.CompletionTokens} reply tokens)" : "";
        Console.WriteLine(_firstToken is { } first
            ? $"[time] {Now} server read the prompt in {(first - _requestStart).TotalSeconds:0.0}s, wrote the reply in {(done - first).TotalSeconds:0.0}s" + tokens
            : message.HasToolCalls
                ? $"[time] {Now} tool calls alone after {(done - _requestStart).TotalSeconds:0.0}s" + tokens
                : $"[time] {Now} no reply after {(done - _requestStart).TotalSeconds:0.0}s");
        foreach (var c in message.ToolCalls ?? [])
            Console.WriteLine($"[call] {Now} {c.Name} {c.Arguments}{(c.Id.StartsWith("text_") ? "   (recovered from text)" : "")}");
    }

    public async Task<bool> RequestApprovalAsync(ToolCall call, Tool tool, string summary, CancellationToken ct)
    {
        if (autoYes) return true;
        Console.Write($"[approve?] {summary} [y/N] ");
        // Pause/Break or Ctrl+C stops the run while it asks; the run waited for Enter first.
        var answer = await Task.Run(Console.ReadLine, CancellationToken.None).WaitAsync(ct);
        return answer?.Trim().ToLowerInvariant() is "y" or "yes";
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
        // Without its numbers: a download's "4.19 of 16.06 GB, 13.8 MB/s" changes every second and
        // wrote a line each time, a thousand lines for a 16 GB model.
        var stage = System.Text.RegularExpressions.Regex.Replace(text.Split(" · ")[0].Split(", step")[0], @"[\d.:]+", "#");
        if (DateTime.Now - _lastProgress < TimeSpan.FromSeconds(15) && stage == _progressStage)
            return;
        (_lastProgress, _progressStage) = (DateTime.Now, stage);
        Console.WriteLine($"[progress] {Now} {text}");
    }

    public void OnToolFinished(ToolCall call, ToolResult result)
    {
        if (result.IsError) ToolErrors++;
        // The start and the end: an error or an exit code is usually at the end.
        var text = result.Text.Length > 1600 && !fullResults ? result.Text[..500] + " … " + result.Text[^1100..] : result.Text;
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
