using Avalonia.Media.Imaging;
using Avalonia.Threading;
using BlackBox.Core.Documents;
using BlackBox.Core.Platform;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BlackBox.App.ViewModels;

/// <summary>
/// The Inspect file panel, a tool of the chat window's toolkit on the left of the chat: one file
/// shown whole, opened by the Inspect button of its card in the chat or dropped on the panel. BlackBox
/// draws Markdown (as a reply), tables, text and pictures itself, plays videos frame by frame
/// (<see cref="InspectVideoViewModel"/>) and the chat's players play the rest of the media, and PDF
/// pages are drawn by PDFium (<see cref="PdfPages"/>); Office files and web pages
/// (and a PDF PDFium can't open) are drawn by the previewer Windows has for them, as
/// in Explorer's preview pane, else shown as their text (<see cref="InspectFile"/>). With the
/// panel open a message is about the file shown; text selected in the text BlackBox draws goes into
/// the chat's box as where it is in the file ("Lines 12-20 · notes.md", "Cells B4:C4 · data.csv"),
/// and the message carries it for the model, as File changes does with lines (the user's request,
/// 2026-10-25). An area dragged over a PDF's page, a picture or a video's frame goes in as a picture
/// of it with where it is ("Area on page 2 · report.pdf"); with Area on, over what the panel shows
/// frozen: a text, or Windows' previewer, which draws in a window of its own whose selection
/// BlackBox can't read. Pictures, frames and pages zoom and pan.
/// </summary>
public sealed partial class InspectFileViewModel : ObservableObject, IToolkit
{
    private readonly PreviewServices _previews;
    private readonly MainWindowViewModel? _chat;
    private bool _picking;

    /// <summary>How wide the chat's players decode what the panel plays with them (animations, sound, 3D).</summary>
    private const int PictureWidth = 1600;

    /// <summary>How wide a picture is decoded at most: as it is, so zoomed in its pixels show, up to this.</summary>
    private const int MaxPictureWidth = 8192;

    /// <summary>Characters of a selection the model is given at most, with the count of the rest.</summary>
    private const int MaxPickedChars = 20_000;

    /// <summary>Where a picture of a marked area is saved for the model: the folder of pasted pictures, which go with their chat.</summary>
    private readonly string? _areasDir;

    /// <param name="chat">The chat the panel is a tool of, where what is selected in it goes; null with none.</param>
    /// <param name="areasDir">Where pictures of marked areas are saved; with a chat.</param>
    public InspectFileViewModel(PreviewServices previews, MainWindowViewModel? chat = null, string? areasDir = null)
    {
        _previews = previews;
        _chat = chat;
        _areasDir = areasDir;
        if (chat is not null)
            chat.Attachments.CollectionChanged += OnBoxChanged;
    }

    /// <summary>In the panel, with a chat to ask in: what is selected or marked goes into its box. Not in the full-screen viewer.</summary>
    public bool CanPick => _chat is not null;

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
    private string? _message = "Press Inspect on a file in the chat, or drop one here.";

