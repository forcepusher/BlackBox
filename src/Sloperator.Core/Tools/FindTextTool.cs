using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Sloperator.Core.Platform;

namespace Sloperator.Core.Tools;

/// <summary>
/// Where some text is on the screen, read by the OS's own text recognition. On the click benchmark
/// Qwen 3.8 found one code in a table of about 200 cells 1 or 2 times in 5, zooming from part to part
/// until it gave up, even with eight looks a try (30 October 2026); the words' boxes give the x/y
/// straight away.
/// </summary>
public sealed class FindTextTool : Tool
{
    /// <summary>The most places listed; more are counted.</summary>
    private const int MaxListed = 10;

    public override string Name => "find_text";
    public override string Description =>
        "Find where some text is on the screen: the OS reads the screen's text and gives the x/y of each place it appears, in screenshot " +
        "coordinates, with the line it is in. Use it to find a label, a link, a menu entry or a value among many (a table, a long list, a page " +
        "of text) instead of searching by zooming, then click the x/y it gives. It reads text, not icons.";
    public override ToolRisk Risk => ToolRisk.ReadOnly;
    public override JsonObject Parameters => Schema(
        ("text", Str("The text to find as it is written on screen; case and spaces don't matter"), true));
    public override string Describe(JsonObject args) => $"Find \"{GetString(args, "text")}\" on screen";

    public override Task<ToolResult> ExecuteAsync(JsonObject args, ToolContext ctx, CancellationToken ct)
    {
        var query = RequireString(args, "text");
        if (ctx.Platform.Text is not { } reader)
            return Task.FromResult(ToolResult.Error("Reading the screen's text isn't available here; look with screenshot and zoom instead."));
        var space = ctx.GetScreenSpace();
        var lines = reader.Read(ctx.Platform.Screen.Capture(space.Screen, includeCursor: false));
        if (lines is null)
            return Task.FromResult(ToolResult.Error(NoLanguage));
        return Task.FromResult(new ToolResult(Report(query, lines, space)));
    }

    /// <summary>
    /// Where Windows reads no language: the agent offers to install the pack of the user's own (the
    /// user: "let the agent offer installation of the packs"), to skip it for now (it may be offered
    /// again), or to turn text recognition off in Settings, which takes find_text away and so the offer.
    /// Adding a Windows capability needs an administrator, so it runs elevated and Windows asks; the
    /// reader tries again on the next call.
    /// </summary>
    internal const string NoLanguage =
        "Windows has no text recognition language installed, so the screen's text can't be read; look with screenshot and zoom for now. " +
        "Once in this chat, offer the user to install it: it improves how you use apps on screen, finding and clicking buttons, menus, links and values " +
        "quicker and more surely (a download of a few MB). Tell them they can install it now, skip it for now (you may ask again another time), " +
        "or turn text recognition off for good in Sloperator's Settings → Advanced → Use text recognition. Only if they choose to install, run in the shell: " +
        "$name = \"Language.OCR~~~$((Get-Culture).Name)~0.0.1.0\"; Start-Process powershell -Verb RunAs -Wait -ArgumentList \"-NoProfile -Command Add-WindowsCapability -Online -Name $name\" " +
        "(Windows asks the user to allow it; for English use Language.OCR~~~en-US~0.0.1.0). Then use find_text again.";

    /// <summary>What the model is told: each place the text is, or the closest readings when it isn't there as written.</summary>
    internal static string Report(string query, IReadOnlyList<TextLine> lines, ScreenSpace space)
    {
        var found = TextSearch.Find(lines, query, fuzzy: false);
        var close = found.Count == 0 ? TextSearch.Find(lines, query, fuzzy: true) : [];
        var sb = new StringBuilder();
        if (found.Count == 0 && close.Count == 0)
            return $"\"{query}\" isn't in the screen's text ({lines.Count} lines read). It may be spelled otherwise, be part of a picture or an icon, " +
                   "or be off this screen: look with screenshot and zoom, or scroll.";
        var matches = found.Count > 0 ? found : close;
        sb.Append(found.Count > 0
            ? $"\"{query}\" is on the screen {Times(found.Count)}:"
            : $"\"{query}\" isn't in the screen's text as written; it reads close to it {Times(close.Count)} (text recognition can confuse O and 0, l and 1):");
        foreach (var m in matches.Take(MaxListed))
        {
            var (x0, y0) = space.ToModel(space.Screen.X + (int)Math.Floor(m.X), space.Screen.Y + (int)Math.Floor(m.Y));
            var (x1, y1) = space.ToModel(space.Screen.X + (int)Math.Ceiling(m.X + m.Width), space.Screen.Y + (int)Math.Ceiling(m.Y + m.Height));
            sb.Append(CultureInfo.InvariantCulture, $"\n- ({(x0 + x1) / 2}, {(y0 + y1) / 2}): \"{m.Text}\", box ({x0}, {y0}) to ({x1}, {y1}), in the line \"{m.Line.Clip(100)}\"");
        }
        if (matches.Count > MaxListed)
            sb.Append($"\n- and {matches.Count - MaxListed} more");
        sb.Append("\nThe x/y are the middle of the text, in screenshot coordinates.");
        return sb.ToString();
    }

