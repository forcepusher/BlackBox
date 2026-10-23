using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using BlackBox.App.ViewModels;

namespace BlackBox.App.Views;

/// <summary>The Diff panel, on the left of the chat window: the changed files, and their diff, whose lines are picked by a click.</summary>
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

    private void OnRefKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            _vm?.ApplyRefCommand.Execute(null);
            e.Handled = true;
        }
    }

    /// <summary>The folders' menu: those opened before, and Choose a folder….</summary>
    private void OnFolderMenu(object? sender, RoutedEventArgs e)
    {
        if (_vm is not { } vm)
            return;
        var menu = new MenuFlyout { Placement = PlacementMode.BottomEdgeAlignedLeft };
        foreach (var folder in vm.Recent.Where(Directory.Exists))
        {
            var item = new MenuItem { Header = folder };
            item.Click += (_, _) => vm.Open(folder);
            menu.Items.Add(item);
        }
        if (menu.Items.Count > 0)
            menu.Items.Add(new Separator());
        var choose = new MenuItem { Header = "Choose a folder…" };
        choose.Click += async (_, _) => await ChooseAsync(vm);
        menu.Items.Add(choose);
        menu.ShowAt(FolderButton);
    }

    private async Task ChooseAsync(DiffViewModel vm)
    {
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage)
            return;
        var start = vm.Folder is { } current ? await storage.TryGetFolderFromPathAsync(current) : null;
        var picked = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "A folder with a git repository", SuggestedStartLocation = start });
        if (picked is [var folder] && folder.TryGetLocalPath() is { } path)
            vm.Open(path);
    }
}
