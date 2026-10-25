using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using BlackBox.Core.Documents;
using CommunityToolkit.Mvvm.ComponentModel;

namespace BlackBox.App.ViewModels;

/// <summary>
/// A page of a PDF in the Inspect file panel (see <see cref="PdfPages"/>): drawn when it comes
/// into the panel's list and let go of when it leaves it, so a PDF of hundreds of pages holds only the
/// few on screen (<see cref="Views.PdfPageView"/>).
/// </summary>
public sealed partial class PdfPageItem(PdfPages pdf, int index) : ObservableObject
{
    /// <summary>How wide pages are drawn, in pixels: sharp across the widest panel on a 125% screen.</summary>
    public const int RenderWidth = 1400;

    public int Index { get; } = index;

    /// <summary>Its height over its width.</summary>
    public double Aspect { get; } = pdf.Sizes[index].Height / Math.Max(1, pdf.Sizes[index].Width);

    [ObservableProperty]
    private Bitmap? _image;

    /// <summary>On screen: it is to be drawn; false again when it leaves, so a drawing that comes late is let go of.</summary>
    private bool _wanted;

    private bool _drawing;

    /// <summary>It came into the panel's list: drawn off the UI thread, if not drawn yet.</summary>
    public void Load()
    {
        _wanted = true;
        if (Image is not null || _drawing)
            return;
        _drawing = true;
        Task.Run(() =>
        {
            Bitmap? bitmap = null;
            try
            {
                if (pdf.Render(Index, RenderWidth) is { } page)
                    bitmap = ToBitmap(page.Bgra, page.Width, page.Height);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException) { /* a page PDFium can't draw stays white */ }
            Dispatcher.UIThread.Post(() =>
            {
                _drawing = false;
                if (_wanted && Image is null)
                    Image = bitmap;
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

    /// <summary>A page drawn by PDFium as a bitmap; with <paramref name="height"/> less than its own, its top only.</summary>
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
