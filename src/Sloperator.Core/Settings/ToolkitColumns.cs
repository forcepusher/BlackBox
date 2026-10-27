namespace Sloperator.Core.Settings;

/// <summary>
/// How the chat window shares its width with a tool's panel on the left of the chat (Video generator,
/// File changes), in the UI's own units; a panel's widths count the splitter on its right. The panel
/// gives way first: shrunk, the window takes its width from the panel down to the panel's least, and
/// only then stops; the chat never gets narrower than its own least. The panel opened at 960 and kept
/// it: the window's least counted the panel as 480, so the chat was squeezed to a few letters across
/// with the fields of its top bar over each other.
/// </summary>
public static class ToolkitColumns
{
    /// <summary>How narrow the chat may get: the window's own least width, as set in its markup.</summary>
    public const double ChatMin = 660;

    /// <summary>The splitter between the panel and the chat.</summary>
    public const double Splitter = 4;

    /// <summary>The window's least width with a panel open that may get <paramref name="panelMin"/> narrow: both at their least.</summary>
    public static double WindowMin(double panelMin) => panelMin + ChatMin;

    /// <summary>
    /// The panel's width in a window <paramref name="window"/> wide: as wide as it opened or was
    /// dragged to (<paramref name="wanted"/>), as far as the chat keeps its least, and never under
    /// the panel's own least (<paramref name="panelMin"/>). A window narrower than both, on a small
    /// screen, leaves the panel its least and the chat what is left.
    /// </summary>
    public static double Panel(double window, double wanted, double panelMin) =>
        Math.Max(panelMin, Math.Min(wanted, window - ChatMin));
}
