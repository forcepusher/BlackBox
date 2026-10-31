using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Sloperator.App.ViewModels;
using Sloperator.Core.Media;

namespace Sloperator.App.Views;

/// <summary>
/// One right-click menu for every picture and file the app shows: Open in each side panel the file
/// opens in (Open in Inspector, Open in Code editor), then Copy image or Copy file. A control says what
/// it shows: <c>FileMenu.Image</c>, the picture on screen; <c>FileMenu.Data</c>, the picture's bytes
/// when no file has it (a screenshot); <c>FileMenu.File</c>, the file behind it. A right-click on it,
/// or on anything in it without a menu of its own, opens the menu. A menu of its own (the Video
/// generator's, File changes') takes <see cref="Items(Visual, IImage, byte[], string)"/> at its end.
/// </summary>
public static class FileMenu
{
    public static readonly AttachedProperty<IImage?> ImageProperty = AvaloniaProperty.RegisterAttached<Control, IImage?>("Image", typeof(FileMenu));
    public static readonly AttachedProperty<byte[]?> DataProperty = AvaloniaProperty.RegisterAttached<Control, byte[]?>("Data", typeof(FileMenu));
    public static readonly AttachedProperty<string?> FileProperty = AvaloniaProperty.RegisterAttached<Control, string?>("File", typeof(FileMenu));

    static FileMenu()
    {
        ImageProperty.Changed.AddClassHandler<Control>((c, _) => Hook(c));
        DataProperty.Changed.AddClassHandler<Control>((c, _) => Hook(c));
        FileProperty.Changed.AddClassHandler<Control>((c, _) => Hook(c));
    }

    public static IImage? GetImage(Control c) => c.GetValue(ImageProperty);
    public static void SetImage(Control c, IImage? value) => c.SetValue(ImageProperty, value);
    public static byte[]? GetData(Control c) => c.GetValue(DataProperty);
    public static void SetData(Control c, byte[]? value) => c.SetValue(DataProperty, value);
    public static string? GetFile(Control c) => c.GetValue(FileProperty);
    public static void SetFile(Control c, string? value) => c.SetValue(FileProperty, value);

    /// <summary>Whether the control has this menu: the chat's own menu leaves it be.</summary>
    public static bool Has(Control c) => GetImage(c) is Bitmap || GetData(c) is not null || GetFile(c) is not null;

    private static void Hook(Control c)
    {
        c.ContextRequested -= OnContextRequested;
        c.ContextRequested += OnContextRequested;
    }

