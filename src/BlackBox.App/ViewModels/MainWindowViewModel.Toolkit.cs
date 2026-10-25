using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia.Threading;
using BlackBox.Core.Agent;
using BlackBox.Core.Diffs;
using CommunityToolkit.Mvvm.ComponentModel;

namespace BlackBox.App.ViewModels;

/// <summary>
/// A tool that opens on the left of the chat window, picked in the Toolkit dropdown at the top of
/// it (Video editor, Diff). It works through the chat: what is picked in it goes into the
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
    /// pick would be ("Untitled video", "whole video"); null when the tool has none. A change is told
    /// by the tool's PropertyChanged under this name.
    /// </summary>
    (string Title, string Details)? Whole => null;

    /// <summary>The tooltip of <see cref="Whole"/>'s card: what it is, and how to pick something instead.</summary>
    string WholeTip => "";

    /// <summary>The icon on <see cref="Whole"/>'s card; a file's with none.</summary>
    Avalonia.Media.Geometry? WholeIcon => null;
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
        new("Video editor", "Plan a video shot by shot and make it with ComfyUI: pick a picture, clip or folder and say what to do", 960, 770, 600,
            vm => new VideoViewModel(vm._previews, vm, vm._paths.VideosDir, null)),
        new("Code changes", "See the files the agent changes in this chat as it works: click lines to ask about them", 880, 560, 0,
            vm => new DiffViewModel(vm)),
        new(DocumentPreviewName, "See a file from the chat beside it: documents, PDFs, spreadsheets, slides, pictures, text and code", 760, 420, 0,
            vm => new DocumentViewModel(vm._previews, vm)),
    ];

    private const string DocumentPreviewName = "Document preview";

    /// <summary>A card's Preview: the file in the Document preview panel, which opens in place of the tool open (if another).</summary>
    internal void PreviewFile(string path)
    {
        if (Toolkit is not DocumentViewModel)
            SelectedToolkit = ToolkitOptions.First(o => o.Name == DocumentPreviewName);
        (Toolkit as DocumentViewModel)?.Show(path);
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
        if (e.PropertyName == nameof(IToolkit.Whole))
            WholeChanged();
    }

    /// <summary>
    /// What a message is about with nothing picked in the open tool (the whole video), shown over the
    /// box where a pick would be; null with something picked, or with no tool that has one.
    /// </summary>
    private (string Title, string Details)? ShownWhole => Attachments.Any(a => a.Picked) ? null : Toolkit?.Whole;

    public bool ShowsWhole => ShownWhole is not null;
    public string WholeTitle => ShownWhole?.Title ?? "";
    public string WholeDetails => ShownWhole?.Details ?? "";
    public string WholeTip => Toolkit?.WholeTip ?? "";
    public Avalonia.Media.Geometry WholeIcon => Toolkit?.WholeIcon ?? AttachmentItem.FileShape;

    /// <summary>The row over the box: the files going with the message, and what it is about in the open tool.</summary>
    public bool ShowsBoxRow => HasAttachments || ShowsWhole;

    private void WholeChanged()
    {
        OnPropertyChanged(nameof(ShowsWhole));
        OnPropertyChanged(nameof(WholeTitle));
        OnPropertyChanged(nameof(WholeDetails));
        OnPropertyChanged(nameof(WholeTip));
        OnPropertyChanged(nameof(WholeIcon));
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
    /// One picked again under another caption (more lines of a file in the Diff panel) is put back with it.
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
            // added and the message lost what was picked in it (BlackBox's own review of the Diff panel).
            foreach (var same in Attachments.Where(a => string.Equals(a.Path, path, StringComparison.OrdinalIgnoreCase)).ToList())
                RemoveAttachment(same);
            Attachments.Add(new AttachmentItem(path, RemoveAttachment, caption));
        }
    }

    /// <summary>The paths picked in the toolkit's panel that are in the box now.</summary>
    internal IReadOnlyList<string> Picked => Attachments.Where(a => a.Picked).Select(a => a.Path).ToList();

    /// <summary>A message from the toolkit's panel (Make the video): into this chat as if typed in the box, with what the tool says about it.</summary>
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
        if (Toolkit is VideoViewModel video && video.LetGo(path) is { } shown)
            playing.Add(shown);
        return playing;
    }

    // ---- the files the agent changed in this chat (the Code changes panel) -------------------------

    /// <summary>The files the agent changed in the chat on screen, each with what it held before.</summary>
    internal FileChanges Changes { get; private set; } = null!;

    /// <summary>On the window's thread: the list changed, or another chat's took its place.</summary>
    internal event Action? ChangesUpdated;

    private void UseChanges(FileChanges changes)
    {
        if (Changes is not null)
            Changes.Changed -= OnChanges;
        Changes = changes;
        changes.Changed += OnChanges;
        ChangesUpdated?.Invoke();
    }

    // A run stopped by leaving the chat still writes into the chat's own list, never into this one.
    private void OnChanges(FileChanges changes) => Dispatcher.UIThread.Post(() =>
    {
        if (ReferenceEquals(changes, Changes))
            ChangesUpdated?.Invoke();
    });

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
