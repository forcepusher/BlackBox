using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using BlackBox.App.ViewModels;

namespace BlackBox.App.Views;

public partial class FilmWindow : Window
{
    public FilmWindow() => InitializeComponent();

    private FilmViewModel Vm => (FilmViewModel)DataContext!;

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        // What it holds goes: players, the watch on the film's folder, jobs still running; a
        // ComfyUI BlackBox started for it is closed once its queue is done.
        (DataContext as FilmViewModel)?.Dispose();
    }

    private async void OnOpenClick(object? sender, RoutedEventArgs e)
    {
        var picked = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open a film's storyboard",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("Storyboard") { Patterns = ["storyboard.json"] }],
        });
        if (picked.FirstOrDefault()?.TryGetLocalPath() is { } path)
            Vm.Open(path);
    }

    private async void OnPickPicture(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not FrameItem frame)
            return;
        var picked = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "A picture for this frame",
            AllowMultiple = false,
            FileTypeFilter = [FilePickerFileTypes.ImageAll],
        });
        if (picked.FirstOrDefault()?.TryGetLocalPath() is { } path)
            frame.TakeOwnPicture(path);
    }

    private async void OnPickSound(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not SoundItem sound)
            return;
        var picked = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "A song or sound for the film",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("Sound") { Patterns = ["*.mp3", "*.wav", "*.flac", "*.ogg", "*.m4a", "*.aac", "*.opus"] }, FilePickerFileTypes.All],
        });
        if (picked.FirstOrDefault()?.TryGetLocalPath() is { } path)
        {
            sound.File = path;
            Vm.RefreshStates();
        }
    }
}
