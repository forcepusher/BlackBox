using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Sloperator.Core.Documents;
using Sloperator.Core.Media;
using SkiaSharp;
using static Sloperator.Platform.Windows.ComAbi;

namespace Sloperator.Platform.Windows;

/// <summary>
/// A PDF drawn by Windows' own renderer (Windows.Data.Pdf, in every Windows 10 and 11), called through
/// WinRT's ABI (<see cref="ComAbi"/>). Until 2026-10-29 PDFium drew the pages, 7.3 MB of native
/// library (3.6 MB of the single-file exe). A page comes back as a BMP, which Skia decodes.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed unsafe class WindowsPdf : IPdfDocument
{
    // Interface ids, from Windows' own metadata (System32\WinMetadata).
    private static readonly Guid IPdfDocumentStatics = new("433A0B5F-C007-4788-90F2-08143D922599");
    private static readonly Guid IPdfPageRenderOptions = new("3C98056F-B7CF-4C29-9A04-52D90267F425");
    private static readonly Guid IBitmapEncoderStatics = new("A74356A7-A4E4-4EB9-8E40-564DE7E1CCB2");
    private static readonly Guid IRandomAccessStream = new("905A0FE1-BC53-11DF-8C49-001E4FC686DA");

    /// <summary>A wrong password, or none for a protected PDF.</summary>
    private const int ErrorWrongPassword = unchecked((int)0x8007052B);

    private readonly Lock _gate = new();
    private nint _document, _stream;

    public IReadOnlyList<(double Width, double Height)> Sizes { get; }

    private WindowsPdf(nint document, nint stream, List<(double, double)> sizes)
    {
        _document = document;
        _stream = stream;
        Sizes = sizes;
    }

    /// <summary>Opens <paramref name="path"/>, from memory: the file stays free to be written over while it is shown.</summary>
    public static IPdfDocument Open(string path)
    {
        Start();
        byte[] data;
        using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            data = new byte[file.Length];
            file.ReadExactly(data);
        }
        nint memory = 0, stream = 0, statics = 0, operation = 0, document = 0;
        try
        {
            fixed (byte* bytes = data)
                memory = SHCreateMemStream(bytes, (uint)data.Length);
            if (memory == 0)
                throw new OutOfMemoryException();
            Check(CreateRandomAccessStreamOverStream(memory, 0, in IRandomAccessStream, out stream));
            statics = Factory("Windows.Data.Pdf.PdfDocument", IPdfDocumentStatics);
            // IPdfDocumentStatics: LoadFromStreamAsync(stream) 8.
            Check(((delegate* unmanaged[Stdcall]<nint, nint, nint*, int>)Slot(statics, 8))(statics, stream, &operation));
            try
            {
                Wait(operation);
                // IAsyncOperation<PdfDocument>: GetResults 8.
                Check(((delegate* unmanaged[Stdcall]<nint, nint*, int>)Slot(operation, 8))(operation, &document));
            }
            catch (Exception ex) when (ex is COMException or ArgumentException or UnauthorizedAccessException or InvalidCastException)
            {
                throw new InvalidDataException(ex.HResult == ErrorWrongPassword ? "It is protected by a password." : "It is not a PDF, or it is damaged.", ex);
            }
            // IPdfDocument: GetPage 6, get_PageCount 7.
            uint count;
            Check(((delegate* unmanaged[Stdcall]<nint, uint*, int>)Slot(document, 7))(document, &count));
            var sizes = new List<(double, double)>((int)count);
            for (uint i = 0; i < count; i++)
            {
                var page = Page(document, i);
                try
                {
                    // In device-independent pixels (1/96 inch); points are 1/72.
                    var size = SizeOf(page);
                    sizes.Add((Math.Round(size.Width * 0.75, 2), Math.Round(size.Height * 0.75, 2)));
                }
                finally { Release(page); }
            }
            var pdf = new WindowsPdf(document, stream, sizes);
            (document, stream) = (0, 0);
            return pdf;
        }
        finally
        {
            Release(document);
            Release(operation);
            Release(statics);
            Release(stream);
            Release(memory);
        }
    }

    public (byte[] Bgra, int Width, int Height)? Draw(int index, int pageWidth, int pageHeight, int x, int y, int width, int height)
    {
        lock (_gate)
        {
            if (_document == 0 || index < 0 || index >= Sizes.Count || width <= 0 || height <= 0)
                return null;
            Start();
            nint page = 0, inspectable = 0, options = 0, memory = 0, stream = 0, action = 0;
            try
            {
                page = Page(_document, (uint)index);
                var size = SizeOf(page);
                var (sx, sy) = (pageWidth / Math.Max(0.01, size.Width), pageHeight / Math.Max(0.01, size.Height));
                inspectable = Activate("Windows.Data.Pdf.PdfPageRenderOptions");
                options = Query(inspectable, IPdfPageRenderOptions);
                // IPdfPageRenderOptions: put_SourceRect 7, put_DestinationWidth 9, put_DestinationHeight 11,
                // put_BackgroundColor 13, put_BitmapEncoderId 17. The source is in the page's own units.
                Check(((delegate* unmanaged[Stdcall]<nint, Rect, int>)Slot(options, 7))(options, new Rect((float)(x / sx), (float)(y / sy), (float)(width / sx), (float)(height / sy))));
                Check(((delegate* unmanaged[Stdcall]<nint, uint, int>)Slot(options, 9))(options, (uint)width));
                Check(((delegate* unmanaged[Stdcall]<nint, uint, int>)Slot(options, 11))(options, (uint)height));
                Check(((delegate* unmanaged[Stdcall]<nint, Color, int>)Slot(options, 13))(options, new Color { A = 255, R = 255, G = 255, B = 255 }));
                Check(((delegate* unmanaged[Stdcall]<nint, Guid, int>)Slot(options, 17))(options, BmpEncoder.Value));
                memory = SHCreateMemStream(null, 0);
                if (memory == 0)
                    throw new OutOfMemoryException();
                Check(CreateRandomAccessStreamOverStream(memory, 0, in IRandomAccessStream, out stream));
                // IPdfPage: RenderToStreamAsync(stream, options) 7; IAsyncAction: GetResults 8.
                Check(((delegate* unmanaged[Stdcall]<nint, nint, nint, nint*, int>)Slot(page, 7))(page, stream, options, &action));
                Wait(action);
                Check(((delegate* unmanaged[Stdcall]<nint, int>)Slot(action, 8))(action));
                return Decode(ReadAll(memory), width, height);
            }
            finally
            {
                Release(action);
                Release(stream);
                Release(memory);
                Release(options);
                Release(inspectable);
                Release(page);
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            Release(_document);
            Release(_stream);
            (_document, _stream) = (0, 0);
        }
    }

    /// <summary>The page as BGRA, premultiplied, exactly <paramref name="width"/> by <paramref name="height"/>.</summary>
    private static (byte[], int, int)? Decode(byte[] bmp, int width, int height)
    {
        using var decoded = SKBitmap.Decode(bmp);
        if (decoded is null)
            return null;
        using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.White);
            canvas.DrawBitmap(decoded, new SKRect(0, 0, width, height), Sampling.High(decoded.Width, width));
        }
        var bgra = new byte[width * height * 4];
        for (var row = 0; row < height; row++)
            Marshal.Copy(bitmap.GetPixels() + row * bitmap.RowBytes, bgra, row * width * 4, width * 4);
        return (bgra, width, height);
    }

    /// <summary>What was written into an IStream, from its start.</summary>
    private static byte[] ReadAll(nint stream)
    {
        // IStream: Read 3, Seek 5.
        ulong end;
        Check(((delegate* unmanaged[Stdcall]<nint, long, uint, ulong*, int>)Slot(stream, 5))(stream, 0, 2, &end));
        Check(((delegate* unmanaged[Stdcall]<nint, long, uint, ulong*, int>)Slot(stream, 5))(stream, 0, 0, null));
        var data = new byte[checked((int)end)];
        fixed (byte* p = data)
        {
            uint read;
            Check(((delegate* unmanaged[Stdcall]<nint, byte*, uint, uint*, int>)Slot(stream, 3))(stream, p, (uint)data.Length, &read));
            return read == data.Length ? data : data[..(int)read];
        }
    }

    /// <summary>The id of Windows' BMP encoder, for the pages: the cheapest to write and to read back.</summary>
    private static readonly Lazy<Guid> BmpEncoder = new(() =>
    {
        var statics = Factory("Windows.Graphics.Imaging.BitmapEncoder", IBitmapEncoderStatics);
        try
        {
            // IBitmapEncoderStatics: get_BmpEncoderId 6.
            Guid id;
            Check(((delegate* unmanaged[Stdcall]<nint, Guid*, int>)Slot(statics, 6))(statics, &id));
            return id;
        }
        finally { Release(statics); }
    });

    private static nint Page(nint document, uint index)
    {
        nint page;
        Check(((delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)Slot(document, 6))(document, index, &page));
        return page;
    }

    private static Size SizeOf(nint page)
    {
        // IPdfPage: get_Size 10.
        Size size;
        Check(((delegate* unmanaged[Stdcall]<nint, Size*, int>)Slot(page, 10))(page, &size));
        return size;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Size { public float Width, Height; }

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct Rect(float X, float Y, float Width, float Height);

    [StructLayout(LayoutKind.Sequential)]
    private struct Color { public byte A, R, G, B; }

    [DllImport("shlwapi.dll")] private static extern nint SHCreateMemStream(byte* data, uint size);
    [DllImport("shcore.dll")] private static extern int CreateRandomAccessStreamOverStream(nint stream, int options, in Guid iid, out nint result);
}
