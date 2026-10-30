using Sloperator.Core.Media;
using SkiaSharp;

namespace Sloperator.Core.Tools;

/// <summary>
/// A picture file shown to the model like a screenshot (by read_file, or attached to a message):
/// scaled to fit a few megapixels, on white where it is transparent, as JPEG.
/// </summary>
internal static class Picture
{
    private static readonly string[] Types = [".png", ".jpg", ".jpeg", ".jfif", ".gif", ".bmp", ".webp", ".ico"];

    /// <summary>The longest side and the most pixels a picture is shown with: about 3,000 image tokens.</summary>
    private const int MaxSide = 2048, MaxPixels = 2048 * 1536;

    /// <summary>Sides are multiples of this (the Qwen vision patch), so the encoder doesn't scale the image again.</summary>
    private const int Patch = 32;

    public static bool Handles(string path) => Types.Contains(Path.GetExtension(path).ToLowerInvariant());

    /// <summary>A picture as the model sees it: its own size, the size it is shown at, the JPEG, and how much of it is transparent.</summary>
    public sealed record Shown(int Width, int Height, int ShownWidth, int ShownHeight, byte[] Jpeg, string? Transparency = null);

    /// <summary>
    /// How much of a picture is fully transparent, in words, or null when none of it is. Shown on
    /// white, a picture that is all transparent looks blank: asked for a walk cycle sprite sheet on a
    /// transparent background, qwen3.8 made one whose mask had gone the wrong way, every pixel
    /// transparent, and described "a 4-frame walk cycle of a knight" in it.
    /// </summary>
    internal static string? Transparency(SKBitmap bitmap)
    {
        if (bitmap.AlphaType == SKAlphaType.Opaque || bitmap.BytesPerPixel != 4 || bitmap.ColorType is not (SKColorType.Rgba8888 or SKColorType.Bgra8888))
            return null;
        var pixels = bitmap.GetPixelSpan();
        long clear = 0, all = (long)bitmap.Width * bitmap.Height;
        for (var i = 3; i < pixels.Length; i += 4)
            if (pixels[i] == 0) clear++;
        if (clear == 0)
            return null;
        return clear == all ? "fully transparent: nothing in it shows"
            : $"{Math.Max(1, (int)Math.Round(100.0 * clear / all))}% of it transparent";
    }

    /// <summary>The picture scaled for the model; null when the file isn't a picture this can read.</summary>
    public static Shown? Load(string path)
    {
        using var original = PictureFile.Decode(path);
        return original is null ? null : Show(original);
    }

    /// <summary>A picture's bytes (an MCP server's image) scaled for the model; null when they aren't a picture this can read.</summary>
    public static Shown? Load(byte[] data)
    {
        // Through a codec: SKBitmap.Decode of bytes that are no picture throws rather than say null.
        using var bytes = SKData.CreateCopy(data);
        using var codec = SKCodec.Create(bytes);
        if (codec is null)
            return null;
        using var original = SKBitmap.Decode(codec);
        return original is null ? null : Show(original);
    }

    private static Shown Show(SKBitmap original)
    {
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
        return new Shown(original.Width, original.Height, width, height, jpeg.ToArray(), Transparency(original));
    }

    /// <param name="grid">The grid the model points on (1000), or null when it points in pixels.</param>
    public static ToolResult Read(string path, int? grid = null)
    {
        if (Load(path) is not { } picture)
            return ToolResult.Error($"{path} could not be read as a picture (damaged, or not what its name says).");
        var size = picture.ShownWidth == picture.Width && picture.ShownHeight == picture.Height ? "" : $", shown at {picture.ShownWidth}x{picture.ShownHeight}";
        var clear = picture.Transparency is { } t ? $", {t} (shown on white)" : "";
        return new ToolResult($"{path}: a {picture.Width}x{picture.Height} picture{size}{clear}{GridNote(picture, grid)}.", picture.Jpeg,
            ImageCaption: $"The picture {Path.GetFileName(path)}:");
    }

    /// <summary>
    /// For a model that points on a 0-1000 grid (see <see cref="Agent.PointingCalibration"/>), how
    /// the places it sees in a picture are found in the picture's own pixels, to crop it or measure
    /// it. Asked to crop the clothed figure from the user's 2945x2160 reference sheet, qwen3.8 read
    /// its corner at (795, 435), on its grid, took that for pixels of the 2048-wide view it was
    /// shown, and cropped the wrong figure three times before it found the clothes by their colour.
    /// </summary>
    internal static string GridNote(Shown picture, int? grid) => grid is { } g
        ? FormattableString.Invariant(
            $"; places you see in it are on your 0-{g} grid, so in its own pixels x is {picture.Width / (double)g:0.###} times your x and y {picture.Height / (double)g:0.###} times your y")
        : "";

    private static int Snap(double size) => Math.Max(Patch, (int)Math.Round(size / Patch) * Patch);
}
