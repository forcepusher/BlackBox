using System.Text.RegularExpressions;
using BlackBox.Core.Mcp;
using BlackBox.Core.Tools;

namespace BlackBox.Core.Agent;

/// <summary>
/// Notices the model guessing, tries that fail with no look-up in between, and points it to
/// web_search, which the system prompt already asks for. It didn't search in the blind-spot runs:
/// an Excel chart call failed the same way four times ("Method invocation failed ... does not
/// contain a method named 'Add'") before the model decided Excel's COM server was "a limited stub"
/// and wrote the chart's XML by hand; one search would have shown that in PowerShell ChartObjects()
/// is a method. And building a geometry nodes graph in Blender 5.2 over MCP, Nemotron went through
/// names from Blender 3 one failed script after another; the server reports a script's error as
/// plain text, which wasn't taken for an error at all. A note, never a block: an error naming
/// something that doesn't exist gets it at once, the same error a second time, any failure from
/// the third since the last look-up, and the first script or command that fails in a task before
/// anything was looked up.
/// </summary>
internal sealed partial class Guesswork
{
    /// <summary>Failures further back than this many calls no longer count.</summary>
    private const int Window = 20;

    /// <summary>Failures since the last look-up (within <see cref="Window"/>) before every next one gets a note.</summary>
    internal const int Tries = 3;

    internal const string RepeatNote =
        "[BlackBox] This error came up before in this task. Unless you know exactly why, look it up with web_search " +
        "(the error message, and the program or language it comes from) before you try again.";

    internal const string NameNote =
        "[BlackBox] The error says a name doesn't exist here. Names change between versions, and yours may be from another one: " +
        "look up the right one for the version in use with web_search (then fetch_page with find= the name) instead of trying other names.";

    internal static string TriesNote(int failed) =>
        $"[BlackBox] {failed} tries have failed since you last looked anything up. Unless you know exactly why this one failed, " +
        "search for the error or for how this is done in the version you are working with (web_search, then fetch_page) " +
        "before you try again: guessing costs more than a search.";

    internal const string FirstNote =
        "[BlackBox] Nothing has been looked up in this task yet. Unless you know exactly why this failed, look up how this is done " +
        "in the version you are working with (web_search, then fetch_page with find=) before you try again, rather than trying variations.";

    private readonly Dictionary<string, int> _seen = [];
    private readonly Queue<int> _failures = new();
    private int _calls;
    private bool _lookedUp, _firstSaid;

    /// <summary>The note for a call's result, or null.</summary>
    public string? Check(Tool? tool, ToolResult result)
    {
        _calls++;
        if (tool is WebSearchTool or FetchPageTool)
        {
            _failures.Clear();
            _lookedUp = true;
            return null;
        }
        // A call the user declined failed for no lack of knowledge.
        if (result.Text == AgentSession.DeclinedText || ErrorLine(result, ErrorsInText(tool)) is not { } line)
            return null;

        _failures.Enqueue(_calls);
        while (_failures.Peek() <= _calls - Window)
            _failures.Dequeue();
        var signature = Signature(line);
        var count = _seen[signature] = _seen.GetValueOrDefault(signature) + 1;
        if (count == 2)
            return RepeatNote;
        if (_failures.Count >= Tries)
            return TriesNote(_failures.Count);
        if (count == 1 && NamesWhatIsNotThere(line))
            return NameNote;
        // The first script or command that fails in a task, before anything was looked up: in the
        // Blender runs the first script was written from memory, and more failed before a search.
        if (!_lookedUp && !_firstSaid && ErrorsInText(tool))
        {
            _firstSaid = true;
            return FirstNote;
        }
        return null;
    }

    /// <summary>
    /// Tools that run code report its errors as their output: an MCP server's "Error executing
    /// code: ..." comes as an ordinary result, and Blender run with a script prints the script's
    /// traceback and exits with 0.
    /// </summary>
    private static bool ErrorsInText(Tool? tool) => tool is ShellTool or SshTool or McpTool;

    private const string Traceback = "Traceback (most recent call last):";

