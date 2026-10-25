using Avalonia.Controls;
using Avalonia.Input;
using BlackBox.App.ViewModels;

namespace BlackBox.App.Views;

/// <summary>The Inspect file panel, on the left of the chat window: the file's bar over the file (<see cref="InspectFileView"/>).</summary>
public partial class InspectFilePanel : UserControl
{
    public InspectFilePanel() => InitializeComponent();

    /// <summary>A click on the bar selects nothing: the selection leaves the box, as a click beside the text does.</summary>
    private void OnBarReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton == MouseButton.Left)
            (DataContext as InspectFileViewModel)?.Unpick();
    }
}
