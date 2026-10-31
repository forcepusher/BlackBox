using SkiaSharp;

namespace Sloperator.Core.Media;

/// <summary>
/// How a picture's pixels are read when it is drawn at another size. SkiaSharp 4 dropped the filter
/// qualities (Low, Medium, High) that a paint or a resize used to take; these give the same pixels:
/// SkiaSharp 2's Medium blended the four nearest pixels of the nearest mipmap (the picture halved as
/// often as it shrinks by half) at any size, its High did that going down, enlarged with Mitchell's
/// cubic and at the picture's own size copied it.
/// </summary>
public static class Sampling
{
    /// <summary>Each pixel the one nearest: an exact copy at the picture's own size, blocks when enlarged.</summary>
    public static readonly SKSamplingOptions Nearest = new(SKFilterMode.Nearest);

    /// <summary>A blend of the four nearest pixels: fast, for small pictures that are only compared.</summary>
    public static readonly SKSamplingOptions Low = new(SKFilterMode.Linear);

    /// <summary>Smooth going down, a plain blend going up: for textures and thumbnails.</summary>
    public static readonly SKSamplingOptions Medium = new(SKFilterMode.Linear, SKMipmapMode.Nearest);

    private static readonly SKSamplingOptions Cubic = new(SKCubicResampler.Mitchell);

    /// <summary>Smooth both ways: for a picture shown or sent at <paramref name="to"/> pixels across from <paramref name="from"/>.</summary>
    public static SKSamplingOptions High(double from, double to) => to < from ? Medium : to > from ? Cubic : Nearest;
}
