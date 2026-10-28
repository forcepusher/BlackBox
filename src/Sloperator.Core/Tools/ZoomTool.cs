using System.Globalization;
using System.Text.Json.Nodes;
using Sloperator.Core.Platform;
using SkiaSharp;

namespace Sloperator.Core.Tools;

/// <summary>
/// A sharp, enlarged picture of part of the screen, with rulers in screenshot coordinates on its
/// sides and a faint grid over it, for the model to aim a click from. A whole screenshot reaches some models
/// shrunk (munito reads any picture at about 1,000 tokens), so a 16 px row of Unity's Hierarchy is a
/// few pixels high there; DeepSeek V4.1 Flash, cropping and reading parts of the screen with
/// scripts of its own, then aimed 5 of 5 clicks at the right row.
/// </summary>
public sealed class ZoomTool : Tool
{
    /// <summary>The most a region is enlarged: past that its pixels only get bigger.</summary>
    private const double MaxScale = 4;

    /// <summary>The smallest region, in screen pixels, on either side.</summary>
    private const int MinSide = 32;

    /// <summary>The width of the rulers, in pixels of the picture.</summary>
    private const int Ruler = 30;

    public override string Name => "zoom";
    public override string Description =>
        "Look closer at part of the screen: a sharp, enlarged picture of the region around (x, y), with rulers and a faint grid " +
        "in screenshot coordinates. Use it before clicking anything small or close to things like it, and read the x/y to click off the rulers.";
    public override ToolRisk Risk => ToolRisk.ReadOnly;
    public override JsonObject Parameters => Schema(
        ("x", Num("X of the region's centre, in screenshot coordinates"), true),
        ("y", Num("Y of the region's centre, in screenshot coordinates"), true),
        ("width", Num("The region's width in screenshot coordinates. Default: a quarter of the screenshot's"), false),
        ("height", Num("The region's height in screenshot coordinates. Default: a quarter of the screenshot's"), false));
    public override string Describe(JsonObject args) =>
        string.Create(CultureInfo.InvariantCulture, $"Zoom in at ({GetNumber(args, "x")}, {GetNumber(args, "y")})");

    public override Task<ToolResult> ExecuteAsync(JsonObject args, ToolContext ctx, CancellationToken ct)
    {
        var space = ctx.GetScreenSpace();
        var (cx, cy) = space.ToScreen(RequireNumber(args, "x"), RequireNumber(args, "y"));
        var screen = space.Screen;
        var width = Side(GetNumber(args, "width"), space.ModelWidth, screen.Width);
        var height = Side(GetNumber(args, "height"), space.ModelHeight, screen.Height);
        var region = new ScreenRect(Math.Clamp(cx - width / 2, screen.X, screen.X + screen.Width - width),
            Math.Clamp(cy - height / 2, screen.Y, screen.Y + screen.Height - height), width, height);
        var frame = ctx.Platform.Screen.Capture(region, includeCursor: true);
        var scale = Math.Min(MaxScale, Math.Min((double)space.ImageWidth / width, (double)space.ImageHeight / height));
        var (x0, y0) = space.ToModel(region.X, region.Y);
        var (x1, y1) = space.ToModel(region.X + region.Width, region.Y + region.Height);
        var jpeg = Render(frame, scale, space, region);
        var text = string.Create(CultureInfo.InvariantCulture,
            $"Zoomed in on ({x0}, {y0}) to ({x1}, {y1}) of the screenshot, {scale:0.#}x. The rulers are in screenshot coordinates: read the x/y to click off them.");
        return Task.FromResult(new ToolResult(text, jpeg, ImageCaption: "Zoomed-in view, rulers in screenshot coordinates:"));
    }

    /// <summary>A side of the region in screen pixels: the given size in the model's coordinates, or a quarter of the screen's.</summary>
    private static int Side(double? given, int modelSide, int screenSide)
    {
        var side = given is > 0 ? (int)Math.Round(given.Value * screenSide / modelSide) : screenSide / 4;
        return Math.Clamp(side, Math.Min(MinSide, screenSide), screenSide);
    }

