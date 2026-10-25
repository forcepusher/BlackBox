using Avalonia.Controls;
using Avalonia.Input;
using BlackBox.App.ViewModels;

namespace BlackBox.App.Views;

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
}
