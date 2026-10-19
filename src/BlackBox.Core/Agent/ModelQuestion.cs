using BlackBox.Core.Llm;

namespace BlackBox.Core.Agent;

/// <summary>
/// A question for the chat model outside a chat, in a conversation of its own: what a window asks
/// it, such as which installed model the user's words mean, or what a film's pieces should be.
/// </summary>
public static class ModelQuestion
{
    /// <returns>The answer, or null when the server couldn't be asked.</returns>
    public static Task<string?> AskAsync(BackendConnection backend, string question, CancellationToken ct) =>
        new FreshLook(backend.Client, backend.Options).AskTextAsync(question, ct);

    /// <summary>A question with its own instructions, and pictures when the model sees pictures (else they are left out).</summary>
    /// <returns>The answer (the thinking before it left out), or null when the server couldn't be asked.</returns>
    /// <param name="think">False: a Qwen 3+ model answers without thinking first, much sooner.</param>
    public static Task<string?> AskAsync(BackendConnection backend, string system, string question, IReadOnlyList<byte[]> pictures, int maxTokens, TimeSpan limit, CancellationToken ct, bool think = true) =>
        new FreshLook(backend.Client, backend.Options).AskAsync(system, question, backend.Vision ? pictures : [], maxTokens, limit, ct, think);
}
