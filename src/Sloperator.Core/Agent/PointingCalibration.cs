using System.Globalization;
using System.Text.RegularExpressions;
using SkiaSharp;

namespace Sloperator.Core.Agent;

/// <summary>
/// Which coordinates a model points with, measured instead of guessed from its name (the user: no
/// model branching): at a chat's start the model is shown a picture of the screenshots' size with
/// three dots at known places and asked for their x/y in pixels. Some models answer on the 0-1000
/// grid they were trained on whatever they are asked: Nemotron 3.5 Super VL, told it clicks in
/// pixels of a 2560-wide screenshot, clicked a button at x 2468 at 974 (ClickAccuracyTests,
/// 30 October 2026). Answers that fit the grid far better than the pixels make the chat's clicks
/// (and its zoom rulers, find_text and pictures) speak the grid; anything else stays in pixels.
/// </summary>
public static partial class PointingCalibration
{
    /// <summary>The grid models point on.</summary>
    public const int Grid = 1000;

    /// <summary>The dots, at fractions of the picture: away from the middle, the edges and each other, none mirroring another.</summary>
    private static readonly (string Name, SKColor Color, double X, double Y)[] Dots =
    [
        ("red", new SKColor(220, 30, 30), 0.18, 0.27),
        ("green", new SKColor(30, 160, 60), 0.71, 0.64),
        ("blue", new SKColor(40, 70, 220), 0.43, 0.86),
    ];

    /// <summary>White, <paramref name="width"/> by <paramref name="height"/>, with the dots, as JPEG.</summary>
    public static byte[] Picture(int width, int height)
    {
        using var surface = SKSurface.Create(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.White);
        var radius = Math.Max(6f, height / 60f);
        foreach (var dot in Dots)
        {
            using var paint = new SKPaint { Color = dot.Color, IsAntialias = true };
            canvas.DrawCircle((float)(dot.X * width), (float)(dot.Y * height), radius, paint);
        }
        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, 90);
        return data.ToArray();
    }

    public static string Question(int width, int height) =>
        $"This picture is {width}x{height} pixels. Give the x/y of the middle of the red, the green and the blue dot, in pixels of this picture " +
        "with (0, 0) its top-left corner, as JSON only: {\"red\": [x, y], \"green\": [x, y], \"blue\": [x, y]}";

    /// <summary>
    /// <see cref="Grid"/> when the answer's x/y lie on the 0-1000 grid over the picture and not on
    /// its pixels; null for pixels, and for an answer that fits neither or isn't there (a failed
    /// request), so nothing changes then.
    /// </summary>
    public static int? Read(string? answer, int width, int height)
    {
        if (answer is null)
            return null;
        double pixelOff = 0, gridOff = 0;
        var read = 0;
        foreach (var dot in Dots)
        {
            if (Pair(dot.Name).Match(answer) is not { Success: true } m)
                continue;
            var x = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            var y = double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
            // How far off each reading is, as a share of the picture.
            pixelOff += Math.Abs(x / width - dot.X) + Math.Abs(y / height - dot.Y);
            gridOff += Math.Abs(x / Grid - dot.X) + Math.Abs(y / Grid - dot.Y);
            read++;
        }
        if (read < 2)
            return null;
        (pixelOff, gridOff) = (pixelOff / read, gridOff / read);
        // The grid only when it fits closely and much better: a picture about 1000 wide fits both.
        return gridOff < 0.06 && gridOff * 3 < pixelOff ? Grid : null;
    }

    private static Regex Pair(string name) => name switch
    {
        "red" => Red(),
        "green" => Green(),
        _ => Blue(),
    };

    [GeneratedRegex(@"red""?\s*[:=]\s*[\[(]\s*(\d+(?:\.\d+)?)\s*,\s*(\d+(?:\.\d+)?)", RegexOptions.IgnoreCase)]
    private static partial Regex Red();

    [GeneratedRegex(@"green""?\s*[:=]\s*[\[(]\s*(\d+(?:\.\d+)?)\s*,\s*(\d+(?:\.\d+)?)", RegexOptions.IgnoreCase)]
    private static partial Regex Green();

    [GeneratedRegex(@"blue""?\s*[:=]\s*[\[(]\s*(\d+(?:\.\d+)?)\s*,\s*(\d+(?:\.\d+)?)", RegexOptions.IgnoreCase)]
    private static partial Regex Blue();
}