    private static string Times(int n) => n == 1 ? "once" : $"{n} times";
}

/// <summary>Finds text among the lines the OS read, as words or runs of words.</summary>
internal static class TextSearch
{
    /// <summary>A run of words with the text, the box around them, the whole line they are in, and how many letters it is off.</summary>
    public sealed record Match(string Text, double X, double Y, double Width, double Height, string Line, int Off = 0);

    /// <summary>
    /// Every run of words whose text holds <paramref name="query"/>, ignoring case and spaces, top to
    /// bottom; with <paramref name="fuzzy"/>, runs that read within a third of its letters of it
    /// instead, closest first, with the letters text recognition mixes up (O and 0, l, I and 1) taken
    /// as the same. A third: Windows read "VY-643" in a table as "w-643", two letters off.
    /// </summary>
    public static List<Match> Find(IReadOnlyList<TextLine> lines, string query, bool fuzzy)
    {
        var wanted = Normal(query, fuzzy);
        var found = new List<Match>();
        if (wanted.Length == 0)
            return found;
        var allowed = fuzzy ? Math.Max(1, (wanted.Length + 1) / 3) : 0;
        foreach (var line in lines)
        {
            var words = line.Words;
            for (var first = 0; first < words.Count; first++)
            {
                var run = new StringBuilder();
                for (var last = first; last < words.Count; last++)
                {
                    run.Append(Normal(words[last].Text, fuzzy));
                    var text = run.ToString();
                    // A run that holds the text, and no shorter one from the same first word does.
                    var off = fuzzy && Math.Abs(text.Length - wanted.Length) <= allowed ? Distance(text, wanted) : int.MaxValue;
                    if (fuzzy ? off <= allowed : text.Contains(wanted, StringComparison.Ordinal))
                    {
                        // Not a longer run than needed: one that would also hold it without its first word is left to that one.
                        if (!fuzzy && first < last && Normal(string.Concat(words.Skip(first + 1).Take(last - first).Select(w => w.Text)), false).Contains(wanted, StringComparison.Ordinal))
                            break;
                        found.Add(Box(words, first, last, line.Text) with { Off = fuzzy ? off : 0 });
                        break;
                    }
                    // A close reading only gets further away; the text itself may still start in this run's last word.
                    if (fuzzy && text.Length > wanted.Length + allowed)
                        break;
                }
            }
        }
        // The OS may read the screen more than once (at two sizes): the same place read twice is one.
        var places = new List<Match>();
        foreach (var m in found.OrderBy(m => m.Off).ThenBy(m => Math.Round(m.Y / 8)).ThenBy(m => m.X))
            if (!places.Any(p => Math.Abs(Mid(p.X, p.Width) - Mid(m.X, m.Width)) < Math.Max(p.Height, m.Height) &&
                                 Math.Abs(Mid(p.Y, p.Height) - Mid(m.Y, m.Height)) < Math.Max(p.Height, m.Height) / 2))
                places.Add(m);
        return places;

        static double Mid(double start, double size) => start + size / 2;
    }

    private static Match Box(IReadOnlyList<TextWord> words, int first, int last, string line)
    {
        var run = words.Skip(first).Take(last - first + 1).ToList();
        var (x0, y0) = (run.Min(w => w.X), run.Min(w => w.Y));
        var (x1, y1) = (run.Max(w => w.X + w.Width), run.Max(w => w.Y + w.Height));
        return new(string.Join(" ", run.Select(w => w.Text)), x0, y0, x1 - x0, y1 - y0, line);
    }

    /// <summary>Lower case without spaces; with <paramref name="fold"/>, the letters recognition confuses made one.</summary>
    private static string Normal(string text, bool fold)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var c in text.ToLowerInvariant())
        {
            if (char.IsWhiteSpace(c))
                continue;
            sb.Append(fold ? c switch { 'o' => '0', 'l' or 'i' or '|' => '1', _ => c } : c);
        }
        return sb.ToString();
    }

    /// <summary>Edits (insertions, deletions, changes) that make one string the other.</summary>
    private static int Distance(string a, string b)
    {
        var row = Enumerable.Range(0, b.Length + 1).ToArray();
        for (var i = 1; i <= a.Length; i++)
        {
            var diagonal = row[0];
            row[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var above = row[j];
                row[j] = Math.Min(Math.Min(row[j] + 1, row[j - 1] + 1), diagonal + (a[i - 1] == b[j - 1] ? 0 : 1));
                diagonal = above;
            }
        }
        return row[b.Length];
    }
}
