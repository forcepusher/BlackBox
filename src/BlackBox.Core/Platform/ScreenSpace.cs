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

    /// <summary>
    /// That grid steps aren't square, for the system prompt. Drawing a circle in Paint, qwen3.8
    /// dragged as many x steps as y steps twice and got 385×241 px ovals before it worked it out.
    /// </summary>
    public string? Proportions() => Grid is { } g && Screen.Width != Screen.Height
        ? FormattableString.Invariant(
            $"The grid isn't square: on this {Screen.Width}x{Screen.Height} screen one x step is {Screen.Width / (double)g:0.##} px and one y step ") +
          FormattableString.Invariant(
            $"{Screen.Height / (double)g:0.##} px, so something as tall as it is wide spans {Screen.Width / (double)Screen.Height:0.##} times as many y steps as x steps.")
        : null;

    public (int X, int Y) ToScreen(double modelX, double modelY)
    {
        // Fractions of the screenshot, (0.87, 0.88), whatever the model was asked for: Kilo's free
        // Nemotron models slip into them. No point meant in pixels or on the grid is that near the corner.
        if (modelX is >= 0 and <= 1 && modelY is >= 0 and <= 1 && (modelX % 1 != 0 || modelY % 1 != 0))
            (modelX, modelY) = (modelX * ModelWidth, modelY * ModelHeight);
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
    /// clicks landed at about 1000/1920 of the intended x). Gemma 4 points on the same grid, asked
    /// for pixels or not: on a 1920x1200 screen it put Notepad's Save button at (492, 671) and the
    /// File name box at (387, 586), where the grid has them at (496, 678) and (385, 600), so every
    /// click it aimed in pixels landed far up and to the left. NVIDIA's Nemotron too: asked for pixels,
    /// Kilo's free Nemotron 3.5 Super VL answered its fresh looks on the grid and hit 0 of 4 targets;
    /// on the grid it hit 12 of 16 (ClickAccuracyTests, 2026-10-16). Null (pixels) for every other model.
    /// </summary>
    public static int? GridFor(string? model) =>
        model is not null && (QwenThreeOrLater().IsMatch(model) || GemmaFourOrLater().IsMatch(model) || Nemotron().IsMatch(model)) ? 1000 : null;

    [System.Text.RegularExpressions.GeneratedRegex("nemotron", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex Nemotron();

    [System.Text.RegularExpressions.GeneratedRegex(@"qwen([3-9]|\d{2})", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex QwenThreeOrLater();

    [System.Text.RegularExpressions.GeneratedRegex(@"gemma[-_ ]?([4-9]|\d{2})", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex GemmaFourOrLater();
}
