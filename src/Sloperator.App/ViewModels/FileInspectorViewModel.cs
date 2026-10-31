using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Sloperator.Core.Documents;
using Sloperator.Core.Media;
using Sloperator.Core.Platform;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using static Sloperator.Core.Strings;

namespace Sloperator.App.ViewModels;

/// <summary>
/// The File inspector panel, one of the chat window's side panels on the left of the chat: one file
/// shown whole, opened by the Inspect button of its card in the chat, picked with the panel's Open
/// file button, or dropped on the panel. Sloperator
/// draws Markdown (as a reply), tables, text and pictures itself, plays videos and animations frame
/// by frame (<see cref="InspectorVideoViewModel"/>, <see cref="InspectorAnimationViewModel"/>), draws a
/// sound's waveform to zoom into and pick a stretch of (<see cref="InspectorSoundViewModel"/>), turns
/// 3D models about (<see cref="InspectorModelViewModel"/>), shows any other file's bytes in hex, and PDF
/// pages are drawn by Windows' PDF renderer (<see cref="PdfPages"/>); Office files and web pages
/// (and a PDF it can't open) are drawn by the previewer Windows has for them, as
/// in Explorer's preview pane, else shown as their text (<see cref="FileInspector"/>). With the
/// panel open a message is about the file shown; text selected in the text Sloperator draws goes into
/// the chat's box as where it is in the file ("Lines 12-20 · notes.md", "Cells B4:C4 · data.csv"),
/// and the message carries it for the model, as File changes does with lines (the user's request,
/// 2026-10-25). An area dragged over a PDF's page, a picture or a video's frame goes in as a picture
/// of it with where it is ("Area on page 2 · report.pdf"); with Area on, over what the panel shows
/// frozen: a text, a 3D model's view, or Windows' previewer, which draws in a window of its own whose
/// selection Sloperator can't read. A stretch dragged over a sound goes in as "0.80-1.45 s · song.mp3",
/// with a picture of it. Pictures, frames, pages and sounds zoom and pan.
/// </summary>
public sealed partial class FileInspectorViewModel : ObservableObject, IFilePanel
{
    private readonly PreviewServices _previews;
    private readonly MainWindowViewModel? _chat;
    private bool _picking;

    /// <summary>How wide a picture is decoded at most: as it is, so zoomed in its pixels show, up to this.</summary>
    private const int MaxPictureWidth = 8192;

    /// <summary>Characters of a selection the model is given at most, with the count of the rest.</summary>
    private const int MaxPickedChars = 20_000;

    /// <summary>Where a picture of a marked area is saved for the model: the folder of pasted pictures, which go with their chat.</summary>
    private readonly string? _areasDir;

    /// <param name="chat">The chat the panel is open in, where what is selected in it goes; null with none.</param>
    /// <param name="areasDir">Where pictures of marked areas are saved; with a chat.</param>
    public FileInspectorViewModel(PreviewServices previews, MainWindowViewModel? chat = null, string? areasDir = null)
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
    [NotifyPropertyChangedFor(nameof(FileName), nameof(HasFile), nameof(CanFreeze), nameof(AreaTip), nameof(TextTip))]
    private string? _path;

    public string FileName => System.IO.Path.GetFileName(Path ?? "");
    public bool HasFile => Path is not null;

    /// <summary>"Markdown, 71 lines, 2 KB", once read.</summary>
    [ObservableProperty]
    private string _details = "";

    /// <summary>Said in the middle of the panel: how to open a file in it, that it is loading, or why it can't be shown.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMessage))]
    private string? _message = "Inspect a file from the chat, or drop one here.";

