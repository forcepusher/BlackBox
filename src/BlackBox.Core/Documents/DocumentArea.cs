using System.Globalization;
using System.Runtime.InteropServices;
using BlackBox.Core.Platform;
using SkiaSharp;

namespace BlackBox.Core.Documents;

/// <summary>
/// An area dragged over a file in the Inspect file panel: its sides as parts of what it was dragged
/// on (0 the left or top, 1 the right or bottom), of a PDF's page <paramref name="Page"/> (from 0), a
/// picture, or the panel's view of the file. The model gets it as a picture of the area and where it is.
/// </summary>
public sealed record DocumentArea(double Left, double Top, double Right, double Bottom, int? Page = null)
{
    /// <summary>
    /// The area between two corners, in parts of what it is on, kept inside it; null when it has no
    /// width or height there (a click).
    /// </summary>
    public static DocumentArea? Between(double x0, double y0, double x1, double y1, int? page = null)
    {
        static double In(double v) => double.IsNaN(v) ? 0 : Math.Clamp(v, 0, 1);
        var (left, right) = (In(Math.Min(x0, x1)), In(Math.Max(x0, x1)));
        var (top, bottom) = (In(Math.Min(y0, y1)), In(Math.Max(y0, y1)));
        return right - left <= 0 || bottom - top <= 0 ? null : new DocumentArea(left, top, right, bottom, page);
    }

    /// <summary>"area on page 2", or "area" on what has no pages.</summary>
    public string Where => Page is { } page ? $"area on page {page + 1}" : "area";

    /// <summary>
    /// The area in <paramref name="units"/> of what it is on, <paramref name="width"/> by <paramref name="height"/>
    /// of them: "x 72-300, y 100-250 of the 612x792 points, from the top left".
    /// </summary>
    public string Place(double width, double height, string units) =>
        $"x {N(Left * width)}-{N(Right * width)}, y {N(Top * height)}-{N(Bottom * height)} of the {N(width)}x{N(height)} {units}, from the top left";

    /// <summary>The area in whole pixels of a picture <paramref name="width"/> by <paramref name="height"/>: one at least each way.</summary>
    public (int X, int Y, int Width, int Height) Pixels(int width, int height)
    {
        var x = Math.Clamp((int)Math.Floor(Left * width), 0, Math.Max(0, width - 1));
        var y = Math.Clamp((int)Math.Floor(Top * height), 0, Math.Max(0, height - 1));
        var right = Math.Clamp((int)Math.Ceiling(Right * width), x + 1, Math.Max(x + 1, width));
        var bottom = Math.Clamp((int)Math.Ceiling(Bottom * height), y + 1, Math.Max(y + 1, height));
        return (x, y, right - x, bottom - y);
    }

    /// <summary>The area cut out of <paramref name="image"/>, which it is in parts of.</summary>
    public RawImage Crop(RawImage image)
    {
        var (x, y, width, height) = Pixels(image.Width, image.Height);
        var bgra = new byte[width * height * 4];
        for (var row = 0; row < height; row++)
            Buffer.BlockCopy(image.Bgra, (y + row) * image.Stride + x * 4, bgra, row * width * 4, width * 4);
        return new RawImage(width, height, bgra);
    }

    /// <summary>A picture's pixels, BGRA and opaque; null when it can't be decoded.</summary>
    public static RawImage? Decode(string path)
    {
        using var decoded = SKBitmap.Decode(path);
        if (decoded is null)
            return null;
        using var bgra = decoded.ColorType == SKColorType.Bgra8888 && decoded.AlphaType == SKAlphaType.Premul ? null : decoded.Copy(SKColorType.Bgra8888);
        var b = bgra ?? decoded;
        var pixels = new byte[b.Width * b.Height * 4];
        for (var row = 0; row < b.Height; row++)
            Marshal.Copy(b.GetPixels() + row * b.RowBytes, pixels, row * b.Width * 4, b.Width * 4);
        return new RawImage(b.Width, b.Height, pixels);
    }

    /// <summary>Writes <paramref name="image"/> to <paramref name="path"/> as a PNG.</summary>
    public static void SavePng(RawImage image, string path)
    {
        var pinned = GCHandle.Alloc(image.Bgra, GCHandleType.Pinned);
        try
        {
            using var bitmap = new SKBitmap();
            bitmap.InstallPixels(new SKImageInfo(image.Width, image.Height, SKColorType.Bgra8888, SKAlphaType.Premul), pinned.AddrOfPinnedObject(), image.Stride);
            using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
            using var file = File.Create(path);
            data.SaveTo(file);
        }
        finally
        {
            pinned.Free();
        }
    }

    private static string N(double v) => Math.Round(v).ToString("0", CultureInfo.InvariantCulture);
}