    public bool HasMessage => Message is not null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Whole), nameof(WholeTip), nameof(ShowsMarkdown), nameof(CanFreeze))]
    private string? _markdown;

    /// <summary>Text in a fixed-width font: code, a log, a document's text where nothing on this PC previews it.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Whole), nameof(WholeTip), nameof(ShowsText), nameof(CanFreeze))]
    private string? _text;

    /// <summary>What <see cref="Markdown"/> or <see cref="Text"/> is: the file as written (its lines are the file's), a CSV drawn as a table, or a document's text only.</summary>
    private enum Shown { File, Table, TextOnly }

    private Shown _shown;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Whole), nameof(WholeTip), nameof(CanZoom))]
    private Bitmap? _picture;

    /// <summary>The picture file's own size, which an area on it is told in.</summary>
    private (int Width, int Height) _pictureSize;

    /// <summary>A video, in a player of the panel's own: frame by frame, zoomed, an area marked on a frame.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Whole), nameof(WholeTip), nameof(CanZoom))]
    private InspectVideoViewModel? _video;

    /// <summary>An animation, a sound or a 3D model, in the chat's player.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Whole))]
    private FilePreview? _media;

    /// <summary>A PDF's pages, drawn by PDFium as they come on screen; null for any other file.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Whole), nameof(WholeTip), nameof(CanZoom))]
    private IReadOnlyList<PdfPageItem>? _pages;

    /// <summary>The PDF the pages are drawn from, open while it is shown.</summary>
    private PdfPages? _pdf;

    /// <summary>The file for Windows' previewer to draw (see <see cref="Views.ShellPreviewHost"/>).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsShell), nameof(WholeTip), nameof(CanFreeze))]
    private string? _shellPath;

    /// <summary>What a load under way is: one started after it wins.</summary>
    private int _loading;

    /// <summary>Shows <paramref name="path"/> in the panel, in place of the file shown.</summary>
    public void Show(string path)
    {
        path = System.IO.Path.GetFullPath(path);
        var load = ++_loading;
        Unpick();
        AreaMode = false;
        Clear();
        Zoom = 1;
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
                var kind = InspectFile.KindOf(path);
                switch (kind)
                {
                    case InspectKind.Markdown:
                    {
                        var (text, cut) = InspectFile.ReadText(path);
                        var lines = Lines(text);
                        Post(load, () => (_shown, Markdown, Details, Message) = (Shown.File, text, $"Markdown, {(cut ? "first " : "")}{lines:N0} line{(lines == 1 ? "" : "s")}, {size}", null));
                        break;
                    }
                    case InspectKind.Text:
                    {
                        var (text, cut) = InspectFile.ReadText(path);
                        var lines = Lines(text);
                        Post(load, () => (_shown, Text, Details, Message) = (Shown.File, text, $"{(cut ? "first " : "")}{lines:N0} line{(lines == 1 ? "" : "s")}, {size}", null));
                        break;
                    }
                    case InspectKind.Table:
                    {
                        var (table, rows, columns) = InspectFile.Table(path);
                        var shown = rows > InspectFile.MaxRows || columns > InspectFile.MaxColumns
                            ? $", the first {Math.Min(rows, InspectFile.MaxRows):N0} rows and {Math.Min(columns, InspectFile.MaxColumns)} columns" : "";
                        Post(load, () => (_shown, Markdown, Details, Message) = (Shown.Table, table, $"{rows:N0} rows, {columns} columns{shown}, {size}", table.Length == 0 ? "The table is empty." : null));
                        break;
                    }
                    case InspectKind.Picture:
                    {
                        using var probe = Core.Media.PictureFile.Codec(path);
                        var own = probe is null ? (0, 0) : (probe.Info.Width, probe.Info.Height);
                        using var stream = File.OpenRead(path);
                        // As it is, so its pixels show zoomed in; a huge one at most 8192 wide.
                        var bitmap = own.Item1 is > 0 and <= MaxPictureWidth ? new Bitmap(stream) : Bitmap.DecodeToWidth(stream, MaxPictureWidth, BitmapInterpolationMode.HighQuality);
                        if (own.Item1 == 0)
                            own = (bitmap.PixelSize.Width, bitmap.PixelSize.Height);
                        var details = probe is null ? size : $"{own.Item1}x{own.Item2}, {size}";
                        Post(load, () => (_pictureSize, Picture, Details, Message) = (own, bitmap, details, null), bitmap.Dispose);
                        break;
                    }
                    case InspectKind.Media when Core.Media.MediaPreview.KindOf(path) == "video" && new InspectVideoViewModel(path, _previews) is var video:
                        if (video.Load())
                            Post(load, () => (Video, Details, Message) = (video, $"{video.Details}, {size}", null), video.Dispose);
                        else
                        {
                            video.Dispose();
                            Post(load, () => (Details, Message) = (size, "Nothing on this PC can play this video."));
                        }
                        break;
                    case InspectKind.Media:
                        Post(load, () =>
                        {
                            Media = FilePreview.For(path, _previews, PictureWidth);
                            Message = null;
                        });
                        break;
                    case InspectKind.Document when ShowPdf(load, path, size):
                        break;
                    case InspectKind.Document when OperatingSystem.IsWindows() && Views.ShellPreviewHost.CanShow(path):
                        Post(load, () => (ShellPath, Details, Message) = (path, size, null));
                        break;
                    case InspectKind.Document:
                        ShowAsText(load, path, size);
                        break;
                    default:
                        Post(load, () => (Details, Message) = (size, "There is no preview for this kind of file. Its card in the chat has Show in folder."));
                        break;
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Post(load, () => Message = $"Can't be shown: {ex.Message}");
            }
        });
    }

    /// <summary>
    /// A PDF's pages, drawn by BlackBox with PDFium; false for any other file, and for a PDF PDFium
    /// can't open (damaged, with a password, or pdfium.dll missing), which Windows' previewer or its
    /// text then shows.
    /// </summary>
    private bool ShowPdf(int load, string path, string size)
    {
        if (!System.IO.Path.GetExtension(path).Equals(".pdf", StringComparison.OrdinalIgnoreCase))
            return false;
        PdfPages pdf;
        try { pdf = PdfPages.Open(path); }
        catch (Exception ex) when (ex is InvalidDataException or DllNotFoundException or EntryPointNotFoundException or BadImageFormatException or IOException)
        {
            return false;
        }
        var pages = Enumerable.Range(0, pdf.Sizes.Count).Select(i => new PdfPageItem(pdf, i)).ToList();
        var details = $"PDF, {pages.Count:N0} page{(pages.Count == 1 ? "" : "s")}, {size}";
        Post(load, () =>
        {
            _pdf = pdf;
            (Pages, Details, Message) = (pages, details, pages.Count == 0 ? "The PDF has no pages." : null);
        }, pdf.Dispose);
        return true;
    }

    /// <summary>Windows' previewer couldn't show the file: its text, as without one.</summary>
    internal void ShellFailed(string why)
    {
        if (ShellPath is not { } path)
            return;
        var load = ++_loading;
        AreaMode = false;
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
        if (InspectFile.DocumentAsText(path) is { } text)
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
        if (!InspectFile.LooksLikeText(path))
        {
            Post(load, () => (Details, Message) = (size, "Nothing on this PC previews this kind of file. Its card in the chat has Show in folder."));
            return;
        }
        var (source, _) = InspectFile.ReadText(path);
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
        if (Pages is { } pages)
            foreach (var page in pages)
                page.Unload();
        Pages = null;
        _pdf?.Dispose();
        _pdf = null;
        Picture?.Dispose();
        Picture = null;
        Video?.Dispose();
        Video = null;
        Media?.Dispose();
        Media = null;
    }

    /// <summary>
    /// Over the chat's box while nothing is selected: the file, as a selection is shown, so the user
    /// sees what a message is about ("notes.md", "whole document").
    /// </summary>
    public (string Title, string Details)? Whole => Path is null ? null : (FileName, Picture is not null || Media is not null || Video is not null ? "whole file" : "whole document");

    public string WholeTip => Markdown is not null || Text is not null
        ? "What you type is about this file. Select text in it, or drag over it with Area on, to ask about that."
        : ShellPath is not null ? "What you type is about this file. Drag over it with Area on to ask about a part."
        : Pages is not null || Picture is not null || Video is not null ? "What you type is about this file. Drag over it to ask about an area."
        : "What you type is about this file.";

    // ---- zoom -----------------------------------------------------------------------------------

    /// <summary>How much a picture, a frame or the PDF's pages are zoomed in: 1 fits them in the panel.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ZoomText))]
    private double _zoom = 1;

    /// <summary>What zooms: pictures, frames, a PDF's pages, and what is frozen for marking.</summary>
    public bool CanZoom => Pages is not null || Picture is not null || Video is not null || Frozen is not null;

    /// <summary>Pages are drawn sharp up to 3 times the panel's width; pictures and frames go to their pixels.</summary>
    public double MaxZoom => Pages is not null && Frozen is null ? 3 : Views.ZoomBox.MaxZoom;

    public string ZoomText => Zoom <= 1.001 ? "Fit" : $"{Zoom * 100:0}%";

    partial void OnZoomChanged(double value)
    {
        if (Pages is { } pages)
            foreach (var page in pages)
                page.Zoom = Frozen is null ? value : 1;
    }

    [RelayCommand]
    private void ZoomIn() => Zoom = Math.Min(MaxZoom, Zoom * Views.ZoomBox.Step);

    [RelayCommand]
    private void ZoomOut() => Zoom = Math.Max(1, Zoom / Views.ZoomBox.Step);

    [RelayCommand]
    private void ZoomFit() => Zoom = 1;

    // ---- Area: what is shown, frozen to be marked on ----------------------------------------------

    /// <summary>
    /// Area is on: what the panel shows is a still picture of it (<see cref="Frozen"/>) to drag an area
    /// over. For a text, whose drags select text, and Windows' previewer, which takes the mouse itself.
    /// </summary>
    [ObservableProperty]
    private bool _areaMode;

    /// <summary>What shows a text or Windows' previewer: Area can be turned on (in the panel).</summary>
    public bool CanFreeze => CanPick && (ShellPath is not null || Markdown is not null || Text is not null);

    /// <summary>The panel's view of the file, frozen while Area is on; null otherwise.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsShell), nameof(ShowsMarkdown), nameof(ShowsText), nameof(CanZoom), nameof(MaxZoom))]
    private Bitmap? _frozen;

    private RawImage? _frozenPixels;

    public bool ShowsShell => ShellPath is not null && Frozen is null;
    public bool ShowsMarkdown => Markdown is not null && Frozen is null;
    public bool ShowsText => Text is not null && Frozen is null;

    /// <summary>The view frozen (by the panel, which can read it) to mark an area on.</summary>
    internal void Freeze(RawImage pixels)
    {
        Unfreeze();
        _frozenPixels = pixels;
        Frozen = PdfPageItem.ToBitmap(pixels.Bgra, pixels.Width, pixels.Height);
        Zoom = 1;
    }

    partial void OnAreaModeChanged(bool value)
    {
        if (!value)
        {
            if (_area is { Frozen: true })
                Unpick();
            Unfreeze();
        }
    }

    private void Unfreeze()
    {
        if (Frozen is null)
            return;
        var frozen = Frozen;
        Frozen = null;
        frozen.Dispose();
        _frozenPixels = null;
        Zoom = 1;
    }

    // ---- what is selected, into the chat's box ---------------------------------------------------

    /// <summary>The text selected in the panel that is in the box; null with none.</summary>
    private DocumentPick? _pick;

    /// <summary>
    /// The area marked in the panel that is in the box: the picture of it saved for the model, where
    /// it is as the card says it ("area on page 2") and as the model is told it; null with none.
    /// </summary>
    private sealed record PickedArea(string Picture, string Where, string Told, bool Frozen);

    private PickedArea? _area;

    /// <summary>The area marked on the panel's picture, frame, page or frozen view; null with none.</summary>
    [ObservableProperty]
    private DocumentArea? _mark;

    partial void OnMarkChanged(DocumentArea? value)
    {
        if (Pages is { } pages)
            foreach (var page in pages)
                page.Mark = value is { Page: { } p } && p == page.Index ? value : null;
    }

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
        DropArea();
        _pick = pick;
        _picking = true;
        try { _chat?.Pick([(path, $"{Capital(Where(pick))} · {FileName}")]); }
        finally { _picking = false; }
    }

    /// <summary>"lines 12-20", "cells B4:C4"; "selected text" in a document's text, whose lines aren't the file's.</summary>
    private string Where(DocumentPick pick) => _shown == Shown.TextOnly ? "selected text" : pick.Where;

    private static string Capital(string text) => text.Length > 0 ? char.ToUpperInvariant(text[0]) + text[1..] : text;

    /// <summary>
    /// An area marked by a drag, on page <see cref="DocumentArea.Page"/> of the PDF, the picture, the
    /// video's frame showing or the frozen view: a picture of it is cut (from the page drawn again
    /// sharp, the picture file, the frame at the video's own size) and saved, and goes into the box as
    /// "Area on page 2 · report.pdf". Null (a click) takes out what was marked.
    /// </summary>
    public void PickArea(DocumentArea? area)
    {
        if (area is null || Path is not { } path || _areasDir is null)
        {
            Unpick();
            return;
        }
        Mark = area;
        var load = _loading;
        var name = FileName;
        Func<(RawImage? Picture, string Where, string Told)> cut;
        if (Frozen is not null && _frozenPixels is { } frozen)
        {
            var told = ShellPath is not null
                ? "as Windows' previewer showed it in the panel, scrolled where they had it"
                : "as the panel drew it, scrolled where they had it";
            cut = () => (area.Crop(frozen), area.Where, $"They marked an area of it {told}: {area.Place(frozen.Width, frozen.Height, "pixels of the panel's view")}");
        }
        else if (Pages is not null && _pdf is { } pdf && area.Page is { } page && page < pdf.Sizes.Count)
        {
            var (w, h) = pdf.Sizes[page];
            cut = () => (pdf.RenderArea(page, area, 1600, PdfPageItem.RenderWidth) is { } a ? new RawImage(a.Width, a.Height, a.Bgra) : null,
                area.Where, $"They marked an {area.Where} of it: {area.Place(w, h, "points")}");
        }
        else if (Picture is not null)
        {
            var (w, h) = _pictureSize;
            cut = () => (DocumentArea.Decode(path) is { } picture ? area.Crop(picture) : null, area.Where, $"They marked an area of the picture: {area.Place(w, h, "pixels")}");
        }
        else if (Video is { } video)
        {
            video.Pause();
            var frame = video.FrameIndex;
            var where = $"area of frame {frame + 1}";
            var at = video.TimeOf(frame).TotalSeconds;
            cut = () => (video.FullFrame(frame) is { } full ? area.Crop(full) : null, where,
                $"They marked an area of frame {frame + 1:N0} of its {video.FrameCount:N0} (at {at:0.000} s; frames counted from 1): " +
                area.Place(video.Size.Width, video.Size.Height, "pixels"));
        }
        else
            return;
        var dir = _areasDir;
        Task.Run(() =>
        {
            try
            {
                var (picture, where, told) = cut();
                if (picture is null)
                    return;
                Directory.CreateDirectory(dir);
                var stamp = DateTime.Now.ToString("yyyy-MM-dd HH-mm-ss", System.Globalization.CultureInfo.InvariantCulture);
                var file = System.IO.Path.Combine(dir, $"Area {stamp}.png");
                for (var n = 2; File.Exists(file); n++)
                    file = System.IO.Path.Combine(dir, $"Area {stamp} ({n}).png");
                DocumentArea.SavePng(picture, file);
                Dispatcher.UIThread.Post(() =>
                {
                    // Another area marked meanwhile, or another file shown, or the mark taken off: it isn't wanted.
                    if (load != _loading || _disposed || !Equals(Mark, area))
                    {
                        TryDelete(file);
                        return;
                    }
                    _pick = null;
                    DropArea();
                    _area = new PickedArea(file, where, told, Frozen is not null);
                    _picking = true;
                    try { _chat?.Pick([(file, $"{Capital(where)} · {name}")]); }
                    finally { _picking = false; }
                });
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Dispatcher.UIThread.Post(() => _chat?.AddNotice($"The marked area could not be saved: {ex.Message}", isError: true, ex));
            }
        });
    }

    /// <summary>The area picked before goes: its picture, which wasn't sent, with it.</summary>
    private void DropArea()
    {
        if (_area is { } area)
            TryDelete(area.Picture);
        _area = null;
    }

    private static void TryDelete(string file)
    {
        try { File.Delete(file); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>A click in the panel that selects nothing, or Esc: the selection leaves the box. False when there was none.</summary>
    public bool Unpick()
    {
        Mark = null;
        if (_pick is null && _area is null)
            return false;
        _pick = null;
        DropArea();
        _picking = true;
        try { _chat?.Pick([]); }
        finally { _picking = false; }
        Unpicked?.Invoke();
        return true;
    }

    /// <summary>The selection taken off the box (its ×): it is no longer selected here either.</summary>
    private void OnBoxChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        if (_picking || _pick is null && _area is null || e.OldItems is null || !e.OldItems.OfType<AttachmentItem>().Any(a => a.Picked))
            return;
        if (_chat?.Attachments.Any(a => a.Picked) == true)
            return;
        _pick = null;
        DropArea();
        Mark = null;
        Unpicked?.Invoke();
    }

    /// <summary>
    /// What a message sent while the panel is open carries: over its bubble, the file and where the
    /// selection is in it; for the model, the file and the selected text with where it is, or the
    /// marked area's picture (it goes with the message as the file picked). Neutral about what is to
    /// be done with it: the user may ask about it, have it explained or changed.
    /// </summary>
    public (string Via, string Context)? About(IReadOnlyList<string> files)
    {
        if (Path is not { } path)
            return null;
        var opening = $"[The Inspect file panel is open beside the chat, showing {path}. ";
        if (_area is { } area && files.Contains(area.Picture, StringComparer.OrdinalIgnoreCase))
            return ($"Inspect file · {FileName} · {area.Where}",
                $"{opening}{area.Told}. The picture {area.Picture} attached below is that area, cut from it, which is what they refer to.]");
        if (_pick is not { } pick || !files.Contains(path, StringComparer.OrdinalIgnoreCase))
            return ("Inspect file · " + FileName, opening + "What they say is about this file unless they say otherwise.]");
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
        return ($"Inspect file · {FileName} · {where}", context.ToString());
    }

    /// <summary>The message went with the selection: it is no longer selected (an area's picture went with it, and stays).</summary>
    public void Sent()
    {
        if (_pick is null && _area is null)
            return;
        _pick = null;
        _area = null;
        Mark = null;
        AreaMode = false;
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
        // An area marked and not sent: its picture goes.
        DropArea();
        _disposed = true;
        _loading++;
        Unfreeze();
        Clear();
    }
}
