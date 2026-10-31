using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Sloperator.Core.Media;
using Sloperator.Core.Platform;
using SkiaSharp;
using static Sloperator.Platform.Windows.ComAbi;

namespace Sloperator.Platform.Windows;

/// <summary>
/// Windows' own text recognition (Windows.Media.Ocr, in every Windows 10 and 11, reading the
/// languages whose OCR pack is installed), called through WinRT's ABI (<see cref="ComAbi"/>). The picture is read twice, enlarged 1.5 and 2.5 times, and both
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

    private const int Bgra8 = 87, Premultiplied = 0, WriteAccess = 2;

    /// <summary>How much a picture is enlarged for each reading, within the engine's largest side.</summary>
    private readonly double[] _scales;

    internal WindowsTextReader(params double[] scales) => _scales = scales is [] ? [1.5, 2.5] : scales;

    private readonly Lock _gate = new();
    private nint _engine;
    private uint _maxSide;

    public IReadOnlyList<TextLine>? Read(RawImage image)
    {
        lock (_gate)
        {
            Start();
            // Tried again each time there is none: the agent may have just installed a language's pack.
            if (_engine == 0)
                (_engine, _maxSide) = CreateEngine();
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

    public IReadOnlyList<string> Languages()
    {
        lock (_gate)
        {
            Start();
            nint statics = 0, list = 0;
            try
            {
                statics = Factory("Windows.Media.Ocr.OcrEngine", IOcrEngineStatics);
                // IOcrEngineStatics: get_AvailableRecognizerLanguages 7; ILanguage: get_DisplayName 7.
                Check(((delegate* unmanaged[Stdcall]<nint, nint*, int>)Slot(statics, 7))(statics, &list));
                var names = new List<string>();
                foreach (var language in Items(list))
                {
                    try { names.Add(Text(language, 7)); }
                    finally { Release(language); }
                }
                return names;
            }
            catch (Exception ex) when (ex is COMException or InvalidCastException or TypeLoadException)
            {
                return []; // no text recognition in this Windows at all
            }
            finally
            {
                Release(list);
                Release(statics);
            }
        }
    }

    public Task<TextInstall> InstallAsync(CancellationToken ct) => TextRecognitionInstall.RunAsync(Languages, ct);

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
            using var resized = source.Resize(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul), Sampling.High(1, scale));
            var bgra = new byte[width * height * 4];
            for (var y = 0; y < height; y++)
                Marshal.Copy(resized.GetPixels() + y * resized.RowBytes, bgra, y * width * 4, width * 4);
            return (bgra, width, height);
        }
        finally { handle.Free(); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public float X, Y, Width, Height; }

    [StructLayout(LayoutKind.Sequential)]
    private struct Plane { public int StartIndex, Width, Height, Stride; }
}

/// <summary>
/// Adds Windows' OCR pack of the user's language (English when there is none for it) through an
/// elevated PowerShell, which Windows asks the user to allow: adding a capability needs an
/// administrator. One encoded command, not a script, so no execution policy stops it.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class TextRecognitionInstall
{
    /// <param name="languages">The languages Windows reads once it is done, to tell an install that took from one that didn't.</param>
    public static async Task<TextInstall> RunAsync(Func<IReadOnlyList<string>> languages, CancellationToken ct)
    {
        var tag = System.Globalization.CultureInfo.CurrentCulture.Name;
        var command = $"$ErrorActionPreference = 'Stop'; try {{ Add-WindowsCapability -Online -Name 'Language.OCR~~~{tag}~0.0.1.0' | Out-Null }} " +
                      "catch { Add-WindowsCapability -Online -Name 'Language.OCR~~~en-US~0.0.1.0' | Out-Null }";
        var start = new System.Diagnostics.ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden,
            Arguments = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand " + Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(command)),
        };
        try
        {
            // Off the caller's thread: starting an elevated process waits until the user answers
            // Windows' prompt, and on the UI thread that froze Sloperator's window meanwhile.
            using var process = await Task.Run(() => System.Diagnostics.Process.Start(start), ct).ConfigureAwait(false);
            if (process is null)
                return TextInstall.Failed;
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223) // ERROR_CANCELLED: the user said no
        {
            return TextInstall.Declined;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return TextInstall.Failed;
        }
        return languages().Count > 0 ? TextInstall.Installed : TextInstall.Failed;
    }
}
