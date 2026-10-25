using Avalonia.Controls;
using Avalonia.Interactivity;
using BlackBox.App.ViewModels;

namespace BlackBox.App.Views;

public partial class ToolkitClose : UserControl
{
    public ToolkitClose() => InitializeComponent();

    /// <summary>The tool closes in the chat window it is in.</summary>
    private void OnClose(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this)?.DataContext is MainWindowViewModel chat)
            chat.CloseToolkit();
    }
}
