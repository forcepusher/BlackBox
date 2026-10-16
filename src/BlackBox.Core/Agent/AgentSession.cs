using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using BlackBox.Core.Llm;
using BlackBox.Core.Settings;
using BlackBox.Core.Tools;

namespace BlackBox.Core.Agent;

public enum AgentStopReason { Completed, Cancelled, Error }

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

    /// <summary>
    /// How much of a step's request has gone out: it carries every screenshot in the context, and
    /// over a slow VPN sending it took minutes that looked like the model thinking.
    /// </summary>
    void OnSending(long sent, long total) { }
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

    /// <summary>Messages the user sent while the run goes on, waiting for the model's next step.</summary>
    private readonly System.Collections.Concurrent.ConcurrentQueue<string> _incoming = new();

    /// <summary>Cancelled when such a message arrives, so a wait in progress ends early (see <see cref="ToolContext.UserMessage"/>).</summary>
    private CancellationTokenSource _arrived = new();

    /// <summary>
    /// A message the user sent while the agent works: it joins the conversation before the model's
    /// next step, and a wait in progress ends early so the model reads it soon. Until now Enter did
    /// nothing during a run, even while BlackBox waited for an OK, and the text sat in the box.
    /// Any thread.
    /// </summary>
    public void Steer(string text)
    {
        lock (_incoming)
        {
            _incoming.Enqueue(text);
            _arrived.Cancel();
        }
    }

    /// <summary>Messages that arrived too late for the run (it ended first); the caller sends them on.</summary>
    public List<string> TakeUndelivered()
    {
        var left = new List<string>();
        while (_incoming.TryDequeue(out var text))
            left.Add(text);
        return left;
    }

    /// <summary>Adds the user's messages that arrived since the last step, as plain user messages: the history only grows.</summary>
    private void DeliverIncoming()
    {
        lock (_incoming)
        {
            if (!_arrived.IsCancellationRequested)
                return;
            // A fresh token for the next message; the one a wait may still hold stays cancelled.
            _arrived = new CancellationTokenSource();
            toolContext.UserMessage = _arrived.Token;
            while (_incoming.TryDequeue(out var text))
                History.Add(new ChatMessage { Role = ChatRole.User, Content = text, FromUser = true });
        }
    }

    public async Task RunAsync(string userText, IAgentObserver observer, CancellationToken ct)
    {
        History.Add(new ChatMessage { Role = ChatRole.User, Content = userText, FromUser = true });
        toolContext.UserMessage = _arrived.Token;
        if (toolContext.Locate is null && FreshLook.Enabled)
        {
            var look = new FreshLook(client, requestOptions);
            toolContext.Locate = (image, target, ct2) => look.LocateAsync(image, toolContext.GetScreenSpace().Describe(), target, ct2);
        }
        var nudges = 0;
        var loops = new LoopDetector();
        // Pointing at web_search only makes sense when the model has it.
        var errors = tools.Find("web_search") is not null ? new ErrorRepeats() : null;
        var justReset = false;

        try
        {
            // No step limit: a run ends when the model is done or the user presses Stop or Esc.
            // Tasks like "keep chatting for 12 hours" are valid;
            // runaway loops are handled by LoopDetector, and growth by context resets.
            for (var step = 1; ; step++)
            {
                ct.ThrowIfCancellationRequested();
                DeliverIncoming();
                // The reset first: its handoff note is a reply of its own, and the step's reply
                // comes after it, in a new step.
                if (contextTokens > 0 && TokensInUse() > ResetThreshold)
                    await ResetContextAsync(observer, ct).ConfigureAwait(false);
                observer.OnStepStarted(step);

                var request = new List<ChatMessage>(History.Count + 1) { ChatMessage.System(systemPrompt) };
                request.AddRange(History);

                var reply = await CompleteWithResetAsync(request, observer, ct).ConfigureAwait(false);
                var cutOff = reply.FinishReason == "length" && !reply.HasToolCalls;
                if (cutOff && contextTokens > 0 && !justReset && OutOfRoom(reply))
                {
                    // The server ran out of context mid-reply, so the size estimate was off. The
                    // cut-off reply is thrown away and this step is redone after a reset.
                    observer.OnNotice("The model ran out of context in the middle of its reply.");
                    observer.OnAssistantMessage(new ChatMessage { Role = ChatRole.Assistant, Content = "" });
                    await ResetContextAsync(observer, ct).ConfigureAwait(false);
                    justReset = true;
                    continue;
                }
                if (cutOff && !justReset)
                    observer.OnNotice("The server stopped the reply at its length limit (in LM Studio: Limit Response Length); asking the model to go on.");
                justReset = false;
                TextToolCallParser.TryExtract(reply, tools);
                // "The task is complete" followed by yet another screenshot: small models can keep
                // re-verifying forever. A completion claim with only look-only calls ends the turn.
                if (reply.HasToolCalls && LooksFinished(reply.Content) &&
                    reply.ToolCalls!.All(c => ToolCallRepair.ResolveTool(c.Name, tools)?.Risk == ToolRisk.ReadOnly))
                    reply.ToolCalls = null;
                History.Add(reply);
                observer.OnAssistantMessage(reply);

                if (!reply.HasToolCalls)
                {
                    // The user wrote while the model answered: it goes on with that.
                    if (!_incoming.IsEmpty)
                        continue;
                    // Small models sometimes announce an action ("Let me click Save.") and stop,
                    // or return nothing at all. Push them on a couple of times before giving up.
                    if (nudges < MaxNudges && (cutOff || LooksUnfinished(reply)))
                    {
                        nudges++;
                        History.Add(ChatMessage.User(NudgeText));
                        continue;
                    }
                    observer.OnFinished(AgentStopReason.Completed, null);
                    return;
                }
                nudges = 0; // it is moving again; a later stall gets its own nudges

                // Only the last screenshot goes to the model: the tools' results come before it.
                ChatMessage? shown = null;
                ContentPart? screenshot = null;
                string? caption = null;
                // This reply's calls were decided on the screen in the last screenshot.
                toolContext.Seen = toolContext.LastShot;
                foreach (var call in reply.ToolCalls!)
                {
                    var result = await RunToolAsync(call, observer, ct).ConfigureAwait(false);
                    if (loops.Check(call, result) is { } warning)
                        result = result with { Text = result.Text + "\n" + warning };
                    if (errors?.Check(call, result) is { } lookItUp)
                        result = result with { Text = result.Text + "\n" + lookItUp };
                    var message = ChatMessage.ToolResult(call.Id, result.Text);
                    History.Add(message);
                    if (result.Image is not null)
                        (shown, screenshot, caption) = (message, ContentPart.FromImage(result.Image, result.ImageMime), result.ImageCaption);
                }

                // Most OpenAI-compatible servers only accept images in user messages,
                // so the screenshot (or a picture read_file opened) is delivered right after the tool results.
                if (screenshot is not null)
                {
                    shown!.Content += caption is null ? "\n(The screenshot is attached in the next message.)" : "\n(The picture is attached in the next message.)";
                    History.Add(new ChatMessage { Role = ChatRole.User, Parts = [ContentPart.FromText(caption ?? "Screenshot after the last action:"), screenshot] });
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            observer.OnFinished(AgentStopReason.Cancelled, null);
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
        "(take a new screenshot if needed), decide, and call the next tool.";

    internal const string LongToolCallNote =
        "[BlackBox] Your last reply was lost: the connection drops when a single tool call takes more than about a minute to write. " +
        "Keep every tool call short. Write long files in parts of at most about 100 lines: create the file with write_file, " +
        "then add each next part with write_file and append set to true. Continue with the task.";
    internal const string StillTooLongNote =
        "[BlackBox] Your reply was lost again for the same reason: that part was still too long to write in under a minute. " +
        "Make each part much smaller, at most about 40 lines (about 1,500 characters), and add the rest with further " +
        "write_file calls with append set to true. Continue with the task.";

    // Marked as BlackBox's: taken for the user's words, "continue" read as a go-ahead, and the
    // model asked again for a command the user had just declined twice.
    internal const string NudgeText =
        "[BlackBox] Continue with the task. If there is another step, call the tool for it now. If the task is finished, reply with a short summary of what you did.";

    // Not "try another approach": told that after a declined listing command, the model asked for
    // it twice more in other words and then listed the folder with another tool.
    internal const string DeclinedText =
        "The user declined this action. Don't do the same thing another way; ask them what they would like instead, or go on with the parts of the task that don't need it.";

    /// <summary>
    /// Whether a reply cut off by the length limit ran out of context, rather than into a
    /// reply-length cap set on the server; without a token count it is taken as out of context.
    /// </summary>
    private bool OutOfRoom(ChatMessage reply) =>
        reply.PromptTokens is not { } prompt || prompt + (reply.CompletionTokens ?? 0) >= contextTokens * 9L / 10;

    internal static bool LooksFinished(string? content) => content is { Length: > 0 } && CompletionClaim().IsMatch(content);

    internal static bool LooksUnfinished(ChatMessage reply)
    {
        var text = (reply.Content ?? "").Trim();
        if (text.Length == 0)
            return true;
        var tail = text.Length > 240 ? text[^240..] : text;
        // "Let me know how you'd like to proceed." asks the user; it announces no step.
        return tail.EndsWith(':') || AnnouncedAction().IsMatch(tail);
    }

    [GeneratedRegex(@"\b((?<!\b(whether|if|once|when)\s+(the\s+)?)task (is|has been) (now )?(complete|completed|done|finished)|i('ve| have) (successfully )?(completed|finished) (the|your|this) task|everything is done|all done)\b", RegexOptions.IgnoreCase)]
    private static partial Regex CompletionClaim();

    [GeneratedRegex(@"(^|[.!?\n]\s*)(let me(?! know)|let's|i'll|i will|i'm going to|i am going to|next,? i|now i|i need to)\b[^.!?\n]*[.!]?\s*$", RegexOptions.IgnoreCase)]
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
            ToolCallRepair.ApplyNameHints(call.Name, args, tool);
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
            var denied = ToolResult.Error(DeclinedText);
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
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // Whatever goes wrong in one tool (a timeout, a file in use, a bug) is the model's to
            // work around; only Stop ends the run.
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
                observer.OnStepStarted(0); // the retried reply is a new one, after the handoff note
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
                return await client.CompleteAsync(request, toolDefs, options, onDelta, ct, observer.OnSending).ConfigureAwait(false);
            }
            catch (ConnectionLostException ex) when (++drops <= MaxReconnects)
            {
                observer.OnNotice($"Connection lost ({ex.Message}). Reconnecting, attempt {drops} of {MaxReconnects}…");
                // Resending would only repeat a tool call too long to get through, so the model is
                // asked to split it. The note stays in the history so later calls stay short too.
                // If the smaller parts are still too long, it is told once more, with a firmer limit.
                if (ex.ToolCallInProgress is not null && toolDefs.Count > 0 && request[^1].Content != StillTooLongNote)
                {
                    var again = request[^1].Content == LongToolCallNote;
                    var note = ChatMessage.User(again ? StillTooLongNote : LongToolCallNote);
                    History.Add(note);
                    request = [.. request, note];
                    observer.OnNotice(again
                        ? "The model's smaller parts were still too long; asking for much smaller ones."
                        : "The reply was lost while the model was writing a long tool call; asking it to write in smaller parts.");
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

    /// <summary>Rough token count of the conversation (about 3.5 characters per token; a screenshot one per 32x32 pixels).</summary>
    internal int EstimateTokens() =>
        (int)(((long)systemPrompt.Length + _toolChars.Value + History.Sum(CharsOf)) * 2 / 7);

    /// <summary>Size of the tool definitions, which go into every request; MCP servers can add a lot.</summary>
    private readonly Lazy<long> _toolChars = new(() =>
        tools.Definitions().Sum(d => (long)d.Name.Length + d.Description.Length + d.Parameters.ToJsonString().Length + 40));

    /// <summary>Rough token count of one message.</summary>
    internal static int EstimateTokens(ChatMessage m) => (int)((long)CharsOf(m) * 2 / 7);

    private static long CharsOf(ChatMessage m) =>
        (m.Content?.Length ?? 0) + (m.Reasoning?.Length ?? 0) +
        (m.ToolCalls?.Sum(c => c.Name.Length + c.Arguments.Length) ?? 0) +
        (m.Parts?.Sum(p => p.IsImage ? ImageTokens(p.ImageBytes!) * 7 / 2 : p.Text?.Length ?? 0) ?? 0);

    /// <summary>What the Qwen vision encoder spends on an image: a token per 32x32 pixels (2000 if it can't be read).</summary>
    internal static long ImageTokens(byte[] image)
    {
        using var codec = SkiaSharp.SKCodec.Create(new SkiaSharp.SKMemoryStream(image));
        return codec is null ? 2000 : (long)Math.Ceiling(codec.Info.Width / 32.0) * (long)Math.Ceiling(codec.Info.Height / 32.0);
    }

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
    private void RepairDanglingToolCalls() => RepairDanglingToolCalls(History);

    /// <inheritdoc cref="RepairDanglingToolCalls()"/>
    public static void RepairDanglingToolCalls(List<ChatMessage> history)
    {
        for (var i = history.Count - 1; i >= 0; i--)
        {
            var msg = history[i];
            if (msg.Role != ChatRole.Assistant || !msg.HasToolCalls)
                continue;
            var answered = history.Skip(i + 1).Where(m => m.Role == ChatRole.Tool).Select(m => m.ToolCallId).ToHashSet();
            var insertAt = i + 1 + history.Skip(i + 1).TakeWhile(m => m.Role == ChatRole.Tool).Count();
            foreach (var call in msg.ToolCalls!.Where(c => !answered.Contains(c.Id)))
                history.Insert(insertAt++, ChatMessage.ToolResult(call.Id, "Not run: the user stopped the agent."));
            break;
        }
    }
}
