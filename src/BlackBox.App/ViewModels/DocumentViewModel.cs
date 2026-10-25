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
/// in Explorer's preview pane, else shown as their text (<see cref="DocumentPreview"/>).
/// </summary>
public sealed partial class DocumentViewModel(PreviewServices previews) : ObservableObject, IToolkit
{
    /// <summary>How wide pictures are decoded for the panel.</summary>
    private const int PictureWidth = 1600;

    /// <summary>The file shown; null before one is.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FileName), nameof(HasFile))]
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
    private string? _markdown;

    /// <summary>Text in a fixed-width font: code, a log, a document's text where nothing on this PC previews it.</summary>
    [ObservableProperty]
    private string? _text;

    [ObservableProperty]
    private Bitmap? _picture;

    /// <summary>An animation, a video, a sound or a 3D model, in the chat's player.</summary>
    [ObservableProperty]
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
                        Post(load, () => (Markdown, Details, Message) = (text, $"Markdown, {(cut ? "first " : "")}{lines:N0} line{(lines == 1 ? "" : "s")}, {size}", null));
                        break;
                    }
                    case DocumentKind.Text:
                    {
                        var (text, cut) = DocumentPreview.ReadText(path);
                        var lines = Lines(text);
                        Post(load, () => (Text, Details, Message) = (text, $"{(cut ? "first " : "")}{lines:N0} line{(lines == 1 ? "" : "s")}, {size}", null));
                        break;
                    }
                    case DocumentKind.Table:
                    {
                        var (table, rows, columns) = DocumentPreview.Table(path);
                        var shown = rows > DocumentPreview.MaxRows || columns > DocumentPreview.MaxColumns
                            ? $", the first {Math.Min(rows, DocumentPreview.MaxRows):N0} rows and {Math.Min(columns, DocumentPreview.MaxColumns)} columns" : "";
                        Post(load, () => (Markdown, Details, Message) = (table, $"{rows:N0} rows, {columns} columns{shown}, {size}", table.Length == 0 ? "The table is empty." : null));
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
                            Media = FilePreview.For(path, previews, PictureWidth);
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
        Post(load, () => (Text, Details, Message) = (source, details, null));
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

    public (string Via, string Context)? About(IReadOnlyList<string> files) =>
        Path is null ? null
            : ("Document preview · " + FileName,
               $"[The Document preview panel is open beside the chat, showing {Path}. What they say is about this file unless they say otherwise.]");

    public void Sent() { }

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
        _disposed = true;
        _loading++;
        Clear();
    }
}
