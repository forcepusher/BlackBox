using System.Diagnostics;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;

namespace BlackBox.Core.Comfy;

/// <summary>A file a job made: where ComfyUI keeps it, and where it is on this PC.</summary>
public sealed record ComfyOutput(string NodeId, string Filename, string Subfolder, string Type, string? LocalPath)
{
    /// <summary>image, animation, video, audio, 3d or other, by the file's extension.</summary>
    public string Kind => KindOf(Filename);

    public static string KindOf(string file) => Path.GetExtension(file).ToLowerInvariant() switch
    {
        ".png" or ".jpg" or ".jpeg" or ".bmp" or ".tif" or ".tiff" or ".exr" => "image",
        ".webp" or ".gif" or ".apng" => "animation",
        ".mp4" or ".webm" or ".mov" or ".mkv" or ".avi" or ".m4v" => "video",
        ".wav" or ".mp3" or ".flac" or ".ogg" or ".opus" or ".m4a" or ".aac" => "audio",
        ".glb" or ".gltf" or ".obj" or ".ply" or ".stl" or ".fbx" or ".usdz" or ".3mf" or ".dae" => "3d",
        _ => "other",
    };
}

/// <summary>How a job went.</summary>
public sealed class ComfyJob
{
    public required string PromptId { get; init; }

    /// <summary>success, error, interrupted, running or queued (the last two when the wait ended first).</summary>
    public required string Status { get; set; }

    public TimeSpan Elapsed { get; set; }
    public List<ComfyOutput> Files { get; } = [];

    /// <summary>Text the workflow's nodes showed (PreviewAny, a text generator…).</summary>
    public List<string> Texts { get; } = [];

    public string? Error { get; set; }

    /// <summary>Where it was when the wait ended: "KSampler (Advanced), step 2 of 4".</summary>
    public string? Progress { get; set; }
}

/// <summary>Queues workflows on a ComfyUI server, follows them to the end and collects what they made.</summary>
public static class ComfyRunner
{
    /// <summary>
    /// Queues a workflow. Returns its prompt id, or ComfyUI's complaints about it (the problems
    /// /prompt found before running anything).
    /// </summary>
    public static async Task<(string? PromptId, List<string> Problems)> QueueAsync(ComfyApi api, ComfyWorkflow workflow, string clientId, CancellationToken ct)
    {
        var body = new JsonObject { ["prompt"] = JsonNode.Parse(workflow.ToJson()), ["client_id"] = clientId };
        var (status, text) = await api.PostJsonAsync("prompt", body, ct).ConfigureAwait(false);
        JsonNode? answer = null;
        try { answer = JsonNode.Parse(text); }
        catch (System.Text.Json.JsonException) { }
        if (status == HttpStatusCode.OK && (string?)answer?["prompt_id"] is { } id)
            return (id, NodeErrors(answer?["node_errors"] as JsonObject));
        var problems = NodeErrors(answer?["node_errors"] as JsonObject);
        if (answer?["error"] is JsonObject error)
        {
            var type = (string?)error["type"];
            var message = (string?)error["message"] ?? "";
            var details = (string?)error["details"] ?? "";
            if (type == "missing_node_type")
            {
                var cls = (string?)error["extra_info"]?["class_type"];
                problems.Insert(0, $"{message} ({details}{(cls is not null ? ", class_type " + cls : "")}).");
            }
            else if (type != "prompt_outputs_failed_validation" || problems.Count == 0)
                problems.Insert(0, (message + (details.Length > 0 ? ": " + details : "")).Clip(1500));
        }
        else if (problems.Count == 0)
            problems.Add($"ComfyUI refused the workflow ({(int)status}): {text.Clip(1500)}");
        return (null, problems);
    }

    /// <summary>ComfyUI's per-node complaints, one line each, with the node's id and type.</summary>
    internal static List<string> NodeErrors(JsonObject? nodeErrors)
    {
        var lines = new List<string>();
        foreach (var (id, entry) in nodeErrors ?? [])
        {
            var cls = (string?)entry?["class_type"] ?? "";
            foreach (var e in (entry?["errors"] as JsonArray ?? []).OfType<JsonObject>())
            {
                var message = (string?)e["message"] ?? "";
                var details = (string?)e["details"] ?? "";
                // An input a chosen option brings is "format.quality" in the workflow, "quality" in the details.
                if (e["extra_info"]?["input_name"] is JsonValue n && n.TryGetValue<string>(out var input) && input.Contains('.') && !details.Contains(input, StringComparison.Ordinal))
                    details = details == input[(input.LastIndexOf('.') + 1)..] ? input : $"{input}: {details}";
                lines.Add($"node {id} {cls}: {message}{(details.Length > 0 ? ": " + details.Clip(600) : "")}");
            }
        }
        return lines;
    }

