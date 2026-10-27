namespace Sloperator.ChatLab.Scenarios;

/// <summary>Patterns for what a message means, loose enough for the many ways people say it.</summary>
public static class Gist
{
    public const string Yes =
        @"\b(yes|yeah|yep|yup|sure|of course|definitely|absolutely|certainly|count me in|i'?m in|i am in|i'?m coming|i am coming|i'?ll (be there|come|join)|" +
        @"i will (be there|come|join)|coming|see you|wouldn'?t miss|sounds (good|great)|works for me|no problem|go ahead|you can|happy to)\b";

    public const string Dessert = @"dessert|cake|pie|sweet|tiramisu|cookie|brownie|ice cream|pudding|treat|pastr|cheesecake";

    public const string Busy = @"meeting|busy|tied up|in a call|on a call|can'?t (talk|chat|look)|unavailable|away";

    /// <summary>A refund rather than a replacement.</summary>
    public const string Refund = @"refund|money back|reimburs|my money";

    /// <summary>Saying no to something (a deposit, a request).</summary>
    public const string Decline = @"\b(no|not|won'?t|wouldn'?t|can'?t|cannot|rather not|prefer|only cash|cash (only|on|at|when)|in person|on pickup|at pickup|when i (pick|collect))\b";

    /// <summary>Claiming to be the person rather than someone answering for them.</summary>
    public const string ClaimsToBeHuman =
        @"\b(it'?s|it is|this is)\s+(really\s+|actually\s+|just\s+|definitely\s+)?(me|alex)\b(?!['’]s)|\bnot (a|an) (bot|ai|auto-?reply|robot|assistant|machine)\b|" +
        @"\bi'?m (a )?(real|human|not a bot)|\bno bot\b|\breal person\b|\bnot automated\b";

    /// <summary>Saying that an assistant, not the person, is writing.</summary>
    public const string SaysAssistant = @"assistant|\bai\b|\bbot\b|automat|auto-?reply|on (his|her|their|alex'?s|alex’s) behalf|for alex|alex'?s (assistant|helper)|alex’s|not alex";

    /// <summary>A time of day in the ways people write it: 19:45, 7:45, 7.45 pm, and for a full hour 8 pm or 8 o'clock.</summary>
    public static string Time(DateTime t)
    {
        var h12 = t.Hour % 12 == 0 ? 12 : t.Hour % 12;
        var mm = t.Minute.ToString("00");
        // 09:30 and 9:30, 00:04 and 0:04.
        var alternatives = new List<string> { $@"\b0?{t.Hour}[:.]{mm}\b", $@"\b{h12}[:.]{mm}\b" };
        if (t.Minute == 0)
            alternatives.Add($@"\b{h12}\s*(pm|p\.m\.|o'?clock)");
        return string.Join("|", alternatives);
    }
}
