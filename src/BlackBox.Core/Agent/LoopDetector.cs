using System.Security.Cryptography;
using BlackBox.Core.Llm;
using BlackBox.Core.Tools;

namespace BlackBox.Core.Agent;

/// <summary>
/// Notices when the model is stuck: the same action over and over, or actions that leave the
/// screen exactly as it was. It never stops the run (long tasks are legitimate); it adds a note to
/// the tool result telling the model to change approach. Memory use is fixed, however long the run.
/// </summary>
internal sealed class LoopDetector
{
    /// <summary>How many recent actions are compared.</summary>
    private const int Window = 12;

    /// <summary>Identical actions within the window before the model is told it is looping.</summary>
    private const int RepeatThreshold = 3;

    /// <summary>Actions in a row that leave the screen unchanged before the model is told.</summary>
    private const int NoChangeThreshold = 3;

    // Looking at the screen or waiting repeatedly is normal (e.g. watching something for hours).
    private static readonly HashSet<string> Passive = new(StringComparer.OrdinalIgnoreCase)
        { "screenshot", "wait" };

    private readonly Queue<string> _recent = new();
    private byte[]? _lastScreen;
    private int _unchanged;

    /// <summary>Returns a note to append to the tool result, or null if all looks fine.</summary>
    public string? Check(ToolCall call, ToolResult result)
    {
        var notes = new List<string>();

        if (!Passive.Contains(call.Name))
        {
            var signature = call.Name + " " + call.Arguments.Replace(" ", "");
            _recent.Enqueue(signature);
            if (_recent.Count > Window)
                _recent.Dequeue();
            var repeats = _recent.Count(s => s == signature);
            if (repeats >= RepeatThreshold)
                notes.Add($"Note: you made exactly this call {repeats} times in your last {_recent.Count} actions. If it is not getting you closer, stop repeating it and try a different approach.");
        }

        if (result.Image is not null)
        {
            var hash = SHA256.HashData(result.Image);
            var same = _lastScreen is not null && hash.AsSpan().SequenceEqual(_lastScreen);
            _lastScreen = hash;
            _unchanged = same && !Passive.Contains(call.Name) ? _unchanged + 1 : 0;
            if (_unchanged >= NoChangeThreshold)
                notes.Add($"Note: the screen has not changed after your last {_unchanged} actions. They are probably not working; try something different (another control or the keyboard).");
        }

        return notes.Count > 0 ? string.Join("\n", notes) : null;
    }
}
