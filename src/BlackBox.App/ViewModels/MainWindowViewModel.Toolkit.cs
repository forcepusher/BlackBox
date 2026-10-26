using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia.Threading;
using BlackBox.Core.Agent;
using BlackBox.Core.Diffs;
using BlackBox.Core.Settings;
using CommunityToolkit.Mvvm.ComponentModel;

namespace BlackBox.App.ViewModels;

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

    /// <summary>
    /// What a message is about while nothing is picked in the tool, shown over the chat's box where a
    /// pick would be ("Untitled video", "whole video"); null when the tool shows nothing yet, and the
    /// card then names the tool. A change is told by the tool's PropertyChanged under this name.
    /// </summary>
    (string Title, string Details)? Whole => null;

    /// <summary>The tooltip of <see cref="Whole"/>'s card: what it is, and how to pick something instead.</summary>
    string WholeTip => "";

    /// <summary>The path data of the icon on <see cref="Whole"/>'s card; a file's with none.</summary>
    string? WholeIcon => null;
}

/// <summary>
/// An entry of the Toolkit dropdown: None, or a tool with how wide its panel opens and how narrow it
/// may get with its top bar still in one row, both with the splitter on its right, and how short the
/// window may get with it open (0: as short as the chat may).
/// </summary>
public sealed record ToolkitOption(string Name, string Description, double Width, double MinWidth, double MinHeight, Func<MainWindowViewModel, IToolkit>? Open)
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
        new("None", "The chat alone", 0, 0, 0, null),
        new(FileChangesName, "See the files the agent changes in this chat as it works: click lines to ask about them", 880, 560, 0,
            vm => new FileChangesViewModel(vm)),
        new(FileInspectorName, "See a file from the chat beside it: documents, spreadsheets, pictures, video, sound, 3D, code", 760, 420, 0,
            vm => new FileInspectorViewModel(vm._previews, vm, vm._paths.PastedDir)),
        new("Video generator", "Plan a video shot by shot and make it with ComfyUI: pick a picture, clip or folder and say what to do", 960, 770, 600,
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

    partial void OnToolkitChanged(IToolkit? oldValue, IToolkit? newValue)
    {
        if (oldValue is INotifyPropertyChanged old)
            old.PropertyChanged -= OnToolkitPropertyChanged;
        if (newValue is INotifyPropertyChanged now)
            now.PropertyChanged += OnToolkitPropertyChanged;
        WholeChanged();
    }

    private void OnToolkitPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(IToolkit.Whole) or nameof(IToolkit.WholeTip))
            WholeChanged();
    }

    /// <summary>
    /// What a message is about with nothing picked in the open tool (the whole video), as a card; with
    /// a tool that shows nothing yet, the tool itself. Every open tool has one or a pick in the box,
    /// so what goes with a message from it is always in view (the user, 2026-10-25).
    /// </summary>
    private ToolCard WholeOf(IToolkit toolkit) =>
        toolkit.Whole is { } whole ? new ToolCard(whole.Title, whole.Details, Icon: toolkit.WholeIcon)
        : new ToolCard(SelectedToolkit?.Name ?? "Toolkit", EmptyToolkit);

    private const string EmptyToolkit = "nothing in it yet";

    /// <summary>The card over the box of what a message is about in the open tool; null with something picked there, or no tool open.</summary>
    [ObservableProperty]
    private ToolCardItem? _wholeCard;

    public bool ShowsWhole => WholeCard is not null;

    /// <summary>The row over the box: the files going with the message, and what it is about in the open tool.</summary>
    public bool ShowsBoxRow => HasAttachments || ShowsWhole;

    private void WholeChanged()
    {
        WholeCard = Toolkit is { } toolkit && !Attachments.Any(a => a.Picked) && WholeOf(toolkit) is { } card ? new ToolCardItem(card, toolkit.WholeTip) : null;
        OnPropertyChanged(nameof(ShowsWhole));
        OnPropertyChanged(nameof(ShowsBoxRow));
    }

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
            // added and the message lost what was picked in it (BlackBox's own review of the File changes panel).
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
            _ = SendAsync(WithToolkit(new UserInput(text), []));
            return;
        }
        var picked = Attachments.Where(a => a.Picked).ToList();
        var input = WithToolkit(new UserInput(text, picked.Where(a => File.Exists(a.Path) || Directory.Exists(a.Path)).Select(a => a.Path).ToList()), Picked);
        foreach (var pick in picked)
            RemoveAttachment(pick);
        _ = SendAsync(input);
    }

    /// <summary>
    /// A message as the model gets it while a tool is open: with what the tool says about it and what
    /// was picked in it, which then goes with it (none for a message of the tool's own: what is picked
    /// stays in the box for the next), and the cards of those as the box showed them. A tool with
    /// nothing to say still has the model told it is open, as its card says.
    /// </summary>
    private UserInput WithToolkit(UserInput input, IReadOnlyList<string> picked)
    {
        if (Toolkit is not { } toolkit)
            return input;
        var cards = Attachments.Where(a => a.Picked && picked.Contains(a.Path, StringComparer.OrdinalIgnoreCase))
            .Select(a => new ToolCard(a.Title, Path: a.Path)).ToList();
        if (cards.Count == 0)
            cards.Add(WholeOf(toolkit));
        var name = SelectedToolkit?.Name ?? "Toolkit";
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
        if (_settings.Panels.OpenFileChanges && Toolkit is null && !_closed)
            SelectedToolkit = ToolkitOptions.First(o => o.Name == FileChangesName);
    }

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
