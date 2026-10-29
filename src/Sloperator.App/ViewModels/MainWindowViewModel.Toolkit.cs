using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia.Threading;
using Sloperator.Core.Agent;
using Sloperator.Core.Diffs;
using Sloperator.Core.Settings;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Sloperator.App.ViewModels;

/// <summary>
/// A tool that opens on the left of the chat window, picked in the Toolkit dropdown at the top of
/// it (Video generator, File changes). It works through the chat: what is picked in it goes into the
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

/// <summary>
/// An entry of the Toolkit dropdown: None, or a tool with how narrow its panel may get with its top
/// bar still in one row, splitter included, and how short the window may get with it open (0: as
/// short as the chat may). Every tool's panel opens as wide (<see cref="ToolkitColumns.PanelWidth"/>).
/// </summary>
public sealed record ToolkitOption(string Name, string Description, double MinWidth, double MinHeight, Func<MainWindowViewModel, IToolkit>? Open)
{
    /// <summary>How wide the panel opens, splitter included; 0 for None.</summary>
    public double Width => Open is null ? 0 : ToolkitColumns.PanelWidth;

    /// <summary>What the dropdown says while it is picked: "Side panel" with none, else the tool's name.</summary>
    public string Label => Open is null ? "Side panel" : Name;

    public override string ToString() => Name;
}

public sealed partial class MainWindowViewModel
{
    // ---- the toolkit: a tool's panel on the left of the chat --------------------------------

    /// <summary>What the Toolkit dropdown offers: nothing, or a tool whose panel opens on the left of the chat.</summary>
    public IReadOnlyList<ToolkitOption> ToolkitOptions { get; } =
    [
        new("None", "", 0, 0, null),
        new(FileChangesName, "Files the agent changed", 560, 0,
            vm => new FileChangesViewModel(vm)),
        new(FileInspectorName, "Any file from the chat", 420, 0,
            vm => new FileInspectorViewModel(vm._previews, vm, vm._paths.PastedDir)),
        new("Video generator", "Plan and make a video shot by shot", 770, 600,
            vm => new VideoGeneratorViewModel(vm._previews, vm, vm._paths.VideosDir, null)),
    ];

    private const string FileInspectorName = "File inspector", FileChangesName = "File changes";

    /// <summary>A card's Inspect: the file in the File inspector panel, which opens in place of the tool open (if another).</summary>
    internal void Inspect(string path)
    {
        if (Toolkit is not FileInspectorViewModel)
            SelectedToolkit = ToolkitOptions.First(o => o.Name == FileInspectorName);
        (Toolkit as FileInspectorViewModel)?.Show(path);
    }

