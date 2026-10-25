using Avalonia.Controls;
using Avalonia.Interactivity;
using BlackBox.App.ViewModels;

namespace BlackBox.App.Views;

public partial class PreviewFooter : UserControl
{
    public PreviewFooter() => InitializeComponent();

    /// <summary>Preview: the file in the Document preview panel of the chat window it is in.</summary>
    private void OnPreview(object? sender, RoutedEventArgs e)
    {
        if (DataContext is FilePreview preview && TopLevel.GetTopLevel(this)?.DataContext is MainWindowViewModel chat)
            chat.PreviewFile(preview.Path);
    }
}
