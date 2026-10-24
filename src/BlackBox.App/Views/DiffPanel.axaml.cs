using Avalonia.Controls;
using Avalonia.Threading;
using BlackBox.App.ViewModels;

namespace BlackBox.App.Views;

/// <summary>The Code changes panel, on the left of the chat window: the files the agent changed, and their diff, whose lines are picked by a click.</summary>
public partial class DiffPanel : UserControl
{
    public DiffPanel()
    {
        InitializeComponent();
    }

    private DiffViewModel? _vm;

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm is not null)
            _vm.GoToRow -= OnGoToRow;
        _vm = DataContext as DiffViewModel;
        if (_vm is not null)
            _vm.GoToRow += OnGoToRow;
    }

    /// <summary>A file in the list was clicked: its header at the top of the diff, as far as the list scrolls.</summary>
    private void OnGoToRow(int row)
    {
        if (_vm is null || row >= _vm.Rows.Count)
            return;
        // To the end first, then back to the row: brought into view from above, it would stand at the bottom.
        DiffList.ScrollIntoView(_vm.Rows.Count - 1);
        Dispatcher.UIThread.Post(() => DiffList.ScrollIntoView(row), DispatcherPriority.Background);
    }

    private void OnFilePicked(object? sender, SelectionChangedEventArgs e)
    {
        if (FileList.SelectedItem is DiffFileItem file)
            _vm?.GoToCommand.Execute(file);
    }
}
