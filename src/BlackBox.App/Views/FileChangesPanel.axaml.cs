using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;
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
        // Ahead of the line's own handling of the press, which picks it; moves and the release come
        // after the line took them.
        DiffList.AddHandler(PointerPressedEvent, OnDiffPressed, RoutingStrategies.Tunnel);
        DiffList.AddHandler(PointerMovedEvent, OnDiffMoved, RoutingStrategies.Bubble, handledEventsToo: true);
        DiffList.AddHandler(PointerReleasedEvent, (_, _) => EndDrag(), RoutingStrategies.Bubble, handledEventsToo: true);
        DiffList.AddHandler(PointerCaptureLostEvent, (_, _) => EndDrag(), RoutingStrategies.Bubble, handledEventsToo: true);
        // Scrolled by hand (the wheel, keys), the diff stays where the user took it: no more watching.
        DiffList.AddHandler(PointerWheelChangedEvent, (_, _) => _vm?.StopWatching(), RoutingStrategies.Tunnel);
        DiffList.AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key is Key.PageUp or Key.PageDown or Key.Home or Key.End or Key.Up or Key.Down)
                _vm?.StopWatching();
        }, RoutingStrategies.Tunnel);
        _dragScroll = new DispatcherTimer(TimeSpan.FromMilliseconds(30), DispatcherPriority.Input, (_, _) => DragScroll());
    }

    // ---- picking lines by a drag -----------------------------------------------------------------

    /// <summary>The row the drag started on; -1 with no button down on a line.</summary>
    private int _dragFrom = -1;

    /// <summary>Whether the pointer has left the row it was pressed on: till then it is a click.</summary>
    private bool _dragging;

    /// <summary>What was picked before a Ctrl+drag, which adds to it; nothing for a plain drag.</summary>
    private IReadOnlyList<int> _dragKept = [];

    /// <summary>Where the pointer is, over the diff; above or below it, the diff scrolls.</summary>
    private Point _dragAt;

    /// <summary>Where the button went down, over the diff.</summary>
    private Point _dragPressedAt;

    private readonly DispatcherTimer _dragScroll;

    /// <summary>
    /// A press on a line may start a drag, which picks every line between as it goes, as a Shift+click
    /// there would. Shift held is a Shift+click, no drag; Ctrl held adds the drag to what was picked.
    /// </summary>
    private void OnDiffPressed(object? sender, PointerPressedEventArgs e)
    {
        EndDrag();
        // The scrollbar, taken by hand: no more watching.
        if ((e.Source as Visual)?.FindAncestorOfType<ScrollBar>(includeSelf: true) is not null)
            _vm?.StopWatching();
        var point = e.GetCurrentPoint(DiffList);
        if (!point.Properties.IsLeftButtonPressed || (e.KeyModifiers & KeyModifiers.Shift) != 0 ||
            (e.Source as Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true) is not { } item || _vm is null)
            return;
        _dragFrom = DiffList.IndexFromContainer(item);
        _dragKept = (e.KeyModifiers & KeyModifiers.Control) != 0 ? _vm.Selection.SelectedIndexes.ToList() : [];
        _dragAt = _dragPressedAt = point.Position;
    }

    private void OnDiffMoved(object? sender, PointerEventArgs e)
    {
        if (_dragFrom < 0)
            return;
        if (!e.GetCurrentPoint(DiffList).Properties.IsLeftButtonPressed)
        {
            EndDrag();
            return;
        }
        _dragAt = e.GetPosition(DiffList);
        var row = RowAt(_dragAt.Y);
        if (!_dragging)
        {
            // A row pressed half in sight is scrolled into it, moving the rows under a pointer that
            // stays still: only the pointer's own move starts a drag.
            if (row == _dragFrom || row < 0 || Math.Abs(_dragAt.Y - _dragPressedAt.Y) < 4)
                return;
            _dragging = true;
            // Held by the list, the drag goes on past its edges and over other controls.
            e.Pointer.Capture(DiffList);
            _dragScroll.Start();
        }
        if (row >= 0)
            _vm?.PickDrag(_dragFrom, row, _dragKept);
    }

    /// <summary>Above or below the diff while dragging, it scrolls, faster the further away, and the pick follows.</summary>
    private void DragScroll()
    {
        if (!_dragging || DiffList.FindDescendantOfType<ScrollViewer>() is not { } scroll)
            return;
        var height = DiffList.Bounds.Height;
        var past = _dragAt.Y < 0 ? _dragAt.Y : _dragAt.Y > height ? _dragAt.Y - height : 0;
        if (past == 0)
            return;
        scroll.Offset = scroll.Offset.WithY(Math.Clamp(scroll.Offset.Y + Math.Sign(past) * Math.Min(4 + Math.Abs(past) / 2, 80), 0, Math.Max(0, scroll.Extent.Height - scroll.Viewport.Height)));
        DiffList.UpdateLayout();
        if (RowAt(_dragAt.Y) is var row and >= 0)
            _vm?.PickDrag(_dragFrom, row, _dragKept);
    }

    /// <summary>The row at this height over the diff; past its top or bottom, the first or last row shown.</summary>
    private int RowAt(double y)
    {
        var best = -1;
        var bestDistance = double.MaxValue;
        foreach (var container in DiffList.GetRealizedContainers())
        {
            if (!container.IsVisible || container.TranslatePoint(default, DiffList) is not { } top)
                continue;
            var bottom = top.Y + container.Bounds.Height;
            // Only rows in sight: those kept realized above and below don't count.
            if (bottom <= 0 || top.Y >= DiffList.Bounds.Height)
                continue;
            var distance = y < top.Y ? top.Y - y : y >= bottom ? y - bottom : 0;
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = DiffList.IndexFromContainer(container);
            }
        }
        return best;
    }

    private void EndDrag()
    {
        _dragFrom = -1;
        _dragging = false;
        _dragKept = [];
        _dragScroll.Stop();
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
        {
            _vm.GoToRow -= OnGoToRow;
            _vm.ShowRow -= OnShowRow;
        }
        _vm = DataContext as FileChangesViewModel;
        if (_vm is not null)
        {
            _vm.GoToRow += OnGoToRow;
            _vm.ShowRow += OnShowRow;
        }
    }

    /// <summary>
    /// Watching, the agent changed this row: left as it is in the upper half of the diff, else brought a
    /// quarter of the way down, so a little before the change and more of what follows it show.
    /// </summary>
    private void OnShowRow(int row)
    {
        // After the rows are laid out: they were just replaced.
        Dispatcher.UIThread.Post(() =>
        {
            if (_vm is null || row >= _vm.Rows.Count)
                return;
            if (Shown(row) is { } top && top >= 0 && top <= DiffList.Bounds.Height / 2)
                return;
            DiffList.ScrollIntoView(row);
            // Once the rows it brought are laid out, and the list's height with them.
            Dispatcher.UIThread.Post(() =>
            {
                if (Shown(row) is { } at && DiffList.FindDescendantOfType<ScrollViewer>() is { } scroll)
                    scroll.Offset = scroll.Offset.WithY(Math.Clamp(scroll.Offset.Y + at - DiffList.Bounds.Height / 4, 0, Math.Max(0, scroll.Extent.Height - scroll.Viewport.Height)));
            }, DispatcherPriority.Background);
        }, DispatcherPriority.Background);

        // Where the row's top is over the diff; null when it isn't laid out.
        double? Shown(int index) =>
            DiffList.ContainerFromIndex(index) is { IsVisible: true } container && container.TranslatePoint(default, DiffList) is { } point ? point.Y : null;
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
        if (FileList.SelectedItem is DiffFileItem file && _vm is { Reselecting: false })
        {
            // Gone to by hand: watching would take it away again.
            _vm.StopWatching();
            _vm.GoToCommand.Execute(file);
        }
    }

    /// <summary>
    /// A right-click on a file: Show in folder, in the look of the Video generator's menus. A deleted
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
