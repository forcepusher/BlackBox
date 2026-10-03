using System.Text;
using BlackBox.Core.Llm;

namespace BlackBox.Core.Agent;

/// <summary>
/// Context resets. When the conversation is about to fill the model's context, the model writes
/// a handoff note for itself, and the history is replaced by the user's own messages, that note
/// and the latest screenshot. The run then carries on in a small, fresh context.
/// </summary>
/// <remarks>
/// Why a reset rather than trimming or summarizing the middle: until the reset, the history only
/// grows at the end, so the server's cache always holds and every step processes only the new
/// messages. The handoff request is the same cached prompt plus one short message, so the note
/// starts streaming almost at once (no silent stretch for a proxy to cut). After the reset the
/// new prompt is a few thousand tokens, so the one full reprocess it costs takes seconds.
/// Editing old messages instead makes servers with hybrid models (Qwen 3.5+) reprocess the
/// whole long prompt every time, which over an unstable link means minutes of dropped connections.
/// </remarks>
public sealed partial class AgentSession
{
    internal const string HandoffRequest =
        "[BlackBox] Your context is almost full and will be reset after this reply. Write a handoff note for yourself: " +
        "after the reset you will see only the user's messages, this note and the current screenshot. Cover what the user asked for, " +
        "what you have done so far and what worked or failed, the current state (open windows and apps, files you created or changed " +
        "with their full paths, important values), decisions you made, and exactly what to do next. Be concrete and complete but " +
        "keep it under about 600 words. Reply with the note as plain text and do not call any tools.";

    internal const string HandoffHeader =
        "[BlackBox] The context was reset to make room. This is your handoff note from before the reset; continue the task from here:";

    private const string CurrentScreenText = "The screen right now:";

    /// <summary>Room the handoff reply itself may use (thinking included).</summary>
    private const int HandoffMaxTokens = 8_192;

    /// <summary>The prompt size (in server-reported tokens where known) at which the context is reset.</summary>
    /// <remarks>
    /// A fifth of the context (at least 16k) stays free: enough for the reply in progress and for
    /// the handoff note, which is written in the same context before the reset.
    /// </remarks>
    internal int ResetThreshold => contextTokens - Math.Max(contextTokens / 5, 16_384);

    // Last exact size the server reported, and how many history messages it covered.
    private int? _measuredTokens;
    private int _measuredCount;

    /// <summary>
    /// The conversation's size in tokens: the server's last exact count plus an estimate for the
    /// messages added since; a pure estimate when the server reports no usage.
    /// </summary>
    internal int TokensInUse()
    {
        if (_measuredTokens is { } measured && _measuredCount <= History.Count)
            return measured + History.Skip(_measuredCount).Sum(EstimateTokens);
        return EstimateTokens();
    }

    private void RecordUsage(ChatMessage reply)
    {
        if (reply.PromptTokens is { } prompt)
        {
            _measuredTokens = prompt + (reply.CompletionTokens ?? EstimateTokens(reply));
            _measuredCount = History.Count; // the reply has just been added
        }
    }

    /// <summary>
    /// Asks the model for a handoff note on the current (cached) history, then replaces the
    /// history with the user's messages, the note and the latest screenshot. If the model can't
    /// write the note (no room left, connection gone), a note is assembled from the last steps.
    /// </summary>
    internal async Task ResetContextAsync(IAgentObserver observer, CancellationToken ct)
    {
        var before = TokensInUse();
        observer.OnNotice("The context is nearly full. Asking the model for a handoff note, then starting fresh…");

        string note;
        try
        {
            note = await WriteHandoffNoteAsync(observer, ct).ConfigureAwait(false);
        }
        catch (LlmException ex)
        {
            observer.OnNotice("The model couldn't write a handoff note (" + ex.Message + "); using a record of the last steps instead.");
            note = "";
        }
        if (note.Length == 0)
            note = FallbackNote();

        var userMessages = History.Where(m => m.FromUser).ToList();
        var screen = History.LastOrDefault(m => m.Parts?.Any(p => p.IsImage) == true)?.Parts?.LastOrDefault(p => p.IsImage);
        History.Clear();
        History.AddRange(userMessages);
        History.Add(ChatMessage.User(HandoffHeader + "\n\n" + note));
        if (screen is not null)
            History.Add(new ChatMessage { Role = ChatRole.User, Parts = [ContentPart.FromText(CurrentScreenText), screen] });
        _measuredTokens = null;

        // Very long user messages alone could still be too big; trimming is the last resort.
        if (EstimateTokens() > ResetThreshold)
            FitContext(ResetThreshold);
        observer.OnNotice($"Context reset: about {before / 1000}k tokens replaced by a handoff note ({EstimateTokens() / 1000 + 1}k).");
    }

    private async Task<string> WriteHandoffNoteAsync(IAgentObserver observer, CancellationToken ct)
    {
        // Same tools and options as every step, so the prompt matches the server's cache up to
        // the request message; only the length limit differs, and it isn't part of the prompt.
        var options = new ChatRequestOptions
        {
            Model = requestOptions.Model,
            Temperature = requestOptions.Temperature,
            MaxTokens = HandoffMaxTokens,
            Extra = requestOptions.Extra,
            SendReasoning = requestOptions.SendReasoning,
        };
        var request = new List<ChatMessage>(History.Count + 2) { ChatMessage.System(systemPrompt) };
        request.AddRange(History);
        request.Add(ChatMessage.User(HandoffRequest));

        observer.OnStepStarted(0);
        var guard = new RepetitionGuard(observer.OnDelta);
        var reply = await CompleteWithReconnectAsync(request, tools.Definitions(), options, observer, guard.OnDelta, ct).ConfigureAwait(false);
        observer.OnAssistantMessage(new ChatMessage { Role = ChatRole.Assistant, Content = reply.Content, Reasoning = reply.Reasoning });

        // A note cut off by the length limit is still worth keeping; a tool call instead of a
        // note is not.
        return (reply.Content ?? "").Trim();
    }

    /// <summary>Drops the thinking of all but the last three replies, oldest first, until under budget.</summary>
    private void DropOldReasoning(int budget)
    {
        var assistants = History.Where(m => m.Role == ChatRole.Assistant && m.Reasoning is not null).ToList();
        foreach (var m in assistants.Take(Math.Max(0, assistants.Count - 3)))
        {
            m.Reasoning = null;
            if (EstimateTokens() <= budget) return;
        }
    }

    /// <summary>A plain record of the last steps, for when the model can't write its own note.</summary>
    private string FallbackNote()
    {
        var sb = new StringBuilder("(Written by BlackBox: the model could not write a note. These were your last steps.)\n");
        foreach (var m in History.Where(m => !m.FromUser).TakeLast(16))
        {
            if (m.Role == ChatRole.Assistant)
            {
                if (!string.IsNullOrWhiteSpace(m.Content)) sb.Append("You said: ").AppendLine(Clip(m.Content, 600));
                foreach (var c in m.ToolCalls ?? [])
                    sb.Append("You called ").Append(c.Name).Append(' ').AppendLine(Clip(c.Arguments, 300));
            }
            else if (m.Role == ChatRole.Tool)
                sb.Append("Result: ").AppendLine(Clip(m.Content, 300));
        }
        return sb.ToString();
    }

    private static string Clip(string? s, int max)
    {
        s = (s ?? "").Trim();
        return s.Length <= max ? s : s[..(max / 2)] + " … " + s[^(max / 2)..];
    }
}
