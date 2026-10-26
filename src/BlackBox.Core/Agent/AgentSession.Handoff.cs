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
        "with their full paths, important values), decisions you made, and exactly what to do next, a check of the result not done yet " +
        "included: that is part of the task, not optional. Be concrete and complete but " +
        "keep it under about 600 words. Reply with the note as plain text and do not call any tools.";

    internal const string HandoffHeader =
        "[BlackBox] The context was reset to make room. This is your handoff note from before the reset; continue the task from here:";

    private const string CurrentScreenText = "The screen right now:";

    /// <summary>Room the handoff reply itself may use (thinking included).</summary>
    private const int HandoffMaxTokens = 8_192;

    /// <summary>The prompt size (in server-reported tokens where known) at which the context is reset.</summary>
    /// <remarks>
    /// A fifth of the context (at least 16k) stays free: enough for the reply in progress and for
    /// the handoff note, which is written in the same context before the reset. Never more than
    /// half, though: with 16k or less, 16k free would mean a reset before every step.
    /// </remarks>
    public int ResetThreshold => contextTokens - Math.Min(Math.Max(contextTokens / 5, 16_384), contextTokens / 2);

    /// <summary>
    /// The conversation's size in tokens: the server's exact count for the last reply (its prompt
    /// and the reply itself) plus an estimate for the messages added since; a pure estimate before
    /// the first reply, after a reset, or when the server reports no usage.
    /// </summary>
    /// <remarks>
    /// The count is read from the history, so a session that continues a chat (the app starts one
    /// for every message) starts from the server's count too. The estimate alone can be a third
    /// too low on text full of numbers, which put the reset too late.
    /// </remarks>
    public int TokensInUse()
    {
        var last = History.FindLastIndex(m => m.PromptTokens is not null);
        if (last < 0)
            return EstimateTokens();
        var reply = History[last];
        return reply.PromptTokens!.Value + (reply.CompletionTokens ?? EstimateTokens(reply)) + History.Skip(last + 1).Sum(EstimateTokens);
    }

    /// <summary>
    /// Asks the model for a handoff note on the current (cached) history, then replaces the
    /// history with the user's messages, the note and the latest screenshot. If the model can't
    /// write the note (no room left, connection gone), a note is assembled from the last steps.
    /// </summary>
    /// <param name="overflowed">The server refused the conversation as too long (or too big, or more input than a minute takes):
    /// the note is asked for on its latest part, without pictures, which fits.</param>
    internal async Task ResetContextAsync(IAgentObserver observer, CancellationToken ct, bool overflowed = false)
    {
        var before = TokensInUse();
        observer.OnNotice("The context is nearly full. Asking the model for a handoff note, then starting fresh…");

        string note;
        try
        {
            note = await WriteHandoffNoteAsync(observer, overflowed, ct).ConfigureAwait(false);
        }
        catch (LlmException ex)
        {
            observer.OnNotice("The model couldn't write a handoff note (" + ex.Message + "); using a record of the last steps instead.", ex);
            note = "";
        }
        if (note.Length == 0)
            note = FallbackNote();

        // The user's messages, and where they stopped a task: without it the model took up a task
        // the user had stopped again after the reset, from its own note's "not done yet".
        var userMessages = History.Where(m => m.FromUser || m.Content == StoppedNote).ToList();
        // The last screenshot, not the last picture: a picture the user attached (kept with their
        // message anyway), one read_file opened or a ComfyUI job's result was put after the note
        // as "The screen right now:". The screen an earlier reset carried counts: a second reset
        // before the next screenshot kept none.
        var screen = History.LastOrDefault(m => !m.FromUser && m.Parts is [{ Text: { } said }, { IsImage: true }] &&
                                                (said == ScreenshotText || said.StartsWith(CurrentScreenText, StringComparison.Ordinal)))?.Parts![1];
        // Which monitor it shows goes with it: a later run looks at that one (see ScreenShown).
        var shown = ScreenShown(History);
        History.Clear();
        History.AddRange(userMessages);
        History.Add(ChatMessage.User(HandoffHeader + "\n\n" + note));
        // A project's overview went with the rest: indexing it again brings it back. So did the
        // guides tools showed once (the storyboard guide).
        _projects.Forget();
        toolContext.ContextResets++;
        if (screen is not null)
            History.Add(new ChatMessage { Role = ChatRole.User, Parts = [ContentPart.FromText(shown is { } s ? $"{CurrentScreenText} screen {s.Screen + 1} of {s.Count}." : CurrentScreenText), screen] });

        // Very long user messages alone could still be too big; trimming is the last resort.
        if (EstimateTokens() > ResetThreshold)
            FitContext(ResetThreshold);
        observer.OnNotice($"Context reset: about {before / 1000}k tokens replaced by a handoff note ({EstimateTokens() / 1000 + 1}k).");
    }

    private async Task<string> WriteHandoffNoteAsync(IAgentObserver observer, bool overflowed, CancellationToken ct)
    {
        // Same tools and options as every step, so the prompt matches the server's cache up to
        // the request message; only the length limit differs, and it isn't part of the prompt.
        var options = new ChatRequestOptions
        {
            Model = requestOptions.Model,
            MaxTokens = HandoffMaxTokens,
            Extra = requestOptions.Extra,
            SendReasoning = requestOptions.SendReasoning,
            // Claude's cache holds the conversation for one effort: another would read it all anew.
            Effort = requestOptions.Effort,
        };
        var request = new List<ChatMessage>(History.Count + 2) { ChatMessage.System(systemPrompt) };
        request.AddRange(overflowed ? LatestPart() : History);
        request.Add(ChatMessage.User(HandoffRequest));

        observer.OnStepStarted(0);
        var guard = new RepetitionGuard(observer.OnDelta);
        var reply = await CompleteWithReconnectAsync(request, tools.Definitions(), options, observer, guard.OnDelta, ct).ConfigureAwait(false);
        observer.OnAssistantMessage(new ChatMessage { Role = ChatRole.Assistant, Content = reply.Content, Reasoning = reply.Reasoning });

        // A note cut off by the length limit is still worth keeping; a tool call instead of a
        // note is not.
        return (reply.Content ?? "").Trim();
    }

    /// <summary>
    /// The conversation from its end back to half the context, without pictures or thinking, for
    /// a note after the whole of it was refused: asked on the history that had just failed, plus
    /// a message, the note failed every time, and only the record of the last steps was left. The
    /// user's first message comes first, as what the task is.
    /// </summary>
    private List<ChatMessage> LatestPart()
    {
        var plain = Chats.ChatStore.ForSaving(History);
        foreach (var m in plain)
            m.Reasoning = null;
        var budget = Math.Max(8_192, contextTokens / 2);
        var start = plain.Count;
        for (var used = 0; start > 0 && used + EstimateTokens(plain[start - 1]) <= budget; start--)
            used += EstimateTokens(plain[start - 1]);
        // Not on a tool's result whose call was left out.
        while (start < plain.Count && plain[start].Role == ChatRole.Tool)
            start++;
        var part = plain.Skip(start).ToList();
        if (plain.FirstOrDefault(m => m.FromUser) is { } task && !part.Contains(task))
            part.Insert(0, task);
        return part;
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
                if (!string.IsNullOrWhiteSpace(m.Content)) sb.Append("You said: ").AppendLine(ClipMiddle(m.Content, 600));
                foreach (var c in m.ToolCalls ?? [])
                    sb.Append("You called ").Append(c.Name).Append(' ').AppendLine(ClipMiddle(c.Arguments, 300));
            }
            else if (m.Role == ChatRole.Tool)
                sb.Append("Result: ").AppendLine(ClipMiddle(m.Content, 300));
        }
        return sb.ToString();
    }

    /// <summary>The start and the end of a long text, which say most about a step.</summary>
    private static string ClipMiddle(string? s, int max)
    {
        s = (s ?? "").Trim();
        return s.Length <= max ? s : s[..(max / 2)] + " … " + s[^(max / 2)..];
    }
}
