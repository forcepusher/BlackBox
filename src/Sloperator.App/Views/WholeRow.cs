using Avalonia;
using Avalonia.Controls;

namespace Sloperator.App.Views;

/// <summary>
/// A row that shows each of its children whole or not at all, never cut short with "…": left to
/// right, the first always, each next one only while it and every one before it fit in full. The
/// server status (the user, 2026-10-30): in a narrow window the words go and the dot stays, its
/// tooltip saying them. <see cref="FromRight"/> turns it round: laid against the right edge, the last
/// always, each one before it only while it and every one after it fit.
/// </summary>
public class WholeRow : Panel
{
    /// <summary>Room to spare for layout rounding: a part as wide as what is left still fits.</summary>
    private const double Slack = 0.01;

    public static readonly StyledProperty<bool> FromRightProperty =
        AvaloniaProperty.Register<WholeRow, bool>(nameof(FromRight));

    static WholeRow() => AffectsMeasure<WholeRow>(FromRightProperty);

    public WholeRow() => ClipToBounds = true;

    /// <summary>The last child kept first and the row against the right edge, where the clip still hides what doesn't fit.</summary>
    public bool FromRight
    {
        get => GetValue(FromRightProperty);
        set => SetValue(FromRightProperty, value);
    }

    /// <summary>The children in the order they are kept: the first of them always.</summary>
    private IEnumerable<Control> InOrder() => FromRight ? Enumerable.Reverse(Children) : Children;

    protected override Size MeasureOverride(Size availableSize)
    {
        double width = 0, height = 0;
        var fits = true;
        var first = true;
        foreach (var child in InOrder())
        {
            child.Measure(new Size(double.PositiveInfinity, availableSize.Height));
            if (!first && width + child.DesiredSize.Width > availableSize.Width + Slack)
                fits = false;
            first = false;
            if (!fits || !child.IsVisible)
                continue;
            width += child.DesiredSize.Width;
            height = Math.Max(height, child.DesiredSize.Height);
        }
        return new Size(Math.Min(width, availableSize.Width), height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        double used = 0;
        var fits = true;
        var first = true;
        foreach (var child in InOrder())
        {
            var width = child.DesiredSize.Width;
            if (!first && used + width > finalSize.Width + Slack)
                fits = false;
            first = false;
            // One that doesn't fit is put past the edge it is laid from, where the clip hides it.
            var x = FromRight ? (fits ? finalSize.Width - used - width : -width) : fits ? used : finalSize.Width;
            child.Arrange(new Rect(x, 0, width, finalSize.Height));
            if (fits && child.IsVisible)
                used += width;
        }
        return finalSize;
    }
}
