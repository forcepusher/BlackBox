using Sloperator.Core.Settings;
using Xunit;

namespace Sloperator.Core.Tests;

/// <summary>
/// The chat window with a tool's panel open, made narrower: the panel gives way to its least before
/// the chat does, and the window stops where both are at their least. The panel kept the 960 it
/// opened at, and the chat was squeezed to a few letters across.
/// </summary>
public class SidePanelColumnsTests
{
    [Theory]
    [InlineData(SidePanelColumns.PanelWidth, 770)] // Video generator
    [InlineData(SidePanelColumns.PanelWidth, 560)] // File changes
    [InlineData(SidePanelColumns.PanelWidth, 420)] // File inspector
    public void At_the_least_width_with_a_panel_open_both_keep_their_least(double opened, double least)
    {
        var window = SidePanelColumns.WindowMin(least);
        var panel = SidePanelColumns.Panel(window, opened, least);
        Assert.Equal(least, panel);
        Assert.Equal(SidePanelColumns.ChatMin, window - panel);
    }

    [Fact]
    public void Made_narrower_the_panel_gives_way_first_and_takes_its_width_back_after()
    {
        // Wide enough for both: the panel as it opened.
        Assert.Equal(960, SidePanelColumns.Panel(1900, 960, 770));
        // Narrower: the chat stays at its least and the panel shrinks.
        Assert.Equal(1500 - SidePanelColumns.ChatMin, SidePanelColumns.Panel(1500, 960, 770));
        // Wider again: back to as it opened, never more.
        Assert.Equal(960, SidePanelColumns.Panel(2400, 960, 770));
        // A panel dragged narrower than that stays as dragged while there is room.
        Assert.Equal(800, SidePanelColumns.Panel(1500, 800, 770));
    }

    [Fact]
    public void A_window_narrower_than_both_leaves_the_panel_its_least()
    {
        // A screen too narrow for both: the panel can't go under its least, the chat has what is left.
        Assert.Equal(770, SidePanelColumns.Panel(1300, 960, 770));
    }
}
