using BlackBox.Core.Llm;
using BlackBox.Core.Settings;
using BlackBox.Core.Tools;

namespace BlackBox.Core.Agent;

public enum AgentStopReason { Completed, MaxSteps, Cancelled, Failsafe, Error }

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
    private const string OmittedScreenshot = "[earlier screenshot omitted to save context]";

    /// <summary>Conversation so far (without the system prompt). Can be shared across sessions so settings changes keep the chat.</summary>
    public List<ChatMessage> History { get; } = history ?? [];

    public async Task RunAsync(string userText, IAgentObserver observer, CancellationToken ct)
    {
        History.Add(ChatMessage.User(userText));
        var settings = toolContext.Settings;
        var toolDefs = tools.Definitions();
        var nudges = 0;
        var actionCounts = new Dictionary<string, int>();

        try
        {
            for (var step = 1; step <= settings.MaxSteps; step++)
            {
                ct.ThrowIfCancellationRequested();
                observer.OnStepStarted(step);
                PruneScreenshots(settings.KeepScreenshots, PruneSlack);
                if (contextTokens > 0)
                    await EnsureFitsAsync(PromptBudget, observer, ct).ConfigureAwait(false);

                var request = new List<ChatMessage>(History.Count + 1) { ChatMessage.System(systemPrompt) };
                request.AddRange(History);

                ChatMessage reply;
                try
                {
                    reply = await CompleteFittingAsync(request, requestOptions, observer, ct).ConfigureAwait(false);
                }
                catch (ReasoningBudgetExceededException ex) when (WithoutThinking(requestOptions) is { } direct)
                {
                    // Quantized models can get stuck deliberating. Cut the thought short and ask
                    // again with thinking switched off for this one step.
                    observer.OnAssistantMessage(new ChatMessage { Role = ChatRole.Assistant, Content = "", Reasoning = ex.PartialReasoning + "\n[thinking cut short]" });
                    observer.OnStepStarted(step);
                    reply = await CompleteFittingAsync(request, direct, observer, ct).ConfigureAwait(false);
                }
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

                var screenshots = new List<ContentPart>();
                foreach (var call in reply.ToolCalls!)
                {
                    var result = await RunToolAsync(call, observer, ct).ConfigureAwait(false);
                    var signature = call.Name + " " + call.Arguments.Replace(" ", "");
                    var repeats = actionCounts[signature] = actionCounts.GetValueOrDefault(signature) + 1;
                    if (repeats >= 3 && call.Name is not ("screenshot" or "wait" or "zoom" or "list_ui_elements" or "list_windows"))
                        result = result with { Text = result.Text + $"\nNote: you have now made exactly this call {repeats} times. If it is not getting you closer, stop repeating it and try a different approach." };
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

            observer.OnFinished(AgentStopReason.MaxSteps, $"Stopped after {settings.MaxSteps} steps. Send another message to let it continue.");
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

    /// <summary>
    /// Screenshots are pruned in batches: rewriting old messages invalidates the server's prompt
    /// cache from that point on, so doing it every step would reprocess most of the prompt each time.
    /// </summary>
    private const int PruneSlack = 3;

    /// <summary>Request options with thinking disabled, or null if the backend has no such switch.</summary>
    internal static ChatRequestOptions? WithoutThinking(ChatRequestOptions options)
    {
        if (options.Extra?["chat_template_kwargs"] is not System.Text.Json.Nodes.JsonObject)
            return null;
        var extra = (System.Text.Json.Nodes.JsonObject)options.Extra.DeepClone();
        extra["chat_template_kwargs"]!["enable_thinking"] = false;
        return new ChatRequestOptions
        {
            Model = options.Model,
            Temperature = options.Temperature,
            TopP = options.TopP,
            MaxTokens = options.MaxTokens,
            SendReasoning = options.SendReasoning,
            Extra = extra,
        };
    }

    private const int MaxNudges = 2;

    /// <summary>How many dropped connections one step survives.</summary>
    internal const int MaxReconnects = 12;
    private const string NudgeText =
        "Continue with the task. If there is another step, call the tool for it now. If the task is finished, reply with a short summary of what you did.";

    internal static bool LooksFinished(string? content) =>
        content is { Length: > 0 } &&
        System.Text.RegularExpressions.Regex.IsMatch(content,
            @"\b((?<!\b(whether|if|once|when)\s+(the\s+)?)task (is|has been) (now )?(complete|completed|done|finished)|i('ve| have) (successfully )?(completed|finished) (the|your|this) task|everything is done|all done)\b",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    internal static bool LooksUnfinished(ChatMessage reply)
    {
        var text = (reply.Content ?? "").Trim();
        if (text.Length == 0)
            return true;
        var tail = text.Length > 240 ? text[^240..] : text;
        return tail.EndsWith(':') ||
               System.Text.RegularExpressions.Regex.IsMatch(tail,
                   @"(^|[.!?\n]\s*)(let me|let's|i'll|i will|i'm going to|i am going to|next,? i|now i|i need to)\b[^.!?\n]*[.!]?\s*$",
                   System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }

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

        System.Text.Json.Nodes.JsonObject args;
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
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or InvalidOperationException or ArgumentException or System.Runtime.InteropServices.ExternalException or TimeoutException)
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
    /// Sends the request; if the server says it does not fit, trims history much harder and
    /// tries again (twice at most) before giving up.
    /// </summary>
    private async Task<ChatMessage> CompleteFittingAsync(List<ChatMessage> request, ChatRequestOptions options, IAgentObserver observer, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return await CompleteWithReconnectAsync(request, tools.Definitions(), options, observer, observer.OnDelta, ct).ConfigureAwait(false);
            }
            catch (ContextOverflowException) when (attempt < 2)
            {
                var before = EstimateTokens();
                PruneScreenshots(1);
                await EnsureFitsAsync(before / 2, observer, ct).ConfigureAwait(false);
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
                if (ex.ReceivedTokens && onDelta is not null)
                {
                    // Discard the half-streamed reply; the retry produces it again.
                    observer.OnAssistantMessage(new ChatMessage { Role = ChatRole.Assistant, Content = "" });
                    observer.OnStepStarted(0);
                }
                await Task.Delay(TimeSpan.FromSeconds(Math.Min(2 * drops, 10)), ct).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Rough token count of the conversation (about 3.5 characters per token, ~1300 per screenshot).</summary>
    internal int EstimateTokens() =>
        (systemPrompt.Length + 12_000 /* tool schemas */ + History.Sum(m =>
            (m.Content?.Length ?? 0) + (m.Reasoning?.Length ?? 0) +
            (m.ToolCalls?.Sum(c => c.Name.Length + c.Arguments.Length) ?? 0) +
            (m.Parts?.Sum(p => p.IsImage ? 4_500 : p.Text?.Length ?? 0) ?? 0))) * 2 / 7;

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
    /// Once more than <paramref name="keep"/> + <paramref name="slack"/> screenshots are in the
    /// history, drops all but the newest <paramref name="keep"/>.
    /// </summary>
    internal void PruneScreenshots(int keep, int slack = 0)
    {
        var total = History.Sum(m => m.Parts?.Count(p => p.IsImage) ?? 0);
        if (total <= keep + slack)
            return;
        var seen = 0;
        for (var i = History.Count - 1; i >= 0; i--)
        {
            var parts = History[i].Parts;
            if (parts is null) continue;
            for (var p = parts.Count - 1; p >= 0; p--)
            {
                if (!parts[p].IsImage) continue;
                if (++seen > keep)
                    parts[p] = ContentPart.FromText(OmittedScreenshot);
            }
        }
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
