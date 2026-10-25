using Avalonia.Controls;
using Avalonia.Interactivity;

namespace BlackBox.App.Views;

/// <summary>
/// Selectable text that keeps its selection when it loses focus: what is selected in the Document
/// preview panel stays marked while the user types about it in the chat's box, as picked lines do
/// in Code changes. Whoever shows it clears it (a click elsewhere in the panel, Esc, the message sent).
/// </summary>
public class KeptSelectionTextBlock : SelectableTextBlock
{
    protected override Type StyleKeyOverride => typeof(SelectableTextBlock);

    protected override void OnLostFocus(RoutedEventArgs e)
    {
        var (start, end) = (SelectionStart, SelectionEnd);
        base.OnLostFocus(e);
        if (start != end && SelectionStart == SelectionEnd)
            (SelectionStart, SelectionEnd) = (start, end);
    }
}
