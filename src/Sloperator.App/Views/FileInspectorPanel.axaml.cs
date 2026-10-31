using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Sloperator.App.ViewModels;
using Sloperator.Core.Documents;

namespace Sloperator.App.Views;

/// <summary>The File inspector panel, on the left of the chat window: the file's bar over the file (<see cref="FileInspectorView"/>).</summary>
public partial class FileInspectorPanel : UserControl
{
    public FileInspectorPanel() => InitializeComponent();

    /// <summary>A click on the bar selects nothing: the selection leaves the box, as a click beside the text does.</summary>
    private void OnBarReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton == MouseButton.Left)
            (DataContext as FileInspectorViewModel)?.Unpick();
    }

    /// <summary>
    /// Open file: a file picked from the PC is shown. The picker lists what the panel shows first, all
    /// together and kind by kind, and All files last, since any other file opens in hex.
    /// </summary>
    private async void OnOpenClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not FileInspectorViewModel vm || TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage)
            return;
        var types = FileInspector.PickerTypes;
        List<FilePickerFileType> filters =
        [
            new("Files it shows") { Patterns = types.SelectMany(t => t.Extensions).Select(x => "*" + x).ToList() },
            .. types.Select(t => new FilePickerFileType(t.Name) { Patterns = t.Extensions.Select(x => "*" + x).ToList() }),
            new("All files") { Patterns = ["*.*"] },
        ];
        var start = vm.Path is { } shown ? await storage.TryGetFolderFromPathAsync(System.IO.Path.GetDirectoryName(shown)!) : null;
        var picked = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open a file in the File inspector",
            FileTypeFilter = filters,
            SuggestedStartLocation = start,
        });
        if (picked.FirstOrDefault()?.TryGetLocalPath() is { } path)
            vm.Show(path);
    }
}
