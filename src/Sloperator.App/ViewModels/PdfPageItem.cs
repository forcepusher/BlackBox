using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Sloperator.Core.Documents;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Sloperator.App.ViewModels;

/// <summary>
/// A page of a PDF in the File inspector panel (see <see cref="PdfPages"/>): drawn when it comes
/// into the panel's list and let go of when it leaves it, so a PDF of hundreds of pages holds only the
/// few on screen (<see cref="Views.PdfPageView"/>).
/// </summary>
public sealed partial class PdfPageItem(IPdfDocument pdf, int index) : ObservableObject
{
    /// <summary>How wide pages are drawn, in pixels: sharp across the widest panel on a 125% screen.</summary>
    public const int RenderWidth = 1400;

    public int Index { get; } = index;

    /// <summary>Its height over its width.</summary>
    public double Aspect { get; } = pdf.Sizes[index].Height / Math.Max(1, pdf.Sizes[index].Width);

    [ObservableProperty]
    private Bitmap? _image;

    /// <summary>How much the panel zooms its pages in (1: as wide as the panel); drawn again sharper when it does.</summary>
    [ObservableProperty]
    private double _zoom = 1;

    /// <summary>The area marked on the page, in parts of it; null with none.</summary>
    [ObservableProperty]
    private DocumentArea? _mark;

    /// <summary>How wide it is drawn at the zoom: at most 3x <see cref="RenderWidth"/>, a page then some 50 MB.</summary>
    private int Wanted => (int)Math.Min(RenderWidth * 3, RenderWidth * Zoom);

    /// <summary>Zoomed in past what it was drawn at: drawn again, the picture before showing meanwhile.</summary>
    partial void OnZoomChanged(double value)
    {
        if (_wanted && Image is { } image && image.PixelSize.Width < Wanted * 0.9)
            Draw();
    }

    /// <summary>On screen: it is to be drawn; false again when it leaves, so a drawing that comes late is let go of.</summary>
    private bool _wanted;

    private bool _drawing;

    /// <summary>It came into the panel's list: drawn off the UI thread, if not drawn yet.</summary>
    public void Load()
    {
        _wanted = true;
        if (Image is not null)
            return;
        Draw();
    }

    private void Draw()
    {
        if (_drawing)
            return;
        _drawing = true;
        var width = Wanted;
        Task.Run(() =>
        {
            Bitmap? bitmap = null;
            try
            {
                if (pdf.Render(Index, width) is { } page)
                    bitmap = ToBitmap(page.Bgra, page.Width, page.Height);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException) { /* a page that can't be drawn stays white */ }
            Dispatcher.UIThread.Post(() =>
            {
                _drawing = false;
                if (_wanted && bitmap is not null && (Image is null || Image.PixelSize.Width < bitmap.PixelSize.Width))
                {
                    var old = Image;
                    Image = bitmap;
                    old?.Dispose();
                    // Zoomed in further while it was drawn.
                    if (bitmap.PixelSize.Width < Wanted * 0.9)
                        Draw();
                }
                else
                    bitmap?.Dispose();
            });
        });
    }

    /// <summary>It left the panel's list: its picture goes.</summary>
    public void Unload()
    {
        _wanted = false;
        var image = Image;
        Image = null;
        image?.Dispose();
    }

    /// <summary>A drawn page as a bitmap; with <paramref name="height"/> less than its own, its top only.</summary>
    internal static Bitmap ToBitmap(byte[] bgra, int width, int height)
    {
        var pinned = GCHandle.Alloc(bgra, GCHandleType.Pinned);
        try
        {
            return new Bitmap(PixelFormat.Bgra8888, AlphaFormat.Premul, pinned.AddrOfPinnedObject(), new PixelSize(width, height), new Vector(96, 96), width * 4);
        }
        finally
        {
            pinned.Free();
        }
    }

    public override string ToString() => $"Page {Index + 1}";
}
