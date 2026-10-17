using System.Text.RegularExpressions;
using BlackBox.Core.Tools;

namespace BlackBox.Core.Agent;

/// <summary>
/// Notices the same error coming back in a run and points the model to web_search, which the
/// system prompt already asks for after an error it doesn't understand. It didn't search in the
/// blind-spot runs: an Excel chart call failed the same way four times ("Method invocation failed
/// ... does not contain a method named 'Add'") before the model decided Excel's COM server was "a
/// limited stub" and wrote the chart's XML by hand; one search would have shown that in
/// PowerShell ChartObjects() is a method. A note, never a block: each error gets it once.
/// </summary>
internal sealed partial class ErrorRepeats
{
    private readonly Dictionary<string, int> _seen = [];

    /// <summary>The note for the second time an error comes up, or null.</summary>
    public string? Check(ToolResult result)
    {
        if (Signature(result) is not { } signature)
            return null;
        var count = _seen[signature] = _seen.GetValueOrDefault(signature) + 1;
        return count == 2
            ? "[BlackBox] This error came up before in this task. Unless you know exactly why, look it up with web_search " +
              "(the error message, and the program or language it comes from) before you try again."
            : null;
    }

    /// <summary>
    /// The first line of the error that says what went wrong, its numbers made general so the same
    /// error matches however the call differed (another line, another cell); null when the result
    /// isn't an error. A PowerShell error record counts even when the script went on and its exit
    /// code was 0.
    /// </summary>
    internal static string? Signature(ToolResult result)
    {
        if (!result.IsError && !result.Text.Contains("FullyQualifiedErrorId", StringComparison.Ordinal))
            return null;
        foreach (var raw in result.Text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length < 8 || Noise().IsMatch(line))
                continue;
            line = Numbers().Replace(line.ToLowerInvariant(), "#");
            line = Spaces().Replace(line, " ");
            return line.Length > 160 ? line[..160] : line;
        }
        return null;
    }

    // Lines that say where, not what: exit codes, PowerShell's "At line:3 char:5", "+ ..." echoes, ~~~ markers.
    [GeneratedRegex(@"^(exit code -?\d+|at line:\d+|at [a-z]:\\.*:\d+ char:\d+|\+|~+$|screenshot)", RegexOptions.IgnoreCase)]
    private static partial Regex Noise();

    [GeneratedRegex(@"\d+")]
    private static partial Regex Numbers();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();
}
