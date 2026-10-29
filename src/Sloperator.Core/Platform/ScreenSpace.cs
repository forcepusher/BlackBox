using SkiaSharp;

namespace Sloperator.Core.Platform;

/// <summary>
/// Maps between the coordinates the model works in and real screen pixels, and makes the
/// downscaled screenshot the model sees. The scale only depends on screen size and settings, so it
/// is stable across calls.
/// </summary>
/// <remarks>
/// The screen is scaled down to fit a box (e.g. 1600×900) keeping its shape; it is never scaled
/// up. Sides are rounded to multiples of 32 (the Qwen vision patch grid) so the vision encoder does
/// not resize the image again.
/// The model's x/y are pixels of that image.
/// </remarks>
public sealed class ScreenSpace
{
    private const int Patch = 32;

    /// <param name="box">Largest size to send, or null for the screen's own resolution.</param>
    public ScreenSpace(ScreenRect screen, (int Width, int Height)? box)
    {
        Screen = screen;
        var scale = box is { } b ? Math.Min(1.0, Math.Min((double)b.Width / screen.Width, (double)b.Height / screen.Height)) : 1.0;
        ImageWidth = Snap(screen.Width * scale);
        ImageHeight = Snap(screen.Height * scale);
    }

    public ScreenRect Screen { get; }
    public int ImageWidth { get; }
    public int ImageHeight { get; }

    private static int Snap(double size) => Math.Max(Patch, (int)Math.Round(size / Patch) * Patch);

    /// <summary>
    /// How the model's x/y relate to the screenshot, for the system prompt. Pixels come with the
    /// image's size: told only "pixels of the screenshot", DeepSeek V4.1 Flash answered fresh looks
    /// on a 0-1000 grid, so on a 2560-wide screen every x it found landed at 1000/2560 of the
    /// target's (the Hierarchy tab at x 1368 came back as 551; 8 of 8 Unity targets so). Every model
    /// is asked for pixels, those trained on that grid too: told the size, Qwen 3.8 (q6_k_xl) hit
    /// 21/28 in pixels against 19/28 on its grid (ClickAccuracyTests, 2026-10-29).
    /// </summary>
    public string Describe() => $"x/y are pixels of the screenshot, which is {ImageWidth}x{ImageHeight}, origin top-left.";

    public (int X, int Y) ToScreen(double modelX, double modelY)
    {
        // Fractions of the screenshot, (0.87, 0.88), whatever the model was asked for: Kilo's free
        // Nemotron models slip into them. No point meant in pixels is that near the corner.
        if (modelX is >= 0 and <= 1 && modelY is >= 0 and <= 1 && (modelX % 1 != 0 || modelY % 1 != 0))
            (modelX, modelY) = (modelX * ImageWidth, modelY * ImageHeight);
        var x = Screen.X + (int)Math.Round(Math.Clamp(modelX, 0, ImageWidth) * Screen.Width / ImageWidth);
        var y = Screen.Y + (int)Math.Round(Math.Clamp(modelY, 0, ImageHeight) * Screen.Height / ImageHeight);
        return (Math.Min(x, Screen.X + Screen.Width - 1), Math.Min(y, Screen.Y + Screen.Height - 1));
    }

    public (int X, int Y) ToModel(int screenX, int screenY) =>
        ((int)Math.Round((double)(screenX - Screen.X) * ImageWidth / Screen.Width),
         (int)Math.Round((double)(screenY - Screen.Y) * ImageHeight / Screen.Height));

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
