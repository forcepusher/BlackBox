using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia.Threading;
using Sloperator.Core.Agent;
using Sloperator.Core.Diffs;
using Sloperator.Core.Settings;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Sloperator.App.ViewModels;

/// <summary>
/// A panel that opens on the left of the chat window, picked in the Side panel dropdown at the top of
/// it (Video generator, File changes). It works through the chat: what is picked in it goes into the
/// chat's box, and what the user sends from there is done by the agent in the chat, in view.
/// </summary>
public interface ISidePanel : IDisposable
{
    /// <summary>
    /// What a message sent while the panel is open carries: the line over its bubble, and what the
    /// model is told with it (the panel's work, what of it the files picked into the box are); null
    /// when the panel has nothing to say about it.
    /// </summary>
    (string Via, string Context)? About(IReadOnlyList<string> files);

    /// <summary>A message went: what was picked into the box went with it.</summary>
    void Sent();

    /// <summary>Files dropped on the panel; false when the panel doesn't take them (they then go with the next message).</summary>
    bool Drop(IReadOnlyList<string> paths);
}

/// <summary>A side panel that follows the agent's edits, with Wrap and Watch in its top bar (File changes, Code editor; see WatchToggles).</summary>
public interface IWatchPanel : ISidePanel
{
    /// <summary>Long lines wrap inside the panel; kept in settings.</summary>
    bool Wrap { get; set; }

    /// <summary>The panel goes to what the agent changes as it changes it; kept in settings.</summary>
    bool Watch { get; set; }
}

/// <summary>A side panel that shows one file, which a file's right-click menu opens in it (File inspector, Code editor).</summary>
public interface IFilePanel : ISidePanel
{
    /// <summary>The file shown; null before one is.</summary>
    string? Path { get; }

    /// <summary>Shows <paramref name="path"/> in the panel, in place of the file shown.</summary>
    void Show(string path);
}

/// <summary>
/// An entry of the Side panel dropdown: None, or a panel with how narrow it may get with its top
/// bar still in one row, splitter included, and how short the window may get with it open (0: as
/// short as the chat may). Every panel opens as wide (<see cref="SidePanelColumns.PanelWidth"/>).
/// </summary>
/// <param name="OpenIn">
/// For an <see cref="IFilePanel"/>: what every file's right-click menu calls it, "Open in Inspector"
/// (see <see cref="Views.FileMenu"/>); null for a panel no file opens in.
/// </param>
/// <param name="Opens">Which files it opens from that menu, by their path.</param>
public sealed record SidePanelOption(string Name, string Description, double MinWidth, double MinHeight, Func<MainWindowViewModel, ISidePanel>? Open,
    string? OpenIn = null, Func<string, bool>? Opens = null)
{
    /// <summary>How wide the panel opens, splitter included; 0 for None.</summary>
    public double Width => Open is null ? 0 : SidePanelColumns.PanelWidth;

    /// <summary>What the dropdown says while it is picked: "Side panel" with none, else the panel's name.</summary>
    public string Label => Open is null ? "Side panel" : Name;

    public override string ToString() => Name;
}

public sealed partial class MainWindowViewModel
{
    // ---- the side panel, on the left of the chat ------------------------------------------------

    /// <summary>What the Side panel dropdown offers: nothing, or a panel that opens on the left of the chat.</summary>
    public IReadOnlyList<SidePanelOption> SidePanelOptions { get; } =
    [
        new("None", "", 0, 0, null),
        new(FileChangesName, "Files the agent changed", 560, 0,
            vm => new FileChangesViewModel(vm)),
        new(FileInspectorName, "Look inside any file", 420, 0,
            vm => new FileInspectorViewModel(vm._previews, vm, vm._paths.PastedDir),
            OpenIn: "Inspector", Opens: path => !LocalFolder(path)),
        new("Code editor", "Browse and edit a folder's files", 800, 0,
            vm => new CodeEditorViewModel(vm),
            OpenIn: "Code editor", Opens: Core.Documents.FileInspector.IsText),
        new("Video generator", "Plan and make a video shot by shot", 770, 633,
            vm => new VideoGeneratorViewModel(vm._previews, vm, vm._paths.VideosDir, null)),
    ];

