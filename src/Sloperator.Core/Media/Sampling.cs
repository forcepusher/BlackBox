using SkiaSharp;

namespace Sloperator.Core.Media;

/// <summary>
/// How a picture's pixels are read when it is drawn at another size. SkiaSharp 4 dropped the filter
/// qualities (Low, Medium, High) that a paint or a resize used to take; these give the same pixels:
/// SkiaSharp 2's High shrank through mipmaps and enlarged with Mitchell's cubic, its Medium blended
/// mipmaps going down and neighbours going up.
/// </summary>
public static class Sampling
{
    /// <summary>Each pixel the one nearest: an exact copy at the picture's own size, blocks when enlarged.</summary>
    public static readonly SKSamplingOptions Nearest = new(SKFilterMode.Nearest);

    /// <summary>A blend of the four nearest pixels: fast, for small pictures that are only compared.</summary>
    public static readonly SKSamplingOptions Low = new(SKFilterMode.Linear);

    private static readonly SKSamplingOptions Mipmaps = new(SKFilterMode.Linear, SKMipmapMode.Linear);
    private static readonly SKSamplingOptions Cubic = new(SKCubicResampler.Mitchell);

    /// <summary>Smooth both ways: for a picture shown or sent at <paramref name="to"/> pixels across from <paramref name="from"/>.</summary>
    public static SKSamplingOptions High(double from, double to) => to < from ? Mipmaps : Cubic;

    /// <summary>Smooth going down, a plain blend going up: for textures and thumbnails.</summary>
    public static SKSamplingOptions Medium(double from, double to) => to < from ? Mipmaps : Low;
}
