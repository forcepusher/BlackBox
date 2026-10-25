using System.Diagnostics;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using BlackBox.Core.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BlackBox.App.ViewModels;

/// <summary>
/// A file going with a message: dropped on the window, pasted, or picked with the paperclip. It
/// waits under the box (with × to take it off) until the message is sent, then shows in the
/// message's bubble. A picture shows as a thumbnail, anything else as its name and size.
/// </summary>
public sealed partial class AttachmentItem : ObservableObject, IDisposable
{
    private readonly Action<AttachmentItem>? _remove;

    /// <param name="remove">Takes it off the message being written; null in a sent message's bubble.</param>
    /// <param name="picked">Picked in the toolkit's panel (a piece of the video): what it is there, "Shot 2 · s2.mp4".</param>
    public AttachmentItem(string path, Action<AttachmentItem>? remove = null, string? picked = null)
    {
        Path = path;
        _remove = remove;
        Caption = picked;
        FileName = System.IO.Path.GetFileName(path.TrimEnd('\\', '/')) is { Length: > 0 } name ? name : path;
        IsPicture = MediaPreview.KindOf(path) is "image" or "animation";
        // Looked at off the UI thread: a file on a share that is off (a NAS, a mapped drive whose
        // server is gone) holds each look up for half a minute, and a reopened chat whose messages
        // had a few froze the window for minutes.
        Task.Run(Look);
    }

    private void Look()
    {
        bool folder;
        string details;
        try
        {
            folder = Directory.Exists(Path);
            details = folder ? "folder" : File.Exists(Path) ? FilePreview.Size(Path) : Picked ? "not made yet" : "no longer there";
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return;
        }
        Dispatcher.UIThread.Post(() =>
        {
            (IsFolder, Details) = (folder, details);
            if (folder)
                IsPicture = false;
        });
        if (IsPicture && !folder && details != "no longer there")
            LoadThumbnail(details);
    }

    public string Path { get; }
    public string FileName { get; }

    /// <summary>What it is in the toolkit's panel it was picked in ("Shot 2 · s2.mp4"); null for a file of the user's.</summary>
    public string? Caption { get; }

    /// <summary>Picked in the toolkit's panel, which says what it is to the model with the message.</summary>
    public bool Picked => Caption is not null;

    /// <summary>The name it shows by: what it is in the panel it was picked in, or its file's name.</summary>
    public string Title => Caption ?? FileName;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsName))]
    private bool _isPicture;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Icon))]
    private bool _isFolder;

    public bool CanRemove => _remove is not null;

    /// <summary>A file's name and size, or a folder's name; what a picture's thumbnail is for the rest.</summary>
    public bool ShowsName => !IsPicture || Thumbnail is null;

    private static readonly Avalonia.Media.Geometry FileShape = Avalonia.Media.StreamGeometry.Parse(
        "M14 2H6c-1.1 0-1.99.9-1.99 2L4 20c0 1.1.89 2 1.99 2H18c1.1 0 2-.9 2-2V8l-6-6zm2 16H8v-2h8v2zm0-4H8v-2h8v2zm-3-5V3.5L18.5 9H13z");
    private static readonly Avalonia.Media.Geometry FolderShape = Avalonia.Media.StreamGeometry.Parse(
        "M10 4H4c-1.1 0-1.99.9-1.99 2L2 18c0 1.1.9 2 2 2h16c1.1 0 2-.9 2-2V8c0-1.1-.9-2-2-2h-8l-2-2z");

    /// <summary>The icon next to the name: a folder or a file.</summary>
    public Avalonia.Media.Geometry Icon => IsFolder ? FolderShape : FileShape;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsName))]
    private Bitmap? _thumbnail;

    /// <summary>"1920x1080, 1.2 MB", "220 KB", "folder".</summary>
    [ObservableProperty]
    private string _details = "";

    /// <summary>The thumbnail's tooltip: the full path and what it is.</summary>
    public string Tip => Caption is null ? $"{Path}\n{Details}" : $"{Caption}\n{Path}\n{Details}";

    partial void OnDetailsChanged(string value) => OnPropertyChanged(nameof(Tip));

    private void LoadThumbnail(string size)
    {
        try
        {
            using var stream = File.OpenRead(Path);
            var bitmap = Bitmap.DecodeToHeight(stream, 240, BitmapInterpolationMode.HighQuality);
            using var probe = PictureFile.Codec(Path);
            var details = probe is null ? size : $"{probe.Info.Width}x{probe.Info.Height}, {size}";
            Dispatcher.UIThread.Post(() =>
            {
                if (_disposed) bitmap.Dispose(); // taken off, or its chat left, while it loaded
                else (Thumbnail, Details) = (bitmap, details);
            });
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Not a picture after all (or gone): it shows as a file.
        }
    }

    [RelayCommand]
    private void Remove() => _remove?.Invoke(this);

    // Off the UI thread: Windows' opening of a file on a share that is off waits for the share.
    [RelayCommand]
    private void Open() => Task.Run(() =>
    {
        try { Process.Start(new ProcessStartInfo(Path) { UseShellExecute = true })?.Dispose(); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { }
    });

    private bool _disposed;

    public void Dispose()
    {
        _disposed = true;
        Thumbnail?.Dispose();
        Thumbnail = null;
    }
}
