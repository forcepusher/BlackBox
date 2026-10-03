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

    /// <summary>Messages at the end of the history that are never compacted.</summary>
    internal const int KeepRecentMessages = 8;

    /// <summary>
    /// Size of one compaction chunk. About 8k tokens: even uncached that is well under a minute
    /// of prompt processing on a single consumer GPU.
    /// </summary>
    internal const int ChunkChars = 28_000;

    private const string CompactionSystemPrompt =
        "You compress the history of a computer-use agent so it can continue its task with less context. " +
        "Write a concise factual summary in plain text: what was done, what worked, what failed, " +
        "important values found (file paths, numbers, names, URLs, window titles), and the current state " +
        "of the screen and task. Keep everything still needed to finish the task; drop chatter and repetition. " +
        "Do not invent anything. Do not call tools.";

    private int PromptBudget => (int)(contextTokens * 0.8) - (requestOptions.MaxTokens ?? 4096);

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
            await CompactAsync(observer, ct).ConfigureAwait(false);
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
    internal async Task<bool> CompactAsync(IAgentObserver observer, CancellationToken ct)
    {
        // Keep the first message (the task) and the recent tail. Never split a tool call from its results.
        var start = 1;
        var end = History.Count - KeepRecentMessages;
        while (end > start && History[end].Role == ChatRole.Tool)
            end--;
        if (end - start < 2)
            return false;

        var middle = History.GetRange(start, end - start);
        var chunks = Chunk(Render(middle), ChunkChars);
        var options = new ChatRequestOptions
        {
            Model = requestOptions.Model,
            Temperature = 0.1,
            MaxTokens = 1500,
            Extra = WithoutThinking(requestOptions)?.Extra,
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
