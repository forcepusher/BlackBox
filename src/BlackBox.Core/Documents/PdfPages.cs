using System.Runtime.InteropServices;

namespace BlackBox.Core.Documents;

/// <summary>
/// A PDF drawn page by page by PDFium, Chrome's PDF engine (pdfium.dll from bblanchon.PDFium), for the
/// File inspector panel: BlackBox draws a PDF itself, whatever the PC has for PDFs. Windows'
/// previewer for PDFs was the one the PDF's program registered, and Acrobat's failed on every PDF on
/// the laptop (2026-10-25). PDFium isn't safe to call from two threads at once: every call here holds
/// one lock, so several panels (windows) take turns.
/// </summary>
public sealed class PdfPages : IDisposable
{
    private static readonly object Lock = new();
    private static bool _started;

    private nint _document;

    /// <summary>The file's bytes, which PDFium reads from for as long as the document is open.</summary>
    private GCHandle _data;

    /// <summary>Each page's size in points (1/72 inch).</summary>
    public IReadOnlyList<(double Width, double Height)> Sizes { get; }

    private PdfPages(nint document, GCHandle data, List<(double, double)> sizes)
    {
        _document = document;
        _data = data;
        Sizes = sizes;
    }

    /// <summary>
    /// Opens <paramref name="path"/>; throws <see cref="InvalidDataException"/> for a file PDFium can't
    /// read as a PDF (damaged, password-protected) and <see cref="DllNotFoundException"/> without pdfium.dll.
    /// </summary>
    public static PdfPages Open(string path)
    {
        lock (Lock)
        {
            if (!_started)
            {
                FPDF_InitLibrary();
                _started = true;
            }
            // From memory, not by its path: PDFium on Windows takes paths in the ANSI code page, and
            // the file stays free to be written over while it is shown.
            byte[] data;
            using (var file = Tools.TextFiles.OpenRead(path))
            {
                data = new byte[file.Length];
                file.ReadExactly(data);
            }
            var pinned = GCHandle.Alloc(data, GCHandleType.Pinned);
            var document = FPDF_LoadMemDocument(pinned.AddrOfPinnedObject(), data.Length, null);
            if (document == 0)
            {
                pinned.Free();
                throw new InvalidDataException(FPDF_GetLastError() switch
                {
                    2 => "The file is not there or can't be opened.",
                    3 => "It is not a PDF, or it is damaged.",
                    4 => "It is protected by a password.",
                    _ => "PDFium couldn't read it.",
                });
            }
            var count = FPDF_GetPageCount(document);
            var sizes = new List<(double, double)>(count);
            for (var i = 0; i < count; i++)
                sizes.Add(FPDF_GetPageSizeByIndex(document, i, out var w, out var h) != 0 ? (w, h) : (612, 792));
            return new PdfPages(document, pinned, sizes);
        }
    }

    /// <summary>
    /// Page <paramref name="index"/> (from 0), <paramref name="width"/> pixels wide, on white, with its
    /// annotations (form fields, comments): BGRA rows from the top, premultiplied (opaque). Null once
    /// the document is closed.
    /// </summary>
    public (byte[] Bgra, int Width, int Height)? Render(int index, int width)
    {
        lock (Lock)
        {
            if (_document == 0 || index < 0 || index >= Sizes.Count)
                return null;
            var (w, h) = Sizes[index];
            width = Math.Max(1, width);
            var height = Math.Max(1, (int)Math.Round(width * h / Math.Max(1, w)));
            var page = FPDF_LoadPage(_document, index);
            if (page == 0)
                return null;
            var bitmap = FPDFBitmap_Create(width, height, 1);
            try
            {
                if (bitmap == 0)
                    return null;
                FPDFBitmap_FillRect(bitmap, 0, 0, width, height, 0xFFFFFFFF);
                FPDF_RenderPageBitmap(bitmap, page, 0, 0, width, height, 0, FPDF_ANNOT);
                var stride = FPDFBitmap_GetStride(bitmap);
                var buffer = FPDFBitmap_GetBuffer(bitmap);
                var bgra = new byte[width * height * 4];
                for (var row = 0; row < height; row++)
                    Marshal.Copy(buffer + row * stride, bgra, row * width * 4, width * 4);
                return (bgra, width, height);
            }
            finally
            {
                if (bitmap != 0)
                    FPDFBitmap_Destroy(bitmap);
                FPDF_ClosePage(page);
            }
        }
    }

