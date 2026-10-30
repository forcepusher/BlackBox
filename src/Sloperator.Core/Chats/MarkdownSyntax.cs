using System.Text;
using System.Text.RegularExpressions;

namespace Sloperator.Core.Chats;

/// <summary>
/// What the chat's Markdown (<see cref="MarkdownLite"/>) and documents written from Markdown
/// (<see cref="Documents.MarkdownDocument"/>) read alike: emphasis markers and tables.
/// </summary>
internal static partial class MarkdownSyntax
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

    /// <summary>
    /// A table row's cells, split on the | that aren't escaped or inside `code`. An escaped \| becomes
    /// a | in its cell, or stays \| with <paramref name="keepEscapes"/> for an inline reader that
    /// unescapes it itself.
    /// </summary>
    public static List<string> Cells(string row, bool keepEscapes = false)
    {
        var t = row.Trim();
        if (t.StartsWith('|')) t = t[1..];
        if (t.EndsWith('|') && !t.EndsWith("\\|")) t = t[..^1];
        var cells = new List<string>();
        var cell = new StringBuilder();
        var inCode = false;
        for (var i = 0; i < t.Length; i++)
        {
            if (t[i] == '\\' && i + 1 < t.Length && t[i + 1] == '|') { cell.Append(keepEscapes ? "\\|" : "|"); i++; continue; }
            if (t[i] == '`') inCode = !inCode;
            if (t[i] == '|' && !inCode) { cells.Add(cell.ToString().Trim()); cell.Clear(); continue; }
            cell.Append(t[i]);
        }
        cells.Add(cell.ToString().Trim());
        return cells;
    }

    /// <summary>Which of a table's rows is its |---| line; -1 when it has none.</summary>
    public static int SeparatorRow(List<List<string>> cells) => cells.FindIndex(r => r.Count > 0 && r.All(c => Separator().IsMatch(c)));

    // A |---| line's cell: dashes, a colon at either end for the alignment (":-:" is one too); a lone "-" is a cell.
    [GeneratedRegex(@"^(:-+:?|-+:|-{2,})$")]
    private static partial Regex Separator();
}
