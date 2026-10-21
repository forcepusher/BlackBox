using System.ComponentModel;
using System.Diagnostics;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using BlackBox.Core.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BlackBox.App.ViewModels;

/// <summary>A picture or video the full-screen viewer shows: a file, or a screenshot that has none.</summary>
/// <param name="Source">What was clicked for it in the chat (a preview, an attachment, a tool call), to start there.</param>
public sealed record MediaEntry(object Source, string? Path, byte[]? Picture, string Name);

/// <summary>
/// The full-screen viewer, over the screen the chat is on: a picture at the screen's size (the
/// wheel comes closer), an animation, or a video playing with its sound, and the chat's other
/// pictures and videos one arrow key away. Prompt shows what the file was made with.
/// </summary>
public sealed partial class MediaViewerViewModel : ObservableObject, IDisposable
{
    private readonly IReadOnlyList<MediaEntry> _entries;
    private readonly PreviewServices _services;
    private readonly int _width;

    /// <param name="width">The screen's width in pixels: pictures and frames are decoded at most that wide.</param>
    public MediaViewerViewModel(IReadOnlyList<MediaEntry> entries, int index, PreviewServices services, int width)
    {
        _entries = entries;
        _services = services;
        _width = Math.Max(FilePreview.Width, width);
        ShowEntry(Math.Clamp(index, 0, entries.Count - 1));
    }

    /// <summary>What is on screen: a <see cref="ViewerPicture"/>, an <see cref="AnimationPreview"/> or a <see cref="VideoPreview"/>.</summary>
    [ObservableProperty]
    private object? _shown;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Name), nameof(Counter), nameof(HasFile))]
    [NotifyCanExecuteChangedFor(nameof(PreviousCommand), nameof(NextCommand), nameof(OpenCommand), nameof(ShowInFolderCommand))]
    private int _index;

    public MediaEntry Current => _entries[Index];
    public string Name => Current.Name;
    public bool HasFile => Current.Path is not null;

    /// <summary>"3 of 7", when the chat has more than one.</summary>
    public string Counter => _entries.Count > 1 ? $"{Index + 1} of {_entries.Count}" : "";

    public bool HasMany => _entries.Count > 1;

    /// <summary>"1024x1024, 1.2 MB", from what is shown.</summary>
    [ObservableProperty]
    private string _details = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRecipe))]
    private MediaRecipe? _recipe;

    public bool HasRecipe => Recipe is not null;

    /// <summary>The panel with the prompts and settings is open; it stays open from one file to the next.</summary>
    [ObservableProperty]
    private bool _showRecipe;

    [RelayCommand]
    private void ToggleRecipe() => ShowRecipe = !ShowRecipe;

    private bool CanGoBack() => Index > 0;
    private bool CanGoOn() => Index < _entries.Count - 1;

    [RelayCommand(CanExecute = nameof(CanGoBack))]
    private void Previous() => ShowEntry(Index - 1);

    [RelayCommand(CanExecute = nameof(CanGoOn))]
    private void Next() => ShowEntry(Index + 1);

    /// <summary>Plays or pauses the video on screen (Space).</summary>
    public void PlayOrPause()
    {
        if (Shown is VideoPreview video)
            video.PlayCommand.Execute(null);
    }

    [RelayCommand(CanExecute = nameof(HasFile))]
    private void Open()
    {
        var path = Current.Path!;
        Task.Run(() =>
        {
            try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })?.Dispose(); }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException) { }
        });
    }

    [RelayCommand(CanExecute = nameof(HasFile))]
    private void ShowInFolder()
    {
        var path = Current.Path!;
        Task.Run(() =>
        {
            try { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true })?.Dispose(); }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException) { }
        });
    }

    private void ShowEntry(int index)
    {
        Release();
        Index = index;
        Recipe = null;
        Details = "";
        var entry = _entries[index];
        if (entry.Path is not { } path)
        {
            Shown = new ViewerPicture(null, entry.Picture, _width * 2, PictureLoaded);
            return;
        }
        if (MediaPreview.KindOf(path) is "video" or "animation")
        {
            var preview = FilePreview.For(path, _services, _width);
            preview.PropertyChanged += OnPreviewChanged;
            (preview as VideoPreview)?.PlayWhenLoaded();
            Shown = preview;
            return;
        }
        Shown = new ViewerPicture(path, null, _width * 2, PictureLoaded);
        var at = index;
        Task.Run(() =>
        {
            var recipe = MediaRecipe.Read(path);
            Dispatcher.UIThread.Post(() =>
            {
                if (Index == at && Shown is ViewerPicture) Recipe = recipe;
            });
        });
    }

    /// <summary>A video's or animation's details and recipe, as its preview reads them.</summary>
    private void OnPreviewChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!ReferenceEquals(sender, Shown) || sender is not FilePreview preview)
            return;
        if (e.PropertyName == nameof(FilePreview.Details))
            Details = preview.Details;
        else if (e.PropertyName == nameof(FilePreview.Recipe))
            Recipe = preview.Recipe;
    }

    private void Release()
    {
        if (Shown is FilePreview preview)
            preview.PropertyChanged -= OnPreviewChanged;
        (Shown as IDisposable)?.Dispose();
        Shown = null;
    }

    public void Dispose() => Release();

    private void PictureLoaded(ViewerPicture picture, string details)
    {
        if (ReferenceEquals(Shown, picture))
            Details = details;
    }
}

/// <summary>A picture at full size (up to twice a screen's width, for coming closer), read off the UI thread.</summary>
public sealed partial class ViewerPicture : ObservableObject, IDisposable
{
    [ObservableProperty]
    private Bitmap? _image;

    private bool _disposed;

    public ViewerPicture(string? path, byte[]? data, int width, Action<ViewerPicture, string> loaded)
    {
        Task.Run(() =>
        {
            Bitmap? bitmap = null;
            string details;
            try
            {
                using var stream = data is not null ? new MemoryStream(data) : (Stream)File.OpenRead(path!);
                using var probe = SkiaSharp.SKCodec.Create(path ?? "") ?? (data is not null ? SkiaSharp.SKCodec.Create(new MemoryStream(data)) : null);
                var size = probe is { } p ? $"{p.Info.Width}x{p.Info.Height}" : "";
                bitmap = probe is { } c && c.Info.Width > width ? Bitmap.DecodeToWidth(stream, width, BitmapInterpolationMode.HighQuality) : new Bitmap(stream);
                details = path is null ? size : string.Join(", ", new[] { size, FilePreview.Size(path) }.Where(s => s.Length > 0));
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                details = path is not null && !File.Exists(path) ? "The file is no longer there." : $"Can't be shown: {ex.Message}";
            }
            Dispatcher.UIThread.Post(() =>
            {
                if (_disposed)
                {
                    bitmap?.Dispose();
                    return;
                }
                Image = bitmap;
                loaded(this, details);
            });
        });
    }

    public void Dispose()
    {
        _disposed = true;
        Image?.Dispose();
        Image = null;
    }
}