    public bool HasMessage => Message is not null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsMarkdown), nameof(CanShowText), nameof(TextTip))]
    private string? _markdown;

    /// <summary>Text in a fixed-width font: code, a log, a document's text where nothing on this PC previews it.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsText), nameof(TextTip))]
    private string? _text;

    /// <summary>What <see cref="Markdown"/> or <see cref="Text"/> is: the file as written (its lines are the file's), a CSV drawn as a table, or a document's text only.</summary>
    private enum Shown { File, Table, TextOnly, Hex }

    private Shown _shown;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanZoom), nameof(ShowsPicture))]
    private Bitmap? _picture;

    /// <summary>The picture file's own size, which an area on it is told in (an SVG's in its units).</summary>
    private (int Width, int Height) _pictureSize;

    /// <summary>An SVG, drawn sharp at any zoom in place of <see cref="Picture"/> (which holds it drawn at its own size, to copy).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanShowText), nameof(TextTip))]
    private SvgPicture? _drawing;

    public bool IsDrawing => Drawing is not null;

    /// <summary>What kind of file is shown, once its load has begun.</summary>
    private InspectorKind? _kind;

    /// <summary>
    /// Text can be turned on: the file is drawn as something else that has text to select (an SVG's
    /// source, a Markdown file's or a CSV's as written, the text in a Word, PowerPoint, Excel or PDF
    /// file or a web page). A text file or code already shows as text; a picture, video, sound or 3D
    /// model has none.
    /// </summary>
    public bool CanShowText => IsDrawing || ShellPath is not null || Pages is not null
        || (_kind is InspectorKind.Markdown or InspectorKind.Table && (Markdown is not null || ShowText));

    /// <summary>Text is always in the panel's bar (the user, 2026-10-31: in place of Code, which only an SVG had), greyed where it has nothing to show.</summary>
    public string TextTip => CanShowText ? (ShowText ? "Back to the file as it looks" : "Its text, to select and ask about")
        : !HasFile ? "Open a file first" : Text is not null || Markdown is not null ? "Shown as text already" : "This file has no text";

    /// <summary>Text is on: the file's text shows in place of how it looks, to select and ask about.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanZoom), nameof(ShowsPicture), nameof(ShowsShell), nameof(ShowsPages), nameof(CanShowText), nameof(TextTip))]
    private bool _showText;

    public bool ShowsPicture => Picture is not null && !ShowText && Frozen is null;

    /// <summary>How the file looked with Text off: a Markdown file's or a CSV's drawing, and what its selections are.</summary>
    private (string? Markdown, Shown Shown) _looks;

    partial void OnShowTextChanged(bool value)
    {
        Unpick();
        AreaMode = false;
        if (!value)
        {
            (_shown, Markdown, Text) = (_looks.Shown, _looks.Markdown, null);
            return;
        }
        if (Path is not { } path)
            return;
        _looks = (Markdown, _shown);
        if (IsDrawing || _kind is InspectorKind.Markdown or InspectorKind.Table)
        {
            (_shown, Markdown, Text) = (Shown.File, null, FileInspector.ReadText(path).Text);
            return;
        }
        // A document's text, as read_file gives it the agent (Word's and PowerPoint's as Markdown): read off the UI thread.
        var load = _loading;
        Task.Run(() =>
        {
            string? text = null;
            try { text = FileInspector.DocumentAsText(path); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { }
            var markdown = System.IO.Path.GetExtension(path).ToLowerInvariant() is ".docx" or ".docm" or ".dotx" or ".pptx" or ".pptm" or ".potx";
            Post(load, () =>
            {
                if (!ShowText)
                    return;
                _shown = Shown.TextOnly;
                if (text is null) Text = "";
                else if (markdown) Markdown = text;
                else Text = text;
            });
        });
    }

    /// <summary>A video, in a player of the panel's own: frame by frame, zoomed, an area marked on a frame.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanZoom), nameof(ShowsVideo))]
    private InspectorVideoViewModel? _video;

    /// <summary>An animation, frame by frame as a video is.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanZoom), nameof(ShowsAnimation))]
    private InspectorAnimationViewModel? _animation;

    /// <summary>A sound, as its waveform.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanZoom), nameof(MaxZoom), nameof(ShowsSound))]
    private InspectorSoundViewModel? _sound;

    /// <summary>A 3D model, to turn about.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanZoom), nameof(MaxZoom), nameof(ShowsModel))]
    private InspectorModelViewModel? _model;

    public bool ShowsModel => Model is not null && Frozen is null;

    /// <summary>A PDF's pages, drawn by Windows' PDF renderer as they come on screen; null for any other file.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanZoom), nameof(ShowsPages), nameof(CanShowText), nameof(TextTip))]
    private IReadOnlyList<PdfPageItem>? _pages;

    /// <summary>The PDF the pages are drawn from, open while it is shown.</summary>
    private IPdfDocument? _pdf;

    /// <summary>The file for Windows' previewer to draw (see <see cref="Views.ShellPreviewHost"/>).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsShell), nameof(CanShowText), nameof(TextTip))]
    private string? _shellPath;

    /// <summary>What a load under way is: one started after it wins.</summary>
    private int _loading;

    /// <summary>The prompts and settings a picture, animation, video, sound or 3D model says it was made with (ComfyUI keeps them in every file it saves); null when it says none.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRecipe), nameof(ShowsPromptButton))]
    private MediaRecipe? _recipe;

    public bool HasRecipe => Recipe is not null;

    /// <summary>Prompt metadata, in the corner over the file in the panel: there only for a file that says how it was made.</summary>
    public bool ShowsPromptButton => HasRecipe && CanPick;

    /// <summary>Prompt is on: what the file was made with shows under it; it stays on from one file to the next.</summary>
    [ObservableProperty]
    private bool _showRecipe;

    /// <summary>Reads what the file says it was made with, beside the file's own load, so a large one's wait doesn't hold the picture.</summary>
    private void LoadRecipe(int load, string path) => Task.Run(() =>
    {
        if (MediaRecipe.Read(path) is { } recipe)
            Post(load, () => Recipe = recipe);
    });

    /// <summary>Shows <paramref name="path"/> in the panel, in place of the file shown.</summary>
    public void Show(string path)
    {
        path = System.IO.Path.GetFullPath(path);
        var load = ++_loading;
        Unpick();
        AreaMode = false;
        ShowText = false;
        (_looks, _kind) = (default, null);
        Clear();
        Zoom = 1;
        Path = path;
        Details = "";
        Recipe = null;
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
                var kind = FileInspector.KindOf(path);
                Post(load, () => _kind = kind);
                if (kind is InspectorKind.Picture or InspectorKind.Animation or InspectorKind.Video or InspectorKind.Sound or InspectorKind.Model)
                    LoadRecipe(load, path);
                switch (kind)
                {
                    case InspectorKind.Markdown:
                    {
                        var (text, cut) = FileInspector.ReadText(path);
                        var lines = Lines(text);
                        Post(load, () => (_shown, Markdown, Details, Message) = (Shown.File, text, $"Markdown, {(cut ? "first " : "")}{lines:N0} line{(lines == 1 ? "" : "s")}, {size}", null));
                        break;
                    }
                    case InspectorKind.Text:
                    {
                        var (text, cut) = FileInspector.ReadText(path);
                        var lines = Lines(text);
                        Post(load, () => (_shown, Text, Details, Message) = (Shown.File, text, $"{(cut ? "first " : "")}{lines:N0} line{(lines == 1 ? "" : "s")}, {size}", null));
                        break;
                    }
                    case InspectorKind.Table:
                    {
                        var (table, rows, columns) = FileInspector.Table(path);
                        var shown = rows > FileInspector.MaxRows || columns > FileInspector.MaxColumns
                            ? $", the first {Math.Min(rows, FileInspector.MaxRows):N0} rows and {Math.Min(columns, FileInspector.MaxColumns)} columns" : "";
                        Post(load, () => (_shown, Markdown, Details, Message) = (Shown.Table, table, $"{rows:N0} rows, {columns} columns{shown}, {size}", table.Length == 0 ? "The table is empty." : null));
                        break;
                    }
                    case InspectorKind.Picture:
                        ShowPicture(load, path, size);
                        break;
                    case InspectorKind.Video when new InspectorVideoViewModel(path, _previews) is var video:
                        if (video.Load())
                            Post(load, () => (Video, Details, Message) = (video, $"{video.Details}, {size}", null), video.Dispose);
                        else
                        {
                            video.Dispose();
                            Post(load, () => (Details, Message) = (size, "Nothing on this PC can play this video."));
                        }
                        break;
                    case InspectorKind.Animation when new InspectorAnimationViewModel(path) is var animation:
                        if (animation.Load())
                            Post(load, () => (Animation, Details, Message) = (animation, $"{animation.Details}, {size}", null), animation.Dispose);
                        else
                        {
                            animation.Dispose();
                            ShowPicture(load, path, size);
                        }
                        break;
                    case InspectorKind.Sound when new InspectorSoundViewModel(path, _previews) is var sound:
                        if (sound.Load())
                            Post(load, () =>
                            {
                                sound.Picked += PickRange;
                                sound.PropertyChanged += OnPartZoomed;
                                (Sound, Details, Message) = (sound, $"{sound.Details}, {size}", sound.Samples.Length == 0 ? "The sound is empty." : null);
                            }, sound.Dispose);
                        else
                        {
                            sound.Dispose();
                            Post(load, () => (Details, Message) = (size, "Nothing on this PC can play this sound."));
                        }
                        break;
                    case InspectorKind.Model when new InspectorModelViewModel(path) is var model:
                    {
                        var mesh = model.Load();
                        Post(load, () =>
                        {
                            model.Mesh = mesh;
                            model.PropertyChanged += OnPartZoomed;
                            (Model, Details, Message) = (model, $"{model.Details}, {size}", mesh.TriangleCount == 0 ? "The model has no triangles to draw." : null);
                        }, () =>
                        {
                            mesh.Dispose();
                            model.Dispose();
                        });
                        break;
                    }
                    case InspectorKind.Document when ShowPdf(load, path, size):
                        break;
                    case InspectorKind.Document when OperatingSystem.IsWindows() && Views.ShellPreviewHost.CanShow(path):
                        Post(load, () => (ShellPath, Details, Message) = (path, size, null));
                        break;
                    case InspectorKind.Document:
                        ShowAsText(load, path, size);
                        break;
                    default:
                    {
                        var (hex, length) = FileInspector.Hex(path);
                        var details = length > FileInspector.MaxHexBytes ? $"{size}, its first {Core.Strings.Bytes(FileInspector.MaxHexBytes)} in hex" : $"{size}, in hex";
                        Post(load, () => (_shown, Text, Details, Message) = (Shown.Hex, hex, details, hex.Length == 0 ? "The file is empty." : null));
                        break;
                    }
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Post(load, () => Message = $"Can't be shown: {ex.Message}");
            }
        });
    }

    /// <summary>A picture, as it is, so its pixels show zoomed in; a huge one at most 8192 wide. An SVG as what it draws.</summary>
    private void ShowPicture(int load, string path, string size)
    {
        if (SvgFile.Handles(path))
        {
            if (SvgFile.Open(path) is not { } svg)
            {
                // Not one Svg.Skia draws: its text, which is what an SVG is.
                var (text, cut) = FileInspector.ReadText(path);
                var lines = Lines(text);
                Post(load, () => (_shown, Text, Details, Message) = (Shown.File, text,
                    $"SVG that draws nothing here, {(cut ? "first " : "")}{lines:N0} line{(lines == 1 ? "" : "s")}, {size}", null));
                return;
            }
            using var drawn = svg.Render(1);
            var picture = FilePreview.ToBitmap(drawn);
            var units = ((int)Math.Round(svg.Width), (int)Math.Round(svg.Height));
            Post(load, () => (_pictureSize, Drawing, Picture, Details, Message) = (units, svg, picture, $"SVG {svg.Width:0.#}x{svg.Height:0.#}, {size}", null), picture.Dispose);
            return;
        }
        using var probe = Core.Media.PictureFile.Codec(path);
        var own = probe is null ? (0, 0) : (probe.Info.Width, probe.Info.Height);
        using var stream = File.OpenRead(path);
        var bitmap = own.Item1 is > 0 and <= MaxPictureWidth ? new Bitmap(stream) : Bitmap.DecodeToWidth(stream, MaxPictureWidth, BitmapInterpolationMode.HighQuality);
        if (own.Item1 == 0)
            own = (bitmap.PixelSize.Width, bitmap.PixelSize.Height);
        var details = probe is null ? size : $"{own.Item1}x{own.Item2}, {size}";
        Post(load, () => (_pictureSize, Picture, Details, Message) = (own, bitmap, details, null), bitmap.Dispose);
    }

    /// <summary>A sound's or a model's own zoom (the wheel over it) shows on the panel's zoom buttons.</summary>
    private void OnPartZoomed(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(InspectorSoundViewModel.Zoom))
            Zoom = sender is InspectorSoundViewModel s ? s.Zoom : sender is InspectorModelViewModel m ? m.Zoom : Zoom;
    }

    /// <summary>
    /// A PDF's pages, drawn by Windows' PDF renderer; false for any other file, and for a PDF it
    /// can't open (damaged, with a password), which Windows' previewer or its
    /// text then shows.
    /// </summary>
    private bool ShowPdf(int load, string path, string size)
    {
        if (!System.IO.Path.GetExtension(path).Equals(".pdf", StringComparison.OrdinalIgnoreCase))
            return false;
        IPdfDocument pdf;
        try { pdf = PdfPages.Open(path); }
        catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or IOException or UnauthorizedAccessException)
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
        if (FileInspector.DocumentAsText(path) is { } text)
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
        if (!FileInspector.LooksLikeText(path))
        {
            Post(load, () => (Details, Message) = (size, "No preview for this kind of file."));
            return;
        }
        var (source, _) = FileInspector.ReadText(path);
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
        // Not disposed: the render thread may still be drawing it (see SvgPicture).
        Drawing = null;
        Video?.Dispose();
        Video = null;
        Animation?.Dispose();
        Animation = null;
        if (Sound is { } sound)
        {
            sound.Picked -= PickRange;
            sound.PropertyChanged -= OnPartZoomed;
            sound.Dispose();
        }
        Sound = null;
        if (Model is { } model)
        {
            model.PropertyChanged -= OnPartZoomed;
            model.Dispose();
        }
        Model = null;
    }

    // ---- zoom -----------------------------------------------------------------------------------

    /// <summary>How much a picture, a frame or the PDF's pages are zoomed in: 1 fits them in the panel.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ZoomText))]
    private double _zoom = 1;

    /// <summary>What zooms: pictures, frames, a PDF's pages, a sound, a 3D model, and what is frozen for marking.</summary>
    public bool CanZoom => ShowsPages || ShowsPicture || Video is not null || Animation is not null || Sound is not null || Model is not null || Frozen is not null;

    /// <summary>Pages are drawn sharp up to 3 times the panel's width; pictures and frames go to their pixels, a sound to its samples.</summary>
    public double MaxZoom => Frozen is not null ? Views.ZoomBox.MaxZoom : Pages is not null ? 3 : Sound is { } sound ? sound.MaxZoom
        : Model is not null ? InspectorModelViewModel.MaxZoom : Views.ZoomBox.MaxZoom;

    public string ZoomText => Zoom <= 1.001 ? "Fit" : $"{Zoom * 100:0}%";

    partial void OnZoomChanged(double value)
    {
        if (Pages is { } pages)
            foreach (var page in pages)
                page.Zoom = Frozen is null ? value : 1;
        if (Frozen is null && Sound is { } sound && Math.Abs(sound.Zoom - value) > 1e-9)
            sound.Zoom = value;
        if (Frozen is null && Model is { } model && Math.Abs(model.Zoom - value) > 1e-9)
            model.Zoom = value;
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
    /// over. Made for a text, whose drags select text, and Windows' previewer, which takes the mouse itself;
    /// there for any file, as the bar's buttons are the same for every file.
    /// </summary>
    [ObservableProperty]
    private bool _areaMode;

    /// <summary>Area can be turned on (in the panel) for any file shown (the user, 2026-10-31: for everything but no file).</summary>
    public bool CanFreeze => CanPick && HasFile;

    /// <summary>Area is always in the panel's bar, greyed with no file open.</summary>
    public string AreaTip => CanFreeze ? "Freeze the view to drag over a part of it" : "Open a file first";

    /// <summary>The panel's view of the file, frozen while Area is on; null otherwise.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsShell), nameof(ShowsMarkdown), nameof(ShowsText), nameof(ShowsModel), nameof(ShowsPicture), nameof(ShowsVideo), nameof(ShowsAnimation), nameof(ShowsSound), nameof(ShowsPages), nameof(CanZoom), nameof(MaxZoom))]
    private Bitmap? _frozen;

    private RawImage? _frozenPixels;

    public bool ShowsShell => ShellPath is not null && !ShowText && Frozen is null;
    public bool ShowsMarkdown => Markdown is not null && Frozen is null;
    public bool ShowsText => Text is not null && Frozen is null;
    public bool ShowsVideo => Video is not null && Frozen is null;
    public bool ShowsAnimation => Animation is not null && Frozen is null;
    public bool ShowsSound => Sound is not null && Frozen is null;
    public bool ShowsPages => Pages is not null && !ShowText && Frozen is null;

    /// <summary>What plays stops as the view is frozen: the still is the moment Area was turned on.</summary>
    internal void StopPlaying()
    {
        Video?.Pause();
        Animation?.Pause();
        Sound?.Pause();
        Model?.Pause();
    }

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
        // A model or a sound zoomed before keeps its zoom.
        Zoom = Model?.Zoom ?? Sound?.Zoom ?? 1;
    }

    // ---- what is selected, into the chat's box ---------------------------------------------------

    /// <summary>The text selected in the panel that is in the box; null with none.</summary>
    private DocumentPick? _pick;

    /// <summary>
    /// The area marked in the panel that is in the box: the picture of it saved for the model, where
    /// it is as the card says it ("area on page 2") and as the model is told it; null with none.
    /// </summary>
    private sealed record PickedArea(string Picture, string Where, string Told, bool Frozen, string Shows = "that area, cut from it");

    private PickedArea? _area;

    /// <summary>The area marked on the panel's picture, frame, page or frozen view; null with none.</summary>
    [ObservableProperty]
    private DocumentArea? _mark;

    partial void OnMarkChanged(DocumentArea? value)
    {
        if (Pages is { } pages)
            foreach (var page in pages)
                page.Mark = value is { Page: { } p } && p == page.Index ? value : null;
        if (value is null && Sound is { } sound)
            sound.Range = null;
    }

    /// <summary>The selection was taken off the box, sent or let go of: the panel clears it on screen too.</summary>
    public event Action? Unpicked;

    /// <summary>Text selected in <see cref="Text"/>, from character <paramref name="start"/> to <paramref name="end"/>; nothing when they are the same.</summary>
    public void SelectedInText(int start, int end) =>
        Picked(Text is not { } text ? null : _shown == Shown.Hex ? DocumentPick.InHex(text, start, end) : DocumentPick.InText(text, start, end));

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
        Func<(RawImage? Picture, string Where, string Told)> cut;
        if (Frozen is not null && _frozenPixels is { } frozen)
        {
            Model?.Pause();
            var told = ShellPath is not null ? "as Windows' previewer showed it in the panel, scrolled where they had it"
                : Model is not null ? "as the panel drew it in that view"
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
            cut = () => (DocumentArea.Decode(path) is { } picture ? area.Crop(picture) : null, area.Where, Drawing is null
                ? $"They marked an area of the picture: {area.Place(w, h, "pixels")}"
                : $"They marked an area of what the SVG draws: {area.Place(w, h, "units of its drawing (its CSS pixels)")}");
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
        else if (Animation is { } animation)
        {
            animation.Pause();
            var frame = animation.FrameIndex;
            var where = $"area of frame {frame + 1}";
            var at = animation.TimeOf(frame).TotalSeconds;
            cut = () => (animation.FullFrame(frame) is { } full ? area.Crop(full) : null, where,
                $"They marked an area of frame {frame + 1:N0} of its {animation.FileFrames:N0} (at {at:0.000} s; frames counted from 1): " +
                area.Place(animation.Size.Width, animation.Size.Height, "pixels"));
        }
        else
            return;
        Save(cut, area);
    }

    /// <summary>
    /// A stretch dragged over the sound, from <c>From</c> to <c>To</c> seconds: a picture of it (its
    /// waveform over its spectrogram, as the model hears a sound) is saved and goes into the box as
    /// "0.80-1.45 s · song.mp3", the model told where it is to the sample. Null (a click) takes out what was picked.
    /// </summary>
    private void PickRange((double From, double To)? range)
    {
        if (range is not { } r || Sound is not { } sound || _areasDir is null || Path is null)
        {
            Unpick();
            return;
        }
        // A stretch, like an area, is told apart from another by its marks: one mark stands for it.
        var area = new DocumentArea(r.From / sound.Length, 0, r.To / sound.Length, 1);
        Mark = area;
        var where = Core.Media.SoundRange.Label(r.From, r.To);
        Save(() =>
        {
            using var picture = Core.Media.SoundRange.Picture(sound.Samples, sound.Rate, r.From, r.To);
            var bgra = new byte[picture.Width * picture.Height * 4];
            using (var copy = picture.Copy(SkiaSharp.SKColorType.Bgra8888))
                for (var row = 0; row < copy.Height; row++)
                    System.Runtime.InteropServices.Marshal.Copy(copy.GetPixels() + row * copy.RowBytes, bgra, row * copy.Width * 4, copy.Width * 4);
            return (new RawImage(picture.Width, picture.Height, bgra), where, Core.Media.SoundRange.Told(sound.Samples, sound.Rate, r.From, r.To));
        }, area, "that stretch's waveform (top) and spectrogram (bottom, low notes at the bottom, louder is brighter), its seconds marked");
    }

    /// <summary>Cuts the picture of what was picked off the UI thread, saves it, and puts it in the box if it is still what is marked.</summary>
    private void Save(Func<(RawImage? Picture, string Where, string Told)> cut, DocumentArea area, string? shows = null)
    {
        var load = _loading;
        var name = FileName;
        var dir = _areasDir!;
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
                    _area = shows is null ? new PickedArea(file, where, told, Frozen is not null) : new PickedArea(file, where, told, Frozen is not null, shows);
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
        var opening = $"[The File inspector panel is open beside the chat, showing {path}. ";
        if (Model is { } shown)
            opening += $"It draws the 3D model {char.ToLowerInvariant(shown.ViewText[0])}{shown.ViewText[1..]}, at {shown.Zoom * 100:0}% zoom" +
                       (shown.TakeText is { } take ? $", posed {take}" : "") + ". ";
        if (_area is { } area && files.Contains(area.Picture, StringComparer.OrdinalIgnoreCase))
            return ($"File inspector · {FileName} · {area.Where}",
                $"{opening}{area.Told}. The picture {area.Picture} attached below is {area.Shows}, which is what they refer to.]");
        if (_pick is not { } pick || !files.Contains(path, StringComparer.OrdinalIgnoreCase))
            return ("File inspector · " + FileName, opening + "What they say is about this file unless they say otherwise.]");
        var where = Where(pick);
        var context = new System.Text.StringBuilder(opening);
        context.Append(_shown switch
        {
            Shown.Table => $"They selected {where} of it (shown as a table, its first row the header as row 1{(pick.Columns is { Count: > 0 } columns ? $"; {(columns.Count == 1 ? "column" : "columns")} {string.Join(", ", columns)}" : "")})",
            Shown.TextOnly => "They selected this in its text (as read_file gives it, so not lines of the file)",
            Shown.Hex when pick.Bytes is { } bytes => $"They selected {where} of it ({(bytes.Last - bytes.First + 1):N0} bytes from offset {bytes.First:N0}; the panel shows its start as a hex dump: each line an offset, 16 bytes in hex, and those bytes as text, a dot for any that isn't a letter)",
            _ when Markdown is not null => $"They selected {where} of it (the panel draws its Markdown formatted: the text below is as shown, without the marks)",
            _ => $"They selected {where} of it",
        }).Append(", which is what they refer to:\n");
        var text = pick.Text.TrimEnd('\r', '\n');
        context.Append(text.Length <= MaxPickedChars ? text : text[..MaxPickedChars] + $"\n[... {text.Length - MaxPickedChars:N0} more selected characters]");
        context.Append("\n]");
        return ($"File inspector · {FileName} · {where}", context.ToString());
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
