using Avalonia.Controls;
using Avalonia.Interactivity;
using Sloperator.App.ViewModels;

namespace Sloperator.App.Views;

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
