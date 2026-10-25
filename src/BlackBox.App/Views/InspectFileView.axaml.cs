using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using BlackBox.App.ViewModels;

namespace BlackBox.App.Views;

/// <summary>
/// A file shown whole, in the Inspect file panel on the left of the chat window or in the full-screen
/// viewer: text selected in it goes into the chat's box (in the panel; the viewer's file has no chat).
/// </summary>
public partial class InspectFileView : UserControl
{
    public InspectFileView()
    {
        InitializeComponent();
        // Windows' previewer couldn't show it: the file's text instead.
        Shell.Failed += why => (DataContext as InspectFileViewModel)?.ShellFailed(why);
        // The text takes the presses and releases for selecting: these see them anyway.
        AddHandler(PointerPressedEvent, OnPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(PointerReleasedEvent, OnReleased, handledEventsToo: true);
        AddHandler(KeyUpEvent, OnKeyUp, handledEventsToo: true);
    }

    private InspectFileViewModel? _vm;

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm is not null)
            _vm.Unpicked -= ClearSelection;
        _vm = DataContext as InspectFileViewModel;
        if (_vm is not null)
            _vm.Unpicked += ClearSelection;
    }

    /// <summary>
    /// A press starts a selection in one block of the text: those in the others go, as the text keeps
    /// a selection that loses focus (so it stays marked while the user types about it).
    /// </summary>
    private void OnPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed || OnScrollBar(e))
            return;
        var pressed = (e.Source as Avalonia.Visual)?.FindAncestorOfType<SelectableTextBlock>(includeSelf: true);
        Document.ClearSelection(pressed);
        if (!ReferenceEquals(pressed, PlainText))
            PlainText.ClearSelection();
    }

    /// <summary>
    /// A drag over the text, a double-click on a word or a Shift+click: what is selected goes into the
    /// box. A click that selects nothing (on the text, beside it, on the bar at the top) takes it out,
    /// as Esc does; a click in the chat keeps it, as the box is clicked to ask about it.
    /// </summary>
    private void OnReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton == MouseButton.Left && !OnScrollBar(e))
            Picked();
    }

    /// <summary>A scroll bar dragged or clicked to see more of the file keeps the selection.</summary>
    private static bool OnScrollBar(RoutedEventArgs e) =>
        (e.Source as Avalonia.Visual)?.FindAncestorOfType<Avalonia.Controls.Primitives.ScrollBar>(includeSelf: true) is not null;

    /// <summary>Shift+arrows and Ctrl+A select too; Esc lets go.</summary>
    private void OnKeyUp(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            _vm?.Unpick();
            ClearSelection();
        }
        else if ((e.KeyModifiers & (KeyModifiers.Shift | KeyModifiers.Control)) != 0)
            Picked();
    }

    private void Picked()
    {
        if (_vm is null)
            return;
        if (_vm.Markdown is not null && Document.Selection is { } selected)
            _vm.SelectedInMarkdown(selected.Block, selected.Start, selected.End);
        else if (_vm.Text is not null && PlainText.SelectionStart != PlainText.SelectionEnd)
            _vm.SelectedInText(Math.Min(PlainText.SelectionStart, PlainText.SelectionEnd), Math.Max(PlainText.SelectionStart, PlainText.SelectionEnd));
        else
            _vm.Unpick();
    }

    private void ClearSelection()
    {
        Document.ClearSelection();
        PlainText.ClearSelection();
    }
}
