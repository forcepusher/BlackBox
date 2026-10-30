using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Sloperator.App.Views;
using Xunit;

namespace Sloperator.App.Tests;

/// <summary>
/// History's and Servers' lists hang under their button with their right edge on the button's right
/// edge, growing leftwards, whether the list is wider or narrower than the button; by a window at the
/// screen's left edge the list moves along to stay on the screen. The dropdowns hang from the left.
/// </summary>
public sealed class ListPlacementTests
{
    /// <summary>The list's box and its button's, in screen pixels: (left, top, right) each.</summary>
    private sealed record Edges(int ListLeft, int ListTop, int ListRight, int ButtonLeft, int ButtonBottom, int ButtonRight);

    private static Edges Open(Button button, FlyoutBase flyout)
    {
        flyout.ShowAt(button);
        Dispatcher.UIThread.RunJobs();
        var content = (Control)((Flyout)flyout).Content!;
        var presenter = content.FindAncestorOfType<FlyoutPresenter>()!;
        presenter.UpdateLayout();
        var listTopLeft = presenter.PointToScreen(new Point(0, 0));
        var listRight = presenter.PointToScreen(new Point(presenter.Bounds.Width, 0)).X;
        var buttonTopLeft = button.PointToScreen(new Point(0, 0));
        var buttonBottomRight = button.PointToScreen(new Point(button.Bounds.Width, button.Bounds.Height));
        flyout.Hide();
        Dispatcher.UIThread.RunJobs();
        return new Edges(listTopLeft.X, listTopLeft.Y, listRight, buttonTopLeft.X, buttonBottomRight.Y, buttonBottomRight.X);
    }

    [Fact]
    public void History_and_Servers_hang_from_the_right_and_the_dropdowns_from_the_left()
    {
        var (lists, dropdowns) = HeadlessApp.Run(() =>
        {
            // The window's own markup in a plain window: MainWindow itself wants its view model to close.
            var main = new MainWindow();
            var content = (Control)main.Content!;
            main.Content = null;
            var window = new Window { Width = 1000, Height = 760, Content = content };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            var lists = new[] { "HistoryButton", "SavedServersButton" }
                .Select(name => ((PopupFlyoutBase)main.FindControl<Button>(name)!.Flyout!).Placement).ToList();
            var dropdowns = content.GetVisualDescendants().OfType<ComboBox>()
                .Select(box => box.GetVisualDescendants().OfType<Popup>().First(p => p.Name == "PART_Popup").Placement).ToList();
            window.Close();
            return (lists, dropdowns);
        });
        Assert.All(lists, placement => Assert.Equal(Dropdown.FromRight, placement));
        Assert.NotEmpty(dropdowns);
        Assert.All(dropdowns, placement => Assert.Equal(Dropdown.Placement, placement));
    }

    [Theory]
    [InlineData(100, 340)] // wider than its button, as History's
    [InlineData(200, 40)]  // narrower
    public void A_list_from_the_right_ends_on_its_buttons_right_edge(int buttonWidth, int listWidth)
    {
        var edges = HeadlessApp.Run(() => OpenInWindow(new PixelPoint(600, 100), HorizontalAlignment.Right, buttonWidth, listWidth));
        Assert.Equal(edges.ButtonRight, edges.ListRight);
        Assert.Equal(edges.ButtonBottom, edges.ListTop);
    }

    [Fact]
    public void By_the_screens_left_edge_a_list_from_the_right_stays_on_the_screen()
    {
        var edges = HeadlessApp.Run(() => OpenInWindow(new PixelPoint(0, 100), HorizontalAlignment.Left, 80, 340));
        Assert.True(edges.ListLeft >= 0, $"the list starts at {edges.ListLeft}, off the screen");
        Assert.Equal(edges.ButtonBottom, edges.ListTop);
    }

    private static Edges OpenInWindow(PixelPoint at, HorizontalAlignment side, int buttonWidth, int listWidth)
    {
        var button = new Button { Content = "History", Width = buttonWidth, Height = 32, HorizontalAlignment = side, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(8) };
        var flyout = new Flyout { Placement = Dropdown.FromRight, FlyoutPresenterClasses = { "list" }, Content = new Panel { Width = listWidth, Height = 60 } };
        var window = new Window { Width = 500, Height = 300, Position = at, Content = button };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        var edges = Open(button, flyout);
        window.Close();
        return edges;
    }
}