    /// <summary>The × on the open tool's panel: None in the dropdown.</summary>
    internal void CloseToolkit() => SelectedToolkit = ToolkitOptions[0];

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
        // What it put in the box goes with it: about a video no longer open, it would be sent bare.
        foreach (var picked in Attachments.Where(a => a.Picked).ToList())
            RemoveAttachment(picked);
        // One tool for another in one change, so the window goes from the one's panel to the other's
        // without closing between.
        Toolkit = value?.Open is { } open && !_closed ? open(this) : null;
        old?.Dispose();
    }

    /// <summary>
    /// What is picked in the toolkit's panel, into the box (one picked there before taken out): the
    /// user then says what to do with it. A piece not made yet has no file: it goes as its place.
    /// One picked again under another caption (more lines of a file in the File changes panel) is put back with it.
    /// </summary>
    internal void Pick(IReadOnlyList<(string Path, string Caption)> picked)
    {
        foreach (var old in Attachments.Where(a => a.Picked && !picked.Any(p => string.Equals(p.Path, a.Path, StringComparison.OrdinalIgnoreCase) && p.Caption == a.Caption)).ToList())
            RemoveAttachment(old);
        foreach (var (path, caption) in picked)
        {
            if (Attachments.Any(a => a.Picked && string.Equals(a.Path, path, StringComparison.OrdinalIgnoreCase)))
                continue;
            // The same file put in the box by the user goes as the pick: left there, the pick wasn't
            // added and the message lost what was picked in it (Sloperator's own review of the File changes panel).
            foreach (var same in Attachments.Where(a => string.Equals(a.Path, path, StringComparison.OrdinalIgnoreCase)).ToList())
                RemoveAttachment(same);
            // After the other picks, before the user's own files: what comes from the tool goes first (the user, 2026-10-26).
            Attachments.Insert(Attachments.Count(a => a.Picked), new AttachmentItem(path, RemoveAttachment, caption));
        }
    }

    /// <summary>The paths picked in the toolkit's panel that are in the box now.</summary>
    internal IReadOnlyList<string> Picked => Attachments.Where(a => a.Picked).Select(a => a.Path).ToList();

    /// <summary>
    /// A message from the toolkit's panel (Make the video): into this chat as if typed in the box, with
    /// what the tool says about it. With <paramref name="withPicks"/> (Generate on a piece), about
    /// what is picked in the box, which goes with it as when the user sends: "Make it." sent bare was
    /// about the whole video, and the model was asked to make all of it.
    /// </summary>
    internal void SendFromToolkit(string text, bool withPicks = false)
    {
        if (_closed)
            return;
        if (!withPicks)
        {
            _ = SendAsync(WithToolkit(new UserInput(text), [], fromTool: true));
            return;
        }
        var picked = Attachments.Where(a => a.Picked).ToList();
        var input = WithToolkit(new UserInput(text, picked.Where(a => File.Exists(a.Path) || Directory.Exists(a.Path)).Select(a => a.Path).ToList()), Picked, fromTool: true);
        foreach (var pick in picked)
            RemoveAttachment(pick);
        _ = SendAsync(input);
    }

    /// <summary>
    /// A message as the model gets it while a tool is open: with what the tool says about it and what
    /// was picked in it, which then goes with it (none for a message of the tool's own: what is picked
    /// stays in the box for the next), and the cards of those as the box showed them. A message the
    /// user typed with nothing picked goes as it is: no card, no word of the tool (the user,
    /// 2026-10-28: a card of what they never picked, with no × to take it off, was context they
    /// didn't choose). The tool's own messages (Make the video) always say what the tool shows.
    /// </summary>
    private UserInput WithToolkit(UserInput input, IReadOnlyList<string> picked, bool fromTool = false)
    {
        if (Toolkit is not { } toolkit || picked.Count == 0 && !fromTool)
            return input;
        var cards = Attachments.Where(a => a.Picked && picked.Contains(a.Path, StringComparer.OrdinalIgnoreCase))
            .Select(a => new ToolCard(a.Title, Path: a.Path)).ToList();
        var name = SelectedToolkit?.Name ?? "Side panel";
        var about = toolkit.About(picked) ?? (name, $"[The {name} panel is open beside the chat, with nothing in it yet.]");
        if (picked.Count > 0)
            toolkit.Sent();
        return input with { Via = about.Via, Context = about.Context, Cards = cards };
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
        if (Toolkit is VideoGeneratorViewModel video && video.LetGo(path) is { } shown)
            playing.Add(shown);
        return playing;
    }

    // ---- the files the agent changed in this chat (the File changes panel) -------------------------

    /// <summary>The files the agent changed in the chat on screen, each with what it held before.</summary>
    internal FileChanges Changes { get; private set; } = null!;

    /// <summary>How the toolkit panels show things (File changes' Wrap and Watch), saved as the user leaves them.</summary>
    internal PanelSettings Panels => _settings.Panels;

    internal void SavePanels() => SaveSettings();

    /// <summary>On the window's thread: the list changed, or another chat's took its place.</summary>
    internal event Action? ChangesUpdated;

    /// <summary>
    /// The chat on screen has had no file changed by the agent: its first change opens File changes,
    /// once (the user, 2026-10-27). A chat whose list has files had its turn, closed or not.
    /// </summary>
    private bool _changesNotShown;

    private void UseChanges(FileChanges changes)
    {
        if (Changes is not null)
            Changes.Changed -= OnChanges;
        Changes = changes;
        _changesNotShown = changes.IsEmpty;
        changes.Changed += OnChanges;
        ChangesUpdated?.Invoke();
    }

    // A run stopped by leaving the chat still writes into the chat's own list, never into this one.
    private void OnChanges(FileChanges changes)
    {
        // Read on the agent's thread: a write_file that failed is in the list, but changed nothing to show.
        var first = _changesNotShown && ReferenceEquals(changes, Changes) && changes.Diff().Files.Count > 0;
        Dispatcher.UIThread.Post(() =>
        {
            if (!ReferenceEquals(changes, Changes))
                return;
            if (first && _changesNotShown)
                OpenOnFirstChange();
            ChangesUpdated?.Invoke();
        });
    }

    /// <summary>The agent's first change in the chat: File changes opens, unless the user turned that off or has another tool open, which it doesn't take the place of.</summary>
    private void OpenOnFirstChange()
    {
        _changesNotShown = false;
        if (!_settings.Panels.OpenFileChanges || Toolkit is not null || _closed)
            return;
        OpeningOnFirstChange = true;
        try { SelectedToolkit = ToolkitOptions.First(o => o.Name == FileChangesName); }
        finally { OpeningOnFirstChange = false; }
    }

    /// <summary>Set while File changes opens on the agent's first change: what its first read shows is that change, lit up.</summary>
    internal bool OpeningOnFirstChange { get; private set; }

    /// <summary>Where a chat's list is kept: Data/chats/changes/id.json.</summary>
    private string ChangesFile(string chatId) => Path.Combine(_chats.Folder, "changes", Path.GetFileName(chatId) + ".json");

    /// <summary>Files dropped on the toolkit's panel: the tool takes them, or they go with the next message.</summary>
    internal bool DropOnToolkit(IReadOnlyList<string> paths) => Toolkit?.Drop(paths) == true;

    /// <summary>The video tool's call running in this chat now: what it was given, and how it is getting on; null when none runs.</summary>
    internal (JsonObject Args, string Status)? VideoCall()
    {
        if (!IsBusy || Items.OfType<ToolCallItem>().LastOrDefault(t => t.State == ToolItemState.Running) is not { Name: "video" } call)
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
