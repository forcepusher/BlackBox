using Avalonia.Controls;
using Avalonia.Interactivity;
using BlackBox.App.ViewModels;

namespace BlackBox.App.Views;

public partial class PreviewFooter : UserControl
{
    public PreviewFooter() => InitializeComponent();

    /// <summary>Inspect: the file in the File inspector panel of the chat window it is in.</summary>
    private void OnInspect(object? sender, RoutedEventArgs e)
    {
        if (DataContext is FilePreview preview && TopLevel.GetTopLevel(this)?.DataContext is MainWindowViewModel chat)
            chat.Inspect(preview.Path);
    }
}
