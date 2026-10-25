using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using BlackBox.App.ViewModels;

namespace BlackBox.App.Views;

/// <summary>The File changes panel, on the left of the chat window: the files the agent changed, and their diff, whose lines are picked by a click.</summary>
public partial class FileChangesPanel : UserControl
{
    public FileChangesPanel()
    {
        InitializeComponent();
        // Lines and files take their own clicks; what gets here is a click away from them.
        AddHandler(PointerPressedEvent, OnAwayPressed);
    }

    /// <summary>
    /// A click in the panel away from its lines (below the last, on the bar at the top, on the list
    /// of files but not on a file) unpicks them, as Esc does. A click on a file only goes there, so
    /// lines are picked in one file and Ctrl+clicked in another; a click in the chat keeps them, as
    /// the box is clicked to ask about them. Shift or Ctrl held keeps them too: those add.
    /// </summary>
    private void OnAwayPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Handled || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed || (e.KeyModifiers & (KeyModifiers.Shift | KeyModifiers.Control)) != 0)
            return;
        _vm?.Unpick();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (!e.Handled && e.Key == Key.Escape && _vm?.Unpick() == true)
            e.Handled = true;
    }

    private FileChangesViewModel? _vm;

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm is not null)
            _vm.GoToRow -= OnGoToRow;
        _vm = DataContext as FileChangesViewModel;
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

    /// <summary>
    /// A right-click on a file: Show in folder, in the look of the Video editor's menus. A deleted
    /// file has nothing to show, so the entry is greyed out.
    /// </summary>
    private void OnFileMenu(object? sender, ContextRequestedEventArgs e)
    {
        if ((e.Source as Control)?.FindLogicalAncestorOfType<ListBoxItem>(includeSelf: true) is not { DataContext: DiffFileItem file } item)
            return;
        e.Handled = true;
        var show = new MenuItem { Header = "Show in folder", IsEnabled = File.Exists(file.FullPath) };
        show.Click += (_, _) => FilePreview.ShowInFolder(file.FullPath);
        var menu = new ContextMenu();
        menu.Items.Add(show);
        menu.Open(item);
    }
}
