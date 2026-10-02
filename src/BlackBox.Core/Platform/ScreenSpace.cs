using SkiaSharp;

namespace BlackBox.Core.Platform;

/// <summary>
/// Maps between the downscaled screenshot the model sees and real screen pixels.
/// The model always works in screenshot coordinates; the scale only depends on screen size
/// and settings, so it is stable across calls.
/// </summary>
public sealed class ScreenSpace(ScreenRect screen, int maxWidth, int maxHeight)
{
    public ScreenRect Screen { get; } = screen;

    public double Scale { get; } = Math.Min(1.0, Math.Min((double)maxWidth / screen.Width, (double)maxHeight / screen.Height));

    public int ImageWidth => Math.Max(1, (int)Math.Round(Screen.Width * Scale));
    public int ImageHeight => Math.Max(1, (int)Math.Round(Screen.Height * Scale));

    public (int X, int Y) ToScreen(double imageX, double imageY)
    {
        var x = Screen.X + (int)Math.Round(Math.Clamp(imageX, 0, ImageWidth - 1) / Scale);
        var y = Screen.Y + (int)Math.Round(Math.Clamp(imageY, 0, ImageHeight - 1) / Scale);
        return (Math.Min(x, Screen.X + Screen.Width - 1), Math.Min(y, Screen.Y + Screen.Height - 1));
    }

    public (int X, int Y) ToImage(int screenX, int screenY) =>
        ((int)Math.Round((screenX - Screen.X) * Scale), (int)Math.Round((screenY - Screen.Y) * Scale));

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
