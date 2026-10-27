using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Sloperator.App.ViewModels;

namespace Sloperator.App.Views;

/// <summary>
/// A page of a PDF in the File inspector panel: as wide as the panel lets it be (never wider than it
/// was drawn), as tall as the page's shape makes that, white until its picture is in. It asks for its
/// picture while it is in the panel's list and lets it go when the list recycles it for another page.
/// Zoomed in, it is that much wider (the list then scrolls sideways too); an area marked on it is drawn over it.
/// </summary>
public sealed class PdfPageView : Control
{
    private PdfPageItem? _page;

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_page is not null)
        {
            _page.PropertyChanged -= OnPageChanged;
            _page.Unload();
        }
        _page = DataContext as PdfPageItem;
        if (_page is not null)
        {
            _page.PropertyChanged += OnPageChanged;
            if (VisualRoot is not null)
                _page.Load();
        }
        InvalidateMeasure();
        InvalidateVisual();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _page?.Load();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _page?.Unload();
    }

    private void OnPageChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PdfPageItem.Zoom))
            InvalidateMeasure();
        InvalidateVisual();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var scaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
        var most = PdfPageItem.RenderWidth / scaling * (_page?.Zoom ?? 1);
        var width = double.IsInfinity(availableSize.Width) ? most : Math.Min(most, availableSize.Width);
        return new Size(width, width * (_page?.Aspect ?? 1.294));
    }

    public override void Render(DrawingContext context)
    {
        var rect = new Rect(Bounds.Size);
        context.FillRectangle(Brushes.White, rect);
        if (_page?.Image is { } image)
            context.DrawImage(image, new Rect(image.Size), rect);
        if (_page?.Mark is { } mark)
            ZoomBox.DrawMark(context, new Rect(mark.Left * rect.Width, mark.Top * rect.Height, (mark.Right - mark.Left) * rect.Width, (mark.Bottom - mark.Top) * rect.Height));
    }
}
