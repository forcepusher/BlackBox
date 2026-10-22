using System.Text.Json;
using System.Text.Json.Nodes;
using BlackBox.Core.Agent;

namespace BlackBox.App.ViewModels;

public sealed partial class MainWindowViewModel
{
    // ---- the Filmmaking window: an add-on of this chat ---------------------------------

    /// <summary>
    /// The Filmmaking window's model. What the user tells it, and what its buttons ask for, come to
    /// this chat as messages: the agent does them here with the film tool, for the user to watch and
    /// stop, with every frame and clip it makes; the window shows the film as it changes.
    /// </summary>
    public FilmViewModel CreateFilmViewModel() => new(_previews, this, _paths.FilmsDir, null) { ShowChatWindow = () => _windows.BringForward(this) };

    /// <summary>A message from the Filmmaking window: into this chat as if typed here; during a run it waits above the box until the model reads it.</summary>
    internal void SendFromFilm(UserInput input)
    {
        if (!_closed)
            _ = SendAsync(input);
    }

    /// <summary>What the run in this chat is doing now, in a few words for the Filmmaking window ("Make shot s2 · KSampler, step 3 of 8"); null when no run goes on.</summary>
    internal string? Doing()
    {
        if (!IsBusy)
            return null;
        if (Items.OfType<ToolCallItem>().LastOrDefault(t => t.State is ToolItemState.Running or ToolItemState.AwaitingApproval) is { } tool)
            return tool.State == ToolItemState.AwaitingApproval ? $"{tool.Summary}, waiting for your OK" : $"{tool.Summary} · {tool.StatusText}";
        if (_current is { } reply)
            return reply.HasText ? "writing" : reply.HasReasoning ? "thinking" : "waiting for the model";
        return "working";
    }

    /// <summary>The film tool's call running in this chat now: what it was given, and how it is getting on; null when none runs.</summary>
    internal (JsonObject Args, string Status)? FilmCall()
    {
        if (!IsBusy || Items.OfType<ToolCallItem>().LastOrDefault(t => t.State == ToolItemState.Running) is not { Name: "film" } call)
            return null;
        try
        {
            return JsonNode.Parse(call.Arguments) is JsonObject args ? (args, call.Progress) : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