    private const string FileInspectorName = "File inspector", FileChangesName = "File changes";

    /// <summary>A card's Inspect: the file in the File inspector panel.</summary>
    internal void Inspect(string path) => OpenIn(SidePanelOptions.First(o => o.Name == FileInspectorName), path);

    /// <summary>
    /// The panels a file's right-click menu opens it in: each that opens its kind, but the one open
    /// with it in it already.
    /// </summary>
    internal IEnumerable<SidePanelOption> PanelsFor(string path) => SidePanelOptions.Where(o => o.OpenIn is not null && o.Opens?.Invoke(path) == true
        && !(ReferenceEquals(o, SelectedSidePanel) && SidePanel is IFilePanel { Path: { } shown } && string.Equals(shown, Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase)));

    /// <summary>The file in that panel (a card's Inspect, Open in on its right-click menu), which opens in place of the panel open (if another).</summary>
    internal void OpenIn(SidePanelOption panel, string path)
    {
        if (!ReferenceEquals(SelectedSidePanel, panel))
            SelectedSidePanel = panel;
        (SidePanel as IFilePanel)?.Show(path);
    }

    /// <summary>The × on the open panel: None in the dropdown.</summary>
    internal void CloseSidePanel() => SelectedSidePanel = SidePanelOptions[0];

    [ObservableProperty]
    private SidePanelOption? _selectedSidePanel;

    /// <summary>The open panel, on the left of the chat; null with none.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSidePanel))]
    private ISidePanel? _sidePanel;

    public bool HasSidePanel => SidePanel is not null;

    partial void OnSelectedSidePanelChanged(SidePanelOption? value)
    {
        var old = SidePanel;
        // What it put in the box goes with it: about a video no longer open, it would be sent bare.
        foreach (var picked in Attachments.Where(a => a.Picked).ToList())
            RemoveAttachment(picked);
        // One panel for another in one change, so the window goes from the one to the other
        // without closing between.
        SidePanel = value?.Open is { } open && !_closed ? open(this) : null;
        old?.Dispose();
    }

