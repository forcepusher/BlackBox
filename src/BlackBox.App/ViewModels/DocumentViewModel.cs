using Avalonia.Media.Imaging;
using Avalonia.Threading;
using BlackBox.Core.Documents;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BlackBox.App.ViewModels;

/// <summary>
/// The Document preview panel, a tool of the chat window's toolkit on the left of the chat: one file
/// shown whole, opened by the Preview button of its card in the chat or dropped on the panel. BlackBox
/// draws Markdown (as a reply), tables, text and pictures itself, and the chat's players play the rest
/// of the media; PDF, Office files and web pages are drawn by the previewer Windows has for them, as
/// in Explorer's preview pane, else shown as their text (<see cref="DocumentPreview"/>). With the
/// panel open a message is about the file shown; text selected in the text BlackBox draws goes into
/// the chat's box as where it is in the file ("Lines 12-20 · notes.md", "Cells B4:C4 · data.csv"),
/// and the message carries it for the model, as Code changes does with lines (the user's request,
/// 2026-10-25). Windows' previewers draw in a window of their own, whose selection BlackBox can't
/// read: there the message is about the whole file.
/// </summary>
public sealed partial class DocumentViewModel : ObservableObject, IToolkit
{
    private readonly PreviewServices _previews;
    private readonly MainWindowViewModel? _chat;
    private bool _picking;

    /// <summary>How wide pictures are decoded for the panel.</summary>
    private const int PictureWidth = 1600;

    /// <summary>Characters of a selection the model is given at most, with the count of the rest.</summary>
    private const int MaxPickedChars = 20_000;

    /// <param name="chat">The chat the panel is a tool of, where what is selected in it goes; null with none.</param>
    public DocumentViewModel(PreviewServices previews, MainWindowViewModel? chat = null)
    {
        _previews = previews;
        _chat = chat;
        if (chat is not null)
            chat.Attachments.CollectionChanged += OnBoxChanged;
    }

    /// <summary>The file shown; null before one is.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FileName), nameof(HasFile), nameof(Whole))]
    private string? _path;

    public string FileName => System.IO.Path.GetFileName(Path ?? "");
    public bool HasFile => Path is not null;

    /// <summary>"Markdown, 71 lines, 2 KB", once read.</summary>
    [ObservableProperty]
    private string _details = "";

    /// <summary>Said in the middle of the panel: how to open a file in it, that it is loading, or why it can't be shown.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMessage))]
    private string? _message = "Press Preview on a file in the chat, or drop one here.";

