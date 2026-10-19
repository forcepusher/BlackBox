using BlackBox.Core.Llm;

namespace BlackBox.Core.Agent;

/// <summary>
/// A question for the chat model outside a chat, in a conversation of its own: what a window asks
/// it, such as which installed model the user's words mean.
/// </summary>
public static class ModelQuestion
{
    /// <returns>The answer, or null when the server couldn't be asked.</returns>
    public static Task<string?> AskAsync(BackendConnection backend, string question, CancellationToken ct) =>
        new FreshLook(backend.Client, backend.Options).AskTextAsync(question, ct);
}