    /// <summary>
    /// The region enlarged, with rulers on all four sides labelled in the model's coordinates, finer
    /// ticks between the labels and a faint grid line across the picture at each label: qwen3.8, with
    /// labels on two sides only, read a click 55 grid steps off the one it had zoomed in on.
    /// </summary>
    internal static byte[] Render(RawImage frame, double scale, ScreenSpace space, ScreenRect region)
    {
        var (w, h) = ((int)Math.Round(frame.Width * scale), (int)Math.Round(frame.Height * scale));
        var info = new SKImageInfo(frame.Width, frame.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var src = new SKBitmap();
        var handle = System.Runtime.InteropServices.GCHandle.Alloc(frame.Bgra, System.Runtime.InteropServices.GCHandleType.Pinned);
        try
        {
            src.InstallPixels(info, handle.AddrOfPinnedObject(), frame.Stride);
            using var surface = SKSurface.Create(new SKImageInfo(w + 2 * Ruler, h + 2 * Ruler, SKColorType.Bgra8888, SKAlphaType.Premul));
            var canvas = surface.Canvas;
            canvas.Clear(SKColors.White);
            using (var image = SKImage.FromBitmap(src))
            using (var sharp = new SKPaint { FilterQuality = scale > 1 ? SKFilterQuality.None : SKFilterQuality.High })
                canvas.DrawImage(image, new SKRect(Ruler, Ruler, Ruler + w, Ruler + h), sharp);

            using var ink = new SKPaint { Color = SKColors.Black, IsAntialias = true, StrokeWidth = 1, TextSize = 12 };
            using var font = new SKFont(SKTypeface.Default, 12);
            // Magenta, half see-through: visible on light and dark interfaces alike, and seldom their own colour.
            using var grid = new SKPaint { Color = new SKColor(255, 0, 255, 110), StrokeWidth = 1 };
            var (x0, y0) = space.ToModel(region.X, region.Y);
            var (x1, y1) = space.ToModel(region.X + region.Width, region.Y + region.Height);
            // Where a model coordinate lies on the picture.
            float Px(double x) => Ruler + (float)((x - x0) * w / Math.Max(1, x1 - x0));
            float Py(double y) => Ruler + (float)((y - y0) * h / Math.Max(1, y1 - y0));
            var (right, bottom) = (Ruler + w, Ruler + h);
            var xStep = Step(x1 - x0, w);
            foreach (var x in Ticks(x0, x1, Minor(xStep)))
            {
                var px = Px(x);
                var major = x % xStep == 0;
                var tick = major ? 8 : 4;
                canvas.DrawLine(px, Ruler - tick, px, Ruler, ink);
                canvas.DrawLine(px, bottom, px, bottom + tick, ink);
                if (!major)
                    continue;
                // A grid line across the picture, and the label above and below it.
                canvas.DrawLine(px, Ruler, px, bottom, grid);
                var label = x.ToString(CultureInfo.InvariantCulture);
                var lx = Math.Clamp(px - ink.MeasureText(label) / 2, 0, w + 2 * Ruler - ink.MeasureText(label));
                canvas.DrawText(label, lx, Ruler - 11, font, ink);
                canvas.DrawText(label, lx, bottom + 21, font, ink);
            }
            var yStep = Step(y1 - y0, h);
            foreach (var y in Ticks(y0, y1, Minor(yStep)))
            {
                var py = Py(y);
                var major = y % yStep == 0;
                var tick = major ? 6 : 3;
                canvas.DrawLine(Ruler - tick, py, Ruler, py, ink);
                canvas.DrawLine(right, py, right + tick, py, ink);
                if (!major)
                    continue;
                canvas.DrawLine(Ruler, py, right, py, grid);
                var label = y.ToString(CultureInfo.InvariantCulture);
                foreach (var (at, turn) in new[] { (Ruler - 10f, -90f), (right + 10f, 90f) })
                {
                    canvas.Save();
                    canvas.RotateDegrees(turn, at, py);
                    canvas.DrawText(label, at - ink.MeasureText(label) / 2, py, font, ink);
                    canvas.Restore();
                }
            }
            using var snapshot = surface.Snapshot();
            using var data = snapshot.Encode(SKEncodedImageFormat.Jpeg, 90);
            return data.ToArray();
        }
        finally
        {
            handle.Free();
        }
    }

    /// <summary>The step between unlabelled ticks: a fifth of a labelled step of 5, 10, 50…, a half of one of 2, 20…; none for a step of 1.</summary>
    internal static double Minor(double step) => step % 5 == 0 ? step / 5 : step % 2 == 0 ? step / 2 : step;

    /// <summary>The multiples of a step from <paramref name="from"/> to <paramref name="to"/>.</summary>
    private static IEnumerable<double> Ticks(double from, double to, double step)
    {
        for (var t = Math.Ceiling(from / step) * step; t <= to; t += step)
            yield return t;
    }

    /// <summary>A round step (1, 2 or 5 times a power of ten) that puts a tick about every 80 pixels of the picture.</summary>
    internal static double Step(double span, int pixels)
    {
        var raw = Math.Max(1, span * 80.0 / Math.Max(1, pixels));
        var power = Math.Pow(10, Math.Floor(Math.Log10(raw)));
        return new[] { 1, 2, 5, 10 }.Select(m => m * power).First(s => s >= raw);
    }
}
