using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace Sloperator.App.Views;

public enum CaptionGlyphKind { Minimize, Maximize, Restore, Close }

/// <summary>
/// A title bar button's sign in its 16 px box, in lines one screen pixel thick on whole pixels at
/// any scale, as Windows draws its own: a path's 1 px stroke is 1.25 px at 125% and blurs over two
/// rows. Coordinates are the mockup's (minimize M3,8 H13, maximize a 9x9 square at 3.5, close an X
/// from 4,4 to 12,12); a straight line's centre goes down to the pixel it falls in, so the minimize
/// line is the row under the box's middle and the squares run 3 to 13 at 100%, 4 to 16 at 125%.
/// </summary>
public sealed class CaptionGlyph : Control
{
    public static readonly StyledProperty<CaptionGlyphKind> KindProperty =
        AvaloniaProperty.Register<CaptionGlyph, CaptionGlyphKind>(nameof(Kind));

    public static readonly StyledProperty<IBrush?> ForegroundProperty = TextElement.ForegroundProperty.AddOwner<CaptionGlyph>();

    static CaptionGlyph() => AffectsRender<CaptionGlyph>(KindProperty, ForegroundProperty);

    public CaptionGlyphKind Kind
    {
        get => GetValue(KindProperty);
        set => SetValue(KindProperty, value);
    }

    public IBrush? Foreground
    {
        get => GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    private TopLevel? _top;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _top = TopLevel.GetTopLevel(this);
        if (_top is not null)
            _top.ScalingChanged += OnScalingChanged;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (_top is not null)
            _top.ScalingChanged -= OnScalingChanged;
        _top = null;
    }

    private void OnScalingChanged(object? sender, EventArgs e) => InvalidateVisual();

    public override void Render(DrawingContext context)
    {
        if (Foreground is not { } brush)
            return;
        var s = _top?.RenderScaling ?? 1;
        // In screen pixels: an edge to the nearest, a line's centre to the pixel it is in.
        int Edge(double v) => (int)Math.Round(v * s);
        int Line(double v) => (int)Math.Floor(v * s);
        void Fill(int x0, int y0, int x1, int y1) => context.FillRectangle(brush, new Rect(x0 / s, y0 / s, (x1 - x0) / s, (y1 - y0) / s));
        // A square's outline on the pixels left..right, top..bottom (inclusive).
        void Square(int left, int top, int right, int bottom)
        {
            Fill(left, top, right + 1, top + 1);
            Fill(left, bottom, right + 1, bottom + 1);
            Fill(left, top + 1, left + 1, bottom);
            Fill(right, top + 1, right + 1, bottom);
        }

        switch (Kind)
        {
            case CaptionGlyphKind.Minimize:
                Fill(Edge(3), Line(8), Edge(13), Line(8) + 1);
                break;
            case CaptionGlyphKind.Maximize:
                Square(Line(3.5), Line(3.5), Line(12.5), Line(12.5));
                break;
            case CaptionGlyphKind.Restore:
            {
                // The window in front (7x7 at 3.5,5.5) and the top and right of the one behind it (at 5.5,3.5).
                int left = Line(3.5), top = Line(5.5), right = Line(10.5), bottom = Line(12.5);
                int backLeft = Line(5.5), backTop = Line(3.5), backRight = Line(12.5), backBottom = Line(10.5);
                Square(left, top, right, bottom);
                Fill(backLeft, backTop, backRight + 1, backTop + 1);
                Fill(backRight, backTop + 1, backRight + 1, backBottom + 1);
                Fill(backLeft, backTop + 1, backLeft + 1, top);
                Fill(right + 1, backBottom, backRight, backBottom + 1);
                break;
            }
            case CaptionGlyphKind.Close:
            {
                // Slanted lines can't sit on whole pixels; one pixel thick, ends on the mockup's points.
                var pen = new Pen(brush, 1 / s);
                double a = Edge(4) / s, b = Edge(12) / s;
                context.DrawLine(pen, new Point(a, a), new Point(b, b));
                context.DrawLine(pen, new Point(b, a), new Point(a, b));
                break;
            }
        }
    }
}