    /// <summary>
    /// An area of page <paramref name="index"/> alone, as sharp as <paramref name="longSide"/> pixels
    /// along its longer side make it (as least as sharp as the panel draws the page, at most 600 dpi):
    /// for the model, a picture of what the user marked. The page is drawn at that scale into a bitmap
    /// of the area's size, PDFium leaving out all but the area. Null once the document is closed.
    /// </summary>
    public (byte[] Bgra, int Width, int Height)? RenderArea(int index, DocumentArea area, int longSide, int leastPageWidth)
    {
        lock (Lock)
        {
            if (_document == 0 || index < 0 || index >= Sizes.Count)
                return null;
            var (w, h) = Sizes[index];
            w = Math.Max(1, w);
            h = Math.Max(1, h);
            // Pixels per point: the area's longer side at longSide, between the panel's and 600 dpi.
            var scale = longSide / Math.Max((area.Right - area.Left) * w, (area.Bottom - area.Top) * h);
            scale = Math.Clamp(scale, leastPageWidth / w, 600 / 72.0);
            var (pageWidth, pageHeight) = ((int)Math.Round(w * scale), (int)Math.Round(h * scale));
            var (x, y, width, height) = area.Pixels(pageWidth, pageHeight);
            var page = FPDF_LoadPage(_document, index);
            if (page == 0)
                return null;
            var bitmap = FPDFBitmap_Create(width, height, 1);
            try
            {
                if (bitmap == 0)
                    return null;
                FPDFBitmap_FillRect(bitmap, 0, 0, width, height, 0xFFFFFFFF);
                FPDF_RenderPageBitmap(bitmap, page, -x, -y, pageWidth, pageHeight, 0, FPDF_ANNOT);
                var stride = FPDFBitmap_GetStride(bitmap);
                var buffer = FPDFBitmap_GetBuffer(bitmap);
                var bgra = new byte[width * height * 4];
                for (var row = 0; row < height; row++)
                    Marshal.Copy(buffer + row * stride, bgra, row * width * 4, width * 4);
                return (bgra, width, height);
            }
            finally
            {
                if (bitmap != 0)
                    FPDFBitmap_Destroy(bitmap);
                FPDF_ClosePage(page);
            }
        }
    }

    public void Dispose()
    {
        lock (Lock)
        {
            if (_document != 0)
                FPDF_CloseDocument(_document);
            if (_data.IsAllocated)
                _data.Free();
            _document = 0;
        }
    }

    private const int FPDF_ANNOT = 0x01;
    private const string Pdfium = "pdfium";

    [DllImport(Pdfium)] private static extern void FPDF_InitLibrary();
    [DllImport(Pdfium)] private static extern nint FPDF_LoadMemDocument(nint data, int size, [MarshalAs(UnmanagedType.LPUTF8Str)] string? password);
    [DllImport(Pdfium)] private static extern uint FPDF_GetLastError();
    [DllImport(Pdfium)] private static extern int FPDF_GetPageCount(nint document);
    [DllImport(Pdfium)] private static extern int FPDF_GetPageSizeByIndex(nint document, int index, out double width, out double height);
    [DllImport(Pdfium)] private static extern nint FPDF_LoadPage(nint document, int index);
    [DllImport(Pdfium)] private static extern void FPDF_ClosePage(nint page);
    [DllImport(Pdfium)] private static extern void FPDF_CloseDocument(nint document);
    [DllImport(Pdfium)] private static extern nint FPDFBitmap_Create(int width, int height, int alpha);
    [DllImport(Pdfium)] private static extern void FPDFBitmap_FillRect(nint bitmap, int left, int top, int width, int height, uint color);
    [DllImport(Pdfium)] private static extern void FPDF_RenderPageBitmap(nint bitmap, nint page, int x, int y, int width, int height, int rotate, int flags);
    [DllImport(Pdfium)] private static extern nint FPDFBitmap_GetBuffer(nint bitmap);
    [DllImport(Pdfium)] private static extern int FPDFBitmap_GetStride(nint bitmap);
    [DllImport(Pdfium)] private static extern void FPDFBitmap_Destroy(nint bitmap);
}
