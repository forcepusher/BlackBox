using System.Text;
using BlackBox.Core.Llm;

namespace BlackBox.Core.Agent;

/// <summary>
/// Watches one streamed reply for degenerate repetition: a confused model can write the same
/// paragraph of thinking over and over without ever reaching a tool call. <see cref="LoopDetector"/>
/// only sees finished steps, so it never notices; this cuts the reply off while it streams.
/// </summary>
internal sealed class RepetitionGuard(Action<StreamDelta>? inner)
{
    /// <summary>Length of the tail that is looked up earlier in the reply.</summary>
    internal const int TailChars = 200;

    /// <summary>The tail appearing this many times means the reply is going in circles.</summary>
    internal const int Repeats = 3;

    private const int CheckEvery = 100;

    private readonly StringBuilder _text = new();
    private int _checkedAt;

    public void OnDelta(StreamDelta delta)
    {
        inner?.Invoke(delta);
        _text.Append(delta.Text);
        if (_text.Length - _checkedAt < CheckEvery || _text.Length < TailChars * Repeats)
            return;
        _checkedAt = _text.Length;
        if (IsRepeating(_text.ToString()))
            throw new RepetitionException();
    }

    internal static bool IsRepeating(string text)
    {
        var tail = text[^TailChars..];
        // Lines of nothing but board digits or punctuation repeat legitimately; real loops are prose.
        if (tail.Count(char.IsLetter) < TailChars / 4)
            return false;
        var count = 0;
        for (var i = text.IndexOf(tail, StringComparison.Ordinal); i >= 0; i = text.IndexOf(tail, i + TailChars, StringComparison.Ordinal))
            if (++count >= Repeats)
                return true;
        return false;
    }
}

/// <summary>The model's reply was cut off because it kept repeating itself.</summary>
internal sealed class RepetitionException() : LlmException("the model's reply kept repeating itself");