    private static void OnContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (e.Handled || sender is not Control control)
            return;
        var file = GetFile(control);
        List<Control> items = [.. Open(control, file), .. Copy(control, () => GetImage(control), GetData(control), file)];
        if (items.Count == 0)
            return;
        e.Handled = true;
        new ContextMenu { ItemsSource = items }.Open(control);
    }

    /// <summary>The menu's entries for what <paramref name="at"/> shows: <see cref="Open"/>, then Copy image or Copy file.</summary>
    public static List<Control> Items(Visual at, IImage? image, byte[]? data, string? file) => [.. Open(at, file), .. Copy(at, () => image, data, file)];

    /// <summary>
    /// Open in each side panel of the chat that opens a file of this kind ("Open in Inspector" for any
    /// file, "Open in Code editor" for text, see <see cref="SidePanelOption.OpenIn"/>), but the panel
    /// open with this file in it already. The panel opens in place of the one open, as a card's Inspect
    /// does; from the full-screen viewer, which would cover it, the viewer closes first. The panels are
    /// told by the file's name, and by the disk only on this PC's own drives (a folder, or a file of no
    /// kind its name tells), as a share whose server is off would hold the menu.
    /// </summary>
    public static List<Control> Open(Visual at, string? file)
    {
        var items = new List<Control>();
        var top = TopLevel.GetTopLevel(at);
        var chat = top is MediaViewer viewer ? viewer.Chat : top?.DataContext as MainWindowViewModel;
        if (file is null || chat is null)
            return items;
        foreach (var panel in chat.PanelsFor(file))
        {
            var open = new MenuItem { Header = $"Open in {panel.OpenIn}" };
            open.Click += (_, _) =>
            {
                (top as MediaViewer)?.Close();
                chat.OpenIn(panel, file);
            };
            items.Add(open);
        }
        return items;
    }

    /// <summary>
    /// Copy image for a picture: the whole of it, not the size it is shown at, and its file with it when
    /// it has one, so Paint, Word or a chat app's box paste the picture and Explorer or a mail the file.
    /// Copy file for any other file (a video, a sound, a document, a folder), which a video's frame or a
    /// document's peek doesn't stand for. Nothing is read from the disk until a click: a share whose
    /// server is off would hold the menu. The picture is read again at the click: a playing video's
    /// frame on screen then, the one at the right-click let go of.
    /// </summary>
    public static List<Control> Copy(Visual at, IImage? image, byte[]? data, string? file) => Copy(at, () => image, data, file);

    private static List<Control> Copy(Visual at, Func<IImage?> image, byte[]? data, string? file)
    {
        var picture = file is not null && MediaPreview.KindOf(file) is "image" or "animation";
        var items = new List<Control>();
        if (image() is Bitmap || data is not null || picture)
        {
            var copy = new MenuItem { Header = "Copy image" };
            copy.Click += (_, _) => _ = CopyImageAsync(at, image, data, picture ? file : null);
            items.Add(copy);
        }
        if (file is not null && !picture)
        {
            var copy = new MenuItem { Header = "Copy file" };
            copy.Click += (_, _) => _ = CopyFileAsync(at, file);
            items.Add(copy);
        }
        return items;
    }

    /// <summary>The picture last put on the clipboard: programs read it from Sloperator when they paste, so it stays until the next copy.</summary>
    private static Bitmap? _copied;

    private static async Task CopyImageAsync(Visual at, Func<IImage?> shown, byte[]? data, string? file)
    {
        if (TopLevel.GetTopLevel(at) is not { Clipboard: { } clipboard } top)
            return;
        try
        {
            // What is on screen is copied before anything is awaited: a video's next frame lets go of it.
            var picture = file is null && data is null ? Duplicate(shown() as Bitmap) : await Task.Run(() => FullSize(file, data)) ?? Duplicate(shown() as Bitmap);
            if (picture is null)
                return;
            var item = new DataTransferItem();
            item.SetBitmap(picture);
            if (file is not null && await Task.Run(() => File.Exists(file)) && await top.StorageProvider.TryGetFileFromPathAsync(file) is { } stored)
                item.SetFile(stored);
            await SetAsync(clipboard, item, picture);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Another program holds the clipboard; the next try will do.
        }
    }

    private static async Task CopyFileAsync(Visual at, string path)
    {
        if (TopLevel.GetTopLevel(at) is not { Clipboard: { } clipboard } top)
            return;
        try
        {
            var kind = await Task.Run(() => File.Exists(path) ? 1 : Directory.Exists(path) ? 2 : 0);
            if ((kind == 1 ? await top.StorageProvider.TryGetFileFromPathAsync(path) as IStorageItem
                    : kind == 2 ? await top.StorageProvider.TryGetFolderFromPathAsync(path) : null) is not { } stored)
                return;
            var item = new DataTransferItem();
            item.SetFile(stored);
            await SetAsync(clipboard, item, null);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Another program holds the clipboard; the next try will do.
        }
    }

    private static async Task SetAsync(IClipboard clipboard, DataTransferItem item, Bitmap? picture)
    {
        var transfer = new DataTransfer();
        transfer.Add(item);
        await clipboard.SetDataAsync(transfer);
        (_copied, picture) = (picture, _copied);
        picture?.Dispose();
    }

    /// <summary>The picture in <paramref name="data"/> or the file at <paramref name="path"/>, decoded whole; null if neither is a picture.</summary>
    private static Bitmap? FullSize(string? path, byte[]? data)
    {
        try
        {
            if (data is not null)
                return new Bitmap(new MemoryStream(data));
            if (path is not null && SvgFile.Handles(path))
                return PictureFile.Decode(path) is { } drawn ? Copied(drawn) : null;
            if (path is not null && File.Exists(path))
                return new Bitmap(path);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { }
        return null;
    }

    /// <summary>An SVG drawn at its own size, as a bitmap of its own.</summary>
    private static Bitmap Copied(SkiaSharp.SKBitmap drawn)
    {
        using (drawn)
            return ViewModels.FilePreview.ToBitmap(drawn);
    }

    /// <summary>A copy of a picture on screen, which its view may let go of (a video's frame changes as it plays).</summary>
    private static Bitmap? Duplicate(Bitmap? shown)
    {
        if (shown is null)
            return null;
        using var png = new MemoryStream();
        shown.Save(png, PngBitmapEncoderOptions.Default);
        png.Position = 0;
        return new Bitmap(png);
    }
}
