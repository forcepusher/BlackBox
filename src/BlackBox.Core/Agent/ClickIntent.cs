using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using BlackBox.Core.Llm;

namespace BlackBox.Core.Agent;

/// <summary>
/// Catches a click whose arguments contradict what the model just wrote. Deep in a run the
/// model can reason its way to the right spot ("let me click Assets at (34, 26)") and then emit
/// the arguments of its previous, wrong call ({"x":34,"y":3}): in one Unity run, nine times in a
/// row, plus twice (409, 5) for a Play button it had just placed at (521, 52). When the last
/// sentence about clicking states a point and the call's point is clearly elsewhere, the stated
/// point is used and the model is told.
/// </summary>
internal static partial class ClickIntent
{
    /// <summary>How much of the end of the reply (thinking and text) is searched for the stated point.</summary>
    private const int Tail = 600;

    /// <summary>
    /// If the reply has exactly one click and its x/y differ from the point the reply's text says
    /// it will click by more than 2% of the screen, moves the call to the stated point and returns
    /// a note for the tool result; null otherwise.
    /// </summary>
    public static string? Reconcile(ChatMessage reply, int modelWidth, int modelHeight)
    {
        var clicks = reply.ToolCalls?.Where(c => c.Name == "mouse_click").ToList();
        if (clicks is not { Count: 1 } || reply.ToolCalls!.Count(c => c.Name.Contains("click") || c.Name == "mouse_drag") != 1)
            return null;
        var call = clicks[0];
        JsonObject args;
        try { args = ToolCallRepair.ParseArguments(call.Arguments); }
        catch (Tools.ToolArgumentException) { return null; }
        if (Number(args["x"]) is not { } x || Number(args["y"]) is not { } y)
            return null;
        if (Stated(reply.Reasoning, reply.Content) is not { } said || said.X > modelWidth || said.Y > modelHeight)
            return null;
        if (Math.Abs(said.X - x) <= modelWidth / 50.0 && Math.Abs(said.Y - y) <= modelHeight / 50.0)
            return null;
        args["x"] = said.X;
        args["y"] = said.Y;
        call.Arguments = args.ToJsonString();
        return $"Note: your call said ({Fmt(x)}, {Fmt(y)}), but you wrote that you would click ({Fmt(said.X)}, {Fmt(said.Y)}), so BlackBox clicked there.";
    }

    /// <summary>The point in the last sentence near the end of the reply that talks about clicking and names one.</summary>
    internal static (double X, double Y)? Stated(string? reasoning, string? content)
    {
        var text = ((reasoning ?? "").TrimEnd() + "\n" + (content ?? "").Trim()).Trim();
        var tail = text.Length > Tail ? text[^Tail..] : text;
        foreach (var sentence in Sentence().Split(tail).Reverse())
        {
            if (!ClickWord().IsMatch(sentence))
                continue;
            if (Point().Matches(sentence).LastOrDefault() is { } m)
                return (double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture));
        }
        return null;
    }

    private static double? Number(JsonNode? node) =>
        node is JsonValue v && double.TryParse(v.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;

    private static string Fmt(double v) => v.ToString("0.#", CultureInfo.InvariantCulture);

    [GeneratedRegex(@"(?<=[.!?])\s+|\n")]
    private static partial Regex Sentence();

    [GeneratedRegex(@"\bclick", RegexOptions.IgnoreCase)]
    private static partial Regex ClickWord();

    [GeneratedRegex(@"\(\s*(\d{1,4}(?:\.\d+)?)\s*,\s*(\d{1,4}(?:\.\d+)?)\s*\)")]
    private static partial Regex Point();
}
