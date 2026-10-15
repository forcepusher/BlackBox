using SkiaSharp;

namespace BlackBox.Core.Tools;

/// <summary>
/// A picture file for read_file, shown to the model like a screenshot: scaled to fit a few
/// megapixels, on white where it is transparent, as JPEG.
/// </summary>
internal static class Picture
{
    private static readonly string[] Types = [".png", ".jpg", ".jpeg", ".jfif", ".gif", ".bmp", ".webp", ".ico"];

    /// <summary>The longest side and the most pixels a picture is shown with: about 3,000 image tokens.</summary>
    private const int MaxSide = 2048, MaxPixels = 2048 * 1536;

    /// <summary>Sides are multiples of this (the Qwen vision patch), so the encoder doesn't scale the image again.</summary>
    private const int Patch = 32;

    public static bool Handles(string path) => Types.Contains(Path.GetExtension(path).ToLowerInvariant());

    public static ToolResult Read(string path)
    {
        using var original = SKBitmap.Decode(path);
        if (original is null)
            return ToolResult.Error($"{path} could not be read as a picture (damaged, or not what its name says).");
        var scale = Math.Min(1.0, Math.Min((double)MaxSide / Math.Max(original.Width, original.Height),
                                           Math.Sqrt((double)MaxPixels / ((long)original.Width * original.Height))));
        var width = Snap(original.Width * scale);
        var height = Snap(original.Height * scale);

        using var surface = SKSurface.Create(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
        surface.Canvas.Clear(SKColors.White);
        using (var paint = new SKPaint { FilterQuality = SKFilterQuality.High, IsAntialias = true })
            surface.Canvas.DrawBitmap(original, new SKRect(0, 0, width, height), paint);
        using var image = surface.Snapshot();
        using var jpeg = image.Encode(SKEncodedImageFormat.Jpeg, 85);

        var size = width == original.Width && height == original.Height ? "" : $", shown at {width}x{height}";
        return new ToolResult($"{path}: a {original.Width}x{original.Height} picture{size}.", jpeg.ToArray(),
            ImageCaption: $"The picture {Path.GetFileName(path)}:");
    }

    private static int Snap(double size) => Math.Max(Patch, (int)Math.Round(size / Patch) * Patch);
}
