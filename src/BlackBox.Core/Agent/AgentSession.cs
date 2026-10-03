using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using BlackBox.Core.Llm;
using BlackBox.Core.Settings;
using BlackBox.Core.Tools;

namespace BlackBox.Core.Agent;

public enum AgentStopReason { Completed, Cancelled, Failsafe, Error }

/// <summary>Callbacks from the agent loop. Called on a background thread.</summary>
public interface IAgentObserver
{
    void OnStepStarted(int step);
    void OnDelta(StreamDelta delta);
    void OnAssistantMessage(ChatMessage message);
    Task<bool> RequestApprovalAsync(ToolCall call, Tool tool, string summary, CancellationToken ct);
    void OnToolStarted(ToolCall call, string summary);

    /// <summary>Status the user should see that is not part of the conversation (e.g. reconnecting).</summary>
    void OnNotice(string text);
    void OnToolFinished(ToolCall call, ToolResult result);
    void OnFinished(AgentStopReason reason, string? detail);
}

/// <summary>
/// One conversation with the model. Each user turn runs the loop: ask the model, run the tools it
/// calls, feed results back, repeat until it answers without tool calls.
/// </summary>
public sealed partial class AgentSession(
    OpenAiChatClient client,
    ChatRequestOptions requestOptions,
    ToolRegistry tools,
    ToolContext toolContext,
    string systemPrompt,
    List<ChatMessage>? history = null,
    int contextTokens = 0)
{
    /// <summary>The model's context size as this session knows it: as reported at the start, or corrected by a server error.</summary>
    public int ContextTokens => contextTokens;

    /// <summary>Conversation so far (without the system prompt). Can be shared across sessions so settings changes keep the chat.</summary>
    public List<ChatMessage> History { get; } = history ?? [];

    public async Task RunAsync(string userText, IAgentObserver observer, CancellationToken ct)
    {
        History.Add(new ChatMessage { Role = ChatRole.User, Content = userText, FromUser = true });
        var nudges = 0;
        var loops = new LoopDetector();
        var justReset = false;
        Task? warmUp = null;

        try
        {
            // No step limit: a run ends when the model is done, the user presses Stop, or the
            // mouse hits the failsafe corner. Tasks like "keep chatting for 12 hours" are valid;
            // runaway loops are handled by LoopDetector, and growth by context resets.
            for (var step = 1; ; step++)
            {
                ct.ThrowIfCancellationRequested();
                observer.OnStepStarted(step);
                if (warmUp is not null)
                {
                    await warmUp.ConfigureAwait(false); // never two requests on the server at once
                    warmUp = null;
                }
                if (contextTokens > 0 && TokensInUse() > ResetThreshold)
                    await ResetContextAsync(observer, ct).ConfigureAwait(false);

                var request = new List<ChatMessage>(History.Count + 1) { ChatMessage.System(systemPrompt) };
                request.AddRange(History);

                var reply = await CompleteWithResetAsync(request, observer, ct).ConfigureAwait(false);
                if (reply.FinishReason == "length" && !reply.HasToolCalls && contextTokens > 0 && !justReset)
                {
                    // The server ran out of context mid-reply, so the size estimate was off. The
                    // cut-off reply is thrown away and this step is redone after a reset.
                    observer.OnNotice("The model ran out of context in the middle of its reply.");
                    observer.OnAssistantMessage(new ChatMessage { Role = ChatRole.Assistant, Content = "" });
                    await ResetContextAsync(observer, ct).ConfigureAwait(false);
                    justReset = true;
                    continue;
                }
                justReset = false;
                TextToolCallParser.TryExtract(reply, tools);
                // "The task is complete" followed by yet another screenshot: small models can keep
                // re-verifying forever. A completion claim with only look-only calls ends the turn.
                if (reply.HasToolCalls && LooksFinished(reply.Content) &&
                    reply.ToolCalls!.All(c => ToolCallRepair.ResolveTool(c.Name, tools)?.Risk == ToolRisk.ReadOnly))
                    reply.ToolCalls = null;
                History.Add(reply);
                RecordUsage(reply);
                observer.OnAssistantMessage(reply);

                if (!reply.HasToolCalls)
                {
                    // Small models sometimes announce an action ("Let me click Save.") and stop,
                    // or return nothing at all. Push them on a couple of times before giving up.
                    if (nudges < MaxNudges && LooksUnfinished(reply))
                    {
                        nudges++;
                        History.Add(ChatMessage.User(NudgeText));
                        continue;
                    }
                    observer.OnFinished(AgentStopReason.Completed, null);
                    return;
                }

                warmUp = WarmCacheAsync(ct);
                var screenshots = new List<ContentPart>();
                foreach (var call in reply.ToolCalls!)
                {
                    var result = await RunToolAsync(call, observer, ct).ConfigureAwait(false);
                    if (loops.Check(call, result) is { } warning)
                        result = result with { Text = result.Text + "\n" + warning };
                    History.Add(ChatMessage.ToolResult(call.Id, result.Image is null ? result.Text : result.Text + "\n(The screenshot is attached in the next message.)"));
                    if (result.Image is not null)
                        screenshots.Add(ContentPart.FromImage(result.Image, result.ImageMime));
                }

                // Most OpenAI-compatible servers only accept images in user messages,
                // so screenshots produced by tools are delivered right after the tool results.
                if (screenshots.Count > 0)
                {
                    var parts = new List<ContentPart> { ContentPart.FromText("Screenshot after the last action:") };
                    parts.AddRange(screenshots.TakeLast(1));
                    History.Add(new ChatMessage { Role = ChatRole.User, Parts = parts });
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            observer.OnFinished(AgentStopReason.Cancelled, null);
        }
        catch (FailsafeTriggeredException ex)
        {
            observer.OnFinished(AgentStopReason.Failsafe, ex.Message);
        }
        catch (Exception ex)
        {
            observer.OnFinished(AgentStopReason.Error, ex.Message);
        }
        finally
        {
            RepairDanglingToolCalls();
        }
    }

    private const int MaxNudges = 2;

    /// <summary>How many dropped connections one step survives.</summary>
    internal const int MaxReconnects = 12;

    /// <summary>How many looping replies one step survives before the run ends with an error.</summary>
    private const int MaxRepetitionRetries = 3;

    internal const string RepetitionNote =
        "[BlackBox] Your last reply was cut off because your thinking kept repeating the same lines. " +
        "Don't re-check earlier screens from memory: you may have misread one. Trust what the current screen shows " +
        "(zoom in if needed), decide, and call the next tool.";

    internal const string LongToolCallNote =
        "[BlackBox] Your last reply was lost: the connection drops when a single tool call takes more than about a minute to write. " +
        "Keep every tool call short. Write long files in parts of at most about 100 lines: create the file with write_file, " +
        "then add each next part with write_file and append set to true. Continue with the task.";
    private const string NudgeText =
        "Continue with the task. If there is another step, call the tool for it now. If the task is finished, reply with a short summary of what you did.";

    /// <summary>The throwaway user message a cache warm-up ends with (see <see cref="WarmCacheAsync"/>).</summary>
    internal const string WarmUpText = ".";

    /// <summary>
    /// Has the server read the reply it just produced back into its cache while the tools run.
    /// </summary>
    /// <remarks>
    /// Hybrid models (Qwen3.5+) cannot rewind their state, so llama.cpp-based servers checkpoint it
    /// only while reading a prompt, never after generating. The next request therefore has to read
    /// the model's own reply again, at prefill speed (about 2 s per 1,000 tokens, thinking included).
    /// A one-token request that ends in a throwaway user message makes the server do that now and
    /// leave a checkpoint where the next request's tool results begin (a user message too). Measured
    /// on LM Studio with qwen3.8: the next step then reads 2 s of prompt instead of 12 s for a
    /// 6.5k-token reply. The warm-up is awaited before the next real request so the two never run
    /// at once; if it fails, the next request simply reads the reply itself, as before.
    /// </remarks>
    private async Task WarmCacheAsync(CancellationToken ct)
    {
        var request = new List<ChatMessage>(History.Count + 2) { ChatMessage.System(systemPrompt) };
        request.AddRange(History);
        request.Add(ChatMessage.User(WarmUpText));
        try
        {
            await client.CompleteAsync(request, tools.Definitions(), requestOptions.WithMaxTokens(1), null, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is LlmException || ex is OperationCanceledException && ct.IsCancellationRequested)
        {
        }
    }

    internal static bool LooksFinished(string? content) => content is { Length: > 0 } && CompletionClaim().IsMatch(content);

    internal static bool LooksUnfinished(ChatMessage reply)
    {
        var text = (reply.Content ?? "").Trim();
        if (text.Length == 0)
            return true;
        var tail = text.Length > 240 ? text[^240..] : text;
        return tail.EndsWith(':') || AnnouncedAction().IsMatch(tail);
    }

    [GeneratedRegex(@"\b((?<!\b(whether|if|once|when)\s+(the\s+)?)task (is|has been) (now )?(complete|completed|done|finished)|i('ve| have) (successfully )?(completed|finished) (the|your|this) task|everything is done|all done)\b", RegexOptions.IgnoreCase)]
    private static partial Regex CompletionClaim();

    [GeneratedRegex(@"(^|[.!?\n]\s*)(let me|let's|i'll|i will|i'm going to|i am going to|next,? i|now i|i need to)\b[^.!?\n]*[.!]?\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex AnnouncedAction();

    private async Task<ToolResult> RunToolAsync(ToolCall call, IAgentObserver observer, CancellationToken ct)
    {
        var tool = ToolCallRepair.ResolveTool(call.Name, tools);
        if (tool is null)
        {
            var unknown = ToolResult.Error($"Unknown tool '{call.Name}'. Available: {string.Join(", ", tools.All.Select(t => t.Name))}.");
            observer.OnToolStarted(call, call.Name);
            observer.OnToolFinished(call, unknown);
            return unknown;
        }

        JsonObject args;
        string summary;
        try
        {
            args = ToolCallRepair.ParseArguments(call.Arguments);
            ToolCallRepair.ApplyNameHints(call.Name, args);
            ToolCallRepair.NormalizeArguments(args, tool);
            summary = tool.Describe(args);
        }
        catch (ToolArgumentException ex)
        {
            var bad = ToolResult.Error($"{ex.Message} Parameters for {tool.Name}: {tool.Parameters.ToJsonString()}");
            observer.OnToolStarted(call, call.Name);
            observer.OnToolFinished(call, bad);
            return bad;
        }

        if (NeedsApproval(tool) && !await observer.RequestApprovalAsync(call, tool, summary, ct).ConfigureAwait(false))
        {
            var denied = ToolResult.Error("The user declined this action. Ask what they would like instead, or try another approach.");
            observer.OnToolFinished(call, denied);
            return denied;
        }

        observer.OnToolStarted(call, summary);
        ToolResult result;
        try
        {
            result = await tool.ExecuteAsync(args, toolContext, ct).ConfigureAwait(false);
        }
        catch (ToolArgumentException ex)
        {
            result = ToolResult.Error($"{ex.Message} Parameters for {tool.Name}: {tool.Parameters.ToJsonString()}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or InvalidOperationException or ArgumentException or ExternalException or TimeoutException)
        {
            result = ToolResult.Error(ex.Message);
        }
        observer.OnToolFinished(call, result);
        return result;
    }

    private bool NeedsApproval(Tool tool) => toolContext.Settings.Approval switch
    {
        ApprovalMode.AskAll => tool.Risk != ToolRisk.ReadOnly,
        ApprovalMode.AskRisky => tool.Risk == ToolRisk.Risky,
        _ => false,
    };

    /// <summary>
    /// Sends the request; if the server says it does not fit, resets the context and tries again
    /// (twice at most) before giving up.
    /// </summary>
    private async Task<ChatMessage> CompleteWithResetAsync(List<ChatMessage> request, IAgentObserver observer, CancellationToken ct)
    {
        var repeats = 0;
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                var guard = new RepetitionGuard(observer.OnDelta);
                return await CompleteWithReconnectAsync(request, tools.Definitions(), requestOptions, observer, guard.OnDelta, ct).ConfigureAwait(false);
            }
            catch (RepetitionException) when (++repeats <= MaxRepetitionRetries)
            {
                // Dropping the stream stops the server too. The looping reply is thrown away and
                // the model is told to trust the screen instead of its memory; appending the note
                // keeps the server's cache.
                observer.OnNotice("The model's thinking started repeating itself; cut it off and asked it to look again.");
                observer.OnAssistantMessage(new ChatMessage { Role = ChatRole.Assistant, Content = "" });
                observer.OnStepStarted(0);
                if (History[^1].Content != RepetitionNote)
                {
                    var note = ChatMessage.User(RepetitionNote);
                    History.Add(note);
                    request.Add(note);
                }
                attempt--;
            }
            catch (ContextOverflowException ex) when (attempt < 2)
            {
                // The context size was assumed or wrong; most servers name the real one.
                if (ex.ContextLimit is { } limit && (contextTokens == 0 || limit < contextTokens))
                {
                    observer.OnNotice($"The server's context is {limit / 1000}k tokens, not {contextTokens / 1000}k; working with that from now on.");
                    contextTokens = limit;
                }
                await ResetContextAsync(observer, ct).ConfigureAwait(false);
                request.Clear();
                request.Add(ChatMessage.System(systemPrompt));
                request.AddRange(History);
            }
        }
    }

    /// <summary>
    /// Sends one request, retrying when the connection drops. Unstable links (VPNs, proxies that
    /// cut silent connections) drop long requests, but the server keeps what it already processed
    /// in its prompt cache, so sending the identical request again resumes where it stopped.
    /// </summary>
    private async Task<ChatMessage> CompleteWithReconnectAsync(
        IReadOnlyList<ChatMessage> request, IReadOnlyList<ToolDefinition> toolDefs, ChatRequestOptions options,
        IAgentObserver observer, Action<StreamDelta>? onDelta, CancellationToken ct)
    {
        var drops = 0;
        while (true)
        {
            try
            {
                return await client.CompleteAsync(request, toolDefs, options, onDelta, ct).ConfigureAwait(false);
            }
            catch (ConnectionLostException ex) when (++drops <= MaxReconnects)
            {
                observer.OnNotice($"Connection lost ({ex.Message}). Reconnecting, attempt {drops} of {MaxReconnects}…");
                // Resending would only repeat a tool call too long to get through, so the model is
                // asked to split it. The note stays in the history so later calls stay short too.
                if (ex.ToolCallInProgress is not null && toolDefs.Count > 0 && request[^1].Content != LongToolCallNote)
                {
                    var note = ChatMessage.User(LongToolCallNote);
                    History.Add(note);
                    request = [.. request, note];
                    observer.OnNotice("The reply was lost while the model was writing a long tool call; asking it to write in smaller parts.");
                }
                if (ex.ReceivedTokens && onDelta is not null)
                {
                    // Discard the half-streamed reply; the retry produces it again.
                    observer.OnAssistantMessage(new ChatMessage { Role = ChatRole.Assistant, Content = "" });
                    observer.OnStepStarted(0);
                }
                // The server keeps what it processed, so resending at once loses nothing; a short
                // pause only after repeated drops, in case the link itself is down.
                if (drops > 1)
                    await Task.Delay(TimeSpan.FromSeconds(Math.Min(drops - 1, 3)), ct).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Rough token count of the conversation (about 3.5 characters per token, ~2000 per screenshot, the size of a 1920x1080 one).</summary>
    internal int EstimateTokens() =>
        (int)(((long)systemPrompt.Length + 12_000 /* tool schemas */ + History.Sum(CharsOf)) * 2 / 7);

    /// <summary>Rough token count of one message.</summary>
    internal static int EstimateTokens(ChatMessage m) => (int)((long)CharsOf(m) * 2 / 7);

    private static long CharsOf(ChatMessage m) =>
        (m.Content?.Length ?? 0) + (m.Reasoning?.Length ?? 0) +
        (m.ToolCalls?.Sum(c => c.Name.Length + c.Arguments.Length) ?? 0) +
        (m.Parts?.Sum(p => p.IsImage ? 7_000 : p.Text?.Length ?? 0) ?? 0);

    /// <summary>
    /// Keeps the prompt under <paramref name="budget"/> tokens. Preserved thinking is the bulk of
    /// a long run, so older reasoning goes first, then the text of old tool results.
    /// </summary>
    internal void FitContext(int budget)
    {
        if (budget <= 0 || EstimateTokens() <= budget)
            return;

        DropOldReasoning(budget);
        if (EstimateTokens() <= budget)
            return;

        foreach (var m in History.Take(Math.Max(0, History.Count - 8)).Where(m => m.Role == ChatRole.Tool && m.Content is { Length: > 400 }))
        {
            m.Content = m.Content![..300] + "\n[older output trimmed to save context]";
            if (EstimateTokens() <= budget) return;
        }

        // Last resort: drop whole steps from the middle. The original request (first message)
        // and the most recent steps stay, and tool results never lose their tool call.
        const string gap = "[Earlier steps were removed to fit the context window. Take a screenshot if you need to see the current state.]";
        var removedAny = false;
        while (EstimateTokens() > budget && History.Count > 8)
        {
            var at = History.Count > 1 && History[1].Content == gap ? 2 : 1;
            History.RemoveAt(at);
            while (at < History.Count - 6 && History[at].Role == ChatRole.Tool)
                History.RemoveAt(at); // results whose call was just removed
            removedAny = true;
        }
        if (removedAny && History[1].Content != gap)
            History.Insert(1, ChatMessage.User(gap));
    }

    /// <summary>
    /// If a run was interrupted between the model asking for tools and the results arriving,
    /// add placeholder results so the next request is still a valid conversation.
    /// </summary>
    private void RepairDanglingToolCalls()
    {
        for (var i = History.Count - 1; i >= 0; i--)
        {
            var msg = History[i];
            if (msg.Role != ChatRole.Assistant || !msg.HasToolCalls)
                continue;
            var answered = History.Skip(i + 1).Where(m => m.Role == ChatRole.Tool).Select(m => m.ToolCallId).ToHashSet();
            var insertAt = i + 1 + History.Skip(i + 1).TakeWhile(m => m.Role == ChatRole.Tool).Count();
            foreach (var call in msg.ToolCalls!.Where(c => !answered.Contains(c.Id)))
                History.Insert(insertAt++, ChatMessage.ToolResult(call.Id, "Not run: the user stopped the agent."));
            break;
        }
    }
}
