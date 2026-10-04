using System.Globalization;
using System.Text.RegularExpressions;
using BlackBox.Core.Llm;

namespace BlackBox.Core.Agent;

/// <summary>
/// Asks the model where something is on a screenshot, in a new conversation of its own. Deep in a
/// long run the model can settle on a wrong idea of where something is ("the menu bar is at
/// y≈3") and click there again and again, whatever the miss notes say; the same model looking at
/// the same screenshot without that history gets it right (17 of 19 targets, all eight menus
/// included, on a real Unity screenshot in ClickAccuracyTests). Used after a click that changed
/// nothing.
/// </summary>
internal sealed partial class FreshLook(OpenAiChatClient client, ChatRequestOptions options)
{
    /// <summary>Room for the reply, thinking included; a look that needs more is given up on.</summary>
    private const int MaxTokens = 4096;

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(120);

    /// <summary>Off only to measure what the fresh look adds (ClickAccuracyTests).</summary>
    internal static bool Enabled { get; set; } = true;

    /// <param name="coordinates">How x/y relate to the screenshot (<see cref="Platform.ScreenSpace.Describe"/>).</param>
    /// <returns>The target's centre in the model's coordinates, or null if it wasn't found or the look failed.</returns>
    public async Task<(double X, double Y)?> LocateAsync(byte[] screenshot, string coordinates, string target, CancellationToken ct)
    {
        List<ChatMessage> request =
        [
            ChatMessage.System("You find things on screenshots. Coordinates: " + coordinates),
            new()
            {
                Role = ChatRole.User,
                Parts =
                [
                    ContentPart.FromImage(screenshot, "image/jpeg"),
                    ContentPart.FromText($"Where is the {target}? Reply with only the x, y of its centre, like (412, 87), or with none if it is not on the screenshot."),
                ],
            },
        ];
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Timeout);
        ChatMessage reply;
        try
        {
            reply = await client.CompleteAsync(request, [], new ChatRequestOptions { Model = options.Model, Extra = options.Extra, MaxTokens = MaxTokens }, null, timeout.Token)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is LlmException or HttpRequestException or IOException || (ex is OperationCanceledException && !ct.IsCancellationRequested))
        {
            return null;
        }
        return Parse(reply.Content);
    }

    internal static (double X, double Y)? Parse(string? reply)
    {
        if (Point().Matches(reply ?? "").LastOrDefault() is not { } m)
            return null;
        return (double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture));
    }

    [GeneratedRegex(@"(\d+(?:\.\d+)?)\s*,\s*(\d+(?:\.\d+)?)")]
    private static partial Regex Point();
}
