namespace Sloperator.Core.Documents;

/// <summary>
/// A PDF opened to be drawn page by page (see <see cref="PdfPages"/>). Safe to call from several
/// threads: the panel draws pages in the background while another window draws its own.
/// </summary>
public interface IPdfDocument : IDisposable
{
    /// <summary>Each page's size in points (1/72 inch).</summary>
    IReadOnlyList<(double Width, double Height)> Sizes { get; }

    /// <summary>
    /// Page <paramref name="index"/> (from 0) drawn <paramref name="pageWidth"/> by <paramref name="pageHeight"/>
    /// pixels on white, with its annotations, of which the part <paramref name="x"/>, <paramref name="y"/>,
    /// <paramref name="width"/>, <paramref name="height"/> alone: BGRA rows from the top, premultiplied
    /// (opaque). Null once the document is closed.
    /// </summary>
    (byte[] Bgra, int Width, int Height)? Draw(int index, int pageWidth, int pageHeight, int x, int y, int width, int height);
}

/// <summary>
/// PDFs drawn page by page for the File inspector panel: Sloperator draws a PDF itself, whatever the
/// PC has for PDFs. Windows' previewer for PDFs was the one the PDF's program registered, and Acrobat's
/// failed on every PDF on the laptop (2026-10-25). The platform does the drawing (<see cref="Opener"/>):
/// on Windows, Windows' own PDF renderer.
/// </summary>
public static class PdfPages
{
    /// <summary>Opens a PDF by its path; set by the platform, null where it draws none.</summary>
    public static Func<string, IPdfDocument>? Opener { get; set; }

    /// <summary>
    /// Opens <paramref name="path"/>; throws <see cref="InvalidDataException"/> for a file that can't be
    /// read as a PDF (damaged, password-protected) and <see cref="NotSupportedException"/> where the
    /// platform draws no PDFs.
    /// </summary>
    public static IPdfDocument Open(string path) =>
        (Opener ?? throw new NotSupportedException("This system has nothing to draw PDF pages with."))(path);

    /// <summary>Page <paramref name="index"/> (from 0), <paramref name="width"/> pixels wide; null for no such page, or once the document is closed.</summary>
    public static (byte[] Bgra, int Width, int Height)? Render(this IPdfDocument pdf, int index, int width)
    {
        if (index < 0 || index >= pdf.Sizes.Count)
            return null;
        var (w, h) = pdf.Sizes[index];
        width = Math.Max(1, width);
        var height = Math.Max(1, (int)Math.Round(width * h / Math.Max(1, w)));
        return pdf.Draw(index, width, height, 0, 0, width, height);
    }

    /// <summary>
    /// An area of page <paramref name="index"/> alone, as sharp as <paramref name="longSide"/> pixels
    /// along its longer side make it (as least as sharp as the panel draws the page, at most 600 dpi):
    /// for the model, a picture of what the user marked. Null for no such page, or once the document is closed.
    /// </summary>
    public static (byte[] Bgra, int Width, int Height)? RenderArea(this IPdfDocument pdf, int index, DocumentArea area, int longSide, int leastPageWidth)
    {
        if (index < 0 || index >= pdf.Sizes.Count)
            return null;
        var (w, h) = pdf.Sizes[index];
        w = Math.Max(1, w);
        h = Math.Max(1, h);
        // Pixels per point: the area's longer side at longSide, between the panel's and 600 dpi.
        var scale = longSide / Math.Max((area.Right - area.Left) * w, (area.Bottom - area.Top) * h);
        scale = Math.Clamp(scale, leastPageWidth / w, 600 / 72.0);
        var (pageWidth, pageHeight) = ((int)Math.Round(w * scale), (int)Math.Round(h * scale));
        var (x, y, width, height) = area.Pixels(pageWidth, pageHeight);
        return pdf.Draw(index, pageWidth, pageHeight, x, y, width, height);
    }
}
