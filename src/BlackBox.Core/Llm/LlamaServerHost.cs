using System.Diagnostics;
using System.Globalization;
using BlackBox.Core.Platform;
using BlackBox.Core.Settings;

namespace BlackBox.Core.Llm;

public enum ServerState { Stopped, Starting, Running, Failed }

/// <summary>
/// Starts, monitors and stops a llama.cpp <c>llama-server</c> process and exposes its
/// OpenAI-compatible endpoint.
/// </summary>
public sealed class LlamaServerHost : IAsyncDisposable
{
    private readonly AppPaths _paths;
    private readonly IPlatformServices _platform;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(5) };
    private readonly List<string> _recentLog = [];
    private int _logLines;
    private Process? _process;
    private StreamWriter? _logFile;

    public LlamaServerHost(AppPaths paths, IPlatformServices platform)
    {
        _paths = paths;
        _platform = platform;
    }

    public ServerState State { get; private set; } = ServerState.Stopped;
    public string? LastError { get; private set; }

    /// <summary>Non-fatal problem from the last start, e.g. a missing vision projector.</summary>
    public string? Warning { get; private set; }
    public string BaseUrl { get; private set; } = "";
    public bool HasVision { get; private set; }

    public event Action<ServerState>? StateChanged;
    public event Action<string>? LogLine;

    public string RecentLog
    {
        get { lock (_recentLog) return string.Join(Environment.NewLine, _recentLog); }
    }

    /// <summary>Finds llama-server: explicit setting, then ./llama next to the app, then PATH.</summary>
    public string? LocateServer(LocalModelSettings s)
    {
        var exe = OperatingSystem.IsWindows() ? "llama-server.exe" : "llama-server";
        if (!string.IsNullOrWhiteSpace(s.ServerPath))
        {
            var p = _paths.Resolve(s.ServerPath);
            if (Directory.Exists(p)) p = Path.Combine(p, exe);
            return File.Exists(p) ? p : null;
        }

        foreach (var dir in new[] { _paths.LlamaDir, Path.Combine(_paths.LlamaDir, "bin") })
        {
            var p = Path.Combine(dir, exe);
            if (File.Exists(p)) return p;
        }

        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var p = Path.Combine(dir.Trim('"'), exe);
                if (File.Exists(p)) return p;
            }
            catch (ArgumentException) { }
        }
        return null;
    }

    public static IReadOnlyList<string> BuildArguments(LocalModelSettings s, string modelPath, string? mmprojPath, int? contextSize = null)
    {
        var args = new List<string>
        {
            "--model", modelPath,
            "--host", "127.0.0.1",
            "--port", s.Port.ToString(CultureInfo.InvariantCulture),
            "--ctx-size", (contextSize ?? s.ContextSize).ToString(CultureInfo.InvariantCulture),
            "--n-gpu-layers", s.GpuLayers.ToString(CultureInfo.InvariantCulture),
            "--batch-size", s.BatchSize.ToString(CultureInfo.InvariantCulture),
            "--flash-attn", s.FlashAttention ? "on" : "off",
            // Jinja templates give us native tool calling and reasoning output for Qwen models.
            "--jinja",
            "--alias", "local",
            "--parallel", "1",
        };
        if (!string.IsNullOrWhiteSpace(s.KvCacheType) && s.KvCacheType != "f16")
        {
            args.AddRange(["--cache-type-k", s.KvCacheType, "--cache-type-v", s.KvCacheType]);
        }
        if (s.Threads > 0)
            args.AddRange(["--threads", s.Threads.ToString(CultureInfo.InvariantCulture)]);
        if (mmprojPath is not null)
            args.AddRange(["--mmproj", mmprojPath]);
        args.AddRange(SplitArgs(s.ExtraArgs));
        return args;
    }

    public async Task StartAsync(LocalModelSettings s, CancellationToken ct)
    {
        await StopAsync().ConfigureAwait(false);
        LastError = null;
        Warning = null;
        BaseUrl = $"http://127.0.0.1:{s.Port}/v1";

        var exe = LocateServer(s);
        var model = _paths.Resolve(s.ModelPath);
        var mmproj = string.IsNullOrWhiteSpace(s.MmprojPath) ? null : _paths.Resolve(s.MmprojPath);
        if (exe is null)
        {
            Fail("llama-server was not found. Put llama.cpp in the 'llama' folder next to BlackBox, or set its path in Settings.");
            return;
        }
        if (!File.Exists(model))
        {
            Fail($"Model file not found: {model}");
            return;
        }
        if (mmproj is not null && !File.Exists(mmproj))
        {
            Warning = $"Vision projector not found at {mmproj}, so the model is running text-only and cannot see the screen.";
            Log("WARNING: " + Warning);
            mmproj = null;
        }

        if (await IsHealthyAsync(ct).ConfigureAwait(false))
        {
            Fail($"Port {s.Port} is already in use by another server. Close it or pick another port in Settings.");
            return;
        }

        HasVision = mmproj is not null;
        Directory.CreateDirectory(_paths.LogsDir);
        _logFile = new StreamWriter(Path.Combine(_paths.LogsDir, "llama-server.log"), append: false) { AutoFlush = true };

        // If the requested context does not fit in VRAM, step it down instead of failing:
        // a working agent with less context beats an error message.
        foreach (var ctxSize in ContextLadder(s.ContextSize))
        {
            var logStart = LogCount;
            if (await LaunchAsync(exe, s, model, mmproj, ctxSize, ct).ConfigureAwait(false))
            {
                EffectiveContextSize = ctxSize;
                if (ctxSize < s.ContextSize)
                {
                    var note = $"Context lowered from {s.ContextSize / 1024}k to {ctxSize / 1024}k tokens so the model fits in GPU memory.";
                    Warning = Warning is null ? note : Warning + " " + note;
                    Log("WARNING: " + note);
                }
                return;
            }
            if (ct.IsCancellationRequested || !LooksOutOfMemory(logStart))
                return;
            Log($"Not enough memory with a {ctxSize / 1024}k context; retrying with less.");
        }
    }

    /// <summary>Context size actually loaded (may be lower than requested).</summary>
    public int EffectiveContextSize { get; private set; }

    internal static IEnumerable<int> ContextLadder(int requested)
    {
        yield return requested;
        foreach (var smaller in new[] { 65536, 49152, 32768, 16384, 8192 })
            if (smaller < requested)
                yield return smaller;
    }

    private int LogCount
    {
        get { lock (_recentLog) return _logLines; }
    }

    private bool LooksOutOfMemory(int sinceLine)
    {
        string[] lines;
        lock (_recentLog)
        {
            var skip = Math.Max(0, _recentLog.Count - (_logLines - sinceLine));
            lines = _recentLog.Skip(skip).ToArray();
        }
        return lines.Any(l =>
            l.Contains("out of memory", StringComparison.OrdinalIgnoreCase) ||
            l.Contains("OutOfDeviceMemory", StringComparison.OrdinalIgnoreCase) ||
            l.Contains("failed to allocate", StringComparison.OrdinalIgnoreCase) ||
            l.Contains("unable to allocate", StringComparison.OrdinalIgnoreCase) ||
            l.Contains("cudaMalloc", StringComparison.OrdinalIgnoreCase) ||
            l.Contains("failed to create context", StringComparison.OrdinalIgnoreCase) ||
            l.Contains("failed to initialize the context", StringComparison.OrdinalIgnoreCase));
    }

    private async Task<bool> LaunchAsync(string exe, LocalModelSettings s, string model, string? mmproj, int contextSize, CancellationToken ct)
    {
        SetState(ServerState.Starting);
        LastError = null;

        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(exe)!,
        };
        foreach (var a in BuildArguments(s, model, mmproj, contextSize))
            psi.ArgumentList.Add(a);
        Log($"> {exe} {string.Join(' ', psi.ArgumentList.Select(Quote))}");

        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) Log(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) Log(e.Data); };
        process.Exited += (_, _) =>
        {
            if (!ReferenceEquals(_process, process)) return;
            var code = SafeExitCode(process);
            Log($"llama-server exited with code {code}");
            Fail(State == ServerState.Starting
                ? $"llama-server stopped while loading (exit code {code}). Open the Log for details; the model may not fit in GPU memory."
                : $"llama-server stopped unexpectedly (exit code {code}).");
        };

        try
        {
            process.Start();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Fail("Could not start llama-server: " + ex.Message);
            return false;
        }
        _process = process;
        _platform.TieToAppLifetime(process);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        // Loading a multi-GB model can take a while; poll /health until it answers 200.
        var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(10);
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            if (State != ServerState.Starting)
            {
                // Make sure the stderr reader has flushed the error lines before we inspect them.
                await process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None).ConfigureAwait(false);
                _process = null;
                process.Dispose();
                return false;
            }
            if (await IsHealthyAsync(ct).ConfigureAwait(false))
            {
                await WarmUpAsync(mmproj is not null, ct).ConfigureAwait(false);
                SetState(ServerState.Running);
                return true;
            }
            await Task.Delay(500, ct).ConfigureAwait(false);
        }
        await StopAsync().ConfigureAwait(false);
        Fail("llama-server did not become ready within 10 minutes.");
        return false;
    }
    public async Task StopAsync()
    {
        var p = _process;
        _process = null;
        if (p is not null)
        {
            try
            {
                if (!p.HasExited)
                {
                    p.Kill(entireProcessTree: true);
                    await p.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or System.ComponentModel.Win32Exception) { }
            p.Dispose();
            Log("llama-server stopped.");
        }
        _logFile?.Dispose();
        _logFile = null;
        if (State != ServerState.Failed)
            SetState(ServerState.Stopped);
    }

    /// <summary>
    /// The Vulkan backend compiles its GPU shaders on first use, which made the first real
    /// request take minutes. One throwaway request with a long prompt (and an image when vision
    /// is on) does that work while the UI still says "Loading", so the first task is fast.
    /// </summary>
    private async Task WarmUpAsync(bool vision, CancellationToken ct)
    {
        Log("Warming up (first run compiles GPU shaders and can take a few minutes)…");
        var started = DateTime.UtcNow;
        var filler = string.Concat(Enumerable.Repeat("The quick brown fox jumps over the lazy dog. ", 150));
        var parts = new List<ContentPart> { ContentPart.FromText(filler + "\nReply with OK.") };
        if (vision)
            parts.Add(ContentPart.FromImage(BlankJpeg(), "image/jpeg"));

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
            var client = new OpenAiChatClient(http, BaseUrl, null);
            await client.CompleteAsync(
                [new ChatMessage { Role = ChatRole.User, Parts = parts }], [],
                new ChatRequestOptions
                {
                    Model = "local",
                    MaxTokens = 1,
                    Extra = new System.Text.Json.Nodes.JsonObject
                    {
                        ["chat_template_kwargs"] = new System.Text.Json.Nodes.JsonObject { ["enable_thinking"] = false },
                    },
                },
                null, ct).ConfigureAwait(false);
            Log($"Warm-up done in {(DateTime.UtcNow - started).TotalSeconds:0.0}s.");
        }
        catch (Exception ex) when (ex is LlmException or HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            Log("Warm-up skipped: " + ex.Message);
        }
    }

    private static byte[] BlankJpeg()
    {
        using var bmp = new SkiaSharp.SKBitmap(512, 512);
        bmp.Erase(new SkiaSharp.SKColor(128, 128, 128));
        using var img = SkiaSharp.SKImage.FromBitmap(bmp);
        using var data = img.Encode(SkiaSharp.SKEncodedImageFormat.Jpeg, 80);
        return data.ToArray();
    }

    private async Task<bool> IsHealthyAsync(CancellationToken ct)
    {
        try
        {
            using var resp = await _http.GetAsync(BaseUrl[..^3] + "/health", ct).ConfigureAwait(false);
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return false;
        }
    }

    private void Fail(string message)
    {
        LastError = message;
        Log("ERROR: " + message);
        SetState(ServerState.Failed);
    }

    private void SetState(ServerState state)
    {
        if (State == state) return;
        State = state;
        StateChanged?.Invoke(state);
    }

    private void Log(string line)
    {
        lock (_recentLog)
        {
            _recentLog.Add(line);
            _logLines++;
            if (_recentLog.Count > 400) _recentLog.RemoveRange(0, _recentLog.Count - 400);
            try { _logFile?.WriteLine(line); } catch (ObjectDisposedException) { }
        }
        LogLine?.Invoke(line);
    }

    private static int SafeExitCode(Process p)
    {
        try { return p.ExitCode; } catch (InvalidOperationException) { return -1; }
    }

    private static string Quote(string a) => a.Contains(' ') ? $"\"{a}\"" : a;

    /// <summary>Splits a command-line fragment on whitespace, honouring double quotes.</summary>
    public static IEnumerable<string> SplitArgs(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) yield break;
        var current = new System.Text.StringBuilder();
        var quoted = false;
        foreach (var ch in text)
        {
            if (ch == '"') { quoted = !quoted; continue; }
            if (char.IsWhiteSpace(ch) && !quoted)
            {
                if (current.Length > 0) { yield return current.ToString(); current.Clear(); }
                continue;
            }
            current.Append(ch);
        }
        if (current.Length > 0) yield return current.ToString();
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _http.Dispose();
    }
}
