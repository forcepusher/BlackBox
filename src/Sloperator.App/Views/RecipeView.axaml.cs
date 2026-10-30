using Avalonia.Input.Platform;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Sloperator.Core.Media;

namespace Sloperator.App.Views;

public partial class RecipeView : UserControl
{
    public RecipeView() => InitializeComponent();

    private void OnCopyText(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is RecipeText text)
            Copy(text.Text);
    }

    private void OnCopyAll(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MediaRecipe recipe)
            Copy(recipe.Describe());
    }

    private async void Copy(string text)
    {
        try
        {
            if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
                await clipboard.SetTextAsync(text);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Another program holds the clipboard; the next try will do.
        }
    }
}
