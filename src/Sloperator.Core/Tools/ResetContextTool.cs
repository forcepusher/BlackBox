using System.Text.Json.Nodes;

namespace Sloperator.Core.Tools;

/// <summary>
/// Starts a fresh context with a handoff note, as when the context fills up, but when the user
/// asks for it. Told "Do a handoff to reset the context", the model had no way to: it wrote a
/// handoff file in the scratch folder and went on in the same full context.
/// </summary>
public sealed class ResetContextTool : Tool
{
    public override string Name => "reset_context";

    public override string Description =>
        "Reset your context: next you are asked for a handoff note for yourself, then the conversation is replaced by the user's messages, " +
        "that note and the current screenshot, and you carry on from there. Use it when the user asks you to reset, summarize or compact your " +
        "context, hand off or start fresh (\"Summarize context\" asks for this tool, not for a summary in your reply); the app does this by itself when the context is nearly full. Call it alone, as your reply's last call.";

    public override ToolRisk Risk => ToolRisk.ReadOnly;

    public override JsonObject Parameters => Schema();

    public override string Describe(JsonObject args) => "Reset the context with a handoff note";

    public override Task<ToolResult> ExecuteAsync(JsonObject args, ToolContext ctx, CancellationToken ct) =>
        Task.FromResult(new ToolResult(ctx.ResetContext is not { } reset
            ? "The context can't be reset here."
            : reset()
                ? "The context will be reset now: you are asked for the handoff note next."
                : "The context was just reset: there is nothing to hand off yet. Carry on with the task."));
}