    /// <summary>
    /// The first line of the error that says what went wrong; null when the result isn't an
    /// error. A PowerShell error record counts even when the script went on and its exit code was
    /// 0, and with <paramref name="errorsInText"/> so do a Python traceback and output that begins
    /// with "Error". For a traceback it is the line under its stack ("AttributeError: ..."): the
    /// one above is the same for every Python error.
    /// </summary>
    internal static string? ErrorLine(ToolResult result, bool errorsInText = false)
    {
        var text = result.Text;
        var traceback = text.LastIndexOf(Traceback, StringComparison.Ordinal);
        if (!result.IsError && !text.Contains("FullyQualifiedErrorId", StringComparison.Ordinal) &&
            !(errorsInText && (traceback >= 0 || text.TrimStart().StartsWith("Error", StringComparison.OrdinalIgnoreCase))))
            return null;
        // The stack's lines are indented; the error follows them. mcp-for-blender sends the
        // traceback inside JSON, on one line with its line breaks written as \n.
        var stack = traceback >= 0 ? text[(traceback + Traceback.Length)..] : "";
        if (stack.StartsWith("\\n", StringComparison.Ordinal))
            stack = JsonEscape().Replace(stack, m => m.Groups[1].Value switch { "n" => "\n", "t" => "\t", "r" => "", var c => c });
        if (traceback >= 0 && FirstMeaningful(stack.Split('\n').Where(l => l.Length > 0 && !char.IsWhiteSpace(l[0]))) is { } raised)
            return raised;
        return FirstMeaningful(text.Split('\n'));
    }

    [GeneratedRegex(@"\\(.)")]
    private static partial Regex JsonEscape();

    private static string? FirstMeaningful(IEnumerable<string> lines) =>
        lines.Select(l => l.Trim()).FirstOrDefault(l => l.Length >= 8 && !Noise().IsMatch(l));

    /// <summary>The error line with its numbers made general, so the same error matches however the call differed (another line, another cell).</summary>
    internal static string Signature(string errorLine)
    {
        var line = Spaces().Replace(Numbers().Replace(errorLine.ToLowerInvariant(), "#"), " ");
        return line.Length > 160 ? line[..160] : line;
    }

    /// <inheritdoc cref="Signature(string)"/>
    internal static string? Signature(ToolResult result, bool errorsInText = false) =>
        ErrorLine(result, errorsInText) is { } line ? Signature(line) : null;

    /// <summary>
    /// Whether the error says a name doesn't exist: an attribute, method, argument, enum value,
    /// node type or command the model remembered from another version, or made up.
    /// </summary>
    internal static bool NamesWhatIsNotThere(string errorLine) =>
        // Python's "'NoneType' object has no attribute 'modifiers'" says a variable was empty, not that a name is wrong.
        MissingName().IsMatch(errorLine) && !errorLine.Contains("'NoneType' object", StringComparison.Ordinal);

    // Lines that say where, not what: exit codes, PowerShell's "At line:3 char:5", "+ ..." echoes, ~~~ markers.
    [GeneratedRegex(@"^(exit code -?\d+|at line:\d+|at [a-z]:\\.*:\d+ char:\d+|\+|~+$|screenshot)", RegexOptions.IgnoreCase)]
    private static partial Regex Noise();

    [GeneratedRegex(@"\d+")]
    private static partial Regex Numbers();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();

    // Python, Blender, C#, PowerShell, JavaScript, GDScript and command-line tools, as they say it.
    [GeneratedRegex(@"has no attribute|is not defined|not declared|no module named|cannot import name|unexpected keyword|positional argument|" +
                    @"\b(key|enum|attribute|property|method|function|module|command|option|parameter|argument|identifier|member|field|node|socket|type)\b[^.;]{0,80}\bnot found|" +
                    @"(could|can) ?not be found|unable to find type|cannot find (module|an overload|type|symbol)|" +
                    @"does not contain an? (definition|method|property|member)|does not exist in the current context|" +
                    @"is not recognized as (the name|an internal or external command)|is not a function|nonexistent (function|method|property)|\btype \S+ undefined\b|" +
                    @"\bunknown (option|argument|parameter|command|flag|property|attribute|field|key|type|function|method|keyword|encoder|decoder|format|node|socket)|" +
                    @"\bunrecognized (option|argument|command|parameter)|\binvalid (choice|option|keyword|enum)|\bno such (option|command|method|property|attribute|module|function)",
        RegexOptions.IgnoreCase)]
    private static partial Regex MissingName();
}
