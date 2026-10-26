namespace BlackBox.Core.Chats;

/// <summary>
/// What the chat's Markdown (<see cref="MarkdownLite"/>) and documents written from Markdown
/// (<see cref="Documents.MarkdownDocument"/>) read alike: emphasis markers and table rows.
/// </summary>
internal static class MarkdownSyntax
{
    public static char At(string s, int i) => i < s.Length ? s[i] : ' ';

    /// <summary>A single * or _ that starts a word ("*very*"), not one in "5 * 3", "*.txt" or "snake_case".</summary>
    public static bool OpensItalic(string s, int i) =>
        (i == 0 || char.IsWhiteSpace(s[i - 1]) || "([\"'".Contains(s[i - 1])) && char.IsLetterOrDigit(At(s, i + 1));

    /// <summary>Where the italic opened before <paramref name="from"/> with <paramref name="marker"/> closes; -1 when it doesn't.</summary>
    public static int FindItalicEnd(string s, int from, char marker)
    {
        for (var j = from + 1; j < s.Length; j++)
        {
            if (s[j] != marker) continue;
            // A double marker is bold inside the italic ("*a **b** c*"): neither of its two closes it.
            if (j + 1 < s.Length && s[j + 1] == marker)
            {
                j++;
                continue;
            }
            if (!char.IsWhiteSpace(s[j - 1]) && (j + 1 == s.Length || !char.IsLetterOrDigit(s[j + 1])))
                return j;
        }
        return -1;
    }

    public static bool IsTableRow(string line) => line.TrimStart().StartsWith('|') && line.Trim().Length > 1;
}
