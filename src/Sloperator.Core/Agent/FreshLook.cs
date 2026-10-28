using System.Text.RegularExpressions;
using Sloperator.Core.Llm;

namespace Sloperator.Core.Agent;

/// <summary>
/// Asks the model about pictures in a new conversation of its own, without the run's history: a
/// judgement with fresh eyes, such as whether a clip shows what its shot should.
/// </summary>
internal sealed partial class FreshLook(OpenAiChatClient client, ChatRequestOptions options)
{
    /// <summary>What a Qwen 3+ reply without thinking starts with, as its chat template writes it when thinking is off.</summary>
    internal const string NoThinking = "<think>\n\n</think>\n\n";

    /// <summary>
    /// Set once the server refused a reply begun for the model. LM Studio continues such a reply,
    /// while the usual switches (enable_thinking, reasoning_effort, /no_think) changed nothing for qwen3.8.
    /// </summary>
    private bool _prefillRejected;

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
        // A server that takes no reply begun for the model says so with an error, not a reply:
        // the question is asked again the usual way, and from then on always.
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

    [GeneratedRegex(@"qwen([3-9]|\d{2})", RegexOptions.IgnoreCase)]
    private static partial Regex QwenThreeOrLater();
}
