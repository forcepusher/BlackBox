using System.Text;
using BlackBox.Core.Llm;

namespace BlackBox.Core.Agent;

/// <summary>
/// Context compaction: when the conversation outgrows its budget, the middle of the run is
/// replaced by a summary the model writes itself. The original task and the most recent steps
/// stay verbatim.
/// </summary>
/// <remarks>
/// Built for unstable links: the middle is summarized in small chunks (rolling summary), so each
/// request is short enough to be processed well within a proxy's idle cutoff, and every request
/// goes through the reconnect loop. If compaction still fails, the plain trimming in
/// <see cref="FitContext"/> takes over, so a run never stops because of context size.
/// </remarks>
public sealed partial class AgentSession
{
    internal const string SummaryHeader = "[Summary of the earlier steps of this task, written to save context]";

    /// <summary>Messages at the end of the history that are always kept verbatim.</summary>
    internal const int KeepRecentMessages = 8;

    /// <summary>
    /// Size of one compaction chunk, about 24k tokens: under a minute of prompt processing on a
    /// single consumer GPU, so even a link that cuts silent connections gets each one through
    /// (and if not, the resend resumes from the server's cache).
    /// </summary>
    internal const int ChunkChars = 84_000;

    private const string CompactionSystemPrompt =
        "You compress the history of a computer-use agent so it can continue its task with less context. " +
        "Write a concise factual summary in plain text: what was done, what worked, what failed, " +
        "important values found (file paths, numbers, names, URLs, window titles), and the current state " +
        "of the screen and task. Keep everything still needed to finish the task; drop chatter and repetition. " +
        "Do not invent anything. Do not call tools.";

    /// <summary>
    /// The whole context the server reports is used. Compaction starts only when the prompt
    /// would leave less than a reply's worth of room (16k, or an eighth of small contexts), with
    /// a little slack because token counts here are estimates.
    /// </summary>
    private int PromptBudget => (int)(contextTokens * 0.95) - Math.Min(16_384, contextTokens / 8);

    /// <summary>
    /// Brings the conversation under <paramref name="budget"/> tokens: old screenshots and old
    /// thinking go first, then the middle of the run is compacted into a summary, and only if
    /// that fails are old outputs and steps cut.
    /// </summary>
    internal async Task EnsureFitsAsync(int budget, IAgentObserver observer, CancellationToken ct)
    {
        if (budget <= 0 || EstimateTokens() <= budget)
            return;

        DropOldReasoning(budget);
        if (EstimateTokens() <= budget)
            return;

        try
        {
            // Aim well below the budget so the next compaction is many steps away; every
            // compaction changes the prompt prefix and costs one uncached pass.
            await CompactAsync(observer, ct, keepTokens: budget / 2).ConfigureAwait(false);
        }
        catch (LlmException ex)
        {
            observer.OnNotice("Could not summarize earlier steps (" + ex.Message + "); trimming them instead.");
        }

        FitContext(budget);
    }

    private void DropOldReasoning(int budget)
    {
        var assistants = History.Where(m => m.Role == ChatRole.Assistant && m.Reasoning is not null).ToList();
        foreach (var m in assistants.Take(Math.Max(0, assistants.Count - 3)))
        {
            m.Reasoning = null;
            if (EstimateTokens() <= budget) return;
        }
    }

