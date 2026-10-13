using System.Text;
using System.Text.RegularExpressions;

namespace BlackBox.Core.Ssh;

/// <summary>What a terminal shows, from what a program sent to it.</summary>
public static partial class TerminalText
{
    /// <summary>
    /// Removes escape sequences (colours, cursor moves, window titles) and other control
    /// characters, and applies carriage returns and backspaces the way a terminal shows them:
    /// a progress bar redrawn with \r leaves only its last state. Lines end in \n.
    /// </summary>
    public static string Clean(string raw)
    {
        var text = Escape().Replace(raw, "").Replace("\r\n", "\n");
        var sb = new StringBuilder(text.Length);
        var line = new StringBuilder();
        var col = 0;
        foreach (var c in text)
        {
            switch (c)
            {
                case '\n':
                    sb.Append(line).Append('\n');
                    line.Clear();
                    col = 0;
                    break;
                case '\r':
                    col = 0;
                    break;
                case '\b':
                    if (col > 0) col--;
                    break;
                case '\t':
                    Put(c);
                    break;
                default:
                    if (!char.IsControl(c)) Put(c);
                    break;
            }
        }
        sb.Append(line);
        return sb.ToString();

        void Put(char c)
        {
            if (col < line.Length) line[col] = c;
            else line.Append(c);
            col++;
        }
    }

    /// <summary>
    /// Whether a program switched to the full-screen mode of editors and pagers (vim, less, top),
    /// or, on the dumb terminal BlackBox uses, stopped to say it can't (less does).
    /// </summary>
    public static bool EntersFullScreen(string raw) =>
        raw.Contains("\x1b[?1049h") || raw.Contains("\x1b[?47h") || raw.Contains("\x1b[?1047h") ||
        raw.Contains("terminal is not fully functional");

    /// <summary>
    /// The beginning and the end of a long output, with a note of what was left out between them,
    /// cut at line ends. Errors and results usually sit at the end, so that part is the larger.
    /// </summary>
    public static string Clip(string text, int max)
    {
        if (text.Length <= max)
            return text;
        var head = max / 4;
        var headEnd = text.LastIndexOf('\n', head);
        if (headEnd < head / 2) headEnd = head;
        var tailFrom = text.Length - (max - head);
        var tailStart = text.IndexOf('\n', tailFrom);
        if (tailStart < 0 || tailStart - tailFrom > head / 2) tailStart = tailFrom;
        var cut = text[headEnd..tailStart];
        return text[..headEnd] + $"\n[... {cut.Count(c => c == '\n'):N0} lines ({cut.Length:N0} characters) left out ...]" + text[tailStart..];
    }

    /// <summary>
    /// Whether the unfinished last line of the output reads like a question waiting for an
    /// answer ("Password: ", "Do you want to continue? [Y/n] ", "(yes/no/[fingerprint])?").
    /// Only asked once the output has been quiet for a while.
    /// </summary>
    public static bool LooksLikePrompt(string lastLine)
    {
        var line = lastLine.TrimEnd();
        if (line.Length == 0 || line.Length > 300 || line.EndsWith('%'))
            return false;
        // Questions end in ": ", "? ", "] ", "> " or ") "; the prompts of shells, psql and REPLs in "# ", "$ " or "> ".
        return PromptWords().IsMatch(line) || (lastLine.EndsWith(' ') && line[^1] is ':' or '?' or ']' or '>' or ')' or '#' or '$');
    }

    /// <summary>sudo's own password prompt.</summary>
    public static bool IsSudoPrompt(string lastLine) => SudoPrompt().IsMatch(lastLine.Trim());

    [GeneratedRegex(@"\x1b\[[0-?]*[ -/]*[@-~]|\x1b\][^\x07\x1b]*(\x07|\x1b\\)|\x1b[PX^_][^\x1b]*\x1b\\|\x1b[()*+][0-9A-Za-z]|\x1b[@-Z\\-_=>78]")]
    private static partial Regex Escape();

    [GeneratedRegex(@"(?i)(password|passphrase|\[y/n\]|\(y/n\)|\by/n\b|\(yes/no|continue\?|press (enter|return|any key))[^\n]*$")]
    private static partial Regex PromptWords();

    [GeneratedRegex(@"^\[sudo\] password for [^:]+:$")]
    private static partial Regex SudoPrompt();
}
