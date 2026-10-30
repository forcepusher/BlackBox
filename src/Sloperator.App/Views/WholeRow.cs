using Avalonia;
using Avalonia.Controls;

namespace Sloperator.App.Views;

/// <summary>
/// A row that shows each of its children whole or not at all, never cut short with "…": left to
/// right, the first always, each next one only while it and every one before it fit in full. The
/// top bar's server status (the user, 2026-10-30): in a narrow window, or beside a long Side panel
/// name, the words go and the dot stays, its tooltip saying them.
/// </summary>
public class WholeRow : Panel
{
    /// <summary>Room to spare for layout rounding: a part as wide as what is left still fits.</summary>
    private const double Slack = 0.01;

    public WholeRow() => ClipToBounds = true;

    protected override Size MeasureOverride(Size availableSize)
    {
        double width = 0, height = 0;
        var fits = true;
        for (var i = 0; i < Children.Count; i++)
        {
            var child = Children[i];
            child.Measure(new Size(double.PositiveInfinity, availableSize.Height));
            if (i > 0 && width + child.DesiredSize.Width > availableSize.Width + Slack)
                fits = false;
            if (!fits || !child.IsVisible)
                continue;
            width += child.DesiredSize.Width;
            height = Math.Max(height, child.DesiredSize.Height);
        }
        return new Size(Math.Min(width, availableSize.Width), height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        double x = 0;
        var fits = true;
        for (var i = 0; i < Children.Count; i++)
        {
            var child = Children[i];
            var width = child.DesiredSize.Width;
            if (i > 0 && x + width > finalSize.Width + Slack)
                fits = false;
            // One that doesn't fit is put past the right edge, where the clip hides it.
            child.Arrange(new Rect(fits ? x : finalSize.Width, 0, width, finalSize.Height));
            if (fits && child.IsVisible)
                x += width;
        }
        return finalSize;
    }
}
