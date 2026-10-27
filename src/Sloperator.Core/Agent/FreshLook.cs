using System.Globalization;
using System.Text.RegularExpressions;
using Sloperator.Core.Llm;

namespace Sloperator.Core.Agent;

/// <summary>
/// Asks the model where something is on a screenshot, in a new conversation of its own. Deep in a
/// long run the model can settle on a wrong idea of where something is ("the menu bar is at
/// y≈3") and click there again and again, whatever the miss notes say; the same model looking at
/// the same screenshot without that history gets it right (17 of 19 targets, all eight menus
/// included, on a real Unity screenshot in ClickAccuracyTests). So every mouse_click is aimed this
/// way: the model names the target, and a fresh look finds it.
/// </summary>
/// <remarks>
/// A look is the slow part of a click: on a 27B model reading about 500 tokens a second, a whole
/// 2560x1080 screenshot (2,720 tokens) took 9 s to read and the thinking about it another 4-13 s.
/// So mouse_click shows it only the quarter of the screen around where the model aimed (see
/// <see cref="Tools.MouseClickTool"/>), and Qwen 3 and later answer it without thinking, their
/// reply begun with an empty think block: 3 s a look, and 19 of 19 Unity targets found.
/// </remarks>
internal sealed partial class FreshLook(OpenAiChatClient client, ChatRequestOptions options)
{
    /// <summary>Room for the reply, thinking included; a look that needs more is given up on.</summary>
    private const int MaxTokens = 4096;

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(120);

    /// <summary>Off only to measure what the fresh look adds (ClickAccuracyTests): clicks then go to the model's own x/y.</summary>
    internal static bool Enabled { get; set; } = true;

    /// <summary>Off only to measure the look at the whole screen (ClickAccuracyTests): every look is then a whole screenshot.</summary>
    internal static bool Cropped { get; set; } = true;

    /// <summary>On only to measure a look with thinking (ClickAccuracyTests): the reply is then not begun with an empty think block.</summary>
    internal static bool Thinking { get; set; }

    /// <summary>What a Qwen 3+ reply without thinking starts with, as its chat template writes it when thinking is off.</summary>
    internal const string NoThinking = "<think>\n\n</think>\n\n";

    /// <summary>
    /// Set once the server refused a reply begun for the model. LM Studio continues such a reply,
    /// while the usual switches (enable_thinking, reasoning_effort, /no_think) changed nothing for qwen3.8.
    /// </summary>
    private bool _prefillRejected;

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
        var prefill = !Thinking && !_prefillRejected && ThinksInQwenTags(options.Model);
        if (prefill)
            request.Add(new ChatMessage { Role = ChatRole.Assistant, Content = NoThinking });
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Timeout);
        ChatMessage reply;
        try
        {
            reply = await client.CompleteAsync(request, [], new ChatRequestOptions { Model = options.Model, Extra = options.Extra, MaxTokens = MaxTokens, QuickAnswer = !Thinking }, null, timeout.Token)
                .ConfigureAwait(false);
        }
        // A server that takes no reply begun for the model says so with an error, not a reply:
        // the look is asked again the usual way, and from then on always.
        catch (LlmException ex) when (prefill && ex is not (ConnectionLostException or RateLimitedException or ContextOverflowException))
        {
            _prefillRejected = true;
            return await LocateAsync(screenshot, coordinates, target, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is LlmException or HttpRequestException or IOException || (ex is OperationCanceledException && !ct.IsCancellationRequested))
        {
            return null;
        }
        return Parse(reply.Content);
    }

    /// <summary>
    /// Asks about pictures in a conversation of its own: a judgement with fresh eyes, such as
    /// whether a clip shows what its shot should (its frames, and a closer look at its busiest
    /// moment). The model may think first; only its answer comes back, or null when the request failed.
    /// </summary>
    public Task<string?> AskAsync(IReadOnlyList<byte[]> pictures, string question, CancellationToken ct) =>
        AskAsync("You look at pictures carefully and say plainly what you see, faults included. You don't praise what isn't good.", question, pictures, 8192, TimeSpan.FromMinutes(5), ct);

    /// <summary>
    /// A question in a conversation of its own, with its own instructions and pictures or none:
    /// only the answer comes back (the thinking before it left out), or null when the request failed.
    /// </summary>
    /// <param name="think">False: a Qwen 3+ model answers without thinking first (its reply begun with an empty think block), much sooner.</param>
    public async Task<string?> AskAsync(string system, string question, IReadOnlyList<byte[]> pictures, int maxTokens, TimeSpan limit, CancellationToken ct, bool think = true)
    {
        List<ChatMessage> request =
        [
            ChatMessage.System(system),
            pictures.Count == 0 ? ChatMessage.User(question)
                : new() { Role = ChatRole.User, Parts = [.. pictures.Select(p => ContentPart.FromImage(p, "image/jpeg")), ContentPart.FromText(question)] },
        ];
        var prefill = !think && !_prefillRejected && ThinksInQwenTags(options.Model);
        if (prefill)
            request.Add(new ChatMessage { Role = ChatRole.Assistant, Content = NoThinking });
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(limit);
        try
        {
            var reply = await client.CompleteAsync(request, [], new ChatRequestOptions { Model = options.Model, Extra = options.Extra, MaxTokens = maxTokens, QuickAnswer = !think }, null, timeout.Token)
                .ConfigureAwait(false);
            var text = reply.Content ?? "";
            if (text.LastIndexOf("</think>", StringComparison.Ordinal) is var end and >= 0)
                text = text[(end + "</think>".Length)..];
            return text.Trim() is { Length: > 0 } answer ? answer : null;
        }
        catch (LlmException ex) when (prefill && ex is not (ConnectionLostException or RateLimitedException or ContextOverflowException))
        {
            _prefillRejected = true;
            return await AskAsync(system, question, pictures, maxTokens, limit, ct, think).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is LlmException or HttpRequestException or IOException || (ex is OperationCanceledException && !ct.IsCancellationRequested))
        {
            return null;
        }
    }

    /// <summary>Qwen 3 and later: their chat templates mark thinking with think tags and skip it for a reply begun with an empty pair.</summary>
    internal static bool ThinksInQwenTags(string? model) => model is not null && QwenThreeOrLater().IsMatch(model);

    internal static (double X, double Y)? Parse(string? reply)
    {
        if (Point().Matches(reply ?? "").LastOrDefault() is not { } m)
            return null;
        return (double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture));
    }

    [GeneratedRegex(@"(\d+(?:\.\d+)?)\s*,\s*(\d+(?:\.\d+)?)")]
    private static partial Regex Point();

    [GeneratedRegex(@"qwen([3-9]|\d{2})", RegexOptions.IgnoreCase)]
    private static partial Regex QwenThreeOrLater();
}
