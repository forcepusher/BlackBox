using Avalonia.Controls;
using BlackBox.App.ViewModels;

namespace BlackBox.App.Views;

/// <summary>The Document preview panel, on the left of the chat window: one file shown whole.</summary>
public partial class DocumentPanel : UserControl
{
    public DocumentPanel()
    {
        InitializeComponent();
        // Windows' previewer couldn't show it: the file's text instead.
        Shell.Failed += why => (DataContext as DocumentViewModel)?.ShellFailed(why);
    }
}
