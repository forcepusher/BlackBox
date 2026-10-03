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

    private void OnRestoreDefaults(object? sender, RoutedEventArgs e)
    {
        var vm = Vm;
        vm.RestoreAdvancedDefaults();
        // The settings objects were replaced; rebind so every field shows the new values.
        DataContext = null;
        DataContext = vm;
    }

    private async void OnBrowseWorkingDir(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Working folder" });
        if (folders.Count > 0 && folders[0].TryGetLocalPath() is { } path)
            Vm.WorkingDirectory = path;
    }
}
