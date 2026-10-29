using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Sloperator.Core.Documents;
using SkiaSharp;

namespace Sloperator.Platform.Windows;

/// <summary>
/// A PDF drawn by Windows' own renderer (Windows.Data.Pdf, in every Windows 10 and 11), called through
/// WinRT's ABI as MediaFoundation is through COM's: the .NET projection of WinRT would have changed the
/// target framework of every project and brought its own assemblies. Until 2026-10-29 PDFium drew the
/// pages, 7.3 MB of native library (3.6 MB of the single-file exe). A page comes back as a BMP, which
/// Skia decodes; the async calls are waited out by polling, as each is short.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed unsafe class WindowsPdf : IPdfDocument
{
    // Interface ids, from Windows' own metadata (System32\WinMetadata).
    private static readonly Guid IPdfDocumentStatics = new("433A0B5F-C007-4788-90F2-08143D922599");
    private static readonly Guid IPdfPageRenderOptions = new("3C98056F-B7CF-4C29-9A04-52D90267F425");
    private static readonly Guid IBitmapEncoderStatics = new("A74356A7-A4E4-4EB9-8E40-564DE7E1CCB2");
    private static readonly Guid IAsyncInfo = new("00000036-0000-0000-C000-000000000046");
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
        _ = CoInitializeEx(0, 0); // MTA where nothing else has been set up; an STA thread works too
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
            _ = CoInitializeEx(0, 0);
            nint page = 0, inspectable = 0, options = 0, memory = 0, stream = 0, action = 0;
            try
            {
                page = Page(_document, (uint)index);
                var size = SizeOf(page);
                var (sx, sy) = (pageWidth / Math.Max(0.01, size.Width), pageHeight / Math.Max(0.01, size.Height));
                Check(RoActivateInstance("Windows.Data.Pdf.PdfPageRenderOptions", out inspectable));
                Check(QueryInterface(inspectable, IPdfPageRenderOptions, out options));
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
            canvas.DrawBitmap(decoded, new SKRect(0, 0, width, height));
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

    /// <summary>Waits for a WinRT async operation or action to end; throws what it failed with.</summary>
    private static void Wait(nint async)
    {
        Check(QueryInterface(async, IAsyncInfo, out var info));
        try
        {
            // IAsyncInfo: get_Status 7 (0 started, 1 completed, 2 canceled, 3 error), get_ErrorCode 8.
            for (var spins = 0; ; spins++)
            {
                int status;
                Check(((delegate* unmanaged[Stdcall]<nint, int*, int>)Slot(info, 7))(info, &status));
                if (status == 1)
                    return;
                if (status == 2)
                    throw new OperationCanceledException();
                if (status == 3)
                {
                    int error;
                    Check(((delegate* unmanaged[Stdcall]<nint, int*, int>)Slot(info, 8))(info, &error));
                    Marshal.ThrowExceptionForHR(error);
                }
                if (spins < 20) Thread.Yield();
                else Thread.Sleep(1);
            }
        }
        finally { Release(info); }
    }

    private static nint Factory(string runtimeClass, Guid iid)
    {
        var name = HString(runtimeClass);
        try
        {
            Check(RoGetActivationFactory(name, in iid, out var factory));
            return factory;
        }
        finally { _ = WindowsDeleteString(name); }
    }

    private static int RoActivateInstance(string runtimeClass, out nint instance)
    {
        var name = HString(runtimeClass);
        try { return RoActivateInstance(name, out instance); }
        finally { _ = WindowsDeleteString(name); }
    }

    /// <summary>A WinRT string (.NET no longer marshals them), freed with WindowsDeleteString.</summary>
    private static nint HString(string text)
    {
        Check(WindowsCreateString(text, (uint)text.Length, out var hstring));
        return hstring;
    }

    private static int QueryInterface(nint obj, Guid iid, out nint result)
    {
        nint found;
        var hr = ((delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)Slot(obj, 0))(obj, &iid, &found);
        result = found;
        return hr;
    }

    private static void Check(int hr)
    {
        if (hr < 0) Marshal.ThrowExceptionForHR(hr);
    }

    private static void* Slot(nint obj, int index) => (*(void***)obj)[index];

    private static void Release(nint obj)
    {
        if (obj != 0) ((delegate* unmanaged[Stdcall]<nint, uint>)Slot(obj, 2))(obj);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Size { public float Width, Height; }

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct Rect(float X, float Y, float Width, float Height);

    [StructLayout(LayoutKind.Sequential)]
    private struct Color { public byte A, R, G, B; }

    [DllImport("ole32.dll")] private static extern int CoInitializeEx(nint reserved, uint coInit);
    [DllImport("shlwapi.dll")] private static extern nint SHCreateMemStream(byte* data, uint size);
    [DllImport("shcore.dll")] private static extern int CreateRandomAccessStreamOverStream(nint stream, int options, in Guid iid, out nint result);
    [DllImport("combase.dll")] private static extern int RoGetActivationFactory(nint activatableClassId, in Guid iid, out nint factory);
    [DllImport("combase.dll")] private static extern int RoActivateInstance(nint activatableClassId, out nint instance);
    [DllImport("combase.dll", CharSet = CharSet.Unicode)] private static extern int WindowsCreateString(string source, uint length, out nint hstring);
    [DllImport("combase.dll")] private static extern int WindowsDeleteString(nint hstring);
}
