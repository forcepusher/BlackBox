using Avalonia.Controls;
using Avalonia.Interactivity;
using Sloperator.App.ViewModels;

namespace Sloperator.App.Views;

public partial class SidePanelClose : UserControl
{
    public SidePanelClose() => InitializeComponent();

    /// <summary>The panel closes in the chat window it is in.</summary>
    private void OnClose(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this)?.DataContext is MainWindowViewModel chat)
            chat.CloseSidePanel();
    }
}
