using SkiaSharp;

namespace BlackBox.Core.Platform;

/// <summary>
/// Maps between the coordinates the model works in and real screen pixels, and makes the
/// downscaled screenshot the model sees. The scale only depends on screen size and settings, so it
/// is stable across calls.
/// </summary>
/// <remarks>
/// The screen is scaled down to fit a box (e.g. 1600×900) keeping its shape; it is never scaled
/// up. Sides are rounded to multiples of 32 (the Qwen vision patch grid) so the vision encoder does
/// not resize the image again.
/// The model's x/y are either pixels of that image, or, for models trained that way (Qwen3 and
/// later), a 0-1000 grid over it whatever its size.
/// </remarks>
public sealed class ScreenSpace
{
    private const int Patch = 32;

    /// <param name="box">Largest size to send, or null for the screen's own resolution.</param>
    /// <param name="grid">Size of the relative grid the model's x/y are on (1000), or null for image pixels.</param>
    public ScreenSpace(ScreenRect screen, (int Width, int Height)? box, int? grid = null)
    {
        Screen = screen;
        Grid = grid;
        var scale = box is { } b ? Math.Min(1.0, Math.Min((double)b.Width / screen.Width, (double)b.Height / screen.Height)) : 1.0;
        ImageWidth = Snap(screen.Width * scale);
        ImageHeight = Snap(screen.Height * scale);
    }

    public ScreenRect Screen { get; }
    public int ImageWidth { get; }
    public int ImageHeight { get; }
    public int? Grid { get; }

    /// <summary>The largest x and y the model can give.</summary>
    public int ModelWidth => Grid ?? ImageWidth;
    public int ModelHeight => Grid ?? ImageHeight;

    private static int Snap(double size) => Math.Max(Patch, (int)Math.Round(size / Patch) * Patch);

    /// <summary>How the model's x/y relate to the screenshot, for the system prompt.</summary>
    public string Describe() => Grid is { } g
        ? $"x/y are on a 0-{g} grid over the screenshot, whatever its size: (0, 0) is its top-left corner and ({g}, {g}) its bottom-right."
        : "x/y are pixels of the screenshot, origin top-left.";

    public (int X, int Y) ToScreen(double modelX, double modelY)
    {
        var x = Screen.X + (int)Math.Round(Math.Clamp(modelX, 0, ModelWidth) * Screen.Width / ModelWidth);
        var y = Screen.Y + (int)Math.Round(Math.Clamp(modelY, 0, ModelHeight) * Screen.Height / ModelHeight);
        return (Math.Min(x, Screen.X + Screen.Width - 1), Math.Min(y, Screen.Y + Screen.Height - 1));
    }

    public (int X, int Y) ToModel(int screenX, int screenY) =>
        ((int)Math.Round((double)(screenX - Screen.X) * ModelWidth / Screen.Width),
         (int)Math.Round((double)(screenY - Screen.Y) * ModelHeight / Screen.Height));

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

/// <summary>Which coordinates a model was trained to point with.</summary>
public static partial class ModelCoordinates
{
    /// <summary>
    /// 1000 for Qwen3 and later: they were trained to give positions on a 0-1000 grid over the
    /// image. Asked for pixels, qwen3.8 often slips back to that grid for x (measured: 4 of 16
    /// clicks landed at about 1000/1920 of the intended x). Null (pixels) for every other model.
    /// </summary>
    public static int? GridFor(string? model) => model is not null && QwenThreeOrLater().IsMatch(model) ? 1000 : null;

    [System.Text.RegularExpressions.GeneratedRegex(@"qwen([3-9]|\d{2})", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex QwenThreeOrLater();
}
