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
    void OnToolFinished(ToolCall call, ToolResult result);
    void OnFinished(AgentStopReason reason, string? detail);
}

/// <summary>
/// One conversation with the model. Each user turn runs the loop: ask the model, run the tools it
/// calls, feed results back, repeat until it answers without tool calls.
/// </summary>
public sealed class AgentSession(
    OpenAiChatClient client,
    ChatRequestOptions requestOptions,
    ToolRegistry tools,
    ToolContext toolContext,
    string systemPrompt,
    List<ChatMessage>? history = null)
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

                var request = new List<ChatMessage>(History.Count + 1) { ChatMessage.System(systemPrompt) };
                request.AddRange(History);

                ChatMessage reply;
                try
                {
                    reply = await client.CompleteAsync(request, toolDefs, requestOptions, observer.OnDelta, ct).ConfigureAwait(false);
                }
                catch (ReasoningBudgetExceededException ex) when (WithoutThinking(requestOptions) is { } direct)
                {
                    // Quantized models can get stuck deliberating. Cut the thought short and ask
                    // again with thinking switched off for this one step.
                    observer.OnAssistantMessage(new ChatMessage { Role = ChatRole.Assistant, Content = "", Reasoning = ex.PartialReasoning + "\n[thinking cut short]" });
                    observer.OnStepStarted(step);
                    reply = await client.CompleteAsync(request, toolDefs, direct, observer.OnDelta, ct).ConfigureAwait(false);
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
            Extra = extra,
        };
    }

    private const int MaxNudges = 2;
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
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or InvalidOperationException or ArgumentException)
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