    /// <summary>
    /// What is picked in the side panel, into the box (one picked there before taken out): the
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
            // After the other picks, before the user's own files: what comes from the panel goes first (the user, 2026-10-26).
            Attachments.Insert(Attachments.Count(a => a.Picked), new AttachmentItem(path, RemoveAttachment, caption));
        }
    }

    /// <summary>The paths picked in the side panel that are in the box now.</summary>
    internal IReadOnlyList<string> Picked => Attachments.Where(a => a.Picked).Select(a => a.Path).ToList();

    /// <summary>
    /// A message from the side panel (Make the video): into this chat as if typed in the box, with
    /// what the panel says about it. With <paramref name="withPicks"/> (Generate on a piece), about
    /// what is picked in the box, which goes with it as when the user sends: "Make it." sent bare was
    /// about the whole video, and the model was asked to make all of it.
    /// </summary>
    internal void SendFromSidePanel(string text, bool withPicks = false)
    {
        if (_closed)
            return;
        if (!withPicks)
        {
            _ = SendAsync(WithSidePanel(new UserInput(text), [], fromPanel: true));
            return;
        }
        var picked = Attachments.Where(a => a.Picked).ToList();
        var input = WithSidePanel(new UserInput(text, picked.Where(a => File.Exists(a.Path) || Directory.Exists(a.Path)).Select(a => a.Path).ToList()), Picked, fromPanel: true);
        foreach (var pick in picked)
            RemoveAttachment(pick);
        _ = SendAsync(input);
    }

    /// <summary>
    /// A message as the model gets it while a panel is open: with what the panel says about it and what
    /// was picked in it, which then goes with it (none for a message of the panel's own: what is picked
    /// stays in the box for the next), and the cards of those as the box showed them. A message the
    /// user typed with nothing picked goes as it is: no card, no word of the panel (the user,
    /// 2026-10-28: a card of what they never picked, with no × to take it off, was context they
    /// didn't choose). The panel's own messages (Make the video) always say what the panel shows.
    /// </summary>
    private UserInput WithSidePanel(UserInput input, IReadOnlyList<string> picked, bool fromPanel = false)
    {
        if (SidePanel is not { } sidePanel || picked.Count == 0 && !fromPanel)
            return input;
        var cards = Attachments.Where(a => a.Picked && picked.Contains(a.Path, StringComparer.OrdinalIgnoreCase))
            .Select(a => new SidePanelCard(a.Title, Path: a.Path)).ToList();
        var name = SelectedSidePanel?.Name ?? "Side panel";
        var about = sidePanel.About(picked) ?? (name, $"[The {name} panel is open beside the chat, with nothing in it yet.]");
        if (picked.Count > 0)
            sidePanel.Sent();
        return input with { Via = about.Via, Context = about.Context, Cards = cards };
    }

    /// <summary>
    /// A file about to be written over (a shot made again): the chat's players of it, and the
    /// side panel's, stop; their playbacks, still holding it, are handed back to be ended.
    /// </summary>
    internal List<IDisposable> LetGo(string path)
    {
        var playing = new List<IDisposable>();
        foreach (var preview in Items.SelectMany(i => i.Previews).OfType<PlayerPreview>()
                     .Where(p => string.Equals(Path.GetFullPath(p.Path), path, StringComparison.OrdinalIgnoreCase)))
            if (preview.LetGo() is { } playback)
                playing.Add(playback);
        if (SidePanel is VideoGeneratorViewModel video && video.LetGo(path) is { } shown)
            playing.Add(shown);
        return playing;
    }

    // ---- the files the agent changed in this chat (the File changes panel) -------------------------

    /// <summary>The files the agent changed in the chat on screen, each with what it held before.</summary>
    internal FileChanges Changes { get; private set; } = null!;

    /// <summary>How the side panels show things (File changes' Wrap and Watch), saved as the user leaves them.</summary>
    internal PanelSettings Panels => _settings.Panels;

    internal void SavePanels() => SaveSettings();

    /// <summary>On the window's thread: the list changed, or another chat's took its place.</summary>
    internal event Action? ChangesUpdated;

    /// <param name="editorFolder">The folder the chat had open in the Code editor panel (see <see cref="EditorFolder"/>).</param>
    private void UseChanges(FileChanges changes, string? editorFolder = null)
    {
        EditorFolder = editorFolder;
        if (Changes is not null)
            Changes.Changed -= OnChanges;
        Changes = changes;
        changes.Changed += OnChanges;
        ChangesUpdated?.Invoke();
    }

    // A run stopped by leaving the chat still writes into the chat's own list, never into this one.
    // File changes never opens by itself (the user, 2026-10-31: it widened the window mid-task): it
    // shows the changes once picked in the Side panel dropdown.
    private void OnChanges(FileChanges changes) => Dispatcher.UIThread.Post(() =>
    {
        if (ReferenceEquals(changes, Changes))
            ChangesUpdated?.Invoke();
    });

    // ---- the Code editor panel's folder and unsaved files ---------------------------------------

    /// <summary>
    /// The folder the user opened in the Code editor panel in the chat on screen, saved with the chat;
    /// null while none was, and the panel shows the project the agent worked in.
    /// </summary>
    internal string? EditorFolder { get; set; }

    /// <summary>Files edited in the Code editor panel and not saved yet, by path: kept while the window is open, so closing the panel loses nothing.</summary>
    internal Dictionary<string, CodeDraft> CodeDrafts { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Where a chat's list is kept: Data/chats/changes/id.json.</summary>
    private string ChangesFile(string chatId) => Path.Combine(_chats.Folder, "changes", Path.GetFileName(chatId) + ".json");

    /// <summary>Files dropped on the side panel: the panel takes them, or they go with the next message.</summary>
    internal bool DropOnSidePanel(IReadOnlyList<string> paths) => SidePanel?.Drop(paths) == true;

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