    public bool HasMessage => Message is not null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Whole))]
    private string? _markdown;

    /// <summary>Text in a fixed-width font: code, a log, a document's text where nothing on this PC previews it.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Whole))]
    private string? _text;

    /// <summary>What <see cref="Markdown"/> or <see cref="Text"/> is: the file as written (its lines are the file's), a CSV drawn as a table, or a document's text only.</summary>
    private enum Shown { File, Table, TextOnly }

    private Shown _shown;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Whole))]
    private Bitmap? _picture;

    /// <summary>An animation, a video, a sound or a 3D model, in the chat's player.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Whole))]
    private FilePreview? _media;

    /// <summary>The file for Windows' previewer to draw (see <see cref="Views.ShellPreviewHost"/>).</summary>
    [ObservableProperty]
    private string? _shellPath;

    /// <summary>What a load under way is: one started after it wins.</summary>
    private int _loading;

    /// <summary>Shows <paramref name="path"/> in the panel, in place of the file shown.</summary>
    public void Show(string path)
    {
        path = System.IO.Path.GetFullPath(path);
        var load = ++_loading;
        Unpick();
        Clear();
        Path = path;
        Details = "";
        Message = "Loading…";
        Task.Run(() =>
        {
            try
            {
                if (!File.Exists(path))
                {
                    Post(load, () => Message = "The file is no longer there.");
                    return;
                }
                var size = FilePreview.Size(path);
                var kind = DocumentPreview.KindOf(path);
                switch (kind)
                {
                    case DocumentKind.Markdown:
                    {
                        var (text, cut) = DocumentPreview.ReadText(path);
                        var lines = Lines(text);
                        Post(load, () => (_shown, Markdown, Details, Message) = (Shown.File, text, $"Markdown, {(cut ? "first " : "")}{lines:N0} line{(lines == 1 ? "" : "s")}, {size}", null));
                        break;
                    }
                    case DocumentKind.Text:
                    {
                        var (text, cut) = DocumentPreview.ReadText(path);
                        var lines = Lines(text);
                        Post(load, () => (_shown, Text, Details, Message) = (Shown.File, text, $"{(cut ? "first " : "")}{lines:N0} line{(lines == 1 ? "" : "s")}, {size}", null));
                        break;
                    }
                    case DocumentKind.Table:
                    {
                        var (table, rows, columns) = DocumentPreview.Table(path);
                        var shown = rows > DocumentPreview.MaxRows || columns > DocumentPreview.MaxColumns
                            ? $", the first {Math.Min(rows, DocumentPreview.MaxRows):N0} rows and {Math.Min(columns, DocumentPreview.MaxColumns)} columns" : "";
                        Post(load, () => (_shown, Markdown, Details, Message) = (Shown.Table, table, $"{rows:N0} rows, {columns} columns{shown}, {size}", table.Length == 0 ? "The table is empty." : null));
                        break;
                    }
                    case DocumentKind.Picture:
                    {
                        using var stream = File.OpenRead(path);
                        var bitmap = Bitmap.DecodeToWidth(stream, PictureWidth, BitmapInterpolationMode.HighQuality);
                        using var probe = Core.Media.PictureFile.Codec(path);
                        var details = probe is null ? size : $"{probe.Info.Width}x{probe.Info.Height}, {size}";
                        Post(load, () => (Picture, Details, Message) = (bitmap, details, null), bitmap.Dispose);
                        break;
                    }
                    case DocumentKind.Media:
                        Post(load, () =>
                        {
                            Media = FilePreview.For(path, _previews, PictureWidth);
                            Message = null;
                        });
                        break;
                    case DocumentKind.Document when OperatingSystem.IsWindows() && Views.ShellPreviewHost.CanShow(path):
                        Post(load, () => (ShellPath, Details, Message) = (path, size, null));
                        break;
                    case DocumentKind.Document:
                        ShowAsText(load, path, size);
                        break;
                    default:
                        Post(load, () => (Details, Message) = (size, "There is no preview for this kind of file. Show in folder finds it."));
                        break;
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Post(load, () => Message = $"Can't be shown: {ex.Message}");
            }
        });
    }

    /// <summary>Windows' previewer couldn't show the file: its text, as without one.</summary>
    internal void ShellFailed(string why)
    {
        if (ShellPath is not { } path)
            return;
        var load = ++_loading;
        ShellPath = null;
        Message = "Loading…";
        Task.Run(() =>
        {
            try { ShowAsText(load, path, FilePreview.Size(path), $" (the previewer for it failed: {why})"); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { Post(load, () => Message = $"Can't be shown: {ex.Message}"); }
        });
    }

    /// <summary>
    /// A document with no previewer on this PC: its text, as read_file gives it the agent (Word's and
    /// PowerPoint's with their headings and tables, as Markdown); a web page or an RTF shows its source.
    /// </summary>
    private void ShowAsText(int load, string path, string size, string why = "")
    {
        var details = $"{size}, its text only{why}";
        if (DocumentPreview.DocumentAsText(path) is { } text)
        {
            var markdown = System.IO.Path.GetExtension(path).ToLowerInvariant() is ".docx" or ".docm" or ".dotx" or ".pptx" or ".pptm" or ".potx";
            Post(load, () =>
            {
                _shown = Shown.TextOnly;
                if (markdown) Markdown = text;
                else Text = text;
                (Details, Message) = (details, null);
            });
            return;
        }
        if (!DocumentPreview.LooksLikeText(path))
        {
            Post(load, () => (Details, Message) = (size, "Nothing on this PC previews this kind of file. Show in folder finds it."));
            return;
        }
        var (source, _) = DocumentPreview.ReadText(path);
        Post(load, () => (_shown, Text, Details, Message) = (Shown.File, source, details, null));
    }

    private static int Lines(string text) => text.Length == 0 ? 0 : text.TrimEnd('\n', '\r').Count(c => c == '\n') + 1;

    /// <summary>Runs <paramref name="show"/> on the UI thread if no other file was asked for since; else <paramref name="drop"/> lets go of what was loaded.</summary>
    private void Post(int load, Action show, Action? drop = null) => Dispatcher.UIThread.Post(() =>
    {
        if (load == _loading && !_disposed) show();
        else drop?.Invoke();
    });

    /// <summary>Lets go of what shows the file before.</summary>
    private void Clear()
    {
        Markdown = Text = ShellPath = null;
        Picture?.Dispose();
        Picture = null;
        Media?.Dispose();
        Media = null;
    }

    [RelayCommand]
    private void ShowInFolder()
    {
        if (Path is { } path)
            FilePreview.ShowInFolder(path);
    }

    /// <summary>
    /// Over the chat's box while nothing is selected: the file, as a selection is shown, so the user
    /// sees what a message is about ("notes.md", "whole document").
    /// </summary>
    public (string Title, string Details)? Whole => Path is null ? null : (FileName, Picture is not null || Media is not null ? "whole file" : "whole document");

    public string WholeTip => Markdown is not null || Text is not null
        ? "What you type is about this file. Select text in it to ask about that."
        : "What you type is about this file.";

    // ---- what is selected, into the chat's box ---------------------------------------------------

    /// <summary>The text selected in the panel that is in the box; null with none.</summary>
    private DocumentPick? _pick;

    /// <summary>The selection was taken off the box, sent or let go of: the panel clears it on screen too.</summary>
    public event Action? Unpicked;

    /// <summary>Text selected in <see cref="Text"/>, from character <paramref name="start"/> to <paramref name="end"/>; nothing when they are the same.</summary>
    public void SelectedInText(int start, int end) =>
        Picked(Text is { } text ? DocumentPick.InText(text, start, end) : null);

    /// <summary>Text selected in block <paramref name="block"/> of <see cref="Markdown"/> as drawn (see <see cref="DocumentPick.InMarkdown"/>).</summary>
    public void SelectedInMarkdown(int block, int start, int end) =>
        Picked(Markdown is { } markdown ? DocumentPick.InMarkdown(markdown, block, start, end, _shown == Shown.Table) : null);

    /// <summary>
    /// The selection into the box as where it is in the file ("Lines 12-20 · notes.md": the place
    /// first, as a long name is cut short there); a click that selects nothing takes it out.
    /// </summary>
    private void Picked(DocumentPick? pick)
    {
        if (Path is not { } path || pick is null || pick.Text.Trim().Length == 0)
        {
            Unpick();
            return;
        }
        _pick = pick;
        _picking = true;
        try { _chat?.Pick([(path, $"{Capital(Where(pick))} · {FileName}")]); }
        finally { _picking = false; }
    }

    /// <summary>"lines 12-20", "cells B4:C4"; "selected text" in a document's text, whose lines aren't the file's.</summary>
    private string Where(DocumentPick pick) => _shown == Shown.TextOnly ? "selected text" : pick.Where;

    private static string Capital(string text) => text.Length > 0 ? char.ToUpperInvariant(text[0]) + text[1..] : text;

    /// <summary>A click in the panel that selects nothing, or Esc: the selection leaves the box. False when there was none.</summary>
    public bool Unpick()
    {
        if (_pick is null)
            return false;
        _pick = null;
        _picking = true;
        try { _chat?.Pick([]); }
        finally { _picking = false; }
        Unpicked?.Invoke();
        return true;
    }

    /// <summary>The selection taken off the box (its ×): it is no longer selected here either.</summary>
    private void OnBoxChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        if (_picking || _pick is null || e.OldItems is null || !e.OldItems.OfType<AttachmentItem>().Any(a => a.Picked))
            return;
        if (_chat?.Attachments.Any(a => a.Picked) == true)
            return;
        _pick = null;
        Unpicked?.Invoke();
    }

    /// <summary>
    /// What a message sent while the panel is open carries: over its bubble, the file and where the
    /// selection is in it; for the model, the file and the selected text with where it is. Neutral
    /// about what is to be done with it: the user may ask about it, have it explained or changed.
    /// </summary>
    public (string Via, string Context)? About(IReadOnlyList<string> files)
    {
        if (Path is not { } path)
            return null;
        var opening = $"[The Document preview panel is open beside the chat, showing {path}. ";
        if (_pick is not { } pick || !files.Contains(path, StringComparer.OrdinalIgnoreCase))
            return ("Document preview · " + FileName, opening + "What they say is about this file unless they say otherwise.]");
        var where = Where(pick);
        var context = new System.Text.StringBuilder(opening);
        context.Append(_shown switch
        {
            Shown.Table => $"They selected {where} of it (shown as a table, its first row the header as row 1{(pick.Columns is { Count: > 0 } columns ? $"; {(columns.Count == 1 ? "column" : "columns")} {string.Join(", ", columns)}" : "")})",
            Shown.TextOnly => "They selected this in its text (as read_file gives it, so not lines of the file)",
            _ when Markdown is not null => $"They selected {where} of it (the panel draws its Markdown formatted: the text below is as shown, without the marks)",
            _ => $"They selected {where} of it",
        }).Append(", which is what they refer to:\n");
        var text = pick.Text.TrimEnd('\r', '\n');
        context.Append(text.Length <= MaxPickedChars ? text : text[..MaxPickedChars] + $"\n[... {text.Length - MaxPickedChars:N0} more selected characters]");
        context.Append("\n]");
        return ($"Document preview · {FileName} · {where}", context.ToString());
    }

    /// <summary>The message went with the selection: it is no longer selected.</summary>
    public void Sent()
    {
        if (_pick is null)
            return;
        _pick = null;
        Unpicked?.Invoke();
    }

    /// <summary>A file dropped on the panel is shown in it (the first of several).</summary>
    public bool Drop(IReadOnlyList<string> paths)
    {
        if (paths.FirstOrDefault(File.Exists) is not { } path)
            return false;
        Show(path);
        return true;
    }

    private bool _disposed;

    public void Dispose()
    {
        if (_chat is not null)
            _chat.Attachments.CollectionChanged -= OnBoxChanged;
        _disposed = true;
        _loading++;
        Clear();
    }
}
