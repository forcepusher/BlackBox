using System.Text.Json;
using System.Text.Json.Nodes;
using BlackBox.Core.Agent;
using CommunityToolkit.Mvvm.ComponentModel;

namespace BlackBox.App.ViewModels;

/// <summary>
/// A tool that opens on the left of the chat window, picked in the Toolkit dropdown at the top of
/// it (Filmmaking now; more to come). It works through the chat: what is picked in it goes into the
/// chat's box, and what the user sends from there is done by the agent in the chat, in view.
/// </summary>
public interface IToolkit : IDisposable
{
    /// <summary>
    /// What a message sent while the tool is open carries: the line over its bubble, and what the
    /// model is told with it (the tool's work, what of it the files picked into the box are); null
    /// when the tool has nothing to say about it.
    /// </summary>
    (string Via, string Context)? About(IReadOnlyList<string> files);

    /// <summary>A message went: what was picked into the box went with it.</summary>
    void Sent();

    /// <summary>Files dropped on the tool's panel; false when the tool doesn't take them (they then go with the next message).</summary>
    bool Drop(IReadOnlyList<string> paths);
}

/// <summary>An entry of the Toolkit dropdown: None, or a tool with how wide its panel opens.</summary>
public sealed record ToolkitOption(string Name, string Description, double Width, Func<MainWindowViewModel, IToolkit>? Open)
{
    /// <summary>What the dropdown says while it is picked: "Toolkit" with none, else the tool's name.</summary>
    public string Label => Open is null ? "Toolkit" : Name;

    public override string ToString() => Name;
}

public sealed partial class MainWindowViewModel
{
    // ---- the toolkit: a tool's panel on the left of the chat --------------------------------

    /// <summary>What the Toolkit dropdown offers: nothing, or a tool whose panel opens on the left of the chat.</summary>
    public IReadOnlyList<ToolkitOption> ToolkitOptions { get; } =
    [
        new("None", "The chat alone", 0, null),
        new("Filmmaking", "Plan a film shot by shot and make it with ComfyUI: pick a picture, clip or folder of it and say in the chat what to do", 960,
            vm => new FilmViewModel(vm._previews, vm, vm._paths.FilmsDir, null)),
    ];

    [ObservableProperty]
    private ToolkitOption? _selectedToolkit;

    /// <summary>The open tool's panel, on the left of the chat; null with none.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasToolkit))]
    private IToolkit? _toolkit;

    public bool HasToolkit => Toolkit is not null;

    partial void OnSelectedToolkitChanged(ToolkitOption? value)
    {
        var old = Toolkit;
        Toolkit = null;
        // What it put in the box goes with it: about a film no longer open, it would be sent bare.
        foreach (var picked in Attachments.Where(a => a.Picked).ToList())
            RemoveAttachment(picked);
        old?.Dispose();
        if (value?.Open is { } open && !_closed)
            Toolkit = open(this);
    }

    /// <summary>
    /// What is picked in the toolkit's panel, into the box (one picked there before taken out): the
    /// user then says what to do with it. A piece not made yet has no file: it goes as its place.
    /// </summary>
    internal void Pick(IReadOnlyList<(string Path, string Caption)> picked)
    {
        foreach (var old in Attachments.Where(a => a.Picked && !picked.Any(p => string.Equals(p.Path, a.Path, StringComparison.OrdinalIgnoreCase))).ToList())
            RemoveAttachment(old);
        foreach (var (path, caption) in picked)
            if (!Attachments.Any(a => string.Equals(a.Path, path, StringComparison.OrdinalIgnoreCase)))
                Attachments.Add(new AttachmentItem(path, RemoveAttachment, caption));
    }

    /// <summary>The paths picked in the toolkit's panel that are in the box now.</summary>
    internal IReadOnlyList<string> Picked => Attachments.Where(a => a.Picked).Select(a => a.Path).ToList();

    /// <summary>A message from the toolkit's panel (Make the film): into this chat as if typed in the box, with what the tool says about it.</summary>
    internal void SendFromToolkit(string text)
    {
        if (!_closed)
            _ = SendAsync(WithToolkit(new UserInput(text), []));
    }

    /// <summary>
    /// A message as the model gets it while a tool is open: with what the tool says about it and what
    /// was picked in it, which then goes with it (none for a message of the tool's own: what is picked
    /// stays in the box for the next).
    /// </summary>
    private UserInput WithToolkit(UserInput input, IReadOnlyList<string> picked)
    {
        if (Toolkit?.About(picked) is not { } about)
            return input;
        if (picked.Count > 0)
            Toolkit.Sent();
        return input with { Via = about.Via, Context = about.Context };
    }

    /// <summary>
    /// A file about to be written over (a shot made again): the chat's players of it, and the
    /// toolkit's, stop; their playbacks, still holding it, are handed back to be ended.
    /// </summary>
    internal List<IDisposable> LetGo(string path)
    {
        var playing = new List<IDisposable>();
        foreach (var preview in Items.SelectMany(i => i.Previews).OfType<PlayerPreview>()
                     .Where(p => string.Equals(Path.GetFullPath(p.Path), path, StringComparison.OrdinalIgnoreCase)))
            if (preview.LetGo() is { } playback)
                playing.Add(playback);
        if (Toolkit is FilmViewModel film && film.LetGo(path) is { } shown)
            playing.Add(shown);
        return playing;
    }

    /// <summary>Files dropped on the toolkit's panel: the tool takes them, or they go with the next message.</summary>
    internal bool DropOnToolkit(IReadOnlyList<string> paths) => Toolkit?.Drop(paths) == true;

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
