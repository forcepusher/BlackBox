using Sloperator.Core.Llm;

namespace Sloperator.Core.Agent;

/// <summary>
/// Asks the model about pictures in a new conversation of its own, without the run's history: a
/// judgement with fresh eyes, such as whether a clip shows what its shot should.
/// </summary>
internal sealed class FreshLook(OpenAiChatClient client, ChatRequestOptions options)
{
    private const string System = "You look at pictures carefully and say plainly what you see, faults included. You don't praise what isn't good.";

    /// <summary>
    /// Asks about pictures in a conversation of its own: a judgement with fresh eyes, such as
    /// whether a clip shows what its shot should (its frames, and a closer look at its busiest
    /// moment). The model may think first; only its answer comes back, or null when the request failed.
    /// </summary>
    public async Task<string?> AskAsync(IReadOnlyList<byte[]> pictures, string question, CancellationToken ct)
    {
        List<ChatMessage> request =
        [
            ChatMessage.System(System),
            pictures.Count == 0 ? ChatMessage.User(question)
                : new() { Role = ChatRole.User, Parts = [.. pictures.Select(p => ContentPart.FromImage(p, "image/jpeg")), ContentPart.FromText(question)] },
        ];
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(5));
        try
        {
            var reply = await client.CompleteAsync(request, [], new ChatRequestOptions { Model = options.Model, Extra = options.Extra, MaxTokens = 8192 }, null, timeout.Token)
                .ConfigureAwait(false);
            var text = reply.Content ?? "";
            if (text.LastIndexOf("</think>", StringComparison.Ordinal) is var end and >= 0)
                text = text[(end + "</think>".Length)..];
            return text.Trim() is { Length: > 0 } answer ? answer : null;
        }
        catch (Exception ex) when (ex is LlmException or HttpRequestException or IOException || (ex is OperationCanceledException && !ct.IsCancellationRequested))
        {
            return null;
        }
    }
}
