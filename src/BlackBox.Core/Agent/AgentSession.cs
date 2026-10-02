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
        var toolNames = tools.All.Select(t => t.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

        try
        {
            for (var step = 1; step <= settings.MaxSteps; step++)
            {
                ct.ThrowIfCancellationRequested();
                observer.OnStepStarted(step);
                PruneScreenshots(settings.KeepScreenshots);

                var request = new List<ChatMessage>(History.Count + 1) { ChatMessage.System(systemPrompt) };
                request.AddRange(History);

                var reply = await client.CompleteAsync(request, toolDefs, requestOptions, observer.OnDelta, ct).ConfigureAwait(false);
                TextToolCallParser.TryExtract(reply, toolNames);
                History.Add(reply);
                observer.OnAssistantMessage(reply);

                if (!reply.HasToolCalls)
                {
                    observer.OnFinished(AgentStopReason.Completed, null);
                    return;
                }

                var screenshots = new List<ContentPart>();
                foreach (var call in reply.ToolCalls!)
                {
                    var result = await RunToolAsync(call, observer, ct).ConfigureAwait(false);
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

    private async Task<ToolResult> RunToolAsync(ToolCall call, IAgentObserver observer, CancellationToken ct)
    {
        var tool = tools.Find(call.Name);
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
            args = ToolArgs.Parse(call.Arguments);
            summary = tool.Describe(args);
        }
        catch (ToolArgumentException ex)
        {
            var bad = ToolResult.Error(ex.Message);
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
        catch (Exception ex) when (ex is ToolArgumentException or IOException or UnauthorizedAccessException or NotSupportedException or InvalidOperationException or ArgumentException)
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

    /// <summary>Keeps only the newest <paramref name="keep"/> screenshots in the history.</summary>
    internal void PruneScreenshots(int keep)
    {
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
