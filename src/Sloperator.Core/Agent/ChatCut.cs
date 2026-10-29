using Sloperator.Core.Llm;

namespace Sloperator.Core.Agent;

/// <summary>
/// Where the model's conversation is cut when a message of the user's is undone with everything
/// after it. The chat on screen and the conversation aren't one list: messages sent together are
/// one message to the model, one sent during a run is marked as such in front of its words, Continue
/// sends a message the chat doesn't show again, and a context reset keeps the user's messages alone.
/// So each message carries ids (<see cref="UserInput.Ids"/>) that the conversation's copy of it keeps
/// (<see cref="ChatMessage.Sent"/>), through resets and saving; a chat saved before they existed is
/// matched by the words of its messages, in order, among the user's messages.
/// </summary>
public static class ChatCut
{
    /// <summary>
    /// Where in <paramref name="history"/> the message <paramref name="sent"/>[<paramref name="undone"/>]
    /// begins, <paramref name="sent"/> being the user's messages the chat shows, in order. A message
    /// the model never had (its run failed before) begins where the next one that it had does; with
    /// none, at the end: what is in the conversation belongs to the messages before it.
    /// </summary>
    public static int HistoryStart(IReadOnlyList<ChatMessage> history, IReadOnlyList<UserInput> sent, int undone)
    {
        // By the ids a message and the conversation's copy of it share: words could match an older
        // message with the same ones ("continue") that the chat no longer shows, once a long run
        // had dropped the chat's oldest lines, and the model then lost more than was undone.
        if (undone >= 0 && undone < sent.Count && sent[undone].Ids.Count > 0)
        {
            var ids = sent.Skip(undone).SelectMany(s => s.Ids).ToHashSet(StringComparer.Ordinal);
            var first = history.Select((m, i) => (m, i)).FirstOrDefault(x => x.m.FromUser && x.m.Sent?.Any(ids.Contains) == true);
            return first.m is null ? history.Count : first.i;
        }
        // A chat saved before messages had ids: by their words.
        // Where the search goes on: a message, and how far into it (several sent together are one).
        var (at, from) = (0, 0);
        for (var i = 0; i < sent.Count; i++)
        {
            if (Key(sent[i]) is not { Length: > 0 } key)
                continue;
            for (var (m, start) = (at, from); m < history.Count; m++, start = 0)
            {
                if (!history[m].FromUser || Find(TextOf(history[m]), key, start) is not (>= 0 and var found))
                    continue;
                (at, from) = (m, found + key.Length);
                if (i >= undone)
                    return m;
                break;
            }
        }
        return history.Count;
    }

    internal const string UndoneNote =
        "[Sloperator] The user undid their messages after the ones above, with everything you did for them: the files write_file and edit_file " +
        "changed since were put back. Your handoff note may tell of work that is now undone; check before you go by it.";

    /// <summary>
    /// Cuts <paramref name="history"/> at <paramref name="start"/> (see <see cref="HistoryStart"/>). A
    /// context reset since keeps its handoff note, with a word that the messages after the cut were
    /// undone: a reset puts every message of the user's before its note, so a cut at one of them took
    /// the note too, and the model was left with the messages before it and nothing of the work done.
    /// </summary>
    public static void Cut(List<ChatMessage> history, int start)
    {
        var note = history.FindLastIndex(m => m.Content?.StartsWith(AgentSession.HandoffHeader, StringComparison.Ordinal) == true);
        var kept = note > start ? history[note] : null;
        history.RemoveRange(start, history.Count - start);
        if (kept is null)
            return;
        history.Add(kept);
        history.Add(ChatMessage.User(UndoneNote));
    }

    /// <summary>How the message's words begin in the conversation: what the model read first, or for files alone, the first file's line.</summary>
    private static string Key(UserInput input) =>
        UserInput.Tidy(input.ModelText) is { Length: > 0 } text ? text
        : input.Files.Count > 0 ? $"- {input.Files[0]} ("
        : "";

    /// <summary>Tools that only look, wait or point: what they did needs no undoing.</summary>
    private static readonly HashSet<string> OnlyLook =
        ["screenshot", "zoom", "read_file", "list_directory", "index_folder", "diff", "web_search", "fetch_page", "wait", "mouse_move"];

    /// <summary>The tools whose changes File changes keeps, to put back.</summary>
    private static readonly HashSet<string> Kept = ["write_file", "edit_file"];

    /// <summary>
    /// What the chat says once a message is undone: the files put back, those that couldn't be, and
    /// the tool calls undone whose changes Sloperator doesn't keep (commands, copies, ComfyUI, clicks
    /// and typing in other programs), by name and count. Null when nothing was changed or left.
    /// </summary>
    /// <param name="back">What the files' roll back did; null for a message from before messages had turns.</param>
    /// <param name="calls">The tool calls undone, with whether each ran (not declined).</param>
    public static string? Said(Diffs.FileChanges.RollBack? back, IEnumerable<(string Name, bool Ran)> calls)
    {
        var ran = calls.Where(c => c.Ran).Select(c => c.Name).ToList();
        List<string> said = [];
        if (back is not null)
        {
            if (back.Restored.Count > 0)
                said.Add($"Put back as before your message: {Names(back.Restored)}.");
            if (back.Deleted.Count > 0)
                said.Add($"Moved to the Recycle Bin, as made after it: {Names(back.Deleted)}.");
            foreach (var (path, why) in back.Failed)
                said.Add($"Couldn't put back {Path.GetFileName(path)}: {why}");
        }
        else if (ran.Any(Kept.Contains))
            said.Add("The files write_file and edit_file changed weren't put back: this chat is from before Sloperator kept them for each message.");
        var left = ran.Where(n => !OnlyLook.Contains(n) && !Kept.Contains(n)).GroupBy(n => n).Select(g => g.Count() == 1 ? g.Key : $"{g.Key} ×{g.Count()}").ToList();
        if (left.Count > 0)
            said.Add($"Not undone, as Sloperator doesn't keep what they change: {List(left)}.");
        return said.Count == 0 ? null : string.Join(" ", said);

        static string Names(IReadOnlyList<string> paths) =>
            List(paths.Take(paths.Count > 6 ? 5 : 6).Select(p => Path.GetFileName(p)).Append(paths.Count > 6 ? $"{paths.Count - 5} more" : null).OfType<string>().ToList());

        static string List(IReadOnlyList<string> items) => items.Count == 1 ? items[0] : string.Join(", ", items.SkipLast(1)) + " and " + items[^1];
    }

    private static string TextOf(ChatMessage m) => (m.Parts is { Count: > 0 } parts ? parts[0].Text : m.Content) ?? "";

    /// <summary>Where <paramref name="key"/> stands at the start of a line of <paramref name="text"/>, from <paramref name="start"/> on; -1 when it doesn't.</summary>
    private static int Find(string text, string key, int start)
    {
        if (start == 0 && text.StartsWith(key, StringComparison.Ordinal))
            return 0;
        var line = text.IndexOf("\n" + key, start, StringComparison.Ordinal);
        return line < 0 ? -1 : line + 1;
    }
}