    /// <summary>
    /// Follows a queued job until it ends, the wait is over, or the user writes (then it goes on
    /// running, and a later call picks it up). Stop cancels the job in ComfyUI too.
    /// </summary>
    /// <param name="titles">Node id → what to call it in progress reports.</param>
    public static async Task<ComfyJob> WaitAsync(ComfyApi api, string promptId, string clientId, IReadOnlyDictionary<string, string> titles, TimeSpan maxWait,
        Action<string>? progress, CancellationToken userMessage, CancellationToken ct)
    {
        var job = new ComfyJob { PromptId = promptId, Status = "queued" };
        var clock = Stopwatch.StartNew();
        using var socket = new ClientWebSocket();
        var events = new JobEvents(promptId);
        Task? listening = null;
        using var listen = CancellationTokenSource.CreateLinkedTokenSource(ct);
        try
        {
            await socket.ConnectAsync(api.WebSocketUrl(clientId), listen.Token).WaitAsync(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
            listening = ListenAsync(socket, events, listen.Token);
        }
        catch (Exception ex) when (ex is WebSocketException or TimeoutException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            // Progress comes from polling alone then.
        }

        try
        {
            var nextPoll = TimeSpan.Zero;
            // A server that died in the middle of the job (out of memory, a crash), or a job taken
            // out of the queue, was waited on for the whole wait, then said to be "still running",
            // and the next wait did the same. Each is told once it has gone on for a while.
            int unanswered = 0, missing = 0;
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                if (events.Finished || clock.Elapsed >= nextPoll)
                {
                    nextPoll = clock.Elapsed + TimeSpan.FromSeconds(events.Finished ? 0 : 4);
                    var (entry, answered, listed) = await HistoryAsync(api, promptId, ct).ConfigureAwait(false);
                    if (entry is not null)
                    {
                        Collect(entry, job, events);
                        job.Elapsed = clock.Elapsed;
                        return job;
                    }
                    var (ahead, queueAnswered) = await QueuePositionAsync(api, promptId, ct).ConfigureAwait(false);
                    // Not answering with its socket still open is a server busy in Python, not one gone.
                    unanswered = answered || queueAnswered || listening is { IsCompleted: false } ? 0 : unanswered + 1;
                    missing = answered && queueAnswered && !listed && ahead is null ? missing + 1 : 0;
                    if (unanswered >= GiveUpAfter || missing >= GiveUpAfter)
                    {
                        job.Status = "error";
                        job.Error = unanswered > 0
                            ? $"ComfyUI stopped answering while the job ran (for {Clock(clock.Elapsed)} now): its process may have ended, out of memory or with an error. Its log says why; action=status shows ComfyUI's state."
                            : "The job isn't in ComfyUI's queue or history any more: it was taken out of the queue, or ComfyUI was restarted since. Run it again.";
                        job.Elapsed = clock.Elapsed;
                        return job;
                    }
                    if (!events.Started && ahead is { } position)
                        job.Progress = position switch { < 0 => "starting", 0 => "next in ComfyUI's queue", _ => $"waiting in ComfyUI's queue behind {position} job(s)" };
                }
                if (events.Started)
                {
                    job.Status = "running";
                    job.Progress = events.Describe(titles);
                }
                progress?.Invoke($"{job.Progress ?? "queued"} · {Clock(clock.Elapsed)}");
                if (clock.Elapsed >= maxWait || userMessage.IsCancellationRequested)
                {
                    job.Elapsed = clock.Elapsed;
                    return job;
                }
                // Never throws: a Stop or a message ends the delay, and the next look ends the wait.
                using var tick = CancellationTokenSource.CreateLinkedTokenSource(ct, userMessage);
                await Task.WhenAny(events.Changed.Task, Task.Delay(1000, tick.Token)).ConfigureAwait(false);
                events.ResetChanged();
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Stop: the job must not go on using the GPU for nothing.
            try { await api.CancelAsync(promptId, CancellationToken.None).ConfigureAwait(false); }
            catch (ComfyApiException) { }
            throw;
        }
        finally
        {
            await listen.CancelAsync().ConfigureAwait(false);
            if (listening is not null)
                try { await listening.ConfigureAwait(false); }
                catch (Exception) { /* the socket closing */ }
        }
    }

    internal static string Clock(TimeSpan t) => t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t:mm\\:ss}" : $"{(int)t.TotalMinutes}:{t:ss}";

    /// <summary>The polls in a row after which a server that doesn't answer, or a job that is nowhere, is given up on (about half a minute).</summary>
    internal static int GiveUpAfter { get; set; } = 8;