    /// <summary>Replaces the middle of the history with a model-written summary. Returns false if there was nothing to compact.</summary>
    /// <param name="keepTokens">
    /// How much recent history stays verbatim (at least <see cref="KeepRecentMessages"/> messages).
    /// Keeping about half the budget means detailed recent work survives, the next compaction is
    /// far away, and only the older half has to be summarized.
    /// </param>
    internal async Task<bool> CompactAsync(IAgentObserver observer, CancellationToken ct, int keepTokens = 0)
    {
        // Keep the first message (the task) and the recent tail. Never split a tool call from its results.
        var start = 1;
        var end = History.Count - KeepRecentMessages;
        for (var kept = History.Skip(end).Sum(EstimateTokens); end > start + 1; end--)
        {
            var next = EstimateTokens(History[end - 1]);
            if (kept + next > keepTokens) break;
            kept += next;
        }
        while (end > start && History[end].Role == ChatRole.Tool)
            end--;
        if (end - start < 2)
            return false;

        var middle = History.GetRange(start, end - start);
        var chunks = Chunk(Render(middle), ChunkChars);
        var options = new ChatRequestOptions
        {
            Model = requestOptions.Model,
            // This request is BlackBox's own, so it sets its own limits: a factual, bounded summary.
            Temperature = 0.1,
            MaxTokens = 1500,
        };

        var summary = "";
        for (var i = 0; i < chunks.Count; i++)
        {
            observer.OnNotice($"Summarizing earlier steps to save context ({i + 1}/{chunks.Count})…");
            var prompt = new StringBuilder();
            prompt.AppendLine("The task: " + (History[0].Content ?? "").Trim());
            if (summary.Length > 0)
                prompt.AppendLine().AppendLine("Summary so far:").AppendLine(summary);
            prompt.AppendLine().AppendLine(summary.Length > 0 ? "Further steps:" : "Steps:").AppendLine(chunks[i]);
            prompt.AppendLine().Append("Write the updated summary of everything above.");

            var reply = await CompleteWithReconnectAsync(
                [ChatMessage.System(CompactionSystemPrompt), ChatMessage.User(prompt.ToString())],
                [], options, observer, onDelta: null, ct).ConfigureAwait(false);
            var text = (reply.Content ?? "").Trim();
            if (text.Length == 0)
                throw new LlmException("the model returned an empty summary");
            summary = text;
        }

        History.RemoveRange(start, end - start);
        History.Insert(start, ChatMessage.User(SummaryHeader + "\n" + summary));
        observer.OnNotice($"Earlier steps summarized: {middle.Count} messages became one summary.");
        return true;
    }

    /// <summary>Plain-text transcript of messages for the summarizer (no images, long parts clipped).</summary>
    internal static string Render(IEnumerable<ChatMessage> messages)
    {
        var sb = new StringBuilder();
        foreach (var m in messages)
        {
            switch (m.Role)
            {
                case ChatRole.User when m.Parts is not null:
                    sb.AppendLine("[screenshot]");
                    break;
                case ChatRole.User:
                    sb.Append("User: ").AppendLine(Clip(m.Content, 4000));
                    break;
                case ChatRole.Assistant:
                    if (!string.IsNullOrWhiteSpace(m.Reasoning)) sb.Append("Agent thought: ").AppendLine(Clip(m.Reasoning, 1200));
                    if (!string.IsNullOrWhiteSpace(m.Content)) sb.Append("Agent: ").AppendLine(Clip(m.Content, 2000));
                    foreach (var c in m.ToolCalls ?? [])
                        sb.Append("Agent called ").Append(c.Name).Append(' ').AppendLine(Clip(c.Arguments, 600));
                    break;
                case ChatRole.Tool:
                    sb.Append("Result: ").AppendLine(Clip(m.Content, 2000));
                    break;
            }
        }
        return sb.ToString();
    }

    internal static List<string> Chunk(string text, int maxChars)
    {
        var chunks = new List<string>();
        var current = new StringBuilder();
        foreach (var line in text.Split('\n'))
        {
            if (current.Length + line.Length > maxChars && current.Length > 0)
            {
                chunks.Add(current.ToString());
                current.Clear();
            }
            current.AppendLine(line.Length > maxChars ? line[..maxChars] : line);
        }
        if (current.Length > 0)
            chunks.Add(current.ToString());
        return chunks;
    }

    private static string Clip(string? s, int max)
    {
        s = (s ?? "").Trim();
        return s.Length <= max ? s : s[..(max / 2)] + " … " + s[^(max / 2)..];
    }
}
