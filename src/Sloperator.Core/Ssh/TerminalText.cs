using System.Text;
using System.Text.RegularExpressions;

namespace Sloperator.Core.Ssh;

/// <summary>What a terminal shows, from what a program sent to it.</summary>
public static partial class TerminalText
{
    /// <summary>The terminal's height (the ssh tool asks for 200 x 50): cursor moves can't reach above it.</summary>
    private const int Rows = 50;

    /// <summary>
    /// What a terminal shows for a program's output, as plain lines ending in \n: colours,
    /// window titles and other escape sequences are removed; carriage returns, backspaces and
    /// the cursor moves and line erases of progress displays are applied, so a redrawn line
    /// keeps only its last state (a \r progress bar, docker pull's line per layer). A status bar
    /// drawn at a fixed place between saving and restoring the cursor (apt's) is left out.
    /// </summary>
    public static string Clean(string raw)
    {
        var lines = new List<StringBuilder> { new() };
        int row = 0, col = 0;
        (int Row, int Col)? saved = null;
        var away = false; // at a fixed place on the screen, between saving and restoring the cursor
        for (var i = 0; i < raw.Length; i++)
        {
            var c = raw[i];
            if (c == '\x1b')
            {
                if (EscapeAt().Match(raw, i) is { Success: true } m)
                {
                    Apply(m.Value);
                    i += m.Length - 1;
                }
                continue;
            }
            switch (c)
            {
                case '\n':
                    if (away) break;
                    if (++row == lines.Count) lines.Add(new StringBuilder());
                    col = 0;
                    break;
                case '\r':
                    col = 0;
                    break;
                case '\b':
                    if (col > 0) col--;
                    break;
                default:
                    if (c == '\t' || !char.IsControl(c)) Put(c);
                    break;
            }
        }
        return string.Join('\n', lines);

        void Put(char ch)
        {
            if (away) return;
            var line = lines[row];
            if (col > line.Length) line.Append(' ', col - line.Length);
            if (col < line.Length) line[col] = ch;
            else line.Append(ch);
            col++;
        }

        void Apply(string esc)
        {
            switch (esc)
            {
                case "\x1b" + "7":
                    saved = (row, col);
                    return;
                case "\x1b" + "8":
                    if (saved is { } s) (row, col) = s;
                    saved = null;
                    away = false;
                    return;
            }
            if (esc.Length < 3 || esc[1] != '[' || away)
                return;
            var args = esc[2..^1];
            var n = int.TryParse(args.Split(';')[0], out var v) && v > 0 ? v : 1;
            var top = Math.Max(0, lines.Count - Rows);
            switch (esc[^1])
            {
                case 'A': row = Math.Max(top, row - n); break;
                case 'B': row = Math.Min(lines.Count - 1, row + n); break;
                case 'C': col += n; break;
                case 'D': col = Math.Max(0, col - n); break;
                case 'E': row = Math.Min(lines.Count - 1, row + n); col = 0; break;
                case 'F': row = Math.Max(top, row - n); col = 0; break;
                case 'G': col = n - 1; break;
                case 'H' or 'f':
                    // A place on the screen: only followed while the cursor is saved, to be put back.
                    away = saved is not null;
                    break;
                case 'K':
                    var line = lines[row];
                    if (args is "" or "0") line.Length = Math.Min(col, line.Length);
                    else if (args == "1") for (var k = 0; k < Math.Min(col + 1, line.Length); k++) line[k] = ' ';
                    else if (args == "2") line.Clear();
                    break;
                case 'J':
                    if (args is "" or "0")
                    {
                        lines[row].Length = Math.Min(col, lines[row].Length);
                        lines.RemoveRange(row + 1, lines.Count - row - 1);
                    }
                    break;
            }
        }
    }

    /// <summary>
    /// Whether a line is the bytes of an image or an archive printed as text (curl without -o,
    /// cat of a binary file): more than a tenth of it undecodable or control characters. Colour
    /// codes (escape) and tabs don't count; short lines never do.
    /// </summary>
    /// <param name="afterBinary">The line before was binary: then a short line with any such character is a piece of it too.</param>
    /// <param name="needControl">
    /// Count it as binary only with control characters in it too: text in a legacy code page read
    /// as UTF-8 (a Russian Windows tool's output) is all undecodable letters, but never control characters.
    /// </param>
    public static bool LooksBinary(string line, bool afterBinary = false, bool needControl = false)
    {
        int odd = 0, control = 0;
        foreach (var c in line)
        {
            if (char.IsControl(c) && c is not ('\t' or '\r' or '\x1b'))
                control++;
            else if (c != '�')
                continue;
            odd++;
        }
        if (needControl && control * 50 < line.Length)
            return false;
        return line.Length < 16 ? afterBinary && odd > 0 : odd * 10 > line.Length;
    }

    /// <summary>The text with each run of binary-looking lines (see <see cref="LooksBinary"/>) replaced by a one-line note.</summary>
    public static string DropBinary(string text)
    {
        if (!text.Contains('�') && !text.Any(c => c < ' ' && c is not ('\n' or '\t' or '\r' or '\x1b')))
            return text;
        var sb = new StringBuilder(text.Length);
        var left = 0;
        foreach (var line in text.Split('\n'))
        {
            if (LooksBinary(line, afterBinary: left > 0))
            {
                left += line.Length + 1;
                continue;
            }
            if (left > 0)
                sb.Append(BinaryNote(left)).Append('\n');
            left = 0;
            sb.Append(line).Append('\n');
        }
        if (left > 0)
            sb.Append(BinaryNote(left)).Append('\n');
        return sb.ToString(0, sb.Length - 1);
    }

    /// <summary>What stands in for binary output that was left out.</summary>
    public static string BinaryNote(long chars) => $"[binary data left out: {chars:N0} characters; save it to a file with -o or > instead of printing it]";

    /// <summary>
    /// Whether a program switched to the full-screen mode of editors and pagers (vim, less, top),
    /// or, on the dumb terminal Sloperator uses, stopped to say it can't (less does).
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

    /// <summary>An escape sequence starting right at the position the match starts from.</summary>
    [GeneratedRegex(@"\G(?:\x1b\[[0-?]*[ -/]*[@-~]|\x1b\][^\x07\x1b]*(?:\x07|\x1b\\)|\x1b[PX^_][^\x1b]*\x1b\\|\x1b[()*+][0-9A-Za-z]|\x1b[@-Z\\-_=>78])")]
    private static partial Regex EscapeAt();

    [GeneratedRegex(@"(?i)(password|passphrase|\[y/n\]|\(y/n\)|\by/n\b|\(yes/no|continue\?|press (enter|return|any key))[^\n]*$")]
    private static partial Regex PromptWords();

    [GeneratedRegex(@"^\[sudo\] password for [^:]+:$")]
    private static partial Regex SudoPrompt();
}
