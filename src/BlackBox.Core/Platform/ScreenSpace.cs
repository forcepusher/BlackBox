using SkiaSharp;

namespace BlackBox.Core.Platform;

/// <summary>
/// Maps between the downscaled screenshot the model sees and real screen pixels.
/// The model always works in screenshot coordinates; the scale only depends on screen size
/// and settings, so it is stable across calls.
/// </summary>
/// <remarks>
/// The size is capped by pixel count rather than width, so ultrawide and tall screens keep the
/// same level of detail as a 16:9 one. Sides are rounded to multiples of 32 (the Qwen vision
/// patch grid) so the vision encoder does not resize the image again, which would shift the
/// coordinates the model answers with.
/// </remarks>
public sealed class ScreenSpace
{
    private const int Grid = 32;

    public ScreenSpace(ScreenRect screen, int maxPixels)
    {
        Screen = screen;
        var scale = Math.Min(1.0, Math.Sqrt((double)maxPixels / ((double)screen.Width * screen.Height)));
        ImageWidth = Snap(screen.Width * scale);
        ImageHeight = Snap(screen.Height * scale);
        ScaleX = (double)ImageWidth / screen.Width;
        ScaleY = (double)ImageHeight / screen.Height;
    }

    public ScreenRect Screen { get; }
    public int ImageWidth { get; }
    public int ImageHeight { get; }
    public double ScaleX { get; }
    public double ScaleY { get; }

    private static int Snap(double size) => Math.Max(Grid, (int)Math.Round(size / Grid) * Grid);

    public (int X, int Y) ToScreen(double imageX, double imageY)
    {
        var x = Screen.X + (int)Math.Round(Math.Clamp(imageX, 0, ImageWidth - 1) / ScaleX);
        var y = Screen.Y + (int)Math.Round(Math.Clamp(imageY, 0, ImageHeight - 1) / ScaleY);
        return (Math.Min(x, Screen.X + Screen.Width - 1), Math.Min(y, Screen.Y + Screen.Height - 1));
    }

    public (int X, int Y) ToImage(int screenX, int screenY) =>
        ((int)Math.Round((screenX - Screen.X) * ScaleX), (int)Math.Round((screenY - Screen.Y) * ScaleY));

    /// <summary>Downscales a captured frame to the model's view and encodes it as JPEG.</summary>
    public byte[] Encode(RawImage frame, int quality = 85)
    {
        var info = new SKImageInfo(frame.Width, frame.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var src = new SKBitmap();
        var handle = System.Runtime.InteropServices.GCHandle.Alloc(frame.Bgra, System.Runtime.InteropServices.GCHandleType.Pinned);
        try
        {
            src.InstallPixels(info, handle.AddrOfPinnedObject(), frame.Stride);
            using var scaled = ImageWidth == frame.Width && ImageHeight == frame.Height
                ? src.Copy()
                : src.Resize(new SKImageInfo(ImageWidth, ImageHeight, SKColorType.Bgra8888, SKAlphaType.Premul), SKFilterQuality.High);
            using var image = SKImage.FromBitmap(scaled);
            using var data = image.Encode(SKEncodedImageFormat.Jpeg, quality);
            return data.ToArray();
        }
        finally
        {
            handle.Free();
        }
    }
}
