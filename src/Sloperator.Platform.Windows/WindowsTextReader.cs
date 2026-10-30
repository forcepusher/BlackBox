using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Sloperator.Core.Platform;
using SkiaSharp;

namespace Sloperator.Platform.Windows;

/// <summary>
/// Windows' own text recognition (Windows.Media.Ocr, in every Windows 10 and 11, reading the
/// languages whose OCR pack is installed), called through WinRT's ABI as <see cref="WindowsPdf"/> is,
/// without the .NET projection. The picture is read twice, enlarged 1.5 and 2.5 times, and both
/// readings are kept: 12 px Segoe UI codes were all read right at 1.5x, but "VY-643" came back as
/// "w-643" at 2x to 5x, and at 1x two of six codes were missed (2026-10-30).
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed unsafe class WindowsTextReader : ITextReader
{
    // Interface ids, from Windows' own metadata (System32\WinMetadata).
    private static readonly Guid IOcrEngineStatics = new("5BFFA85A-3384-3540-9940-699120D428A8");
    private static readonly Guid ISoftwareBitmapFactory = new("C99FEB69-2D62-4D47-A6B3-4FDB6A07FDF8");
    private static readonly Guid IMemoryBuffer = new("FBC4DD2A-245B-11E4-AF98-689423260CF8");
    private static readonly Guid IMemoryBufferByteAccess = new("5B0D3235-4DBA-4D44-865E-8F1D0E4FD04D");
    private static readonly Guid IClosable = new("30D5A829-7FA4-4026-83BB-D75BAE4EA99E");
    private static readonly Guid IAsyncInfo = new("00000036-0000-0000-C000-000000000046");

    private const int Bgra8 = 87, Premultiplied = 0, WriteAccess = 2;

    /// <summary>How much a picture is enlarged for each reading, within the engine's largest side.</summary>
    private readonly double[] _scales;

    internal WindowsTextReader(params double[] scales) => _scales = scales is [] ? [1.5, 2.5] : scales;

    private readonly Lock _gate = new();
    private nint _engine;
    private uint _maxSide;
    private bool _tried;

    public IReadOnlyList<TextLine>? Read(RawImage image)
    {
        lock (_gate)
        {
            _ = CoInitializeEx(0, 0); // MTA where nothing else has been set up; an STA thread works too
            if (!_tried)
            {
                _tried = true;
                (_engine, _maxSide) = CreateEngine();
            }
            if (_engine == 0)
                return null;
            var largest = (_maxSide - 1.0) / Math.Max(image.Width, image.Height);
            return _scales.Select(s => Math.Min(s, largest)).Distinct().SelectMany(s => ReadAt(image, s)).ToList();
        }
    }

    /// <summary>The lines read off the picture enlarged by <paramref name="scale"/>, in the picture's own pixels.</summary>
    private List<TextLine> ReadAt(RawImage image, double scale)
    {
        var (pixels, width, height) = Math.Abs(scale - 1) < 0.01 ? (image.Bgra, image.Width, image.Height) : Resize(image, scale);
        nint bitmap = 0, operation = 0, result = 0, lines = 0;
        try
        {
            bitmap = Bitmap(pixels, width, height);
            // IOcrEngine: RecognizeAsync 6; IAsyncOperation<OcrResult>: GetResults 8.
            Check(((delegate* unmanaged[Stdcall]<nint, nint, nint*, int>)Slot(_engine, 6))(_engine, bitmap, &operation));
            Wait(operation);
            Check(((delegate* unmanaged[Stdcall]<nint, nint*, int>)Slot(operation, 8))(operation, &result));
            // IOcrResult: get_Lines 6.
            Check(((delegate* unmanaged[Stdcall]<nint, nint*, int>)Slot(result, 6))(result, &lines));
            var read = new List<TextLine>();
            foreach (var line in Items(lines))
            {
                nint words = 0;
                try
                {
                    // IOcrLine: get_Words 6, get_Text 7; IOcrWord: get_BoundingRect 6, get_Text 7.
                    Check(((delegate* unmanaged[Stdcall]<nint, nint*, int>)Slot(line, 6))(line, &words));
                    var list = new List<TextWord>();
                    foreach (var word in Items(words))
                    {
                        try
                        {
                            Rect box;
                            Check(((delegate* unmanaged[Stdcall]<nint, Rect*, int>)Slot(word, 6))(word, &box));
                            list.Add(new TextWord(Text(word, 7), box.X / scale, box.Y / scale, box.Width / scale, box.Height / scale));
                        }
                        finally { Release(word); }
                    }
                    read.Add(new TextLine(Text(line, 7), list));
                }
                finally
                {
                    Release(words);
                    Release(line);
                }
            }
            return read;
        }
        finally
        {
            Release(lines);
            Release(result);
            Release(operation);
            Release(bitmap);
        }
    }

    /// <summary>An engine for the user's languages, and the largest side it reads; no engine when none of them can be read.</summary>
    private static (nint Engine, uint MaxSide) CreateEngine()
    {
        var statics = Factory("Windows.Media.Ocr.OcrEngine", IOcrEngineStatics);
        try
        {
            // IOcrEngineStatics: get_MaxImageDimension 6, TryCreateFromUserProfileLanguages 10 (null when none is installed).
            uint max;
            nint engine;
            Check(((delegate* unmanaged[Stdcall]<nint, uint*, int>)Slot(statics, 6))(statics, &max));
            Check(((delegate* unmanaged[Stdcall]<nint, nint*, int>)Slot(statics, 10))(statics, &engine));
            return (engine, max);
        }
        finally { Release(statics); }
    }

    /// <summary>A SoftwareBitmap of the pixels: BGRA, premultiplied, rows of <paramref name="width"/> pixels.</summary>
    private static nint Bitmap(byte[] pixels, int width, int height)
    {
        var factory = Factory("Windows.Graphics.Imaging.SoftwareBitmap", ISoftwareBitmapFactory);
        nint bitmap = 0, buffer = 0, memory = 0, reference = 0, access = 0;
        try
        {
            // ISoftwareBitmapFactory: CreateWithAlpha 7; ISoftwareBitmap: LockBuffer 15; IBitmapBuffer: GetPlaneDescription 7.
            Check(((delegate* unmanaged[Stdcall]<nint, int, int, int, int, nint*, int>)Slot(factory, 7))(factory, Bgra8, width, height, Premultiplied, &bitmap));
            Check(((delegate* unmanaged[Stdcall]<nint, int, nint*, int>)Slot(bitmap, 15))(bitmap, WriteAccess, &buffer));
            Plane plane;
            Check(((delegate* unmanaged[Stdcall]<nint, int, Plane*, int>)Slot(buffer, 7))(buffer, 0, &plane));
            // IMemoryBuffer: CreateReference 6; IMemoryBufferByteAccess: GetBuffer 3.
            memory = Query(buffer, IMemoryBuffer);
            Check(((delegate* unmanaged[Stdcall]<nint, nint*, int>)Slot(memory, 6))(memory, &reference));
            access = Query(reference, IMemoryBufferByteAccess);
            byte* target;
            uint capacity;
            Check(((delegate* unmanaged[Stdcall]<nint, byte**, uint*, int>)Slot(access, 3))(access, &target, &capacity));
            var row = width * 4;
            if (plane.StartIndex + (long)plane.Stride * (height - 1) + row > capacity)
                throw new InvalidOperationException("The bitmap's buffer is smaller than its pixels.");
            fixed (byte* source = pixels)
                for (var y = 0; y < height; y++)
                    Buffer.MemoryCopy(source + y * row, target + plane.StartIndex + y * plane.Stride, row, row);
            // Writes count once the reference and the buffer are closed.
            Close(reference);
            Close(buffer);
            var done = bitmap;
            bitmap = 0;
            return done;
        }
        finally
        {
            Release(access);
            Release(reference);
            Release(memory);
            Release(buffer);
            Release(bitmap);
            Release(factory);
        }
    }

    /// <summary>The picture enlarged (or shrunk) by <paramref name="scale"/>, as BGRA rows without padding.</summary>
    private static (byte[], int, int) Resize(RawImage image, double scale)
    {
        var (width, height) = (Math.Max(1, (int)Math.Round(image.Width * scale)), Math.Max(1, (int)Math.Round(image.Height * scale)));
        var handle = GCHandle.Alloc(image.Bgra, GCHandleType.Pinned);
        try
        {
            using var source = new SKBitmap();
            source.InstallPixels(new SKImageInfo(image.Width, image.Height, SKColorType.Bgra8888, SKAlphaType.Premul), handle.AddrOfPinnedObject(), image.Stride);
            using var resized = source.Resize(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul), SKFilterQuality.High);
            var bgra = new byte[width * height * 4];
            for (var y = 0; y < height; y++)
                Marshal.Copy(resized.GetPixels() + y * resized.RowBytes, bgra, y * width * 4, width * 4);
            return (bgra, width, height);
        }
        finally { handle.Free(); }
    }

    /// <summary>The items of an IVectorView (GetAt 6, get_Size 7), each to be released by the caller.</summary>
    private static List<nint> Items(nint vector)
    {
        uint count;
        Check(((delegate* unmanaged[Stdcall]<nint, uint*, int>)Slot(vector, 7))(vector, &count));
        var items = new List<nint>((int)count);
        for (uint i = 0; i < count; i++)
        {
            nint item;
            Check(((delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)Slot(vector, 6))(vector, i, &item));
            items.Add(item);
        }
        return items;
    }

    /// <summary>A string property (an HSTRING getter at <paramref name="slot"/>).</summary>
    private static string Text(nint obj, int slot)
    {
        nint hstring;
        Check(((delegate* unmanaged[Stdcall]<nint, nint*, int>)Slot(obj, slot))(obj, &hstring));
        try
        {
            uint length;
            var chars = WindowsGetStringRawBuffer(hstring, &length);
            return chars == null ? "" : new string(chars, 0, (int)length);
        }
        finally { _ = WindowsDeleteString(hstring); }
    }

    /// <summary>Waits for a WinRT async operation to end; throws what it failed with (see <see cref="WindowsPdf"/>).</summary>
    private static void Wait(nint async)
    {
        var info = Query(async, IAsyncInfo);
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

    /// <summary>IClosable.Close (6), through the object's own IClosable.</summary>
    private static void Close(nint obj)
    {
        var closable = Query(obj, IClosable);
        try { Check(((delegate* unmanaged[Stdcall]<nint, int>)Slot(closable, 6))(closable)); }
        finally { Release(closable); }
    }

    private static nint Factory(string runtimeClass, Guid iid)
    {
        nint name;
        fixed (char* chars = runtimeClass)
            Check(WindowsCreateString(chars, (uint)runtimeClass.Length, &name));
        try
        {
            nint factory;
            Check(RoGetActivationFactory(name, &iid, &factory));
            return factory;
        }
        finally { _ = WindowsDeleteString(name); }
    }

    private static nint Query(nint obj, Guid iid)
    {
        nint result;
        Check(((delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)Slot(obj, 0))(obj, &iid, &result));
        return result;
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
    private struct Rect { public float X, Y, Width, Height; }

    [StructLayout(LayoutKind.Sequential)]
    private struct Plane { public int StartIndex, Width, Height, Stride; }

    [DllImport("ole32.dll")] private static extern int CoInitializeEx(nint reserved, uint coInit);
    [DllImport("combase.dll")] private static extern int RoGetActivationFactory(nint activatableClassId, Guid* iid, nint* factory);
    [DllImport("combase.dll")] private static extern int WindowsCreateString(char* source, uint length, nint* hstring);
    [DllImport("combase.dll")] private static extern int WindowsDeleteString(nint hstring);
    [DllImport("combase.dll")] private static extern char* WindowsGetStringRawBuffer(nint hstring, uint* length);
}