    /// <summary>The job's history entry once it has ended; whether the server answered at all, and whether its history has the job in any state.</summary>
    private static async Task<(JsonObject? Entry, bool Answered, bool Listed)> HistoryAsync(ComfyApi api, string promptId, CancellationToken ct)
    {
        try
        {
            var listed = await api.GetJsonAsync("history/" + Uri.EscapeDataString(promptId), ct, TimeSpan.FromSeconds(30)).ConfigureAwait(false) is JsonObject h
                         && h[promptId] is JsonObject e ? e : null;
            return (listed?["status"]?["completed"] is not null ? listed : null, true, listed is not null);
        }
        catch (Exception ex) when (ex is ComfyApiException or System.Text.Json.JsonException)
        {
            return (null, false, false);
        }
    }

    /// <summary>How many jobs run or wait before this one: -1 when it is running, null when it isn't in the queue; and whether the server answered.</summary>
    private static async Task<(int? Ahead, bool Answered)> QueuePositionAsync(ComfyApi api, string promptId, CancellationToken ct)
    {
        try
        {
            if (await api.GetJsonAsync("queue", ct, TimeSpan.FromSeconds(10)).ConfigureAwait(false) is not JsonObject q)
                return (null, false);
            var running = (q["queue_running"] as JsonArray ?? []).OfType<JsonArray>().ToList();
            if (running.Any(r => r.Count > 1 && (string?)r[1] == promptId))
                return (-1, true);
            var pending = (q["queue_pending"] as JsonArray ?? []).OfType<JsonArray>().OrderBy(r => r.Count > 0 && r[0] is JsonValue n && n.TryGetValue<double>(out var d) ? d : 0).ToList();
            var at = pending.FindIndex(r => r.Count > 1 && (string?)r[1] == promptId);
            return (at < 0 ? null : at + running.Count, true);
        }
        catch (Exception ex) when (ex is ComfyApiException or System.Text.Json.JsonException or InvalidOperationException)
        {
            return (null, false);
        }
    }

    /// <summary>The outcome and the files of a finished job, from its history entry.</summary>
    private static void Collect(JsonObject entry, ComfyJob job, JobEvents events)
    {
        var status = entry["status"] as JsonObject;
        job.Status = (string?)status?["status_str"] == "success" ? "success" : "error";
        foreach (var message in (status?["messages"] as JsonArray ?? []).OfType<JsonArray>())
        {
            var kind = message.Count > 0 ? (string?)message[0] : null;
            if (kind == "execution_error" && message.Count > 1 && message[1] is JsonObject e)
                job.Error = DescribeError(e);
            else if (kind == "execution_interrupted")
            {
                job.Status = "interrupted";
                job.Error = "The job was stopped in ComfyUI before it finished.";
            }
        }
        if (job.Status == "error" && job.Error is null)
            job.Error = events.Error ?? "ComfyUI reports the job failed, without saying why. Its log may: action=status.";

        foreach (var (node, output) in entry["outputs"] as JsonObject ?? [])
        {
            if (output is not JsonObject o)
                continue;
            foreach (var (key, value) in o)
            {
                if (value is not JsonArray items)
                    continue;
                foreach (var item in items)
                {
                    if (item is JsonObject f && (string?)f["filename"] is { Length: > 0 } filename)
                    {
                        var subfolder = (string?)f["subfolder"] ?? "";
                        var type = (string?)f["type"] ?? "output";
                        if (job.Files.Any(x => x.Filename == filename && x.Subfolder == subfolder && x.Type == type))
                            continue;
                        job.Files.Add(new ComfyOutput(node, filename, subfolder, type, null));
                    }
                    else if (item is JsonValue v && v.TryGetValue<string>(out var text) && key is "text" or "string" or "result")
                        job.Texts.Add(text);
                }
            }
        }
    }

    /// <summary>"Node 129:86 KSamplerAdvanced failed: OutOfMemoryError: CUDA out of memory…", with the end of the traceback and a hint for the usual causes.</summary>
    internal static string DescribeError(JsonObject e)
    {
        var sb = new StringBuilder();
        sb.Append($"Node {(string?)e["node_id"]} {(string?)e["node_type"]} failed: ");
        var type = ((string?)e["exception_type"] ?? "").Split('.').Last();
        var message = ((string?)e["exception_message"] ?? "").Trim();
        sb.Append(type.Length > 0 ? type + ": " : "").Append(message.Clip(1200));
        var trace = (e["traceback"] as JsonArray ?? []).Select(t => (string?)t).OfType<string>().ToList();
        if (trace.Count > 0)
            sb.Append("\nTraceback, last lines:\n").Append(string.Join("", trace.TakeLast(3)).Trim().Clip(1200));
        var all = (type + " " + message).ToLowerInvariant();
        if (all.Contains("out of memory") || all.Contains("outofmemory") || all.Contains("allocation on device"))
            sb.Append("\nThe GPU ran out of memory: make the picture or video smaller or shorter (fewer frames), use fewer steps or a smaller model, or free memory first (action=free).");
        else if (all.Contains("no such file") || all.Contains("not found") || all.Contains("does not exist"))
            sb.Append("\nA file it needs is missing: check the model and input file names (action=models lists the models).");
        return sb.ToString();
    }

