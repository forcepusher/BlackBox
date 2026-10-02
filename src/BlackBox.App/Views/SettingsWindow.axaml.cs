using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using BlackBox.App.ViewModels;

namespace BlackBox.App.Views;

public partial class SettingsWindow : Window
{
    public SettingsWindow() => InitializeComponent();

    private SettingsViewModel Vm => (SettingsViewModel)DataContext!;

    private void OnSave(object? sender, RoutedEventArgs e) => Close(true);

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);

    private async void OnBrowseModel(object? sender, RoutedEventArgs e)
    {
        if (await PickFile("Choose a GGUF model", "GGUF model", "*.gguf") is { } path)
            Vm.ModelPath = path;
    }

    private async void OnBrowseMmproj(object? sender, RoutedEventArgs e)
    {
        if (await PickFile("Choose the vision projector (mmproj)", "GGUF projector", "*.gguf") is { } path)
            Vm.MmprojPath = path;
    }

    private async void OnBrowseServer(object? sender, RoutedEventArgs e)
    {
        var pattern = OperatingSystem.IsWindows() ? "llama-server.exe" : "llama-server";
        if (await PickFile("Choose llama-server", "llama-server", pattern) is { } path)
            Vm.ServerPath = path;
    }

    private async void OnBrowseWorkingDir(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Working folder" });
        if (folders.Count > 0 && folders[0].TryGetLocalPath() is { } path)
            Vm.WorkingDirectory = path;
    }

    private async Task<string?> PickFile(string title, string typeName, string pattern)
    {
        var start = await StorageProvider.TryGetFolderFromPathAsync(Vm.Paths.ModelsDir);
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            SuggestedStartLocation = start,
            FileTypeFilter = [new FilePickerFileType(typeName) { Patterns = [pattern] }, FilePickerFileTypes.All],
        });
        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }
}