    /// <summary>Fills in where each file is on this PC: in place when the server is local, otherwise downloaded into <paramref name="downloadDir"/>.</summary>
    public static async Task<List<ComfyOutput>> LocateAsync(ComfyApi api, IReadOnlyList<ComfyOutput> files, IReadOnlyDictionary<string, string> folders, string downloadDir, CancellationToken ct)
    {
        var located = new List<ComfyOutput>();
        foreach (var f in files)
        {
            string? path = null;
            if (api.IsLocal && folders.TryGetValue(f.Type, out var dir))
            {
                var candidate = Path.GetFullPath(Path.Combine(dir, f.Subfolder, f.Filename));
                if (File.Exists(candidate))
                    path = candidate;
            }
            if (path is null)
            {
                try
                {
                    var bytes = await api.GetBytesAsync(api.ViewPath(f.Filename, f.Subfolder, f.Type), ct).ConfigureAwait(false);
                    Directory.CreateDirectory(downloadDir);
                    path = Path.Combine(downloadDir, f.Filename);
                    await File.WriteAllBytesAsync(path, bytes, ct).ConfigureAwait(false);
                }
                catch (ComfyApiException) { }
            }
            located.Add(f with { LocalPath = path });
        }
        return located;
    }

    private static async Task ListenAsync(ClientWebSocket socket, JobEvents events, CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        while (socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            using var message = new MemoryStream();
            WebSocketReceiveResult r;
            do
            {
                r = await socket.ReceiveAsync(buffer, ct).ConfigureAwait(false);
                if (r.MessageType == WebSocketMessageType.Close)
                    return;
                if (r.MessageType == WebSocketMessageType.Text)
                    message.Write(buffer, 0, r.Count);
            } while (!r.EndOfMessage);
            if (r.MessageType != WebSocketMessageType.Text)
                continue; // previews of the image in progress
            try
            {
                if (JsonNode.Parse(message.ToArray()) is JsonObject m)
                    events.Take(m);
            }
            catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException or FormatException)
            {
                // One odd message must not end the listening.
            }
        }
    }

    /// <summary>What the server's web socket said about one job.</summary>
    private sealed class JobEvents(string promptId)
    {
        private readonly object _gate = new();
        public bool Started { get; private set; }
        public bool Finished { get; private set; }
        public string? Error { get; private set; }
        private string? _node;
        private int _value, _max;
        public TaskCompletionSource Changed { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void ResetChanged()
        {
            lock (_gate)
                if (Changed.Task.IsCompleted)
                    Changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public void Take(JsonObject m)
        {
            var type = (string?)m["type"];
            var data = m["data"] as JsonObject;
            if (data is null || (string?)data["prompt_id"] is { } id && id != promptId)
                return;
            lock (_gate)
            {
                switch (type)
                {
                    case "execution_start":
                        Started = true;
                        break;
                    case "execution_cached":
                        Started = true;
                        break;
                    case "executing":
                        Started = true;
                        _node = data["node"] is JsonValue n0 && n0.TryGetValue<string>(out var executing) ? executing : null;
                        (_value, _max) = (0, 0);
                        if (_node is null) Finished = true;
                        break;
                    case "progress":
                        Started = true;
                        _node = data["node"] is JsonValue n && n.TryGetValue<string>(out var node) ? node : _node;
                        // Some nodes report fractions (2.0, 0.5): numbers of any kind are read.
                        _value = (int)Math.Round(Number(data["value"]));
                        _max = (int)Math.Round(Number(data["max"]));
                        break;
                    case "execution_success":
                    case "execution_interrupted":
                        Finished = true;
                        break;
                    case "execution_error":
                        Error = DescribeError(data);
                        Finished = true;
                        break;
                    default:
                        return;
                }
                Changed.TrySetResult();
            }
        }

        private static double Number(JsonNode? n) => n is JsonValue v && v.TryGetValue<double>(out var d) ? d : 0;

        public string Describe(IReadOnlyDictionary<string, string> titles)
        {
            lock (_gate)
            {
                if (_node is null)
                    return Finished ? "finishing" : "running";
                var name = titles.TryGetValue(_node, out var t) ? t : _node;
                return _max > 1 ? $"{name}, step {_value} of {_max}" : name;
            }
        }
    }
}
